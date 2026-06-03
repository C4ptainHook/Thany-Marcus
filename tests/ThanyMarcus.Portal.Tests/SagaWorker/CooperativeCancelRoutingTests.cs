using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class CooperativeCancelRoutingTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task No_signal_is_a_no_op()
    {
        var ct = TestContext.Current.CancellationToken;
        var (job, cloud) = await SeedAsync(SagaStatus.TfApplying, ct);

        var routed = await SagaTransitions.TryRouteCancelAsync(Db, Clock, job, cloud, SagaStatus.TfApplying, ct);

        routed.ShouldBeFalse();
        job.Status.ShouldBe(SagaStatus.TfApplying);
    }

    [Theory]
    [InlineData(SagaStatus.Pending)]
    [InlineData(SagaStatus.MintingSpaces)]
    [InlineData(SagaStatus.TfPlanning)]
    public async Task Pre_apply_phases_route_straight_to_cancelled(string phase)
    {
        var ct = TestContext.Current.CancellationToken;
        var (job, cloud) = await SeedAsync(phase, ct, cancelRequested: true);

        var routed = await SagaTransitions.TryRouteCancelAsync(Db, Clock, job, cloud, phase, ct);
        Db.ChangeTracker.Clear();

        routed.ShouldBeTrue();
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.Cancelled);
        var reloadedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        reloadedCloud.ProvisioningStatus.ShouldBe(SagaStatus.Cancelled);
    }

    [Fact]
    public async Task After_apply_without_dns_routes_to_rolling_back_tf()
    {
        var ct = TestContext.Current.CancellationToken;
        var (job, cloud) = await SeedAsync(SagaStatus.TfApplying, ct, cancelRequested: true);

        var routed = await SagaTransitions.TryRouteCancelAsync(Db, Clock, job, cloud, SagaStatus.TfApplying, ct);
        Db.ChangeTracker.Clear();

        routed.ShouldBeTrue();
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.RollingBackTf);
        // The reason is what makes RollingBackTfHandler land the create job on rolled_back.
        reloadedJob.EventsLog.RootElement.GetRawText().ShouldContain("user_cancelled");
    }

    [Theory]
    [InlineData(SagaStatus.DnsCreating)]
    [InlineData(SagaStatus.AwaitingCloudCallback)]
    [InlineData(SagaStatus.AwaitingCert)]
    [InlineData(SagaStatus.IssuingPluginToken)]
    public async Task Post_dns_phases_route_to_rolling_back_dns(string phase)
    {
        var ct = TestContext.Current.CancellationToken;
        var (job, cloud) = await SeedAsync(phase, ct, cancelRequested: true, subdomain: "thany-xyz");

        var routed = await SagaTransitions.TryRouteCancelAsync(Db, Clock, job, cloud, phase, ct);
        Db.ChangeTracker.Clear();

        routed.ShouldBeTrue();
        var reloadedJob = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloadedJob.Status.ShouldBe(SagaStatus.RollingBackDns);
    }

    private async Task<(ProvisioningJob job, Cloud cloud)> SeedAsync(
        string status, CancellationToken ct, bool cancelRequested = false, string? subdomain = null)
    {
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, seededJob) = await SagaTestSeed.SeedAsync(Db, Clock, dp, status: status, ct: ct);

        var cloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == seededJob.CloudId, ct);
        if (cancelRequested) cloud.CancelRequestedAt = Clock.GetCurrentInstant();
        if (subdomain is not null) cloud.Subdomain = subdomain;
        await Db.SaveChangesAsync(ct);

        var job = await Db.ProvisioningJobs.SingleAsync(j => j.Id == seededJob.Id, ct);
        return (job, cloud);
    }
}
