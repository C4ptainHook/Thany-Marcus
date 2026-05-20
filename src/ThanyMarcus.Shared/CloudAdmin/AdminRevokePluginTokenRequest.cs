using System.Text.Json.Serialization;

namespace ThanyMarcus.Shared.CloudAdmin;

public sealed record AdminRevokePluginTokenRequest(
    [property: JsonPropertyName("token_hash")] string TokenHashBase64);
