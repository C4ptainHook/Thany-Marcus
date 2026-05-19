using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class VideoSplitterWorker : SpecialistWorkerBase<IVideoSplitterClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Video;

    public VideoSplitterWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<VideoSplitterWorker> log)
        : base(services, config, env, clock, log) { }

    // FORK: video children deferred to handoff #6
    protected override async Task<string> ExtractAsync(IVideoSplitterClient client, Attachment att, CancellationToken ct)
    {
        _ = await client.SplitAsync(att.StorageKey, ct);
        return string.Empty;
    }
}
