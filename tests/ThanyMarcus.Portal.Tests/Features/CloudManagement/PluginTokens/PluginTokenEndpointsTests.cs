using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.PluginTokens;

public sealed class PluginTokenEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    [Fact]
    public async Task Reissue_mints_revokes_prior_metadata_and_returns_raw_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, oldHash) = await SeedWithActiveMetadataAsync(ct);

        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var bodyDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var rawToken = bodyDoc.RootElement.GetProperty("token").GetString()!;
        rawToken.ShouldStartWith("tm_");
        bodyDoc.RootElement.GetProperty("cloudUrl").GetString().ShouldBe($"https://{cloud.Hostname}");

        var newHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));

        Db.ChangeTracker.Clear();
        var rows = await Db.PluginTokenMetadata
            .Where(p => p.CloudId == cloud.Id)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        rows.Count.ShouldBe(2);
        rows[0].TokenHash.ShouldBe(oldHash);
        rows[0].RevokedAt.ShouldNotBeNull();
        rows[1].RevokedAt.ShouldBeNull();
        rows[1].TokenHash.ShouldBe(newHash);
    }

    [Fact]
    public async Task Reissue_returns_404_for_unknown_cloud()
    {
        var ct = TestContext.Current.CancellationToken;
        using var client = Factory.WithClock(Clock).WithTestAuth(Guid.NewGuid()).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{Guid.NewGuid()}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reissue_returns_403_for_wrong_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, cloud, _) = await SeedWithActiveMetadataAsync(ct);

        using var client = Factory.WithClock(Clock).WithTestAuth(Guid.NewGuid()).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    private async Task<(User user, Cloud cloud, byte[] oldHash)> SeedWithActiveMetadataAsync(CancellationToken ct)
    {
        var (user, cloud) = await SeedAsync(ct);

        var oldHash = SHA256.HashData(Encoding.UTF8.GetBytes("tm_old_token"));
        var now = Clock.GetCurrentInstant();
        Db.PluginTokenMetadata.Add(new PluginTokenMetadata
        {
            CloudId   = cloud.Id,
            Name      = "plugin",
            TokenHash = oldHash,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud, oldHash);
    }

    private async Task<(User user, Cloud cloud)> SeedAsync(CancellationToken ct)
    {
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

        var cloud = new Cloud
        {
            UserId             = user.Id,
            Name               = "test",
            Provider           = "digitalocean",
            Region             = "nyc3",
            Hostname           = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = SagaStatus.Succeeded,
            CreatedAt          = now,
            UpdatedAt          = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud);
    }
}
