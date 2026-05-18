# ADR-0042: Cloud-side composite-ingest pipeline architecture

Status: Accepted (drafted from 2026-05-18/19 grilling; implementation in M5+)
Date: 2026-05-19

## Context

CLOUD-002 (composite ingest) shipped the `/api/ingest` endpoint surface and the
`notes`/`attachments`/`ingest_jobs` schema, but the actual processing pipeline
on the user cloud was a single coarse status field
(`queued/processing/succeeded/dead_lettered`) and no concrete plan for how
attachments flow through their per-kind processors (image VLM, audio ASR,
documents, video). The "composite draft (body + N attachments) → one processed
note" model fixed by [[composite_ingest_decision]] (memory key
`composite_ingest_decision.md`, 2026-05-17) constrains *what* the pipeline must
deliver but says nothing about how.

The user cloud is intentionally **CPU-only** (a small DO droplet, ~4 vCPU /
16 GB; the burst-worker GPU tier of [[0035-burst-worker-llm-tier]] is deferred
to future-work — see §"Deferred future-work hooks" below). The pipeline must
work on that hardware. Locked sidecars from [[0043-cloud-model-lineup]]
(forthcoming, paired with this ADR): Ollama serving MiniCPM-V 4.6 for vision,
docling-serve for structured documents, parakeet-server for ASR, ffmpeg for
video keyframe extraction, in-process Granite Embedding 278m for vectors,
AngleSharp+Readability for URLs.

Plugin-side captures arrive as composite drafts: arbitrary mix of body text +
URLs + images + audio + video + documents + opaque files. Each attachment kind
needs a different processor; attachments within one note vary in size from a
few KB to tens of MB; a single capture may queue while a previous one is still
processing. The pipeline must extract per-attachment content, compose a unified
Markdown body, route the note to a project, find entity mentions, embed for
retrieval, and surface progress to the plugin — all without GPU.

The portal-side provisioning saga ([[0033-provisioning-saga-and-worker]] +
[[0019-background-work-and-saga-durability]]) demonstrates the SKIP-LOCKED +
lease + transition-version pattern for durable in-process work coordination on
the portal side. The cloud-side ingest pipeline borrows the same primitives but
operates over an additional concurrency axis: multiple **heterogeneous
sidecars** (each with its own runtime concurrency profile) that the saga must
dispatch to without thrashing the shared CPU.

## Decision

A two-layer queue: **ingest jobs** (one per note) drive coarse state; per-job
**extraction tasks** (one per attachment-extraction sub-work) drive fine state.
Specialist worker pools per sidecar claim sub-tasks via SKIP LOCKED, capped by
per-sidecar concurrency. All workers are in-process `IHostedService`s in the
`Cloud.Api` process; no separate worker binary. Progress is published to the
plugin over a single per-cloud SSE channel; correctness is recovered via
`/api/sync/pull` regardless of SSE drops. Plugin uploads binaries via
presigned PUT directly to Spaces — cloud-api never holds binary content.

### 1. Worker topology

- `JobOrchestratorWorker` (3 concurrent slots, config-driven). Claims one
  `ingest_jobs` row at a time via SKIP LOCKED. Advances the row through the
  phase machine in §3. During `extracting_attachments` it splits attachments
  into `extraction_tasks` rows and waits (polling + LISTEN/NOTIFY) for all to
  reach terminal state.
- `VlmWorker` (1 worker, claims `target_sidecar='ollama'`).
- `DoclingWorker` (2 workers, `target_sidecar='docling'`).
- `ParakeetWorker` (1 worker, `target_sidecar='parakeet'`).
- `UrlFetcherWorker` (4 workers, `target_sidecar='url'`; pure I/O bound).
- `VideoSplitterWorker` (1 worker, `target_sidecar='video'`; runs ffmpeg
  locally, then spawns child extraction_tasks for keyframes + audio track).

Each specialist worker holds an in-process `SemaphoreSlim` against its
sidecar; the per-sidecar concurrency cap (`Ollama=1, Docling=2, Parakeet=1,
Embedding=2 (in-process), UrlFetcher=4`) prevents thrashing when multiple jobs
fan out simultaneously. All caps live in `appsettings.json` under
`IngestSaga:Sidecars:*:MaxConcurrency`.

### 2. API surface (three-phase upload)

The plugin issues three calls per composite ingest, with binary bytes never
transiting cloud-api:

