using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class CancelHandler(
    PortalDbContext db,
    IClock clock,
    ISagaCredentialSource credentials,
    IProvisioningProviderRegistry providers,
    ITerraformRunner tf,
    WorkspaceLayout workspaceLayout,
    IConfiguration config,
    ILogger<CancelHandler> log)
{
    private const string Phase = "cancelling";
    private const int MaxDestroyAttempts = 5;
    private static readonly Duration DeferDelay = Duration.FromSeconds(5);
    private static readonly Duration RetryDelay = Duration.FromMinutes(1);

    public async Task HandleAsync(ProvisioningJob cancelJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancelJob);
        var cancelJobId = cancelJob.Id;
        cancelJob = await db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJobId, ct);

        var cloud = await db.Clouds
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == cancelJob.CloudId, ct);

        if (cloud.CancelRequestedAt is null)
        {
            cloud.CancelRequestedAt = clock.GetCurrentInstant();
        }

        var recentCreate = await db.ProvisioningJobs
            .Where(j => j.CloudId == cancelJob.CloudId && j.Kind == SagaKinds.Create)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (recentCreate is not null && !SagaStatus.IsTerminal(recentCreate.Status))
        {
            EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
            {
                ["event"] = "deferring_to_create_saga",
                ["create_job"] = recentCreate.Id.ToString(),
                ["create_status"] = recentCreate.Status,
            });
            await SagaTransitions.RescheduleAsync(db, clock, cancelJob, DeferDelay, ct);
            return;
        }

        if (recentCreate is not null &&
            recentCreate.Status is SagaStatus.RolledBack or SagaStatus.Cancelled)
        {
            EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
            {
                ["event"] = "create_saga_already_compensated",
                ["create_status"] = recentCreate.Status,
            });
            await FinalizeCancelledAsync(cancelJob, cloud, hadResources: false, ct);
            return;
        }

        await DestroyAndFinalizeAsync(cancelJob, cloud, recentCreate, ct);
    }

    private async Task DestroyAndFinalizeAsync(
        ProvisioningJob cancelJob, Cloud cloud, ProvisioningJob? recentCreate, CancellationToken ct)
    {
        string? failReason;
        var hadResources = false;
        try
        {
            (failReason, hadResources) = await RunDestroyAsync(cancelJob, cloud, recentCreate, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogDestroyFailed(log, ex, cloud.Id);
            failReason = $"destroy_threw: {ex.Message}";
        }

        if (failReason is not null)
        {
            await DestroyFailedAsync(cancelJob, cloud, failReason, ct);
            return;
        }

        await FinalizeCancelledAsync(cancelJob, cloud, hadResources, ct);
    }

    /// <summary>
    /// Tears down whatever the failed/abandoned create saga left behind. Always init + select the
    /// cloud's workspace before reading state, so a missing or stale <c>.terraform/environment</c>
    /// can't make a leftover droplet look absent. Returns (failReason, hadResources): a non-null
    /// failReason means destroy did not complete and should be retried.
    /// </summary>
    private async Task<(string?, bool)> RunDestroyAsync(
        ProvisioningJob cancelJob, Cloud cloud, ProvisioningJob? recentCreate, CancellationToken ct)
    {
        var workdir = ResolveWorkdir(cancelJob, recentCreate);
        if (!Directory.Exists(workdir))
        {
            workdir = await workspaceLayout.RenderAsync(cancelJob, cloud, ct);
        }

        var connStr = config.GetConnectionString("Portal")
            ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");
        var pgUrl = TfPlanningHandler.ToPostgresUrl(connStr);

        var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["conn_str"] = pgUrl,
        }, ct);
        EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stdout", initResult.Stdout);
        EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stderr", initResult.Stderr);
        if (!initResult.Success)
        {
            return ("tf_init_failed", false);
        }

        var wsResult = await tf.SelectWorkspaceAsync(workdir, cloud.Id.ToString(), ct);
        EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stdout", wsResult.Stdout);
        EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stderr", wsResult.Stderr);
        if (!wsResult.Success)
        {
            if (LooksLikeMissingWorkspace(wsResult.Stderr) && cloud.VmIp is null)
            {
                EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
                {
                    ["event"] = "no_workspace_to_destroy",
                });
                return (null, false);
            }
            return ("tf_workspace_select_failed", false);
        }

        if (!await tf.HasResourcesAsync(workdir, ct))
        {
            EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
            {
                ["event"] = "no_resources_to_destroy",
            });
            return (null, false);
        }

        var provider = providers.Resolve(cloud.Provider);
        var dek = new byte[32];
        try
        {
            if (!await credentials.TryGetDekAsync(cloud, dek, ct))
            {
                LogStepUpMissing(log, cancelJob.Id);
                return ("step_up_unlock_missing", true);
            }

            var destroyEnv = await BuildEnvAsync(provider, cloud, dek, ct);
            var destroyResult = await tf.DestroyAsync(workdir, destroyEnv, ct);
            EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stdout", destroyResult.Stdout);
            EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stderr", destroyResult.Stderr);

            if (!destroyResult.Success)
            {
                return ($"tf_destroy_exit_{destroyResult.ExitCode}", true);
            }

            LogDestroyOk(log, cloud.Id);
            EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
            {
                ["event"] = "tf_destroy_ok",
            });
            return (null, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private string ResolveWorkdir(ProvisioningJob cancelJob, ProvisioningJob? recentCreate)
    {
        if (recentCreate is not null)
        {
            var createDir = workspaceLayout.GetJobDir(recentCreate.Id);
            if (Directory.Exists(createDir)) return createDir;
        }
        return workspaceLayout.GetJobDir(cancelJob.Id);
    }

    private async Task FinalizeCancelledAsync(
        ProvisioningJob cancelJob, Cloud cloud, bool hadResources, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        var now = clock.GetCurrentInstant();

        cloud.ProvisioningStatus      = SagaStatus.Cancelled;
        cloud.DestroyedAt             = now;
        cloud.ProvisioningCompletedAt = now;
        cloud.UpdatedAt               = now;

        EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
        {
            ["event"] = "cancel_complete",
            ["had_resources"] = hadResources,
        });

        cancelJob.Status         = SagaStatus.Succeeded;
        cancelJob.ClaimedBy       = null;
        cancelJob.LeaseExpiresAt  = null;
        cancelJob.UpdatedAt       = now;

        await db.SagaCredentialGrants
            .Where(g => g.CloudId == cloud.Id)
            .ExecuteDeleteAsync(ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task DestroyFailedAsync(
        ProvisioningJob cancelJob, Cloud cloud, string reason, CancellationToken ct)
    {
        var attempt = CountDestroyFailures(cancelJob) + 1;
        EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
        {
            ["event"] = "cancel_destroy_failed",
            ["reason"] = reason,
            ["attempt"] = attempt,
        });
        cloud.ProvisioningError = $"cancel: terraform destroy failed: {reason}";

        if (attempt >= MaxDestroyAttempts)
        {
            LogDestroyAbandoned(log, cloud.Id, attempt);
            EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
            {
                ["event"] = "cancel_destroy_abandoned",
                ["attempts"] = attempt,
            });
            await SagaTransitions.TransitionToTerminalAsync(
                db, clock, cancelJob, cloud, SagaStatus.FailedDestroy, ct);
            return;
        }

        await SagaTransitions.RescheduleAsync(db, clock, cancelJob, RetryDelay, ct);
    }

    private static int CountDestroyFailures(ProvisioningJob job)
    {
        var root = job.EventsLog.RootElement;
        if (root.ValueKind != JsonValueKind.Array) return 0;
        var count = 0;
        foreach (var entry in root.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object &&
                entry.TryGetProperty("event", out var ev) &&
                ev.ValueKind == JsonValueKind.String &&
                ev.GetString() == "cancel_destroy_failed")
            {
                count++;
            }
        }
        return count;
    }

    private static bool LooksLikeMissingWorkspace(string stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        if (!stderr.Contains("workspace", StringComparison.OrdinalIgnoreCase)) return false;
        return stderr.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("doesn't exist", StringComparison.OrdinalIgnoreCase);
    }

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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "CancelHandler: terraform destroy completed for cancelled cloud {CloudId}")]
    private static partial void LogDestroyOk(ILogger logger, Guid cloudId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "CancelHandler: terraform destroy threw for cancelled cloud {CloudId}; will retry")]
    private static partial void LogDestroyFailed(ILogger logger, Exception ex, Guid cloudId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "CancelHandler: step-up unlock missing for cancel job {JobId}; destroy needs provider creds, will retry")]
    private static partial void LogStepUpMissing(ILogger logger, Guid jobId);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error,
        Message = "CancelHandler: destroy abandoned for cloud {CloudId} after {Attempts} attempts; landing failed_destroy (orphans visible)")]
    private static partial void LogDestroyAbandoned(ILogger logger, Guid cloudId, int attempts);
}
