using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement;

public sealed class CloudTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task User_delete_with_cloud_throws_restrict()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        Db.Users.Add(user);
        Db.Clouds.Add(NewCloud(user.Id, now, "host-a.example.com"));
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var tracked = await Db.Users.SingleAsync(u => u.Id == user.Id, ct);
        Db.Users.Remove(tracked);
        await Should.ThrowAsync<DbUpdateException>(async () => await Db.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task Soft_deleted_cloud_is_filtered_out_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User { GoogleSubject = "g2", Email = "u2@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        var live = NewCloud(user.Id, now, "live.example.com");
        var destroyed = NewCloud(user.Id, now, "dead.example.com");
        destroyed.DestroyedAt = now;
        Db.Users.Add(user);
        Db.Clouds.AddRange(live, destroyed);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        (await Db.Clouds.CountAsync(c => c.UserId == user.Id, ct)).ShouldBe(1);
        (await Db.Clouds.IgnoreQueryFilters().CountAsync(c => c.UserId == user.Id, ct)).ShouldBe(2);
    }

    private static Cloud NewCloud(Guid userId, NodaTime.Instant now, string host) => new()
    {
        UserId = userId,
        Name = "test",
        Provider = "hetzner",
        Region = "nbg1",
        Hostname = host,
        ProvisioningStatus = "pending",
        CreatedAt = now,
        UpdatedAt = now,
    };
}
