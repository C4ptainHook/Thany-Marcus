using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

public sealed class CaptchaEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private async Task<User> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = $"u-{Guid.NewGuid():N}@x.com",
            Name = "U",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private async Task SeedFailedCountAsync(Guid userId, string kind, short count)
    {
        var ct = TestContext.Current.CancellationToken;
        Db.AuthLockouts.Add(new AuthLockout
        {
            UserId        = userId,
            Kind          = kind,
            FailedCount   = count,
            LastAttemptAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Disabled_Turnstile_returns_required_false_and_empty_site_key()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock);
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<CaptchaStateResponse>(
            new Uri("/api/auth/captcha-state?kind=totp", UriKind.Relative), ct);

        response.ShouldNotBeNull();
        response!.Required.ShouldBeFalse();
        response.SiteKey.ShouldBe("");
    }

    [Fact]
    public async Task Totp_kind_with_failed_count_below_threshold_returns_required_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, AuthLockoutKinds.Totp, 1);

        var cfg = new Dictionary<string, string?>();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<CaptchaStateResponse>(
            new Uri("/api/auth/captcha-state?kind=totp", UriKind.Relative), ct);

        response!.Required.ShouldBeFalse();
        response.SiteKey.ShouldBe(TurnstileTestExtensions.TestSiteKey);
    }

    [Fact]
    public async Task Totp_kind_with_failed_count_at_threshold_returns_required_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, AuthLockoutKinds.Totp, 2);

        var cfg = new Dictionary<string, string?>();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<CaptchaStateResponse>(
            new Uri("/api/auth/captcha-state?kind=totp", UriKind.Relative), ct);

        response!.Required.ShouldBeTrue();
        response.SiteKey.ShouldBe(TurnstileTestExtensions.TestSiteKey);
    }

    [Fact]
    public async Task Totp_kind_with_tracker_entry_returns_required_true_regardless_of_failed_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var cfg = new Dictionary<string, string?>();
        cfg.Enabled(failureThreshold: 100);
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);

        var tracker = factory.Services.GetRequiredService<CaptchaRequirementTracker>();
        tracker.MarkRequired("u:" + user.Id, Duration.FromMinutes(15));

        using var client = factory.CreateClient();
        var response = await client.GetFromJsonAsync<CaptchaStateResponse>(
            new Uri("/api/auth/captcha-state?kind=totp", UriKind.Relative), ct);

        response!.Required.ShouldBeTrue();
    }

    [Fact]
    public async Task Unlock_kind_uses_unlock_failed_count_not_totp()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, AuthLockoutKinds.Totp, 5);

        var cfg = new Dictionary<string, string?>();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await client.GetFromJsonAsync<CaptchaStateResponse>(
            new Uri("/api/auth/captcha-state?kind=unlock", UriKind.Relative), ct);

        response!.Required.ShouldBeFalse();
    }

    [Fact]
    public async Task Signin_kind_is_unauthenticated_and_returns_required_false_initially()
    {
        var ct = TestContext.Current.CancellationToken;
        var cfg = new Dictionary<string, string?>();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithUnauthenticated()
            .WithRemoteIpHeader()
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(
            HttpMethod.Get, "/api/auth/captcha-state?kind=signin");
        req.Headers.Add(RateLimitTestExtensions.RemoteIpHeader, "192.0.2.55");
        var raw = await client.SendAsync(req, ct);
        raw.StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await raw.Content.ReadFromJsonAsync<CaptchaStateResponse>(ct);

        response!.Required.ShouldBeFalse();
        response.SiteKey.ShouldBe(TurnstileTestExtensions.TestSiteKey);
    }

    [Fact]
    public async Task Signin_kind_returns_required_true_when_tracker_has_ip_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var cfg = new Dictionary<string, string?>();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithUnauthenticated()
            .WithRemoteIpHeader()
            .WithRateLimitConfig(cfg);

        var tracker = factory.Services.GetRequiredService<CaptchaRequirementTracker>();
        tracker.MarkRequired("ip:192.0.2.55", Duration.FromMinutes(15));

        using var client = factory.CreateClient();
        var req = new HttpRequestMessage(
            HttpMethod.Get, "/api/auth/captcha-state?kind=signin");
        req.Headers.Add(RateLimitTestExtensions.RemoteIpHeader, "192.0.2.55");
        var raw = await client.SendAsync(req, ct);
        var response = await raw.Content.ReadFromJsonAsync<CaptchaStateResponse>(ct);

        response!.Required.ShouldBeTrue();
    }

    [Fact]
    public async Task Totp_kind_unauthenticated_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var cfg = new Dictionary<string, string?>();
        cfg.Enabled();
        var factory = Factory
            .WithUnauthenticated()
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            new Uri("/api/auth/captcha-state?kind=totp", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invalid_kind_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cfg = new Dictionary<string, string?>();
        cfg.Enabled();
        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(
            new Uri("/api/auth/captcha-state?kind=nonsense", UriKind.Relative), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
