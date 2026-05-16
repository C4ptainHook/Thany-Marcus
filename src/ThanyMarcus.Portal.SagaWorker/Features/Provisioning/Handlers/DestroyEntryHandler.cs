using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed class DestroyEntryHandler(PortalDbContext db, IClock clock) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.Destroying;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        var next = cloud.Subdomain is not null
            ? SagaStatus.RollingBackDns
            : SagaStatus.RollingBackTf;

        EventsLogAppender.Append(job, clock, Phase, new JsonObject
        {
            ["event"] = "destroy_entry",
            ["next"] = next,
            ["rollback_reason"] = "user_destroy",
        });

        await SagaTransitions.TransitionAsync(db, clock, job, next, Duration.Zero, ct: ct);
    }
}
