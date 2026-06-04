using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Features.Provisioning.Pricing;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class TfApplyingHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Apply_success_transitions_to_dns_creating_with_tf_outputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, status: SagaStatus.TfApplying, ct: ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        SeedPlanFile(workspaceBase.Path, job.Id);

        var tf = new FakeTerraformRunner();
        tf.QueueApplyOk();
        tf.QueueOutputJson("{\"ip\":{\"value\":\"203.0.113.1\",\"type\":\"string\"}}");

        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.DnsCreating);
        reloaded.TfOutputs.ShouldNotBeNull();
        tf.Calls.ShouldContain(c => c.Command == "apply");
        tf.Calls.ShouldContain(c => c.Command == "output");
    }

    [Fact]
    public async Task Apply_failure_transitions_to_rolling_back_tf_with_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, status: SagaStatus.TfApplying, ct: ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        SeedPlanFile(workspaceBase.Path, job.Id);

        var tf = new FakeTerraformRunner();
        tf.QueueApplyFailure();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackTf);
        reloaded.EventsLog.RootElement.GetRawText().ShouldContain("tf_apply_failed");
    }

    [Fact]
    public async Task Apply_success_stamps_pricing_on_digitalocean_cloud()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp, status: SagaStatus.TfApplying, provider: "digitalocean", ct: ct);

        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        await connections.SaveAsync(user.Id, "do-access-token", "do-refresh-token",
            Clock.GetCurrentInstant().Plus(Duration.FromDays(30)), SagaTestSeed.MakeDek(), ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        SeedPlanFile(workspaceBase.Path, job.Id);

        var tf = new FakeTerraformRunner();
        tf.QueueApplyOk();
        tf.QueueOutputJson("{\"ip\":{\"value\":\"203.0.113.7\",\"type\":\"string\"}}");

        var catalog = new StubDoSizesCatalog(new DoSize("stub-size", 48.00m, 0.07143m, 8192, 4, 160));
        var handler = BuildHandler(dp, tf, workspaceBase.Path, catalog);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        reloaded.PriceMonthlyUsd.ShouldBe(48.00m);
        reloaded.PriceHourlyUsd.ShouldBe(0.07143m);
        reloaded.PriceCurrency.ShouldBe("USD");
        reloaded.PricedAt.ShouldNotBeNull();
        reloaded.PricedSource.ShouldBe("do_api_v2_sizes");
    }

    [Fact]
    public async Task Apply_succeeds_when_pricing_lookup_throws_and_leaves_columns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp, status: SagaStatus.TfApplying, provider: "digitalocean", ct: ct);

        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        await connections.SaveAsync(user.Id, "do-access-token", "do-refresh-token",
            Clock.GetCurrentInstant().Plus(Duration.FromDays(30)), SagaTestSeed.MakeDek(), ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        SeedPlanFile(workspaceBase.Path, job.Id);

        var tf = new FakeTerraformRunner();
        tf.QueueApplyOk();
        tf.QueueOutputJson("{\"ip\":{\"value\":\"203.0.113.8\",\"type\":\"string\"}}");

        var handler = BuildHandler(dp, tf, workspaceBase.Path, new ThrowingDoSizesCatalog());

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.DnsCreating);

        var reloaded = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
        reloaded.PriceMonthlyUsd.ShouldBeNull();
        reloaded.PriceHourlyUsd.ShouldBeNull();
        reloaded.PricedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Cancel_requested_routes_to_rolling_back_tf_without_apply()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, status: SagaStatus.TfApplying, ct: ct);

        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        trackedCloud.CancelRequestedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        var tf = new FakeTerraformRunner();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackTf);
        tf.Calls.ShouldNotContain(c => c.Command == "apply");
    }

    private static void SeedPlanFile(string baseDir, Guid jobId)
    {
        var dir = Path.Combine(baseDir, "jobs", jobId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plan.tfplan"), "fake-plan");
    }

    private TfApplyingHandler BuildHandler(
        IDataProtectionProvider dp,
        FakeTerraformRunner tf,
        string workspaceBase,
        IDoSizesCatalog? doSizesCatalog = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkspaceBase"] = workspaceBase,
                ["Provisioning:TerraformModulesDir"] = workspaceBase,
                ["Provisioning:DefaultSize"] = "stub-size",
                ["ConnectionStrings:Portal"] = Postgres.ConnectionString,
            }).Build();

        var credentials = TestSagaCredentials.Source(Db, dp, Clock);
        var providers = TestProvisioningProviders.Registry(Db, Clock);
        var connections = new DigitalOceanOAuthConnections(Db, Clock);
        var workspaceLayout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new TfApplyingHandler(
            Db, Clock, credentials, providers, connections, tf,
            workspaceLayout, doSizesCatalog ?? new NoopDoSizesCatalog(), config, NullLogger<TfApplyingHandler>.Instance);
    }

    private sealed class NoopDoSizesCatalog : IDoSizesCatalog
    {
        public Task<DoSize?> GetAsync(string slug, string oauthToken, CancellationToken ct) =>
            Task.FromResult<DoSize?>(null);
    }

    private sealed class StubDoSizesCatalog(DoSize size) : IDoSizesCatalog
    {
        public Task<DoSize?> GetAsync(string slug, string oauthToken, CancellationToken ct) =>
            Task.FromResult<DoSize?>(size.Slug == slug ? size : null);
    }

    private sealed class ThrowingDoSizesCatalog : IDoSizesCatalog
    {
        public Task<DoSize?> GetAsync(string slug, string oauthToken, CancellationToken ct) =>
            throw new HttpRequestException("simulated DO outage");
    }
}
