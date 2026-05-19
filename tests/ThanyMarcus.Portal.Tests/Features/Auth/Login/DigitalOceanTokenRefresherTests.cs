using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.Auth.Login;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.Features.Auth.Login;

public sealed class DigitalOceanTokenRefresherTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
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

    private async Task SaveConnectionAsync(Guid userId, Instant accessExpiresAt)
    {
        var ct = TestContext.Current.CancellationToken;
        var dek = SagaTestSeed.MakeDek();
        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        await connections.SaveAsync(userId, "old-access", "old-refresh", accessExpiresAt, dek, ct);
        Db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task Access_expiring_in_3_days_is_refreshed_and_marked_connected()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SaveConnectionAsync(user.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(3)));

        var doClient = new FakeDigitalOceanOAuthClient
        {
            OnRefresh = _ => new DoTokenResponse { AccessToken = "fresh-access", RefreshToken = "fresh-refresh", ExpiresIn = 2592000 },
        };
        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        var refresher = new DigitalOceanTokenRefresher(
            connections, doClient, Clock, NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);
        Db.ChangeTracker.Clear();

        doClient.RefreshedTokens.ShouldBe(["old-refresh"]);
        (await connections.GetAccessTokenAsync(user.Id, SagaTestSeed.MakeDek(), ct)).ShouldBe("fresh-access");
        (await connections.GetRefreshTokenAsync(user.Id, SagaTestSeed.MakeDek(), ct)).ShouldBe("fresh-refresh");
        var info = await connections.GetInfoAsync(user.Id, ct);
        info!.ConnectionStatus.ShouldBe(DigitalOceanConnectionStatus.Connected);
    }

    [Fact]
    public async Task Access_expiring_far_out_is_not_refreshed()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SaveConnectionAsync(user.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(30)));

        var doClient = new FakeDigitalOceanOAuthClient();
        var refresher = new DigitalOceanTokenRefresher(
            new DigitalOceanOAuthConnections(Db, Clock), doClient, Clock,
            NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);

        doClient.RefreshedTokens.ShouldBeEmpty();
    }

    [Fact]
    public async Task Refresh_failure_flips_connection_status_to_needs_reauth()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();
        await SaveConnectionAsync(user.Id, Clock.GetCurrentInstant().Plus(Duration.FromDays(2)));

        var doClient = new FakeDigitalOceanOAuthClient
        {
            OnRefresh = _ => throw new DigitalOceanOAuthRefreshFailedException("revoked", 401),
        };
        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        var refresher = new DigitalOceanTokenRefresher(
            connections, doClient, Clock,
            NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);
        Db.ChangeTracker.Clear();

        var info = await connections.GetInfoAsync(user.Id, ct);
        info!.ConnectionStatus.ShouldBe(DigitalOceanConnectionStatus.NeedsReauth);
    }

    [Fact]
    public async Task No_connection_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        var user = await InsertUserAsync();

        var doClient = new FakeDigitalOceanOAuthClient();
        var refresher = new DigitalOceanTokenRefresher(
            new DigitalOceanOAuthConnections(Db, Clock), doClient, Clock,
            NullLogger<DigitalOceanTokenRefresher>.Instance);

        await refresher.RefreshExpiringAsync(user.Id, SagaTestSeed.MakeDek(), ct);
        doClient.RefreshedTokens.ShouldBeEmpty();
    }
}
