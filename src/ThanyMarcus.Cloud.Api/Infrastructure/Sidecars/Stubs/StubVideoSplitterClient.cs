namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubVideoSplitterClient : IVideoSplitterClient
{
    // FORK: stub; real impl lands in handoffs #4-#6
    public Task<VideoSplitResult> SplitAsync(string storageKey, CancellationToken ct) =>
        Task.FromResult(new VideoSplitResult(Array.Empty<string>(), null));
}
