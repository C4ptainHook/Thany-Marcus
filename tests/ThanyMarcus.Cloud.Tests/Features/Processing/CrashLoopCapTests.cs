using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

[Collection(PostgresCollection.Name)]
public sealed class CrashLoopCapTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Reclaim_of_stale_lease_increments_consecutive_crashes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, jobId) = await JobOrchestratorWorkerTests.SeedJobAsync(
            postgres,
            IngestJobStatus.Composing,
            leaseOwner: "ghost-worker/deadbeef",
            leaseExpiresAt: SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromSeconds(120)));

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldNotBeNull();
        claimed!.Id.ShouldBe(jobId);
        claimed.ConsecutiveCrashes.ShouldBe((short)1);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var dbJob = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        dbJob.ConsecutiveCrashes.ShouldBe((short)1);
    }

    [Fact]
    public async Task Clean_reclaim_does_not_increment_consecutive_crashes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, jobId) = await JobOrchestratorWorkerTests.SeedJobAsync(
            postgres,
            IngestJobStatus.Composing,
            leaseOwner: null,
            leaseExpiresAt: null);

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldNotBeNull();
        claimed!.Id.ShouldBe(jobId);
        claimed.ConsecutiveCrashes.ShouldBe((short)0);
    }

    [Fact]
    public async Task Initial_queued_claim_does_not_increment_consecutive_crashes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await JobOrchestratorWorkerTests.SeedJobAsync(postgres, IngestJobStatus.Queued);

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldNotBeNull();
        claimed!.ConsecutiveCrashes.ShouldBe((short)0);
    }

    [Fact]
    public async Task Dispatch_with_crashes_at_or_above_cap_terminates_to_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobWithCrashesAsync(
            postgres,
            IngestJobStatus.Composing,
            consecutiveCrashes: IngestPhaseDispatcher.DefaultMaxConsecutiveCrashes);

        var job = await LoadJobAsync(postgres, jobId);
        await using var scope = sp.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IngestPhaseDispatcher>();
        await dispatcher.DispatchAsync(job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.FailedComposition);
        after.FinishedAt.ShouldNotBeNull();
        after.LastError.ShouldNotBeNull();
        after.LastError!.ShouldContain("crash_loop_cap_exceeded");

        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.Status.ShouldBe(NoteStatus.Failed);
    }

    [Fact]
    public async Task Dispatch_below_cap_proceeds_normally_and_resets_crashes()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (_, jobId) = await SeedClaimedJobWithCrashesAsync(
            postgres,
            IngestJobStatus.Composing,
            consecutiveCrashes: (short)(IngestPhaseDispatcher.DefaultMaxConsecutiveCrashes - 1));

        var job = await LoadJobAsync(postgres, jobId);
        await using var scope = sp.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IngestPhaseDispatcher>();
        await dispatcher.DispatchAsync(job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);
        after.ConsecutiveCrashes.ShouldBe((short)0);
    }

    private static async Task<(Guid noteId, Guid jobId)> SeedClaimedJobWithCrashesAsync(
        PostgresFixture postgres, string status, short consecutiveCrashes)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id           = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt   = now,
            Status       = NoteStatus.Processing,
            BodyInput    = "test body",
            CreatedAt    = now,
            UpdatedAt    = now,
        };
        var job = new IngestJob
        {
            Id                  = Guid.CreateVersion7(),
            NoteId              = note.Id,
            Status              = status,
            Attempts            = 1,
            ConsecutiveCrashes  = consecutiveCrashes,
            LeaseOwner          = "test-worker/feedbeef",
            LeaseExpiresAt      = now.Plus(Duration.FromSeconds(60)),
            ScheduledAt         = now,
            StartedAt           = now,
            CreatedAt           = now,
            UpdatedAt           = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync();
        return (note.Id, job.Id);
    }

    private static async Task<IngestJob> LoadJobAsync(PostgresFixture postgres, Guid jobId)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        return await db.IngestJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }
}
