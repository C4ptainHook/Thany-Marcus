using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Llm;

public sealed class LlmEventAppender
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly CloudDbContext db;
    private readonly IClock clock;

    public LlmEventAppender(CloudDbContext db, IClock clock)
    {
        this.db = db;
        this.clock = clock;
    }

    public async Task AppendAsync(Guid jobId, LlmEvent ev, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ev);
        var now = clock.GetCurrentInstant();
        var payload = JsonSerializer.Serialize(new
        {
            at = now.ToString(),
            stage = ev.Stage,
            prompt_id = ev.PromptId,
            model = ev.Model,
            model_version = ev.ModelVersion,
            llm_mode = ev.LlmMode,
            llm_mode_fallback = ev.LlmModeFallback,
            duration_ms = ev.DurationMs,
            retry_index = ev.RetryIndex,
            decision = ev.Decision,
            confidence = ev.Confidence,
            rationale = ev.Rationale,
            error = ev.Error,
        }, JsonOpts);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                events_log = events_log || {payload}::jsonb,
                updated_at = {now}
              WHERE id = {jobId}
            """, ct);
    }

    public async Task AppendEmbeddingEmitAsync(
        Guid jobId, string model, string modelVersion, long durationMs, int dim, string bodyHash, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var payload = JsonSerializer.Serialize(new
        {
            at = now.ToString(),
            stage = EmbeddingEventStages.Emit,
            model,
            model_version = modelVersion,
            duration_ms = durationMs,
            dim,
            body_hash = bodyHash,
        }, JsonOpts);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                events_log = events_log || {payload}::jsonb,
                updated_at = {now}
              WHERE id = {jobId}
            """, ct);
    }

    public async Task AppendEmbeddingSkipAsync(
        Guid jobId, string bodyHash, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var payload = JsonSerializer.Serialize(new
        {
            at = now.ToString(),
            stage = EmbeddingEventStages.Skip,
            reason = "unchanged_body",
            body_hash = bodyHash,
        }, JsonOpts);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                events_log = events_log || {payload}::jsonb,
                updated_at = {now}
              WHERE id = {jobId}
            """, ct);
    }
}

public sealed record LlmEvent(
    string Stage,
    string PromptId,
    string Model,
    string ModelVersion,
    string LlmMode,
    bool LlmModeFallback,
    long DurationMs,
    int RetryIndex,
    string? Decision,
    double? Confidence,
    string? Rationale,
    string? Error);

public static class LlmEventStages
{
    public const string Route = "llm_route";
    public const string Extract = "llm_extract";
    public const string Dedup = "llm_dedup";
    public const string HubGenerate = "llm_hub_generate";
    public const string Synthesis = "llm_synthesis";
    public const string SynthesisCacheHit = "llm_synthesis_cache_hit";
}

public static class EmbeddingEventStages
{
    public const string Emit = "embedding_emit";
    public const string Skip = "embedding_skip";
}