```
POST /api/ingest/init
  → metadata only (clientNoteId, bodyInput, attachments[])
  ← noteId + presigned PUT URLs per binary attachment

PUT  https://<spaces>/.../<storage_key>?<sig>
  → from plugin direct to Spaces, per binary attachment, possibly in parallel

POST /api/ingest/{noteId}/finalize
  → empty body; signals "all uploads done"
  ← ingestJobId + status=queued; saga claims
```

Behaviors locked:

| Concern | Behavior |
|---|---|
| Idempotency | `clientNoteId` UNIQUE-INDEX-on-`notes` (partial, NOT NULL). Second `/init` returns the same `noteId` with freshly-regenerated presigned URLs (URLs expire). `clientAttachmentId` UNIQUE-INDEX-on-(`note_id`, `client_attachment_id`). |
| URL attachments | No upload phase; cloud stores URL string from `/init`. |
| Pure-text captures | No binaries, no presigned URLs in response; explicit `/finalize` still required (uniform protocol). |
| Hash dedup | If incoming `sha256` matches an existing extracted attachment row in the cloud, `/init` returns `cached: true` and the plugin skips the PUT. Cross-note dedup is automatic. |
| Upload trust | `/finalize` trusts the client; it does NOT HEAD the bucket to confirm. Missing binaries surface as `extraction_status='failed'` naturally. |
| Presigned-URL expiry | 15 min, configurable; plugin retries `/init` to refresh. |
| Abandoned drafts | Notes left `pending` past 24h are garbage-collected by housekeeping (out of MVP scope; trivially small at thesis scale). |

The split contract — JSON metadata to cloud-api, binary bytes to Spaces — is
the **load-bearing assumption that keeps cloud-api thin**. Cloud-api RAM,
bandwidth, and connection-hold time stay independent of attachment size.
Without this split the 4 vCPU / 16 GB droplet cannot safely accept concurrent
captures with multi-MB binaries.

### 3. State machines

**`ingest_jobs.status` (single-column phase vocabulary):**

```
                       finalize endpoint
                              │
                              ▼
                          [queued]
                              │ JobOrchestrator claims
                              ▼
              [extracting_attachments]
                              │ split into extraction_tasks
                              │ wait until all sub-tasks terminal
                              ▼
                        [composing]
                              │ template + concat extracted_texts
                              ▼
                         [routing]
                              │ LLM picks project
                              ▼
                  [extracting_entities]
                              │ LLM finds mentions, pgvector dedup
                              ▼
                        [embedding]
                              │ Granite encode body_output
                              ▼
                        [succeeded]    (terminal: note.status=ready)

  Failure terminals (per phase, after retry budget):
    failed_extraction
    failed_composition
    failed_route
    failed_entities
    failed_embedding
    dead_lettered
```

**Phase retry policy:**

| Phase | MaxAttempts | Backoff (s) |
|---|---|---|
| `extracting_attachments` | 1 (orchestrator delegates retries to sub-tasks) | — |
| `composing` | 2 | 2 → 4 |
| `routing` | 3 | 2 → 4 → 8 |
| `extracting_entities` | 3 | 2 → 4 → 8 |
| `embedding` | 3 | 2 → 4 → 8 |

All values in `appsettings.json:IngestSaga:Phases:*:MaxAttempts` /
`BackoffSecondsBase`.

**`extraction_tasks.status`:**

```
                   orchestrator INSERT
                           │
                           ▼
                       [queued]
                           │ specialist worker claims
                           ▼
                    [processing]
                           │
            ┌──────────────┼─────────────────┬───────────────┐
            ▼              ▼                 ▼               ▼
       [succeeded]      [failed]          [skipped]   (retry → queued)
       (sidecar OK)   (attempts          (classical
                       exhausted)         filter rejected)
```

A failed `extraction_task` does **not** fail its parent `ingest_job`. The
orchestrator implements best-effort composition (per §4 below).

**Phase → `note.status` projection** (what the plugin sees):

| `ingest_job.status` | `note.status` |
|---|---|
| (no row yet) | `pending` |
| `queued`, `extracting_attachments`, …, `embedding` | `processing` |
| `succeeded` | `ready` |
| any failure terminal | `failed` |

### 4. Failure cascade

