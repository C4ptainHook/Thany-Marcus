using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

public static class RateLimitTestExtensions
{
    public const string SubUsHeader = "X-Test-Sub-Us";
    public const string RemoteIpHeader = "X-Test-Remote-Ip";

    public static WebApplicationFactory<Program> WithPerRequestTestAuth(
        this WebApplicationFactory<Program> factory,
        string totp = TotpClaimValues.NotEnabled)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddAuthentication(o =>
            {
                o.DefaultScheme = PerRequestTestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = PerRequestTestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = PerRequestTestAuthHandler.SchemeName;
            })
            .AddScheme<PerRequestTestAuthOptions, PerRequestTestAuthHandler>(
                PerRequestTestAuthHandler.SchemeName,
                o => o.Totp = totp);

            s.Configure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = PerRequestTestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = PerRequestTestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = PerRequestTestAuthHandler.SchemeName;
            });
        }));

    public static WebApplicationFactory<Program> WithRemoteIpHeader(
        this WebApplicationFactory<Program> factory)
        => factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
                s.AddTransient<IStartupFilter, RemoteIpHeaderStartupFilter>()));

    public static WebApplicationFactory<Program> WithRateLimitConfig(
        this WebApplicationFactory<Program> factory,
        IDictionary<string, string?> overrides)
        => factory.WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(overrides)));
}

public sealed class PerRequestTestAuthOptions : AuthenticationSchemeOptions
{
    public string Totp { get; set; } = TotpClaimValues.NotEnabled;
}

public sealed class PerRequestTestAuthHandler(
    IOptionsMonitor<PerRequestTestAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<PerRequestTestAuthOptions>(options, logger, encoder)
{
    public const string SchemeName = "PerRequestTest";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(RateLimitTestExtensions.SubUsHeader, out var subUs)
            || string.IsNullOrEmpty(subUs))
            return Task.FromResult(AuthenticateResult.NoResult());

        var totp = Request.Headers.TryGetValue("X-Test-Totp", out var totpHeader)
                   && !string.IsNullOrEmpty(totpHeader)
            ? totpHeader.ToString()
            : Options.Totp;

        var identity = new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.SubUs, subUs.ToString()),
                new Claim(ClaimTypes.Email, $"{subUs}@example.com"),
                new Claim(ClaimTypes.Name, subUs.ToString()),
                new Claim(AuthClaimTypes.Totp, totp),
            ],
            SchemeName,
            ClaimTypes.Name,
            ClaimTypes.Role);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), new AuthenticationProperties(), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal sealed class RemoteIpHeaderStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (ctx, n) =>
        {
            if (ctx.Request.Headers.TryGetValue(RateLimitTestExtensions.RemoteIpHeader, out var ip)
                && !string.IsNullOrEmpty(ip)
                && IPAddress.TryParse(ip.ToString(), out var parsed))
            {
                ctx.Connection.RemoteIpAddress = parsed;
            }
            await n();
        });
        next(app);
    };
}
