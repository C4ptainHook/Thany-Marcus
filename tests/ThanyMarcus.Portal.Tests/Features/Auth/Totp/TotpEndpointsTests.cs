using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using OtpNet;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Totp;

public sealed class TotpEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private static readonly Uri PassphraseInitUri = new("/api/auth/passphrase/init", UriKind.Relative);
    private static readonly Uri UnlockUri = new("/api/auth/unlock", UriKind.Relative);

    private static async Task UnlockAsync(HttpClient client, string passphrase = "hunter2hunter2")
    {
        var ct = TestContext.Current.CancellationToken;
        (await client.PostAsJsonAsync(PassphraseInitUri, new PassphraseInitRequest(passphrase), ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync(UnlockUri, new PassphraseUnlockRequest(passphrase), ct)).EnsureSuccessStatusCode();
    }

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

    private static string ComputeCode(string secret, Instant at) =>
        new OtpNet.Totp(Base32Encoding.ToBytes(secret)).ComputeTotp(at.ToDateTimeUtc());

    [Fact]
    public async Task Enable_init_returns_secret_and_qr_png_data_uri()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();

        var response = await client.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct);
        body.ShouldNotBeNull();
        body!.Secret.ShouldNotBeNullOrWhiteSpace();
        Base32Encoding.ToBytes(body.Secret).Length.ShouldBe(20);
        body.QrPngDataUri.ShouldStartWith("data:image/png;base64,");
    }

    [Fact]
    public async Task Enable_verify_round_trip_writes_secret_backup_codes_and_recovery_wrapper()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();
        await UnlockAsync(client);

        var init = await client.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        var initBody = (await init.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var code = ComputeCode(initBody.Secret, Clock.GetCurrentInstant());

        var verify = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/enable/verify", UriKind.Relative),
            new TotpEnableVerifyRequest(initBody.Secret, code),
            ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var verifyBody = (await verify.Content.ReadFromJsonAsync<TotpEnableVerifyResponse>(ct))!;
        verifyBody.BackupCodes.Count.ShouldBe(8);
        verifyBody.BackupCodes.Distinct().Count().ShouldBe(8);

        var secret = await Db.TotpSecrets.SingleAsync(t => t.UserId == user.Id, ct);
        secret.EnabledAt.ShouldNotBeNull();
        secret.DisabledAt.ShouldBeNull();
        (await Db.TotpBackupCodes.CountAsync(b => b.UserId == user.Id, ct)).ShouldBe(8);

        var account = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        account.TotpWrappedDek.ShouldNotBeNull();
    }

    [Fact]
    public async Task Enable_verify_without_step_up_returns_401_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();

        var init = await client.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        var initBody = (await init.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var code = ComputeCode(initBody.Secret, Clock.GetCurrentInstant());

        var verify = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/enable/verify", UriKind.Relative),
            new TotpEnableVerifyRequest(initBody.Secret, code),
            ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await Db.TotpSecrets.AnyAsync(t => t.UserId == user.Id, ct)).ShouldBeFalse();
        (await Db.TotpBackupCodes.AnyAsync(b => b.UserId == user.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Enable_verify_with_bad_code_returns_400_and_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();
        await UnlockAsync(client);

        var init = await client.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        var initBody = (await init.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;

        var verify = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/enable/verify", UriKind.Relative),
            new TotpEnableVerifyRequest(initBody.Secret, "000000"),
            ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Db.TotpSecrets.AnyAsync(t => t.UserId == user.Id, ct)).ShouldBeFalse();
        (await Db.TotpBackupCodes.AnyAsync(b => b.UserId == user.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Challenge_with_valid_totp_code_returns_204()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var (_, secret) = await EnableAsync(user.Id);

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var code = ComputeCode(secret, Clock.GetCurrentInstant());
        var response = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest(code),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Challenge_with_backup_code_marks_used_and_blocks_reuse()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var (backupCodes, _) = await EnableAsync(user.Id);

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var first = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest(backupCodes[0]),
            ct);
        first.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var used = await Db.TotpBackupCodes
            .CountAsync(b => b.UserId == user.Id && b.UsedAt != null, ct);
        used.ShouldBe(1);

        var reused = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest(backupCodes[0]),
            ct);
        reused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var second = await client.PostAsJsonAsync(
            new Uri("/totp-challenge", UriKind.Relative),
            new TotpChallengeRequest(backupCodes[1]),
            ct);
        second.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Disable_with_valid_totp_code_sets_disabled_at_and_purges_backup_codes()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var (_, secret) = await EnableAsync(user.Id);

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .CreateClient();

        var code = ComputeCode(secret, Clock.GetCurrentInstant());
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/disable", UriKind.Relative),
            new TotpDisableRequest(code),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var row = await Db.TotpSecrets.SingleAsync(t => t.UserId == user.Id, ct);
        row.DisabledAt.ShouldNotBeNull();
        (await Db.TotpBackupCodes.CountAsync(b => b.UserId == user.Id, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task Disable_with_backup_code_returns_401_and_keeps_totp_enabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var (backupCodes, _) = await EnableAsync(user.Id);

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.Verified)
            .WithClock(Clock)
            .CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/disable", UriKind.Relative),
            new TotpDisableRequest(backupCodes[0]),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var row = await Db.TotpSecrets.SingleAsync(t => t.UserId == user.Id, ct);
        row.DisabledAt.ShouldBeNull();
    }

    [Fact]
    public async Task Disable_when_not_verified_returns_403()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var (_, secret) = await EnableAsync(user.Id);

        using var client = Factory
            .WithTestAuth(user.Id, totp: TotpClaimValues.NotVerified)
            .WithClock(Clock)
            .CreateClient();

        var code = ComputeCode(secret, Clock.GetCurrentInstant());
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/disable", UriKind.Relative),
            new TotpDisableRequest(code),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(IReadOnlyList<string> BackupCodes, string Secret)> EnableAsync(Guid userId)
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory
            .WithTestAuth(userId, totp: TotpClaimValues.NotEnabled)
            .WithClock(Clock)
            .CreateClient();
        await UnlockAsync(client);

        var init = await client.PostAsync(
            new Uri("/api/auth/totp/enable/init", UriKind.Relative), content: null, ct);
        var initBody = (await init.Content.ReadFromJsonAsync<TotpEnableInitResponse>(ct))!;
        var code = ComputeCode(initBody.Secret, Clock.GetCurrentInstant());

        var verify = await client.PostAsJsonAsync(
            new Uri("/api/auth/totp/enable/verify", UriKind.Relative),
            new TotpEnableVerifyRequest(initBody.Secret, code),
            ct);
        verify.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = (await verify.Content.ReadFromJsonAsync<TotpEnableVerifyResponse>(ct))!;
        return (body.BackupCodes, initBody.Secret);
    }
}
