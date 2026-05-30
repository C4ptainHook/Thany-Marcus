using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static partial class CancelIngestEndpoint
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Warning,
        Message = "spaces DELETE failed during cancel (noteId={NoteId} attachmentId={AttachmentId} key={Key})")]
    private static partial void LogSpacesDeleteFailure(
        ILogger logger, Exception ex, Guid noteId, Guid attachmentId, string key);


    public static void MapCancelIngestEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/api/ingest/{noteId:guid}/cancel", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("PostIngestCancel")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

    private static async Task<IResult> HandleAsync(
        Guid noteId,
        CloudDbContext db,
        IArtifactStore store,
        IIngestEventBus bus,
        IClock clock,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger("CancelIngestEndpoint");
        var note = await db.Notes.SingleOrDefaultAsync(n => n.Id == noteId, ct);
        if (note is null || note.DeletedAt is not null)
        {
            return Results.NotFound();
        }

        var job = await db.IngestJobs
            .Where(j => j.NoteId == noteId)
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (job is null)
        {
            return Results.NotFound();
        }

        if (IngestJobStatus.IsTerminal(job.Status))
        {
            return Results.Problem(
                $"ingest already terminal (status={job.Status})",
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetCurrentInstant();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                status              = 'cancelled',
                last_error          = 'user_cancelled',
                lease_owner         = NULL,
                lease_expires_at    = NULL,
                finished_at         = {now},
                transition_version  = transition_version + 1,
                updated_at          = {now}
              WHERE id = {job.Id}
                AND status NOT IN ('succeeded','failed_extraction','failed_composition',
                                   'failed_route','failed_entities','failed_synthesis',
                                   'failed_embedding','dead_lettered','cancelled')
            """, ct);
        if (rows == 0)
        {
            return Results.Problem(
                "ingest already terminal",
                statusCode: StatusCodes.Status409Conflict);
        }

        var attachments = await db.Attachments.Where(a => a.NoteId == noteId).ToListAsync(ct);
        foreach (var att in attachments)
        {
            if (AttachmentKind.IsBinary(att.Kind))
            {
                try
                {
                    await store.DeleteAsync(att.StorageKey, ct);
                }
                catch (Exception ex)
                {
                    LogSpacesDeleteFailure(log, ex, noteId, att.Id, att.StorageKey);
                }
            }
        }

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM attachments WHERE note_id = {noteId}", ct);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM notes WHERE id = {noteId}", ct);

        await bus.PublishNoteCancelledAsync(noteId, ct);

        return Results.NoContent();
    }
}
