using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.PluginApi;

namespace ThanyMarcus.Cloud.Api.Features.Sync;

public static class SyncPushEndpoint
{
    public const string UserEditPushedStage = "user_edit_pushed";

    public static void MapSyncPushEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/sync/push", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("PostSyncPush")
            .Produces<SyncPushResponse>(StatusCodes.Status200OK)
            .Produces<SyncPushConflict>(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(
        SyncPushRequest req,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var now = clock.GetCurrentInstant();
        var baseInstant = Instant.FromDateTimeOffset(req.BaseUpdatedAt);
        var deletedFlag = req.Deleted;
        var newBody = req.Body ?? string.Empty;

        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                body_output        = CASE WHEN {deletedFlag} THEN body_output ELSE {newBody} END,
                body_hash          = NULL,
                deleted_at         = CASE WHEN {deletedFlag} THEN {now} ELSE deleted_at END,
                updated_at         = {now},
                transition_version = transition_version + 1
              WHERE id = {req.NoteId} AND updated_at = {baseInstant}
            """, ct);

        if (rows == 0)
        {
            var existing = await db.Notes
                .AsNoTracking()
                .Where(n => n.Id == req.NoteId)
                .Select(n => new { n.UpdatedAt, n.TransitionVersion })
                .SingleOrDefaultAsync(ct);
            if (existing is null)
            {
                return Results.NotFound(new { code = SyncPushConflictCodes.NoteNotFound });
            }
            return Results.Conflict(new SyncPushConflict(
                Code:                     SyncPushConflictCodes.StaleBaseline,
                CurrentUpdatedAt:         existing.UpdatedAt.ToDateTimeOffset(),
                CurrentTransitionVersion: existing.TransitionVersion));
        }

        var updatedRow = await db.Notes
            .AsNoTracking()
            .Where(n => n.Id == req.NoteId)
            .Select(n => new { n.TransitionVersion })
            .SingleAsync(ct);

        Guid? embedJobId = null;
        if (!deletedFlag)
        {
            embedJobId = await TryEnqueueUserEditEmbedAsync(db, req.NoteId, now, ct);
        }

        if (embedJobId is { } jobId)
        {
            await AppendUserEditPushedEventAsync(
                db, jobId, baseInstant, now, newBody, deletedFlag, ct);
        }

        await NotifyAsync(db, JobOrchestratorWorker.ChangedChannel, req.NoteId.ToString(), ct);

        return Results.Ok(new SyncPushResponse(
            NoteId:            req.NoteId,
            UpdatedAt:         now.ToDateTimeOffset(),
            TransitionVersion: updatedRow.TransitionVersion));
    }

    private static async Task<Guid?> TryEnqueueUserEditEmbedAsync(
        CloudDbContext db, Guid noteId, Instant now, CancellationToken ct)
    {
        var terminals = new[]
        {
            IngestJobStatus.Succeeded,
            IngestJobStatus.FailedExtraction,
            IngestJobStatus.FailedComposition,
            IngestJobStatus.FailedRoute,
            IngestJobStatus.FailedEntities,
            IngestJobStatus.FailedSynthesis,
            IngestJobStatus.FailedEmbedding,
            IngestJobStatus.DeadLettered,
        };
        var hasActiveJob = await db.IngestJobs
            .AnyAsync(j => j.NoteId == noteId && !terminals.Contains(j.Status), ct);
        if (hasActiveJob)
        {
            return null;
        }

        var job = new IngestJob
        {
            Id          = Guid.CreateVersion7(),
            NoteId      = noteId,
            Kind        = IngestJobKind.UserEditEmbed,
            Status      = IngestJobStatus.Embedding,
            ScheduledAt = now,
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync(ct);
        return job.Id;
    }

    private static async Task AppendUserEditPushedEventAsync(
        CloudDbContext db,
        Guid jobId,
        Instant baseUpdatedAt,
        Instant now,
        string body,
        bool deleted,
        CancellationToken ct)
    {
        var bodyBytes = System.Text.Encoding.UTF8.GetByteCount(body);
        var bodySha256 = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(body)));
        var payload = JsonSerializer.Serialize(new
        {
            at = now.ToString(),
            stage = UserEditPushedStage,
            actor = "plugin",
            base_updated_at = baseUpdatedAt.ToString(),
            new_updated_at = now.ToString(),
            body_bytes = bodyBytes,
            body_sha256 = bodySha256,
            deleted,
        });
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                events_log = events_log || {payload}::jsonb,
                updated_at = {now}
              WHERE id = {jobId}
            """, ct);
    }

    private static async Task NotifyAsync(CloudDbContext db, string channel, string payload, CancellationToken ct)
    {
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
            cmd.Parameters.AddWithValue("chan", channel);
            cmd.Parameters.AddWithValue("payload", payload);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns) await db.Database.CloseConnectionAsync();
        }
    }
}
