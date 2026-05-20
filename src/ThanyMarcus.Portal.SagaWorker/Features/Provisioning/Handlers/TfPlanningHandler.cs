using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class TfPlanningHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthConnections connections,
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
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedTf, ct);
                return;
            }

            if (cloud.Provider != DigitalOceanTfEnv.DigitalOceanProvider && cloud.Provider != "stub")
            {
                providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);
                if (providerToken is null)
                {
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject
                    {
                        ["error"] = "provider_token_not_found",
                        ["provider"] = cloud.Provider,
                    });
                    job.LastError = $"no provider token for {cloud.Provider}";
                    await SagaTransitions.TransitionToTerminalAsync(
                        db, clock, job, cloud, SagaStatus.FailedTf, ct);
                    return;
                }
            }

            var workdir = await workspaceLayout.RenderAsync(job, cloud, ct);
            var connStr = config.GetConnectionString("Portal")
                ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");
            var pgUrl = ToPostgresUrl(connStr);

            var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["conn_str"] = pgUrl,
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
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedTf, ct);
                return;
            }

            var wsResult = await tf.SelectOrCreateWorkspaceAsync(workdir, cloud.Id.ToString(), ct);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", wsResult.Stdout);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stderr", wsResult.Stderr);
            if (!wsResult.Success)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "workspace_select_failed",
                    ["exit_code"] = wsResult.ExitCode,
                });
                job.LastError = "terraform workspace select/new failed";
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedTf, ct);
                return;
            }

            var planEnv = await BuildEnvAsync(cloud, job, providerToken, dek, ct);
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
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedTf, ct);
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

    private async Task<Dictionary<string, string>> BuildEnvAsync(
        Api.Features.CloudManagement.Cloud cloud,
        ProvisioningJob job,
        byte[]? providerToken,
        byte[] dek,
        CancellationToken ct)
    {
        var enrollmentToken = job.EnrollmentToken
            ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["TF_VAR_cloud_id"] = cloud.Id.ToString(),
            ["TF_VAR_region"] = cloud.Region,
            ["TF_VAR_hostname"] = cloud.Hostname,
            ["TF_VAR_enrollment_token"] = enrollmentToken,
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

    internal static string ToPostgresUrl(string netConnStr)
    {
        var b = new Npgsql.NpgsqlConnectionStringBuilder(netConnStr);
        var user = Uri.EscapeDataString(b.Username ?? "");
        var pass = Uri.EscapeDataString(b.Password ?? "");
        var host = b.Host ?? "localhost";
        var port = b.Port == 0 ? 5432 : b.Port;
        var db   = b.Database ?? "";
        var sslMode = b.SslMode is Npgsql.SslMode.Require or Npgsql.SslMode.VerifyCA or Npgsql.SslMode.VerifyFull
            ? "require"
            : "disable";
        return $"postgres://{user}:{pass}@{host}:{port}/{db}?sslmode={sslMode}";
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "TfPlanningHandler: step-up unlock missing for job {JobId}; failing")]
    private static partial void LogStepUpExpired(ILogger logger, Guid jobId);
}
