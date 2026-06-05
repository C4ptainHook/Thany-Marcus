using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Processing.Synthesis;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Synthesis;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed partial class SynthesizingHandler : IPhaseHandler
{
    public const string SynthesisTemplateVersion = "synthesis-v2";
    internal const string RenderVersion = "essence-render-v2";
    private const int DefaultSeed = 42;
    private const double DefaultTemperature = 0.3;
    private const int DefaultMaxOutputTokens = 2048;

    private const string FailedEssence =
        "# Synthesis unavailable\n\n"
        + "The automatic essence could not be generated this time. "
        + "Your original capture is preserved below under Origin.";

    [GeneratedRegex(@"^\s*#\s+(.+?)\s*$", RegexOptions.Multiline)]
    private static partial Regex H1Title();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex SlugSeparators();

    public string Phase => IngestJobStatus.Synthesizing;

    private readonly CloudDbContext db;
    private readonly ISynthesisLlmRouter router;
    private readonly ISynthesisApiKeyStore apiKeyStore;
    private readonly LlmEventAppender events;
    private readonly JobStateTransitions transitions;
    private readonly IClock clock;

    public SynthesizingHandler(
        CloudDbContext db,
        ISynthesisLlmRouter router,
        ISynthesisApiKeyStore apiKeyStore,
        LlmEventAppender events,
        JobStateTransitions transitions,
        IClock clock)
    {
        this.db = db;
        this.router = router;
        this.apiKeyStore = apiKeyStore;
        this.events = events;
        this.transitions = transitions;
        this.clock = clock;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        var attachments = await db.Attachments
            .Where(a => a.NoteId == note.Id)
            .ToListAsync(ct);

        var privacyMode = note.PrivacyMode ?? PrivacyModes.Private;
        var publicModel = privacyMode == PrivacyModes.Public ? note.PublicModel : null;
        var modelTag = router.ResolveModelTag(privacyMode, publicModel);

        var preset = note.SynthesisPreset ?? SynthesisPresets.Zettelkasten;
        var promptVersion = preset == SynthesisPresets.Custom
            ? CustomPromptVersion(note.SynthesisPromptBody)
            : SynthesisPresetBodies.VersionFor(preset);

        var topLevel = attachments.Where(a => a.ParentAttachmentId is null).OrderBy(a => a.CreatedAt).ToList();
        var inputs = BuildInputs(topLevel);

        var rawHash = ComputeRawExtractionsHash(note, topLevel);
        var cacheKey = ComputeCacheKey(rawHash, modelTag, promptVersion, privacyMode, preset);

        var now = clock.GetCurrentInstant();
        var seed = DefaultSeed;

        // Cache hit.
        if (string.Equals(note.SynthesisCacheKey, cacheKey, StringComparison.Ordinal)
            && !string.IsNullOrEmpty(note.SynthesisCacheValue))
        {
            note.BodyOutput = note.SynthesisCacheValue;
            note.RelativePath ??= $"Inbox/{note.Id}.md";
            note.UpdatedAt = now;
            job.LastComposeTemplate = SynthesisTemplateVersion;
            await db.SaveChangesAsync(ct);
            await events.AppendAsync(job.Id, BuildEvent(
                stage: LlmEventStages.SynthesisCacheHit,
                modelTag: modelTag,
                privacyMode: privacyMode,
                durationMs: 0,
                decision: "cache_hit",
                error: null), ct);
            await TransitionToEmbeddingAsync(job, ct);
            return PhaseHandlerResult.Advanced;
        }

        var systemBody = preset == SynthesisPresets.Custom
            ? SynthesisPresetBodies.AppendGuardrailsToCustom(note.SynthesisPromptBody ?? string.Empty)
            : SynthesisPresetBodies.BodyFor(preset);

        var budget = EssenceBudget.For(EssenceBudget.EstimateTokens(note.BodyInput, inputs));
        var form = FormRouter.Pick(note.BodyInput, inputs);
        var schema = EssenceSchemas.Build(form, budget);

        var prompt = SynthesisPromptBuilder.Build(
            systemBody: systemBody,
            userBody: note.BodyInput,
            attachmentInputs: inputs,
            form: form,
            budget: budget);

        var apiKey = privacyMode == PrivacyModes.Public ? apiKeyStore.Take(note.Id) : null;
        var llm = router.Resolve(privacyMode, publicModel);
        var request = new SynthesisRequest(
            Prompt: prompt,
            Model: modelTag,
            ApiKey: apiKey,
            Seed: seed,
            Temperature: DefaultTemperature,
            MaxOutputTokens: DefaultMaxOutputTokens,
            ResponseSchema: schema);

        string essence;
        string status;
        string? error = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await llm.CompleteAsync(request, ct);
            essence = EssenceRenderer.Render(form, response.Body, budget);
            status = "ok";
        }
        catch (Exception ex) when (ex is SynthesisLlmException or EssenceParseException)
        {
            essence = FailedEssence;
            status = "failed";
            error = ex.Message;
        }
        sw.Stop();

        await events.AppendAsync(job.Id, BuildEvent(
            stage: LlmEventStages.Synthesis,
            modelTag: modelTag,
            privacyMode: privacyMode,
            durationMs: sw.ElapsedMilliseconds,
            decision: status,
            error: error), ct);

        var sources = SourcesRenderer.Render(topLevel);
        var fields = new SynthesisFrontmatterFields(
            PrivacyMode: privacyMode,
            Model: modelTag,
            Preset: preset,
            PromptVersion: promptVersion,
            Seed: seed,
            SynthesizedAt: now,
            Status: status,
            Error: error);
        var processingDetails = ProcessingDetailsRenderer.Render(fields);
        var frontmatter = FrontmatterBuilder.BuildSynthesis(note, now);
        var finalBody = EssenceLayout.Assemble(frontmatter, essence, note.BodyInput, sources, processingDetails);

        note.BodyOutput = finalBody;
        if (status == "ok")
        {
            note.RelativePath = ComputeRelativePath(note.RelativePath, note.Id, essence);
        }
        else
        {
            note.RelativePath ??= $"Inbox/{note.Id}.md";
        }
        note.UpdatedAt = now;
        job.LastComposeTemplate = SynthesisTemplateVersion;

        if (status == "ok")
        {
            note.SynthesisCacheKey = cacheKey;
            note.SynthesisCacheValue = finalBody;
        }
        else
        {
            // Failure: don't persist a poisoned cache; reprocess should re-attempt.
            note.SynthesisCacheKey = null;
            note.SynthesisCacheValue = null;
        }

        await db.SaveChangesAsync(ct);

        await TransitionToEmbeddingAsync(job, ct);
        return PhaseHandlerResult.Advanced;
    }

    private async Task TransitionToEmbeddingAsync(IngestJob job, CancellationToken ct) =>
        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Embedding,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);

    private static List<SynthesisInput> BuildInputs(List<Attachment> attachments)
    {
        var list = new List<SynthesisInput>(attachments.Count);
        var n = 0;
        foreach (var att in attachments)
        {
            var kind = att.Kind switch
            {
                AttachmentKind.Image => "image",
                AttachmentKind.Voice => "voice",
                AttachmentKind.Url   => "url",
                AttachmentKind.File  => "file",
                _ => att.Kind,
            };
            var id = $"att-{++n}";

            if (att.Mode == AttachmentMode.Reference)
            {
                list.Add(new SynthesisInput(kind, Content: null, FailureReason: null,
                    Mode: AttachmentMode.Reference, Id: id));
            }
            else if (att.Mode == AttachmentMode.Metadata)
            {
                var (title, description, url) = ReadMetadata(att);
                list.Add(new SynthesisInput(kind, Content: null, FailureReason: null,
                    Mode: AttachmentMode.Metadata, Id: id,
                    Title: title, Description: description, Url: url));
            }
            else if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
            {
                list.Add(new SynthesisInput(kind, Content: null,
                    FailureReason: att.ExtractionError ?? "unknown", Id: id));
            }
            else if (att.ExtractionStatus == AttachmentExtractionStatus.Extracted
                     && !string.IsNullOrWhiteSpace(att.ExtractedText))
            {
                list.Add(new SynthesisInput(kind, Content: att.ExtractedText, FailureReason: null, Id: id));
            }
        }
        return list;
    }

    private static (string? Title, string? Description, string? Url) ReadMetadata(Attachment att)
    {
        if (att.Extra is null || att.Extra.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
            return (null, null, att.Url);
        var root = att.Extra.RootElement;
        return (
            GetString(root, "title"),
            GetString(root, "description"),
            GetString(root, "canonical_url") ?? att.Url);

        static string? GetString(System.Text.Json.JsonElement root, string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == System.Text.Json.JsonValueKind.String
                ? el.GetString()
                : null;
    }

    internal static string ComputeRawExtractionsHash(Note note, IReadOnlyList<Attachment> attachments)
    {
        var sb = new StringBuilder();
        sb.Append("body:").AppendLine(note.BodyInput ?? "");
        foreach (var att in attachments.OrderBy(a => a.CreatedAt))
        {
            sb.Append("att:").Append(att.Id).Append(':')
              .Append(att.Kind).Append(':')
              .Append(att.ExtractionStatus).Append(':')
              .Append(att.Sha256 ?? "")
              .AppendLine();
            if (!string.IsNullOrEmpty(att.ExtractedText)) sb.AppendLine(att.ExtractedText);
        }
        return Sha256(sb.ToString());
    }

    internal static string ComputeCacheKey(
        string rawHash, string modelTag, string promptVersion, string privacyMode, string preset)
    {
        var s = string.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}|{4}|{5}",
            rawHash, modelTag, promptVersion, privacyMode, preset, RenderVersion);
        return Sha256(s);
    }

    internal static string ComputeRelativePath(string? existing, Guid noteId, string body)
    {
        var slug = ExtractTitleSlug(body);
        if (string.IsNullOrEmpty(slug))
        {
            return existing ?? $"Inbox/{noteId}.md";
        }
        var filename = $"{slug}.md";
        if (string.IsNullOrEmpty(existing))
        {
            return $"Inbox/{filename}";
        }
        var lastSlash = existing.LastIndexOf('/');
        var parent = lastSlash < 0 ? "Inbox" : existing.Substring(0, lastSlash);
        return $"{parent}/{filename}";
    }

    internal static string ExtractTitleSlug(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return string.Empty;
        var match = H1Title().Match(body);
        if (!match.Success) return string.Empty;
        var title = match.Groups[1].Value.Trim();
        if (string.IsNullOrEmpty(title)) return string.Empty;
        var normalized = SlugSeparators().Replace(title, "-").Trim('-').ToLowerInvariant();
        if (normalized.Length > 60) normalized = normalized.Substring(0, 60).TrimEnd('-');
        return normalized;
    }

    private static string Sha256(string s)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        return Convert.ToHexString(bytes);
    }

    private static string CustomPromptVersion(string? body)
    {
        if (string.IsNullOrEmpty(body)) return "custom-empty";
        return "custom-" + Sha256(body)[..8].ToLowerInvariant();
    }

    private static LlmEvent BuildEvent(
        string stage, string modelTag, string privacyMode, long durationMs, string? decision, string? error) =>
        new(stage, SynthesisTemplateVersion, modelTag, modelTag, privacyMode, false, durationMs, 0, decision, null, null, error);
}
