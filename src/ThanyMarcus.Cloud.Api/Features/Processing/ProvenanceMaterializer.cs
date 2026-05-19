using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class ProvenanceMaterializer
{
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
            CacheHits:           cacheHits);

        return JsonSerializer.Serialize(doc);
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
        [property: System.Text.Json.Serialization.JsonPropertyName("cache_hits")]          int CacheHits);

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
