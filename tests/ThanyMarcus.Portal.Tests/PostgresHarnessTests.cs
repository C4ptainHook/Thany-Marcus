using Shouldly;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresHarnessTests(PostgresFixture postgres)
{
    private readonly PostgresFixture postgres = postgres;

    [Fact]
    public async Task Container_accepts_connections_and_executes_sql()
    {
        var ct = TestContext.Current.CancellationToken;
        var result = await postgres.Container.ExecScriptAsync("SELECT 1;", ct);

        result.ExitCode.ShouldBe(0L, customMessage: result.Stderr);
    }

    [Fact]
    public void Connection_string_is_populated()
    {
        postgres.ConnectionString.ShouldContain("Host=");
        postgres.ConnectionString.ShouldContain("Port=");
    }
}
