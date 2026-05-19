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

    protected override Task<string> ExtractAsync(IUrlFetcherClient client, Attachment att, CancellationToken ct)
    {
        var url = att.Url ?? TryExtractUrlFromExtra(att) ?? att.StorageKey;
        return client.FetchMarkdownAsync(url, ct);
    }

    private static string? TryExtractUrlFromExtra(Attachment att)
    {
        try
        {
            var root = att.Extra.RootElement;
            if (root.ValueKind == System.Text.Json.JsonValueKind.Object
                && root.TryGetProperty("url", out var u)
                && u.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return u.GetString();
            }
        }
        catch (ObjectDisposedException) { }
        return null;
    }
}
