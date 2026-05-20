using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Tests.Features.Admin.PluginTokens;

[Collection(PostgresCollection.Name)]
public sealed class AdminPluginTokenEndpointsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string CloudAdminToken = "test-cloud-admin-token";
    private CloudApiFactory factory = null!;

    public ValueTask InitializeAsync()
    {
        factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await factory.DisposeAsync();
    }

    [Fact]
    public async Task Posts_valid_hash_returns_200_and_persists_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("plugin-token-1"u8);
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                TokenHashBase64: Convert.ToBase64String(hash), Label: "plugin")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);

        using var resp = await client.SendAsync(req, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AdminIssuePluginTokenResponse>(ct);
        body.ShouldNotBeNull();
        body!.TokenId.ShouldNotBe(Guid.Empty);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        var row = await db.PluginTokens.SingleAsync(p => p.Id == body.TokenId, ct);
        row.TokenHash.ShouldBe(hash);
        row.Label.ShouldBe("plugin");
    }

    [Fact]
    public async Task Duplicate_hash_returns_200_same_token_id_upsert_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("plugin-token-dup"u8);

        async Task<AdminIssuePluginTokenResponse> Post(string label)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
            {
                Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                    TokenHashBase64: Convert.ToBase64String(hash), Label: label)),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
            using var resp = await client.SendAsync(req, ct);
            resp.StatusCode.ShouldBe(HttpStatusCode.OK);
            return (await resp.Content.ReadFromJsonAsync<AdminIssuePluginTokenResponse>(ct))!;
        }

        var first = await Post("plugin");
        var second = await Post("plugin");
        second.TokenId.ShouldBe(first.TokenId);
    }

    [Fact]
    public async Task Hash_wrong_length_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                TokenHashBase64: Convert.ToBase64String(new byte[] { 1, 2, 3 }), Label: "plugin")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);

        using var resp = await client.SendAsync(req, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_label_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("any-token"u8);
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                TokenHashBase64: Convert.ToBase64String(hash), Label: "")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);

        using var resp = await client.SendAsync(req, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task No_auth_header_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("any-token"u8);
        using var resp = await client.PostAsJsonAsync(
            new Uri("/admin/plugin-tokens", UriKind.Relative),
            new AdminIssuePluginTokenRequest(Convert.ToBase64String(hash), "plugin"),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Wrong_admin_token_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("any-token"u8);
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                TokenHashBase64: Convert.ToBase64String(hash), Label: "plugin")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token");

        using var resp = await client.SendAsync(req, ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_sets_revoked_at_and_rejects_future_plugin_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var raw = "tm_revoke_e2e_token";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));

        var post = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                Convert.ToBase64String(hash), "plugin")),
        };
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
        using var postResp = await client.SendAsync(post, ct);
        postResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var revoke = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens/revoke")
        {
            Content = JsonContent.Create(new AdminRevokePluginTokenRequest(
                Convert.ToBase64String(hash))),
        };
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
        using var revokeResp = await client.SendAsync(revoke, ct);
        revokeResp.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPluginTokenAuthenticator>();
        var principal = await auth.AuthenticateAsync($"Bearer {raw}", ct);
        principal.ShouldBeNull();
    }

    [Fact]
    public async Task Revoke_unknown_hash_returns_404()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("never-existed"u8);
        var revoke = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens/revoke")
        {
            Content = JsonContent.Create(new AdminRevokePluginTokenRequest(
                Convert.ToBase64String(hash))),
        };
        revoke.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
        using var resp = await client.SendAsync(revoke, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Revoke_without_auth_returns_401()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var hash = SHA256.HashData("any"u8);
        using var resp = await client.PostAsJsonAsync(
            new Uri("/admin/plugin-tokens/revoke", UriKind.Relative),
            new AdminRevokePluginTokenRequest(Convert.ToBase64String(hash)),
            ct);

        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Posted_hash_authenticates_plugin_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();

        var raw = "tm_authflow_e2e_token";
        var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));

        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                Convert.ToBase64String(hash), "plugin")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
        using var postResp = await client.SendAsync(req, ct);
        postResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPluginTokenAuthenticator>();
        var principal = await auth.AuthenticateAsync($"Bearer {raw}", ct);
        principal.ShouldNotBeNull();
    }
}
