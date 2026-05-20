using System.Data;
using System.Security.Cryptography;
using System.Text;
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

public sealed partial class CancelHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthConnections connections,
    ITerraformRunner tf,
    WorkspaceLayout workspaceLayout,
    IConfiguration config,
    ILogger<CancelHandler> log)
{
    private const string Phase = "cancelling";

    public async Task HandleAsync(ProvisioningJob cancelJob, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(cancelJob);
        var cancelJobId = cancelJob.Id;
        cancelJob = await db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJobId, ct);

        var cloud = await db.Clouds
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == cancelJob.CloudId, ct);

        var siblingCreateJobId = await db.ProvisioningJobs
            .Where(j => j.CloudId == cancelJob.CloudId
                     && j.Kind == SagaKinds.Create
                     && !SagaStatus.Terminal.Contains(j.Status))
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);

        var workdir = workspaceLayout.GetJobDir(siblingCreateJobId ?? cancelJob.Id);
        var hadResources = await tf.HasResourcesAsync(workdir, ct);

        if (hadResources)
        {
            try
            {
                await BestEffortDestroyAsync(cloud, cancelJob, workdir, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDestroyFailed(log, ex, cloud.Id);
                cloud.ProvisioningError = $"cancel: terraform destroy failed: {ex.Message}";
                EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
                {
                    ["event"] = "tf_destroy_threw",
                    ["error"] = ex.Message,
                });
            }
        }
        else
        {
            EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
            {
                ["event"] = "no_resources_to_destroy",
            });
        }

        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        var now = clock.GetCurrentInstant();

        if (siblingCreateJobId is { } createId)
        {
            var trackedCreate = await db.ProvisioningJobs.SingleOrDefaultAsync(j => j.Id == createId, ct);
            if (trackedCreate is not null && !SagaStatus.IsTerminal(trackedCreate.Status))
            {
                trackedCreate.Status = SagaStatus.Cancelled;
                trackedCreate.UpdatedAt = now;
            }
        }

        cloud.ProvisioningStatus      = SagaStatus.Cancelled;
        cloud.DestroyedAt             = now;
        cloud.ProvisioningCompletedAt = now;
        cloud.UpdatedAt               = now;

        EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
        {
            ["event"] = "cancel_complete",
            ["had_resources"] = hadResources,
            ["sibling_create_job"] = siblingCreateJobId?.ToString(),
        });

        cancelJob.Status    = SagaStatus.Succeeded;
        cancelJob.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task BestEffortDestroyAsync(
        Cloud cloud,
        ProvisioningJob cancelJob,
        string workdir,
        CancellationToken ct)
    {
        var dek = new byte[32];
        byte[]? providerToken = null;
        try
        {
            if (await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
            {
                if (cloud.Provider != DigitalOceanTfEnv.DigitalOceanProvider && cloud.Provider != "stub")
                    providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);
            }
            else
            {
                LogStepUpMissing(log, cancelJob.Id);
            }

            var connStr = config.GetConnectionString("Portal")
                ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");

            var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conn_str"] = connStr,
            }, ct);
            EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stdout", initResult.Stdout);
            EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stderr", initResult.Stderr);

            if (!initResult.Success)
            {
                cloud.ProvisioningError = "cancel: terraform init failed";
                EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
                {
                    ["event"] = "tf_init_failed",
                    ["exit_code"] = initResult.ExitCode,
                });
                return;
            }

            var destroyEnv = await BuildEnvAsync(cloud, providerToken, dek, ct);
            var destroyResult = await tf.DestroyAsync(workdir, destroyEnv, ct);
            EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stdout", destroyResult.Stdout);
            EventsLogAppender.AppendTerraformStream(cancelJob, clock, Phase, "tf_stderr", destroyResult.Stderr);

            if (destroyResult.Success)
            {
                LogDestroyOk(log, cloud.Id);
                EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
                {
                    ["event"] = "tf_destroy_ok",
                });
            }
            else
            {
                cloud.ProvisioningError = $"cancel: terraform destroy exit={destroyResult.ExitCode}";
                EventsLogAppender.Append(cancelJob, clock, Phase, new JsonObject
                {
                    ["event"] = "tf_destroy_failed",
                    ["exit_code"] = destroyResult.ExitCode,
                });
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (providerToken is not null) CryptographicOperations.ZeroMemory(providerToken);
        }
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

    [LoggerMessage(EventId = 1, Level = LogLevel.Information,
        Message = "CancelHandler: terraform destroy completed for cancelled cloud {CloudId}")]
    private static partial void LogDestroyOk(ILogger logger, Guid cloudId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error,
        Message = "CancelHandler: terraform destroy threw for cancelled cloud {CloudId}; orphans may remain")]
    private static partial void LogDestroyFailed(ILogger logger, Exception ex, Guid cloudId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning,
        Message = "CancelHandler: step-up unlock missing for cancel job {JobId}; destroy will proceed without provider creds (may fail for real providers)")]
    private static partial void LogStepUpMissing(ILogger logger, Guid jobId);
}
