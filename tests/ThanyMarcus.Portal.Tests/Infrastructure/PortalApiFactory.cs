using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

public sealed class PortalApiFactory(PostgresFixture postgres) : WebApplicationFactory<Program>
{
    private readonly PostgresFixture postgres = postgres;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Portal"] = postgres.ConnectionString,
            });
        });
    }
}
