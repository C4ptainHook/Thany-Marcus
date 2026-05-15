using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.Provisioning;

public sealed class ProvisioningJobTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Round_trip_includes_jsonb_payload_and_nullable_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var cloud = await SeedCloudAsync(now, ct);

        using var payload = JsonDocument.Parse("{\"region\":\"nbg1\"}");
        var job = new ProvisioningJob
        {
            CloudId = cloud.Id,
            Kind = "provision",
            Payload = payload,
            Status = "pending",
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.ProvisioningJobs.Add(job);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var fetched = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        fetched.Status.ShouldBe("pending");
        fetched.LeaseExpires.ShouldBeNull();
        fetched.Payload.RootElement.GetProperty("region").GetString().ShouldBe("nbg1");
    }

    [Fact]
    public async Task Partial_index_supports_next_pending_query()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var cloud = await SeedCloudAsync(now, ct);

        using var payload = JsonDocument.Parse("{}");
        for (var i = 0; i < 50; i++)
        {
            Db.ProvisioningJobs.Add(new ProvisioningJob
            {
                CloudId = cloud.Id,
                Kind = "provision",
                Payload = JsonDocument.Parse("{}"),
                Status = "completed",
                Attempts = 1,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        var pending = new ProvisioningJob
        {
            CloudId = cloud.Id,
            Kind = "provision",
            Payload = JsonDocument.Parse("{}"),
            Status = "pending",
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.ProvisioningJobs.Add(pending);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var next = await Db.ProvisioningJobs
            .Where(j => j.Status == "pending")
            .OrderBy(j => j.CreatedAt)
            .FirstAsync(ct);

        next.Id.ShouldBe(pending.Id);
    }

    [Fact]
    public async Task Cascades_on_cloud_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var cloud = await SeedCloudAsync(now, ct);

        Db.ProvisioningJobs.Add(new ProvisioningJob
        {
            CloudId = cloud.Id,
            Kind = "provision",
            Payload = JsonDocument.Parse("{}"),
            Status = "pending",
            Attempts = 0,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var trackedCloud = await Db.Clouds.SingleAsync(c => c.Id == cloud.Id, ct);
        Db.Clouds.Remove(trackedCloud);
        await Db.SaveChangesAsync(ct);

        (await Db.ProvisioningJobs.CountAsync(j => j.CloudId == cloud.Id, ct)).ShouldBe(0);
    }

    private async Task<Cloud> SeedCloudAsync(Instant now, CancellationToken ct)
    {
        var user = new User { GoogleSubject = $"g-{Guid.NewGuid():N}", Email = $"{Guid.NewGuid():N}@x.com", Name = "U", LastSeenAt = now, CreatedAt = now, UpdatedAt = now };
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
        return cloud;
    }
}
