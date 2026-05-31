using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests;

public sealed class CrossEntityCascadeTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task User_delete_with_no_clouds_cascades_to_all_auth_children()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        using var paramsDoc = JsonDocument.Parse("{}");

        Db.Users.Add(user);
        Db.TotpSecrets.Add(new TotpSecret { UserId = user.Id, Ciphertext = [1], Nonce = new byte[12], Tag = new byte[16], CreatedAt = now, UpdatedAt = now });
        Db.TotpBackupCodes.Add(new TotpBackupCode { UserId = user.Id, HashedCode = "b", CreatedAt = now });
        Db.EmergencyKits.Add(new EmergencyKit
        {
            UserId = user.Id,
            HashedString = "r",
            WrapArgon2Salt = new byte[1],
            WrapArgon2Params = paramsDoc,
            WrappedDek = new byte[1],
            WrapNonce = new byte[1],
            WrapTag = new byte[1],
            CreatedAt = now,
        });
        Db.EncryptedProviderTokens.Add(new EncryptedProviderToken
        {
            UserId = user.Id,
            Provider = "p",
            Ciphertext = [1],
            Nonce = new byte[12],
            Tag = new byte[16],
            CreatedAt = now,
            UpdatedAt = now,
        });
        Db.AuthLockouts.Add(new AuthLockout { UserId = user.Id, Kind = "totp", FailedCount = 1, LastAttemptAt = now });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.TotpSecrets.AnyAsync(t => t.UserId == user.Id, ct)).ShouldBeFalse();
        (await Db.TotpBackupCodes.AnyAsync(t => t.UserId == user.Id, ct)).ShouldBeFalse();
        (await Db.EmergencyKits.AnyAsync(r => r.UserId == user.Id, ct)).ShouldBeFalse();
        (await Db.EncryptedProviderTokens.AnyAsync(e => e.UserId == user.Id, ct)).ShouldBeFalse();
        (await Db.AuthLockouts.AnyAsync(a => a.UserId == user.Id, ct)).ShouldBeFalse();
    }
}
