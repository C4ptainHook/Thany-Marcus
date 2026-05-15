# ADR-0019: Background work + saga durability

Status: Accepted
Date: 2026-05-15

## Context

The portal's only long-running operation is **cloud provisioning** (PORTAL-007 through PORTAL-011): the user clicks "Create cloud," the portal runs Terraform (60–120s), waits for cloud-init, registers a Cloudflare DNS A-record, waits for Let's Encrypt cert acquisition, receives a registration callback from the new cloud, and finally marks the cloud `ready`. Destroy is structurally similar (PORTAL-015).

Two durability questions must be answered:

1. **Job-queue durability.** When the user posts `POST /api/clouds`, the worker must reliably pick it up — even if the portal process restarts between accept and execution.
2. **Saga-state durability.** While the worker is in the middle of provisioning, it can crash (deploy, OOM, VPS reboot). The next worker must resume from the correct phase without re-running steps that already committed observable side effects (Terraform-applied droplet, Cloudflare A-record, etc.).

A third question is **live progress streaming** for the wizard UI — but that is transient by nature (if the SSE connection drops, the client reconnects and resumes from the current saga state), so it does not need the same durability story. See [[0020-server-push-sse]].

## Options considered

### Job queue

- **A. In-memory `Channel<T>`.** Simple, fast, no DB cost. **Loses every queued job on process restart.** Unacceptable for the provisioning workflow.
- **B. Postgres-backed queue (`SELECT ... FOR UPDATE SKIP LOCKED`).** Native Postgres pattern. Job rows live in the same DB as app data, so state transitions can be transactional with app writes. No external dependencies. Performance ceiling is well above thesis-scale workload.
- **C. Hangfire.** Mature .NET background-job library with dashboard and cron support. Owns its own schema (`HangFire.*` tables) — separate from app schema, so cross-table transactions are awkward. Job serialization is reflection-driven, less ergonomic than a structured JSONB payload.
- **D. Wolverine durable queue.** Discussed and rejected in [[0018-portal-api-minimal-apis-vsa]] — framework adoption for ≤1.5 patterns is a poor weight-to-leverage ratio.
- **E. External queue (RabbitMQ, Redis Streams).** Extra infrastructure for one saga. Overkill.

### Saga state durability

- **A. Mutable status enum + step timestamps on `clouds`.** Authoritative state is a column. Resume = read status, run remaining idempotent steps. Simplest possible model.
- **B. Event sourcing** (append-only `provisioning_events`, current state derived as a projection). Frames the workflow as immutable events, gives audit trail and replay-on-reconnect for free. Adds an extra table, a projection step, dual-write care between events and any mutable view.
- **C. Hybrid — both mutable status and append-only event log.** Worst of both: dual-write problem on every transition, two sources of truth to keep consistent.

### Worker wakeup mechanism

