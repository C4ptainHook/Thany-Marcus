using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Entities;
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
    public const string SynthesisTemplateVersion = "synthesis-v1";
    private const int DefaultSeed = 42;
    private const double DefaultTemperature = 0.3;
    private const int DefaultMaxOutputTokens = 2048;

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

        var entityNames = await LoadCanonicalEntityNamesAsync(note.Id, ct);
        var rawHash = ComputeRawExtractionsHash(note, topLevel);
        var mentionsHash = ComputeMentionsHash(entityNames);
        var cacheKey = ComputeCacheKey(rawHash, mentionsHash, modelTag, promptVersion, privacyMode, preset);

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

        var prompt = SynthesisPromptBuilder.Build(
            systemBody: systemBody,
            entityCanonicalNames: entityNames,
            userBody: note.BodyInput,
            attachmentInputs: inputs);

        var apiKey = privacyMode == PrivacyModes.Public ? apiKeyStore.Take(note.Id) : null;
        var llm = router.Resolve(privacyMode, publicModel);
        var request = new SynthesisRequest(
            Prompt: prompt,
            Model: modelTag,
            ApiKey: apiKey,
            Seed: seed,
            Temperature: DefaultTemperature,
            MaxOutputTokens: DefaultMaxOutputTokens);

        string body;
        string status;
        string? error = null;
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await llm.CompleteAsync(request, ct);
            body = response.Body;
            status = "ok";
        }
        catch (SynthesisLlmException ex)
        {
            sw.Stop();
            body = $"*Synthesis failed — see Sources below.*";
            status = "failed";
            error = ex.Message;
            await events.AppendAsync(job.Id, BuildEvent(
                stage: LlmEventStages.Synthesis,
                modelTag: modelTag,
                privacyMode: privacyMode,
                durationMs: sw.ElapsedMilliseconds,
                decision: "failed",
                error: ex.Message), ct);
        }
        if (sw.IsRunning) sw.Stop();

        if (status == "ok")
        {
            await events.AppendAsync(job.Id, BuildEvent(
                stage: LlmEventStages.Synthesis,
                modelTag: modelTag,
                privacyMode: privacyMode,
                durationMs: sw.ElapsedMilliseconds,
                decision: "ok",
                error: null), ct);
        }

        var sources = SourcesRenderer.Render(note.BodyInput, topLevel);
        var fields = new SynthesisFrontmatterFields(
            PrivacyMode: privacyMode,
            Model: modelTag,
            PromptVersion: promptVersion,
            Seed: seed,
            SynthesizedAt: now,
            Status: status,
            Error: error);
        var frontmatter = FrontmatterBuilder.BuildSynthesis(note, topLevel, fields);
        var finalBody = $"---\n{frontmatter}---\n\n{body.TrimEnd()}\n\n{sources}";

        note.BodyOutput = finalBody;
        note.RelativePath = ComputeRelativePath(note.RelativePath, note.Id, body);
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

    private async Task<IReadOnlyList<string>> LoadCanonicalEntityNamesAsync(Guid noteId, CancellationToken ct)
    {
        var entityIds = await db.Mentions
            .Where(m => m.NoteId == noteId)
            .Select(m => m.EntityId)
            .Distinct()
            .ToListAsync(ct);
        if (entityIds.Count == 0)
        {
            // Fall back to top projects to give the model some safe link targets.
            return await db.Entities
                .Where(e => e.Kind == EntityKind.Project && e.DeletedAt == null)
                .OrderByDescending(e => e.UpdatedAt)
                .Take(20)
                .Select(e => e.CanonicalName)
                .ToListAsync(ct);
        }
        return await db.Entities
            .Where(e => entityIds.Contains(e.Id) && e.DeletedAt == null)
            .OrderBy(e => e.CanonicalName)
            .Select(e => e.CanonicalName)
            .ToListAsync(ct);
    }

    private static List<SynthesisInput> BuildInputs(List<Attachment> attachments)
    {
        var list = new List<SynthesisInput>(attachments.Count);
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
            if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
            {
                list.Add(new SynthesisInput(kind, Content: null, FailureReason: att.ExtractionError ?? "unknown"));
            }
            else if (att.ExtractionStatus == AttachmentExtractionStatus.Extracted
                     && !string.IsNullOrWhiteSpace(att.ExtractedText))
            {
                list.Add(new SynthesisInput(kind, Content: att.ExtractedText, FailureReason: null));
            }
        }
        return list;
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

    internal static string ComputeMentionsHash(IReadOnlyList<string> entityNames)
    {
        var sorted = entityNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
        return Sha256(string.Join("\n", sorted));
    }

    internal static string ComputeCacheKey(
        string rawHash, string mentionsHash, string modelTag, string promptVersion, string privacyMode, string preset)
    {
        var s = string.Format(
            CultureInfo.InvariantCulture,
            "{0}|{1}|{2}|{3}|{4}|{5}",
            rawHash, mentionsHash, modelTag, promptVersion, privacyMode, preset);
        return Sha256(s);
    }

    internal static string ComputeRelativePath(string? existing, Guid noteId, string body)
    {
        var slug = ExtractTitleSlug(body);
        if (string.IsNullOrEmpty(slug))
        {
            return existing ?? $"Inbox/{noteId}.md";
        }
        var idSuffix = noteId.ToString("N").Substring(0, 8);
        var filename = $"{slug}-{idSuffix}.md";
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
        new(stage, "synthesis-v1", modelTag, modelTag, privacyMode, false, durationMs, 0, decision, null, null, error);
}
