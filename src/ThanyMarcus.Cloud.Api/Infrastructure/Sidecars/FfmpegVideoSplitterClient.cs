using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public sealed partial class FfmpegVideoSplitterClient : IVideoSplitterClient
{
    private readonly IFfmpegRunner ffmpeg;
    private readonly IFfprobeRunner ffprobe;
    private readonly IHttpClientFactory httpFactory;
    private readonly ILogger<FfmpegVideoSplitterClient> log;

    public const string HttpClientName = "VideoSplitterDownload";

    public FfmpegVideoSplitterClient(
        IFfmpegRunner ffmpeg,
        IFfprobeRunner ffprobe,
        IHttpClientFactory httpFactory,
        ILogger<FfmpegVideoSplitterClient> log)
    {
        this.ffmpeg = ffmpeg;
        this.ffprobe = ffprobe;
        this.httpFactory = httpFactory;
        this.log = log;
    }

    public async Task<VideoSplitOutput> SplitAsync(VideoSplitInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var tempDir = Path.Combine(Path.GetTempPath(), $"tm-video-{input.ParentVideo.Id:N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var inputPath = Path.Combine(tempDir, "input");
            await DownloadAsync(input.PresignedVideoUrl, inputPath, ct);

            var probe = await ffprobe.ProbeAsync(inputPath, ct);

            var framesDir = Path.Combine(tempDir, "frames");
            Directory.CreateDirectory(framesDir);
            var framePattern = Path.Combine(framesDir, "%03d.jpg");
            await ffmpeg.RunAsync(
                $"-y -i \"{inputPath}\" -vf fps=1/{input.KeyframeIntervalSeconds} " +
                $"-q:v {QvFromQuality(input.JpegQuality)} \"{framePattern}\"",
                TimeSpan.FromMinutes(5), ct);

            var frameFiles = Directory.EnumerateFiles(framesDir, "*.jpg")
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToList();
            var keyframes = new List<KeyframeUpload>(frameFiles.Count);
            for (var i = 0; i < frameFiles.Count; i++)
            {
                var bytes = await File.ReadAllBytesAsync(frameFiles[i], ct);
                var offset = i * (double)input.KeyframeIntervalSeconds + input.KeyframeIntervalSeconds / 2.0;
                keyframes.Add(new KeyframeUpload(i, offset, bytes));
            }

            var audioPath = Path.Combine(tempDir, "audio.wav");
            await ffmpeg.RunAsync(
                $"-y -i \"{inputPath}\" -ac 1 -ar 16000 -c:a pcm_s16le -vn \"{audioPath}\"",
                TimeSpan.FromMinutes(5), ct);
            var audioBytes = await File.ReadAllBytesAsync(audioPath, ct);
            var audio = new AudioUpload(audioBytes, probe.DurationSeconds);

            return new VideoSplitOutput(
                Keyframes: keyframes,
                Audio: audio,
                VideoDurationSeconds: probe.DurationSeconds,
                VideoCodec: probe.VideoCodec);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { LogCleanup(log, ex, input.ParentVideo.Id); }
        }
    }

    private async Task DownloadAsync(string presignedUrl, string destPath, CancellationToken ct)
    {
        using var http = httpFactory.CreateClient(HttpClientName);
        using var resp = await http.GetAsync(presignedUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        await src.CopyToAsync(dst, ct);
    }

    // ffmpeg -q:v scale runs 2 (best) ... 31 (worst) for mjpeg.
    // q85-ish → q:v 4 is the documented sweet spot.
    private static int QvFromQuality(int q)
    {
        if (q >= 95) return 2;
        if (q >= 85) return 4;
        if (q >= 75) return 6;
        if (q >= 65) return 8;
        return 10;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "video splitter temp cleanup failed: attachment={AttId}")]
    private static partial void LogCleanup(ILogger logger, Exception ex, Guid attId);
}
