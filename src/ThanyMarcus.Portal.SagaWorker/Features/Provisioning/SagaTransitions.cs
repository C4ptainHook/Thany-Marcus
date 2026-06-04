using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class SagaTransitions
{
    public const string CancelReason = "user_cancelled";

    private static readonly string[] PreApplyPhases =
        [SagaStatus.Pending, SagaStatus.MintingSpaces, SagaStatus.TfPlanning];

    /// <summary>
    /// Cooperative cancel: if a cancel was requested on the cloud, route the create saga into
    /// its own compensation chain instead of marching forward, then return true. Re-reads the
    /// cancel signal from the database so callers can use it both at handler entry and after a
    /// long Terraform step (the signal may be set by the cancel endpoint mid-step).
    /// </summary>
    public static async Task<bool> TryRouteCancelAsync(
        PortalDbContext db,
        IClock clock,
        ProvisioningJob job,
        Cloud cloud,
        string phase,
        CancellationToken ct = default)
    {
        var cancelAt = await db.Clouds
            .IgnoreQueryFilters()
            .Where(c => c.Id == cloud.Id)
            .Select(c => c.CancelRequestedAt)
            .FirstAsync(ct);
        if (cancelAt is null)
        {
            return false;
        }
        cloud.CancelRequestedAt = cancelAt;

        var preApply = Array.IndexOf(PreApplyPhases, phase) >= 0;
        var route = preApply
            ? SagaStatus.Cancelled
            : cloud.Subdomain is not null
                ? SagaStatus.RollingBackDns
                : SagaStatus.RollingBackTf;

        EventsLogAppender.Append(job, clock, phase, new JsonObject
        {
            ["event"] = "cancel_requested",
            ["route"] = route,
            ["rollback_reason"] = CancelReason,
        });

        if (preApply)
        {
            await TransitionToTerminalAsync(db, clock, job, cloud, SagaStatus.Cancelled, ct);
        }
        else
        {
            await TransitionAsync(db, clock, job, route, Duration.Zero, ct: ct);
        }
        return true;
    }

    public static async Task TransitionToTerminalAsync(
        PortalDbContext db,
        IClock clock,
        ProvisioningJob job,
        Cloud cloud,
        string terminalStatus,
        CancellationToken ct = default)
    {
        if (!SagaStatus.IsTerminal(terminalStatus))
        {
            throw new ArgumentException(
                $"'{terminalStatus}' is not a terminal saga status", nameof(terminalStatus));
        }
        await TransitionAsync(
            db, clock, job, terminalStatus, Duration.Zero,
            cloud: cloud,
            cloudMutation: c => c.ProvisioningStatus = terminalStatus,
            ct: ct);

        await db.SagaCredentialGrants
            .Where(g => g.CloudId == cloud.Id)
            .ExecuteDeleteAsync(ct);
    }

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
