namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

public interface IVideoSplitterClient
{
    Task<VideoSplitResult> SplitAsync(string storageKey, CancellationToken ct);
}

public sealed record VideoSplitResult(
    IReadOnlyList<string> KeyframeStorageKeys,
    string? AudioStorageKey);
