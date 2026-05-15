using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class TotpSecretTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task TotpSecret_cascades_on_user_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g1", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        var secret = new TotpSecret
        {
            UserId = user.Id,
            Ciphertext = [1, 2, 3],
            Nonce = new byte[12],
            Tag = new byte[16],
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        Db.TotpSecrets.Add(secret);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.TotpSecrets.AnyAsync(t => t.Id == secret.Id, ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task TotpSecret_is_one_to_one_with_user()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g2", Email = "u2@x.com", Name = "U2", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.TotpSecrets.Add(new TotpSecret { UserId = user.Id, Ciphertext = [1], Nonce = new byte[12], Tag = new byte[16], CreatedAt = now, UpdatedAt = now });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        Db.TotpSecrets.Add(new TotpSecret { UserId = user.Id, Ciphertext = [2], Nonce = new byte[12], Tag = new byte[16], CreatedAt = now, UpdatedAt = now });
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(ct));
    }
}
