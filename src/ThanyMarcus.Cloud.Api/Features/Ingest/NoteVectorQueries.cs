using System.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using Pgvector;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Ingest;

public static class NoteVectorQueries
{
    // Raw-SQL path: Pgvector.EntityFrameworkCore 0.x does not surface the <=> cosine operator into LINQ.
    public static async Task<List<RelatedNote>> NearestAsync(
        CloudDbContext db,
        float[] query,
        int k,
        Guid? excludeNoteId,
        Instant excludeCreatedAfter,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);

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
                SELECT id, relative_path, body_output, embedding <=> @emb::vector AS distance
                  FROM notes
                 WHERE deleted_at IS NULL
                   AND status = 'ready'
                   AND embedding IS NOT NULL
                   AND created_at < @exclude_after
                   AND (@exclude_id::uuid IS NULL OR id <> @exclude_id::uuid)
                 ORDER BY embedding <=> @emb::vector
                 LIMIT @k
                """;
            cmd.Parameters.AddWithValue("emb", new Vector(query));
            cmd.Parameters.AddWithValue("k", k);
            cmd.Parameters.AddWithValue("exclude_after", excludeCreatedAfter.ToDateTimeUtc());
            var idParam = cmd.CreateParameter();
            idParam.ParameterName = "exclude_id";
            idParam.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Uuid;
            idParam.Value = (object?)excludeNoteId ?? DBNull.Value;
            cmd.Parameters.Add(idParam);

            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var list = new List<RelatedNote>(k);
            while (await rdr.ReadAsync(ct))
            {
                list.Add(new RelatedNote(
                    Id: rdr.GetGuid(0),
                    RelativePath: rdr.IsDBNull(1) ? string.Empty : rdr.GetString(1),
                    BodyOutput: rdr.IsDBNull(2) ? string.Empty : rdr.GetString(2),
                    Distance: rdr.GetDouble(3)));
            }
            return list;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}

public sealed record RelatedNote(
    Guid Id,
    string RelativePath,
    string BodyOutput,
    double Distance);
