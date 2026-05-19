using System.Text.Json;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Sync;

namespace ThanyMarcus.Cloud.Tests.Features.Sync;

public sealed class IngestSseTranslatorTests
{
    private static readonly IngestSseTranslator Translator = new();

    [Fact]
    public void NotePhaseChanged_roundtrips()
    {
        var noteId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            kind = "note_phase_changed",
            noteId,
            jobId,
            from = "queued",
            to = "extracting_attachments",
        });
        var evt = Translator.Translate(payload);
        evt.ShouldBeOfType<IngestSseEvent.NotePhaseChanged>();
        var typed = (IngestSseEvent.NotePhaseChanged)evt!;
        typed.NoteId.ShouldBe(noteId);
        typed.JobId.ShouldBe(jobId);
        typed.From.ShouldBe("queued");
        typed.To.ShouldBe("extracting_attachments");
        Translator.Serialize(typed).ShouldContain(noteId.ToString());
    }

    [Fact]
    public void AttachmentStatusChanged_roundtrips()
    {
        var noteId = Guid.NewGuid();
        var attId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new
        {
            kind = "attachment_status_changed",
            noteId,
            attachmentId = attId,
            from = "pending",
            to = "extracted",
        });
        var evt = Translator.Translate(payload);
        evt.ShouldBeOfType<IngestSseEvent.AttachmentStatusChanged>();
        var typed = (IngestSseEvent.AttachmentStatusChanged)evt!;
        typed.NoteId.ShouldBe(noteId);
        typed.AttachmentId.ShouldBe(attId);
    }

    [Fact]
    public void NoteSucceeded_roundtrips()
    {
        var noteId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { kind = "note_succeeded", noteId });
        var evt = Translator.Translate(payload);
        evt.ShouldBeOfType<IngestSseEvent.NoteSucceeded>();
        ((IngestSseEvent.NoteSucceeded)evt!).NoteId.ShouldBe(noteId);
    }

    [Fact]
    public void NoteFailed_roundtrips()
    {
        var noteId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { kind = "note_failed", noteId, error = "boom" });
        var evt = Translator.Translate(payload);
        var typed = evt as IngestSseEvent.NoteFailed;
        typed.ShouldNotBeNull();
        typed.NoteId.ShouldBe(noteId);
        typed.Error.ShouldBe("boom");
    }

    [Fact]
    public void HubMaterialized_roundtrips()
    {
        var noteId = Guid.NewGuid();
        var entId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { kind = "hub_materialized", noteId, entityId = entId });
        var evt = Translator.Translate(payload);
        var typed = evt as IngestSseEvent.HubMaterialized;
        typed.ShouldNotBeNull();
        typed.NoteId.ShouldBe(noteId);
        typed.EntityId.ShouldBe(entId);
    }

    [Fact]
    public void Unknown_kind_returns_null()
    {
        var payload = JsonSerializer.Serialize(new { kind = "unknown" });
        Translator.Translate(payload).ShouldBeNull();
    }

    [Fact]
    public void Garbage_payload_returns_null()
    {
        Translator.Translate("not json").ShouldBeNull();
        Translator.Translate("").ShouldBeNull();
    }
}
