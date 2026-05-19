using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

internal static class DigitalOceanTfEnv
{
    public const string DigitalOceanProvider = "digitalocean";

    public static async Task<bool> TryAddDoEnvVarsAsync(
        Dictionary<string, string> env,
        Guid cloudId,
        ReadOnlyMemory<byte> dek,
        ICloudSecretBundle secrets,
        CancellationToken ct)
    {
        var accessToken  = await secrets.TryGetAsync(cloudId, CloudSecretKind.DoOAuthAccess,    dek, ct);
        var spacesId     = await secrets.TryGetAsync(cloudId, CloudSecretKind.DoSpacesAccessId, dek, ct);
        var spacesSecret = await secrets.TryGetAsync(cloudId, CloudSecretKind.DoSpacesSecret,   dek, ct);

        if (accessToken is null || spacesId is null || spacesSecret is null)
            return false;

        env["DIGITALOCEAN_TOKEN"]       = accessToken;
        env["SPACES_ACCESS_KEY_ID"]     = spacesId;
        env["SPACES_SECRET_ACCESS_KEY"] = spacesSecret;
        return true;
    }
}
