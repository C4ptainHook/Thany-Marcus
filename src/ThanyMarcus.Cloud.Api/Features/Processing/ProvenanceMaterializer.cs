using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed partial class ProvenanceMaterializer
{
    [GeneratedRegex(@"(?m)^compose_template:\s*(?<v>\S+)\s*$")]
    private static partial Regex ComposeTemplateLine();
    private readonly CloudDbContext db;

    public ProvenanceMaterializer(CloudDbContext db)
    {
        this.db = db;
    }

    public async Task MaterializeAndPersistAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var note = await db.Notes
            .AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == job.NoteId, ct);
        if (note is null) return;

        var attachments = await db.Attachments
            .AsNoTracking()
            .Where(a => a.NoteId == job.NoteId)
            .ToListAsync(ct);

        var tasks = await db.ExtractionTasks
            .AsNoTracking()
            .Where(t => t.IngestJobId == job.Id)
            .ToListAsync(ct);

        var persistedJob = await db.IngestJobs
            .AsNoTracking()
            .SingleOrDefaultAsync(j => j.Id == job.Id, ct) ?? job;
        // LastComposeTemplate is [NotMapped] and dispatcher loads each phase in a fresh scope, so
        // we cannot rely on the in-memory job alone — recover it from the frontmatter we just wrote.
        persistedJob.LastComposeTemplate = job.LastComposeTemplate
            ?? ExtractComposeTemplateFromBody(note.BodyOutput);

        var docJson = BuildJson(persistedJob, note, attachments, tasks);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                provenance = {docJson}::jsonb
              WHERE id = {note.Id}
            """, ct);
    }

    public static string BuildJson(
        IngestJob job,
        Note note,
        IReadOnlyList<Attachment> attachments,
        IReadOnlyList<ExtractionTask> tasks)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(note);
        ArgumentNullException.ThrowIfNull(attachments);
        ArgumentNullException.ThrowIfNull(tasks);

        var phaseEvents = ParseEventsLog(job.EventsLog);
        var failures = attachments
            .Where(a => a.ExtractionStatus == AttachmentExtractionStatus.Failed)
            .Select(a => new ExtractionFailure(a.Id, a.Kind, a.ExtractionError))
            .ToList();
        var summary = attachments
            .GroupBy(a => a.Kind, StringComparer.Ordinal)
            .Select(g => new ExtractionSummaryEntry(
                Kind:      g.Key,
                Total:     g.Count(),
                Extracted: g.Count(a => a.ExtractionStatus == AttachmentExtractionStatus.Extracted),
                Skipped:   g.Count(a => a.ExtractionStatus == AttachmentExtractionStatus.Skipped),
                Failed:    g.Count(a => a.ExtractionStatus == AttachmentExtractionStatus.Failed)))
            .ToList();
        var cacheHits = tasks.Count(t => t.Status == ExtractionTaskStatus.Skipped);

        double? totalMs = null;
        if (job.StartedAt is { } started && job.FinishedAt is { } finished)
        {
            totalMs = (finished - started).TotalMilliseconds;
        }

        var llmCalls = ExtractLlmCalls(job.EventsLog);

        var doc = new ProvenanceDocument(
            JobId:               job.Id,
            Kind:                job.Kind,
            LlmMode:             note.LlmMode,
            LlmModel:            ResolveModelName(note),
            GeneratedAt:         (job.FinishedAt ?? job.UpdatedAt).ToString(),
            TotalMs:             totalMs,
            PhaseEvents:         phaseEvents,
            ExtractionFailures:  failures,
            ExtractionSummary:   summary,
            CacheHits:           cacheHits,
            ComposeTemplate:     job.LastComposeTemplate,
            LlmCalls:            llmCalls);

        return JsonSerializer.Serialize(doc);
    }

    private static IReadOnlyList<LlmCallRollup> ExtractLlmCalls(JsonDocument eventsLog)
    {
        if (eventsLog.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<LlmCallRollup>();
        }
        var list = new List<LlmCallRollup>();
        foreach (var el in eventsLog.RootElement.EnumerateArray())
        {
            if (!el.TryGetProperty("stage", out var stage) || stage.ValueKind != JsonValueKind.String) continue;
            var stageStr = stage.GetString();
            if (stageStr is null) continue;
            if (!stageStr.StartsWith("llm_", StringComparison.Ordinal) &&
                !stageStr.StartsWith("embedding_", StringComparison.Ordinal) &&
                !stageStr.StartsWith("user_edit_", StringComparison.Ordinal)) continue;
            list.Add(new LlmCallRollup(
                Stage:       stageStr,
                PromptId:    el.TryGetProperty("prompt_id", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null,
                RetryIndex:  el.TryGetProperty("retry_index", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : 0,
                Confidence:  el.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null,
                LlmMode:     el.TryGetProperty("llm_mode", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null,
                Decision:    el.TryGetProperty("decision", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null));
        }
        return list;
    }

    private static IReadOnlyList<JsonElement> ParseEventsLog(JsonDocument eventsLog)
    {
        if (eventsLog.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<JsonElement>();
        }
        var list = new List<JsonElement>(eventsLog.RootElement.GetArrayLength());
        foreach (var el in eventsLog.RootElement.EnumerateArray())
        {
            list.Add(el.Clone());
        }
        return list;
    }

    private static string? ExtractComposeTemplateFromBody(string? body)
    {
        if (string.IsNullOrEmpty(body)) return null;
        var m = ComposeTemplateLine().Match(body);
        return m.Success ? m.Groups["v"].Value : null;
    }

    private static string ResolveModelName(Note note) =>
        string.IsNullOrWhiteSpace(note.LlmMode) || note.LlmMode == Features.Settings.LlmModes.Safe
            ? "noop"
            : (note.LlmMode ?? "noop");

    private sealed record ProvenanceDocument(
        [property: System.Text.Json.Serialization.JsonPropertyName("job_id")]              Guid JobId,
        [property: System.Text.Json.Serialization.JsonPropertyName("kind")]                string Kind,
        [property: System.Text.Json.Serialization.JsonPropertyName("llm_mode")]            string? LlmMode,
        [property: System.Text.Json.Serialization.JsonPropertyName("llm_model")]           string LlmModel,
        [property: System.Text.Json.Serialization.JsonPropertyName("generated_at")]        string GeneratedAt,
        [property: System.Text.Json.Serialization.JsonPropertyName("total_ms")]            double? TotalMs,
        [property: System.Text.Json.Serialization.JsonPropertyName("phase_events")]        IReadOnlyList<JsonElement> PhaseEvents,
        [property: System.Text.Json.Serialization.JsonPropertyName("extraction_failures")] IReadOnlyList<ExtractionFailure> ExtractionFailures,
        [property: System.Text.Json.Serialization.JsonPropertyName("extraction_summary")]  IReadOnlyList<ExtractionSummaryEntry> ExtractionSummary,
        [property: System.Text.Json.Serialization.JsonPropertyName("cache_hits")]          int CacheHits,
        [property: System.Text.Json.Serialization.JsonPropertyName("compose_template")]    string? ComposeTemplate,
        [property: System.Text.Json.Serialization.JsonPropertyName("llm_calls")]           IReadOnlyList<LlmCallRollup> LlmCalls);

    private sealed record LlmCallRollup(
        [property: System.Text.Json.Serialization.JsonPropertyName("stage")]        string Stage,
        [property: System.Text.Json.Serialization.JsonPropertyName("prompt_id")]    string? PromptId,
        [property: System.Text.Json.Serialization.JsonPropertyName("retry_index")]  int RetryIndex,
        [property: System.Text.Json.Serialization.JsonPropertyName("confidence")]   double? Confidence,
        [property: System.Text.Json.Serialization.JsonPropertyName("llm_mode")]     string? LlmMode,
        [property: System.Text.Json.Serialization.JsonPropertyName("decision")]     string? Decision);

    private sealed record ExtractionFailure(
        [property: System.Text.Json.Serialization.JsonPropertyName("attachmentId")] Guid AttachmentId,
        [property: System.Text.Json.Serialization.JsonPropertyName("kind")]         string Kind,
        [property: System.Text.Json.Serialization.JsonPropertyName("error")]        string? Error);

    private sealed record ExtractionSummaryEntry(
        [property: System.Text.Json.Serialization.JsonPropertyName("kind")]      string Kind,
        [property: System.Text.Json.Serialization.JsonPropertyName("total")]     int Total,
        [property: System.Text.Json.Serialization.JsonPropertyName("extracted")] int Extracted,
        [property: System.Text.Json.Serialization.JsonPropertyName("skipped")]   int Skipped,
        [property: System.Text.Json.Serialization.JsonPropertyName("failed")]    int Failed);
}
