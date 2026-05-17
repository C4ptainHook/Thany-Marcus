using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public sealed partial class SagaPhaseDispatcher
{
    private readonly Dictionary<string, ISagaPhaseHandler> handlers;
    private readonly CancelHandler cancelHandler;
    private readonly PortalDbContext db;
    private readonly IClock clock;
    private readonly ILogger<SagaPhaseDispatcher> log;

    public SagaPhaseDispatcher(
        IEnumerable<ISagaPhaseHandler> handlers,
        CancelHandler cancelHandler,
        PortalDbContext db,
        IClock clock,
        ILogger<SagaPhaseDispatcher> log)
    {
        ArgumentNullException.ThrowIfNull(handlers);
        this.handlers = new Dictionary<string, ISagaPhaseHandler>(StringComparer.Ordinal);
        foreach (var h in handlers)
        {
            if (!this.handlers.TryAdd(h.Phase, h))
            {
                throw new InvalidOperationException($"Duplicate ISagaPhaseHandler registration for phase '{h.Phase}'");
            }
        }
        this.cancelHandler = cancelHandler;
        this.db = db;
        this.clock = clock;
        this.log = log;
    }

    public IReadOnlyCollection<string> KnownPhases => handlers.Keys;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (SagaStatus.IsTerminal(job.Status))
        {
            LogTerminalClaim(log, job.Id, job.Status);
            return;
        }

        if (job.Kind == SagaKinds.Cancel)
        {
            await cancelHandler.HandleAsync(job, ct);
            return;
        }

        var cloud = await db.Clouds.IgnoreQueryFilters()
            .SingleOrDefaultAsync(c => c.Id == job.CloudId, ct);
        if (cloud is not null && SagaStatus.IsTerminal(cloud.ProvisioningStatus))
        {
            LogAbandonForTerminalCloud(log, job.Id, cloud.Id, cloud.ProvisioningStatus);
            var tracked = await db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
            tracked.Status    = cloud.ProvisioningStatus;
            tracked.UpdatedAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            return;
        }

        if (!handlers.TryGetValue(job.Status, out var handler))
        {
            LogNoHandler(log, job.Id, job.Status);
            return;
        }
        await handler.HandleAsync(job, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "Claim observed terminal status; dropping (job={JobId} status={Status})")]
    private static partial void LogTerminalClaim(ILogger logger, Guid jobId, string status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "No handler registered for status (job={JobId} status={Status})")]
    private static partial void LogNoHandler(ILogger logger, Guid jobId, string status);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information,
        Message = "Abandoning job {JobId} for terminal cloud {CloudId} (status={Status})")]
    private static partial void LogAbandonForTerminalCloud(ILogger logger, Guid jobId, Guid cloudId, string status);
}
