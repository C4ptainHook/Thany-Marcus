using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public sealed partial class InboxRerouteService
{
    private readonly CloudDbContext db;
    private readonly ILlmClientFactory llmFactory;
    private readonly FolderRouter router;
    private readonly LlmEventAppender events;
    private readonly IOptionsMonitor<LlmIntelligenceOptions> opts;
    private readonly IClock clock;
    private readonly ILogger<InboxRerouteService> log;

    public InboxRerouteService(
        CloudDbContext db,
        ILlmClientFactory llmFactory,
        FolderRouter router,
        LlmEventAppender events,
        IOptionsMonitor<LlmIntelligenceOptions> opts,
        IClock clock,
        ILogger<InboxRerouteService> log)
    {
        this.db = db;
        this.llmFactory = llmFactory;
        this.router = router;
        this.events = events;
        this.opts = opts;
        this.clock = clock;
        this.log = log;
    }

    public async Task<FolderDissolveResponse> RunAsync(CancellationToken ct)
    {
        var o = opts.CurrentValue;
        var candidates = await router.LoadCandidatesAsync(o.RoutingFoldersMax, ct);
        if (candidates.Count == 0)
        {
            return new FolderDissolveResponse(
                FolderDissolveMode.Reroute, 0, Array.Empty<SyncDesiredItem>());
        }

        var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
        var llm = llmFactory.Resolve(settings, out var fellBackToSafe);

        var inboxPrefix = FolderRouter.InboxFolder + "/";
        var source = db.Notes
            .Where(n => n.DeletedAt == null
                        && n.Status == NoteStatus.Ready
                        && !n.IsHub
                        && n.RelativePath != null
                        && n.RelativePath.StartsWith(inboxPrefix))
            .OrderBy(n => n.CreatedAt);

        var total = await source.CountAsync(ct);
        var notes = await source.Take(o.RerouteMaxBatch).ToListAsync(ct);
        if (total > notes.Count)
        {
            LogBatchCapped(log, notes.Count, total - notes.Count, o.RerouteMaxBatch);
        }

        var desired = new List<SyncDesiredItem>();
        foreach (var note in notes)
        {
            var attachments = await db.Attachments
                .Where(a => a.NoteId == note.Id)
                .ToListAsync(ct);
            var bodyExcerpt = FolderRouter.BuildExcerpt(note, attachments);

            var outcome = await router.DecideAsync(
                bodyExcerpt, candidates, llm, fellBackToSafe, o.Thresholds.RouteAcceptMin, ct);

            if (outcome.Failure is not null)
            {
                LogNoteFailed(log, outcome.Failure, note.Id);
                continue;
            }
            if (outcome.ChosenFolder is null) continue;

            var now = clock.GetCurrentInstant();
            var newPath = $"{outcome.ChosenFolder}/{note.Id}.md";
            var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE notes SET
                    relative_path      = {newPath},
                    updated_at         = {now},
                    transition_version = transition_version + 1
                  WHERE id = {note.Id}
                    AND deleted_at IS NULL
                    AND status = 'ready'
                    AND relative_path LIKE 'Inbox/%'
                """, ct);
            if (rows == 0) continue;

            await AppendRouteEventAsync(note.Id, outcome.Event, ct);
            desired.Add(new SyncDesiredItem(note.Id, newPath));
        }

        return new FolderDissolveResponse(FolderDissolveMode.Reroute, desired.Count, desired);
    }

    private async Task AppendRouteEventAsync(Guid noteId, LlmEvent evt, CancellationToken ct)
    {
        var jobId = await db.IngestJobs
            .Where(j => j.NoteId == noteId)
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);
        if (jobId is { } id) await events.AppendAsync(id, evt, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "Inbox re-route batch capped at {Processed} notes; {Dropped} left for a later pass (cap={Cap})")]
    private static partial void LogBatchCapped(ILogger logger, int processed, int dropped, int cap);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "Inbox re-route skipped note {NoteId}: route decision failed")]
    private static partial void LogNoteFailed(ILogger logger, Exception ex, Guid noteId);
}