- **A. Continuous polling.** Worker runs the claim query every N seconds (e.g., 5s) whether or not there is work. Simple. Up to N seconds of pickup latency. Constant DB load (~12 queries/min idle for N=5).
- **B. Postgres `LISTEN`/`NOTIFY` + safety-net poll.** Producer fires `NOTIFY provisioning_new` inside the same transaction as the `INSERT`. Worker holds a `LISTEN provisioning_new` connection and wakes on the notification — zero DB queries while idle. A coarse safety-net poll every ~60s still catches the expired-lease case (a crashed worker's job becomes eligible for re-claim, but no NOTIFY fires for it). Sub-second pickup latency on the happy path.
- **C. In-process `Task.Run` on POST + periodic recovery scan.** HTTP handler spawns a `Task` directly; durability comes from a recovery scan that picks up `in_progress` rows with expired leases. Simplest happy path; splits the saga into two code paths (live + recovery).

## Decision

### Job queue: Postgres-backed with `FOR UPDATE SKIP LOCKED` + lease/heartbeat

```sql
CREATE TABLE provisioning_jobs (
  id            UUID PRIMARY KEY,
  cloud_id      UUID NOT NULL REFERENCES clouds(id),
  kind          TEXT NOT NULL,        -- 'provision' | 'destroy'
  payload       JSONB NOT NULL,
  status        TEXT NOT NULL,        -- 'pending' | 'in_progress' | 'completed' | 'failed'
  attempts      INT  NOT NULL DEFAULT 0,
  worker_id     TEXT,
  lease_expires TIMESTAMPTZ,
  created_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at    TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX ON provisioning_jobs (created_at) WHERE status = 'pending';
```

Claim loop:

```sql
BEGIN;
SELECT * FROM provisioning_jobs
 WHERE status = 'pending'
    OR (status = 'in_progress' AND lease_expires < now())   -- abandoned by a crashed worker
 ORDER BY created_at
 FOR UPDATE SKIP LOCKED
 LIMIT 1;
-- if found:
UPDATE provisioning_jobs
   SET status = 'in_progress',
       worker_id = $me,
       lease_expires = now() + interval '60 seconds',
       attempts = attempts + 1,
       updated_at = now()
 WHERE id = $found;
COMMIT;
```

Worker is an `IHostedService`. While running a job, it heartbeats every 30s (`UPDATE ... SET lease_expires = now() + 60s`). On crash, the lease expires within 60s, and any worker can re-claim by the same query.

### Worker wakeup: `LISTEN`/`NOTIFY` + safety-net poll

The worker does **not** poll continuously. The producer fires a Postgres notification inside the same transaction as the job insert:

```csharp
// inside CreateCloud handler:
db.Clouds.Add(cloud);
db.ProvisioningJobs.Add(job);
await db.SaveChangesAsync();
await db.Database.ExecuteSqlRawAsync("NOTIFY provisioning_new", ct);
```

The notification only fires on transaction commit, so it cannot signal phantom work. The worker holds a `LISTEN` connection and reacts immediately:

```csharp
// inside ProvisioningWorker (IHostedService):
public async Task ExecuteAsync(CancellationToken ct)
{
    // 1. Startup sweep — recover any expired-lease jobs from a previous crash.
    await ClaimAndRunLoopAsync(ct);

    // 2. Subscribe to NOTIFY.
    await using var listenConn = new NpgsqlConnection(connStr);
    await listenConn.OpenAsync(ct);
    listenConn.Notification += (_, _) => signal.Release();

    await using var listenCmd = new NpgsqlCommand("LISTEN provisioning_new", listenConn);
    await listenCmd.ExecuteNonQueryAsync(ct);

    // 3. Loop: wait on EITHER notification OR safety-net interval.
    while (!ct.IsCancellationRequested)
    {
        var waitTask = listenConn.WaitAsync(ct).AsTask();
        var safetyNet = Task.Delay(TimeSpan.FromSeconds(60), ct);
        await Task.WhenAny(waitTask, safetyNet);

        await ClaimAndRunLoopAsync(ct);   // claim & process all pending work
    }
}
```

**Why the safety-net poll is still required:**
- A crashed worker's `in_progress` job becomes eligible for re-claim when its lease expires — but no NOTIFY fires for it. The 60-second safety-net catches these.
- If the LISTEN connection drops (network blip, Postgres restart, etc.) and reconnect takes a moment, the safety-net acts as a backstop.
- A startup sweep before the LISTEN loop catches any work that accumulated while the worker was down.

**Why this beats continuous polling:**
- Zero DB queries while idle (LISTEN holds a socket open; no queries flow).
- Sub-second pickup latency on the happy path (vs. up to N seconds for an N-second poll).
- Safety-net poll runs at 60s instead of 5s — 12× fewer queries against an idle queue.
- Same crash-resume semantics — the lease + heartbeat mechanism is unchanged.

### Saga state: mutable status enum + step timestamps, no event log

```sql
ALTER TABLE clouds ADD COLUMN
  provisioning_status TEXT NOT NULL,           -- 'pending' | 'planning' | 'applying' |
                                                -- 'dns_registering' | 'cert_provisioning' |
                                                -- 'admin_registering' | 'ready' | 'failed'
  plan_started_at        TIMESTAMPTZ,
  apply_started_at       TIMESTAMPTZ,
  dns_started_at         TIMESTAMPTZ,
  cert_started_at        TIMESTAMPTZ,
  admin_started_at       TIMESTAMPTZ,
  provisioning_completed_at TIMESTAMPTZ,
  provisioning_error     TEXT;                  -- non-null only when status='failed'
```

Worker loop (sketch):

```csharp
foreach (var step in StepsFrom(cloud.ProvisioningStatus))   // resume from current
{
    cloud.ProvisioningStatus = step.Name;
    step.SetStartedAt(cloud, DateTime.UtcNow);
    await db.SaveChangesAsync();                              // checkpoint

    await step.ExecuteAsync(cloud, liveLogBus);               // idempotent

    await JobQueue.HeartbeatAsync(job.Id);                    // extend lease
}
```

**Crash-resume is idempotent because every step is idempotent:**
- Terraform: the `pg` backend keeps state across portal restarts; re-running `apply` is a no-op if the droplet already exists (terraform idempotency guarantee).
- Cloudflare DNS: API supports PUT semantics on existing records.
- Cert polling: read-only.
- Admin-callback polling: read-only.

If a step is genuinely partway through observable side effects, the next worker may retry — that retry is safe by design.

### Live log streaming

In-memory `Channel<LogLine>` per `cloud_id`, with multi-subscriber fan-out:

```csharp
public sealed class LiveLogBus
{
    private readonly ConcurrentDictionary<Guid, List<ChannelWriter<LogLine>>> writers = new();

    public IAsyncEnumerable<LogLine> Subscribe(Guid cloudId, CancellationToken ct) { ... }
    public void Publish(Guid cloudId, LogLine line) { ... }
}
```

If the portal restarts mid-apply, the live tail is lost for that window. The SSE consumer reconnects, re-reads the current `provisioning_status` from the DB (durable), and re-attaches to the live channel.

## Consequences

- **Positive:**
  - One database holds queue, saga state, and app data. No dual-write problem — every state transition is one EF Core `SaveChangesAsync`.
  - Crash-resume needs ~no extra code — the status column carries the resume cursor, and each step is already idempotent by external constraint.
  - Live progress and durable state are clearly separated, each fit to purpose.
  - No external job runner to operate (no Hangfire schema, no Wolverine config, no RabbitMQ).
  - `LISTEN`/`NOTIFY` wakeup gives sub-second pickup latency on the happy path while eliminating the per-poll DB load that a naive worker would generate. Idle DB traffic drops to one safety-net query per minute.
- **Negative:**
  - `FOR UPDATE SKIP LOCKED` is Postgres-specific. Migrating to another database would require rewriting the queue. Acceptable — the rest of the system (Terraform `pg` backend, eventually pgvector on the cloud side) is also Postgres-locked.
  - `LISTEN`/`NOTIFY` is also Postgres-specific. Same lock-in. The safety-net poll could be lifted out and reused against a different database; the NOTIFY wakeup could not.
  - The worker holds an open Postgres connection for the lifetime of the process. Counts against connection-pool budget on Azure Database for PostgreSQL if you ever migrate off Postgres-in-Docker-Compose. Acceptable on Postgres-in-Compose where connection limits are user-configurable.
  - No replay-on-reconnect for SSE subscribers (no event log to replay from). Mitigation: on reconnect, send current status from DB, then attach to live channel. Acceptable UX for a 60–120s flow where the user typically keeps the wizard tab open.
  - No audit trail of phase-by-phase progress beyond per-step timestamps. Acceptable for thesis — no compliance requirement, and the timestamp columns give you median/p95 metrics for the thesis writeup.
- **Neutral:**
  - Concurrent-worker semantics (single worker vs N workers; per-job-type partitioning) deferred to PORTAL-007 — start single-worker for simplicity, add concurrency later if needed. NOTIFY broadcasts to all listeners, so N workers would all wake — `SKIP LOCKED` handles the contention naturally.
  - Retry caps and dead-letter behavior deferred to PORTAL-007.

## Related

- [[0018-portal-api-minimal-apis-vsa]] — VSA structure that houses `Features/Provisioning/`
- [[0020-server-push-sse]] — live log consumed by SSE endpoint
- [[0024-dbcontext-shape]] — single PortalDbContext, raw SQL via `FromSqlInterpolated` for the claim query
- `plans/cloud-pivot-plan-2026-05-13.md §15` — saga pattern committed in the plan
