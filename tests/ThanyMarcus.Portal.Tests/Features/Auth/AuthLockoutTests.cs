using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class AuthLockoutTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Composite_pk_allows_multiple_kinds_per_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.AuthLockouts.Add(new AuthLockout { UserId = user.Id, Kind = "totp", FailedCount = 1, LastAttemptAt = now });
        Db.AuthLockouts.Add(new AuthLockout { UserId = user.Id, Kind = "unlock", FailedCount = 2, LastAttemptAt = now });
        await Db.SaveChangesAsync(ct);

        (await Db.AuthLockouts.CountAsync(a => a.UserId == user.Id, ct)).ShouldBe(2);
    }

    [Fact]
    public async Task Duplicate_user_kind_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g2", Email = "u2@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.AuthLockouts.Add(new AuthLockout { UserId = user.Id, Kind = "totp", FailedCount = 1, LastAttemptAt = now });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        Db.AuthLockouts.Add(new AuthLockout { UserId = user.Id, Kind = "totp", FailedCount = 2, LastAttemptAt = now });
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Cascades_on_user_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g3", Email = "u3@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.AuthLockouts.Add(new AuthLockout { UserId = user.Id, Kind = "totp", FailedCount = 1, LastAttemptAt = now });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.AuthLockouts.CountAsync(a => a.UserId == user.Id, ct)).ShouldBe(0);
    }
}
