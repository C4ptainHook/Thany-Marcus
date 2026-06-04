using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Features.Sync;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class NoteDeleteEndpoint
{
    public static void MapNoteDeleteEndpoint(this IEndpointRouteBuilder app)
    {
        app.MapDelete("/api/notes/{noteId:guid}", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("DeleteNote")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapDelete("/api/sync/notes/{noteId:guid}", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("SyncDeleteNote")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> HandleAsync(
        Guid noteId,
        NoteTombstoneService tombstones,
        CloudDbContext db,
        CancellationToken ct)
    {
        var outcome = await tombstones.TombstoneAsync(noteId, ct);
        if (outcome == TombstoneOutcome.NotFound)
        {
            return Results.NotFound();
        }

        if (outcome == TombstoneOutcome.Tombstoned)
        {
            await NotifyAsync(db, JobOrchestratorWorker.ChangedChannel, noteId.ToString(), ct);
        }
        return Results.NoContent();
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
