using System.Net;
using Shouldly;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScaffoldTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Host_boots_and_health_live_returns_ok()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), ct);

        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_returns_ok_against_postgres()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        using var client = factory.CreateClient();

        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), ct);

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
