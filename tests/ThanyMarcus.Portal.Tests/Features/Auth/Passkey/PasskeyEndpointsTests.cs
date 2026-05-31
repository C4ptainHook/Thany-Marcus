using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Passkey;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Passkey;

public sealed class PasskeyEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly Guid AppleAaguid = new("fbfc3007-154e-4ecc-8c0b-6e020557d7bd");

    // ---- Register ----

    [Fact]
    public async Task Register_challenge_requires_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithUnauthenticated().CreateClient();
        var resp = await client.PostAsync(Url("/api/auth/passkey/register/challenge"), content: null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Register_challenge_returns_options_and_challenge_id()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = AuthedClient(user.Id, new FakeFido2());

        var resp = await client.PostAsync(Url("/api/auth/passkey/register/challenge"), content: null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Guid.TryParse(body.GetProperty("challengeId").GetString(), out _).ShouldBeTrue();
        body.GetProperty("options").GetProperty("challenge").GetString().ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Register_complete_persists_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var credId = new byte[] { 11, 22, 33, 44 };
        var fake = new FakeFido2
        {
            OnMakeNewCredential = _ => new RegisteredPublicKeyCredential
            {
                Id = credId,
                PublicKey = new byte[] { 1, 2, 3, 4, 5 },
                SignCount = 0,
                AaGuid = AppleAaguid,
                Transports = [AuthenticatorTransport.Internal],
                IsBackedUp = true,
            },
        };
        using var client = AuthedClient(user.Id, fake);

        var challengeId = await StartRegisterChallengeAsync(client);
        var resp = await client.PostAsync(
            Url("/api/auth/passkey/register/complete"),
            JsonBody(AttestationBody(challengeId, credId)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var row = await Db.PasskeyCredentials.SingleAsync(p => p.UserId == user.Id, ct);
        row.CredentialId.ShouldBe(credId);
        row.AuthenticatorName.ShouldBe("iCloud Keychain");
        row.Aaguid.ShouldBe(AppleAaguid);
        row.BackedUp.ShouldBeTrue();
        row.Transports.ShouldContain("internal");
        row.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Register_complete_rejects_unknown_challenge()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = AuthedClient(user.Id, new FakeFido2 { OnMakeNewCredential = _ => throw new InvalidOperationException("should not run")});

        var resp = await client.PostAsync(
            Url("/api/auth/passkey/register/complete"),
            JsonBody(AttestationBody(Guid.NewGuid().ToString(), new byte[] { 1, 2, 3 })),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_complete_rejects_duplicate_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var credId = new byte[] { 7, 7, 7, 7 };
        await InsertCredentialAsync(user.Id, credId);

        var fake = new FakeFido2 { OnMakeNewCredential = _ => throw new InvalidOperationException("should not run")};
        using var client = AuthedClient(user.Id, fake);

        var challengeId = await StartRegisterChallengeAsync(client);
        var resp = await client.PostAsync(
            Url("/api/auth/passkey/register/complete"),
            JsonBody(AttestationBody(challengeId, credId)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    // ---- Login ----

    [Fact]
    public async Task Login_challenge_is_anonymous()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(new FakeFido2());
        var resp = await client.PostAsync(Url("/api/auth/passkey/login/challenge"), content: null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        Guid.TryParse(body.GetProperty("challengeId").GetString(), out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Login_complete_signs_in_and_updates_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var credId = new byte[] { 5, 5, 5, 5 };
        await InsertCredentialAsync(user.Id, credId, signCount: 3);

        var fake = new FakeFido2
        {
            OnMakeAssertion = _ => new VerifyAssertionResult { CredentialId = credId, SignCount = 7, IsBackedUp = true },
        };
        using var client = AnonClient(fake);

        var challengeId = await StartLoginChallengeAsync(client);
        var resp = await client.PostAsync(
            Url("/api/auth/passkey/login/complete"),
            JsonBody(AssertionBody(challengeId, credId, user.Id)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(".Portal.Auth=", StringComparison.Ordinal)).ShouldBeTrue();

        var row = await Db.PasskeyCredentials.SingleAsync(p => p.CredentialId == credId, ct);
        row.SignCount.ShouldBe(7);
        row.LastUsedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Login_complete_with_totp_enabled_still_signs_in()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync(totpEnabled: true);
        var credId = new byte[] { 6, 6, 6, 6 };
        await InsertCredentialAsync(user.Id, credId);

        var fake = new FakeFido2
        {
            OnMakeAssertion = _ => new VerifyAssertionResult { CredentialId = credId, SignCount = 1, IsBackedUp = false },
        };
        using var client = AnonClient(fake);

        var challengeId = await StartLoginChallengeAsync(client);
        var resp = await client.PostAsync(
            Url("/api/auth/passkey/login/complete"),
            JsonBody(AssertionBody(challengeId, credId, user.Id)),
            ct);

        // Session is issued; TOTP gating happens via the Totp claim (pending_totp), same path as Google SSO.
        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        resp.Headers.GetValues("Set-Cookie")
            .Any(c => c.StartsWith(".Portal.Auth=", StringComparison.Ordinal)).ShouldBeTrue();
    }

    [Fact]
    public async Task Login_complete_rejects_unknown_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = AnonClient(new FakeFido2());
        var challengeId = await StartLoginChallengeAsync(client);

        var resp = await client.PostAsync(
            Url("/api/auth/passkey/login/complete"),
            JsonBody(AssertionBody(challengeId, new byte[] { 9, 8, 7 }, Guid.NewGuid())),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_complete_rejects_revoked_credential()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var credId = new byte[] { 4, 3, 2, 1 };
        await InsertCredentialAsync(user.Id, credId, revoked: true);

        var fake = new FakeFido2
        {
            OnMakeAssertion = _ => new VerifyAssertionResult { CredentialId = credId, SignCount = 1, IsBackedUp = false },
        };
        using var client = AnonClient(fake);
        var challengeId = await StartLoginChallengeAsync(client);

        var resp = await client.PostAsync(
            Url("/api/auth/passkey/login/complete"),
            JsonBody(AssertionBody(challengeId, credId, user.Id)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_complete_rejects_invalid_assertion()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var credId = new byte[] { 2, 2, 2, 2 };
        await InsertCredentialAsync(user.Id, credId);

        // OnMakeAssertion left null -> fake throws Fido2VerificationException.
        using var client = AnonClient(new FakeFido2());
        var challengeId = await StartLoginChallengeAsync(client);

        var resp = await client.PostAsync(
            Url("/api/auth/passkey/login/complete"),
            JsonBody(AssertionBody(challengeId, credId, user.Id)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ---- List ----

    [Fact]
    public async Task List_returns_only_own_active_credentials_sorted_by_recency()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await InsertUserAsync();
        var bob = await InsertUserAsync();
        var t0 = Clock.GetCurrentInstant();
        await InsertCredentialAsync(alice.Id, [1], createdAt: t0, name: "Older");
        await InsertCredentialAsync(alice.Id, [2], createdAt: t0 + Duration.FromMinutes(5), name: "Newer");
        await InsertCredentialAsync(alice.Id, [3], revoked: true, name: "Revoked");
        await InsertCredentialAsync(bob.Id, [4], name: "Bob's");

        using var client = Factory.WithTestAuth(alice.Id).WithClock(Clock).CreateClient();
        var resp = await client.GetAsync(Url("/api/auth/passkeys"), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var list = (await resp.Content.ReadFromJsonAsync<List<PasskeyDto>>(ct))!;

        list.Count.ShouldBe(2);
        list[0].AuthenticatorName.ShouldBe("Newer");
        list[1].AuthenticatorName.ShouldBe("Older");
    }

    // ---- Revoke ----

    [Fact]
    public async Task Revoke_own_credential_soft_deletes_it()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var cred = await InsertCredentialAsync(user.Id, [1, 2]);

        using var client = Factory.WithTestAuth(user.Id).WithClock(Clock).CreateClient();
        var resp = await client.DeleteAsync(Url($"/api/auth/passkeys/{cred.Id}"), ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var row = await Db.PasskeyCredentials.SingleAsync(p => p.Id == cred.Id, ct);
        row.RevokedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Revoke_other_users_credential_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await InsertUserAsync();
        var bob = await InsertUserAsync();
        var bobsCred = await InsertCredentialAsync(bob.Id, [9, 9]);

        using var client = Factory.WithTestAuth(alice.Id).WithClock(Clock).CreateClient();
        var resp = await client.DeleteAsync(Url($"/api/auth/passkeys/{bobsCred.Id}"), ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var row = await Db.PasskeyCredentials.SingleAsync(p => p.Id == bobsCred.Id, ct);
        row.RevokedAt.ShouldBeNull();
    }

    // ---- Helpers ----

    private HttpClient AuthedClient(Guid userId, FakeFido2 fake)
        => Factory.WithTestAuth(userId).WithClock(Clock).WithFido2(fake).CreateClient();

    private HttpClient AnonClient(FakeFido2 fake)
        => Factory.WithUnauthenticated().WithClock(Clock).WithFido2(fake).CreateClient();

    private static async Task<string> StartRegisterChallengeAsync(HttpClient client)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await client.PostAsync(Url("/api/auth/passkey/register/challenge"), content: null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("challengeId").GetString()!;
    }

    private static async Task<string> StartLoginChallengeAsync(HttpClient client)
    {
        var ct = TestContext.Current.CancellationToken;
        var resp = await client.PostAsync(Url("/api/auth/passkey/login/challenge"), content: null, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(ct);
        return body.GetProperty("challengeId").GetString()!;
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

    private static string AssertionBody(string challengeId, byte[] credId, Guid userId)
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
                    ["authenticatorData"] = B64Url(dummy),
                    ["signature"] = B64Url(dummy),
                    ["clientDataJSON"] = B64Url(dummy),
                    ["userHandle"] = B64Url(userId.ToByteArray()),
                },
                ["clientExtensionResults"] = new JsonObject(),
            },
        };
        return body.ToJsonString();
    }

    private async Task<User> InsertUserAsync(bool totpEnabled = false)
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
        if (totpEnabled)
        {
            Db.TotpSecrets.Add(new TotpSecret
            {
                UserId = user.Id,
                Ciphertext = [1],
                Nonce = new byte[12],
                Tag = new byte[16],
                EnabledAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    private async Task<PasskeyCredential> InsertCredentialAsync(
        Guid userId,
        byte[] credentialId,
        bool revoked = false,
        long signCount = 0,
        Instant? createdAt = null,
        string? name = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = createdAt ?? Clock.GetCurrentInstant();
        var cred = new PasskeyCredential
        {
            UserId = userId,
            CredentialId = credentialId,
            PublicKey = [9, 9, 9],
            SignCount = signCount,
            Aaguid = Guid.Empty,
            AuthenticatorName = name ?? "Test Key",
            Transports = ["internal"],
            BackedUp = false,
            CreatedAt = now,
            UpdatedAt = now,
            RevokedAt = revoked ? now : null,
        };
        Db.PasskeyCredentials.Add(cred);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return cred;
    }
}
