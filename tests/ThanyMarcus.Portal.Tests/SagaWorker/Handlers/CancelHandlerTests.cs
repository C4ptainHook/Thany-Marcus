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
    public async Task Cancel_with_sibling_create_job_marks_both_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.TfPlanning,
            kind: SagaKinds.Create,
            ct: ct);

        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        var tf = new FakeTerraformRunner();
        tf.QueueHasResources(false);
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Succeeded);

        var reloadedCreate = await Db.ProvisioningJobs.SingleAsync(j => j.Id == createJob.Id, ct);
        reloadedCreate.Status.ShouldBe(SagaStatus.Cancelled);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
        reloadedCloud.ProvisioningCompletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancel_with_no_sibling_create_still_marks_cloud_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud) = await SeedUserAndCloudAsync(SagaStatus.FailedTf, ct);
        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        var tf = new FakeTerraformRunner();
        tf.QueueHasResources(false);
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Succeeded);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Cancel_with_terraform_state_invokes_destroy()
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
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", createJob.Id.ToString()));

        var tf = new FakeTerraformRunner();
        tf.QueueHasResources(true);
        tf.QueueInitOk();
        tf.QueueDestroyOk();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        tf.Calls.ShouldContain(c => c.Command == "state-list");
        tf.Calls.ShouldContain(c => c.Command == "destroy");

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.ProvisioningError.ShouldBeNull();
    }

    [Fact]
    public async Task Terraform_destroy_failure_still_completes_cancel_with_provisioning_error()
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
        Directory.CreateDirectory(Path.Combine(workspaceBase.Path, "jobs", createJob.Id.ToString()));

        var tf = new ThrowingTerraformRunner();
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Succeeded);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
        reloadedCloud.DestroyedAt.ShouldNotBeNull();
        reloadedCloud.ProvisioningError.ShouldNotBeNull();
        reloadedCloud.ProvisioningError.ShouldContain("terraform destroy failed");
    }

    [Fact]
    public async Task Handler_is_idempotent_when_create_already_terminal()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (user, cloud, createJob) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.TfPlanning,
            kind: SagaKinds.Create,
            ct: ct);

        var trackedCreate = await Db.ProvisioningJobs.SingleAsync(j => j.Id == createJob.Id, ct);
        trackedCreate.Status = SagaStatus.Cancelled;
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var cancelJob = await SeedCancelJobAsync(cloud.Id, user.Id, ct);

        using var workspaceBase = new TfPlanningHandlerTests.TempDir();
        var tf = new FakeTerraformRunner();
        tf.QueueHasResources(false);
        var handler = BuildHandler(dp, tf, workspaceBase.Path);

        await handler.HandleAsync(cancelJob, ct);
        Db.ChangeTracker.Clear();

        var reloadedCreate = await Db.ProvisioningJobs.SingleAsync(j => j.Id == createJob.Id, ct);
        reloadedCreate.Status.ShouldBe(SagaStatus.Cancelled);

        var reloadedCancel = await Db.ProvisioningJobs.SingleAsync(j => j.Id == cancelJob.Id, ct);
        reloadedCancel.Status.ShouldBe(SagaStatus.Succeeded);

        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
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

        var unlockCache = new PostgresInfraOpUnlockCache(Db, dp, Clock);
        var providerVault = new ProviderTokenVault(Db, Clock);
        var secrets = new CloudSecretBundle(Db, Clock);
        var layout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new CancelHandler(
            Db, Clock, unlockCache, providerVault, secrets, tf,
            layout, config, NullLogger<CancelHandler>.Instance);
    }

    private sealed class ThrowingTerraformRunner : global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.ITerraformRunner
    {
        public Task<bool> HasResourcesAsync(string workdir, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.TerraformResult> InitAsync(
            string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct) =>
            throw new InvalidOperationException("boom: provider unreachable");

        public Task<global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.TerraformResult> PlanAsync(
            string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.TerraformResult> ApplyAsync(
            string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.TerraformResult> DestroyAsync(
            string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct) =>
            throw new NotImplementedException();

        public Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct) =>
            Task.CompletedTask;

        public Task<global::ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform.TerraformResult> DeleteWorkspaceAsync(
            string workdir, string workspaceName, CancellationToken ct) =>
            throw new NotImplementedException();
    }
}
