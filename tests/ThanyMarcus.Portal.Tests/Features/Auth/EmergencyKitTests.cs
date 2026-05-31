using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class EmergencyKitTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Envelope_columns_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        using var paramsDoc = JsonDocument.Parse("{\"m\":65536,\"t\":3,\"p\":4}");
        var kit = new EmergencyKit
        {
            UserId = user.Id,
            HashedString = "$argon2id$v=19$m=65536,t=3,p=4$abc$def",
            WrapArgon2Salt = new byte[16],
            WrapArgon2Params = paramsDoc,
            WrappedDek = new byte[32],
            WrapNonce = new byte[12],
            WrapTag = new byte[16],
            CreatedAt = now,
        };
        Db.Users.Add(user);
        Db.EmergencyKits.Add(kit);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var fetched = await Db.EmergencyKits.SingleAsync(k => k.Id == kit.Id, ct);
        fetched.WrapArgon2Salt.Length.ShouldBe(16);
        fetched.WrappedDek.Length.ShouldBe(32);
        fetched.WrapNonce.Length.ShouldBe(12);
        fetched.WrapTag.Length.ShouldBe(16);
        fetched.WrapArgon2Params.RootElement.GetProperty("m").GetInt32().ShouldBe(65536);
        fetched.UsedAt.ShouldBeNull();
        fetched.RevokedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Active_kit_is_unique_per_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g2", Email = "u2@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        // A revoked kit plus a fresh active kit is allowed (partial index ignores revoked rows).
        Db.EmergencyKits.Add(NewKit(user.Id, now, revokedAt: now));
        Db.EmergencyKits.Add(NewKit(user.Id, now));
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        // A second active kit violates the partial unique index.
        Db.EmergencyKits.Add(NewKit(user.Id, now));
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Cascades_on_user_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g3", Email = "u3@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.EmergencyKits.Add(NewKit(user.Id, now));
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.EmergencyKits.CountAsync(k => k.UserId == user.Id, ct)).ShouldBe(0);
    }

    private static EmergencyKit NewKit(Guid userId, NodaTime.Instant now, NodaTime.Instant? revokedAt = null)
    {
        using var paramsDoc = JsonDocument.Parse("{}");
        return new EmergencyKit
        {
            UserId = userId,
            HashedString = "h",
            WrapArgon2Salt = new byte[1],
            WrapArgon2Params = JsonDocument.Parse("{}"),
            WrappedDek = new byte[1],
            WrapNonce = new byte[1],
            WrapTag = new byte[1],
            CreatedAt = now,
            RevokedAt = revokedAt,
        };
    }
}
