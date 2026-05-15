using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;

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
                ["Google:ClientId"] = "test-client-id",
                ["Google:ClientSecret"] = "test-client-secret",
            });
        });

        builder.ConfigureServices(s => s.AddTransient<IStartupFilter, TestEndpointsStartupFilter>());
    }
}

internal sealed class TestEndpointsStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        next(app);
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/api/test/totp-required", () => Results.Ok())
                .RequireAuthorization(AuthPolicies.TotpRequired);

            endpoints.MapGet("/api/test/step-up-only", () => Results.Ok())
                .RequireAuthorization()
                .AddEndpointFilter<RequireInfraOpUnlockFilter>();
        });
    };
}
