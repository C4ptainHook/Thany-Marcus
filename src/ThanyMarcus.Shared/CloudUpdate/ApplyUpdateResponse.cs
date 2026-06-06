using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudUpdate;

public sealed record ApplyUpdateResponse(
    [property: JsonPropertyName("accepted")] bool Accepted,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("target_version")] string TargetVersion,
    [property: JsonPropertyName("message")] string Message);
