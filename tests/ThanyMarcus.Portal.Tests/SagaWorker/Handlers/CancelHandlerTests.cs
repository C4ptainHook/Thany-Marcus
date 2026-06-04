using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Auth.DigitalOcean;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.CloudManagement.Secrets;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Portal.Tests.SagaWorker.Fakes;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class CancelHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Defers_while_sibling_create_is_non_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.TfApplying,
            kind: SagaKinds.Create,
            ct: ct);

        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        var tf = new FakeTerraformRunner();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        // Cancel job parks itself; the create saga self-compensates instead.
        tf.Calls.ShouldBeEmpty();

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Pending);
        SagaStatus.IsTerminal(reloadedCancel.Status).ShouldBeFalse();

        var reloadedCreate = await Db.ProvisioningJobs.SingleAsync(j => j.Id == createJob.Id, ct);
        reloadedCreate.Status.ShouldBe(SagaStatus.TfApplying);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.TfApplying);
        reloadedCloud.CancelRequestedAt.ShouldNotBeNull();
        reloadedCloud.DestroyedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Finalizes_clean_when_create_already_compensated()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.TfPlanning,
            kind: SagaKinds.Create,
            ct: ct);

        var trackedCreate = await Db.ProvisioningJobs.SingleAsync(j => j.Id == createJob.Id, ct);
        trackedCreate.Status = SagaStatus.RolledBack;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        var tf = new FakeTerraformRunner();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        // Rollback already destroyed everything: no Terraform from the cancel job.
        tf.Calls.ShouldBeEmpty();

        var reloadedCreate = await Db.ProvisioningJobs.SingleAsync(j => j.Id == createJob.Id, ct);
        reloadedCreate.Status.ShouldBe(SagaStatus.RolledBack);

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Succeeded);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
        reloadedCloud.ProvisioningCompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task No_sibling_create_with_empty_state_finalizes_without_destroy()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud) = await SeedUserAndCloudAsync(SagaStatus.FailedTf, ct);
        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        // Pre-create the workdir so the handler does not need to render.
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", cancelJob.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueHasResources(false);
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        // Reliable check: init + workspace select before state list, but nothing to destroy.
        tf.Calls.ShouldContain(c => c.Command == "init");
        tf.Calls.ShouldContain(c => c.Command == "workspace-select-only");
        tf.Calls.ShouldContain(c => c.Command == "state-list");
        tf.Calls.ShouldNotContain(c => c.Command == "destroy");

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Succeeded);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Failed_create_with_live_resources_invokes_destroy()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.FailedTf,
            kind: SagaKinds.Create,
            ct: ct);
        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", createJob.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueHasResources(true);
        tf.QueueDestroyOk();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        tf.Calls.ShouldContain(c => c.Command == "workspace-select-only");
        tf.Calls.ShouldContain(c => c.Command == "state-list");
        tf.Calls.ShouldContain(c => c.Command == "destroy");

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.ProvisioningError.ShouldBeNull();
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Destroy_failure_under_cap_reschedules_and_does_not_cancel()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.FailedTf,
            kind: SagaKinds.Create,
            ct: ct);
        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", createJob.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueHasResources(true);
        tf.QueueDestroyFailure();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Pending);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        // A failed destroy must NOT be hidden behind a green "cancelled".
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.FailedTf);
        reloadedCloud.DestroyedAt.ShouldBeNull();
        reloadedCloud.ProvisioningError.ShouldNotBeNull();
        reloadedCloud.ProvisioningError.ShouldContain("destroy failed");
    }

    [Fact]
    public async Task Destroy_failure_over_cap_lands_failed_destroy()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.FailedTf,
            kind: SagaKinds.Create,
            ct: ct);
        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        // Four prior failures already recorded: this attempt is the fifth (the cap).
        var trackedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        trackedCancel.EventsLog.Dispose();
        trackedCancel.EventsLog = JsonDocument.Parse(
            "[{\"event\":\"cancel_destroy_failed\"},{\"event\":\"cancel_destroy_failed\"}," +
            "{\"event\":\"cancel_destroy_failed\"},{\"event\":\"cancel_destroy_failed\"}]");
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", createJob.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueHasResources(true);
        tf.QueueDestroyFailure();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.FailedDestroy);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.FailedDestroy);
        reloadedCloud.DestroyedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Missing_step_up_with_live_resources_reschedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.FailedTf,
            kind: SagaKinds.Create,
            seedUnlock: false,
            ct: ct);
        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", createJob.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueInitOk();
        tf.QueueHasResources(true);
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        // No DEK ⇒ a real destroy can't authenticate; retry rather than proceed or fake success.
        tf.Calls.ShouldNotContain(c => c.Command == "destroy");

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Pending);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.FailedTf);
        reloadedCloud.DestroyedAt.ShouldBeNull();
    }

    private async Task<ProvisioningJob> SeedCancelJobAsync(Guid cloudId, Guid userId, CancellationToken ct)
    {
        var now = Clock.GetCurrentInstant();
        var job = new ProvisioningJob
        {
            CloudId = cloudId,
            UserId = userId,
            Kind = SagaKinds.Cancel,
            Payload = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
            Status = SagaStatus.Pending,
            NextVisibleAt = now,
            AttemptCount = 1,
            EventsLog = JsonDocument.Parse("[]"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.ProvisioningJobs.Add(job);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return job;
    }

    private async Task<(User user, Cloud cloud)> SeedUserAndCloudAsync(string status, CancellationToken ct)
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
            UserId = user.Id,
            Name = "c",
            Provider = "stub",
            Region = "nyc3",
            Hostname = $"h-{Guid.NewGuid():N}.example.com",
            ProvisioningStatus = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Db.Clouds.Add(cloud);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();
        return (user, cloud);
    }

    private CancelHandler BuildHandler(IDataProtectionProvider dp, global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.ITerraformRunner tf, string workspaceBase)
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
        var layout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new CancelHandler(
            Db, Clock, credentials, providers, tf,
            layout, config, NullLogger<CancelHandler>.Instance);
    }
}
