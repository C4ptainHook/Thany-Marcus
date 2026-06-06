using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Events;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class IssuingPluginTokenHandler(
    PortalDbContext db,
    IClock clock,
    ICloudAdminTokenAccessor adminTokens,
    IPortalToCloudPluginTokenClient cloudClient,
    IProvisioningEventBus eventBus,
    IOptionsMonitor<PluginTokenSyncOptions> options,
    ILogger<IssuingPluginTokenHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.IssuingPluginToken;

    private const string Label = "plugin";

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
            return;

        var adminToken = await adminTokens.GetPlaintextAsync(cloud.Id, ct);
        if (string.IsNullOrEmpty(adminToken))
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["error"] = "missing_admin_token",
            });
            job.LastError = "missing_admin_token";
            await SagaTransitions.TransitionToTerminalAsync(
                db, clock, job, cloud, SagaStatus.FailedPluginToken, ct);
            return;
        }

        var raw = "tm_" + RandomNumberGenerator.GetHexString(64).ToLowerInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var cloudUrl = $"https://{cloud.Hostname}/admin/plugin-tokens";

        Guid cloudTokenId;
        try
        {
            cloudTokenId = await cloudClient.PostAsync(cloudUrl, adminToken, hashBytes, Label, ct);
        }
        catch (PluginTokenSyncException ex)
        {
            LogSyncFailure(log, ex, job.Id, ex.StatusCode);
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["error"] = ex.Message,
                ["status_code"] = ex.StatusCode,
                ["attempt_count"] = (int)job.AttemptCount,
            });
            job.LastError = ex.Message;

            var opts = options.CurrentValue;
            var nonRetryable = ex.StatusCode is 400 or 401;
            if (nonRetryable || job.AttemptCount >= opts.MaxAttempts)
            {
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedPluginToken, ct);
                return;
            }

            job.AttemptCount = (short)(job.AttemptCount + 1);
            var backoff = Duration.FromSeconds(Math.Pow(2, job.AttemptCount));
            await SagaTransitions.RescheduleAsync(db, clock, job, backoff, ct);
            return;
        }

        db.PluginTokenMetadata.Add(new PluginTokenMetadata
        {
            CloudId   = cloud.Id,
            Name      = Label,
            TokenHash = hashBytes,
            CreatedAt = clock.GetCurrentInstant(),
            UpdatedAt = clock.GetCurrentInstant(),
        });

        var deepLink = $"obsidian://thany-marcus-connect?cloudUrl=https://{cloud.Hostname}&token={raw}";

        EventsLogAppender.Append(job, clock, Phase, new JsonObject
        {
            ["event"]          = "plugin_token_issued",
            ["cloud_token_id"] = cloudTokenId.ToString(),
        });

        cloud.ProvisioningCompletedAt = clock.GetCurrentInstant();
        job.AdminTokenCiphertext = null;

        await db.SaveChangesAsync(ct);
        await eventBus.PublishPluginTokenIssuedAsync(cloud.Id, raw, deepLink, ct);

        await SagaTransitions.TransitionToTerminalAsync(
            db, clock, job, cloud, SagaStatus.Succeeded, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "IssuingPluginTokenHandler: cloud sync failed for job {JobId} (status={StatusCode})")]
    private static partial void LogSyncFailure(ILogger logger, Exception ex, Guid jobId, int? statusCode);
}
