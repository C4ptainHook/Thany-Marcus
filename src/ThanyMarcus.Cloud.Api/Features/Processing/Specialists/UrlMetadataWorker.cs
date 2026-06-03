using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Extraction;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class UrlMetadataWorker : SpecialistWorkerBase<IUrlExtractor>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.UrlMetadata;

    public UrlMetadataWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<UrlMetadataWorker> log)
        : base(services, config, env, clock, log) { }

    protected override async Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices, IUrlExtractor extractor, ExtractionTask task, Attachment att, CancellationToken ct)
    {
        var url = att.Url ?? throw new UrlFetcherException("missing_url",
            $"Attachment {att.Id} has no url column populated");

        var result = await extractor.ExtractAsync(url, ct);
        return MapOutcome(result);
    }

    internal static SpecialistExtractionOutcome MapOutcome(UrlExtractionResult result)
    {
        var extra = UrlExtractionExtra.Build(result);
        var hasContent = !string.IsNullOrWhiteSpace(result.Title)
                         || !string.IsNullOrWhiteSpace(result.Description);

        if (hasContent)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: extra,
                AttachmentStatus: AttachmentExtractionStatus.Extracted,
                ExtractionError: null);
        }

        if (IsFetchFailure(result.MinimalReason))
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: extra,
                AttachmentStatus: AttachmentExtractionStatus.Failed,
                ExtractionError: result.MinimalReason);
        }

        return new SpecialistExtractionOutcome(
            ExtractedText: null,
            ExtractionCacheKey: null,
            Extra: extra,
            AttachmentStatus: AttachmentExtractionStatus.ExtractedMinimal,
            ExtractionError: result.MinimalReason);
    }

    private static bool IsFetchFailure(string? minimalReason) =>
        minimalReason is not null
        && (minimalReason.StartsWith("unreachable", StringComparison.Ordinal)
            || minimalReason.StartsWith("http:", StringComparison.Ordinal));
}
