namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubUrlFetcherClient : IUrlFetcherClient
{
    // FORK: stub; real impl lands in handoffs #4-#6
    public Task<string> FetchMarkdownAsync(string url, CancellationToken ct) =>
        Task.FromResult($"# [stub URL extraction]\n\nURL: {url}\n");
}
