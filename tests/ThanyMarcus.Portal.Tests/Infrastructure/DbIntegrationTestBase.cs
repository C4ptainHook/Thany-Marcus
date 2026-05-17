using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class DbIntegrationTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; } = postgres;
    protected FakeClock Clock { get; } = new(Instant.FromUtc(2026, 5, 15, 12, 0));
    protected PortalDbContext Db { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Postgres.ResetAsync();

        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TimestampInterceptor(Clock))
            .Options;

        Db = new PortalDbContext(opts);
    }

    public async ValueTask DisposeAsync()
    {
        await Db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
