using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement;

public sealed class PluginTokenMetadataTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Cascades_on_cloud_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var (user, cloud) = await SeedAsync(now, ct);

        Db.PluginTokenMetadata.Add(new PluginTokenMetadata
        {
            CloudId = cloud.Id,
            Name = "primary",
            TokenHash = new byte[] { 0x01 },
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var trackedCloud = await Db.Clouds.SingleAsync(c => c.Id == cloud.Id, ct);
        Db.Clouds.Remove(trackedCloud);
        await Db.SaveChangesAsync(ct);

        (await Db.PluginTokenMetadata.CountAsync(p => p.CloudId == cloud.Id, ct)).ShouldBe(0);
        _ = user;
    }

    private async Task<(User user, Cloud cloud)> SeedAsync(Instant now, CancellationToken ct)
    {
        var user = new User { GoogleSubject = "g", Email = "u@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
        var cloud = new Cloud
        {
            UserId = user.Id,
            Name = "c",
            Provider = "hetzner",
            Region = "nbg1",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = "pending",
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        return (user, cloud);
    }
}
