using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class ForwardedHeadersTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    [Fact]
    public async Task X_Forwarded_Proto_https_marks_request_as_https()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddTransient<IStartupFilter, IsHttpsProbeStartupFilter>()));
        using var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/test/is-https", UriKind.Relative));
        req.Headers.Add("X-Forwarded-Proto", "https");
        var res = await client.SendAsync(req, ct);

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync(ct)).ShouldBe("True");
    }

    [Fact]
    public async Task Without_X_Forwarded_Proto_request_is_http()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = Factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddTransient<IStartupFilter, IsHttpsProbeStartupFilter>()));
        using var client = factory.CreateClient();

        var res = await client.GetAsync(new Uri("/api/test/is-https", UriKind.Relative), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await res.Content.ReadAsStringAsync(ct)).ShouldBe("False");
    }

    private sealed class IsHttpsProbeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.UseEndpoints(endpoints =>
                endpoints.MapGet("/api/test/is-https", (HttpContext http) =>
                    Results.Text(http.Request.IsHttps.ToString())));
        };
    }
}
