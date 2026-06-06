using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Migrate;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class BlueGreenHandler(
    string phase,
    PortalDbContext db,
    IClock clock,
    ISagaCredentialSource credentials,
    IDigitalOceanOAuthConnections connections,
    IEnumerable<IBlueGreenOperations> operations,
    ILogger<BlueGreenHandler> log) : ISagaPhaseHandler
{
    private static readonly Duration VerifyDeadline = Duration.FromMinutes(15);
    private static readonly Duration VerifyCadence = Duration.FromSeconds(10);

    public string Phase => phase;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        var ops = operations.First(o => o.Provider == cloud.Provider);

        switch (phase)
        {
            case SagaStatus.MigrateQuiescing:     await QuiesceAsync(job, cloud, ops, ct); break;
            case SagaStatus.MigrateSnapshotting:  await SnapshotAsync(job, cloud, ops, ct); break;
            case SagaStatus.MigrateProvisioning:  await ProvisionAsync(job, cloud, ops, ct); break;
            case SagaStatus.MigrateVerifying:     await VerifyAsync(job, cloud, ops, ct); break;
            case SagaStatus.MigrateCutover:       await CutoverAsync(job, cloud, ops, ct); break;
            case SagaStatus.MigratePostGate:      await PostGateAsync(job, cloud, ops, ct); break;
            case SagaStatus.MigrateDestroyingOld: await DestroyOldAsync(job, cloud, ops, ct); break;
        }
    }

    private async Task QuiesceAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var token = await TryGetTokenAsync(cloud, ct);
        if (token is null) { await FailAsync(job, cloud, "do_token_unavailable", ct); return; }

        if (!await ops.HasCapacityAsync(token, ct))
        {
            Append(job, new JsonObject { ["error"] = "insufficient_capacity" });
            await FailAsync(job, cloud, "insufficient_capacity", ct);
            return;
        }

        var oldDropletId = await ops.FindActiveDropletIdAsync(token, cloud, ct);
        Append(job, new JsonObject { ["event"] = "quiesced", ["old_droplet_id"] = oldDropletId });
        await NextAsync(job, cloud, SagaStatus.MigrateSnapshotting, ct);
    }

    private async Task SnapshotAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var token = await TryGetTokenAsync(cloud, ct);
        if (token is null) { await FailAsync(job, cloud, "do_token_unavailable", ct); return; }

        var snapshotId = await ops.SnapshotDataVolumeAsync(token, cloud, ct);
        Append(job, new JsonObject { ["event"] = "snapshot_created", ["snapshot_id"] = snapshotId });
        await NextAsync(job, cloud, SagaStatus.MigrateProvisioning, ct);
    }

    private async Task ProvisionAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var token = await TryGetTokenAsync(cloud, ct);
        if (token is null) { await FailAsync(job, cloud, "do_token_unavailable", ct); return; }

        var snapshotId = ReadEventValue(job, "snapshot_id") ?? "";
        var targetVersion = ReadTargetVersion(job);
        try
        {
            var green = await ops.ProvisionGreenAsync(token, cloud, snapshotId, targetVersion, ct);
            Append(job, new JsonObject
            {
                ["event"] = "green_provisioned",
                ["green_droplet_id"] = green.DropletId,
                ["green_ip"] = green.Ipv4,
            });
            await NextAsync(job, cloud, SagaStatus.MigrateVerifying, ct);
        }
        catch (Exception ex) when (ex is NotSupportedException or HttpRequestException)
        {
            LogProvisionFailed(log, ex, job.Id);
            Append(job, new JsonObject { ["error"] = "green_provision_failed" });
            await FailAsync(job, cloud, "green_provision_failed", ct);
        }
    }

    private async Task VerifyAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var greenIp = ReadEventValue(job, "green_ip") ?? "";
        if (await ops.HealthyAsync(greenIp, ct))
        {
            Append(job, new JsonObject { ["event"] = "green_verified" });
            await NextAsync(job, cloud, SagaStatus.MigrateCutover, ct);
            return;
        }

        var now = clock.GetCurrentInstant();
        var age = now - (job.PhaseStartedAt ?? now);
        if (age >= VerifyDeadline)
        {
            var token = await TryGetTokenAsync(cloud, ct);
            var greenDropletId = ReadEventValue(job, "green_droplet_id");
            if (token is not null && greenDropletId is not null)
                await ops.DestroyDropletAsync(token, greenDropletId, ct);
            await FailAsync(job, cloud, "green_health_timeout", ct);
            return;
        }

        await SagaTransitions.RescheduleAsync(db, clock, job, VerifyCadence, ct);
    }

    private async Task CutoverAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var token = await TryGetTokenAsync(cloud, ct);
        if (token is null) { await FailAsync(job, cloud, "do_token_unavailable", ct); return; }

        var greenDropletId = ReadEventValue(job, "green_droplet_id")
            ?? throw new InvalidOperationException("missing green_droplet_id at cutover");
        await ops.ReassignReservedIpAsync(token, cloud, greenDropletId, ct);
        Append(job, new JsonObject { ["event"] = "cutover_to_green", ["green_droplet_id"] = greenDropletId });
        await NextAsync(job, cloud, SagaStatus.MigratePostGate, ct);
    }

    private async Task PostGateAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var greenIp = ReadEventValue(job, "green_ip") ?? "";
        if (await ops.HealthyAsync(greenIp, ct))
        {
            Append(job, new JsonObject { ["event"] = "post_cutover_ok" });
            await NextAsync(job, cloud, SagaStatus.MigrateDestroyingOld, ct);
            return;
        }

        var token = await TryGetTokenAsync(cloud, ct);
        var oldDropletId = ReadEventValue(job, "old_droplet_id");
        var greenDropletId = ReadEventValue(job, "green_droplet_id");
        if (token is not null && oldDropletId is not null)
            await ops.ReassignReservedIpAsync(token, cloud, oldDropletId, ct);
        if (token is not null && greenDropletId is not null)
            await ops.DestroyDropletAsync(token, greenDropletId, ct);

        Append(job, new JsonObject { ["event"] = "post_cutover_failed_rolled_back" });
        await FinalizeAsync(job, cloud, SagaStatus.MigrateRolledBack, ct);
    }

    private async Task DestroyOldAsync(ProvisioningJob job, Cloud cloud, IBlueGreenOperations ops, CancellationToken ct)
    {
        var token = await TryGetTokenAsync(cloud, ct);
        if (token is null) { await FailAsync(job, cloud, "do_token_unavailable", ct); return; }

        var oldDropletId = ReadEventValue(job, "old_droplet_id");
        if (oldDropletId is not null)
            await ops.DestroyDropletAsync(token, oldDropletId, ct);

        var retainUntil = clock.GetCurrentInstant() + Duration.FromDays(7);
        Append(job, new JsonObject
        {
            ["event"] = "old_destroyed",
            ["snapshot_retain_until"] = retainUntil.ToString(),
        });
        await FinalizeAsync(job, cloud, SagaStatus.MigrateSucceeded, ct);
    }

    private Task NextAsync(ProvisioningJob job, Cloud cloud, string next, CancellationToken ct) =>
        SagaTransitions.TransitionAsync(
            db, clock, job, next, Duration.Zero,
            cloud: cloud, cloudMutation: c => c.ProvisioningStatus = next, ct: ct);

    private async Task FailAsync(ProvisioningJob job, Cloud cloud, string reason, CancellationToken ct)
    {
        job.LastError = reason;
        await FinalizeAsync(job, cloud, SagaStatus.FailedMigrate, ct);
    }

    private async Task FinalizeAsync(ProvisioningJob job, Cloud cloud, string terminalJobStatus, CancellationToken ct)
    {
        await SagaTransitions.TransitionAsync(
            db, clock, job, terminalJobStatus, Duration.Zero,
            cloud: cloud, cloudMutation: c => c.ProvisioningStatus = SagaStatus.Succeeded, ct: ct);
        await db.SagaCredentialGrants.Where(g => g.CloudId == cloud.Id).ExecuteDeleteAsync(ct);
    }

    private void Append(ProvisioningJob job, JsonObject body) =>
        EventsLogAppender.Append(job, clock, phase, body);

    private async Task<string?> TryGetTokenAsync(Cloud cloud, CancellationToken ct)
    {
        var dek = new byte[32];
        try
        {
            if (!await credentials.TryGetDekAsync(cloud, dek, ct)) return null;
            return await connections.GetAccessTokenAsync(cloud.UserId, dek, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static string? ReadEventValue(ProvisioningJob job, string key)
    {
        var node = JsonNode.Parse(job.EventsLog.RootElement.GetRawText());
        if (node is not JsonArray arr) return null;
        for (var i = arr.Count - 1; i >= 0; i--)
        {
            if (arr[i] is JsonObject obj && obj.TryGetPropertyValue(key, out var v) && v is not null)
                return v.GetValue<string>();
        }
        return null;
    }

    private static string ReadTargetVersion(ProvisioningJob job) =>
        job.Payload.RootElement.TryGetProperty("target_version", out var v) ? v.GetString() ?? "" : "";

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "BlueGreenHandler: green provision failed for job {JobId}")]
    private static partial void LogProvisionFailed(ILogger logger, Exception ex, Guid jobId);
}
