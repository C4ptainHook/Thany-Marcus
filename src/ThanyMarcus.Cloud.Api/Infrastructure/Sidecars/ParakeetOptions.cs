namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class ParakeetOptions
{
    public string BaseUrl { get; init; } = "http://parakeet:5092";
    public string HealthPath { get; init; } = "/health";
    public string TranscribePath { get; init; } = "/v1/audio/transcriptions";
    public int RequestTimeoutSeconds { get; init; } = 180;
    public TimeSpan PresignedUrlTtl { get; init; } = TimeSpan.FromMinutes(5);
    public int MaxChunkSeconds { get; init; } = 30;
}
