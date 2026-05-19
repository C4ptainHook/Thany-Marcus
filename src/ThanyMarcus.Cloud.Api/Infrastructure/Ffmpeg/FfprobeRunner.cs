using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;

public sealed partial class FfprobeRunner : IFfprobeRunner
{
    private readonly ILogger<FfprobeRunner> log;
    private readonly string executable;
    private readonly TimeSpan timeout;

    public FfprobeRunner(ILogger<FfprobeRunner> log, IConfiguration config)
    {
        this.log = log;
        executable = config["IngestSaga:Ffmpeg:FfprobeExecutable"] ?? "ffprobe";
        timeout    = TimeSpan.FromSeconds(
            config.GetValue("IngestSaga:Ffmpeg:FfprobeTimeoutSeconds", 15));
    }

    public Task<FfprobeResult> ProbeAsync(string inputPath, CancellationToken ct) =>
        RunAsync(QuoteArg(inputPath), ct);

    public Task<FfprobeResult> ProbeUrlAsync(string url, CancellationToken ct) =>
        RunAsync(QuoteArg(url), ct);

    private async Task<FfprobeResult> RunAsync(string quotedInput, CancellationToken ct)
    {
        var args =
            "-v quiet -print_format json -show_format -show_streams " + quotedInput;
        var psi = new ProcessStartInfo
        {
            FileName               = executable,
            Arguments              = args,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var proc = new Process { StartInfo = psi };
        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        if (!proc.Start())
        {
            throw new FfmpegRunnerException("failed to start ffprobe", -1, "");
        }
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new FfmpegRunnerException(
                $"ffprobe timeout after {timeout}", -1, stderrBuf.ToString());
        }

        if (proc.ExitCode != 0)
        {
            throw new FfmpegRunnerException(
                $"ffprobe exited {proc.ExitCode}: {stderrBuf}", proc.ExitCode, stderrBuf.ToString());
        }

        return Parse(stdoutBuf.ToString());
    }

    internal static FfprobeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double duration = 0;
        if (root.TryGetProperty("format", out var fmt) &&
            fmt.TryGetProperty("duration", out var dur) &&
            dur.ValueKind == JsonValueKind.String &&
            double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            duration = d;
        }

        string? videoCodec = null, audioCodec = null;
        int? sampleRate = null, channels = null, width = null, height = null;

        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in streams.EnumerateArray())
            {
                if (!s.TryGetProperty("codec_type", out var typeEl)) continue;
                var type = typeEl.GetString();
                var codec = s.TryGetProperty("codec_name", out var ce) ? ce.GetString() : null;
                if (string.Equals(type, "video", StringComparison.Ordinal) && videoCodec is null)
                {
                    videoCodec = codec;
                    if (s.TryGetProperty("width", out var wEl) && wEl.TryGetInt32(out var w))  width  = w;
                    if (s.TryGetProperty("height", out var hEl) && hEl.TryGetInt32(out var h)) height = h;
                }
                else if (string.Equals(type, "audio", StringComparison.Ordinal) && audioCodec is null)
                {
                    audioCodec = codec;
                    if (s.TryGetProperty("sample_rate", out var srEl) &&
                        srEl.ValueKind == JsonValueKind.String &&
                        int.TryParse(srEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sr))
                    {
                        sampleRate = sr;
                    }
                    if (s.TryGetProperty("channels", out var chEl) && chEl.TryGetInt32(out var ch))
                    {
                        channels = ch;
                    }
                }
            }
        }

        return new FfprobeResult(duration, videoCodec, audioCodec, sampleRate, channels, width, height);
    }

    private static string QuoteArg(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
}
