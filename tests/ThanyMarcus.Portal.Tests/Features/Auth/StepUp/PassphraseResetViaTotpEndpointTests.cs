using System.Net;
using System.Net.Http.Json;
using NodaTime;
using OtpNet;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class PassphraseResetViaTotpEndpointTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private sealed record KitResponse(string RecoveryString, string QrPngDataUri);

    private static readonly Uri InitUri = new("/api/auth/passphrase/init", UriKind.Relative);
    private static readonly Uri UnlockUri = new("/api/auth/unlock", UriKind.Relative);
    private static readonly Uri EnableInitUri = new("/api/auth/totp/enable/init", UriKind.Relative);
    private static readonly Uri EnableVerifyUri = new("/api/auth/totp/enable/verify", UriKind.Relative);
    private static readonly Uri GenerateKitUri = new("/api/auth/emergency-kit/generate", UriKind.Relative);
    private static readonly Uri ResetViaTotpUri = new("/api/auth/passphrase/reset-via-totp", UriKind.Relative);
    private static readonly Uri ResetViaKitUri = new("/api/auth/passphrase/reset-via-kit", UriKind.Relative);

    private async Task<Guid> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"sub-{Guid.NewGuid()}",
            Email = $"u-{Guid.NewGuid():N}@x.com",
            Name = "Alice",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user.Id;
    }

    private HttpClient Client(Guid userId) =>
        Factory.WithTestAuth(userId, totp: TotpClaimValues.Verified).WithClock(Clock).CreateClient();

    private static string ComputeCode(string secret, Instant at) =>
        new OtpNet.Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.ToDateTimeUtc());

    private async Task<string> EnableTotpAsync(HttpClient client)
    {
        var ct = TestContext.Current.CancellationToken;
        var init = (await (await client.PostAsync(EnableInitUri, null, ct))
            .Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var code = ComputeCode(init.Secret, Clock.GetCurrentInstant());
        (await client.PostAsJsonAsync(EnableVerifyUri, new TotpEnableVerifyRequest(init.Secret, code), ct))
            .EnsureSuccessStatusCode();
        return init.Secret;
    }

    // Passphrase set + unlocked + TOTP enabled while unlocked => the DEK is wrapped under the secret.
    private async Task<string> SetupWithTotpRecoveryAsync(HttpClient client, string passphrase)
    {
        var ct = TestContext.Current.CancellationToken;
        (await client.PostAsJsonAsync(InitUri, new PassphraseInitRequest(passphrase), ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest(passphrase), ct)).EnsureSuccessStatusCode();
        return await EnableTotpAsync(client);
    }

    [Fact]
    public async Task Reset_with_valid_totp_rewraps_dek_so_new_passphrase_unlocks()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        var secret = await SetupWithTotpRecoveryAsync(client, "hunter2hunter2");

        var code = ComputeCode(secret, Clock.GetCurrentInstant());
        var reset = await client.PostAsJsonAsync(
            ResetViaTotpUri, new ResetViaTotpRequest(code, "brand-new-pass-9"), ct);
        reset.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest("brand-new-pass-9"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest("hunter2hunter2"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_with_wrong_totp_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await SetupWithTotpRecoveryAsync(client, "hunter2hunter2");

        var reset = await client.PostAsJsonAsync(
            ResetViaTotpUri, new ResetViaTotpRequest("000000", "brand-new-pass-9"), ct);

        reset.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest("hunter2hunter2"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_with_weak_new_passphrase_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        var secret = await SetupWithTotpRecoveryAsync(client, "hunter2hunter2");

        var code = ComputeCode(secret, Clock.GetCurrentInstant());
        var reset = await client.PostAsJsonAsync(
            ResetViaTotpUri, new ResetViaTotpRequest(code, "password"), ct);

        reset.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Enabling_totp_while_locked_is_refused_so_no_half_created_recovery()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);

        (await client.PostAsJsonAsync(InitUri, new PassphraseInitRequest("hunter2hunter2"), ct)).EnsureSuccessStatusCode();

        var init = (await (await client.PostAsync(EnableInitUri, null, ct))
            .Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var code = ComputeCode(init.Secret, Clock.GetCurrentInstant());
        var verify = await client.PostAsJsonAsync(
            EnableVerifyUri, new TotpEnableVerifyRequest(init.Secret, code), ct);

        verify.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest("hunter2hunter2"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Reset_via_totp_does_not_consume_the_emergency_kit()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        var secret = await SetupWithTotpRecoveryAsync(client, "hunter2hunter2");
        var kit = (await (await client.PostAsync(GenerateKitUri, null, ct)).Content.ReadFromJsonAsync<KitResponse>(ct))!;

        var code = ComputeCode(secret, Clock.GetCurrentInstant());
        (await client.PostAsJsonAsync(ResetViaTotpUri, new ResetViaTotpRequest(code, "brand-new-pass-9"), ct))
            .EnsureSuccessStatusCode();

        // The kit is still valid: it can still be redeemed afterwards.
        var redeem = await client.PostAsJsonAsync(
            ResetViaKitUri, new ResetViaKitRequest(kit.RecoveryString, "yet-another-pass-2"), ct);
        redeem.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
