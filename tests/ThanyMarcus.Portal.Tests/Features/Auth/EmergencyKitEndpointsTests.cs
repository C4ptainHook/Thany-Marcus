using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class EmergencyKitEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private sealed record KitResponse(string RecoveryString, string QrPngDataUri);

    private static readonly Uri InitUri = new("/api/auth/passphrase/init", UriKind.Relative);
    private static readonly Uri UnlockUri = new("/api/auth/unlock", UriKind.Relative);
    private static readonly Uri GenerateUri = new("/api/auth/emergency-kit/generate", UriKind.Relative);
    private static readonly Uri StatusUri = new("/api/auth/emergency-kit", UriKind.Relative);
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

    private static async Task UnlockAsync(HttpClient client, string passphrase)
    {
        var ct = TestContext.Current.CancellationToken;
        (await client.PostAsJsonAsync(InitUri, new PassphraseInitRequest(passphrase), ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest(passphrase), ct)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Generate_without_step_up_unlock_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);

        var res = await client.PostAsync(GenerateUri, content: null, ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Generate_when_unlocked_returns_kit_and_persists_single_active_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");

        var res = await client.PostAsync(GenerateUri, content: null, ct);
        res.StatusCode.ShouldBe(HttpStatusCode.OK);
        var kit = (await res.Content.ReadFromJsonAsync<KitResponse>(ct))!;

        kit.RecoveryString.Split(' ').Length.ShouldBe(8);
        kit.QrPngDataUri.ShouldStartWith("data:image/png;base64,");
        (await Db.EmergencyKits.CountAsync(k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null, ct))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Generate_twice_revokes_the_previous_kit()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");

        var first = (await (await client.PostAsync(GenerateUri, null, ct)).Content.ReadFromJsonAsync<KitResponse>(ct))!;
        var second = (await (await client.PostAsync(GenerateUri, null, ct)).Content.ReadFromJsonAsync<KitResponse>(ct))!;

        second.RecoveryString.ShouldNotBe(first.RecoveryString);
        (await Db.EmergencyKits.CountAsync(k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null, ct))
            .ShouldBe(1);
        (await Db.EmergencyKits.CountAsync(k => k.UserId == userId && k.RevokedAt != null, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Status_reports_generated_at_and_no_totp_recovery_for_passphrase_only_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");
        (await client.PostAsync(GenerateUri, null, ct)).EnsureSuccessStatusCode();

        var status = await (await client.GetAsync(StatusUri, ct)).Content.ReadFromJsonAsync<JsonElement>(ct);

        status.GetProperty("generatedAt").ValueKind.ShouldNotBe(JsonValueKind.Null);
        status.GetProperty("lastUsedAt").ValueKind.ShouldBe(JsonValueKind.Null);
        status.GetProperty("totpRecoveryAvailable").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task Reset_via_kit_redeems_sets_new_passphrase_and_issues_a_fresh_kit()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");
        var original = (await (await client.PostAsync(GenerateUri, null, ct)).Content.ReadFromJsonAsync<KitResponse>(ct))!;

        var reset = await client.PostAsJsonAsync(
            ResetViaKitUri, new ResetViaKitRequest(original.RecoveryString, "brand-new-pass-9"), ct);
        reset.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fresh = (await reset.Content.ReadFromJsonAsync<KitResponse>(ct))!;
        fresh.RecoveryString.ShouldNotBe(original.RecoveryString);

        // The new passphrase unlocks; the old one does not.
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest("brand-new-pass-9"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest("hunter2hunter2"), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Exactly one active kit, and the redeemed one is marked used.
        (await Db.EmergencyKits.CountAsync(k => k.UserId == userId && k.UsedAt == null && k.RevokedAt == null, ct))
            .ShouldBe(1);
        (await Db.EmergencyKits.CountAsync(k => k.UserId == userId && k.UsedAt != null, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task Reset_via_kit_with_the_redeemed_string_again_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");
        var original = (await (await client.PostAsync(GenerateUri, null, ct)).Content.ReadFromJsonAsync<KitResponse>(ct))!;

        (await client.PostAsJsonAsync(ResetViaKitUri, new ResetViaKitRequest(original.RecoveryString, "brand-new-pass-9"), ct))
            .EnsureSuccessStatusCode();

        var reused = await client.PostAsJsonAsync(
            ResetViaKitUri, new ResetViaKitRequest(original.RecoveryString, "another-new-pass-1"), ct);
        reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_via_kit_with_an_invalid_recovery_string_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");
        (await client.PostAsync(GenerateUri, null, ct)).EnsureSuccessStatusCode();

        var res = await client.PostAsJsonAsync(
            ResetViaKitUri,
            new ResetViaKitRequest("abandon abandon abandon abandon abandon abandon abandon abandon", "brand-new-pass-9"),
            ct);

        res.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_via_kit_rejects_a_weak_new_passphrase()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = await InsertUserAsync();
        using var client = Client(userId);
        await UnlockAsync(client, "hunter2hunter2");
        var original = (await (await client.PostAsync(GenerateUri, null, ct)).Content.ReadFromJsonAsync<KitResponse>(ct))!;

        var res = await client.PostAsJsonAsync(
            ResetViaKitUri, new ResetViaKitRequest(original.RecoveryString, "short"), ct);

        res.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
