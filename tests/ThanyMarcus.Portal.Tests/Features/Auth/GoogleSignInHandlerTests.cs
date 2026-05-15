using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class GoogleSignInHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private static ClaimsIdentity GoogleIdentity(
        string sub = "google-sub-1",
        string email = "alice@example.com",
        string name = "Alice")
    {
        var identity = new ClaimsIdentity("Google");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, sub));
        identity.AddClaim(new Claim(ClaimTypes.Email, email));
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        return identity;
    }

    private static JsonElement UserInfo(string? picture = null)
        => picture is null
            ? JsonSerializer.SerializeToElement(new { })
            : JsonSerializer.SerializeToElement(new { picture });

    [Fact]
    public async Task New_user_creates_row_and_emits_not_enabled_totp()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new GoogleSignInHandler(Db, Clock);
        var identity = GoogleIdentity();

        await handler.HandleAsync(identity, UserInfo("https://example.com/pic.png"), ct);

        Db.ChangeTracker.Clear();
        var users = await Db.Users.ToListAsync(ct);
        users.Count.ShouldBe(1);
        users[0].GoogleSubject.ShouldBe("google-sub-1");
        users[0].Email.ShouldBe("alice@example.com");
        users[0].Name.ShouldBe("Alice");
        users[0].ProfilePictureUrl.ShouldBe("https://example.com/pic.png");
        users[0].LastSeenAt.ShouldBe(Clock.GetCurrentInstant());

        identity.FindFirst(AuthClaimTypes.SubUs)!.Value.ShouldBe(users[0].Id.ToString());
        identity.FindFirst(AuthClaimTypes.SubGoogle)!.Value.ShouldBe("google-sub-1");
        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.NotEnabled);
        identity.FindFirst("picture")!.Value.ShouldBe("https://example.com/pic.png");
    }

    [Fact]
    public async Task Existing_user_refreshes_fields_and_does_not_duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var initial = Clock.GetCurrentInstant();
        Db.Users.Add(new User
        {
            GoogleSubject = "google-sub-1",
            Email = "old@example.com",
            Name = "Old Name",
            ProfilePictureUrl = "old.png",
            LastSeenAt = initial,
            CreatedAt = initial,
            UpdatedAt = initial,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        Clock.Advance(Duration.FromHours(1));
        var handler = new GoogleSignInHandler(Db, Clock);
        var identity = GoogleIdentity(email: "new@example.com", name: "New Name");

        await handler.HandleAsync(identity, UserInfo("new.png"), ct);

        Db.ChangeTracker.Clear();
        var users = await Db.Users.ToListAsync(ct);
        users.Count.ShouldBe(1);
        users[0].Email.ShouldBe("new@example.com");
        users[0].Name.ShouldBe("New Name");
        users[0].ProfilePictureUrl.ShouldBe("new.png");
        users[0].LastSeenAt.ShouldBe(initial + Duration.FromHours(1));

        identity.FindFirst(AuthClaimTypes.SubUs)!.Value.ShouldBe(users[0].Id.ToString());
        identity.FindFirst(AuthClaimTypes.SubGoogle)!.Value.ShouldBe("google-sub-1");
        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.NotEnabled);
        identity.FindFirst("picture")!.Value.ShouldBe("new.png");
    }

    [Fact]
    public async Task Totp_enabled_emits_not_verified_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = "google-sub-1",
            Email = "a@x.com",
            Name = "A",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        Db.TotpSecrets.Add(new TotpSecret
        {
            UserId = user.Id,
            Ciphertext = [1],
            Nonce = [2],
            Tag = [3],
            EnabledAt = now,
            DisabledAt = null,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new GoogleSignInHandler(Db, Clock);
        var identity = GoogleIdentity();
        await handler.HandleAsync(identity, UserInfo(), ct);

        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.NotVerified);
    }

    [Fact]
    public async Task Totp_disabled_row_emits_not_enabled_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = "google-sub-1",
            Email = "a@x.com",
            Name = "A",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        Db.TotpSecrets.Add(new TotpSecret
        {
            UserId = user.Id,
            Ciphertext = [1],
            Nonce = [2],
            Tag = [3],
            EnabledAt = now,
            DisabledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new GoogleSignInHandler(Db, Clock);
        var identity = GoogleIdentity();
        await handler.HandleAsync(identity, UserInfo(), ct);

        identity.FindFirst(AuthClaimTypes.Totp)!.Value.ShouldBe(TotpClaimValues.NotEnabled);
    }

    [Fact]
    public async Task Missing_sub_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var handler = new GoogleSignInHandler(Db, Clock);
        var identity = new ClaimsIdentity("Google");
        identity.AddClaim(new Claim(ClaimTypes.Email, "a@x.com"));

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await handler.HandleAsync(identity, UserInfo(), ct));
    }
}
