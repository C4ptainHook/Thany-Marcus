using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.StepUp;

public sealed class PassphraseServiceTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
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
    public async Task InitAsync_populates_all_crypto_columns_and_passphrase_set_at()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new PassphraseService(Db, Clock);

        var ok = await svc.InitAsync(user.Id, "hunter2hunter2", ct);

        ok.ShouldBeTrue();
        Db.ChangeTracker.Clear();
        var stored = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        stored.PassphraseArgon2Salt.ShouldNotBeNull();
        stored.PassphraseArgon2Salt!.Length.ShouldBe(16);
        stored.PassphraseArgon2Params.ShouldNotBeNull();
        stored.PassphraseWrappedDek.ShouldNotBeNull();
        stored.PassphraseWrappedDek!.Length.ShouldBe(32);
        stored.PassphraseWrapNonce.ShouldNotBeNull();
        stored.PassphraseWrapNonce!.Length.ShouldBe(12);
        stored.PassphraseWrapTag.ShouldNotBeNull();
        stored.PassphraseWrapTag!.Length.ShouldBe(16);
        stored.PassphraseSetAt.ShouldBe(Clock.GetCurrentInstant());
    }

    [Fact]
    public async Task InitAsync_called_twice_returns_false_and_does_not_overwrite()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new PassphraseService(Db, Clock);
        (await svc.InitAsync(user.Id, "first-pass-phrase", ct)).ShouldBeTrue();
        Db.ChangeTracker.Clear();
        var first = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, ct);

        var second = await svc.InitAsync(user.Id, "second-pass-phrase", ct);

        second.ShouldBeFalse();
        Db.ChangeTracker.Clear();
        var after = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, ct);
        after.PassphraseWrappedDek.ShouldBe(first.PassphraseWrappedDek);
        after.PassphraseArgon2Salt.ShouldBe(first.PassphraseArgon2Salt);
        after.PassphraseWrapNonce.ShouldBe(first.PassphraseWrapNonce);
        after.PassphraseWrapTag.ShouldBe(first.PassphraseWrapTag);
    }

    [Fact]
    public async Task TryUnwrapDekAsync_with_correct_passphrase_round_trips_dek()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new PassphraseService(Db, Clock);
        await svc.InitAsync(user.Id, "hunter2hunter2", ct);
        Db.ChangeTracker.Clear();

        var dek1 = new byte[32];
        var r1 = await svc.TryUnwrapDekAsync(user.Id, "hunter2hunter2", dek1, ct);
        Db.ChangeTracker.Clear();
        var dek2 = new byte[32];
        var r2 = await svc.TryUnwrapDekAsync(user.Id, "hunter2hunter2", dek2, ct);

        r1.ShouldBe(UnlockResult.Unlocked);
        r2.ShouldBe(UnlockResult.Unlocked);
        dek1.ShouldBe(dek2);
        dek1.Any(b => b != 0).ShouldBeTrue();
    }

    [Fact]
    public async Task TryUnwrapDekAsync_with_wrong_passphrase_returns_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new PassphraseService(Db, Clock);
        await svc.InitAsync(user.Id, "hunter2hunter2", ct);
        Db.ChangeTracker.Clear();

        var dek = new byte[32];
        var result = await svc.TryUnwrapDekAsync(user.Id, "wrong-passphrase", dek, ct);

        result.ShouldBe(UnlockResult.Failed);
    }

    [Fact]
    public async Task TryUnwrapDekAsync_with_no_passphrase_set_returns_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = new PassphraseService(Db, Clock);

        var dek = new byte[32];
        var result = await svc.TryUnwrapDekAsync(user.Id, "anything", dek, ct);

        result.ShouldBe(UnlockResult.Failed);
    }

    [Fact]
    public async Task TryUnwrapDekAsync_using_other_users_passphrase_returns_Failed()
    {
        var ct = TestContext.Current.CancellationToken;
        var alice = await InsertUserAsync();
        var bob = await InsertUserAsync();
        var svc = new PassphraseService(Db, Clock);
        await svc.InitAsync(alice.Id, "alice-pass", ct);
        await svc.InitAsync(bob.Id, "bob-pass", ct);
        Db.ChangeTracker.Clear();

        var dek = new byte[32];
        var result = await svc.TryUnwrapDekAsync(bob.Id, "alice-pass", dek, ct);

        result.ShouldBe(UnlockResult.Failed);
    }
}
