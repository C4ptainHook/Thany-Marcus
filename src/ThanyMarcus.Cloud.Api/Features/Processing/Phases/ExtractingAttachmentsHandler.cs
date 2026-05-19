using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Npgsql;
using ThanyMarcus.Cloud.Api.Features.Ingest;
using ThanyMarcus.Cloud.Api.Features.Processing.Specialists;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

namespace ThanyMarcus.Cloud.Api.Features.Processing.Phases;

public sealed class ExtractingAttachmentsHandler : IPhaseHandler
{
    public string Phase => IngestJobStatus.ExtractingAttachments;

    private readonly CloudDbContext db;
    private readonly IClock clock;
    private readonly IConfiguration config;
    private readonly JobStateTransitions transitions;
    private readonly IIngestEventBus eventBus;

    public ExtractingAttachmentsHandler(
        CloudDbContext db,
        IClock clock,
        IConfiguration config,
        JobStateTransitions transitions,
        IIngestEventBus eventBus)
    {
        this.db = db;
        this.clock = clock;
        this.config = config;
        this.transitions = transitions;
        this.eventBus = eventBus;
    }

    public async Task<PhaseHandlerResult> HandleAsync(IngestJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);

        var attachments = await db.Attachments
            .Where(a => a.NoteId == job.NoteId)
            .ToListAsync(ct);

        var existingTaskAttachmentIds = await db.ExtractionTasks
            .Where(t => t.IngestJobId == job.Id)
            .Select(t => t.AttachmentId)
            .ToListAsync(ct);
        var alreadyQueued = new HashSet<Guid>(existingTaskAttachmentIds);

        var modelVersion = config["IngestSaga:Models:Stub:Version"] ?? "stub-v1";
        var now = clock.GetCurrentInstant();
        var newlyQueuedBySidecar = new Dictionary<string, int>(StringComparer.Ordinal);
        var attachmentsWithCacheHit = new List<Attachment>();

        foreach (var att in attachments.Where(a =>
            a.ExtractionStatus == AttachmentExtractionStatus.Pending &&
            !alreadyQueued.Contains(a.Id)))
        {
            var sidecar = ResolveSidecar(att);
            if (sidecar is null)
            {
                att.ExtractionStatus = AttachmentExtractionStatus.Skipped;
                continue;
            }

            var cacheKey = $"sha256:{sidecar}:{modelVersion}";
            att.ExtractionCacheKey = cacheKey;

            var cachedText = !string.IsNullOrWhiteSpace(att.Sha256)
                ? await db.Attachments
                    .Where(a => a.Id != att.Id
                                && a.Sha256 == att.Sha256
                                && a.ExtractionCacheKey == cacheKey
                                && a.ExtractedText != null)
                    .Select(a => a.ExtractedText)
                    .FirstOrDefaultAsync(ct)
                : null;

            if (cachedText is not null)
            {
                att.ExtractedText = cachedText;
                att.ExtractionStatus = AttachmentExtractionStatus.Extracted;
                attachmentsWithCacheHit.Add(att);
                db.ExtractionTasks.Add(new ExtractionTask
                {
                    Id            = Guid.CreateVersion7(),
                    IngestJobId   = job.Id,
                    AttachmentId  = att.Id,
                    TargetSidecar = sidecar,
                    Status        = ExtractionTaskStatus.Skipped,
                    ScheduledAt   = now,
                    StartedAt     = now,
                    FinishedAt    = now,
                    EventsLog     = JsonDocument.Parse("[]"),
                    CreatedAt     = now,
                    UpdatedAt     = now,
                });
            }
            else
            {
                var task = new ExtractionTask
                {
                    Id            = Guid.CreateVersion7(),
                    IngestJobId   = job.Id,
                    AttachmentId  = att.Id,
                    TargetSidecar = sidecar,
                    Status        = ExtractionTaskStatus.Queued,
                    ScheduledAt   = now,
                    EventsLog     = JsonDocument.Parse("[]"),
                    CreatedAt     = now,
                    UpdatedAt     = now,
                };
                db.ExtractionTasks.Add(task);
                newlyQueuedBySidecar.TryGetValue(sidecar, out var count);
                newlyQueuedBySidecar[sidecar] = count + 1;
            }
        }

        await db.SaveChangesAsync(ct);

        foreach (var att in attachmentsWithCacheHit)
        {
            await eventBus.PublishAttachmentStatusChangedAsync(
                job.NoteId, att.Id,
                AttachmentExtractionStatus.Pending,
                AttachmentExtractionStatus.Extracted,
                ct);
        }

        foreach (var sidecar in newlyQueuedBySidecar.Keys)
        {
            await NotifySidecarAsync(sidecar, job.Id, ct);
        }

        var allTerminal = await AllTasksTerminalAsync(job.Id, ct);
        if (allTerminal)
        {
            await transitions.TransitionAsync(
                job,
                nextStatus: IngestJobStatus.Composing,
                lastError: null,
                clearLease: true,
                setFinishedAt: false,
                ct);
            return PhaseHandlerResult.Advanced;
        }

        await ReleaseLeaseAsync(job, ct);
        return PhaseHandlerResult.Waiting;
    }

    private async Task<bool> AllTasksTerminalAsync(Guid jobId, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var owns = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await db.Database.OpenConnectionAsync(ct);
            owns = true;
        }
        try
        {
            await using var cmd = new NpgsqlCommand(
                "SELECT bool_and(status IN ('succeeded','failed','skipped')) FROM extraction_tasks WHERE ingest_job_id = @jobId",
                conn);
            cmd.Parameters.AddWithValue("jobId", jobId);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null || result is DBNull) return true;
            return (bool)result;
        }
        finally
        {
            if (owns)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task ReleaseLeaseAsync(IngestJob job, CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var expectedVersion = job.TransitionVersion;
        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                lease_owner        = NULL,
                lease_expires_at   = NULL,
                transition_version = transition_version + 1,
                updated_at         = {now}
              WHERE id = {job.Id}
                AND lease_owner = {job.LeaseOwner}
                AND transition_version = {expectedVersion}
            """, ct);
        if (rows == 1)
        {
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
            job.TransitionVersion = expectedVersion + 1;
            job.UpdatedAt = now;
        }
    }

    private async Task NotifySidecarAsync(string sidecar, Guid jobId, CancellationToken ct)
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
            if (owns)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private static string? ResolveSidecar(Attachment att) => att.Kind switch
    {
        AttachmentKind.Image => ExtractionTaskSidecar.Ollama,
        AttachmentKind.Voice => ExtractionTaskSidecar.Parakeet,
        AttachmentKind.Url   => ExtractionTaskSidecar.Url,
        AttachmentKind.File  => ExtractionTaskSidecar.Docling,
        _ => null,
    };
}
