using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using NodaTime.Testing;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Database;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class FactoryDbTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; } = postgres;
    protected FakeClock Clock { get; } = new(Instant.FromUtc(2026, 5, 15, 12, 0));
    protected PortalApiFactory Factory { get; private set; } = null!;
    protected PortalDbContext Db { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Postgres.ResetAsync();
        Factory = new PortalApiFactory(Postgres);

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
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}

public static class FactoryClockExtensions
{
    public static WebApplicationFactory<Program> WithClock(
        this WebApplicationFactory<Program> factory,
        IClock clock)
        => factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
        {
            s.RemoveAll<IClock>();
            s.AddSingleton(clock);
        }));
}
