using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class AwaitingCloudCallbackHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Pre_timeout_reschedules_without_transitioning()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.AwaitingCloudCallback,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.PhaseStartedAt = Clock.GetCurrentInstant() - Duration.FromMinutes(1);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new AwaitingCloudCallbackHandler(Db, Clock);
        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.AwaitingCloudCallback);
        reloaded.NextVisibleAt.ShouldBeGreaterThan(Clock.GetCurrentInstant());
    }

    [Fact]
    public async Task Timeout_transitions_to_rolling_back_dns_with_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.AwaitingCloudCallback,
            ct: ct);

        var tracked = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        tracked.PhaseStartedAt = Clock.GetCurrentInstant() - SagaTimeouts.AwaitingCloudCallback - Duration.FromMinutes(1);
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new AwaitingCloudCallbackHandler(Db, Clock);
        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackDns);
        reloaded.EventsLog.RootElement.GetRawText().ShouldContain("callback_timeout");
    }
}
