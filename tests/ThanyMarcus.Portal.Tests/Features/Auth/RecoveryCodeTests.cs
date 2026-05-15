using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class RecoveryCodeTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Envelope_columns_round_trip()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        using var paramsDoc = JsonDocument.Parse("{\"m\":65536,\"t\":3,\"p\":4}");
        var code = new RecoveryCode
        {
            UserId = user.Id,
            HashedCode = "$argon2id$v=19$m=65536,t=3,p=4$abc$def",
            WrapArgon2Salt = new byte[16],
            WrapArgon2Params = paramsDoc,
            WrappedDek = new byte[32],
            WrapNonce = new byte[12],
            WrapTag = new byte[16],
            CreatedAt = now,
        };
        Db.Users.Add(user);
        Db.RecoveryCodes.Add(code);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var fetched = await Db.RecoveryCodes.SingleAsync(r => r.Id == code.Id, ct);
        fetched.WrapArgon2Salt.Length.ShouldBe(16);
        fetched.WrappedDek.Length.ShouldBe(32);
        fetched.WrapNonce.Length.ShouldBe(12);
        fetched.WrapTag.Length.ShouldBe(16);
        fetched.WrapArgon2Params.RootElement.GetProperty("m").GetInt32().ShouldBe(65536);
    }

    [Fact]
    public async Task Cascades_on_user_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g3", Email = "u3@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        using var paramsDoc = JsonDocument.Parse("{}");
        Db.Users.Add(user);
        Db.RecoveryCodes.Add(new RecoveryCode
        {
            UserId = user.Id,
            HashedCode = "h",
            WrapArgon2Salt = new byte[1],
            WrapArgon2Params = paramsDoc,
            WrappedDek = new byte[1],
            WrapNonce = new byte[1],
            WrapTag = new byte[1],
            CreatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.RecoveryCodes.CountAsync(r => r.UserId == user.Id, ct)).ShouldBe(0);
    }
}
