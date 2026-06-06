using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Migrations;

[Collection(PostgresCollection.Name)]
public sealed class PortalEncryptedTokenMigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task After_migration_admin_token_is_ephemeral_on_jobs_not_on_clouds()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync(ct);

        (await ColumnExistsAsync(conn, "clouds", "encrypted_cloud_admin_token", ct)).ShouldBeFalse();
        (await ColumnExistsAsync(conn, "clouds", "cloud_admin_token_hash", ct)).ShouldBeFalse();
        (await ColumnExistsAsync(conn, "provisioning_jobs", "admin_token_ciphertext", ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task After_migration_plugin_token_metadata_token_hash_is_bytea()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var conn = new NpgsqlConnection(postgres.ConnectionString);
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_name = 'plugin_token_metadata' AND column_name = 'token_hash'",
            conn);
        var dataType = (string?)await cmd.ExecuteScalarAsync(ct);
        dataType.ShouldBe("bytea");
    }

    [Fact]
    public async Task DbContext_round_trip_uses_byte_array_for_token_hash()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        await using var db = new PortalDbContext(opts);
        // No exception when reading from clean schema.
        var count = await db.PluginTokenMetadata.CountAsync(ct);
        count.ShouldBe(0);
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection conn, string table, string column, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns " +
            "WHERE table_name = @t AND column_name = @c)", conn);
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        var v = (bool?)await cmd.ExecuteScalarAsync(ct);
        return v ?? false;
    }
}
