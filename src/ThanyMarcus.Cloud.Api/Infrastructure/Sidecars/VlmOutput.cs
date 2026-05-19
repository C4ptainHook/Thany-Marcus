using System.Text.Json.Serialization;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed record VlmOutput(
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("text_in_image")] string? TextInImage);
