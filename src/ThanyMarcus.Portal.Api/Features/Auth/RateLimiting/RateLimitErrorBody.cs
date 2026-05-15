using System.Text.Json.Serialization;

namespace ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;

public sealed record RateLimitErrorBody(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("retry_after_seconds")] int RetryAfterSeconds);
