using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.Lockout;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Lockout;

public sealed class LockoutSweepTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    [Fact]
    public async Task Sweep_deletes_stale_rows_but_keeps_rows_with_active_lockout()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var userStale = new User
        {
            GoogleSubject = "g-stale", Email = "stale@x.com", Name = "Stale",
            LastSeenAt = now, CreatedAt = now, UpdatedAt = now,
        };
        var userActive = new User
        {
            GoogleSubject = "g-active", Email = "active@x.com", Name = "Active",
            LastSeenAt = now, CreatedAt = now, UpdatedAt = now,
        };
        Db.Users.AddRange(userStale, userActive);
        await Db.SaveChangesAsync(ct);

        var thirtyOneDaysAgo = now - Duration.FromDays(31);
        Db.AuthLockouts.Add(new AuthLockout
        {
            UserId = userStale.Id, Kind = AuthLockoutKinds.Totp,
            FailedCount = 1, LastAttemptAt = thirtyOneDaysAgo, LockedUntil = null,
        });
        Db.AuthLockouts.Add(new AuthLockout
        {
            UserId = userActive.Id, Kind = AuthLockoutKinds.Totp,
            FailedCount = 20, LastAttemptAt = thirtyOneDaysAgo,
            LockedUntil = now + Duration.FromDays(1),
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var factory = Factory.WithClock(Clock);
        using var _ = factory.CreateClient();
        var sweep = new AuthLockoutSweepService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptions<LockoutOptions>>(),
            NullLogger<AuthLockoutSweepService>.Instance);

        await sweep.SweepOnceAsync(ct);

        Db.ChangeTracker.Clear();
        (await Db.AuthLockouts.AnyAsync(a => a.UserId == userStale.Id, ct)).ShouldBeFalse();
        (await Db.AuthLockouts.AnyAsync(a => a.UserId == userActive.Id, ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task Sweep_keeps_recently_active_rows_within_retention_window()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = "g-recent", Email = "recent@x.com", Name = "Recent",
            LastSeenAt = now, CreatedAt = now, UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);

        Db.AuthLockouts.Add(new AuthLockout
        {
            UserId = user.Id, Kind = AuthLockoutKinds.Unlock,
            FailedCount = 1, LastAttemptAt = now - Duration.FromDays(5), LockedUntil = null,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var factory = Factory.WithClock(Clock);
        using var _ = factory.CreateClient();
        var sweep = new AuthLockoutSweepService(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            factory.Services.GetRequiredService<IOptions<LockoutOptions>>(),
            NullLogger<AuthLockoutSweepService>.Instance);

        await sweep.SweepOnceAsync(ct);

        Db.ChangeTracker.Clear();
        (await Db.AuthLockouts.AnyAsync(a => a.UserId == user.Id, ct)).ShouldBeTrue();
    }
}
