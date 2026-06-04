using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class TfPlanningHandler(
    PortalDbContext db,
    IClock clock,
    ISagaCredentialSource credentials,
    IProvisioningProviderRegistry providers,
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
        var provider = providers.Resolve(cloud.Provider);

        if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
            return;

        if (cloud.PlanStartedAt is null)
        {
            cloud.PlanStartedAt = clock.GetCurrentInstant();
        }

        var dek = new byte[32];
        try
        {
            if (!await credentials.TryGetDekAsync(cloud, dek, ct))
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

            var planEnv = await BuildEnvAsync(provider, cloud, job, dek, ct);
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

            if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
                return;

            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.TfApplying, Duration.Zero, ct: ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static async Task<Dictionary<string, string>> BuildEnvAsync(
        IProvisioningProvider provider,
        Api.Features.CloudManagement.Cloud cloud,
        ProvisioningJob job,
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
        await provider.AddProvisioningEnvAsync(env, cloud, dek, ct);
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
