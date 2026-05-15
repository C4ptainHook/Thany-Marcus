using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class EncryptedProviderTokenTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task UserId_provider_is_unique()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.EncryptedProviderTokens.Add(new EncryptedProviderToken
        {
            UserId = user.Id,
            Provider = "hetzner",
            Ciphertext = [1],
            Nonce = new byte[12],
            Tag = new byte[16],
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);

        Db.EncryptedProviderTokens.Add(new EncryptedProviderToken
        {
            UserId = user.Id,
            Provider = "hetzner",
            Ciphertext = [2],
            Nonce = new byte[12],
            Tag = new byte[16],
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Cascades_on_user_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g2", Email = "u2@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
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
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Db.SaveChangesAsync(ct);

        (await Db.EncryptedProviderTokens.CountAsync(e => e.UserId == user.Id, ct)).ShouldBe(0);
    }
}
