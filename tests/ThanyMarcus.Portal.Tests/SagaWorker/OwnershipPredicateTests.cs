using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;
using ThanyMarcus.Portal.Tests.Infrastructure;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Portal.Tests.SagaWorker;

public sealed class OwnershipPredicateTests(PostgresFixture postgres) : DbIntegrationTestBase(postgres)
{
    [Fact]
    public async Task Transition_fails_when_another_worker_has_re_claimed_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.TfPlanning,
            ct: ct);

        // Simulate worker A having claimed this row.
        var trackedA = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        trackedA.ClaimedBy = "worker-A";
        trackedA.LeaseExpiresAt = Clock.GetCurrentInstant() + Duration.FromMinutes(2);
        trackedA.TransitionVersion += 1;
        await Db.SaveChangesAsync(ct);

        // Worker B "re-claims" — a separate DbContext so we don't disturb A's tracking.
        var optsB = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var dbB = new PortalDbContext(optsB))
        {
            await dbB.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE provisioning_jobs
                   SET claimed_by         = 'worker-B',
                       lease_expires_at   = now() + interval '2 minutes',
                       transition_version = transition_version + 1,
                       updated_at         = now()
                 WHERE id = {job.Id}", ct);
        }

        // Worker A's tracked entity still has the old TransitionVersion.
        // EF's concurrency check will trigger DbUpdateConcurrencyException,
        // which SagaTransitions translates into SagaOwnershipLostException.
        var ex = await Should.ThrowAsync<SagaOwnershipLostException>(async () =>
        {
            await SagaTransitions.TransitionAsync(
                Db, Clock, trackedA,
                newStatus: SagaStatus.TfApplying,
                nextVisibleDelay: Duration.Zero,
                ct: ct);
        });
        ex.JobId.ShouldBe(job.Id);
        ex.AttemptedWorkerId.ShouldBe("worker-A");

        // Verify worker A's failed write did not mutate the row.
        await using var probeDb = new PortalDbContext(optsB);
        var probe = await probeDb.ProvisioningJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id, ct);
        probe.ClaimedBy.ShouldBe("worker-B");
        probe.Status.ShouldBe(SagaStatus.TfPlanning);
    }

    [Fact]
    public async Task Reschedule_fails_when_another_worker_has_re_claimed_the_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var dp = new EphemeralDataProtectionProvider();
        var (_, _, job) = await SagaTestSeed.SeedAsync(
            Db, Clock, dp,
            status: SagaStatus.AwaitingCert,
            ct: ct);

        var trackedA = await Db.ProvisioningJobs.SingleAsync(j => j.Id == job.Id, ct);
        trackedA.ClaimedBy = "worker-A";
        trackedA.LeaseExpiresAt = Clock.GetCurrentInstant() + Duration.FromMinutes(2);
        trackedA.TransitionVersion += 1;
        await Db.SaveChangesAsync(ct);

        var optsB = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var dbB = new PortalDbContext(optsB))
        {
            await dbB.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE provisioning_jobs
                   SET claimed_by         = 'worker-B',
                       transition_version = transition_version + 1,
                       updated_at         = now()
                 WHERE id = {job.Id}", ct);
        }

        await Should.ThrowAsync<SagaOwnershipLostException>(async () =>
        {
            await SagaTransitions.RescheduleAsync(
                Db, Clock, trackedA,
                nextVisibleDelay: Duration.FromSeconds(30),
                ct: ct);
        });
    }
}
