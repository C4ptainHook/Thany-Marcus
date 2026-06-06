using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudUpdate;

public sealed record CloudUpdateStatus(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("current_version")] string CurrentVersion,
    [property: JsonPropertyName("target_version")] string? TargetVersion,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("updated_at")] string? UpdatedAt);
