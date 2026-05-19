namespace ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;

public interface IFfmpegRunner
{
    Task<FfmpegResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken ct);
}

public sealed record FfmpegResult(int ExitCode, string Stdout, string Stderr);

public sealed class FfmpegRunnerException : Exception
{
    public int ExitCode { get; }
    public string Stderr { get; }

    public FfmpegRunnerException(string message, int exitCode, string stderr)
        : base(message)
    {
        ExitCode = exitCode;
        Stderr = stderr;
    }
}
