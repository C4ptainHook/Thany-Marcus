using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Totp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Totp;

public sealed class TotpBackupCodeServiceTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private const string CrockfordAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

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
    public async Task IssueAsync_produces_8_unique_codes_from_Crockford_alphabet()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);

        var codes = await svc.IssueAsync(user.Id, ct);

        codes.Count.ShouldBe(8);
        codes.Distinct().Count().ShouldBe(8);
        foreach (var code in codes)
        {
            code.Length.ShouldBe(8);
            foreach (var ch in code)
                CrockfordAlphabet.ShouldContain(ch);
        }

        (await Db.TotpBackupCodes.CountAsync(b => b.UserId == user.Id, ct)).ShouldBe(8);
    }

    [Fact]
    public async Task RedeemAsync_with_valid_code_marks_used_and_returns_true()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);
        var codes = await svc.IssueAsync(user.Id, ct);
        Db.ChangeTracker.Clear();

        var redeemed = await svc.RedeemAsync(user.Id, codes[0], ct);

        redeemed.ShouldBeTrue();
        Db.ChangeTracker.Clear();
        var used = await Db.TotpBackupCodes.CountAsync(b => b.UserId == user.Id && b.UsedAt != null, ct);
        used.ShouldBe(1);
    }

    [Fact]
    public async Task RedeemAsync_rejects_already_redeemed_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);
        var codes = await svc.IssueAsync(user.Id, ct);

        (await svc.RedeemAsync(user.Id, codes[0], ct)).ShouldBeTrue();
        (await svc.RedeemAsync(user.Id, codes[0], ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task RedeemAsync_rejects_unknown_code()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);
        await svc.IssueAsync(user.Id, ct);

        (await svc.RedeemAsync(user.Id, "ZZZZZZZZ", ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task RedeemAsync_for_different_user_returns_false()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await InsertUserAsync();
        var bob = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);
        var aliceCodes = await svc.IssueAsync(alice.Id, ct);

        (await svc.RedeemAsync(bob.Id, aliceCodes[0], ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task IssueAsync_called_twice_purges_first_set()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);

        var first = await svc.IssueAsync(user.Id, ct);
        var second = await svc.IssueAsync(user.Id, ct);

        (await Db.TotpBackupCodes.CountAsync(b => b.UserId == user.Id, ct)).ShouldBe(8);
        (await svc.RedeemAsync(user.Id, first[0], ct)).ShouldBeFalse();
        (await svc.RedeemAsync(user.Id, second[0], ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task PurgeUnusedAsync_deletes_only_unused_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new TotpBackupCodeService(Db, Clock);
        var codes = await svc.IssueAsync(user.Id, ct);
        await svc.RedeemAsync(user.Id, codes[0], ct);

        await svc.PurgeUnusedAsync(user.Id, ct);

        var rows = await Db.TotpBackupCodes.Where(b => b.UserId == user.Id).ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].UsedAt.ShouldNotBeNull();
    }
}
