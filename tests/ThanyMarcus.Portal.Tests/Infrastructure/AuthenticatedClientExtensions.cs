using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;

namespace ThanyMarcus.Portal.Tests.Infrastructure;

public static class AuthenticatedClientExtensions
{
    public static WebApplicationFactory<Program> WithTestAuth(
        this WebApplicationFactory<Program> factory,
        Guid userId,
        string email = "alice@example.com",
        string name = "Alice",
        string? picture = null,
        string totp = TotpClaimValues.NotEnabled,
        Instant? issuedAt = null)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            List<Claim> claims =
            [
                new Claim(AuthClaimTypes.SubUs, userId.ToString()),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Name, name),
                new Claim(AuthClaimTypes.Totp, totp),
            ];
            if (picture is not null)
                claims.Add(new Claim("picture", picture));

            s.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<TestAuthOptions, TestAuthHandler>(TestAuthHandler.SchemeName, o =>
            {
                o.Claims = [.. claims];
                o.IssuedAt = issuedAt;
            });

            s.Configure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
        }));

    public static WebApplicationFactory<Program> WithUnauthenticated(
        this WebApplicationFactory<Program> factory)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddAuthentication(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            })
            .AddScheme<NoopAuthOptions, NoopAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            s.Configure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
        }));
}

public sealed class NoopAuthOptions : AuthenticationSchemeOptions { }

public sealed class NoopAuthHandler(
    IOptionsMonitor<NoopAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<NoopAuthOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        => Task.FromResult(AuthenticateResult.NoResult());
}
