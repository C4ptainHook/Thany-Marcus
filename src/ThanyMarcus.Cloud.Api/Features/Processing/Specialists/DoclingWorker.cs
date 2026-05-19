using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class DoclingWorker : SpecialistWorkerBase<IDoclingClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Docling;

    private readonly IConfiguration config;

    public DoclingWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<DoclingWorker> log)
        : base(services, config, env, clock, log)
    {
        this.config = config;
    }

    protected override async Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices, IDoclingClient client, ExtractionTask task, Attachment att, CancellationToken ct)
    {
        var version = config["IngestSaga:Models:Docs:DoclingVersion"] ?? "granite-docling-258m";
        var cacheKey = $"sha256:docling:{version}";

        var cache = scopeServices.GetRequiredService<AttachmentExtractionCache>();
        var hit = await cache.LookupAsync(att.Sha256, cacheKey, ct);
        if (hit is not null)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: hit.ExtractedText,
                ExtractionCacheKey: cacheKey,
                Extra: hit.Extra,
                AttachmentStatus: AttachmentExtractionStatus.Extracted,
                ExtractionError: null);
        }

        var preflighter = scopeServices.GetRequiredService<IDocumentPreflighter>();
        var pre = await preflighter.CheckAsync(att, ct);
        if (!pre.ShouldExtract)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: pre.Metadata,
                AttachmentStatus: AttachmentExtractionStatus.Skipped,
                ExtractionError: pre.SkipReason);
        }

        var markdown = await client.ExtractMarkdownAsync(
            att.StorageKey, att.MimeType ?? "application/octet-stream", ct);

        return new SpecialistExtractionOutcome(
            ExtractedText: markdown,
            ExtractionCacheKey: cacheKey,
            Extra: pre.Metadata,
            AttachmentStatus: AttachmentExtractionStatus.Extracted,
            ExtractionError: null);
    }
}
