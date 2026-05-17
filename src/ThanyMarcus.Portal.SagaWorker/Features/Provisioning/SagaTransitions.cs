using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class SagaTransitions
{
    public static async Task TransitionAsync(
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
        var owningWorker = job.ClaimedBy;
        job.Status = newStatus;
        job.NextVisibleAt = now + nextVisibleDelay;
        job.PhaseStartedAt = now;
        job.ClaimedBy = null;
        job.LeaseExpiresAt = null;
        job.TransitionVersion += 1;
        if (tfOutputs is not null)
        {
            job.TfOutputs?.Dispose();
            job.TfOutputs = tfOutputs;
        }
        if (cloud is not null && cloudMutation is not null)
        {
            cloudMutation(cloud);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SagaOwnershipLostException(job.Id, owningWorker, ex);
        }

        await db.Database.ExecuteSqlRawAsync(
            "SELECT pg_notify('provisioning_job_changed', {0})",
            [job.Id.ToString()], ct);
    }

    public static async Task RescheduleAsync(
        PortalDbContext db,
        IClock clock,
        ProvisioningJob job,
        Duration nextVisibleDelay,
        CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var owningWorker = job.ClaimedBy;
        job.NextVisibleAt = now + nextVisibleDelay;
        job.ClaimedBy = null;
        job.LeaseExpiresAt = null;
        job.TransitionVersion += 1;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new SagaOwnershipLostException(job.Id, owningWorker, ex);
        }
    }
}
