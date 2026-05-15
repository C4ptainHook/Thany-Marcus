namespace ThanyMarcus.Portal.Tests.Infrastructure;

[Collection(PostgresCollection.Name)]
public abstract class FactoryTestBase(PostgresFixture postgres) : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; } = postgres;
    protected PortalApiFactory Factory { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Postgres.ResetAsync();
        Factory = new PortalApiFactory(Postgres);
    }

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