Per-attachment failures are isolated. The `extracting_attachments` phase
advances to `composing` once **all** extraction_tasks reach a terminal state,
regardless of whether each task succeeded, failed, or was skipped. The compose
phase then renders only successfully extracted attachments; failed/skipped
attachments are hidden entirely from `body_output` (per
[[0044-composite-note-assembly]] forthcoming — clean vault output > diagnostic
clutter). Failure detail is preserved on the `attachments` row
(`extraction_error`, `extraction_status`) and in `notes.provenance` JSONB; the
user accesses it via portal or the optional `.provenance/<note-id>.json`
shadow tree.

This best-effort policy fits the F4 lock from the data-model grilling: one
failed image does not destroy the entire capture.

### 5. Progress channel — SSE

A single per-cloud SSE endpoint (`GET /api/sync/events`, bearer-token gated)
streams a closed-vocabulary set of events:

- `note_phase_changed`
- `attachment_status_changed`
- `note_succeeded`
- `note_failed`
- `hub_materialized`

On reconnect, the plugin issues a `/api/sync/pull?since=<last_cursor>` to
backfill missed state. No `Last-Event-ID` replay on the server. This is the
same pattern as [[0020-server-push-sse]] applied to ingest;
[[0036-wizard-progress-transport]] uses it for the portal wizard. The two SSE
streams (portal-side for provisioning, cloud-side for ingest) are siblings.

SSE was chosen over WebSockets despite the latter's more complete feature set
because the data flow is strictly server→plugin (no client-initiated
real-time signaling); `Authorization: Bearer` works natively with SSE but is
awkward with browser WebSocket APIs; auto-reconnect is built into
`EventSource`; and the existing nginx + cloudflared topology requires no
additional configuration for plain HTTP/1.1 streams.

### 6. Cancellation and reprocess

**Cancellation = delete.** No explicit `/cancel` endpoint. User-initiated
`DELETE /api/notes/{id}` sets `notes.deleted_at = now()`; the saga checks
`deleted_at IS NOT NULL` at every phase boundary and dead-letters cleanly
with `last_error='user_cancelled'`. The note remains a tombstone (per F11
soft-delete) and propagates to plugins as a deletion via `/api/sync/pull`.

**Reprocess = new `ingest_jobs` row of kind `reprocess`.** `POST
/api/notes/{id}/reprocess` issues a fresh `ingest_jobs` row referencing the
existing `notes.id`. The saga reuses existing `attachments` rows (cached by
`extraction_cache_key = sha256:sidecar:model:version`) — same-model
reprocess hits the cache and is effectively free; model-version-bumped
reprocess invalidates correctly. UNIQUE INDEX on `ingest_jobs (note_id)
WHERE status NOT IN (terminals)` prevents concurrent capture / reprocess /
hub-regen on the same note.

**Hub regen = new `ingest_jobs` row of kind `hub_regen`** spawned by the
`extracting_entities` phase when an entity crosses its mention threshold or is
explicitly user-created with `source='user'` (which auto-creates a hub per
F9). Hub regen rows skip `extracting_attachments` and start at `composing` —
their input is the entity + its mentions, not raw attachments.

The `kind` column on `ingest_jobs` is `'capture' | 'reprocess' | 'hub_regen'`.
The same phase machine and worker pool serve all three.

### 7. Latency budget

Measured/projected wall-clock on a 4 vCPU / 16 GB droplet without GPU,
end-to-end from `/finalize` to `note.status=ready`:

| Scenario | ~Latency | Notes |
|---|---|---|
| Pure text capture | ~10 s | Skips `extracting_attachments` |
| Single URL capture | ~11 s | URL fetch ~200 ms |
| Single image (whiteboard) | ~23 s | VLM ~10 s |
| Voice memo (60 s) | ~57 s | Parakeet ~45 s |
| PDF (5 pages, native text) | ~22 s | Docling ~10 s |
| Composite (text + image + voice) | ~58 s | Fan-out: max(VLM 10 s ∥ Parakeet 45 s) |
| Heavy composite (text + 2 images + voice + PDF) | ~62 s | Fan-out: max(VLM 20 s serial ∥ Parakeet 45 s ∥ Docling 10 s) |

A constant ~10 s tax for routing + entity-extraction + embedding applies on
every note. Composites dominated by the slowest sidecar (typically ASR).
Inter-job concurrency (3 worker slots) doubles throughput under queue
pressure. CPU contention from concurrent sidecar calls is mitigated by
per-sidecar semaphores (no two VLM calls run simultaneously; Ollama serializes
its inference internally anyway).

