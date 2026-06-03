using System.Net;
using System.Net.Http.Json;
using OtpNet;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Totp;

public sealed class TotpChallengeFailureTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private async Task<User> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = "alice@example.com",
            Name = "Alice",
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
    public async Task Challenge_unauthenticated_returns_401_or_302()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var response = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest("123456"),
            ct);

        ((int)response.StatusCode).ShouldBeOneOf(401, 302);
    }

    [Fact]
    public async Task Challenge_with_no_totp_secret_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest("123456"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Challenge_with_wrong_code_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var totp = new OtpNet.Totp(KeyGeneration.GenerateRandomKey(20));
        var secretBytes = KeyGeneration.GenerateRandomKey(20);
        var secret = Base32Encoding.ToString(secretBytes);

        using var enableClient = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();

        (await enableClient.PostAsJsonAsync(
            new Uri("/api/auth/passphrase/init", UriKind.Relative), new PassphraseInitRequest("hunter2hunter2"), ct))
            .EnsureSuccessStatusCode();
        (await enableClient.PostAsJsonAsync(
            new Uri("/api/auth/unlock", UriKind.Relative), new PassphraseUnlockRequest("hunter2hunter2"), ct))
            .EnsureSuccessStatusCode();

        var init = await enableClient.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        var initBody = (await init.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var validCode = new OtpNet.Totp(Base32Encoding.ToBytes(initBody.Secret))
            .ComputeTotp(Clock.GetCurrentInstant().ToDateTimeUtc());
        var verify = await enableClient.PostAsJsonAsync(
            new Uri("/api/auth/totp/enable/verify", UriKind.Relative),
            new TotpEnableVerifyRequest(initBody.Secret, validCode),
            ct);
        verify.EnsureSuccessStatusCode();

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest("000000"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
