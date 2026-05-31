using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Captcha;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Passkey;

public sealed class PasskeySignupEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly Guid AppleAaguid = new("fbfc3007-154e-4ecc-8c0b-6e020557d7bd");

    [Fact]
    public async Task Signup_challenge_returns_options_and_challenge_id()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(new FakeFido2());

        var resp = await Challenge(client, "newbie");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Guid.TryParse(body.GetProperty("challengeId").GetString(), out _).ShouldBeTrue();
        body.GetProperty("options").GetProperty("challenge").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Signup_happy_path_creates_user_and_passkey_and_signs_in()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(EchoingFido2());

        var challengeId = await StartChallenge(client, "demo_user");
        var credId = new byte[] { 1, 2, 3, 4 };
        var resp = await client.PostAsync(
            Url("/api/auth/passkey/signup/complete"),
            JsonBody(AttestationBody(challengeId, credId)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(".Portal.Auth=", StringComparison.Ordinal)).ShouldBeTrue();

        var user = await Db.Users.SingleAsync(u => u.Username == "demo_user", ct);
        user.Email.ShouldBeNull();
        user.GoogleSubject.ShouldBeNull();
        user.Name.ShouldBe("demo_user");

        var cred = await Db.PasskeyCredentials.SingleAsync(p => p.UserId == user.Id, ct);
        cred.CredentialId.ShouldBe(credId);
        cred.AuthenticatorName.ShouldBe("iCloud Keychain");
    }

    [Theory]
    [InlineData("admin", "username_reserved")]
    [InlineData("ab", "username_length")]
    [InlineData("alice@bob", "username_chars")]
    [InlineData("_alice", "username_prefix")]
    public async Task Signup_challenge_rejects_invalid_username(string username, string expectedError)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(new FakeFido2());

        var resp = await Challenge(client, username);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("error").GetString().ShouldBe(expectedError);
    }

    [Fact]
    public async Task Signup_challenge_requires_acknowledgement()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(new FakeFido2());

        var resp = await Challenge(client, "willing", acknowledged: false);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("error").GetString().ShouldBe("acknowledgement_required");
    }

    [Fact]
    public async Task Signup_challenge_rejects_taken_username_case_insensitively()
    {
        var ct = TestContext.Current.CancellationToken;
        await InsertUserAsync(username: "Taken");
        using var client = AnonClient(new FakeFido2());

        var resp = await Challenge(client, "taken");
        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("error").GetString().ShouldBe("username_taken");
    }

    [Fact]
    public async Task Signup_challenge_rejects_invalid_captcha()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory
            .WithUnauthenticated()
            .WithClock(Clock)
            .WithFido2(new FakeFido2())
            .WithTurnstileValidator(new FailingTurnstile())
            .CreateClient();

        var resp = await Challenge(client, "blocked");
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        body.GetProperty("error").GetString().ShouldBe("captcha_invalid");
    }

    [Fact]
    public async Task Signup_complete_rejects_unknown_challenge()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(EchoingFido2());

        var resp = await client.PostAsync(
            Url("/api/auth/passkey/signup/complete"),
            JsonBody(AttestationBody(Guid.NewGuid().ToString(), new byte[] { 9 })),
            ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Concurrent_same_username_signups_only_one_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(EchoingFido2());

        // Two challenges for the same username both pass (no user exists yet), then both
        // completions race — the unique index on lower(username) lets exactly one through.
        var firstChallenge = await StartChallenge(client, "raceme");
        var secondChallenge = await StartChallenge(client, "raceme");

        var first = client.PostAsync(
            Url("/api/auth/passkey/signup/complete"),
            JsonBody(AttestationBody(firstChallenge, [10, 10])), ct);
        var second = client.PostAsync(
            Url("/api/auth/passkey/signup/complete"),
            JsonBody(AttestationBody(secondChallenge, [20, 20])), ct);
        var results = await Task.WhenAll(first, second);

        results.Count(r => r.StatusCode == HttpStatusCode.NoContent).ShouldBe(1);
        results.Count(r => r.StatusCode == HttpStatusCode.Conflict).ShouldBe(1);
        (await Db.Users.CountAsync(u => u.Username == "raceme", ct)).ShouldBe(1);
    }

    // ---- Helpers ----

    private HttpClient AnonClient(IFido2 fake)
        => Factory.WithUnauthenticated().WithClock(Clock).WithFido2(fake).CreateClient();

    private static FakeFido2 EchoingFido2() => new()
    {
        // Echo the credential id the test sends, so concurrent completions stay distinct and the
        // username index — not the credential-id index — decides the race.
        OnMakeNewCredential = p => new RegisteredPublicKeyCredential
        {
            Id = p.AttestationResponse.RawId,
            PublicKey = [1, 2, 3, 4, 5],
            SignCount = 0,
            AaGuid = AppleAaguid,
            Transports = [AuthenticatorTransport.Internal],
            IsBackedUp = true,
        },
    };

    private static Task<HttpResponseMessage> Challenge(HttpClient client, string username, bool acknowledged = true)
    {
        var body = new JsonObject
        {
            ["username"] = username,
            ["acknowledgedNoRecovery"] = acknowledged,
            ["turnstileToken"] = null,
        };
        return client.PostAsync(Url("/api/auth/passkey/signup/challenge"), JsonBody(body.ToJsonString()),
            TestContext.Current.CancellationToken);
    }

    private static async Task<string> StartChallenge(HttpClient client, string username)
    {
        var resp = await Challenge(client, username);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return body.GetProperty("challengeId").GetString()!;
    }

    private async Task<User> InsertUserAsync(string username)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = $"u-{Guid.NewGuid():N}@x.com",
            Username = username,
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

    private static Uri Url(string path) => new(path, UriKind.Relative);

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    private static string B64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string AttestationBody(string challengeId, byte[] credId)
    {
        var dummy = new byte[] { 1, 2, 3, 4 };
        var body = new JsonObject
        {
            ["challengeId"] = challengeId,
            ["response"] = new JsonObject
            {
                ["id"] = B64Url(credId),
                ["rawId"] = B64Url(credId),
                ["type"] = "public-key",
                ["response"] = new JsonObject
                {
                    ["attestationObject"] = B64Url(dummy),
                    ["clientDataJSON"] = B64Url(dummy),
                    ["transports"] = new JsonArray("internal"),
                },
                ["clientExtensionResults"] = new JsonObject(),
            },
        };
        return body.ToJsonString();
    }

    private sealed class FailingTurnstile : ITurnstileValidator
    {
        public Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct)
            => Task.FromResult(new TurnstileVerifyResult(false, ["bad-token"]));
    }
}

internal static class TurnstileTestExtensions
{
    public static WebApplicationFactory<Program> WithTurnstileValidator(
        this WebApplicationFactory<Program> factory, ITurnstileValidator validator)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ITurnstileValidator>();
            s.AddSingleton(validator);
        }));
}
