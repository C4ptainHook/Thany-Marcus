using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;
using ThanyMarcus.Shared.Saga;

namespace ThanyMarcus.Cloud.Api.Features.Processing;

public sealed class JobStateTransitions
{
    private readonly CloudDbContext db;
    private readonly IClock clock;

    public JobStateTransitions(CloudDbContext db, IClock clock)
    {
        this.db = db;
        this.clock = clock;
    }

    public async Task TransitionAsync(
        IngestJob job,
        string nextStatus,
        string? lastError,
        bool clearLease,
        bool setFinishedAt,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var prevStatus = job.Status;
        var expectedVersion = job.TransitionVersion;
        var payload = BuildEventPayload(now, job.LeaseOwner, prevStatus, nextStatus, lastError);

        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                status              = {nextStatus},
                last_error          = {lastError},
                lease_owner         = CASE WHEN {clearLease} THEN NULL ELSE lease_owner END,
                lease_expires_at    = CASE WHEN {clearLease} THEN NULL ELSE lease_expires_at END,
                consecutive_crashes = 0,
                finished_at         = CASE WHEN {setFinishedAt} THEN {now} ELSE finished_at END,
                events_log          = events_log || {payload}::jsonb,
                transition_version  = transition_version + 1,
                updated_at          = {now}
              WHERE id = {job.Id}
                AND lease_owner = {job.LeaseOwner}
                AND transition_version = {expectedVersion}
            """, ct);

        if (rows == 0)
        {
            throw new SagaOwnershipLostException(job.Id, job.LeaseOwner);
        }

        job.Status = nextStatus;
        job.LastError = lastError;
        job.ConsecutiveCrashes = 0;
        job.TransitionVersion = expectedVersion + 1;
        if (clearLease)
        {
            job.LeaseOwner = null;
            job.LeaseExpiresAt = null;
        }
        if (setFinishedAt)
        {
            job.FinishedAt = now;
        }
        job.UpdatedAt = now;
    }

    public async Task RescheduleAsync(
        IngestJob job,
        Instant nextScheduledAt,
        string? lastError,
        CancellationToken ct)
    {
        var now = clock.GetCurrentInstant();
        var expectedVersion = job.TransitionVersion;
        var payload = BuildEventPayload(now, job.LeaseOwner, job.Status, job.Status, lastError);
        var nextAt = nextScheduledAt;

        var rows = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE ingest_jobs SET
                last_error          = {lastError},
                lease_owner         = NULL,
                lease_expires_at    = NULL,
                consecutive_crashes = 0,
                scheduled_at        = {nextAt},
                events_log          = events_log || {payload}::jsonb,
                transition_version  = transition_version + 1,
                updated_at          = {now}
              WHERE id = {job.Id}
                AND lease_owner = {job.LeaseOwner}
                AND transition_version = {expectedVersion}
            """, ct);

        if (rows == 0)
        {
            throw new SagaOwnershipLostException(job.Id, job.LeaseOwner);
        }

        job.LastError = lastError;
        job.ConsecutiveCrashes = 0;
        job.ScheduledAt = nextScheduledAt;
        job.TransitionVersion = expectedVersion + 1;
        job.LeaseOwner = null;
        job.LeaseExpiresAt = null;
        job.UpdatedAt = now;
    }

    private static string BuildEventPayload(
        Instant at, string? by, string? from, string? to, string? error)
    {
        var obj = new
        {
            at = at.ToString(),
            by = by,
            from = from,
            to = to,
            error = error,
        };
        return JsonSerializer.Serialize(obj);
    }
}
