using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Captcha;

public sealed class RequireTurnstileFilterTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static Dictionary<string, string?> BaseConfig() => new()
    {
        ["Lockout:Totp:MaxFailures"]            = "100",
        ["RateLimiting:TotpChallenge:PermitLimit"] = "100",
    };

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

    private async Task SeedFailedCountAsync(Guid userId, short count)
    {
        var ct = TestContext.Current.CancellationToken;
        Db.AuthLockouts.Add(new AuthLockout
        {
            UserId        = userId,
            Kind          = AuthLockoutKinds.Totp,
            FailedCount   = count,
            LastAttemptAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Filter_admits_request_when_Turnstile_disabled_even_with_high_failed_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, 50);

        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(BaseConfig());
        using var client = factory.CreateClient();

        var response = await SendChallengeAsync(client, user.Id, token: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Filter_admits_when_failed_count_below_threshold_and_no_tracker_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, 1);

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await SendChallengeAsync(client, user.Id, token: null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Filter_returns_428_when_failed_count_at_threshold_and_token_missing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, 2);

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await SendChallengeAsync(client, user.Id, token: null, ct);

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        var body = await response.Content.ReadFromJsonAsync<CaptchaRequiredBody>(ct);
        body!.Error.ShouldBe("captcha_required");
        body.SiteKey.ShouldBe(TurnstileTestExtensions.TestSiteKey);
    }

    [Fact]
    public async Task Filter_admits_when_failed_count_at_threshold_and_token_validates()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, 2);

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 2);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);
        using var client = factory.CreateClient();

        var response = await SendChallengeAsync(client, user.Id, token: StubTurnstileValidator.ValidToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        stub.CallCount.ShouldBe(1);
        stub.SeenTokens.ShouldContain(StubTurnstileValidator.ValidToken);
    }

    [Fact]
    public async Task Filter_returns_428_when_token_present_but_validator_rejects()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SeedFailedCountAsync(user.Id, 2);

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 2);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);
        using var client = factory.CreateClient();

        var response = await SendChallengeAsync(client, user.Id, token: StubTurnstileValidator.InvalidToken, ct);

        response.StatusCode.ShouldBe((HttpStatusCode)428);
        var body = await response.Content.ReadFromJsonAsync<CaptchaRequiredBody>(ct);
        body!.ErrorCodes.ShouldNotBeNull();
        body.ErrorCodes!.ShouldContain("invalid-input-response");
    }

    [Fact]
    public async Task Filter_clears_tracker_partition_after_successful_verify()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 100);
        var stub = new StubTurnstileValidator();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg)
            .WithTurnstileValidator(stub);

        var tracker = factory.Services.GetRequiredService<CaptchaRequirementTracker>();
        var partitionKey = "u:" + user.Id;
        tracker.MarkRequired(partitionKey, Duration.FromMinutes(15));
        tracker.IsRequired(partitionKey).ShouldBeTrue();

        using var client = factory.CreateClient();
        var response = await SendChallengeAsync(client, user.Id, token: StubTurnstileValidator.ValidToken, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        tracker.IsRequired(partitionKey).ShouldBeFalse();
    }

    [Fact]
    public async Task Tracker_signal_alone_triggers_428_when_failed_count_is_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 100);
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);

        var tracker = factory.Services.GetRequiredService<CaptchaRequirementTracker>();
        tracker.MarkRequired("u:" + user.Id, Duration.FromMinutes(15));

        using var client = factory.CreateClient();
        var response = await SendChallengeAsync(client, user.Id, token: null, ct);

        response.StatusCode.ShouldBe((HttpStatusCode)428);
    }

    [Fact]
    public async Task Lockout_filter_runs_before_captcha_so_locked_user_gets_423_not_428()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        Db.AuthLockouts.Add(new AuthLockout
        {
            UserId        = user.Id,
            Kind          = AuthLockoutKinds.Totp,
            FailedCount   = 20,
            LockedUntil   = Clock.GetCurrentInstant() + Duration.FromMinutes(30),
            LastAttemptAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var cfg = BaseConfig();
        cfg.Enabled(failureThreshold: 2);
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(cfg);
        using var client = factory.CreateClient();

        var response = await SendChallengeAsync(client, user.Id, token: null, ct);

        response.StatusCode.ShouldBe((HttpStatusCode)423);
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

    private sealed record CaptchaRequiredBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("site_key")] string SiteKey,
        [property: JsonPropertyName("error_codes")] IReadOnlyList<string>? ErrorCodes);
}
