using System.Text.Json.Serialization;
using NodaTime;

namespace ThanyMarcus.Shared.ReleaseFeed;

public sealed record ReleaseDescriptor(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("strategy")] string Strategy,
    [property: JsonPropertyName("compose_yaml")] string ComposeYaml,
    [property: JsonPropertyName("image_digests")] IReadOnlyDictionary<string, string> ImageDigests,
    [property: JsonPropertyName("model_tags")] IReadOnlyDictionary<string, string> ModelTags,
    [property: JsonPropertyName("env_overlay")] IReadOnlyDictionary<string, string> EnvOverlay,
    [property: JsonPropertyName("schema_min_from")] string SchemaMinFrom,
    [property: JsonPropertyName("notes")] string? Notes,
    [property: JsonPropertyName("released_at")] Instant ReleasedAt);
