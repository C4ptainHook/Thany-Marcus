using System.Text.Json;
using ThanyMarcus.Cloud.Api.Features.Processing;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public sealed class IngestSseTranslator
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public IngestSseEvent? Translate(string notifyPayload)
    {
        if (string.IsNullOrWhiteSpace(notifyPayload))
        {
            return null;
        }

        JsonDocument doc;
        try { doc = JsonDocument.Parse(notifyPayload); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("kind", out var kindProp)) return null;
            var kind = kindProp.GetString();

            return kind switch
            {
                IngestEventKinds.NotePhaseChanged => ParseNotePhaseChanged(root),
                IngestEventKinds.AttachmentStatusChanged => ParseAttachmentStatusChanged(root),
                IngestEventKinds.NoteSucceeded => ParseNoteSucceeded(root),
                IngestEventKinds.NoteFailed => ParseNoteFailed(root),
                IngestEventKinds.NoteCancelled => ParseNoteCancelled(root),
                IngestEventKinds.HubMaterialized => ParseHubMaterialized(root),
                _ => null,
            };
        }
    }

    public string Serialize(IngestSseEvent evt) => evt switch
    {
        IngestSseEvent.NotePhaseChanged n =>
            JsonSerializer.Serialize(new { noteId = n.NoteId, jobId = n.JobId, from = n.From, to = n.To }, JsonOpts),
        IngestSseEvent.AttachmentStatusChanged a =>
            JsonSerializer.Serialize(new { noteId = a.NoteId, attachmentId = a.AttachmentId, from = a.From, to = a.To }, JsonOpts),
        IngestSseEvent.NoteSucceeded s =>
            JsonSerializer.Serialize(new { noteId = s.NoteId }, JsonOpts),
        IngestSseEvent.NoteFailed f =>
            JsonSerializer.Serialize(new { noteId = f.NoteId, error = f.Error }, JsonOpts),
        IngestSseEvent.NoteCancelled c =>
            JsonSerializer.Serialize(new { noteId = c.NoteId }, JsonOpts),
        IngestSseEvent.HubMaterialized h =>
            JsonSerializer.Serialize(new { noteId = h.NoteId, entityId = h.EntityId }, JsonOpts),
        _ => "{}",
    };

    private static IngestSseEvent.NotePhaseChanged? ParseNotePhaseChanged(JsonElement root)
    {
        if (!TryGuid(root, "noteId", out var noteId)) return null;
        if (!TryGuid(root, "jobId", out var jobId)) return null;
        var from = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
        var to = root.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
        return new IngestSseEvent.NotePhaseChanged(noteId, jobId, from, to);
    }

    private static IngestSseEvent.AttachmentStatusChanged? ParseAttachmentStatusChanged(JsonElement root)
    {
        if (!TryGuid(root, "noteId", out var noteId)) return null;
        if (!TryGuid(root, "attachmentId", out var attId)) return null;
        var from = root.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
        var to = root.TryGetProperty("to", out var t) ? t.GetString() ?? "" : "";
        return new IngestSseEvent.AttachmentStatusChanged(noteId, attId, from, to);
    }

    private static IngestSseEvent.NoteSucceeded? ParseNoteSucceeded(JsonElement root)
    {
        if (!TryGuid(root, "noteId", out var noteId)) return null;
        return new IngestSseEvent.NoteSucceeded(noteId);
    }

    private static IngestSseEvent.NoteFailed? ParseNoteFailed(JsonElement root)
    {
        if (!TryGuid(root, "noteId", out var noteId)) return null;
        var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
        return new IngestSseEvent.NoteFailed(noteId, error);
    }

    private static IngestSseEvent.NoteCancelled? ParseNoteCancelled(JsonElement root)
    {
        if (!TryGuid(root, "noteId", out var noteId)) return null;
        return new IngestSseEvent.NoteCancelled(noteId);
    }

    private static IngestSseEvent.HubMaterialized? ParseHubMaterialized(JsonElement root)
    {
        if (!TryGuid(root, "noteId", out var noteId)) return null;
        if (!TryGuid(root, "entityId", out var entId)) return null;
        return new IngestSseEvent.HubMaterialized(noteId, entId);
    }

    private static bool TryGuid(JsonElement root, string name, out Guid value)
    {
        value = Guid.Empty;
        return root.TryGetProperty(name, out var p)
               && p.ValueKind == JsonValueKind.String
               && Guid.TryParse(p.GetString(), out value);
    }
}