This is the **CPU-only thesis story**: a usable composite-capture pipeline,
median ~1 min latency, no GPU dependency, on a $20/mo droplet. The
burst-worker GPU tier from [[0035-burst-worker-llm-tier]] is **deferred** —
not because the design is wrong, but because the CPU-only story is more
defensible thesis-novel architecture. The two designs are not mutually
exclusive; see §"Deferred future-work hooks."

## Schema delta (post-CLOUD-002, lands as one EF migration before M5)

```sql
-- notes
ALTER TABLE notes ADD COLUMN embedding         vector(256)    NULL;
ALTER TABLE notes ADD COLUMN deleted_at        TIMESTAMPTZ    NULL;
ALTER TABLE notes ADD COLUMN is_hub            BOOLEAN        NOT NULL DEFAULT false;
ALTER TABLE notes ADD COLUMN project_id        UUID           NULL REFERENCES entities(id) ON DELETE SET NULL;
ALTER TABLE notes ADD COLUMN hub_entity_id     UUID           NULL REFERENCES entities(id) ON DELETE SET NULL;
ALTER TABLE notes DROP COLUMN suggested_project;  -- replaced by project_id
ALTER TABLE notes ADD COLUMN transition_version BIGINT        NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX ix_notes_client_note_id ON notes(client_note_id) WHERE client_note_id IS NOT NULL;
CREATE INDEX ix_notes_embedding ON notes USING hnsw (embedding vector_cosine_ops);
CREATE INDEX ix_notes_updated_at ON notes(updated_at) WHERE deleted_at IS NULL OR status = 'ready';

-- attachments
-- (kind enum extension url|image|audio|document|video|file is code-only; column is TEXT)
ALTER TABLE attachments ADD COLUMN parent_attachment_id   UUID           NULL REFERENCES attachments(id) ON DELETE CASCADE;
ALTER TABLE attachments ADD COLUMN extraction_cache_key   TEXT           NULL;
CREATE UNIQUE INDEX ix_attachments_note_client ON attachments(note_id, client_attachment_id);
CREATE INDEX ix_attachments_parent ON attachments(parent_attachment_id);
CREATE INDEX ix_attachments_cache  ON attachments(sha256, extraction_cache_key) WHERE extracted_text IS NOT NULL;

-- ingest_jobs (extended; status values are code-only)
ALTER TABLE ingest_jobs ADD COLUMN kind                TEXT NOT NULL DEFAULT 'capture'
  CHECK (kind IN ('capture','hub_regen','reprocess'));
ALTER TABLE ingest_jobs ADD COLUMN events_log          JSONB NOT NULL DEFAULT '[]';
ALTER TABLE ingest_jobs ADD COLUMN transition_version  BIGINT NOT NULL DEFAULT 0;
CREATE UNIQUE INDEX ix_ingest_jobs_active_per_note ON ingest_jobs (note_id)
  WHERE status NOT IN ('succeeded','failed_extraction','failed_composition',
                       'failed_route','failed_entities','failed_embedding','dead_lettered');

-- new: extraction_tasks
CREATE TABLE extraction_tasks (
  id                  UUID PRIMARY KEY,
  ingest_job_id       UUID NOT NULL REFERENCES ingest_jobs(id) ON DELETE CASCADE,
  attachment_id       UUID NOT NULL REFERENCES attachments(id) ON DELETE CASCADE,
  target_sidecar      TEXT NOT NULL CHECK (target_sidecar IN ('ollama','docling','parakeet','url','video')),
  status              TEXT NOT NULL CHECK (status IN ('queued','processing','succeeded','failed','skipped')),
  attempts            SMALLINT NOT NULL DEFAULT 0,
  last_error          TEXT,
  lease_owner         TEXT,
  lease_expires_at    TIMESTAMPTZ,
  scheduled_at        TIMESTAMPTZ NOT NULL DEFAULT now(),
  started_at          TIMESTAMPTZ,
  finished_at         TIMESTAMPTZ,
  transition_version  BIGINT NOT NULL DEFAULT 0,
  events_log          JSONB NOT NULL DEFAULT '[]',
  created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
  updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
);
CREATE INDEX ix_extraction_tasks_claim ON extraction_tasks (target_sidecar, status, scheduled_at)
  WHERE status IN ('queued','processing');
CREATE INDEX ix_extraction_tasks_job ON extraction_tasks (ingest_job_id);

-- new: entities, mentions (covered in ADR-CLOUD-NOTES-SCHEMA, listed here for completeness)
-- new: pgvector extension
CREATE EXTENSION IF NOT EXISTS vector;
```

