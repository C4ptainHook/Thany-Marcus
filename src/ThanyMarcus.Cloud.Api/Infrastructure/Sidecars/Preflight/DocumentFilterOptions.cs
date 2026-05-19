namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

public sealed class DocumentFilterOptions
{
    public long MaxSizeBytes { get; init; } = 50L * 1024 * 1024;
    public int MaxPageCount { get; init; } = 200;
}
