using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using OtpNet;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Lockout;

public sealed class TotpLockoutTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly Dictionary<string, string?> LockoutWinsConfig = new()
    {
        ["Lockout:Totp:MaxFailures"] = "3",
        ["Lockout:Totp:WindowSeconds"] = "3600",
        ["Lockout:Totp:LockoutSeconds"] = "1800",
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

    [Fact]
    public async Task Threshold_failures_lock_account_with_423_body_and_retry_after_header()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            (await SendChallengeAsync(client, user.Id, "000000", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var locked = await SendChallengeAsync(client, user.Id, "000000", ct);
        locked.StatusCode.ShouldBe((HttpStatusCode)423);

        locked.Headers.TryGetValues("Retry-After", out var retry).ShouldBeTrue();
        int.Parse(retry!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

        var body = await locked.Content.ReadFromJsonAsync<LockedBody>(ct);
        body.ShouldNotBeNull();
        body!.Error.ShouldBe("account_locked");
        body.RemainingSeconds.ShouldBeGreaterThan(0);
        body.LockedUntil.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task After_lockout_expires_next_call_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await SendChallengeAsync(client, user.Id, "000000", ct);
        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);

        Clock.Advance(Duration.FromSeconds(1801));

        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Partition_is_per_user_so_other_user_first_call_returns_401_not_423()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = await InsertUserAsync();
        var userB = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await SendChallengeAsync(client, userA.Id, "000000", ct);
        (await SendChallengeAsync(client, userA.Id, "000000", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);

        (await SendChallengeAsync(client, userB.Id, "000000", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Successful_challenge_clears_failed_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var secret = await EnableTotpAsync(user.Id);

        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 2; i++)
            (await PostChallengeAsync(client, "000000", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var goodCode = ComputeCode(secret, Clock.GetCurrentInstant());
        (await PostChallengeAsync(client, goodCode, ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Db.ChangeTracker.Clear();
        var row = await Db.AuthLockouts
            .SingleAsync(a => a.UserId == user.Id && a.Kind == AuthLockoutKinds.Totp, ct);
        row.FailedCount.ShouldBe((short)0);
        row.LockedUntil.ShouldBeNull();

        for (var i = 0; i < 3; i++)
            (await PostChallengeAsync(client, "000000", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await PostChallengeAsync(client, "000000", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);
    }

    [Fact]
    public async Task Config_override_changes_max_failures()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(new Dictionary<string, string?>
            {
                ["Lockout:Totp:MaxFailures"] = "2",
                ["RateLimiting:TotpChallenge:PermitLimit"] = "100",
            });
        using var client = factory.CreateClient();

        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);
    }

    [Fact]
    public async Task Rate_limit_wins_when_burst_exceeds_permit_limit_before_lockout()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(new Dictionary<string, string?>
            {
                ["Lockout:Totp:MaxFailures"] = "100",
                ["RateLimiting:TotpChallenge:PermitLimit"] = "3",
                ["RateLimiting:TotpChallenge:WindowSeconds"] = "300",
            });
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            (await SendChallengeAsync(client, user.Id, "000000", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Lockout_wins_when_threshold_lower_than_rate_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .WithRateLimitConfig(new Dictionary<string, string?>
            {
                ["Lockout:Totp:MaxFailures"] = "3",
                ["RateLimiting:TotpChallenge:PermitLimit"] = "100",
            });
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            (await SendChallengeAsync(client, user.Id, "000000", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await SendChallengeAsync(client, user.Id, "000000", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);
    }

    private async Task<string> EnableTotpAsync(Guid userId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory
            .WithTestAuth(userId, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();

        (await client.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative), new PassphraseInitRequest("hunter2hunter2"), ct))
            .EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(
            new Uri("/api/auth/unlock", UriKind.Relative), new PassphraseUnlockRequest("hunter2hunter2"), ct))
            .EnsureSuccessStatusCode();

        var init = await client.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        var initBody = (await init.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var code = ComputeCode(initBody.Secret, Clock.GetCurrentInstant());

        var verify = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/enable/verify", UriKind.Relative),
            new TotpEnableVerifyRequest(initBody.Secret, code),
            ct);
        verify.EnsureSuccessStatusCode();
        return initBody.Secret;
    }

    private static string ComputeCode(string secret, Instant at) =>
        new OtpNet.Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.ToDateTimeUtc());

    private static async Task<HttpResponseMessage> SendChallengeAsync(
        HttpClient client, Guid userId, string code, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/totp-challenge")
        {
            Content = JsonContent.Create(new TotpChallengeRequest(code)),
        };
        req.Headers.Add(RateLimitTestExtensions.SubUsHeader, userId.ToString());
        return await client.SendAsync(req, ct);
    }

    private static async Task<HttpResponseMessage> PostChallengeAsync(
        HttpClient client, string code, CancellationToken ct) =>
        await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest(code),
            ct);

    private sealed record LockedBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("locked_until")] string LockedUntil,
        [property: JsonPropertyName("remaining_seconds")] int RemainingSeconds);
}
