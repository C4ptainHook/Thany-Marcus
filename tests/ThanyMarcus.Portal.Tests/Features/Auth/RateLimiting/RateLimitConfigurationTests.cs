using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.RateLimiting;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.RateLimiting;

public sealed class RateLimitConfigurationTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
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
    public void Defaults_match_adr_0031_table()
    {
        var defaults = new RateLimitingOptions();
        defaults.TotpChallenge.PermitLimit.ShouldBe(5);
        defaults.TotpChallenge.WindowSeconds.ShouldBe(300);
        defaults.Unlock.PermitLimit.ShouldBe(5);
        defaults.Unlock.WindowSeconds.ShouldBe(300);
        defaults.SignInGoogle.PermitLimit.ShouldBe(20);
        defaults.SignInGoogle.WindowSeconds.ShouldBe(60);
    }

    [Fact]
    public void Options_bind_from_configuration_section()
    {
        using var client = Factory.CreateClient();
        var bound = Factory.Services.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        bound.TotpChallenge.PermitLimit.ShouldBe(5);
        bound.SignInGoogle.PermitLimit.ShouldBe(20);
    }

    [Fact]
    public async Task Config_override_changes_permit_limit()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var factory = Factory
            .WithPerRequestTestAuth(TotpClaimValues.NotVerified)
            .WithRateLimitConfig(new Dictionary<string, string?>
            {
                ["RateLimiting:TotpChallenge:PermitLimit"] = "2",
                ["RateLimiting:TotpChallenge:WindowSeconds"] = "300",
            });
        using var client = factory.CreateClient();

        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await SendChallengeAsync(client, user.Id, ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
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
}
