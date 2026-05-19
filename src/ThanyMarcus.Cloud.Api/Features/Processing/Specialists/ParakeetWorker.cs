using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class ParakeetWorker : SpecialistWorkerBase<IParakeetClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Parakeet;

    public ParakeetWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<ParakeetWorker> log)
        : base(services, config, env, clock, log) { }

    protected override Task<string> ExtractAsync(IParakeetClient client, Attachment att, CancellationToken ct) =>
        client.TranscribeAsync(att.StorageKey, ct);
}
