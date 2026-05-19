using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

[Collection(PostgresCollection.Name)]
public sealed class JobOrchestratorWorkerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_orchestrators_competing_for_one_job_only_one_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (noteId, jobId) = await SeedJobAsync(postgres, IngestJobStatus.Queued);

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestratorA = ProcessingTestHost.NewOrchestrator(sp);
        var orchestratorB = ProcessingTestHost.NewOrchestrator(sp);

        var taskA = orchestratorA.ClaimNextAsync(ct);
        var taskB = orchestratorB.ClaimNextAsync(ct);
        var (claimA, claimB) = (await taskA, await taskB);

        var wins = (claimA is not null ? 1 : 0) + (claimB is not null ? 1 : 0);
        wins.ShouldBe(1);

        var winner = claimA ?? claimB!;
        winner.Id.ShouldBe(jobId);
        winner.Status.ShouldBe(IngestJobStatus.ExtractingAttachments);
    }

    [Fact]
    public async Task Claim_transitions_queued_to_extracting_attachments()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, jobId) = await SeedJobAsync(postgres, IngestJobStatus.Queued);

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldNotBeNull();
        claimed!.Status.ShouldBe(IngestJobStatus.ExtractingAttachments);
        claimed.Attempts.ShouldBe((short)1);
        claimed.LeaseOwner.ShouldNotBeNullOrEmpty();
        claimed.LeaseExpiresAt.ShouldNotBeNull();

        using var probe = NewDbContext(postgres.ConnectionString);
        var dbJob = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        dbJob.Status.ShouldBe(IngestJobStatus.ExtractingAttachments);
    }

    [Fact]
    public async Task Claim_returns_null_when_no_jobs_available()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldBeNull();
    }

    [Fact]
    public async Task Lease_expiry_allows_re_claim_at_same_phase()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, jobId) = await SeedJobAsync(postgres, IngestJobStatus.Composing,
            leaseOwner: "ghost-worker",
            leaseExpiresAt: SystemClock.Instance.GetCurrentInstant().Minus(Duration.FromSeconds(120)));

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldNotBeNull();
        claimed!.Id.ShouldBe(jobId);
        claimed.Status.ShouldBe(IngestJobStatus.Composing);
    }

    [Fact]
    public async Task Terminal_job_is_never_claimed()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        await SeedJobAsync(postgres, IngestJobStatus.Succeeded);

        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var orchestrator = ProcessingTestHost.NewOrchestrator(sp);
        var claimed = await orchestrator.ClaimNextAsync(ct);

        claimed.ShouldBeNull();
    }

    internal static async Task<(Guid noteId, Guid jobId)> SeedJobAsync(
        PostgresFixture postgres,
        string status,
        string? leaseOwner = null,
        Instant? leaseExpiresAt = null,
        Instant? deletedAt = null,
        short attempts = 0)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id            = Guid.CreateVersion7(),
            ClientNoteId  = Guid.NewGuid().ToString(),
            CapturedAt    = now,
            Status        = NoteStatus.Processing,
            BodyInput     = "test body",
            DeletedAt     = deletedAt,
            CreatedAt     = now,
            UpdatedAt     = now,
        };
        var job = new IngestJob
        {
            Id             = Guid.CreateVersion7(),
            NoteId         = note.Id,
            Status         = status,
            Attempts       = attempts,
            LeaseOwner     = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
            ScheduledAt    = now,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync();
        return (note.Id, job.Id);
    }

    internal static CloudDbContext NewDbContext(string connStr)
    {
        var opts = new DbContextOptionsBuilder<CloudDbContext>()
            .UseNpgsql(connStr, npg => npg.UseNodaTime().UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new CloudDbContext(opts);
    }
}
