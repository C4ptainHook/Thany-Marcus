using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Composing;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class HubGenerationHandler
{
    // Same phase name as ComposingHandler — IngestPhaseDispatcher branches on job.Kind.
    public string Phase => IngestJobStatus.Composing;

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly LlmEventAppender events;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;
    private readonly JobStateTransitions transitions;

    public HubGenerationHandler(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        LlmEventAppender events,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        IClock clock,
        JobStateTransitions transitions)
    {
        this.db = db;
        this.llmFactory = llmFactory;
        this.events = events;
        this.opts = opts;
        this.clock = clock;
        this.transitions = transitions;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
        if (!note.IsHub) throw new InvalidOperationException("hub-regen job for non-hub note");
        if (note.HubEntityId is null) throw new InvalidOperationException("hub note missing hub_entity_id");

        var entity = await db.Entities.SingleAsync(e => e.Id == note.HubEntityId.Value, ct);
        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var llm = llmFactory.Resolve(settings, out var fellBackToSafe);
        var o = opts.CurrentValue;

        // Refresh the mutable label off accumulated mentions; identity (CanonicalName / file path)
        // is untouched, so this only changes how the hub reads.
        await DisplayNameRecomputer.RecomputeAsync(db, entity, o.DisplayNameHysteresisMargin, ct);

        var recent = await (
            from m in db.Mentions
            join n in db.Notes on m.NoteId equals n.Id
            where m.EntityId == entity.Id
                  && n.DeletedAt == null
                  && !n.IsHub
            orderby n.CapturedAt descending
            select new { m, n })
            .Take(o.HubMentionWindow)
            .ToListAsync(ct);

        var contexts = new List<HubMentionContext>();
        foreach (var x in recent)
        {
            if (string.IsNullOrEmpty(x.n.BodyOutput)) continue;
            var surrounding = ExtractingEntitiesHandler.ExtractSurrounding(
                x.n.BodyOutput, x.m.StartOffset, x.m.EndOffset, o.SurroundingTextChars);
            contexts.Add(new HubMentionContext(
                NoteTitle: x.n.RelativePath ?? x.n.Id.ToString(),
                NoteCapturedAt: x.n.CapturedAt.ToString("uuuu-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture),
                SurroundingText: surrounding));
        }

        var previousBody = ExtractPreviousMarkdown(note.BodyOutput);
        var entityCtx = new HubEntityContext(entity.Kind, entity.DisplayName ?? entity.CanonicalName, entity.Aliases);
        var prompt = PromptBuilder.BuildHubGenerate(entityCtx, contexts, previousBody);

        var pid = new PromptId("hub-generate", "v1");
        var start = clock.GetCurrentInstant();
        string markdown;
        try
        {
            markdown = await llm.CompleteAsync<string>(pid, new LlmPromptRequest(prompt), ct);
        }
        catch (LlmStructuredOutputException ex)
        {
            await events.AppendAsync(job.Id, new LlmEvent(
                LlmEventStages.HubGenerate, pid.ToString(), llm.ModelName, llm.ModelVersion,
                llm.Mode, fellBackToSafe, ElapsedMs(start), ex.Attempts - 1,
                "failed", null, null, ex.Message), ct);
            throw;
        }
        await events.AppendAsync(job.Id, new LlmEvent(
            LlmEventStages.HubGenerate, pid.ToString(), llm.ModelName, llm.ModelVersion,
            llm.Mode, fellBackToSafe, ElapsedMs(start), 0,
            Decision: previousBody is null ? "initial" : "diff_aware",
            Confidence: null, Rationale: null, Error: null), ct);

        note.LlmMode = llm.Mode;
        note.BodyOutput = ComposeHubBody(note, markdown);
        note.UpdatedAt = clock.GetCurrentInstant();
        job.LastComposeTemplate = CompositeNoteComposer.ComposeTemplateVersion;

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Routing,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }

    private static string ComposeHubBody(Note hubNote, string generatedMarkdown)
    {
        var frontmatter = FrontmatterBuilder.Build(hubNote, Array.Empty<Attachment>(),
            CompositeNoteComposer.ComposeTemplateVersion);
        var userNotes = UserNotesPreserver.Extract(hubNote.BodyOutput);
        var body = $"## User Notes\n\n{userNotes}\n\n## System Output\n\n{generatedMarkdown.TrimEnd()}\n";
        return $"---\n{frontmatter}---\n\n{body}";
    }

    private long ElapsedMs(Instant start) =>
        (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;

    private static string? ExtractPreviousMarkdown(string? bodyOutput)
    {
        if (string.IsNullOrEmpty(bodyOutput)) return null;
        var idx = bodyOutput.IndexOf("## System Output", StringComparison.Ordinal);
        if (idx < 0) return null;
        var after = bodyOutput[idx..];
        // Skip past the heading line itself.
        var newline = after.IndexOf('\n', StringComparison.Ordinal);
        if (newline < 0) return null;
        var content = after[(newline + 1)..].TrimStart('\n').TrimEnd();
        return content.Length == 0 ? null : content;
    }
}
