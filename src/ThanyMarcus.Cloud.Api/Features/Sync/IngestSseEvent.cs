namespace ThanyMarcus.Cloud.Api.Features.Sync;

public abstract record IngestSseEvent(string Kind)
{
    public sealed record NotePhaseChanged(Guid NoteId, Guid JobId, string From, string To)
        : IngestSseEvent("note_phase_changed");

    public sealed record AttachmentStatusChanged(Guid NoteId, Guid AttachmentId, string From, string To)
        : IngestSseEvent("attachment_status_changed");

    public sealed record NoteSucceeded(Guid NoteId)
        : IngestSseEvent("note_succeeded");

    public sealed record NoteFailed(Guid NoteId, string Error)
        : IngestSseEvent("note_failed");

    public sealed record NoteCancelled(Guid NoteId)
        : IngestSseEvent("note_cancelled");

    public sealed record HubMaterialized(Guid NoteId, Guid EntityId)
        : IngestSseEvent("hub_materialized");
}
