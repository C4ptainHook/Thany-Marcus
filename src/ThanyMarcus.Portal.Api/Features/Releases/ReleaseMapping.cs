using System.Text.Json;
using ThanyMarcus.Shared.ReleaseFeed;

namespace ThanyMarcus.Portal.Api.Features.Releases;

public static class ReleaseMapping
{
    public static ReleaseDescriptor ToDescriptor(Release release) => new(
        Version: release.Version,
        Strategy: release.Strategy,
        ComposeYaml: release.ComposeYaml,
        ImageDigests: ReadDictionary(release.ImageDigests),
        ModelTags: ReadDictionary(release.ModelTags),
        EnvOverlay: ReadDictionary(release.EnvOverlay),
        SchemaMinFrom: release.SchemaMinFrom,
        Notes: release.Notes,
        ReleasedAt: release.CreatedAt);

    public static JsonDocument ToJsonDocument(IReadOnlyDictionary<string, string>? values) =>
        JsonSerializer.SerializeToDocument(values ?? new Dictionary<string, string>());

    private static Dictionary<string, string> ReadDictionary(JsonDocument document) =>
        document.RootElement.Deserialize<Dictionary<string, string>>() ?? new Dictionary<string, string>();
}
