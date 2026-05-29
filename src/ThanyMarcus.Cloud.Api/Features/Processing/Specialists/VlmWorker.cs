using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class VlmWorker : SpecialistWorkerBase<IVlmClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Ollama;

    private readonly IConfiguration config;

    public VlmWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<VlmWorker> log)
        : base(services, config, env, clock, log)
    {
        this.config = config;
    }

    protected override async Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices, IVlmClient client, ExtractionTask task, Attachment att, CancellationToken ct)
    {
        var modelTag = config["IngestSaga:Models:Vlm:OllamaTag"] ?? "qwen3-vl:4b";
        var cacheKey = ExtractionCacheKeys.ForOllama(modelTag);

        var cache = scopeServices.GetRequiredService<AttachmentExtractionCache>();
        var hit = await cache.LookupAsync(att.Sha256, cacheKey, ct);
        if (hit is not null)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: hit.ExtractedText,
                ExtractionCacheKey: cacheKey,
                Extra: BuildFromCacheExtra(hit.Extra),
                AttachmentStatus: AttachmentExtractionStatus.Extracted,
                ExtractionError: null);
        }

        var outcome = await client.ExtractAsync(att, ct);
        if (outcome.Skipped)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: outcome.Extra,
                AttachmentStatus: AttachmentExtractionStatus.Skipped,
                ExtractionError: outcome.SkipReason);
        }

        return new SpecialistExtractionOutcome(
            ExtractedText: outcome.ExtractedText,
            ExtractionCacheKey: outcome.ExtractionCacheKey,
            Extra: outcome.Extra,
            AttachmentStatus: AttachmentExtractionStatus.Extracted,
            ExtractionError: null);
    }

    private static JsonDocument BuildFromCacheExtra(JsonDocument cached)
    {
        using var _ = cached;
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            if (cached.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in cached.RootElement.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteBoolean("from_cache", true);
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }
}
