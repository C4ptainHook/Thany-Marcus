using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class CookieAuthPipelineTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private async Task<User> InsertUserAsync(Instant? sessionsInvalidatedAt = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = "alice@example.com",
            Name = "Alice",
            SessionsInvalidatedAt = sessionsInvalidatedAt,
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private static string MintCookie(WebApplicationFactory<Program> factory, Guid userId, DateTimeOffset issuedAt)
    {
        var monitor = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>();
        var options = monitor.Get(CookieAuthenticationDefaults.AuthenticationScheme);
        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, userId.ToString()));
        identity.AddClaim(new Claim(ClaimTypes.Email, "stale@example.com"));
        identity.AddClaim(new Claim(ClaimTypes.Name, "Stale"));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, TotpClaimValues.NotEnabled));
        var principal = new ClaimsPrincipal(identity);
        var props = new AuthenticationProperties
        {
            IssuedUtc = issuedAt,
            ExpiresUtc = issuedAt.AddDays(14),
        };
        var ticket = new AuthenticationTicket(principal, props, CookieAuthenticationDefaults.AuthenticationScheme);
        return options.TicketDataFormat.Protect(ticket);
    }

    [Fact]
    public async Task Cookie_issued_before_sessions_invalidated_returns_401_on_protected_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        var invalidatedAt = Clock.GetCurrentInstant();
        var user = await InsertUserAsync(sessionsInvalidatedAt: invalidatedAt);
        var issuedAt = (invalidatedAt - Duration.FromMinutes(5)).ToDateTimeOffset();

        await using var factory = new PortalApiFactory(Postgres);
        var cookieValue = MintCookie(factory, user.Id, issuedAt);

        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        req.Headers.Add("Cookie", $".Portal.Auth={cookieValue}");
        var response = await client.SendAsync(req, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Cookie_issued_after_sessions_invalidated_passes_and_refreshes_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var invalidatedAt = Clock.GetCurrentInstant() - Duration.FromHours(1);
        var user = await InsertUserAsync(sessionsInvalidatedAt: invalidatedAt);
        var issuedAt = DateTimeOffset.UtcNow;

        await using var factory = new PortalApiFactory(Postgres);
        var cookieValue = MintCookie(factory, user.Id, issuedAt);

        using var client = factory.CreateClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/auth/me", UriKind.Relative));
        req.Headers.Add("Cookie", $".Portal.Auth={cookieValue}");
        var response = await client.SendAsync(req, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<MeResponse>(ct);
        body.ShouldNotBeNull();
        body!.Email.ShouldBe("alice@example.com");
        body.Name.ShouldBe("Alice");
    }
}
