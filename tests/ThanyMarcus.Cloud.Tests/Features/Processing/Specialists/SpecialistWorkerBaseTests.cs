using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Shouldly;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Tests.Infrastructure;

namespace ThanyMarcus.Cloud.Tests.Features.Processing.Specialists;

[Collection(PostgresCollection.Name)]
public sealed class SpecialistWorkerBaseTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_workers_competing_for_one_task_only_one_wins()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (noteId, _, _) = await SeedQueuedTaskAsync(postgres, AttachmentKind.Image, ExtractionTaskSidecar.Ollama);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var workerA = SpecialistTestHost.NewVlm(sp);
        var workerB = SpecialistTestHost.NewVlm(sp);

        var taskA = workerA.ClaimNextAsync(ct);
        var taskB = workerB.ClaimNextAsync(ct);
        var (cA, cB) = (await taskA, await taskB);

        var wins = (cA is not null ? 1 : 0) + (cB is not null ? 1 : 0);
        wins.ShouldBe(1);
        _ = noteId;
    }

    [Fact]
    public async Task Succeeded_extraction_writes_attachment_text()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (_, attId, taskId) = await SeedQueuedTaskAsync(postgres, AttachmentKind.Image, ExtractionTaskSidecar.Ollama);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var worker = SpecialistTestHost.NewVlm(sp);
        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        await worker.ProcessOnceAsync(claimed!, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var att = await probe.Attachments.SingleAsync(a => a.Id == attId, ct);
        att.ExtractionStatus.ShouldBe(AttachmentExtractionStatus.Extracted);
        att.ExtractedText.ShouldNotBeNullOrEmpty();
        var task = await probe.ExtractionTasks.SingleAsync(t => t.Id == taskId, ct);
        task.Status.ShouldBe(ExtractionTaskStatus.Succeeded);
    }

    [Fact]
    public async Task Persistent_failure_exhausts_budget_and_marks_failed()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (_, attId, taskId) = await SeedQueuedTaskAsync(postgres, AttachmentKind.Image, ExtractionTaskSidecar.Ollama);

        var throwing = new ThrowingVlmClient();
        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString, vlm: throwing, maxAttempts: 2);
        var worker = SpecialistTestHost.NewVlm(sp);

        for (var i = 0; i < 2; i++)
        {
            await Bump(postgres, taskId, SystemClock.Instance.GetCurrentInstant());
            var claimed = await worker.ClaimNextAsync(ct);
            claimed.ShouldNotBeNull();
            await worker.ProcessOnceAsync(claimed!, ct);
        }

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var task = await probe.ExtractionTasks.SingleAsync(t => t.Id == taskId, ct);
        task.Status.ShouldBe(ExtractionTaskStatus.Failed);
        task.LastError.ShouldNotBeNull();
        var att = await probe.Attachments.SingleAsync(a => a.Id == attId, ct);
        att.ExtractionStatus.ShouldBe(AttachmentExtractionStatus.Failed);
        att.ExtractionError.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Transient_failure_retries_then_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var (_, _, taskId) = await SeedQueuedTaskAsync(postgres, AttachmentKind.Image, ExtractionTaskSidecar.Ollama);

        var flaky = new ThrowingVlmClient { ThrowCount = 1 };
        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString, vlm: flaky, maxAttempts: 3);
        var worker = SpecialistTestHost.NewVlm(sp);

        var first = await worker.ClaimNextAsync(ct);
        first.ShouldNotBeNull();
        await worker.ProcessOnceAsync(first!, ct);

        await Bump(postgres, taskId, SystemClock.Instance.GetCurrentInstant());

        var second = await worker.ClaimNextAsync(ct);
        second.ShouldNotBeNull();
        await worker.ProcessOnceAsync(second!, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var task = await probe.ExtractionTasks.SingleAsync(t => t.Id == taskId, ct);
        task.Status.ShouldBe(ExtractionTaskStatus.Succeeded);
    }

    [Fact]
    public async Task Lease_expiry_allows_re_claim()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();
        var now = SystemClock.Instance.GetCurrentInstant();
        var (_, _, taskId) = await SeedQueuedTaskAsync(
            postgres, AttachmentKind.Image, ExtractionTaskSidecar.Ollama,
            status: ExtractionTaskStatus.Processing,
            leaseOwner: "ghost",
            leaseExpiresAt: now.Minus(Duration.FromSeconds(120)));

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString);
        var worker = SpecialistTestHost.NewVlm(sp);

        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        claimed!.Id.ShouldBe(taskId);
    }

    private static async Task Bump(PostgresFixture postgres, Guid taskId, Instant now)
    {
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE extraction_tasks SET scheduled_at = {now} WHERE id = {taskId}");
    }

    internal static async Task<(Guid noteId, Guid attachmentId, Guid taskId)> SeedQueuedTaskAsync(
        PostgresFixture postgres,
        string kind,
        string sidecar,
        string status = ExtractionTaskStatus.Queued,
        string? leaseOwner = null,
        Instant? leaseExpiresAt = null)
    {
        var now = SystemClock.Instance.GetCurrentInstant();
        using var db = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var note = new Note
        {
            Id = Guid.CreateVersion7(),
            ClientNoteId = Guid.NewGuid().ToString(),
            CapturedAt = now,
            Status = NoteStatus.Processing,
            BodyInput = "body",
            CreatedAt = now,
            UpdatedAt = now,
        };
        var job = new IngestJob
        {
            Id = Guid.CreateVersion7(),
            NoteId = note.Id,
            Status = IngestJobStatus.ExtractingAttachments,
            ScheduledAt = now,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var attId = Guid.CreateVersion7();
        var att = new Attachment
        {
            Id = attId,
            NoteId = note.Id,
            ClientAttachmentId = "att1",
            Kind = kind,
            StorageProvider = "s3",
            StorageBucket = "test",
            StorageKey = $"k/{attId}",
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = AttachmentExtractionStatus.Pending,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var task = new ExtractionTask
        {
            Id = Guid.CreateVersion7(),
            IngestJobId = job.Id,
            AttachmentId = attId,
            TargetSidecar = sidecar,
            Status = status,
            LeaseOwner = leaseOwner,
            LeaseExpiresAt = leaseExpiresAt,
            ScheduledAt = now,
            EventsLog = JsonDocument.Parse("[]"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Notes.Add(note);
        db.IngestJobs.Add(job);
        db.Attachments.Add(att);
        db.ExtractionTasks.Add(task);
        await db.SaveChangesAsync();
        return (note.Id, att.Id, task.Id);
    }

    private sealed class ThrowingVlmClient : IVlmClient
    {
        public int ThrowCount { get; set; } = int.MaxValue;

        public Task<VlmExtractionOutcome> ExtractAsync(Attachment att, CancellationToken ct)
        {
            if (ThrowCount > 0)
            {
                ThrowCount--;
                throw new InvalidOperationException("synthetic vlm failure");
            }
            var extra = JsonDocument.Parse("{}");
            return Task.FromResult(new VlmExtractionOutcome(
                ExtractedText: $"described:{att.StorageKey}",
                ExtractionCacheKey: ExtractionCacheKeys.ForOllama("stub-v1"),
                Extra: extra,
                Skipped: false,
                SkipReason: null));
        }
    }
}
