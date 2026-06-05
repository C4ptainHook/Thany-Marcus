using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class RoutingHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.Routing;

    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly LlmEventAppender events;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly FolderRouter router;
    private readonly JobStateTransitions transitions;

    public RoutingHandler(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        LlmEventAppender events,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        FolderRouter router,
        JobStateTransitions transitions)
    {
        this.db = db;
        this.llmFactory = llmFactory;
        this.events = events;
        this.opts = opts;
        this.router = router;
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

        var folders = await router.LoadCandidatesAsync(o.RoutingFoldersMax, ct);

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
            note.RelativePath = $"{FolderRouter.InboxFolder}/{note.Id}.md";
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
        var bodyExcerpt = FolderRouter.BuildExcerpt(note, attachments);

        var outcome = await router.DecideAsync(
            bodyExcerpt, folders, llm, fellBackToSafe, o.Thresholds.RouteAcceptMin, ct);
        await events.AppendAsync(job.Id, outcome.Event, ct);
        if (outcome.Failure is not null) throw outcome.Failure;

        note.LlmMode = llm.Mode;
        note.RelativePath = outcome.ChosenFolder is null
            ? $"{FolderRouter.InboxFolder}/{note.Id}.md"
            : $"{outcome.ChosenFolder}/{note.Id}.md";
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
}
