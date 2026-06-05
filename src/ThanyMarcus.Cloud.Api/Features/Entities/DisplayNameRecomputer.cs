using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

// Recomputes an entity's mutable DisplayName (the most-frequent surface form) from its mentions,
// with hysteresis so the label doesn't flap. CanonicalName — the stable link target / stub
// basename — is never touched, so a flip moves nothing in the graph: no link rewrites, no file
// rename, no re-embed. Mutates only the tracked entity; the caller owns SaveChanges.
public static class DisplayNameRecomputer
{
    public static async Task<bool> RecomputeAsync(
        CloudDbContext db, Entity entity, int hysteresisMargin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(entity);

        var counts = await SurfaceFormCountsAsync(db, entity.Id, ct);
        if (counts.Count == 0) return false;

        var currentLabel = entity.DisplayName ?? entity.CanonicalName;
        var currentNorm = currentLabel.Trim().ToLowerInvariant();
        var currentCount = counts.FirstOrDefault(r => r.Norm == currentNorm).Count;

        // Rows are count-desc; the first whose normalized form differs from the current label is the
        // strongest challenger. It must clear the current count by the full margin to flip.
        var challenger = counts.FirstOrDefault(r => r.Norm != currentNorm);
        if (challenger.Rep is null || challenger.Count < currentCount + hysteresisMargin)
        {
            return false;
        }

        entity.DisplayName = challenger.Rep;
        return true;
    }

    private static async Task<List<(string Norm, long Count, string? Rep)>> SurfaceFormCountsAsync(
        CloudDbContext db, Guid entityId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = false;
        if (conn.State != ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
            opened = true;
        }
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT lower(btrim(anchor_text)) AS norm,
                       count(*) AS c,
                       mode() WITHIN GROUP (ORDER BY btrim(anchor_text)) AS rep
                  FROM mentions
                 WHERE entity_id = @id AND btrim(anchor_text) <> ''
                 GROUP BY lower(btrim(anchor_text))
                 ORDER BY c DESC, norm ASC
                """;
            cmd.Parameters.AddWithValue("id", entityId);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var list = new List<(string, long, string?)>();
            while (await rdr.ReadAsync(ct))
            {
                list.Add((rdr.GetString(0), rdr.GetInt64(1), rdr.IsDBNull(2) ? null : rdr.GetString(2)));
            }
            return list;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
