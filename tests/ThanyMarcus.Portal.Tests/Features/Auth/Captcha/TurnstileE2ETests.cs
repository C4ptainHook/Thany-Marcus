using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

public sealed class TurnstileE2ETests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly WebApplicationFactoryClientOptions NoRedirect = new() { AllowAutoRedirect = false };

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

    [Fact]
    public async Task FailedCount_signal_blocks_subsequent_attempt_then_admits_with_valid_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var cfg = new Dictionary<string, string?>
        {
            ["Lockout:Totp:MaxFailures"]               = "100",
            ["RateLimiting:TotpChallenge:PermitLimit"] = "100",
        };
        cfg.Enabled(failureThreshold: 3);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            (await SendChallengeAsync(client, user.Id, token: null, ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe((HttpStatusCode)428);

        var ok = await SendChallengeAsync(client, user.Id, token: StubTurnstileValidator.ValidToken, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tracker_signal_via_OnRejected_blocks_attempt_after_limiter_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var cfg = new Dictionary<string, string?>
        {
            ["Lockout:Totp:MaxFailures"]                = "100",
            ["RateLimiting:TotpChallenge:PermitLimit"]  = "2",
            ["RateLimiting:TotpChallenge:WindowSeconds"] = "1",
        };
        cfg.Enabled(failureThreshold: 100);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);
        using var client = factory.CreateClient();

        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);

        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe((HttpStatusCode)428);

        var ok = await SendChallengeAsync(client, user.Id, token: StubTurnstileValidator.ValidToken, ct);
        ok.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Signin_flow_429_arms_tracker_then_signin_endpoint_returns_428()
    {
        var ct = TestContext.Current.CancellationToken;
        var cfg = new Dictionary<string, string?>
        {
            ["RateLimiting:SignInGoogle:PermitLimit"]  = "2",
            ["RateLimiting:SignInGoogle:WindowSeconds"] = "60",
        };
        cfg.Enabled(failureThreshold: 100);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithUnauthenticated()
            .WithRemoteIpHeader()
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);
        using var client = factory.CreateClient(NoRedirect);

        for (var i = 0; i < 2; i++)
        {
            var resp = await SendSignInCallbackAsync(client, "192.0.2.77", ct);
            resp.StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }
        var rejected = await SendSignInCallbackAsync(client, "192.0.2.77", ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var blocked = await SendSignInChallengeAsync(client, "192.0.2.77", token: null, ct);
        blocked.StatusCode.ShouldBe((HttpStatusCode)428);

        var allowed = await SendSignInChallengeAsync(client, "192.0.2.77", token: StubTurnstileValidator.ValidToken, ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Signin_flow_admits_when_valid_token_supplied_via_query_string()
    {
        var ct = TestContext.Current.CancellationToken;
        var cfg = new Dictionary<string, string?>
        {
            ["RateLimiting:SignInGoogle:PermitLimit"]  = "2",
            ["RateLimiting:SignInGoogle:WindowSeconds"] = "60",
        };
        cfg.Enabled(failureThreshold: 100);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithUnauthenticated()
            .WithRemoteIpHeader()
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);

        var tracker = factory.Services.GetRequiredService<CaptchaRequirementTracker>();
        tracker.MarkRequired("ip:192.0.2.77", Duration.FromMinutes(15));

        using var client = factory.CreateClient(NoRedirect);

        var allowed = await SendSignInChallengeAsync(client, "192.0.2.77", token: StubTurnstileValidator.ValidToken, ct);
        allowed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        stub.CallCount.ShouldBe(1);
        tracker.IsRequired("ip:192.0.2.77").ShouldBeFalse();
    }

    [Fact]
    public async Task Disabled_Turnstile_does_not_gate_any_surface_or_modify_OnRejected_behavior()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(new Dictionary<string, string?>
            {
                ["Lockout:Totp:MaxFailures"]                = "100",
                ["RateLimiting:TotpChallenge:PermitLimit"]  = "2",
                ["RateLimiting:TotpChallenge:WindowSeconds"] = "300",
            });
        using var client = factory.CreateClient();

        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, token: null, ct))
            .StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private static async Task<HttpResponseMessage> SendChallengeAsync(
        HttpClient client, Guid userId, string? token, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/totp-challenge")
        {
            Content = JsonContent.Create(new TotpChallengeRequest("000000")),
        };
        req.Headers.Add(RateLimitTestExtensions.SubUsHeader, userId.ToString());
        if (token is not null)
            req.Headers.Add("cf-turnstile-response", token);
        return await client.SendAsync(req, ct);
    }

    private static Task<HttpResponseMessage> SendSignInCallbackAsync(HttpClient client, string ip, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/signin-google?code=test&state=test");
        req.Headers.Add(RateLimitTestExtensions.RemoteIpHeader, ip);
        return client.SendAsync(req, ct);
    }

    private static Task<HttpResponseMessage> SendSignInChallengeAsync(
        HttpClient client, string ip, string? token, CancellationToken ct)
    {
        var path = token is null
            ? "/api/auth/signin"
            : $"/api/auth/signin?turnstile={token}";
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.Add(RateLimitTestExtensions.RemoteIpHeader, ip);
        return client.SendAsync(req, ct);
    }
}
