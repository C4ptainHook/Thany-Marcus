using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.PluginAuth;
using ThanyMarcus.Cloud.Api.Features.Settings;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;
using ThanyMarcus.Shared.CloudAdmin;
using ThanyMarcus.Shared.CloudRecovery;

namespace ThanyMarcus.Cloud.Tests.Features.Recovery;

[Collection(PostgresCollection.Name)]
public sealed class CloudTrustBoundaryTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string CloudAdminToken = "test-cloud-admin-token";
    private CloudApiFactory factory = null!;

    public ValueTask InitializeAsync()
    {
        factory = new CloudApiFactory { ConnectionString = postgres.ConnectionString };
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await factory.DisposeAsync();

    [Fact]
    public async Task Rotate_registers_new_token_and_revokes_caller()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearSettingsAsync(ct);

        var oldRaw = await SeedFirstTokenAsync(client, ct);
        var newRaw = NewRaw();
        var newHash = SHA256.HashData(Encoding.UTF8.GetBytes(newRaw));

        var req = new HttpRequestMessage(HttpMethod.Post, "/api/plugin-tokens/rotate")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(Convert.ToBase64String(newHash), "plugin")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", oldRaw);
        using var resp = await client.SendAsync(req, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPluginTokenAuthenticator>();
        (await auth.AuthenticateAsync($"Bearer {newRaw}", ct)).ShouldNotBeNull();
        (await auth.AuthenticateAsync($"Bearer {oldRaw}", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Rotate_requires_plugin_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearSettingsAsync(ct);

        var hash = SHA256.HashData("whatever"u8);
        using var resp = await client.PostAsJsonAsync(
            new Uri("/api/plugin-tokens/rotate", UriKind.Relative),
            new AdminIssuePluginTokenRequest(Convert.ToBase64String(hash), "plugin"), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Recovery_code_is_show_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearSettingsAsync(ct);

        var raw = await SeedFirstTokenAsync(client, ct);

        using var first = await ProvisionRecoveryAsync(client, raw, ct);
        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await first.Content.ReadFromJsonAsync<ProvisionRecoveryCodeResponse>(ct);
        body!.RecoveryCode.ShouldStartWith("tmr_");

        using var second = await ProvisionRecoveryAsync(client, raw, ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Recovery_redeem_mints_token_rotates_code_and_burns_old_code()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearSettingsAsync(ct);

        var raw = await SeedFirstTokenAsync(client, ct);
        using var provision = await ProvisionRecoveryAsync(client, raw, ct);
        var code = (await provision.Content.ReadFromJsonAsync<ProvisionRecoveryCodeResponse>(ct))!.RecoveryCode;

        using var redeem = await client.PostAsJsonAsync(
            new Uri("/api/recovery/redeem", UriKind.Relative), new RedeemRecoveryRequest(code), ct);
        redeem.StatusCode.ShouldBe(HttpStatusCode.OK);
        var rr = await redeem.Content.ReadFromJsonAsync<RedeemRecoveryResponse>(ct);
        rr!.Token.ShouldStartWith("tm_");
        rr.RecoveryCode.ShouldStartWith("tmr_");
        rr.RecoveryCode.ShouldNotBe(code);

        await using var scope = factory.Services.CreateAsyncScope();
        var auth = scope.ServiceProvider.GetRequiredService<IPluginTokenAuthenticator>();
        (await auth.AuthenticateAsync($"Bearer {rr.Token}", ct)).ShouldNotBeNull();

        using var reuse = await client.PostAsJsonAsync(
            new Uri("/api/recovery/redeem", UriKind.Relative), new RedeemRecoveryRequest(code), ct);
        reuse.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Recovery_redeem_rejects_bad_code()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        using var client = factory.CreateClient();
        await ClearSettingsAsync(ct);

        var raw = await SeedFirstTokenAsync(client, ct);
        using var _ = await ProvisionRecoveryAsync(client, raw, ct);

        using var resp = await client.PostAsJsonAsync(
            new Uri("/api/recovery/redeem", UriKind.Relative), new RedeemRecoveryRequest("tmr_wrong"), ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static string NewRaw() => "tm_" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private static async Task<HttpResponseMessage> ProvisionRecoveryAsync(
        HttpClient client, string pluginToken, CancellationToken ct)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/recovery-code");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pluginToken);
        return await client.SendAsync(req, ct);
    }

    private static async Task<string> SeedFirstTokenAsync(HttpClient client, CancellationToken ct)
    {
        var raw = NewRaw();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var req = new HttpRequestMessage(HttpMethod.Post, "/admin/plugin-tokens")
        {
            Content = JsonContent.Create(new AdminIssuePluginTokenRequest(Convert.ToBase64String(hash), "plugin")),
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAdminToken);
        using var resp = await client.SendAsync(req, ct);
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
        return raw;
    }

    private async Task ClearSettingsAsync(CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CloudDbContext>();
        await db.CloudSettings
            .Where(s => s.Id == CloudSettings.SingletonId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(x => x.BootstrapConsumedAt, (Instant?)null)
                .SetProperty(x => x.RecoveryAnchorHash, (byte[]?)null), ct);
    }
}
