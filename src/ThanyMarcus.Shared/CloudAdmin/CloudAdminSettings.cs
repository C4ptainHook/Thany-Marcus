using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudAdmin;

public sealed record GetSettingsResponse(
    [property: JsonPropertyName("llmMode")]            string LlmMode,
    [property: JsonPropertyName("llmModel")]           string? LlmModel,
    [property: JsonPropertyName("externalApiKeySet")]  bool ExternalApiKeySet);

public sealed record PutSettingsRequest(
    [property: JsonPropertyName("llmMode")]        string LlmMode,
    [property: JsonPropertyName("externalApiKey")] string? ExternalApiKey,
    [property: JsonPropertyName("llmModel")]       string? LlmModel);
