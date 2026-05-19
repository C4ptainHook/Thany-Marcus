using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IVideoSplitterClient
{
    Task<VideoSplitOutput> SplitAsync(VideoSplitInput input, CancellationToken ct);
}

public sealed record VideoSplitInput(
    Attachment ParentVideo,
    string PresignedVideoUrl,
    int KeyframeIntervalSeconds,
    int JpegQuality);

public sealed record VideoSplitOutput(
    IReadOnlyList<KeyframeUpload> Keyframes,
    AudioUpload Audio,
    double VideoDurationSeconds,
    string? VideoCodec);

public sealed record KeyframeUpload(int Index, double OffsetSeconds, byte[] Bytes);

public sealed record AudioUpload(byte[] WavBytes, double DurationSeconds);
