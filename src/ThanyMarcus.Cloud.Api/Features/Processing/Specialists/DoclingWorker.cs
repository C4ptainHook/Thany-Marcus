using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class DoclingWorker : SpecialistWorkerBase<IDoclingClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Docling;

    public DoclingWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<DoclingWorker> log)
        : base(services, config, env, clock, log) { }

    protected override Task<string> ExtractAsync(IDoclingClient client, Attachment att, CancellationToken ct) =>
        client.ExtractMarkdownAsync(att.StorageKey, att.MimeType ?? "application/octet-stream", ct);
}
