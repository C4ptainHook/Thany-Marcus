using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class TotpBackupCodeTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task UsedAt_is_nullable_and_round_trips()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        var code = new TotpBackupCode { UserId = user.Id, HashedCode = "$argon2id$...", CreatedAt = now };
        Db.Users.Add(user);
        Db.TotpBackupCodes.Add(code);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        (await Db.TotpBackupCodes.SingleAsync(c => c.Id == code.Id, ct)).UsedAt.ShouldBeNull();

        var tracked = await Db.TotpBackupCodes.SingleAsync(c => c.Id == code.Id, ct);
        tracked.UsedAt = now;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        (await Db.TotpBackupCodes.SingleAsync(c => c.Id == code.Id, ct)).UsedAt.ShouldBe(now);
    }

    [Fact]
    public async Task Cascades_on_user_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g2", Email = "u2@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.TotpBackupCodes.Add(new TotpBackupCode { UserId = user.Id, HashedCode = "x", CreatedAt = now });
        Db.TotpBackupCodes.Add(new TotpBackupCode { UserId = user.Id, HashedCode = "y", CreatedAt = now });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.TotpBackupCodes.CountAsync(c => c.UserId == user.Id, ct)).ShouldBe(0);
    }
}
