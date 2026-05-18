using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudAdmin;

public sealed record AdminIssuePluginTokenRequest(
    [property: JsonPropertyName("token_hash")] string TokenHashBase64,
    [property: JsonPropertyName("label")]      string Label);

public sealed record AdminIssuePluginTokenResponse(
    [property: JsonPropertyName("token_id")] Guid TokenId);
