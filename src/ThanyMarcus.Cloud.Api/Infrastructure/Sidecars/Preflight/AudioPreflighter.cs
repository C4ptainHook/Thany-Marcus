using System.Globalization;
using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public sealed partial class AudioPreflighter : IAudioPreflighter
{
    private readonly IArtifactStore store;
    private readonly IFfprobeRunner ffprobe;
    private readonly IFfmpegRunner ffmpeg;
    private readonly AudioFilterOptions options;
    private readonly ILogger<AudioPreflighter> log;

    public AudioPreflighter(
        IArtifactStore store,
        IFfprobeRunner ffprobe,
        IFfmpegRunner ffmpeg,
        AudioFilterOptions options,
        ILogger<AudioPreflighter> log)
    {
        this.store = store;
        this.ffprobe = ffprobe;
        this.ffmpeg = ffmpeg;
        this.options = options;
        this.log = log;
    }

    public async Task<PreflightResult> CheckAsync(Attachment att, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(att);

        if (att.ByteSize is { } size && size > options.MaxSizeBytes)
        {
            return PreflightResult.Skip($"size_{size}_gt_{options.MaxSizeBytes}");
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"tm-audio-{att.Id:N}");
        Directory.CreateDirectory(tempDir);
        var inputPath = Path.Combine(tempDir, "input");
        var wavPath = Path.Combine(tempDir, "audio.wav");
        try
        {
            await using (var src = await store.OpenReadAsync(att.StorageKey, ct))
            await using (var dst = File.Create(inputPath))
            {
                await src.CopyToAsync(dst, ct);
            }

            FfprobeResult probe;
            try
            {
                probe = await ffprobe.ProbeAsync(inputPath, ct);
            }
            catch (FfmpegRunnerException)
            {
                return PreflightResult.Skip("ffprobe_failed");
            }

            if (probe.AudioCodec is null)
            {
                return PreflightResult.Skip("no_decodable_audio_stream");
            }
            if (probe.DurationSeconds > options.MaxDurationSeconds)
            {
                return PreflightResult.Skip(
                    $"duration_{probe.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}_gt_{options.MaxDurationSeconds}");
            }

            await ffmpeg.RunAsync(
                $"-y -i \"{inputPath}\" -ac 1 -ar 16000 -c:a pcm_s16le -vn \"{wavPath}\"",
                TimeSpan.FromMinutes(2), ct);

            float maxRms;
            await using (var fs = File.OpenRead(wavPath))
            {
                maxRms = SilenceDetector.MaxRmsFromWav(fs, options.SilenceWindowMs);
            }
            if (maxRms < options.SilenceRmsThreshold)
            {
                return PreflightResult.Skip(
                    $"silent_rms_{maxRms.ToString("F4", CultureInfo.InvariantCulture)}",
                    BuildMeta(probe, maxRms));
            }
            return PreflightResult.PassWith(BuildMeta(probe, maxRms));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); }
            catch (Exception ex) { LogCleanup(log, ex, att.Id); }
        }
    }

    private static JsonDocument BuildMeta(FfprobeResult probe, float maxRms)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("duration_s", probe.DurationSeconds);
            if (probe.AudioCodec is not null) writer.WriteString("codec", probe.AudioCodec);
            if (probe.AudioSampleRate is { } sr) writer.WriteNumber("sample_rate", sr);
            if (probe.AudioChannels is { } ch) writer.WriteNumber("channels", ch);
            writer.WriteNumber("max_rms", maxRms);
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "audio preflight temp cleanup failed: attachment={AttId}")]
    private static partial void LogCleanup(ILogger logger, Exception ex, Guid attId);
}
