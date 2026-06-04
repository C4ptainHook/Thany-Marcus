using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class TfPlanningHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Success_transitions_to_tf_applying()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, ct: ct);
        Db.ChangeTracker.Clear();

        using var stubModules = StubModulesDir.Create();
        using var workspaceBase = new TempDir();
        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueuePlanOk();
        var handler = BuildHandler(dp, tf, workspaceBase.Path, stubModules.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.TfApplying);
        reloaded.ClaimedBy.ShouldBeNull();
        reloaded.LeaseExpiresAt.ShouldBeNull();
        tf.Calls.ShouldContain(c => c.Command == "plan");
    }

    [Fact]
    public async Task Plan_failure_transitions_to_failed_tf()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, ct: ct);
        Db.ChangeTracker.Clear();

        using var stubModules = StubModulesDir.Create();
        using var workspaceBase = new TempDir();
        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueuePlanFailure();
        var handler = BuildHandler(dp, tf, workspaceBase.Path, stubModules.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.FailedTf);
        reloaded.LastError.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Missing_unlock_transitions_to_failed_tf_without_invoking_terraform()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, seedUnlock: false, ct: ct);
        Db.ChangeTracker.Clear();

        using var stubModules = StubModulesDir.Create();
        using var workspaceBase = new TempDir();
        var tf = new FakeTerraformRunner();
        var handler = BuildHandler(dp, tf, workspaceBase.Path, stubModules.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.FailedTf);
        tf.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Cancel_requested_routes_to_cancelled_without_terraform()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(Db, Clock, dp, ct: ct);

        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        trackedCloud.CancelRequestedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var stubModules = StubModulesDir.Create();
        using var workspaceBase = new TempDir();
        var tf = new FakeTerraformRunner();
        var handler = BuildHandler(dp, tf, workspaceBase.Path, stubModules.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.Cancelled);
        tf.Calls.ShouldBeEmpty();

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
    }

    private TfPlanningHandler BuildHandler(
        IDataProtectionProvider dp,
        FakeTerraformRunner tf,
        string workspaceBase,
        string modulesRoot)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkspaceBase"] = workspaceBase,
                ["Provisioning:TerraformModulesDir"] = modulesRoot,
                ["Provisioning:DefaultSize"] = "stub-size",
                ["ConnectionStrings:Portal"] = Postgres.ConnectionString,
            }).Build();

        var credentials = TestSagaCredentials.Source(Db, dp, Clock);
        var providers = TestProvisioningProviders.Registry(Db, Clock);
        var workspaceLayout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new TfPlanningHandler(
            Db, Clock, credentials, providers, tf,
            workspaceLayout, config, NullLogger<TfPlanningHandler>.Instance);
    }

    internal sealed class StubModulesDir : IDisposable
    {
        public string Path { get; }
        private StubModulesDir(string path) { Path = path; }
        public static StubModulesDir Create()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "stub-mods-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(System.IO.Path.Combine(root, "stub"));
            return new StubModulesDir(root);
        }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir() { Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sw-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path); }
        public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
    }
}
