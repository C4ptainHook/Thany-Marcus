namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class DigitalOceanRegions
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "nyc1", "nyc3",
        "sfo3",
        "ams3",
        "sgp1",
        "lon1",
        "fra1",
        "tor1",
        "blr1",
        "syd1",
    };

    public static bool IsAllowed(string region) => Allowed.Contains(region);
}
