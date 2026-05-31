using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class UrlFetcherWorker : SpecialistWorkerBase<IUrlFetcherClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Url;

    public UrlFetcherWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<UrlFetcherWorker> log)
        : base(services, config, env, clock, log) { }

    protected override async Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices, IUrlFetcherClient client, ExtractionTask task, Attachment att, CancellationToken ct)
    {
        var outcome = await client.FetchAsync(att.NoteId, att, ct);

        if (outcome.RedirectedToAttachmentId is not null)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: outcome.Extra,
                AttachmentStatus: AttachmentExtractionStatus.Skipped,
                ExtractionError: "redirected_to_binary");
        }

        return new SpecialistExtractionOutcome(
            ExtractedText: outcome.ExtractedText,
            ExtractionCacheKey: null,
            Extra: outcome.Extra,
            AttachmentStatus: outcome.IsMinimal
                ? AttachmentExtractionStatus.ExtractedMinimal
                : AttachmentExtractionStatus.Extracted,
            ExtractionError: null);
    }
}
