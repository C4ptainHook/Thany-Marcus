using Microsoft.EntityFrameworkCore;
using Npgsql;
using Respawn;
using Testcontainers.PostgreSql;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .Build();

    public string ConnectionString => Container.GetConnectionString();
    public Respawner Respawner { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var db = new PortalDbContext(opts))
        {
            await db.Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        Respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
        });
    }

    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Respawner.ResetAsync(conn);
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}
