using System.Text.Json.Serialization;

namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Cloudflare;

public sealed record CloudflareEnvelope<T>(
    [property: JsonPropertyName("success")] bool Success,
    [property: JsonPropertyName("errors")] List<CloudflareErrorDetail>? Errors,
    [property: JsonPropertyName("result")] T? Result);

public sealed record CloudflareErrorDetail(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record CloudflareDnsRecordDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("content")] string Content);

public sealed class CloudflareApiException(string message) : Exception(message);
