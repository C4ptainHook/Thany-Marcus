using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class TfPlanningHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
    ITerraformRunner tf,
    WorkspaceLayout workspaceLayout,
    IConfiguration config,
    ILogger<TfPlanningHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.TfPlanning;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        if (cloud.PlanStartedAt is null)
        {
            cloud.PlanStartedAt = clock.GetCurrentInstant();
        }

        var dek = new byte[32];
        byte[]? providerToken = null;
        try
        {
            if (!await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
            {
                LogStepUpExpired(log, job.Id);
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "step_up_required_but_not_unlocked",
                });
                job.LastError = "step-up unlock expired or missing";
                await SagaTransitions.TransitionAsync(
                    db, clock, job, SagaStatus.FailedTf, Duration.Zero, ct: ct);
                return;
            }

            providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);
            if (providerToken is null && cloud.Provider != "stub")
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "provider_token_not_found",
                    ["provider"] = cloud.Provider,
                });
                job.LastError = $"no provider token for {cloud.Provider}";
                await SagaTransitions.TransitionAsync(
                    db, clock, job, SagaStatus.FailedTf, Duration.Zero, ct: ct);
                return;
            }

            var workdir = await workspaceLayout.RenderAsync(job, cloud, ct);
            var connStr = config.GetConnectionString("Portal")
                ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");

            var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conn_str"] = connStr,
            }, ct);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", initResult.Stdout);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stderr", initResult.Stderr);

            if (!initResult.Success)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "init_failed",
                    ["exit_code"] = initResult.ExitCode,
                });
                job.LastError = "terraform init failed";
                await SagaTransitions.TransitionAsync(
                    db, clock, job, SagaStatus.FailedTf, Duration.Zero, ct: ct);
                return;
            }

            var planEnv = BuildEnv(cloud, providerToken);
            var planResult = await tf.PlanAsync(workdir, planEnv, ct);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", planResult.Stdout);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stderr", planResult.Stderr);

            if (!planResult.Success)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "plan_failed",
                    ["exit_code"] = planResult.ExitCode,
                });
                job.LastError = "terraform plan failed";
                await SagaTransitions.TransitionAsync(
                    db, clock, job, SagaStatus.FailedTf, Duration.Zero, ct: ct);
                return;
            }

            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "plan_succeeded",
            });
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.TfApplying, Duration.Zero, ct: ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (providerToken is not null) CryptographicOperations.ZeroMemory(providerToken);
        }
    }

    private static Dictionary<string, string> BuildEnv(Api.Features.CloudManagement.Cloud cloud, byte[]? providerToken)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TF_VAR_cloud_id"] = cloud.Id.ToString(),
            ["TF_VAR_region"] = cloud.Region,
            ["TF_VAR_hostname"] = cloud.Hostname,
            ["TF_VAR_enrollment_token"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
        };
        if (providerToken is not null)
        {
            env["TF_VAR_provider_token"] = Encoding.UTF8.GetString(providerToken);
        }
        return env;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "TfPlanningHandler: step-up unlock missing for job {JobId}; failing")]
    private static partial void LogStepUpExpired(ILogger logger, Guid jobId);
}