Schema lands as one migration; all M5+ code references the new shape.

## Alternatives considered

1. **Option A: per-sidecar `SemaphoreSlim` + single `ingest_jobs` queue, no
   sub-task table.** Simpler — ~1d to implement. The orchestrator
   `Task.WhenAll`s extraction work in-process per job. Rejected: a job's lease
   expiry mid-fan-out wastes all completed extractions on the restart (hash
   cache mitigates partially); cross-job extraction utilization is implicit and
   unobservable; per-sidecar queue depth is not directly visible in the DB.
   Acceptable as a "fast demo, defer architecture" fallback. The Option-B
   schema cost (one new table) is small.

2. **Hangfire.** Mature .NET job library with retry, dashboard, scheduling.
   Rejected: its job model fights the saga's phase machine (Hangfire is
   job-with-continuations, not phase-with-recovery); queue-naming for
   per-sidecar isolation works but is bolted-on; "wait for all sub-tasks"
   requires Hangfire Pro batches (paid) or third-party plugins. Adds external
   dependency. Less thesis-defensible than a custom saga.

3. **TickerQ.** Modern .NET job scheduler with EF Core or Redis persistence
   ([[research-2026-05-19-tickerq.md]]). Rejected for ingest specifically:
   single-pool model has no per-queue concurrency limits — the sidecar
   isolation we need requires running multiple TickerQ instances with separate
   identifiers, which is ugly. TickerQ remains a reasonable fit for *cron-like*
   recurring jobs on the cloud side (housekeeping, weekly eval prompts) but is
   not the right shape for a 5-phase saga.

4. **Channels-based pipeline (`System.Threading.Channels`).** Each phase a
   bounded `Channel<JobContext>`; workers consume from one channel and push to
   the next. Idiomatic modern .NET, natural backpressure. Rejected: channels
   are in-memory; durability requires DB checkpoints between phases anyway,
   which lands us back at the Option-A or Option-B shape. Cleaner code, same
   runtime model.

5. **Workflow engine (Temporal.io / Conductor).** Production-grade durable
   workflow with built-in retry, supervision, replay. Rejected: significant
   infrastructure cost (Temporal needs its own Postgres cluster); learning
   curve; pulls in cross-process orchestration that the single-droplet
   architecture does not need. Useful if the architecture ever splits across
   multiple processes / machines.

6. **Actor model (Akka.NET / Proto.Actor / Orleans).** Each note an actor.
   Rejected for the same overhead-vs-thesis-scale reasoning as workflow
   engines.

7. **Per-attachment finalize endpoint (replace bulk finalize with one per
   attachment).** Considered for streaming uploads. Rejected: no thesis-visible
   benefit; bulk `/finalize` semantics are clean.

8. **Multipart-inline upload through cloud-api** (no presigned URLs). Rejected:
   blows up cloud-api RAM and bandwidth (§2). The presigned-PUT split is
   non-negotiable on the constrained droplet.

9. **GPU burst worker as default** ([[0035-burst-worker-llm-tier]]). Rejected
   for thesis MVP — the CPU-only architecture is the thesis contribution.
   Burst-worker remains a documented future-work hook (§Deferred).

## Consequences

### Positive

- Single deployable: `Cloud.Api` runs the HTTP surface AND the saga workers
  AND the SSE channel — one container per user cloud.
- The same SKIP-LOCKED + lease + transition-version pattern as the portal
  side; operators and future maintainers see one mental model across both.
- Per-sidecar concurrency caps make resource contention explicit and tunable
  in config; under-utilization or thrashing is observable as queue depth on
  `extraction_tasks` rows.
- `extraction_tasks` table gives the M9 evaluation chapter clean
  instrumentation: per-sidecar latency, queue wait time, hit/miss ratio on
  the hash cache.
- Reprocess + hub-regen reuse the same machinery via the `kind` discriminator;
  no parallel code paths.
- Failure cascade is best-effort by default, preserving partial work and
  giving the user something useful even when one attachment fails.

### Negative / accepted costs

- Two-table queue (`ingest_jobs` + `extraction_tasks`) is more state to keep
  consistent than a single-table approach. Mitigated by FK CASCADE + explicit
  orchestrator polling.
