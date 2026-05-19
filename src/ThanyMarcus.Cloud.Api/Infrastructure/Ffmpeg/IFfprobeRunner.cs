namespace ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;

public interface IFfprobeRunner
{
    Task<FfprobeResult> ProbeAsync(string inputPath, CancellationToken ct);
    Task<FfprobeResult> ProbeUrlAsync(string url, CancellationToken ct);
}

public sealed record FfprobeResult(
    double DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    int? AudioSampleRate,
    int? AudioChannels,
    int? VideoWidth,
    int? VideoHeight);
