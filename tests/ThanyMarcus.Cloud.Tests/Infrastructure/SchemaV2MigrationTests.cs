using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace ThanyMarcus.Cloud.Tests.Infrastructure;

[Collection(PostgresCollection.Name)]
public sealed class SchemaV2MigrationTests(PostgresFixture fx) : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;
    public async ValueTask DisposeAsync() => await fx.ResetAsync();

    [Fact]
    public async Task vector_extension_is_installed()
    {
        var count = await ScalarAsync<long>(
            "SELECT COUNT(*)::bigint FROM pg_extension WHERE extname = 'vector'");
        count.ShouldBe(1);
    }

    [Fact]
    public async Task notes_embedding_is_hnsw()
    {
        var count = await ScalarAsync<long>(
            "SELECT COUNT(*)::bigint FROM pg_indexes " +
            "WHERE indexname = 'ix_notes_embedding' AND indexdef LIKE '%USING hnsw%'");
        count.ShouldBe(1);
    }

    [Fact]
    public async Task entities_embedding_is_hnsw()
    {
        var count = await ScalarAsync<long>(
            "SELECT COUNT(*)::bigint FROM pg_indexes " +
            "WHERE indexname = 'ix_entities_embedding' AND indexdef LIKE '%USING hnsw%'");
        count.ShouldBe(1);
    }

    [Fact]
    public async Task notes_has_v2_columns()
    {
        var count = await ScalarAsync<long>(
            "SELECT COUNT(*)::bigint FROM information_schema.columns " +
            "WHERE table_name = 'notes' AND column_name IN " +
            "('embedding','deleted_at','is_hub','hub_entity_id','transition_version')");
        count.ShouldBe(5);
    }

    [Fact]
    public async Task notes_suggested_project_dropped()
    {
        var count = await ScalarAsync<long>(
            "SELECT COUNT(*)::bigint FROM information_schema.columns " +
            "WHERE table_name = 'notes' AND column_name = 'suggested_project'");
        count.ShouldBe(0);
    }

    [Fact]
    public async Task notes_project_id_dropped()
    {
        var count = await ScalarAsync<long>(
            "SELECT COUNT(*)::bigint FROM information_schema.columns " +
            "WHERE table_name = 'notes' AND column_name = 'project_id'");
        count.ShouldBe(0);
    }

    [Fact]
    public async Task entities_partial_unique_blocks_duplicate_within_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(ct);

        await ExecuteAsync(conn,
            "INSERT INTO entities (id, kind, canonical_name, source, created_at, updated_at) " +
            "VALUES (gen_random_uuid(), 'person', 'TestPerson', 'user', now(), now())",
            ct);

        var ex = await Should.ThrowAsync<PostgresException>(async () =>
            await ExecuteAsync(conn,
                "INSERT INTO entities (id, kind, canonical_name, source, created_at, updated_at) " +
                "VALUES (gen_random_uuid(), 'person', 'TestPerson', 'user', now(), now())",
                ct));
        ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task notes_embedding_accepts_vector_literal()
    {
        var ct = TestContext.Current.CancellationToken;
        var dims = string.Join(',', Enumerable.Repeat("0.1", 256));
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(ct);
        await ExecuteAsync(conn,
            "INSERT INTO notes (id, captured_at, status, body_input, embedding, created_at, updated_at) " +
            $"VALUES (gen_random_uuid(), now(), 'pending', 'test', '[{dims}]'::vector(256), now(), now())",
            ct);
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var conn = new NpgsqlConnection(fx.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var result = await cmd.ExecuteScalarAsync(ct);
        return (T)result!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
