using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public sealed partial class CrashRecoveryService(
    IServiceProvider services,
    WorkspaceLayout layout,
    IClock clock,
    ILogger<CrashRecoveryService> log) : IHostedService
{
    private static readonly Duration WorkdirRetention = Duration.FromDays(7);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var now = clock.GetCurrentInstant();

        var stale = await db.ProvisioningJobs
            .Where(j => (j.Status == SagaStatus.TfApplying || j.Status == SagaStatus.RollingBackTf)
                     && j.LeaseExpiresAt != null
                     && j.LeaseExpiresAt < now)
            .ToListAsync(cancellationToken);

        foreach (var job in stale)
        {
            LogStaleLease(log, job.Id, job.Status);
            job.ClaimedBy = null;
            job.LeaseExpiresAt = null;
            job.NextVisibleAt = now;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        try
        {
            layout.SweepTerminal(now, WorkdirRetention);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSweepError(log, ex);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "CrashRecovery: stale lease on job {JobId} (status={Status}); resetting for re-claim")]
    private static partial void LogStaleLease(ILogger logger, Guid jobId, string status);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "CrashRecovery: workspace sweep failed; will retry next boot")]
    private static partial void LogSweepError(ILogger logger, Exception ex);
}
