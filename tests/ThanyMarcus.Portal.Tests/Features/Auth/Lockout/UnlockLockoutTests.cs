using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Lockout;

public sealed class UnlockLockoutTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly Dictionary<string, string?> LockoutWinsConfig = new()
    {
        ["Lockout:Unlock:MaxFailures"] = "3",
        ["Lockout:Unlock:WindowSeconds"] = "3600",
        ["Lockout:Unlock:LockoutSeconds"] = "1800",
        ["RateLimiting:Unlock:PermitLimit"] = "100",
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
    public async Task Threshold_failures_lock_account_with_423()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.Verified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            (await SendUnlockAsync(client, user.Id, "wrong", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var locked = await SendUnlockAsync(client, user.Id, "wrong", ct);
        locked.StatusCode.ShouldBe((HttpStatusCode)423);

        locked.Headers.TryGetValues("Retry-After", out var retry).ShouldBeTrue();
        int.Parse(retry!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

        var body = await locked.Content.ReadFromJsonAsync<LockedBody>(ct);
        body!.Error.ShouldBe("account_locked");
        body.RemainingSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Partition_is_per_user_so_other_user_first_call_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = await InsertUserAsync();
        var userB = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.Verified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await SendUnlockAsync(client, userA.Id, "wrong", ct);
        (await SendUnlockAsync(client, userA.Id, "wrong", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);

        (await SendUnlockAsync(client, userB.Id, "wrong", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task After_lockout_expires_next_call_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.Verified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
            await SendUnlockAsync(client, user.Id, "wrong", ct);
        (await SendUnlockAsync(client, user.Id, "wrong", ct))
            .StatusCode.ShouldBe((HttpStatusCode)423);

        Clock.Advance(Duration.FromSeconds(1801));

        (await SendUnlockAsync(client, user.Id, "wrong", ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Successful_unlock_clears_failed_count()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var factory = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .WithRateLimitConfig(LockoutWinsConfig);
        using var client = factory.CreateClient();

        (await PostUnlockAsync(client, "hunter2hunter2", "/api/auth/passphrase/init", ct))
            .EnsureSuccessStatusCode();

        for (var i = 0; i < 2; i++)
            (await PostUnlockAsync(client, "wrong", "/api/auth/unlock", ct))
                .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        (await PostUnlockAsync(client, "hunter2hunter2", "/api/auth/unlock", ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        Db.ChangeTracker.Clear();
        var row = await Db.AuthLockouts
            .SingleAsync(a => a.UserId == user.Id && a.Kind == AuthLockoutKinds.Unlock, ct);
        row.FailedCount.ShouldBe((short)0);
        row.LockedUntil.ShouldBeNull();
    }

    private static async Task<HttpResponseMessage> SendUnlockAsync(
        HttpClient client, Guid userId, string passphrase, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/unlock")
        {
            Content = JsonContent.Create(new PassphraseUnlockRequest(passphrase)),
        };
        req.Headers.Add(RateLimitTestExtensions.SubUsHeader, userId.ToString());
        return await client.SendAsync(req, ct);
    }

    private static async Task<HttpResponseMessage> PostUnlockAsync(
        HttpClient client, string passphrase, string path, CancellationToken ct)
    {
        var body = path.EndsWith("/init", StringComparison.Ordinal)
            ? (object)new PassphraseInitRequest(passphrase)
            : new PassphraseUnlockRequest(passphrase);
        return await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body, ct);
    }

    private sealed record LockedBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("locked_until")] string LockedUntil,
        [property: JsonPropertyName("remaining_seconds")] int RemainingSeconds);
}
