using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class ReprocessEndpoint
{
    public static void MapReprocessEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/notes/{noteId:guid}/reprocess", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("PostNoteReprocess")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(
        Guid noteId,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null || note.DeletedAt is not null)
        {
            return Results.NotFound();
        }

        var terminals = new[]
        {
            IngestJobStatus.Succeeded,
            IngestJobStatus.FailedExtraction,
            IngestJobStatus.FailedComposition,
            IngestJobStatus.FailedRoute,
            IngestJobStatus.FailedEntities,
            IngestJobStatus.FailedEmbedding,
            IngestJobStatus.DeadLettered,
        };
        var hasActive = await db.IngestJobs.AnyAsync(
            j => j.NoteId == noteId && !terminals.Contains(j.Status), ct);
        if (hasActive)
        {
            return Results.Problem(
                "active ingest job exists for note",
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetCurrentInstant();
        var job = new IngestJob
        {
            Id          = Guid.CreateVersion7(),
            NoteId      = noteId,
            Kind        = IngestJobKind.Reprocess,
            Status      = IngestJobStatus.Queued,
            ScheduledAt = now,
            CreatedAt   = now,
            UpdatedAt   = now,
        };
        db.IngestJobs.Add(job);

        foreach (var att in await db.Attachments.Where(a => a.NoteId == noteId).ToListAsync(ct))
        {
            att.ExtractionStatus = AttachmentExtractionStatus.Pending;
            att.ExtractedText = null;
            att.ExtractionError = null;
        }
        note.Status = NoteStatus.Processing;
        note.TransitionVersion += 1;

        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, JobOrchestratorWorker.NewChannel, ct);

        return Results.Accepted(value: new { jobId = job.Id, status = IngestJobStatus.Queued });
    }

    private static async Task NotifyAsync(CloudDbContext db, string channel, CancellationToken ct)
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
            await using var cmd = new NpgsqlCommand($"NOTIFY {channel}", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns) await db.Database.CloseConnectionAsync();
        }
    }
}
