using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class RoutingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Routing;

    private const int BodyExcerptMax = 1500;
    private const string InboxFolder = "Inbox";

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly LlmEventAppender events;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;
    private readonly JobStateTransitions transitions;

    public RoutingHandler(
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

        if (note.IsHub)
        {
            await transitions.TransitionAsync(
                job,
                nextStatus: IngestJobStatus.Embedding,
                lastError: null,
                clearLease: true,
                setFinishedAt: false,
                ct);
            return PhaseHandlerResult.Advanced;
        }

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var llm = llmFactory.Resolve(settings, out var fellBackToSafe);
        var o = opts.CurrentValue;
        var stubsFolder = (o.EntitySuggestions.StubsFolder ?? string.Empty).Trim().Trim('/');

        var folders = await LoadCandidateFoldersAsync(stubsFolder, o.RoutingFoldersMax, ct);

        if (folders.Count == 0)
        {
            await events.AppendAsync(job.Id, new LlmEvent(
                Stage: LlmEventStages.Route,
                PromptId: "route:v1",
                Model: "",
                ModelVersion: "",
                LlmMode: "skipped",
                LlmModeFallback: false,
                DurationMs: 0,
                RetryIndex: 0,
                Decision: "skipped:no_folders",
                Confidence: null,
                Rationale: null,
                Error: null), ct);
            note.RelativePath = $"{InboxFolder}/{note.Id}.md";
            await db.SaveChangesAsync(ct);
            await transitions.TransitionAsync(
                job,
                nextStatus: IngestJobStatus.Synthesizing,
                lastError: null,
                clearLease: true,
                setFinishedAt: false,
                ct);
            return PhaseHandlerResult.Advanced;
        }

        var attachments = await db.Attachments
            .Where(a => a.NoteId == note.Id)
            .ToListAsync(ct);
        var rawBody = RawExtractions.Concatenate(note, attachments);
        if (string.IsNullOrEmpty(rawBody)) rawBody = note.BodyInput ?? string.Empty;
        var bodyExcerpt = Truncate(rawBody, BodyExcerptMax);
        var prompt = PromptBuilder.BuildRoute(folders, bodyExcerpt);

        var pid = new PromptId("route", "v1");
        var start = clock.GetCurrentInstant();
        RouteDecisionDto decision;
        try
        {
            decision = await llm.CompleteAsync<RouteDecisionDto>(pid, new LlmPromptRequest(prompt), ct);
        }
        catch (LlmStructuredOutputException ex)
        {
            var elapsed = (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;
            await events.AppendAsync(job.Id, new LlmEvent(
                Stage: LlmEventStages.Route,
                PromptId: pid.ToString(),
                Model: llm.ModelName,
                ModelVersion: llm.ModelVersion,
                LlmMode: llm.Mode,
                LlmModeFallback: fellBackToSafe,
                DurationMs: elapsed,
                RetryIndex: ex.Attempts - 1,
                Decision: "failed",
                Confidence: null,
                Rationale: null,
                Error: ex.Message), ct);
            throw;
        }

        note.LlmMode = llm.Mode;
        string? chosenFolder = null;
        if (!string.IsNullOrWhiteSpace(decision.Folder) &&
            decision.Confidence >= o.Thresholds.RouteAcceptMin)
        {
            var match = folders.FirstOrDefault(f =>
                string.Equals(f, decision.Folder, StringComparison.Ordinal));
            if (match is not null) chosenFolder = match;
        }

        note.RelativePath = chosenFolder is null
            ? $"{InboxFolder}/{note.Id}.md"
            : $"{chosenFolder}/{note.Id}.md";

        var durationMs = (long)(clock.GetCurrentInstant() - start).TotalMilliseconds;
        await events.AppendAsync(job.Id, new LlmEvent(
            Stage: LlmEventStages.Route,
            PromptId: pid.ToString(),
            Model: llm.ModelName,
            ModelVersion: llm.ModelVersion,
            LlmMode: llm.Mode,
            LlmModeFallback: fellBackToSafe,
            DurationMs: durationMs,
            RetryIndex: 0,
            Decision: chosenFolder is null ? "unrouted" : $"folder:{chosenFolder}",
            Confidence: decision.Confidence,
            Rationale: decision.Rationale,
            Error: null), ct);

        await db.SaveChangesAsync(ct);

        await transitions.TransitionAsync(
            job,
            nextStatus: IngestJobStatus.Synthesizing,
            lastError: null,
            clearLease: true,
            setFinishedAt: false,
            ct);
        return PhaseHandlerResult.Advanced;
    }

    private async Task<List<string>> LoadCandidateFoldersAsync(
        string stubsFolder, int limit, CancellationToken ct)
    {
        // Top-level folder = first segment of relative_path; skip Inbox + stubs + dot/underscore folders.
        var rows = await db.Notes
            .Where(n => n.DeletedAt == null && n.RelativePath != null && n.RelativePath != "")
            .Select(n => n.RelativePath!)
            .Distinct()
            .ToListAsync(ct);

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in rows)
        {
            var slashIdx = path.IndexOf('/', StringComparison.Ordinal);
            if (slashIdx <= 0) continue;
            var folder = path[..slashIdx];
            if (folder.Length == 0) continue;
            if (folder.StartsWith('_') || folder.StartsWith('.')) continue;
            if (string.Equals(folder, InboxFolder, StringComparison.Ordinal)) continue;
            if (stubsFolder.Length > 0 &&
                string.Equals(folder, stubsFolder, StringComparison.Ordinal)) continue;
            set.Add(folder);
            if (set.Count >= limit) break;
        }
        return set.Take(limit).ToList();
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max];
}
