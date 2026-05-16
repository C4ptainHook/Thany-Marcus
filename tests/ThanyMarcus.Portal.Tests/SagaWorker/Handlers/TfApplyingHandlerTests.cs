using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;
using ThanyMarcus.Portal.Api.Features.Provisioning;
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

    private static void SeedPlanFile(string baseDir, Guid jobId)
    {
        var dir = Path.Combine(baseDir, "jobs", jobId.ToString());
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plan.tfplan"), "fake-plan");
    }

    private TfApplyingHandler BuildHandler(
        IDataProtectionProvider dp,
        FakeTerraformRunner tf,
        string workspaceBase)
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
        var workspaceLayout = new WorkspaceLayout(config, NullLogger<WorkspaceLayout>.Instance);

        return new TfApplyingHandler(
            Db, Clock, unlockCache, providerVault, tf,
            workspaceLayout, config, NullLogger<TfApplyingHandler>.Instance);
    }
}
