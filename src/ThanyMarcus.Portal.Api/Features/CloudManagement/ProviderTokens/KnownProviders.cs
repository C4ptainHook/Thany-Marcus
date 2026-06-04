namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public static class KnownProviders
{
    public const string DigitalOcean = "digitalocean";
    public const string Azure        = "azure";
    public const string Cloudflare   = "cloudflare";
    public const string Stub         = "stub";

    public static readonly IReadOnlyList<string> All = [DigitalOcean, Azure, Cloudflare];

    public static bool IsValid(string provider) => All.Contains(provider, StringComparer.Ordinal);

    public static bool RequiresCredentials(string provider) =>
        !string.Equals(provider, Stub, StringComparison.Ordinal);
}
