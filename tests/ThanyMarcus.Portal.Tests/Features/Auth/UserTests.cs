using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Auth;

public sealed class UserTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Insert_round_trips_all_fields()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = "google-1",
            Email = "user@example.com",
            Name = "Test User",
            ProfilePictureUrl = "https://example.com/pic.png",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };

        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);

        Db.ChangeTracker.Clear();
        var fetched = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        fetched.GoogleSubject.ShouldBe("google-1");
        fetched.Email.ShouldBe("user@example.com");
        fetched.Name.ShouldBe("Test User");
        fetched.ProfilePictureUrl.ShouldBe("https://example.com/pic.png");
        fetched.LastSeenAt.ShouldBe(now);
        fetched.CreatedAt.ShouldBe(now);
    }

    [Fact]
    public async Task Duplicate_google_subject_throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        Db.Users.Add(new User { GoogleSubject = "dup", Email = "a@x.com", Name = "A", LastSeenAt = now, CreatedAt = now, UpdatedAt = now });
        await Db.SaveChangesAsync(ct);

        Db.Users.Add(new User { GoogleSubject = "dup", Email = "b@x.com", Name = "B", LastSeenAt = now, CreatedAt = now, UpdatedAt = now });
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(ct));
    }
}
