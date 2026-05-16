using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed class AwaitingCloudCallbackHandler(
    PortalDbContext db,
    IClock clock) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.AwaitingCloudCallback;

    private static readonly Duration CallbackTimeout = Duration.FromMinutes(5);

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);

        if (job.Status != SagaStatus.AwaitingCloudCallback)
        {
            await SagaTransitions.RescheduleAsync(db, clock, job, Duration.Zero, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var phaseStarted = job.PhaseStartedAt ?? now;
        var age = now - phaseStarted;

        if (age >= CallbackTimeout)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "callback_timeout",
                ["age_seconds"] = (long)age.TotalSeconds,
                ["rollback_reason"] = "callback_timeout",
            });
            job.LastError = "cloud callback timed out";
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.RollingBackDns, Duration.Zero, ct: ct);
            return;
        }

        var remaining = CallbackTimeout - age;
        await SagaTransitions.RescheduleAsync(db, clock, job, remaining, ct);
    }
}
