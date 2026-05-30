using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using ThanyMarcus.Cloud.Api.Features.Entities;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.EntitySuggestions;

public static class EntitySuggestionVectorQueries
{
    // kNN match against open (not accepted, not dismissed) suggestions of the same kind, returning
    // cosine distance so the aggregator can decide whether to fold the candidate into an existing row.
    public static async Task<List<EntityDistance>> NearestOpenWithDistanceAsync(
        CloudDbContext db,
        string kind,
        float[] candidate,
        int k,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(candidate);

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
                SELECT id, embedding <=> @emb::vector AS distance
                  FROM entity_suggestions
                 WHERE accepted_at IS NULL
                   AND dismissed_at IS NULL
                   AND kind = @kind
                   AND embedding IS NOT NULL
                 ORDER BY embedding <=> @emb::vector
                 LIMIT @k
                """;
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("emb", new Vector(candidate));
            cmd.Parameters.AddWithValue("k", k);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var list = new List<EntityDistance>(k);
            while (await rdr.ReadAsync(ct))
            {
                list.Add(new EntityDistance(rdr.GetGuid(0), rdr.GetDouble(1)));
            }
            return list;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
