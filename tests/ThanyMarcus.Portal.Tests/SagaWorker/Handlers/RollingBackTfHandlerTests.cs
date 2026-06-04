using System.Text.Json;
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

public sealed class RollingBackTfHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Apply_failure_origin_lands_in_failed_tf()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunRollbackAsync(rollbackReason: "tf_apply_failed", expected: SagaStatus.FailedTf, ct);
    }

    [Fact]
    public async Task Dns_failure_origin_lands_in_failed_dns()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunRollbackAsync(rollbackReason: "dns_failed", expected: SagaStatus.FailedDns, ct);
    }

    [Fact]
    public async Task Callback_timeout_origin_lands_in_failed_callback()
    {
        var ct = TestContext.Current.CancellationToken;
        await RunRollbackAsync(rollbackReason: "callback_timeout", expected: SagaStatus.FailedCallback, ct);
    }

    [Fact]
    public async Task Destroy_failure_under_attempt_cap_reschedules()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.RollingBackTf,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.EventsLog.Dispose();
        tracked.EventsLog = JsonDocument.Parse("[{\"rollback_reason\":\"tf_apply_failed\"}]");
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

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackTf);
    }

    private async Task RunRollbackAsync(string rollbackReason, string expected, CancellationToken ct)
    {
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.RollingBackTf,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.EventsLog.Dispose();
        tracked.EventsLog = JsonDocument.Parse($"[{{\"rollback_reason\":\"{rollbackReason}\"}}]");
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
        reloaded.Status.ShouldBe(expected);
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

        var credentials = TestSagaCredentials.Source(Db, dp, Clock);
        var providers = TestProvisioningProviders.Registry(Db, Clock);
        var layout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new RollingBackTfHandler(
            Db, Clock, credentials, providers, tf,
            layout, config, NullLogger<RollingBackTfHandler>.Instance);
    }
}
