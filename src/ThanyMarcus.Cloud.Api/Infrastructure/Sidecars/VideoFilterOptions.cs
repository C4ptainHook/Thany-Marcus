namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed class VideoFilterOptions
{
    public int MaxDurationSeconds { get; init; } = 300;
    public int KeyframeIntervalSeconds { get; init; } = 5;
    public int MaxKeyframes { get; init; } = 60;
    public int JpegQuality { get; init; } = 85;
    public TimeSpan PresignedUrlTtl { get; init; } = TimeSpan.FromMinutes(5);
}
