using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public sealed class PostgresIngestEventBus : IIngestEventBus
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private readonly CloudDbContext db;

    public PostgresIngestEventBus(CloudDbContext db)
    {
        this.db = db;
    }

    // events_log append lives in JobStateTransitions / handlers; this bus only fans out via pg_notify
    public Task PublishNotePhaseChangedAsync(Guid noteId, Guid jobId, string fromPhase, string toPhase, CancellationToken ct) =>
        NotifyAsync(new
        {
            kind = IngestEventKinds.NotePhaseChanged,
            noteId,
            jobId,
            from = fromPhase,
            to = toPhase,
        }, ct);

    public Task PublishAttachmentStatusChangedAsync(Guid noteId, Guid attachmentId, string fromStatus, string toStatus, CancellationToken ct) =>
        NotifyAsync(new
        {
            kind = IngestEventKinds.AttachmentStatusChanged,
            noteId,
            attachmentId,
            from = fromStatus,
            to = toStatus,
        }, ct);

    public Task PublishNoteSucceededAsync(Guid noteId, CancellationToken ct) =>
        NotifyAsync(new
        {
            kind = IngestEventKinds.NoteSucceeded,
            noteId,
        }, ct);

    public Task PublishNoteFailedAsync(Guid noteId, string errorMessage, CancellationToken ct) =>
        NotifyAsync(new
        {
            kind = IngestEventKinds.NoteFailed,
            noteId,
            error = errorMessage,
        }, ct);

    public Task PublishNoteCancelledAsync(Guid noteId, CancellationToken ct) =>
        NotifyAsync(new
        {
            kind = IngestEventKinds.NoteCancelled,
            noteId,
        }, ct);

    public Task PublishHubMaterializedAsync(Guid noteId, Guid entityId, CancellationToken ct) =>
        NotifyAsync(new
        {
            kind = IngestEventKinds.HubMaterialized,
            noteId,
            entityId,
        }, ct);

    private async Task NotifyAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var owns = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            owns = true;
        }
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
            cmd.Parameters.AddWithValue("chan", IngestEventKinds.Channel);
            cmd.Parameters.AddWithValue("payload", json);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
