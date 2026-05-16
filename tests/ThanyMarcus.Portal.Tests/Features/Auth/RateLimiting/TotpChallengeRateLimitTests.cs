using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.RateLimiting;

public sealed class TotpChallengeRateLimitTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
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

    [Fact]
    public async Task Sixth_request_returns_429_with_retry_after_header_and_body()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory.WithPerRequestTestAuth(TotpClaimValues.NotVerified);
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
        {
            var ok = await SendChallengeAsync(client, user.Id, ct);
            ok.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var rejected = await SendChallengeAsync(client, user.Id, ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        rejected.Headers.TryGetValues("Retry-After", out var retryHeader).ShouldBeTrue();
        int.Parse(retryHeader!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

        var body = await rejected.Content.ReadFromJsonAsync<RateLimitedBody>(ct);
        body.ShouldNotBeNull();
        body!.Error.ShouldBe("rate_limited");
        body.RetryAfterSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Partition_is_per_user_so_other_user_first_call_admitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = await InsertUserAsync();
        var userB = await InsertUserAsync();
        var factory = Factory.WithPerRequestTestAuth(TotpClaimValues.NotVerified);
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
            (await SendChallengeAsync(client, userA.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, userA.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        var userBFirst = await SendChallengeAsync(client, userB.Id, ct);
        userBFirst.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task After_window_expires_partition_admits_again()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithRateLimitConfig(new Dictionary<string, string?>
            {
                ["RateLimiting:TotpChallenge:PermitLimit"] = "2",
                ["RateLimiting:TotpChallenge:WindowSeconds"] = "1",
            });
        using var client = factory.CreateClient();

        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        await Task.Delay(TimeSpan.FromMilliseconds(1500), ct);

        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> SendChallengeAsync(
        HttpClient client, Guid userId, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/totp-challenge")
        {
            Content = JsonContent.Create(new TotpChallengeRequest("000000")),
        };
        req.Headers.Add(RateLimitTestExtensions.SubUsHeader, userId.ToString());
        return await client.SendAsync(req, ct);
    }

    private sealed record RateLimitedBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("retry_after_seconds")] int RetryAfterSeconds);
}
