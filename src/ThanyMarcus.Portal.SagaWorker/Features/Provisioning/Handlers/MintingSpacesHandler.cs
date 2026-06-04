using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Providers;
using ThanyMarcus.Portal.Api.Features.Provisioning.SagaCredentials;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class MintingSpacesHandler(
    PortalDbContext db,
    IClock clock,
    ISagaCredentialSource credentials,
    IProvisioningProviderRegistry providers,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthConnections connections,
    IDigitalOceanOAuthClient doClient,
    ISpacesKeyProbe probe,
    ILogger<MintingSpacesHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.MintingSpaces;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        var provider = providers.Resolve(cloud.Provider);

        if (await SagaTransitions.TryRouteCancelAsync(db, clock, job, cloud, Phase, ct))
            return;

        if (cloud.MintingSpacesStartedAt is null)
            cloud.MintingSpacesStartedAt = clock.GetCurrentInstant();

        if (!provider.MintsObjectStorageCredentials)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "skip_non_minting_provider",
                ["provider"] = cloud.Provider,
            });
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.TfPlanning, Duration.Zero, ct: ct);
            return;
        }

        var dek = new byte[32];
        try
        {
            if (!await credentials.TryGetDekAsync(cloud, dek, ct))
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "step_up_required_but_not_unlocked",
                });
                job.LastError = "step-up unlock expired or missing";
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedMintingSpaces, ct);
                return;
            }

            var accessToken = await connections.GetAccessTokenAsync(cloud.UserId, dek, ct);
            if (accessToken is null)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "do_oauth_connection_missing",
                });
                job.LastError = "DigitalOcean is not connected for this user";
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedMintingSpaces, ct);
                return;
            }

            DoSpacesKeyMint mint;
            var keyName = $"thany-cloud-{cloud.Id:N}";
            try
            {
                mint = await doClient.MintSpacesFullAccessKeyAsync(accessToken, keyName, ct);
            }
            catch (DigitalOceanOAuthException ex)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"] = "mint_spaces_failed",
                    ["status_code"] = ex.StatusCode,
                });
                job.LastError = $"DO mint spaces key failed (status={ex.StatusCode})";
                LogMintFailed(log, job.Id, ex.StatusCode);
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedMintingSpaces, ct);
                return;
            }

            await secrets.PutAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, mint.AccessKeyId, dek, null, ct);
            await secrets.PutAsync(cloud.Id, CloudSecretKind.DoSpacesSecret,   mint.SecretKey,   dek, null, ct);

            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"]          = "spaces_key_minted",
                ["access_key_id"]  = mint.AccessKeyId,
            });
            await db.SaveChangesAsync(ct);

            var probeStart = clock.GetCurrentInstant();
            try
            {
                await probe.WaitForActiveAsync(cloud.Region, mint.AccessKeyId, mint.SecretKey, ct);
            }
            catch (TimeoutException ex)
            {
                EventsLogAppender.Append(job, clock, Phase, new JsonObject
                {
                    ["error"]         = "spaces_key_activation_timeout",
                    ["access_key_id"] = mint.AccessKeyId,
                });
                job.LastError = ex.Message;
                LogActivationTimeout(log, job.Id, mint.AccessKeyId);
                await SagaTransitions.TransitionToTerminalAsync(
                    db, clock, job, cloud, SagaStatus.FailedMintingSpaces, ct);
                return;
            }
            var elapsedMs = (long)(clock.GetCurrentInstant() - probeStart).TotalMilliseconds;
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"]            = "spaces_key_active",
                ["access_key_id"]    = mint.AccessKeyId,
                ["activation_ms"]    = elapsedMs,
            });

            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.TfPlanning, Duration.Zero,
                cloud: cloud,
                cloudMutation: c => c.ProvisioningStatus = SagaStatus.TfPlanning,
                ct: ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "MintingSpacesHandler: DO mint spaces key failed for job {JobId} with status {StatusCode}")]
    private static partial void LogMintFailed(ILogger logger, Guid jobId, int statusCode);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "MintingSpacesHandler: Spaces key activation timeout for job {JobId} (access_key_id={AccessKeyId})")]
    private static partial void LogActivationTimeout(ILogger logger, Guid jobId, string accessKeyId);
}
