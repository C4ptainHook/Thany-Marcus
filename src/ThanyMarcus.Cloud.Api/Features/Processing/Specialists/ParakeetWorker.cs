using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars.Preflight;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed class ParakeetWorker : SpecialistWorkerBase<IParakeetClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Parakeet;

    private readonly IConfiguration config;

    public ParakeetWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        ILogger<ParakeetWorker> log)
        : base(services, config, env, clock, log)
    {
        this.config = config;
    }

    protected override async Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices, IParakeetClient client, ExtractionTask task, Attachment att, CancellationToken ct)
    {
        var version = config["IngestSaga:Models:Asr:ParakeetVersion"] ?? "parakeet-tdt-0.6b-v3-int8";
        var cacheKey = $"sha256:parakeet:{version}";

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

        var preflighter = scopeServices.GetRequiredService<IAudioPreflighter>();
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

        var transcript = await client.TranscribeAsync(att.StorageKey, ct);

        return new SpecialistExtractionOutcome(
            ExtractedText: transcript.Text,
            ExtractionCacheKey: cacheKey,
            Extra: MergeLanguage(pre.Metadata, transcript.LanguageDetected),
            AttachmentStatus: AttachmentExtractionStatus.Extracted,
            ExtractionError: null);
    }

    private static JsonDocument? MergeLanguage(JsonDocument? preMeta, string? language)
    {
        if (preMeta is null && language is null) return null;
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            if (preMeta is not null && preMeta.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in preMeta.RootElement.EnumerateObject())
                {
                    prop.WriteTo(writer);
                }
            }
            if (language is not null) writer.WriteString("language_detected", language);
            writer.WriteEndObject();
        }
        ms.Position = 0;
        preMeta?.Dispose();
        return JsonDocument.Parse(ms);
    }
}
