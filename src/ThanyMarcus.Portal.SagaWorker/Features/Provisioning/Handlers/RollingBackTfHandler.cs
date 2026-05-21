using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class RollingBackTfHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthConnections connections,
    IDigitalOceanOAuthClient doClient,
    ITerraformRunner tf,
    WorkspaceLayout workspaceLayout,
    IConfiguration config,
    ILogger<RollingBackTfHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.RollingBackTf;

    private const int MaxAttempts = 5;
    private static readonly Duration RetryDelay = Duration.FromMinutes(1);

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        var workdir = workspaceLayout.GetJobDir(job.Id);
        if (!Directory.Exists(workdir))
        {
            workdir = await workspaceLayout.RenderAsync(job, cloud, ct);
        }

        var dek = new byte[32];
        byte[]? providerToken = null;
        try
        {
            if (!await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
            {
                LogStepUpMissing(log, job.Id);
            }
            else if (cloud.Provider != DigitalOceanTfEnv.DigitalOceanProvider && cloud.Provider != "stub")
            {
                providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);
            }

            var connStr = config.GetConnectionString("Portal")
                ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");
            var pgUrl = TfPlanningHandler.ToPostgresUrl(connStr);
            var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conn_str"] = pgUrl,
            }, ct);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", initResult.Stdout);

            if (!initResult.Success)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject { ["error"] = "rollback_init_failed" });
                await HandleDestroyFailureAsync(job, cloud, ct);
                return;
            }

            var wsResult = await tf.SelectWorkspaceAsync(workdir, cloud.Id.ToString(), ct);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", wsResult.Stdout);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stderr", wsResult.Stderr);

            if (wsResult.Success)
            {
                var destroyEnv = await BuildEnvAsync(cloud, providerToken, dek, ct);
                var destroyResult = await tf.DestroyAsync(workdir, destroyEnv, ct);
                EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", destroyResult.Stdout);
                EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stderr", destroyResult.Stderr);

                if (!destroyResult.Success)
                {
                    await HandleDestroyFailureAsync(job, cloud, ct);
                    return;
                }
            }
            else
            {
                var workspaceMissing = LooksLikeMissingWorkspace(wsResult.Stderr);
                var liveResources = cloud.VmIp is not null;
                if (workspaceMissing && !liveResources)
                {
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject
                    {
                        ["event"] = "no_workspace_to_destroy",
                    });
                }
                else
                {
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject
                    {
                        ["event"] = "workspace_select_failed",
                        ["workspace_missing_fingerprint"] = workspaceMissing,
                        ["cloud_has_vm_ip"] = liveResources,
                    });
                    job.LastError = "terraform workspace select failed during rollback";
                    await HandleDestroyFailureAsync(job, cloud, ct);
                    return;
                }
            }

            if (job.Kind == SagaKinds.Destroy)
            {
                await CompleteUserDestroyAsync(job, dek, ct);
            }
            else
            {
                var reason = ReadRollbackReason(job);
                var terminal = MapTerminal(reason);

                cloud.DestroyedAt = clock.GetCurrentInstant();
                cloud.VmIp = null;
                cloud.EncryptedCloudAdminToken = null;
                cloud.TerraformWorkspace = null;

                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["event"] = "tf_rollback_complete",
                    ["terminal_status"] = terminal,
                    ["rollback_reason"] = reason ?? "(unknown)",
                });

                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, terminal, ct);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (providerToken is not null) CryptographicOperations.ZeroMemory(providerToken);
        }
    }

    private async Task HandleDestroyFailureAsync(ProvisioningJob job, Cloud cloud, CancellationToken ct)
    {
        if (job.AttemptCount >= MaxAttempts)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "rollback_abandoned",
                ["attempt_count"] = (int)job.AttemptCount,
                ["kind"] = job.Kind,
            });
            job.LastError = "rollback abandoned after retries";
            var terminal = job.Kind == SagaKinds.Destroy
                ? SagaStatus.FailedDestroy
                : SagaStatus.FailedTf;
            await SagaTransitions.TransitionToTerminalAsync(
                db, clock, job, cloud, terminal, ct);
            return;
        }

        EventsLogAppender.Append(job, clock, Phase, new JsonObject
        {
            ["event"] = "rollback_retry_scheduled",
            ["attempt_count"] = (int)job.AttemptCount,
        });
        await SagaTransitions.RescheduleAsync(db, clock, job, RetryDelay, ct);
    }

    private async Task CompleteUserDestroyAsync(ProvisioningJob job, byte[] dek, CancellationToken ct)
    {
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        var now = clock.GetCurrentInstant();

        if (cloud.Provider == DigitalOceanTfEnv.DigitalOceanProvider)
        {
            await BestEffortRevokeDoCredentialsAsync(cloud.UserId, cloud.Id, dek, ct);
        }

        cloud.DestroyedAt        = now;
        cloud.VmIp               = null;
        cloud.EncryptedCloudAdminToken = null;
        cloud.TerraformWorkspace = null;

        var revoked = await db.PluginTokenMetadata
            .Where(p => p.CloudId == cloud.Id && p.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), ct);

        EventsLogAppender.Append(job, clock, Phase, new JsonObject
        {
            ["event"] = "destroy_cleanup",
            ["revoked_token_count"] = revoked,
            ["cloud_id"] = cloud.Id.ToString(),
        });

        await SagaTransitions.TransitionToTerminalAsync(
            db, clock, job, cloud, SagaStatus.RolledBack, ct);

        var workdir = workspaceLayout.GetJobDir(job.Id);
        try
        {
            await tf.DeleteWorkspaceAsync(workdir, cloud.Id.ToString(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogWorkspaceDeleteFailed(log, ex, cloud.Id);
        }
    }

    private static string? ReadRollbackReason(ProvisioningJob job)
    {
        var root = job.EventsLog.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return null;
        string? latest = null;
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("rollback_reason", out var reasonEl) &&
                reasonEl.ValueKind == JsonValueKind.String)
            {
                latest = reasonEl.GetString();
            }
        }
        return latest;
    }

    private static bool LooksLikeMissingWorkspace(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        if (!stderr.Contains("workspace", StringComparison.OrdinalIgnoreCase)) return false;
        return stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase);
    }

    private static string MapTerminal(string? reason) => reason switch
    {
        "callback_timeout" => SagaStatus.FailedCallback,
        "dns_failed" => SagaStatus.FailedDns,
        "user_cancelled" => SagaStatus.RolledBack,
        _ => SagaStatus.FailedTf,
    };

    private async Task BestEffortRevokeDoCredentialsAsync(Guid userId, Guid cloudId, byte[] dek, CancellationToken ct)
    {
        string? accessToken = null;
        string? spacesId    = null;
        try
        {
            accessToken = await connections.GetAccessTokenAsync(userId, dek, ct);
            spacesId    = await secrets.TryGetAsync(cloudId, CloudSecretKind.DoSpacesAccessId, dek, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDoRevokeReadFailed(log, ex, cloudId);
            return;
        }

        if (accessToken is not null && spacesId is not null)
        {
            try { await doClient.DeleteSpacesKeyAsync(accessToken, spacesId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDoSpacesKeyDeleteFailed(log, ex, cloudId);
            }
        }
        // NOTE: user-level OAuth token is intentionally NOT revoked here.
        // Other clouds for the same user still need it; revoke happens only on explicit disconnect.
        await secrets.DeleteAllForCloudAsync(cloudId, ct);
    }

    private async Task<Dictionary<string, string>> BuildEnvAsync(
        Cloud cloud, byte[]? providerToken, byte[] dek, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TF_VAR_cloud_id"] = cloud.Id.ToString(),
            ["TF_VAR_region"] = cloud.Region,
            ["TF_VAR_hostname"] = cloud.Hostname,
            ["TF_VAR_enrollment_token"] = string.Empty,
        };
        if (cloud.Provider == DigitalOceanTfEnv.DigitalOceanProvider)
        {
            await DigitalOceanTfEnv.TryAddDoEnvVarsAsync(env, cloud.UserId, cloud.Id, dek, connections, secrets, ct);
        }
        else if (providerToken is not null)
        {
            env["TF_VAR_provider_token"] = Encoding.UTF8.GetString(providerToken);
        }
        return env;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "RollingBackTfHandler: step-up unlock missing for job {JobId}; destroy will proceed without provider creds (may fail for real providers)")]
    private static partial void LogStepUpMissing(ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "RollingBackTfHandler: terraform workspace delete failed for cloud {CloudId}; saga proceeds (housekeeping only)")]
    private static partial void LogWorkspaceDeleteFailed(ILogger logger, Exception ex, Guid cloudId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "RollingBackTfHandler: failed to read DO secrets for revoke (cloud {CloudId}); skipping best-effort revoke")]
    private static partial void LogDoRevokeReadFailed(ILogger logger, Exception ex, Guid cloudId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning,
        Message = "RollingBackTfHandler: DELETE /v2/spaces/keys failed for cloud {CloudId}; continuing")]
    private static partial void LogDoSpacesKeyDeleteFailed(ILogger logger, Exception ex, Guid cloudId);
}
