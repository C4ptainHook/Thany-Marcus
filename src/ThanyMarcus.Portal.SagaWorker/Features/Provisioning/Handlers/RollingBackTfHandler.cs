using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class RollingBackTfHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
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
        var hasState = Directory.Exists(workdir);

        var dek = new byte[32];
        byte[]? providerToken = null;
        try
        {
            if (!await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
            {
                LogStepUpMissing(log, job.Id);
            }
            else
            {
                providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);
            }

            if (hasState)
            {
                var connStr = config.GetConnectionString("Portal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");
                var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["conn_str"] = connStr,
                }, ct);
                EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", initResult.Stdout);

                if (initResult.Success)
                {
                    var destroyEnv = BuildEnv(cloud, providerToken);
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
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject { ["error"] = "rollback_init_failed" });
                    await HandleDestroyFailureAsync(job, cloud, ct);
                    return;
                }
            }
            else
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["event"] = "no_workdir_to_destroy",
                });
            }

            if (job.Kind == SagaKinds.Destroy)
            {
                await CompleteUserDestroyAsync(job, ct);
            }
            else
            {
                var reason = ReadRollbackReason(job);
                var terminal = MapTerminal(reason);

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

    private async Task CompleteUserDestroyAsync(ProvisioningJob job, CancellationToken ct)
    {
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        var now = clock.GetCurrentInstant();

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

    private static string MapTerminal(string? reason) => reason switch
    {
        "callback_timeout" => SagaStatus.FailedCallback,
        "dns_failed" => SagaStatus.FailedDns,
        "user_cancelled" => SagaStatus.RolledBack,
        _ => SagaStatus.FailedTf,
    };

    private static Dictionary<string, string> BuildEnv(Cloud cloud, byte[]? providerToken)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TF_VAR_cloud_id"] = cloud.Id.ToString(),
            ["TF_VAR_region"] = cloud.Region,
            ["TF_VAR_hostname"] = cloud.Hostname,
            ["TF_VAR_enrollment_token"] = string.Empty,
        };
        if (providerToken is not null)
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
}
