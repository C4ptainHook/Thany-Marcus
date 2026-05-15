using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.RateLimiting;

public sealed class UnlockRateLimitTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
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
    public async Task Sixth_request_returns_429_with_body_and_retry_after()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory.WithPerRequestTestAuth(TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
            (await SendUnlockAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var rejected = await SendUnlockAsync(client, user.Id, ct);
        rejected.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        rejected.Headers.TryGetValues("Retry-After", out var retryHeader).ShouldBeTrue();
        int.Parse(retryHeader!.Single(), CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

        var body = await rejected.Content.ReadFromJsonAsync<RateLimitedBody>(ct);
        body!.Error.ShouldBe("rate_limited");
        body.RetryAfterSeconds.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Partition_is_per_user_so_other_user_first_call_admitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = await InsertUserAsync();
        var userB = await InsertUserAsync();
        var factory = Factory.WithPerRequestTestAuth(TotpClaimValues.Verified).WithClock(Clock);
        using var client = factory.CreateClient();

        for (var i = 0; i < 5; i++)
            (await SendUnlockAsync(client, userA.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendUnlockAsync(client, userA.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        (await SendUnlockAsync(client, userB.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpResponseMessage> SendUnlockAsync(
        HttpClient client, Guid userId, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/unlock")
        {
            Content = JsonContent.Create(new PassphraseUnlockRequest("wrong")),
        };
        req.Headers.Add(RateLimitTestExtensions.SubUsHeader, userId.ToString());
        return await client.SendAsync(req, ct);
    }

    private sealed record RateLimitedBody(
        [property: JsonPropertyName("error")] string Error,
        [property: JsonPropertyName("retry_after_seconds")] int RetryAfterSeconds);
}
