using System.Text.Json;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

internal static class SagaTransitions
{
    public static Task TransitionAsync(
        PortalDbContext db,
        IClock clock,
        ProvisioningJob job,
        string newStatus,
        Duration nextVisibleDelay,
        Cloud? cloud = null,
        Action<Cloud>? cloudMutation = null,
        JsonDocument? tfOutputs = null,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        job.Status = newStatus;
        job.NextVisibleAt = now + nextVisibleDelay;
        job.PhaseStartedAt = now;
        job.ClaimedBy = null;
        job.LeaseExpiresAt = null;
        if (tfOutputs is not null)
        {
            job.TfOutputs?.Dispose();
            job.TfOutputs = tfOutputs;
        }
        if (cloud is not null && cloudMutation is not null)
        {
            cloudMutation(cloud);
        }
        return db.SaveChangesAsync(ct);
    }

    public static Task RescheduleAsync(
        PortalDbContext db,
        IClock clock,
        ProvisioningJob job,
        Duration nextVisibleDelay,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        job.NextVisibleAt = now + nextVisibleDelay;
        job.ClaimedBy = null;
        job.LeaseExpiresAt = null;
        return db.SaveChangesAsync(ct);
    }
}