- Mid-phase cancellation is not supported in MVP — saga checks
  `notes.deleted_at` only at phase boundaries. A 45 s ASR call won't abort
  mid-stream when the user deletes. Acceptable; future-work hook to refine.
- The "all extraction_tasks terminal" check is currently a poll-or-LISTEN
  loop; under load this could become inefficient (one polling JobOrchestrator
  per job). Mitigated by 3-slot worker cap and short poll interval; revisit
  if eval observes contention.
- Per-sidecar concurrency caps are tuned by hand; if a new model has
  different cost characteristics, the caps need re-tuning. Not automated.

### Operational

- Cloud-api restarts: leases expire after `LeaseDurationSeconds` (120 s);
  next-run workers re-claim. No work is lost; partial extraction_tasks resume
  from the last terminal state of their siblings.
- Backups: the `notes`/`attachments`/`entities`/`mentions`/`ingest_jobs`/
  `extraction_tasks`/`embeddings` are all in the same Postgres DB — a single
  `pg_dump` suffices.
- Observability: each phase transition emits an OTel span; per-sidecar
  Prometheus gauges (`ingest_sidecar_queue_depth{sidecar}`) expose queue
  health.

## Deferred future-work hooks

These are explicitly out of scope for thesis MVP but the architecture above
does not foreclose them:

1. **GPU burst worker** ([[0035-burst-worker-llm-tier]]). Add a fifth specialist
   worker `BurstVlmWorker` that polls extraction_tasks where the cloud has
   marked a GPU-capable burst peer available. The orchestrator's split logic
   can prefer the burst worker when high-priority or when local VLM queue
   depth is high. Composable with the current design — no schema change
   beyond a `target_sidecar='ollama-gpu'` value or a `priority` column.

2. **Mid-phase cancellation.** The saga can check `notes.deleted_at` inside
   long-running sidecar calls (e.g., during Parakeet streaming chunks). Adds
   complexity; useful when users routinely cancel mid-flight, which thesis
   captures don't do.

3. **Parakeet streaming transcription.** Chunked transcribe with partial
   results streaming via the SSE channel. Currently the saga waits for the
   full transcript before advancing. Streaming reduces perceived latency on
   voice memos.

4. **Merged routing + entity-extraction LLM call.** Two LLM passes
   ([[0044-cloud-intelligence-layer]] forthcoming) currently account for ~10 s
   of the per-note tax. Combining them into one structured-output call could
   save ~3-5 s. Deferred because separate calls give cleaner eval metrics for
   thesis.

5. **Cross-job sub-task pool.** Today each `JobOrchestrator` waits on its own
   job's sub-tasks. A future refactor could have a single global "completed
   extraction" notifier; useful only at much higher queue depth than thesis
   demonstrates.

6. **Per-user / per-cloud sub-task `priority` field.** Useful when a "user is
   waiting for this one" capture should jump the queue past a background
   reprocess. Trivial to add (`priority INT NOT NULL DEFAULT 0`,
   `ORDER BY priority DESC, scheduled_at`).

## References

- [[0019-background-work-and-saga-durability]] — the SKIP LOCKED + lease pattern this builds on
- [[0020-server-push-sse]] — the SSE pattern for progress
- [[0024-dbcontext-shape]] — EF Core + Postgres conventions used by the new tables
- [[0028-schema-conventions]] — snake_case columns + Guid v7 ids + Instants
- [[0032-fk-cascades-and-soft-delete]] — CASCADE + soft-delete patterns
- [[0033-provisioning-saga-and-worker]] — portal-side sibling architecture
- [[0035-burst-worker-llm-tier]] — deferred future-work peer
- [[0036-wizard-progress-transport]] — analogous SSE flow in portal wizard
- [[0041-portal-cloud-admin-token-storage]] — DataProtection-encrypted plaintext for portal→cloud admin calls
- `plans/cloud-pivot-plan-2026-05-13.md` — overall cloud-pivot blueprint
- `memory/composite_ingest_decision.md` — composite-ingest decision (2026-05-17)
- Forthcoming companion ADRs:
  - **ADR-0043: cloud-side model lineup** (MiniCPM-V, Docling, Parakeet, Granite, AngleSharp+Readability)
  - **ADR-0044: cloud intelligence layer** (LLM routing, entity extraction, hub generation)
  - **ADR-0045: composite note schema** (entities, mentions, hubs, projects)
