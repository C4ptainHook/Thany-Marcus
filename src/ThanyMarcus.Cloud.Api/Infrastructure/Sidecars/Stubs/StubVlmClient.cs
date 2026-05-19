using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Ingest;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Stubs;

public sealed class StubVlmClient : IVlmClient
{
    public async Task<VlmExtractionOutcome> ExtractAsync(Attachment att, CancellationToken ct)
    {
        await Task.Delay(50, ct);
        var text = $"[stub VLM description for {att.StorageKey}; model=stub-v1]";
        var extra = JsonDocument.Parse("{\"model_response\":{\"model\":\"stub-v1\"}}");
        var cacheKey = ExtractionCacheKeys.ForOllama("stub-v1");
        return new VlmExtractionOutcome(text, cacheKey, extra, Skipped: false, SkipReason: null);
    }
}
