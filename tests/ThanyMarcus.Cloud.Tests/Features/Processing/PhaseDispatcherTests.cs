using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing;

[Collection(PostgresCollection.Name)]
public sealed class PhaseDispatcherTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Extracting_attachments_queues_tasks_and_releases_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.ExtractingAttachments);
        await SeedAttachmentAsync(postgres, noteId, AttachmentKind.Voice);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.ExtractingAttachments);
        after.LeaseOwner.ShouldBeNull();

        var tasks = await probe.ExtractionTasks.Where(t => t.IngestJobId == jobId).ToListAsync(ct);
        tasks.ShouldNotBeEmpty();
        tasks.ShouldAllBe(t => t.Status == "queued");
    }

    [Fact]
    public async Task Extracting_attachments_with_no_attachments_advances_to_extracting_entities()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (_, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.ExtractingAttachments);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.ExtractingEntities);
    }

    [Fact]
    public async Task Deleted_note_short_circuits_to_dead_lettered()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);

        var now = SystemClock.Instance.GetCurrentInstant();
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Composing,
            deletedAt: now);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.DeadLettered);
        after.LastError.ShouldBe("user_cancelled");

        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.Status.ShouldBe(NoteStatus.Failed);
    }

    [Fact]
    public async Task Composing_writes_body_output_and_transitions_to_routing()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Composing);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);

        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.BodyOutput.ShouldNotBeNullOrWhiteSpace();
        note.RelativePath.ShouldBe($"Inbox/{noteId}.md");
    }

    [Fact]
    public async Task Routing_in_safe_mode_keeps_inbox_path()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Routing);
        await SeedBodyOutputAsync(postgres, noteId);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Synthesizing);

        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.ProjectId.ShouldBeNull();
    }

    [Fact]
    public async Task Extracting_entities_in_safe_mode_adds_no_mentions()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.ExtractingEntities);
        await SeedBodyOutputAsync(postgres, noteId);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Routing);

        var mentionCount = await probe.Mentions.CountAsync(m => m.NoteId == noteId, ct);
        mentionCount.ShouldBe(0);
    }

    [Fact]
    public async Task Embedding_transitions_succeeded_and_flips_note_to_ready()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Embedding);
        await SeedBodyOutputAsync(postgres, noteId);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        after.Status.ShouldBe(IngestJobStatus.Succeeded);
        after.FinishedAt.ShouldNotBeNull();
        after.LeaseOwner.ShouldBeNull();

        var note = await probe.Notes.SingleAsync(n => n.Id == noteId, ct);
        note.Status.ShouldBe(NoteStatus.Ready);
        note.Embedding.ShouldNotBeNull();
    }

    [Fact]
    public async Task Events_log_appends_one_entry_per_transition()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        await using var sp = ProcessingTestHost.Build(postgres.ConnectionString);
        var (noteId, jobId) = await SeedClaimedJobAsync(postgres, IngestJobStatus.Composing);

        var job = await LoadJobAsync(postgres, jobId);
        await DispatchAsync(sp, job, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var after = await probe.IngestJobs.SingleAsync(j => j.Id == jobId, ct);
        var events = after.EventsLog.RootElement.EnumerateArray().ToList();
        events.Count.ShouldBe(1);
        events[0].GetProperty("from").GetString().ShouldBe(IngestJobStatus.Composing);
        events[0].GetProperty("to").GetString().ShouldBe(IngestJobStatus.Routing);
    }

    private static async Task<(Guid noteId, Guid jobId)> SeedClaimedJobAsync(
        PostgresFixture postgres,
        string status,
        Instant? deletedAt = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
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
            Attempts       = 1,
            LeaseOwner     = "test-worker/abc12345",
            LeaseExpiresAt = now.Plus(Duration.FromSeconds(60)),
            ScheduledAt    = now,
            StartedAt      = now,
            CreatedAt      = now,
            UpdatedAt      = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        await db.SaveChangesAsync();
        return (note.Id, job.Id);
    }

    private static async Task SeedAttachmentAsync(
        PostgresFixture postgres, Guid noteId, string kind)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var now = SystemClock.Instance.GetCurrentInstant();
        db.Attachments.Add(new Attachment
        {
            Id                 = Guid.CreateVersion7(),
            NoteId             = noteId,
            ClientAttachmentId = "client-att-1",
            Kind               = kind,
            StorageProvider    = "s3",
            StorageBucket      = "test",
            StorageKey         = "k",
            Status             = AttachmentStatus.Uploaded,
            ExtractionStatus   = AttachmentExtractionStatus.Pending,
            Extra              = JsonDocument.Parse("{}"),
            Sha256             = "test-sha",
            CreatedAt          = now,
            UpdatedAt          = now,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedBodyOutputAsync(PostgresFixture postgres, Guid noteId)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = await db.Notes.SingleAsync(n => n.Id == noteId);
        note.BodyOutput = "# composed body";
        note.RelativePath = $"Inbox/{noteId}.md";
        await db.SaveChangesAsync();
    }

    private static async Task<IngestJob> LoadJobAsync(PostgresFixture postgres, Guid jobId)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        return await db.IngestJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
    }

    private static async Task DispatchAsync(ServiceProvider sp, IngestJob job, CancellationToken ct)
    {
        await using var scope = sp.CreateAsyncScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IngestPhaseDispatcher>();
        await dispatcher.DispatchAsync(job, ct);
    }
}
