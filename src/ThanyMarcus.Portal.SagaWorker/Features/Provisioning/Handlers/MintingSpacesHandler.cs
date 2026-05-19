using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed partial class MintingSpacesHandler(
    PortalDbContext db,
    IClock clock,
    IInfraOpUnlockCache unlockCache,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthConnections connections,
    IDigitalOceanOAuthClient doClient,
    ILogger<MintingSpacesHandler> log) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.MintingSpaces;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        var jobId = job.Id;
        job = await db.ProvisioningJobs.SingleAsync(j => j.Id == jobId, ct);
        var cloud = await db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);

        if (cloud.MintingSpacesStartedAt is null)
            cloud.MintingSpacesStartedAt = clock.GetCurrentInstant();

        if (cloud.Provider != KnownProviders.DigitalOcean)
        {
            EventsLogAppender.Append(job, clock, Phase, new JsonObject
            {
                ["event"] = "skip_non_do",
                ["provider"] = cloud.Provider,
            });
            await SagaTransitions.TransitionAsync(
                db, clock, job, SagaStatus.TfPlanning, Duration.Zero, ct: ct);
            return;
        }

        var dek = new byte[32];
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
}
