using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class VlmWorker : SpecialistWorkerBase<IVlmClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Ollama;

    public VlmWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<VlmWorker> log)
        : base(services, config, env, clock, log) { }

    protected override Task<string> ExtractAsync(IVlmClient client, Attachment att, CancellationToken ct) =>
        client.DescribeImageAsync(att.StorageKey, ct);
}
