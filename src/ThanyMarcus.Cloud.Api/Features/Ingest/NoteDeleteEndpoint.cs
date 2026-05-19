using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class NoteDeleteEndpoint
{
    public static void MapNoteDeleteEndpoint(this IEndpointRouteBuilder app) =>
        app.MapDelete("/api/notes/{noteId:guid}", HandleAsync)
            .AddEndpointFilter<RequirePluginAuthFilter>()
            .WithName("DeleteNote")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound);

    private static async Task<IResult> HandleAsync(
        Guid noteId,
        CloudDbContext db,
        IClock clock,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notes SET
                deleted_at         = {now},
                transition_version = transition_version + 1,
                updated_at         = {now}
              WHERE id = {noteId}
                AND deleted_at IS NULL
            """, ct);
        if (rows == 0)
        {
            return Results.NotFound();
        }

        await NotifyAsync(db, JobOrchestratorWorker.ChangedChannel, noteId.ToString(), ct);
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
