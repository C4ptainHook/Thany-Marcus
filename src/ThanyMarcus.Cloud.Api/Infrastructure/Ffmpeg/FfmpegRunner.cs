using System.Diagnostics;
using System.Text;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;

public sealed partial class FfmpegRunner : IFfmpegRunner
{
    private readonly ILogger<FfmpegRunner> log;
    private readonly string executable;

    public FfmpegRunner(ILogger<FfmpegRunner> log, IConfiguration config)
    {
        this.log = log;
        executable = config["IngestSaga:Ffmpeg:Executable"] ?? "ffmpeg";
    }

    public async Task<FfmpegResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(arguments);

        var psi = new ProcessStartInfo
        {
            FileName               = executable,
            Arguments              = arguments,
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
            throw new FfmpegRunnerException("failed to start ffmpeg", -1, "");
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
                $"ffmpeg timeout after {timeout}", -1, stderrBuf.ToString());
        }

        var stdout = stdoutBuf.ToString();
        var stderr = stderrBuf.ToString();
        LogExit(log, executable, proc.ExitCode, arguments);

        if (proc.ExitCode != 0)
        {
            throw new FfmpegRunnerException(
                $"{executable} exited {proc.ExitCode}: {Truncate(stderr)}",
                proc.ExitCode, stderr);
        }
        return new FfmpegResult(proc.ExitCode, stdout, stderr);
    }

    private static string Truncate(string s) =>
        s.Length <= 512 ? s : s[..512] + "...";

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug,
        Message = "ffmpeg exit: exe={Exe} code={Code} args={Args}")]
    private static partial void LogExit(ILogger logger, string exe, int code, string args);
}
