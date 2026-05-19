using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.Login;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Login;

public sealed class DigitalOceanTokenRefresherTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private async Task<Cloud> InsertDoCloudAsync(Guid userId, Instant accessExpiresAt)
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var cloud = new Cloud
        {
            UserId = userId,
            Name = "c",
            Provider = "digitalocean",
            Region = "nyc3",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = SagaStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);

        var dek = SagaTestSeed.MakeDek();
        var bundle = new CloudSecretBundle(Db, Clock);
        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,  "old-access",  dek, accessExpiresAt, ct);
        await bundle.PutAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh, "old-refresh", dek, null,            ct);

        Db.ChangeTracker.Clear();
        return cloud;
    }

    private async Task<User> InsertUserAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = Clock.GetCurrentInstant();
        var user = new User
        {
            GoogleSubject = $"g-{Guid.NewGuid():N}",
            Email = $"{Guid.NewGuid():N}@example.com",
            Name = "Test",
            LastSeenAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Users.Add(user);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return user;
    }

    [Fact]
    public async Task Access_expiring_in_3_days_is_refreshed_and_marked_connected()
    {
        var ct = TestContext.Current.CancellationToken;
        var user  = await InsertUserAsync();
        var cloud = await InsertDoCloudAsync(user.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(3)));

        var doClient = new FakeDigitalOceanOAuthClient
        {
            OnRefresh = _ => new DoTokenResponse { AccessToken = "fresh-access", RefreshToken = "fresh-refresh", ExpiresIn = 2592000 },
        };
        var bundle = new CloudSecretBundle(Db, Clock);
        var refresher = new DigitalOceanTokenRefresher(
            Db, bundle, doClient, Clock, NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);
        Db.ChangeTracker.Clear();

        doClient.RefreshedTokens.ShouldBe(["old-refresh"]);
        (await bundle.GetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,  SagaTestSeed.MakeDek(), ct)).ShouldBe("fresh-access");
        (await bundle.GetAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh, SagaTestSeed.MakeDek(), ct)).ShouldBe("fresh-refresh");
        var cloudAfter = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        cloudAfter.ConnectionStatus.ShouldBe("connected");
    }

    [Fact]
    public async Task Access_expiring_far_out_is_not_refreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        var user  = await InsertUserAsync();
        await InsertDoCloudAsync(user.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(30)));

        var doClient = new FakeDigitalOceanOAuthClient();
        var refresher = new DigitalOceanTokenRefresher(
            Db, new CloudSecretBundle(Db, Clock), doClient, Clock,
            NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);

        doClient.RefreshedTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refresh_failure_flips_connection_status_to_needs_reauth()
    {
        var ct = TestContext.Current.CancellationToken;
        var user  = await InsertUserAsync();
        var cloud = await InsertDoCloudAsync(user.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(2)));

        var doClient = new FakeDigitalOceanOAuthClient
        {
            OnRefresh = _ => throw new DigitalOceanOAuthRefreshFailedException("revoked", 401),
        };
        var refresher = new DigitalOceanTokenRefresher(
            Db, new CloudSecretBundle(Db, Clock), doClient, Clock,
            NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);
        Db.ChangeTracker.Clear();

        var cloudAfter = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        cloudAfter.ConnectionStatus.ShouldBe("needs_reauth");
    }
}
