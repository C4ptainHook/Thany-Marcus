using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public sealed record CalibrationSample(
    IReadOnlyList<double> Positives,
    IReadOnlyList<double> Negatives);

public static class RelatedNotesCalibrationQueries
{
    // Positives = note pairs the vault's own graph says are related (share a unified entity, or a tag);
    // negatives = random pairs with positives excluded. Distance via the same pgvector <=> the endpoint uses.
    // Candidate population mirrors RelatedNotesEndpoint exactly (deleted_at IS NULL, status='ready', embedding NOT NULL).
    public static async Task<CalibrationSample> FetchSampleAsync(
        CloudDbContext db, int randomSampleSize, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

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
                WITH ready AS (
                  SELECT id, embedding, tags
                    FROM notes
                   WHERE deleted_at IS NULL AND status = 'ready' AND embedding IS NOT NULL
                ),
                ent AS (
                  SELECT DISTINCT m1.note_id AS a, m2.note_id AS b
                    FROM mentions m1
                    JOIN mentions m2 ON m1.entity_id = m2.entity_id AND m1.note_id < m2.note_id
                ),
                tag AS (
                  SELECT na.id AS a, nb.id AS b
                    FROM ready na
                    JOIN ready nb ON na.id < nb.id
                   WHERE na.tags IS NOT NULL AND nb.tags IS NOT NULL AND na.tags && nb.tags
                ),
                pos AS (
                  SELECT a, b FROM ent
                  UNION
                  SELECT a, b FROM tag
                ),
                pos_dist AS (
                  SELECT 'pos'::text AS class, (na.embedding <=> nb.embedding) AS dist
                    FROM pos
                    JOIN ready na ON na.id = pos.a
                    JOIN ready nb ON nb.id = pos.b
                ),
                random_pairs AS (
                  SELECT na.id AS a, nb.id AS b
                    FROM ready na
                    JOIN ready nb ON na.id < nb.id
                    LEFT JOIN pos p ON p.a = na.id AND p.b = nb.id
                   WHERE p.a IS NULL
                   ORDER BY random()
                   LIMIT @sample
                ),
                neg_dist AS (
                  SELECT 'neg'::text AS class, (na.embedding <=> nb.embedding) AS dist
                    FROM random_pairs rp
                    JOIN ready na ON na.id = rp.a
                    JOIN ready nb ON nb.id = rp.b
                )
                SELECT class, dist FROM pos_dist
                UNION ALL
                SELECT class, dist FROM neg_dist
                """;
            cmd.Parameters.AddWithValue("sample", randomSampleSize);

            var positives = new List<double>();
            var negatives = new List<double>();
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            while (await rdr.ReadAsync(ct))
            {
                if (rdr.IsDBNull(1)) continue;
                var dist = rdr.GetDouble(1);
                if (rdr.GetString(0) == "pos") positives.Add(dist);
                else negatives.Add(dist);
            }
            return new CalibrationSample(positives, negatives);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    public static async Task<(int ReadyNotes, int Entities)> CountsAsync(
        CloudDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var readyNotes = await db.Notes
            .CountAsync(n => n.DeletedAt == null && n.Status == NoteStatus.Ready && n.Embedding != null, ct);
        var entities = await db.Entities.CountAsync(e => e.DeletedAt == null, ct);
        return (readyNotes, entities);
    }
}
