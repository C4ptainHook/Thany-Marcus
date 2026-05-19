using System.Text.Json;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

public sealed class ProvenanceMaterializerTests
{
    [Fact]
    public void BuildJson_emits_required_top_level_fields()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = Guid.CreateVersion7(),
            Kind = IngestJobKind.Capture,
            Status = IngestJobStatus.Succeeded,
            StartedAt = now.Minus(Duration.FromSeconds(2)),
            FinishedAt = now,
            EventsLog = JsonDocument.Parse(
                "[{\"from\":\"queued\",\"to\":\"extracting_attachments\"},{\"from\":\"extracting_attachments\",\"to\":\"composing\"}]"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var note = new Note
        {
            Id = job.NoteId,
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "x",
            LlmMode = "safe",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var attachments = new[]
        {
            NewAttachment(note.Id, AttachmentKind.Image, AttachmentExtractionStatus.Extracted),
            NewAttachment(note.Id, AttachmentKind.Voice, AttachmentExtractionStatus.Extracted),
            NewAttachment(note.Id, AttachmentKind.Url,   AttachmentExtractionStatus.Skipped),
        };
        var tasks = new[]
        {
            NewTask(job.Id, ExtractionTaskStatus.Skipped),
            NewTask(job.Id, ExtractionTaskStatus.Succeeded),
        };

        var json = ProvenanceMaterializer.BuildJson(job, note, attachments, tasks);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        root.GetProperty("job_id").GetGuid().ShouldBe(job.Id);
        root.GetProperty("kind").GetString().ShouldBe(IngestJobKind.Capture);
        root.GetProperty("llm_mode").GetString().ShouldBe("safe");
        root.GetProperty("llm_model").GetString().ShouldBe("noop");
        root.GetProperty("total_ms").GetDouble().ShouldBeGreaterThan(0);
        root.GetProperty("phase_events").GetArrayLength().ShouldBe(2);
        root.GetProperty("extraction_failures").GetArrayLength().ShouldBe(0);
        root.GetProperty("cache_hits").GetInt32().ShouldBe(1);

        var summary = root.GetProperty("extraction_summary");
        summary.GetArrayLength().ShouldBe(3);
    }

    [Fact]
    public void BuildJson_llm_calls_includes_embedding_events()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var noteId = Guid.CreateVersion7();
        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = noteId,
            Kind = IngestJobKind.Capture,
            Status = IngestJobStatus.Succeeded,
            CreatedAt = now,
            UpdatedAt = now,
            EventsLog = JsonDocument.Parse("""
                [
                  {"stage":"llm_route","prompt_id":"route@v1","retry_index":0,"confidence":0.9,"llm_mode":"safe","decision":"accept"},
                  {"stage":"embedding_emit","model":"ibm-granite/granite-embedding-311m-multilingual-r2","duration_ms":42,"dim":256,"body_hash":"AB12"},
                  {"stage":"embedding_skip","reason":"unchanged_body","body_hash":"AB12"}
                ]
                """),
        };
        var note = new Note
        {
            Id = noteId,
            CapturedAt = now,
            Status = NoteStatus.Ready,
            BodyInput = "x",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var json = ProvenanceMaterializer.BuildJson(job, note, Array.Empty<Attachment>(), Array.Empty<ExtractionTask>());
        using var doc = JsonDocument.Parse(json);
        var calls = doc.RootElement.GetProperty("llm_calls");
        calls.GetArrayLength().ShouldBe(3);
        var stages = calls.EnumerateArray().Select(e => e.GetProperty("stage").GetString()).ToList();
        stages.ShouldContain("llm_route");
        stages.ShouldContain("embedding_emit");
        stages.ShouldContain("embedding_skip");
    }

    [Fact]
    public void BuildJson_collects_failure_entries()
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        var noteId = Guid.CreateVersion7();
        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = noteId,
            Kind = IngestJobKind.Capture,
            Status = IngestJobStatus.FailedExtraction,
            CreatedAt = now,
            UpdatedAt = now,
            EventsLog = JsonDocument.Parse("[]"),
        };
        var note = new Note
        {
            Id = noteId,
            CapturedAt = now,
            Status = NoteStatus.Failed,
            BodyInput = "x",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var att = NewAttachment(noteId, AttachmentKind.Image, AttachmentExtractionStatus.Failed);
        att.ExtractionError = "boom";

        var json = ProvenanceMaterializer.BuildJson(job, note, [att], Array.Empty<ExtractionTask>());
        using var doc = JsonDocument.Parse(json);
        var failures = doc.RootElement.GetProperty("extraction_failures");
        failures.GetArrayLength().ShouldBe(1);
        failures[0].GetProperty("error").GetString().ShouldBe("boom");
    }

    private static Attachment NewAttachment(Guid noteId, string kind, string extractionStatus)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return new Attachment
        {
            Id = Guid.CreateVersion7(),
            NoteId = noteId,
            ClientAttachmentId = "c",
            Kind = kind,
            StorageProvider = "s3",
            StorageBucket = "b",
            StorageKey = "k",
            ExtractionStatus = extractionStatus,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static ExtractionTask NewTask(Guid jobId, string status)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        return new ExtractionTask
        {
            Id = Guid.CreateVersion7(),
            IngestJobId = jobId,
            AttachmentId = Guid.CreateVersion7(),
            TargetSidecar = "ollama",
            Status = status,
            ScheduledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            EventsLog = JsonDocument.Parse("[]"),
        };
    }
}
