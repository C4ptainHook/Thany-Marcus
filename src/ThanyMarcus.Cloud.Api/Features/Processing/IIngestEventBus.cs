namespace ThanyMarcus.Cloud.Api.Features.Processing;

public interface IIngestEventBus
{
    Task PublishNotePhaseChangedAsync(Guid noteId, Guid jobId, string fromPhase, string toPhase, CancellationToken ct);
    Task PublishAttachmentStatusChangedAsync(Guid noteId, Guid attachmentId, string fromStatus, string toStatus, CancellationToken ct);
    Task PublishNoteSucceededAsync(Guid noteId, CancellationToken ct);
    Task PublishNoteFailedAsync(Guid noteId, string errorMessage, CancellationToken ct);
    Task PublishNoteCancelledAsync(Guid noteId, CancellationToken ct);
    Task PublishHubMaterializedAsync(Guid noteId, Guid entityId, CancellationToken ct);
}

public static class IngestEventKinds
{
    public const string NotePhaseChanged = "note_phase_changed";
    public const string NoteSucceeded = "note_succeeded";
    public const string NoteFailed = "note_failed";
    public const string NoteCancelled = "note_cancelled";
    public const string AttachmentStatusChanged = "attachment_status_changed";
    public const string HubMaterialized = "hub_materialized";

    public const string Channel = "ingest_events";
}

public sealed class NoOpIngestEventBus : IIngestEventBus
{
    public Task PublishNotePhaseChangedAsync(Guid noteId, Guid jobId, string fromPhase, string toPhase, CancellationToken ct) =>
        Task.CompletedTask;
    public Task PublishAttachmentStatusChangedAsync(Guid noteId, Guid attachmentId, string fromStatus, string toStatus, CancellationToken ct) =>
        Task.CompletedTask;
    public Task PublishNoteSucceededAsync(Guid noteId, CancellationToken ct) =>
        Task.CompletedTask;
    public Task PublishNoteFailedAsync(Guid noteId, string errorMessage, CancellationToken ct) =>
        Task.CompletedTask;
    public Task PublishNoteCancelledAsync(Guid noteId, CancellationToken ct) =>
        Task.CompletedTask;
    public Task PublishHubMaterializedAsync(Guid noteId, Guid entityId, CancellationToken ct) =>
        Task.CompletedTask;
}
