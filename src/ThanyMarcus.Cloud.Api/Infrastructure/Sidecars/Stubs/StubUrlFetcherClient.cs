using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubUrlFetcherClient : IUrlFetcherClient
{
    public Task<UrlFetchOutcome> FetchAsync(Guid noteId, Attachment att, CancellationToken ct)
    {
        var url = att.Url ?? att.StorageKey;
        var md = $"# [stub URL extraction]\n\nURL: {url}\n";
        var extra = JsonDocument.Parse($"{{\"final_url\":{JsonSerializer.Serialize(url)}}}");
        return Task.FromResult(new UrlFetchOutcome(md, extra, RedirectedToAttachmentId: null));
    }
}
