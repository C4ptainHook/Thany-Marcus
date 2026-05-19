namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubDoclingClient : IDoclingClient
{
    // FORK: stub; real impl lands in handoffs #4-#6
    public Task<string> ExtractMarkdownAsync(string storageKey, string mimeType, CancellationToken ct) =>
        Task.FromResult($"# [stub docling extraction]\n\nFile: {storageKey}\n");
}
