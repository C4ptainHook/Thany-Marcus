using System.Text.Json.Serialization;

namespace ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;

public sealed class DoTokenResponse
{
    [JsonPropertyName("access_token")]
    public string AccessToken { get; init; } = string.Empty;

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; init; } = string.Empty;

    [JsonPropertyName("token_type")]
    public string TokenType { get; init; } = "bearer";

    [JsonPropertyName("expires_in")]
    public long ExpiresIn { get; init; }

    [JsonPropertyName("scope")]
    public string Scope { get; init; } = string.Empty;
}

public sealed class DoSpacesKeyMint
{
    public required string AccessKeyId { get; init; }
    public required string SecretKey   { get; init; }
}

internal sealed class DoSpacesKeyResponseEnvelope
{
    [JsonPropertyName("key")]
    public DoSpacesKey? Key { get; init; }
}

internal sealed class DoSpacesKey
{
    [JsonPropertyName("access_key")]
    public string AccessKey { get; init; } = string.Empty;

    [JsonPropertyName("secret_key")]
    public string SecretKey { get; init; } = string.Empty;
}

public sealed class DigitalOceanOAuthException(string message, int statusCode)
    : Exception($"{message} (status={statusCode})")
{
    public int StatusCode { get; } = statusCode;
}

public sealed class DigitalOceanOAuthRefreshFailedException(string message, int statusCode)
    : Exception($"{message} (status={statusCode})")
{
    public int StatusCode { get; } = statusCode;
}
