using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

public sealed class AlwaysPassDocumentPreflighter : IDocumentPreflighter
{
    public Task<PreflightResult> CheckAsync(Attachment att, CancellationToken ct) =>
        Task.FromResult(PreflightResult.Pass);
}

public sealed class AlwaysPassAudioPreflighter : IAudioPreflighter
{
    public Task<PreflightResult> CheckAsync(Attachment att, CancellationToken ct) =>
        Task.FromResult(PreflightResult.Pass);
}

public sealed class InMemoryDoclingClient(string markdown) : IDoclingClient
{
    public Task<string> ExtractMarkdownAsync(string storageKey, string mimeType, CancellationToken ct) =>
        Task.FromResult(markdown);
}

public sealed class InMemoryParakeetClient(string transcript, string? language = "en") : IParakeetClient
{
    public Task<ParakeetTranscript> TranscribeAsync(string storageKey, CancellationToken ct) =>
        Task.FromResult(new ParakeetTranscript(transcript, language));
}

public sealed class InMemoryVideoSplitterClient(
    int keyframeCount = 0, double durationSeconds = 0, string? videoCodec = null)
    : IVideoSplitterClient
{
    public int CallCount { get; private set; }

    public Task<VideoSplitOutput> SplitAsync(VideoSplitInput input, CancellationToken ct)
    {
        CallCount++;
        var keyframes = Enumerable.Range(0, keyframeCount)
            .Select(i => new KeyframeUpload(i, i * 5.0 + 2.5, new byte[] { (byte)i }))
            .ToList();
        return Task.FromResult(new VideoSplitOutput(
            Keyframes: keyframes,
            Audio: new AudioUpload(Array.Empty<byte>(), durationSeconds),
            VideoDurationSeconds: durationSeconds,
            VideoCodec: videoCodec));
    }
}
