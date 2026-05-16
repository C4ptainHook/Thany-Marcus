using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class RollingBackTfHandlerDestroyTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Destroy_success_soft_deletes_cloud_revokes_tokens_and_deletes_workspace()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.RollingBackTf,
            kind: SagaKinds.Destroy,
            ct: ct);

        var now = Clock.GetCurrentInstant();
        Db.PluginTokenMetadata.AddRange(
            new PluginTokenMetadata { CloudId = cloud.Id, Name = "t1", TokenHash = $"h1-{Guid.NewGuid():N}", CreatedAt = now, UpdatedAt = now },
            new PluginTokenMetadata { CloudId = cloud.Id, Name = "t2", TokenHash = $"h2-{Guid.NewGuid():N}", CreatedAt = now, UpdatedAt = now });
        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        trackedCloud.VmIp = "203.0.113.1";
        trackedCloud.CloudAdminTokenHash = new byte[] { 0xAA, 0xBB };
        trackedCloud.TerraformWorkspace = "abc12345";
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", job.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueDestroyOk();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.RolledBack);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.RolledBack);
        reloadedCloud.VmIp.ShouldBeNull();
        reloadedCloud.CloudAdminTokenHash.ShouldBeNull();
        reloadedCloud.TerraformWorkspace.ShouldBeNull();

        var tokens = await Db.PluginTokenMetadata.Where(p => p.CloudId == cloud.Id).ToListAsync(ct);
        tokens.Count.ShouldBe(2);
        tokens.ShouldAllBe(p => p.RevokedAt != null);

        tf.DeleteWorkspaceCalls.ShouldHaveSingleItem();
        tf.DeleteWorkspaceCalls[0].ShouldBe(cloud.Id.ToString());
    }

    [Fact]
    public async Task Destroy_abandoned_after_max_attempts_marks_failed_destroy_and_does_not_soft_delete()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.RollingBackTf,
            kind: SagaKinds.Destroy,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.AttemptCount = 5;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", job.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueDestroyFailure();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.FailedDestroy);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.DestroyedAt.ShouldBeNull();

        tf.DeleteWorkspaceCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task Destroy_failure_under_max_attempts_reschedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.RollingBackTf,
            kind: SagaKinds.Destroy,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.AttemptCount = 2;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", job.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueDestroyFailure();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.RollingBackTf);
    }

    [Fact]
    public async Task Create_side_rollback_still_lands_in_failed_tf()
    {
        // Regression guard — confirm the create-side rollback path is unchanged.
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.RollingBackTf,
            kind: SagaKinds.Create,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.EventsLog.Dispose();
        tracked.EventsLog = JsonDocument.Parse("[{\"rollback_reason\":\"tf_apply_failed\"}]");
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", job.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueDestroyOk();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.FailedTf);
        tf.DeleteWorkspaceCalls.ShouldBeEmpty();
    }

    private RollingBackTfHandler BuildHandler(IDataProtectionProvider dp, FakeTerraformRunner tf, string workspaceBase)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Provisioning:WorkspaceBase"] = workspaceBase,
                ["Provisioning:TerraformModulesDir"] = workspaceBase,
                ["Provisioning:DefaultSize"] = "stub-size",
                ["ConnectionStrings:Portal"] = Postgres.ConnectionString,
            }).Build();

        var unlockCache = new PostgresInfraOpUnlockCache(Db, dp, Clock);
        var providerVault = new ProviderTokenVault(Db, Clock);
        var layout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new RollingBackTfHandler(
            Db, Clock, unlockCache, providerVault, tf,
            layout, config, NullLogger<RollingBackTfHandler>.Instance);
    }
}
