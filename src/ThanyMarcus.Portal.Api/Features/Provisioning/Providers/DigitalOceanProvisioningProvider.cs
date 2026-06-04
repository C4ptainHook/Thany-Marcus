using Microsoft.Extensions.Logging;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;

namespace ThanyMarcus.Portal.Api.Features.Provisioning.Providers;

public sealed partial class DigitalOceanProvisioningProvider(
    IDigitalOceanOAuthConnections connections,
    ICloudSecretBundle secrets,
    IDigitalOceanOAuthClient doClient,
    ILogger<DigitalOceanProvisioningProvider> log) : IProvisioningProvider
{
    public string Key => KnownProviders.DigitalOcean;
    public bool UserCreatable => true;
    public bool RequiresCredentials => true;
    public bool MintsObjectStorageCredentials => true;
    public bool SupportsPricing => true;
    public string InitialStatusAfterCreate => SagaStatus.MintingSpaces;

    public async Task AddProvisioningEnvAsync(
        IDictionary<string, string> env, Cloud cloud, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var accessToken  = await connections.GetAccessTokenAsync(cloud.UserId, dek, ct);
        var spacesId     = await secrets.TryGetAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, dek, ct);
        var spacesSecret = await secrets.TryGetAsync(cloud.Id, CloudSecretKind.DoSpacesSecret,   dek, ct);

        if (accessToken is null || spacesId is null || spacesSecret is null)
            return;

        env["DIGITALOCEAN_TOKEN"]       = accessToken;
        env["TF_VAR_provider_token"]    = accessToken;
        env["SPACES_ACCESS_KEY_ID"]     = spacesId;
        env["SPACES_SECRET_ACCESS_KEY"] = spacesSecret;
    }

    public async Task RevokeCredentialsAsync(Cloud cloud, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        string? accessToken;
        string? spacesId;
        try
        {
            accessToken = await connections.GetAccessTokenAsync(cloud.UserId, dek, ct);
            spacesId    = await secrets.TryGetAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, dek, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogRevokeReadFailed(log, ex, cloud.Id);
            return;
        }

        if (accessToken is not null && spacesId is not null)
        {
            try { await doClient.DeleteSpacesKeyAsync(accessToken, spacesId, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSpacesKeyDeleteFailed(log, ex, cloud.Id);
            }
        }

        await secrets.DeleteAllForCloudAsync(cloud.Id, ct);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "DigitalOceanProvisioningProvider: failed to read DO secrets for revoke (cloud {CloudId}); skipping best-effort revoke")]
    private static partial void LogRevokeReadFailed(ILogger logger, Exception ex, Guid cloudId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning,
        Message = "DigitalOceanProvisioningProvider: DELETE /v2/spaces/keys failed for cloud {CloudId}; continuing")]
    private static partial void LogSpacesKeyDeleteFailed(ILogger logger, Exception ex, Guid cloudId);
}
