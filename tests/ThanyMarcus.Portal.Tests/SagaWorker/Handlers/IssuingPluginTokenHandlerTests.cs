using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Events;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.CloudManagement.PluginTokens.Sync;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class IssuingPluginTokenHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    private const string AdminToken = "real-admin-token";

    [Fact]
    public async Task Happy_path_writes_metadata_publishes_event_and_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SeedAsync(dpp, withEncryptedAdminToken: true, ct);

        var cloudClient = new RecordingCloudClient();
        var eventBus = new RecordingEventBus();
        var handler = BuildHandler(dpp, cloudClient, eventBus);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.Succeeded);

        var meta = await Db.PluginTokenMetadata.SingleAsync(p => p.CloudId == cloud.Id, ct);
        meta.TokenHash.Length.ShouldBe(32);
        meta.Name.ShouldBe("plugin");

        cloudClient.Calls.Count.ShouldBe(1);
        cloudClient.Calls[0].cloudAdminToken.ShouldBe(AdminToken);
        cloudClient.Calls[0].tokenHash.ShouldBe(meta.TokenHash);

        eventBus.Calls.Count.ShouldBe(1);
        eventBus.Calls[0].rawToken.ShouldStartWith("tm_");
        eventBus.Calls[0].deepLink.ShouldStartWith("obsidian://thany-marcus-connect");

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningCompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Retries_on_5xx_then_fails_after_max_attempts()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SeedAsync(dpp, withEncryptedAdminToken: true, ct);

        var cloudClient = new RecordingCloudClient { ThrowStatus = 500 };
        var eventBus = new RecordingEventBus();
        var handler = BuildHandler(dpp, cloudClient, eventBus, maxAttempts: 2);

        // attempt_count = 1 → throw, reschedule (attempts -> 2)
        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();
        var afterFirst = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        afterFirst.Status.ShouldBe(SagaStatus.IssuingPluginToken);
        afterFirst.AttemptCount.ShouldBe<short>(2);
        afterFirst.LastError!.ShouldContain("500");

        // attempt_count = 2 → throw, MaxAttempts reached → FailedPluginToken
        await handler.HandleAsync(afterFirst, ct);
        Db.ChangeTracker.Clear();
        var afterSecond = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        afterSecond.Status.ShouldBe(SagaStatus.FailedPluginToken);
        eventBus.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Non_retryable_401_dead_letters_immediately()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SeedAsync(dpp, withEncryptedAdminToken: true, ct);

        var cloudClient = new RecordingCloudClient { ThrowStatus = 401 };
        var handler = BuildHandler(dpp, cloudClient, new RecordingEventBus());

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();
        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.FailedPluginToken);
    }

    [Fact]
    public async Task Missing_admin_token_dead_letters_with_specific_error()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SeedAsync(dpp, withEncryptedAdminToken: false, ct);

        var cloudClient = new RecordingCloudClient();
        var handler = BuildHandler(dpp, cloudClient, new RecordingEventBus());

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();
        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.FailedPluginToken);
        reloaded.LastError.ShouldBe("missing_admin_token");
        cloudClient.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sse_event_emitted_exactly_once_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var dpp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SeedAsync(dpp, withEncryptedAdminToken: true, ct);

        var eventBus = new RecordingEventBus();
        var handler = BuildHandler(dpp, new RecordingCloudClient(), eventBus);
        await handler.HandleAsync(job, ct);

        eventBus.Calls.Count.ShouldBe(1);
    }

    private IssuingPluginTokenHandler BuildHandler(
        IDataProtectionProvider dpp,
        IPortalToCloudPluginTokenClient cloudClient,
        IProvisioningEventBus eventBus,
        int maxAttempts = 3)
    {
        var optsMonitor = new StaticOptionsMonitor(new PluginTokenSyncOptions
        {
            HttpTimeoutSeconds = 10,
            MaxAttempts = maxAttempts,
        });
        return new IssuingPluginTokenHandler(
            Db, Clock,
            new CloudAdminTokenAccessor(Db, dpp),
            cloudClient,
            eventBus,
            optsMonitor,
            NullLogger<IssuingPluginTokenHandler>.Instance);
    }

    private async Task<(User user, Cloud cloud, ProvisioningJob job)> SeedAsync(
        EphemeralDataProtectionProvider dpp, bool withEncryptedAdminToken, CancellationToken ct)
    {
        var (user, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dpp, status: SagaStatus.IssuingPluginToken, ct: ct);
        if (withEncryptedAdminToken)
        {
            var trackedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
            var protector = dpp.CreateProtector(CloudAdminTokenAccessor.DataProtectionPurpose);
            trackedJob.AdminTokenCiphertext = protector.Protect(Encoding.UTF8.GetBytes(AdminToken));
            await Db.SaveChangesAsync(ct);
        }
        Db.ChangeTracker.Clear();
        return (user, cloud, job);
    }

    private sealed class RecordingCloudClient : IPortalToCloudPluginTokenClient
    {
        public List<(string cloudUrl, string cloudAdminToken, byte[] tokenHash, string label)> Calls { get; } = new();
        public int? ThrowStatus { get; set; }

        public Task<Guid> PostAsync(string cloudUrl, string cloudAdminToken, byte[] tokenHashBytes, string label, CancellationToken ct)
        {
            Calls.Add((cloudUrl, cloudAdminToken, tokenHashBytes, label));
            if (ThrowStatus is int s)
            {
                throw new PluginTokenSyncException($"cloud returned {s}", statusCode: s);
            }
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task RevokeAsync(string cloudUrl, string cloudAdminToken, byte[] tokenHashBytes, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class RecordingEventBus : IProvisioningEventBus
    {
        public List<(Guid cloudId, string rawToken, string deepLink)> Calls { get; } = new();

        public Task PublishPluginTokenIssuedAsync(Guid cloudId, string rawToken, string deepLink, CancellationToken ct)
        {
            Calls.Add((cloudId, rawToken, deepLink));
            return Task.CompletedTask;
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
