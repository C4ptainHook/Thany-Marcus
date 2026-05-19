namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public sealed class AudioFilterOptions
{
    public long MaxSizeBytes { get; init; } = 200L * 1024 * 1024;
    public int MaxDurationSeconds { get; init; } = 600;
    // RMS amplitude in [0,1]. Whispered captures sometimes fall below this; tune via config.
    public float SilenceRmsThreshold { get; init; } = 0.005f;
    public int SilenceWindowMs { get; init; } = 100;
}
