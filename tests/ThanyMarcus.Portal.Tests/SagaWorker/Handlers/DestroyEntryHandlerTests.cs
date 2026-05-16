using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;
using ThanyMarcus.Portal.Tests.Infrastructure;

namespace ThanyMarcus.Portal.Tests.SagaWorker.Handlers;

public sealed class DestroyEntryHandlerTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Cloud_with_subdomain_transitions_to_rolling_back_dns()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.Destroying,
            kind: SagaKinds.Destroy,
            ct: ct);

        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        trackedCloud.Subdomain = "abc12345";
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new DestroyEntryHandler(Db, Clock);
        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackDns);
        reloaded.EventsLog.RootElement.GetRawText().ShouldContain("user_destroy");
    }

    [Fact]
    public async Task Cloud_without_subdomain_transitions_to_rolling_back_tf()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.Destroying,
            kind: SagaKinds.Destroy,
            ct: ct);

        var handler = new DestroyEntryHandler(Db, Clock);
        await handler.HandleAsync(job, ct);
        Db.ChangeTracker.Clear();

        var reloaded = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        reloaded.Status.ShouldBe(SagaStatus.RollingBackTf);
    }

    [Fact]
    public async Task Sees_soft_deleted_clouds_via_ignore_query_filters()
    {
        // Defensive: if a destroy is re-claimed after the cleanup tx committed (worker crash window),
        // the entry handler must still be able to load the cloud row.
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, cloud, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.Destroying,
            kind: SagaKinds.Destroy,
            ct: ct);

        var trackedCloud = await Db.Clouds.IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id, ct);
        trackedCloud.Subdomain = "abc12345";
        trackedCloud.DestroyedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(ct);
        Db.ChangeTracker.Clear();

        var handler = new DestroyEntryHandler(Db, Clock);
        await Should.NotThrowAsync(async () => await handler.HandleAsync(job, ct));
    }
}
