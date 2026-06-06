using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class RollingBackTfHandler(
    PortalDbContext db,
    IClock clock,
    ISagaCredentialSource credentials,
    IProvisioningProviderRegistry providers,
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
        var provider = providers.Resolve(cloud.Provider);

        var workdir = workspaceLayout.GetJobDir(job.Id);
        if (!Directory.Exists(workdir))
        {
            workdir = await workspaceLayout.RenderAsync(job, cloud, ct);
        }

        var dek = new byte[32];
        try
        {
            if (!await credentials.TryGetDekAsync(cloud, dek, ct))
            {
                LogStepUpMissing(log, job.Id);
                if (provider.RequiresCredentials)
                {
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject
                    {
                        ["event"] = "step_up_unlock_missing",
                    });
                    job.LastError = "step-up unlock missing; cannot decrypt provider credentials for destroy";
                    await HandleDestroyFailureAsync(job, cloud, ct);
                    return;
                }
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
                var destroyEnv = await BuildEnvAsync(provider, cloud, dek, ct);
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
                await CompleteUserDestroyAsync(provider, job, dek, ct);
            }
            else
            {
                var reason = ReadRollbackReason(job);
                var terminal = MapTerminal(reason);

                cloud.DestroyedAt = clock.GetCurrentInstant();
                cloud.VmIp = null;
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRollbackThrew(log, ex, cloud.Id);
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "rollback_threw",
                ["error"] = ex.Message,
            });
            job.LastError = $"rollback threw: {ex.Message}";
            await HandleDestroyFailureAsync(job, cloud, ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
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

    private async Task CompleteUserDestroyAsync(
        IProvisioningProvider provider, ProvisioningJob job, byte[] dek, CancellationToken ct)
    {
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        var now = clock.GetCurrentInstant();

        await provider.RevokeCredentialsAsync(cloud, dek, ct);

        cloud.DestroyedAt        = now;
        cloud.VmIp               = null;
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

    private static async Task<Dictionary<string, string>> BuildEnvAsync(
        IProvisioningProvider provider, Cloud cloud, byte[] dek, CancellationToken ct)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TF_VAR_cloud_id"] = cloud.Id.ToString(),
            ["TF_VAR_region"] = cloud.Region,
            ["TF_VAR_hostname"] = cloud.Hostname,
            ["TF_VAR_enrollment_token"] = string.Empty,
        };
        await provider.AddProvisioningEnvAsync(env, cloud, dek, ct);
        return env;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "RollingBackTfHandler: step-up unlock missing for job {JobId}; real-provider destroy cannot decrypt creds and will retry until unlock or abandon")]
    private static partial void LogStepUpMissing(ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "RollingBackTfHandler: terraform workspace delete failed for cloud {CloudId}; saga proceeds (housekeeping only)")]
    private static partial void LogWorkspaceDeleteFailed(ILogger logger, Exception ex, Guid cloudId);

    [LoggerMessage(EventId = 5, Level = LogLevel.Error,
        Message = "RollingBackTfHandler: rollback threw for cloud {CloudId}; routing to capped retry")]
    private static partial void LogRollbackThrew(ILogger logger, Exception ex, Guid cloudId);
}
