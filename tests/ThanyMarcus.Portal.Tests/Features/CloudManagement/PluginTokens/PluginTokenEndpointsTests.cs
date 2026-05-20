using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.Features.CloudManagement.PluginTokens;

public sealed class PluginTokenEndpointsTests(PostgresFixture postgres) : FactoryDbTestBase(postgres)
{
    private const string AdminToken = "test-cloud-admin-token";

    [Fact]
    public async Task Reissue_posts_new_hash_to_cloud_revokes_old_and_returns_raw_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, oldHash) = await SeedWithAdminTokenAndActiveMetadataAsync(ct);

        var recorder = new RecordingCloudClient();
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id)
            .WithCloudClient(recorder).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var bodyDoc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var rawToken = bodyDoc.RootElement.GetProperty("token").GetString()!;
        rawToken.ShouldStartWith("tm_");
        bodyDoc.RootElement.GetProperty("cloudUrl").GetString().ShouldBe($"https://{cloud.Hostname}");

        recorder.Posts.Count.ShouldBe(1);
        recorder.Posts[0].cloudUrl.ShouldBe($"https://{cloud.Hostname}/admin/plugin-tokens");
        recorder.Posts[0].adminToken.ShouldBe(AdminToken);
        recorder.Posts[0].label.ShouldBe("plugin");
        var newHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        recorder.Posts[0].hash.ShouldBe(newHash);

        recorder.Revokes.Count.ShouldBe(1);
        recorder.Revokes[0].cloudUrl.ShouldBe($"https://{cloud.Hostname}/admin/plugin-tokens/revoke");
        recorder.Revokes[0].hash.ShouldBe(oldHash);

        Db.ChangeTracker.Clear();
        var rows = await Db.PluginTokenMetadata
            .Where(p => p.CloudId == cloud.Id)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        rows.Count.ShouldBe(2);
        rows[0].RevokedAt.ShouldNotBeNull();
        rows[1].RevokedAt.ShouldBeNull();
        rows[1].TokenHash.ShouldBe(newHash);
    }

    [Fact]
    public async Task Reissue_does_not_persist_metadata_when_cloud_post_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, oldHash) = await SeedWithAdminTokenAndActiveMetadataAsync(ct);

        var recorder = new RecordingCloudClient { PostStatus = 503 };
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id)
            .WithCloudClient(recorder).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        Db.ChangeTracker.Clear();
        var rows = await Db.PluginTokenMetadata
            .Where(p => p.CloudId == cloud.Id)
            .ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].RevokedAt.ShouldBeNull();
        rows[0].TokenHash.ShouldBe(oldHash);
        recorder.Revokes.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reissue_returns_503_when_cloud_has_no_admin_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, _) = await SeedAsync(encryptAdminToken: false, ct);

        var recorder = new RecordingCloudClient();
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id)
            .WithCloudClient(recorder).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        recorder.Posts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Reissue_returns_404_for_unknown_cloud()
    {
        var ct = TestContext.Current.CancellationToken;
        var recorder = new RecordingCloudClient();
        using var client = Factory.WithClock(Clock).WithTestAuth(Guid.NewGuid())
            .WithCloudClient(recorder).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{Guid.NewGuid()}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reissue_returns_403_for_wrong_owner()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, cloud, _) = await SeedWithAdminTokenAndActiveMetadataAsync(ct);

        var recorder = new RecordingCloudClient();
        using var client = Factory.WithClock(Clock).WithTestAuth(Guid.NewGuid())
            .WithCloudClient(recorder).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Reissue_completes_even_when_cloud_revoke_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        var (user, cloud, _) = await SeedWithAdminTokenAndActiveMetadataAsync(ct);

        var recorder = new RecordingCloudClient { RevokeStatus = 500 };
        using var client = Factory.WithClock(Clock).WithTestAuth(user.Id)
            .WithCloudClient(recorder).CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/clouds/{cloud.Id}/plugin-tokens", UriKind.Relative),
            content: null, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        recorder.Posts.Count.ShouldBe(1);
        recorder.Revokes.Count.ShouldBe(1);

        Db.ChangeTracker.Clear();
        var rows = await Db.PluginTokenMetadata
            .Where(p => p.CloudId == cloud.Id)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
        rows.Count.ShouldBe(2);
        rows[0].RevokedAt.ShouldNotBeNull();
        rows[1].RevokedAt.ShouldBeNull();
    }

    private async Task<(User user, Cloud cloud, byte[] oldHash)> SeedWithAdminTokenAndActiveMetadataAsync(
        CancellationToken ct)
    {
        var (user, cloud, _) = await SeedAsync(encryptAdminToken: true, ct);

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

    private async Task<(User user, Cloud cloud, byte[]? encrypted)> SeedAsync(
        bool encryptAdminToken, CancellationToken ct)
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

        byte[]? encrypted = null;
        if (encryptAdminToken)
        {
            using var scope = Factory.Services.CreateScope();
            var dpp = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
            encrypted = protector.Protect(Encoding.UTF8.GetBytes(AdminToken));
        }

        var cloud = new Cloud
        {
            UserId                    = user.Id,
            Name                      = "test",
            Provider                  = "digitalocean",
            Region                    = "nyc3",
            Hostname                  = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus        = SagaStatus.Succeeded,
            EncryptedCloudAdminToken  = encrypted,
            CreatedAt                 = now,
            UpdatedAt                 = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud, encrypted);
    }

    private sealed class RecordingCloudClient : IPortalToCloudPluginTokenClient
    {
        public List<(string cloudUrl, string adminToken, byte[] hash, string label)> Posts { get; } = new();
        public List<(string cloudUrl, string adminToken, byte[] hash)> Revokes { get; } = new();
        public int? PostStatus { get; set; }
        public int? RevokeStatus { get; set; }

        public Task<Guid> PostAsync(string cloudUrl, string cloudAdminToken,
            byte[] tokenHashBytes, string label, CancellationToken ct)
        {
            Posts.Add((cloudUrl, cloudAdminToken, tokenHashBytes, label));
            if (PostStatus is int s)
                throw new PluginTokenSyncException($"cloud returned {s}", statusCode: s);
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task RevokeAsync(string cloudUrl, string cloudAdminToken,
            byte[] tokenHashBytes, CancellationToken ct)
        {
            Revokes.Add((cloudUrl, cloudAdminToken, tokenHashBytes));
            if (RevokeStatus is int s)
                throw new PluginTokenSyncException($"cloud returned {s}", statusCode: s);
            return Task.CompletedTask;
        }
    }
}

internal static class FactoryCloudClientExtensions
{
    public static WebApplicationFactory<Program> WithCloudClient(
        this WebApplicationFactory<Program> factory,
        IPortalToCloudPluginTokenClient client)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IPortalToCloudPluginTokenClient>();
            s.AddSingleton(client);
        }));
}
