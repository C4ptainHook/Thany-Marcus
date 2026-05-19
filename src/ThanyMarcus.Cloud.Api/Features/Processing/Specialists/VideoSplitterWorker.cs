using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Cloud.Api.Infrastructure.Ffmpeg;
using ThanyMarcus.Cloud.Api.Infrastructure.Sidecars;
using ThanyMarcus.Cloud.Api.Infrastructure.Storage;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Specialists;

public sealed partial class VideoSplitterWorker : SpecialistWorkerBase<IVideoSplitterClient>
{
    protected override string TargetSidecar => ExtractionTaskSidecar.Video;

    private readonly VideoFilterOptions options;
    private readonly ILogger<VideoSplitterWorker> selfLog;

    public VideoSplitterWorker(
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment env,
        IClock clock,
        VideoFilterOptions options,
        ILogger<VideoSplitterWorker> log)
        : base(services, config, env, clock, log)
    {
        this.options = options;
        this.selfLog = log;
    }

    protected override async Task<SpecialistExtractionOutcome> ExtractAsync(
        IServiceProvider scopeServices, IVideoSplitterClient client,
        ExtractionTask task, Attachment att, CancellationToken ct)
    {
        var expectedCacheKey =
            $"sha256:video:ffmpeg:n-{options.KeyframeIntervalSeconds}s";

        if (string.Equals(att.ExtractionCacheKey, expectedCacheKey, StringComparison.Ordinal) &&
            string.Equals(att.ExtractionStatus, AttachmentExtractionStatus.Extracted, StringComparison.Ordinal))
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: att.ExtractedText,
                ExtractionCacheKey: expectedCacheKey,
                Extra: null,
                AttachmentStatus: AttachmentExtractionStatus.Extracted,
                ExtractionError: null);
        }

        var db = scopeServices.GetRequiredService<CloudDbContext>();
        var store = scopeServices.GetRequiredService<IArtifactStore>();
        var clock = scopeServices.GetRequiredService<IClock>();

        await DeleteExistingChildrenAsync(db, att.Id, ct);

        var presigned = await store.IssueDownloadUrlAsync(att.StorageKey, options.PresignedUrlTtl, ct);

        VideoSplitOutput split;
        try
        {
            split = await client.SplitAsync(new VideoSplitInput(
                ParentVideo: att,
                PresignedVideoUrl: presigned.Url.ToString(),
                KeyframeIntervalSeconds: options.KeyframeIntervalSeconds,
                JpegQuality: options.JpegQuality), ct);
        }
        catch (FfmpegRunnerException ex)
        {
            LogSplitFailure(selfLog, ex, att.Id);
            throw;
        }

        if (split.VideoDurationSeconds > options.MaxDurationSeconds)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: BuildSkipMeta(split),
                AttachmentStatus: AttachmentExtractionStatus.Skipped,
                ExtractionError: $"duration_{split.VideoDurationSeconds.ToString("F1", CultureInfo.InvariantCulture)}_gt_{options.MaxDurationSeconds}");
        }
        if (split.VideoCodec is null)
        {
            return new SpecialistExtractionOutcome(
                ExtractedText: null,
                ExtractionCacheKey: null,
                Extra: BuildSkipMeta(split),
                AttachmentStatus: AttachmentExtractionStatus.Skipped,
                ExtractionError: "no_decodable_video_stream");
        }

        var keyframeCount = Math.Min(split.Keyframes.Count, options.MaxKeyframes);
        var keyframeKeys = new List<string>(keyframeCount);
        var keyframeChildIds = new List<Guid>(keyframeCount);
        var keyframeOffsets = new List<double>(keyframeCount);
        for (var i = 0; i < keyframeCount; i++)
        {
            var kf = split.Keyframes[i];
            var key = $"notes/{att.NoteId}/attachments/{att.Id}/keyframes/{kf.Index:D3}.jpg";
            await store.UploadBytesAsync(key, kf.Bytes, "image/jpeg", finalized: true, ct);
            keyframeKeys.Add(key);
            keyframeOffsets.Add(kf.OffsetSeconds);
            keyframeChildIds.Add(Guid.CreateVersion7());
        }

        var audioKey = $"notes/{att.NoteId}/attachments/{att.Id}/audio.wav";
        await store.UploadBytesAsync(audioKey, split.Audio.WavBytes, "audio/wav", finalized: true, ct);
        var audioChildId = Guid.CreateVersion7();

        var now = clock.GetCurrentInstant();
        for (var i = 0; i < keyframeCount; i++)
        {
            await InsertChildAttachmentAsync(
                db,
                childId: keyframeChildIds[i],
                parentNoteId: att.NoteId,
                parentAttId: att.Id,
                clientAttachmentId: $"{att.ClientAttachmentId}#frame-{i:D3}",
                kind: AttachmentKind.Image,
                storageKey: keyframeKeys[i],
                bucket: att.StorageBucket,
                bytes: split.Keyframes[i].Bytes,
                mimeType: "image/jpeg",
                extraJson: JsonSerializer.Serialize(new
                {
                    frame_index = i,
                    offset_seconds = keyframeOffsets[i],
                    parent_video_id = att.Id,
                }),
                now: now,
                ct: ct);
        }

        await InsertChildAttachmentAsync(
            db,
            childId: audioChildId,
            parentNoteId: att.NoteId,
            parentAttId: att.Id,
            clientAttachmentId: $"{att.ClientAttachmentId}#audio",
            kind: AttachmentKind.Voice,
            storageKey: audioKey,
            bucket: att.StorageBucket,
            bytes: split.Audio.WavBytes,
            mimeType: "audio/wav",
            extraJson: JsonSerializer.Serialize(new
            {
                parent_video_id = att.Id,
                duration_s = split.Audio.DurationSeconds,
            }),
            now: now,
            ct: ct);

        var childPairs = new List<(Guid AttachmentId, string Sidecar)>(keyframeCount + 1);
        foreach (var id in keyframeChildIds)
        {
            childPairs.Add((id, ExtractionTaskSidecar.Ollama));
        }
        childPairs.Add((audioChildId, ExtractionTaskSidecar.Parakeet));

        foreach (var (childAttId, sidecar) in childPairs)
        {
            await InsertChildTaskAsync(db, jobId: task.IngestJobId, attachmentId: childAttId, sidecar: sidecar, now: now, ct);
        }

        await NotifySidecarAsync(db, ExtractionTaskSidecar.Ollama, task.IngestJobId, ct);
        await NotifySidecarAsync(db, ExtractionTaskSidecar.Parakeet, task.IngestJobId, ct);

        return new SpecialistExtractionOutcome(
            ExtractedText: $"[video split: {keyframeCount} keyframes + 1 audio]",
            ExtractionCacheKey: expectedCacheKey,
            Extra: BuildSuccessMeta(split, keyframeCount, keyframeKeys, keyframeOffsets, audioKey),
            AttachmentStatus: AttachmentExtractionStatus.Extracted,
            ExtractionError: null);
    }

    private static async Task DeleteExistingChildrenAsync(CloudDbContext db, Guid parentAttId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM attachments WHERE parent_attachment_id = {parentAttId}",
            ct);
    }

    private static async Task InsertChildAttachmentAsync(
        CloudDbContext db, Guid childId, Guid parentNoteId, Guid parentAttId,
        string clientAttachmentId, string kind, string storageKey, string bucket,
        byte[] bytes, string mimeType, string extraJson, Instant now, CancellationToken ct)
    {
        var sha = "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var byteSize = (long)bytes.Length;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO attachments (
                id, note_id, client_attachment_id,
                kind, storage_provider, storage_bucket, storage_key,
                byte_size, mime_type, sha256, filename,
                status, extraction_status, extracted_text, extraction_error,
                extra, parent_attachment_id, extraction_cache_key, url,
                created_at, updated_at)
            VALUES (
                {childId}, {parentNoteId}, {clientAttachmentId},
                {kind}, 's3', {bucket}, {storageKey},
                {byteSize}, {mimeType}, {sha}, NULL,
                {AttachmentStatus.Uploaded}, {AttachmentExtractionStatus.Pending}, NULL, NULL,
                {extraJson}::jsonb, {parentAttId}, NULL, NULL,
                {now}, {now});
            """, ct);
    }

    private static async Task InsertChildTaskAsync(
        CloudDbContext db, Guid jobId, Guid attachmentId, string sidecar, Instant now, CancellationToken ct)
    {
        var taskId = Guid.CreateVersion7();
        var queued = ExtractionTaskStatus.Queued;
        var eventsLog = "[]";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO extraction_tasks (
                id, ingest_job_id, attachment_id, target_sidecar,
                status, attempts, last_error,
                lease_owner, lease_expires_at, scheduled_at,
                started_at, finished_at, events_log,
                transition_version, created_at, updated_at)
            VALUES (
                {taskId}, {jobId}, {attachmentId}, {sidecar},
                {queued}, 0, NULL,
                NULL, NULL, {now},
                NULL, NULL, {eventsLog}::jsonb,
                0, {now}, {now});
            """, ct);
    }

    private static async Task NotifySidecarAsync(CloudDbContext db, string sidecar, Guid jobId, CancellationToken ct)
    {
        var channel = SpecialistChannels.NewTasksChannel(sidecar);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var owns = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            owns = true;
        }
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT pg_notify(@chan, @payload)", conn);
            cmd.Parameters.AddWithValue("chan", channel);
            cmd.Parameters.AddWithValue("payload", jobId.ToString());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (owns) await db.Database.CloseConnectionAsync();
        }
    }

    private static JsonDocument BuildSuccessMeta(
        VideoSplitOutput split, int keyframeCount,
        IReadOnlyList<string> keyframeKeys, IReadOnlyList<double> keyframeOffsets,
        string audioKey)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("keyframe_count", keyframeCount);
            writer.WriteNumber("audio_duration_s", split.Audio.DurationSeconds);
            writer.WriteNumber("video_duration_s", split.VideoDurationSeconds);
            if (split.VideoCodec is not null) writer.WriteString("video_codec", split.VideoCodec);
            writer.WritePropertyName("frame_offsets");
            writer.WriteStartArray();
            foreach (var o in keyframeOffsets) writer.WriteNumberValue(o);
            writer.WriteEndArray();
            writer.WritePropertyName("keyframe_storage_keys");
            writer.WriteStartArray();
            foreach (var k in keyframeKeys) writer.WriteStringValue(k);
            writer.WriteEndArray();
            writer.WriteString("audio_storage_key", audioKey);
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    private static JsonDocument BuildSkipMeta(VideoSplitOutput split)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteNumber("video_duration_s", split.VideoDurationSeconds);
            if (split.VideoCodec is not null) writer.WriteString("video_codec", split.VideoCodec);
            writer.WriteEndObject();
        }
        ms.Position = 0;
        return JsonDocument.Parse(ms);
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error,
        Message = "VideoSplitterWorker ffmpeg failure: attachment={AttId}")]
    private static partial void LogSplitFailure(ILogger logger, Exception ex, Guid attId);
}
