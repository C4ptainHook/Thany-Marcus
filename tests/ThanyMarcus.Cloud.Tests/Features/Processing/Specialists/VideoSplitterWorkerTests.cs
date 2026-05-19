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
public sealed class VideoSplitterWorkerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Split_inserts_2_keyframe_children_plus_1_audio_child_and_marks_parent_extracted()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, parentId, taskId, jobId) =
            await SeedVideoTaskAsync(postgres);

        var splitter = new CannedSplitterClient(
            keyframeCount: 2,
            audioBytes: 8,
            durationSeconds: 10,
            videoCodec: "h264");

        var store = new FakeArtifactStore();
        await using var sp = SpecialistTestHost.Build(
            postgres.ConnectionString,
            video: splitter,
            store: store);
        var worker = SpecialistTestHost.NewVideo(sp);

        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        await worker.ProcessOnceAsync(claimed!, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);

        var parent = await probe.Attachments.SingleAsync(a => a.Id == parentId, ct);
        parent.ExtractionStatus.ShouldBe(AttachmentExtractionStatus.Extracted);
        parent.ExtractionCacheKey!.ShouldStartWith("sha256:video:ffmpeg:");
        var parentExtra = JsonDocument.Parse(parent.Extra.RootElement.GetRawText()).RootElement;
        parentExtra.GetProperty("keyframe_count").GetInt32().ShouldBe(2);
        parentExtra.GetProperty("video_codec").GetString().ShouldBe("h264");
        parentExtra.GetProperty("audio_storage_key").GetString()!.ShouldContain("/audio.wav");

        var children = await probe.Attachments
            .Where(a => a.ParentAttachmentId == parentId)
            .OrderBy(a => a.ClientAttachmentId)
            .ToListAsync(ct);
        children.Count.ShouldBe(3);
        children.Count(c => c.Kind == AttachmentKind.Image).ShouldBe(2);
        children.Count(c => c.Kind == AttachmentKind.Voice).ShouldBe(1);
        children.ShouldAllBe(c => c.ExtractionStatus == AttachmentExtractionStatus.Pending);

        var tasks = await probe.ExtractionTasks
            .Where(t => t.IngestJobId == jobId)
            .ToListAsync(ct);
        tasks.Count.ShouldBe(1 + 3);
        tasks.Single(t => t.Id == taskId).Status.ShouldBe(ExtractionTaskStatus.Succeeded);
        var childTasks = tasks.Where(t => t.Id != taskId).ToList();
        childTasks.Count(t => t.TargetSidecar == ExtractionTaskSidecar.Ollama).ShouldBe(2);
        childTasks.Count(t => t.TargetSidecar == ExtractionTaskSidecar.Parakeet).ShouldBe(1);
        childTasks.ShouldAllBe(t => t.Status == ExtractionTaskStatus.Queued);

        store.Bodies.Keys.ShouldContain(k => k.EndsWith("/audio.wav", StringComparison.Ordinal));
        store.Bodies.Keys.Count(k => k.EndsWith(".jpg", StringComparison.Ordinal)).ShouldBe(2);
        store.Finalized.Values.ShouldAllBe(v => v);
    }

    [Fact]
    public async Task Split_with_matching_cache_key_reuses_children_and_returns_early()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, parentId, _, _) = await SeedVideoTaskAsync(postgres,
            preExistingCacheKey: "sha256:video:ffmpeg:n-5s",
            preExistingExtractionStatus: AttachmentExtractionStatus.Extracted,
            preExistingExtractedText: "[video split: 2 keyframes + 1 audio]");

        var splitter = new CannedSplitterClient(keyframeCount: 99, audioBytes: 0, durationSeconds: 0, videoCodec: "h264");

        await using var sp = SpecialistTestHost.Build(
            postgres.ConnectionString,
            video: splitter);
        var worker = SpecialistTestHost.NewVideo(sp);

        var claimed = await worker.ClaimNextAsync(ct);
        claimed.ShouldNotBeNull();
        await worker.ProcessOnceAsync(claimed!, ct);

        splitter.CallCount.ShouldBe(0);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var children = await probe.Attachments.Where(a => a.ParentAttachmentId == parentId).ToListAsync(ct);
        children.ShouldBeEmpty();
    }

    [Fact]
    public async Task Skip_when_video_codec_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await postgres.ResetAsync();

        var (_, parentId, _, _) = await SeedVideoTaskAsync(postgres);
        var splitter = new CannedSplitterClient(keyframeCount: 0, audioBytes: 0, durationSeconds: 5, videoCodec: null);

        await using var sp = SpecialistTestHost.Build(postgres.ConnectionString, video: splitter);
        var worker = SpecialistTestHost.NewVideo(sp);

        var claimed = await worker.ClaimNextAsync(ct);
        await worker.ProcessOnceAsync(claimed!, ct);

        using var probe = JobOrchestratorWorkerTests.NewDbContext(postgres.ConnectionString);
        var parent = await probe.Attachments.SingleAsync(a => a.Id == parentId, ct);
        parent.ExtractionStatus.ShouldBe(AttachmentExtractionStatus.Skipped);
        parent.ExtractionError.ShouldBe("no_decodable_video_stream");
    }

    private static async Task<(Guid noteId, Guid parentAttId, Guid taskId, Guid jobId)> SeedVideoTaskAsync(
        PostgresFixture postgres,
        string? preExistingCacheKey = null,
        string preExistingExtractionStatus = AttachmentExtractionStatus.Pending,
        string? preExistingExtractedText = null)
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
            ClientAttachmentId = "vid1",
            Kind = AttachmentKind.File,
            StorageProvider = "s3",
            StorageBucket = "test",
            StorageKey = $"notes/{note.Id}/attachments/{attId}/video.mp4",
            MimeType = "video/mp4",
            Status = AttachmentStatus.Uploaded,
            ExtractionStatus = preExistingExtractionStatus,
            ExtractedText = preExistingExtractedText,
            ExtractionCacheKey = preExistingCacheKey,
            Extra = JsonDocument.Parse("{}"),
            CreatedAt = now,
            UpdatedAt = now,
        };
        var task = new ExtractionTask
        {
            Id = Guid.CreateVersion7(),
            IngestJobId = job.Id,
            AttachmentId = attId,
            TargetSidecar = ExtractionTaskSidecar.Video,
            Status = ExtractionTaskStatus.Queued,
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
        return (note.Id, attId, task.Id, job.Id);
    }

    private sealed class CannedSplitterClient(
        int keyframeCount, int audioBytes, double durationSeconds, string? videoCodec)
        : IVideoSplitterClient
    {
        public int CallCount { get; private set; }

        public Task<VideoSplitOutput> SplitAsync(VideoSplitInput input, CancellationToken ct)
        {
            CallCount++;
            var keyframes = Enumerable.Range(0, keyframeCount)
                .Select(i => new KeyframeUpload(i, i * 5.0 + 2.5, new byte[] { (byte)i }))
                .ToList();
            return Task.FromResult(new VideoSplitOutput(
                Keyframes: keyframes,
                Audio: new AudioUpload(new byte[audioBytes], durationSeconds),
                VideoDurationSeconds: durationSeconds,
                VideoCodec: videoCodec));
        }
    }
}
