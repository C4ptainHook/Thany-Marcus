using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Lockout;

public sealed class AuthLockoutServiceTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private static readonly LockoutOptions TestOptions = new()
    {
        Totp   = new LockoutOptions.KindOptions(MaxFailures: 3, WindowSeconds: 3600, LockoutSeconds: 1800),
        Unlock = new LockoutOptions.KindOptions(MaxFailures: 3, WindowSeconds: 3600, LockoutSeconds: 1800),
    };

    private AuthLockoutService NewService() =>
        new(Db, Clock, Options.Create(TestOptions));

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
    public void Defaults_match_adr_0031_persistent_lockout_table()
    {
        var defaults = new LockoutOptions();
        defaults.Totp.MaxFailures.ShouldBe(20);
        defaults.Totp.WindowSeconds.ShouldBe(3600);
        defaults.Totp.LockoutSeconds.ShouldBe(1800);
        defaults.Unlock.MaxFailures.ShouldBe(20);
        defaults.Unlock.WindowSeconds.ShouldBe(3600);
        defaults.Unlock.LockoutSeconds.ShouldBe(1800);
        defaults.Sweep.IntervalSeconds.ShouldBe(21600);
        defaults.Sweep.RetentionDays.ShouldBe(30);
    }

    [Fact]
    public async Task RecordFailure_inserts_row_with_count_1_on_first_call()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Db.ChangeTracker.Clear();
        var row = await Db.AuthLockouts.SingleAsync(a => a.UserId == user.Id && a.Kind == AuthLockoutKinds.Totp, ct);
        row.FailedCount.ShouldBe((short)1);
        row.LastAttemptAt.ShouldBe(Clock.GetCurrentInstant());
        row.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task RecordFailure_increments_within_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        Clock.Advance(Duration.FromSeconds(60));
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Db.ChangeTracker.Clear();
        var row = await Db.AuthLockouts.SingleAsync(a => a.UserId == user.Id, ct);
        row.FailedCount.ShouldBe((short)2);
        row.LastAttemptAt.ShouldBe(Clock.GetCurrentInstant());
        row.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task RecordFailure_resets_count_to_1_outside_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        Clock.Advance(Duration.FromSeconds(TestOptions.Totp.WindowSeconds + 1));
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Db.ChangeTracker.Clear();
        var row = await Db.AuthLockouts.SingleAsync(a => a.UserId == user.Id, ct);
        row.FailedCount.ShouldBe((short)1);
    }

    [Fact]
    public async Task RecordFailure_sets_locked_until_when_threshold_crossed()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Db.ChangeTracker.Clear();
        var beforeThreshold = await Db.AuthLockouts.SingleAsync(a => a.UserId == user.Id, ct);
        beforeThreshold.LockedUntil.ShouldBeNull();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Db.ChangeTracker.Clear();
        var atThreshold = await Db.AuthLockouts.SingleAsync(a => a.UserId == user.Id, ct);
        atThreshold.FailedCount.ShouldBe((short)TestOptions.Totp.MaxFailures);
        atThreshold.LockedUntil.ShouldNotBeNull();
        atThreshold.LockedUntil!.Value.ShouldBe(
            Clock.GetCurrentInstant() + Duration.FromSeconds(TestOptions.Totp.LockoutSeconds));
    }

    [Fact]
    public async Task IsLocked_returns_NotLocked_when_no_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        var state = await svc.IsLockedAsync(user.Id, AuthLockoutKinds.Totp, ct);

        state.IsLocked.ShouldBeFalse();
        state.LockedUntil.ShouldBeNull();
    }

    [Fact]
    public async Task IsLocked_returns_NotLocked_when_locked_until_in_past()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        Clock.Advance(Duration.FromSeconds(TestOptions.Totp.LockoutSeconds + 1));

        var state = await svc.IsLockedAsync(user.Id, AuthLockoutKinds.Totp, ct);
        state.IsLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task IsLocked_returns_IsLocked_with_positive_remaining_when_lock_is_active()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        var state = await svc.IsLockedAsync(user.Id, AuthLockoutKinds.Totp, ct);
        state.IsLocked.ShouldBeTrue();
        state.LockedUntil.ShouldNotBeNull();
        state.RemainingSeconds.ShouldBeGreaterThan(0);
        state.RemainingSeconds.ShouldBeLessThanOrEqualTo(TestOptions.Totp.LockoutSeconds);
    }

    [Fact]
    public async Task Clear_is_noop_when_no_row_exists()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.ClearAsync(user.Id, AuthLockoutKinds.Totp, ct);

        (await Db.AuthLockouts.AnyAsync(a => a.UserId == user.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Clear_resets_count_and_lock_but_keeps_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Clock.Advance(Duration.FromSeconds(30));
        await svc.ClearAsync(user.Id, AuthLockoutKinds.Totp, ct);

        Db.ChangeTracker.Clear();
        var row = await Db.AuthLockouts.SingleAsync(a => a.UserId == user.Id, ct);
        row.FailedCount.ShouldBe((short)0);
        row.LockedUntil.ShouldBeNull();
        row.LastAttemptAt.ShouldBe(Clock.GetCurrentInstant());
    }

    [Fact]
    public async Task Partition_is_per_user_and_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var userA = await InsertUserAsync();
        var userB = await InsertUserAsync();
        var svc = NewService();

        await svc.RecordFailureAsync(userA.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(userA.Id, AuthLockoutKinds.Totp, ct);
        await svc.RecordFailureAsync(userA.Id, AuthLockoutKinds.Totp, ct);

        (await svc.IsLockedAsync(userA.Id, AuthLockoutKinds.Totp, ct)).IsLocked.ShouldBeTrue();
        (await svc.IsLockedAsync(userB.Id, AuthLockoutKinds.Totp, ct)).IsLocked.ShouldBeFalse();
        (await svc.IsLockedAsync(userA.Id, AuthLockoutKinds.Unlock, ct)).IsLocked.ShouldBeFalse();
    }

    [Fact]
    public async Task RecordFailure_throws_on_unknown_kind()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        var svc = NewService();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(
            async () => await svc.RecordFailureAsync(user.Id, "recovery", ct));
    }
}
