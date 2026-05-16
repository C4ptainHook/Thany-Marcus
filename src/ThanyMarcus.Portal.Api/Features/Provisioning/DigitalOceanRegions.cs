namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public static class DigitalOceanRegions
{
    public sealed record RegionInfo(string Slug, string Label, string Continent);

    public static readonly IReadOnlyList<RegionInfo> Catalog =
    [
        new("nyc1", "New York 1",    "North America"),
        new("nyc3", "New York 3",    "North America"),
        new("sfo3", "San Francisco", "North America"),
        new("tor1", "Toronto",       "North America"),
        new("ams3", "Amsterdam",     "Europe"),
        new("fra1", "Frankfurt",     "Europe"),
        new("lon1", "London",        "Europe"),
        new("blr1", "Bangalore",     "Asia-Pacific"),
        new("sgp1", "Singapore",     "Asia-Pacific"),
        new("syd1", "Sydney",        "Asia-Pacific"),
    ];

    public static readonly IReadOnlySet<string> Allowed =
        Catalog.Select(r => r.Slug).ToHashSet(StringComparer.Ordinal);

    public static bool IsAllowed(string region) => Allowed.Contains(region);
}
