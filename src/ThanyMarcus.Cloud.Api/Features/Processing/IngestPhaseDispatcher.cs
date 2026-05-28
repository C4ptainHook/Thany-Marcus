using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Processing.Phases;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed partial class IngestPhaseDispatcher
{
    private readonly Dictionary<string, IPhaseHandler> handlers;
    private readonly CancelHandler cancelHandler;
    private readonly HubGenerationHandler hubGenerationHandler;
    private readonly JobStateTransitions transitions;
    private readonly CloudDbContext db;
    private readonly IConfiguration config;
    private readonly IClock clock;
    private readonly IIngestEventBus eventBus;
    private readonly ProvenanceMaterializer provenance;
    private readonly ILogger<IngestPhaseDispatcher> log;

    public IngestPhaseDispatcher(
        IEnumerable<IPhaseHandler> handlers,
        CancelHandler cancelHandler,
        HubGenerationHandler hubGenerationHandler,
        JobStateTransitions transitions,
        CloudDbContext db,
        IConfiguration config,
        IClock clock,
        IIngestEventBus eventBus,
        ProvenanceMaterializer provenance,
        ILogger<IngestPhaseDispatcher> log)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = new Dictionary<string, IPhaseHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            if (!this.handlers.TryAdd(h.Phase, h))
            {
                throw new InvalidOperationException(
                    $"Duplicate IPhaseHandler registration for phase '{h.Phase}'");
            }
        }
        this.cancelHandler = cancelHandler;
        this.hubGenerationHandler = hubGenerationHandler;
        this.transitions = transitions;
        this.db = db;
        this.config = config;
        this.clock = clock;
        this.eventBus = eventBus;
        this.provenance = provenance;
        this.log = log;
    }

    public IReadOnlyCollection<string> KnownPhases => handlers.Keys;

    public async Task DispatchAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (IngestJobStatus.IsTerminal(job.Status))
        {
            LogTerminalClaim(log, job.Id, job.Status);
            return;
        }

        var deletedAt = await db.Notes
            .Where(n => n.Id == job.NoteId)
            .Select(n => n.DeletedAt)
            .SingleOrDefaultAsync(ct);
        if (deletedAt is not null)
        {
            try
            {
                await cancelHandler.HandleAsync(job, ct);
            }
            catch (SagaOwnershipLostException ex)
            {
                LogOwnershipLost(log, ex.JobId, ex.AttemptedWorkerId);
            }
            return;
        }

        if (IngestJobStatus.InFlightPhases.Contains(job.Status))
        {
            var maxCrashes = config.GetValue(
                $"IngestSaga:Phases:{job.Status}:MaxConsecutiveCrashes",
                DefaultMaxConsecutiveCrashes);
            if (job.ConsecutiveCrashes >= maxCrashes)
            {
                LogCrashLoopCap(log, job.Id, job.Status, job.ConsecutiveCrashes, maxCrashes);
                try
                {
                    await TransitionToFailureTerminalAsync(
                        job,
                        $"crash_loop_cap_exceeded: phase={job.Status} consecutive_crashes={job.ConsecutiveCrashes}",
                        ct);
                }
                catch (SagaOwnershipLostException ex)
                {
                    LogOwnershipLost(log, ex.JobId, ex.AttemptedWorkerId);
                }
                return;
            }
        }

        IPhaseHandler? handler;
        if (job.Status == IngestJobStatus.Composing && job.Kind == IngestJobKind.HubRegen)
        {
            handler = new HubGenerationHandlerAdapter(hubGenerationHandler);
        }
        else if (!handlers.TryGetValue(job.Status, out handler))
        {
            LogNoHandler(log, job.Id, job.Status);
            return;
        }

        var phaseBefore = job.Status;
        try
        {
            var result = await handler.HandleAsync(job, ct);
            if (result == PhaseHandlerResult.Advanced && job.Status != phaseBefore)
            {
                await eventBus.PublishNotePhaseChangedAsync(
                    job.NoteId, job.Id, phaseBefore, job.Status, ct);
            }
        }
        catch (SagaOwnershipLostException ex)
        {
            LogOwnershipLost(log, ex.JobId, ex.AttemptedWorkerId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogHandlerFailure(log, ex, job.Id, job.Status);
            try
            {
                await HandleFailureAsync(job, ex, ct);
            }
            catch (SagaOwnershipLostException lostEx)
            {
                LogOwnershipLost(log, lostEx.JobId, lostEx.AttemptedWorkerId);
            }
        }
    }

    private async Task HandleFailureAsync(IngestJob job, Exception ex, CancellationToken ct)
    {
        var phase = job.Status;
        var attempts = job.Attempts;
        var maxAttempts = config.GetValue($"IngestSaga:Phases:{phase}:MaxAttempts", DefaultMaxAttempts(phase));
        var backoffBase = config.GetValue($"IngestSaga:Phases:{phase}:BackoffSecondsBase", 2);

        if (attempts >= maxAttempts)
        {
            await TransitionToFailureTerminalAsync(job, ex.Message, ct);
        }
        else
        {
            var backoffSeconds = (int)Math.Pow(2, attempts) * backoffBase;
            var nextAt = clock.GetCurrentInstant().Plus(Duration.FromSeconds(backoffSeconds));
            await transitions.RescheduleAsync(job, nextAt, ex.Message, ct);
        }
    }

    private async Task TransitionToFailureTerminalAsync(IngestJob job, string lastError, CancellationToken ct)
    {
        var terminal = IngestJobStatus.FailureTerminalFor(job.Status);
        await transitions.TransitionAsync(
            job,
            nextStatus: terminal,
            lastError: lastError,
            clearLease: true,
            setFinishedAt: true,
            ct);

        var now = clock.GetCurrentInstant();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                status              = 'failed',
                transition_version  = transition_version + 1,
                updated_at          = {now}
              WHERE id = {job.NoteId}
            """, ct);
        await provenance.MaterializeAndPersistAsync(job, ct);
        await eventBus.PublishNoteFailedAsync(job.NoteId, lastError, ct);
    }

    internal static int DefaultMaxAttempts(string phase) => phase switch
    {
        IngestJobStatus.ExtractingAttachments => 1,
        IngestJobStatus.Composing             => 2,
        IngestJobStatus.Routing               => 3,
        IngestJobStatus.ExtractingEntities    => 3,
        IngestJobStatus.Synthesizing          => 2,
        IngestJobStatus.Embedding             => 3,
        _ => 1,
    };

    internal const int DefaultMaxConsecutiveCrashes = 3;

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Claim observed terminal status; dropping (job={JobId} status={Status})")]
    private static partial void LogTerminalClaim(ILogger logger, Guid jobId, string status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "No handler registered for status (job={JobId} status={Status})")]
    private static partial void LogNoHandler(ILogger logger, Guid jobId, string status);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "Ownership lost during dispatch (job={JobId} worker={WorkerId})")]
    private static partial void LogOwnershipLost(ILogger logger, Guid jobId, string? workerId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "Phase handler failed (job={JobId} phase={Phase})")]
    private static partial void LogHandlerFailure(ILogger logger, Exception ex, Guid jobId, string phase);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error,
        Message = "Crash-loop cap reached; terminating (job={JobId} phase={Phase} consecutive_crashes={Crashes} cap={Cap})")]
    private static partial void LogCrashLoopCap(ILogger logger, Guid jobId, string phase, short crashes, int cap);

    private sealed class HubGenerationHandlerAdapter : IPhaseHandler
    {
        private readonly HubGenerationHandler inner;
        public HubGenerationHandlerAdapter(HubGenerationHandler inner) { this.inner = inner; }
        public string Phase => IngestJobStatus.Composing;
        public Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct) =>
            inner.HandleAsync(job, ct);
    }
}
