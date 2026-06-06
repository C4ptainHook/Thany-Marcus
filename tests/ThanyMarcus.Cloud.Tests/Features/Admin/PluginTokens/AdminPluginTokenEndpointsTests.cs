using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Settings;
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

    private async Task ClearBootstrapConsumedAsync(CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        await db.CloudSettings
            .Where(s => s.Id == CloudSettings.SingletonId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.BootstrapConsumedAt, (Instant?)null), ct);
    }

    [Fact]
    public async Task Posts_valid_hash_returns_200_and_persists_row()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearBootstrapConsumedAsync(ct);

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
        await ClearBootstrapConsumedAsync(ct);

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
        await ClearBootstrapConsumedAsync(ct);

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
        await ClearBootstrapConsumedAsync(ct);

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
        await ClearBootstrapConsumedAsync(ct);

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
        await ClearBootstrapConsumedAsync(ct);

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
    public async Task Second_distinct_hash_after_consumption_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearBootstrapConsumedAsync(ct);

        async Task<HttpStatusCode> Post(string seed)
        {
            var hash = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
            var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
            {
                Content = JsonContent.Create(new AdminIssuePluginTokenRequest(
                    Convert.ToBase64String(hash), "plugin")),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
            using var resp = await client.SendAsync(req, ct);
            return resp.StatusCode;
        }

        (await Post("tm_first")).ShouldBe(HttpStatusCode.OK);
        (await Post("tm_second")).ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Posted_hash_authenticates_plugin_endpoint()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearBootstrapConsumedAsync(ct);

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
