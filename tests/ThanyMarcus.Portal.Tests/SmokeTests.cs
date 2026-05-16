using System.Net;
using Shouldly;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SmokeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly PostgresFixture postgres = postgres;
    private PortalApiFactory factory = null!;

    public ValueTask InitializeAsync()
    {
        factory = new PortalApiFactory(postgres);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task HealthLive_returns_200()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await factory.CreateClient().GetAsync(new Uri("/health/live", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_returns_200()
    {
        var ct = TestContext.Current.CancellationToken;
        var response = await factory.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
