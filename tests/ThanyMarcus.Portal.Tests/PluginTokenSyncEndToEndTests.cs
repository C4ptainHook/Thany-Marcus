using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Events;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker;
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Portal.Tests;

/// <summary>
/// Regression anchor for the PORTAL-014 + CLOUD-005 plugin-token sync path.
///
/// The plan calls for spinning up both Portal.Api and Cloud.Api WebApplicationFactories
/// in the same process. Top-level Programs collide across the two assemblies so we
/// emulate the cloud-side receiver with an HTTP handler that implements the same
/// contract enforced by RequireCloudAdminTokenFilter and AdminPluginTokenEndpoints
/// (covered separately by AdminPluginTokenEndpointsTests on the real cloud).
///
/// Exercised:
///  - Real IssuingPluginTokenHandler
///  - Real PortalToCloudPluginTokenClient HTTP serialization
///  - Real CloudAdminTokenAccessor decrypt path
///  - Real PostgresProvisioningEventBus pg_notify path
///  - Real PluginTokenAuthenticator hash-comparison logic (the cloud-side bearer auth)
///  - Portal Postgres state transitions for plugin_token_metadata + saga status
/// </summary>
public sealed class PluginTokenSyncEndToEndTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private const string AdminToken = "test-cloud-admin-token";

    [Fact]
    public async Task End_to_end_token_lands_on_both_sides_event_emits_and_raw_token_authenticates()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SeedAsync(dpp, ct);

        var fakeCloud = new FakeCloudPluginTokensServer(AdminToken);
        var cloudClient = new PortalToCloudPluginTokenClient(fakeCloud.HttpFactory());

        // Subscribe to the pg_notify channel BEFORE handler runs so we don't miss it.
        var pluginEventTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var listenConn = new NpgsqlConnection(Postgres.ConnectionString);
        await listenConn.OpenAsync(ct);
        listenConn.Notification += (_, e) =>
        {
            if (e.Channel == PostgresProvisioningEventBus.PluginTokenIssuedChannel)
                pluginEventTcs.TrySetResult(e.Payload);
        };
        await using (var listenCmd = new NpgsqlCommand(
            $"LISTEN {PostgresProvisioningEventBus.PluginTokenIssuedChannel};", listenConn))
        {
            await listenCmd.ExecuteNonQueryAsync(ct);
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var listenTask = Task.Run(async () =>
        {
            try { await listenConn.WaitAsync(linked.Token); }
            catch (OperationCanceledException) { }
        }, linked.Token);

        var handler = new IssuingPluginTokenHandler(
            Db, Clock,
            new CloudAdminTokenAccessor(Db, dpp),
            cloudClient,
            new PostgresProvisioningEventBus(Db),
            new StaticOptionsMonitor(new PluginTokenSyncOptions { HttpTimeoutSeconds = 10, MaxAttempts = 3 }),
            NullLogger<IssuingPluginTokenHandler>.Instance);

        await handler.HandleAsync(job, ct);

        Db.ChangeTracker.Clear();
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.Succeeded);

        var meta = await Db.PluginTokenMetadata.SingleAsync(p => p.CloudId == cloud.Id, ct);
        meta.TokenHash.Length.ShouldBe(32);

        fakeCloud.Stored.Count.ShouldBe(1);
        var (cloudRowId, storedHash, storedLabel) = fakeCloud.Stored.Values.Single();
        storedHash.ShouldBe(meta.TokenHash);
        storedLabel.ShouldBe("plugin");
        cloudRowId.ShouldNotBe(Guid.Empty);

        await listenTask;
        pluginEventTcs.Task.IsCompletedSuccessfully.ShouldBeTrue(
            "expected pg_notify('plugin_token_issued', ...) within 5s");

        var payload = await pluginEventTcs.Task;
        using var doc = JsonDocument.Parse(payload);
        var rawToken = doc.RootElement.GetProperty("rawToken").GetString();
        rawToken.ShouldNotBeNullOrEmpty();
        rawToken!.ShouldStartWith("tm_");
        doc.RootElement.GetProperty("deepLink").GetString().ShouldStartWith("obsidian://thany-marcus-connect");

        // Authenticate the raw token against the same hash-comparison logic used by
        // the cloud's RequirePluginAuthFilter (validates the round-trip).
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken));
        presentedHash.ShouldBe(storedHash);
    }

    private async Task<(User user, Cloud cloud, ProvisioningJob job)> SeedAsync(
        EphemeralDataProtectionProvider dpp, CancellationToken ct)
    {
        var (user, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dpp, status: SagaStatus.IssuingPluginToken, ct: ct);
        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
        trackedCloud.EncryptedCloudAdminToken = protector.Protect(Encoding.UTF8.GetBytes(AdminToken));
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud, job);
    }

    private sealed class FakeCloudPluginTokensServer(string adminToken)
    {
        public Dictionary<Guid, (Guid id, byte[] hash, string label)> Stored { get; } = new();

        public IHttpClientFactory HttpFactory() => new Factory(this);

        private async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage req)
        {
            if (req.Headers.Authorization?.Parameter != adminToken
                || req.Headers.Authorization.Scheme != "Bearer")
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }
            if (!req.RequestUri!.AbsolutePath.EndsWith("/admin/plugin-tokens", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            var body = await req.Content!.ReadFromJsonAsync<AdminIssuePluginTokenRequest>();
            if (body is null || string.IsNullOrWhiteSpace(body.Label))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            byte[] hashBytes;
            try
            {
                hashBytes = Convert.FromBase64String(body.TokenHashBase64);
            }
            catch (FormatException)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            if (hashBytes.Length != 32)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }
            var existing = Stored.Values.FirstOrDefault(v => v.hash.AsSpan().SequenceEqual(hashBytes));
            Guid id;
            if (existing.id != Guid.Empty)
            {
                id = existing.id;
                Stored[id] = (id, hashBytes, body.Label);
            }
            else
            {
                id = Guid.CreateVersion7();
                Stored[id] = (id, hashBytes, body.Label);
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new AdminIssuePluginTokenResponse(id)),
            };
        }

        private sealed class Factory(FakeCloudPluginTokensServer server) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(new Handler(server));
        }

        private sealed class Handler(FakeCloudPluginTokensServer server) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => server.HandleAsync(request);
        }
    }

    private sealed class StaticOptionsMonitor(PluginTokenSyncOptions value) : IOptionsMonitor<PluginTokenSyncOptions>
    {
        public PluginTokenSyncOptions CurrentValue { get; } = value;
        public PluginTokenSyncOptions Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<PluginTokenSyncOptions, string?> listener) => NullDisposable.Instance;
        private sealed class NullDisposable : IDisposable
        {
            public static readonly NullDisposable Instance = new();
            public void Dispose() { }
        }
    }
}
