using System.Net;
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

public sealed partial class TfApplyingHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    IProviderTokenVault providerVault,
    ITerraformRunner tf,
    WorkspaceLayout workspaceLayout,
    IConfiguration config,
    ILogger<TfApplyingHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.TfApplying;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        if (cloud.ApplyStartedAt is null)
        {
            cloud.ApplyStartedAt = clock.GetCurrentInstant();
        }

        var workdir = workspaceLayout.GetJobDir(job.Id);
        var planPath = Path.Combine(workdir, "plan.tfplan");
        var needsReplan = !File.Exists(planPath);

        var dek = new byte[32];
        byte[]? providerToken = null;
        try
        {
            if (!await unlockCache.TryGetAsync(cloud.UserId, dek, ct))
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "step_up_required_but_not_unlocked",
                });
                job.LastError = "step-up unlock expired or missing";
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedTf, ct);
                return;
            }

            providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);
            if (providerToken is null && cloud.Provider != "stub")
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "provider_token_not_found",
                });
                job.LastError = $"no provider token for {cloud.Provider}";
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedTf, ct);
                return;
            }

            if (needsReplan)
            {
                LogReplan(log, job.Id);
                await workspaceLayout.RenderAsync(job, cloud, ct);
                var connStr = config.GetConnectionString("Portal")
                    ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured");

                var initResult = await tf.InitAsync(workdir, new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["conn_str"] = connStr,
                }, ct);
                EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", initResult.Stdout);
                if (!initResult.Success)
                {
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject { ["error"] = "re_init_failed" });
                    job.LastError = "terraform init (recovery) failed";
                    await SagaTransitions.TransitionToTerminalAsync(
                        db, clock, job, cloud, SagaStatus.FailedTf, ct);
                    return;
                }

                var planEnv = BuildEnv(cloud, providerToken);
                var planResult = await tf.PlanAsync(workdir, planEnv, ct);
                EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", planResult.Stdout);
                if (!planResult.Success)
                {
                    EventsLogAppender.Append(job, clock, Phase, new JsonObject { ["error"] = "re_plan_failed" });
                    job.LastError = "terraform plan (recovery) failed";
                    await SagaTransitions.TransitionToTerminalAsync(
                        db, clock, job, cloud, SagaStatus.FailedTf, ct);
                    return;
                }
            }

            var applyEnv = BuildEnv(cloud, providerToken);
            var applyResult = await tf.ApplyAsync(workdir, applyEnv, ct);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stdout", applyResult.Stdout);
            EventsLogAppender.AppendTerraformStream(job, clock, Phase, "tf_stderr", applyResult.Stderr);

            if (!applyResult.Success)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "apply_failed",
                    ["exit_code"] = applyResult.ExitCode,
                    ["rollback_reason"] = "tf_apply_failed",
                });
                job.LastError = "terraform apply failed";
                await SagaTransitions.TransitionAsync(
                    db, clock, job, SagaStatus.RollingBackTf, Duration.Zero, ct: ct);
                return;
            }

            JsonDocument tfOutputs;
            try
            {
                tfOutputs = await tf.OutputJsonAsync(workdir, ct);
            }
            catch (TerraformException ex)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = $"tf_output_failed: {ex.Message}",
                    ["rollback_reason"] = "tf_apply_failed",
                });
                job.LastError = "terraform output -json failed";
                await SagaTransitions.TransitionAsync(
                    db, clock, job, SagaStatus.RollingBackTf, Duration.Zero, ct: ct);
                return;
            }

            cloud.VmIp = TryReadIp(tfOutputs);

            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "apply_succeeded",
                ["ip"] = cloud.VmIp,
            });

            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.DnsCreating, Duration.Zero,
                tfOutputs: tfOutputs, ct: ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
            if (providerToken is not null) CryptographicOperations.ZeroMemory(providerToken);
        }
    }

    private static string? TryReadIp(JsonDocument tfOutputs)
    {
        var root = tfOutputs.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("ip", out var ipEntry)) return null;
        if (ipEntry.ValueKind != JsonValueKind.Object) return null;
        if (!ipEntry.TryGetProperty("value", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var raw = value.GetString();
        return IPAddress.TryParse(raw, out _) ? raw : null;
    }

    private static Dictionary<string, string> BuildEnv(Cloud cloud, byte[]? providerToken)
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
        Message = "TfApplyingHandler: plan.tfplan missing for job {JobId}; re-rendering and re-planning")]
    private static partial void LogReplan(ILogger logger, Guid jobId);
}
