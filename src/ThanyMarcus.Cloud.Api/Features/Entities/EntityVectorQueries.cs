using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Llm.Prompts;

namespace ThanyMarcus.Cloud.Api.Features.Entities;

public static class EntityVectorQueries
{
    // Raw-SQL path: Pgvector.EntityFrameworkCore 0.x does not surface the <=> cosine operator into LINQ.
    public static async Task<List<EntityNeighbor>> NearestAsync(
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
                SELECT id, kind, canonical_name, aliases, description
                  FROM entities
                 WHERE deleted_at IS NULL
                   AND kind = @kind
                 ORDER BY embedding <=> @emb::vector
                 LIMIT @k
                """;
            cmd.Parameters.AddWithValue("kind", kind);
            cmd.Parameters.AddWithValue("emb", new Vector(candidate));
            cmd.Parameters.AddWithValue("k", k);
            await using var rdr = await cmd.ExecuteReaderAsync(ct);
            var list = new List<EntityNeighbor>(k);
            while (await rdr.ReadAsync(ct))
            {
                var aliases = rdr.IsDBNull(3) ? Array.Empty<string>() : (string[])rdr.GetValue(3);
                list.Add(new EntityNeighbor(
                    Id: rdr.GetGuid(0),
                    Kind: rdr.GetString(1),
                    CanonicalName: rdr.GetString(2),
                    Aliases: aliases,
                    Description: rdr.IsDBNull(4) ? null : rdr.GetString(4)));
            }
            return list;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    // kNN match against curated entities returning cosine distance so the caller can gate on it.
    public static async Task<List<EntityDistance>> NearestWithDistanceAsync(
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
                  FROM entities
                 WHERE deleted_at IS NULL
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

public readonly record struct EntityDistance(Guid Id, double Distance);
