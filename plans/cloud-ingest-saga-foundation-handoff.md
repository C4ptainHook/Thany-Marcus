# CLOUD-INGEST-SAGA-FOUNDATION — JobOrchestrator + extraction_tasks claim loop + SSE bus — Handoff Brief

Date: 2026-05-19
Status: Draft. Third ticket of the ADR-0042 implementation series. Lands the phase-machine saga + per-sidecar specialist worker pools + SSE progress channel + reprocess/cancel endpoints + provenance event log, with every sidecar call routed through a **stub client returning canned data**. Real model wiring (`VlmWorker → ollama:11434`, `DoclingWorker → docling:5001`, `ParakeetWorker → parakeet:5092`, `UrlFetcherWorker → live HTTP`, `VideoSplitterWorker → ffmpeg`) lands in handoffs #4–#6.

**Goal:** replace the CLOUD-002-vintage single-phase saga (`IngestSagaWorker` + `CompositeIngestHandler`) with the ADR-0042 §3 phase machine, drive per-attachment work through `extraction_tasks` claimed by specialist workers under per-sidecar concurrency caps, expose a per-cloud SSE channel (`GET /api/sync/events`) symmetric to the portal's `/api/clouds/{id}/events`, accept `POST /api/notes/{id}/reprocess` for cache-aware re-runs, treat `DELETE /api/notes/{id}` as cancellation, and append an immutable provenance event for every phase transition. After this ticket: a composite-ingest finalize triggers the orchestrator → it splits attachments into queued `extraction_tasks` → five specialist workers (one of each kind) claim sub-tasks via SKIP LOCKED → each calls a stub client that returns canned text → the orchestrator advances through `composing → routing → extracting_entities → embedding → succeeded`, emitting an SSE event at each transition. The plugin sees `note.status` flip `processing → ready` over ~10 s with no real model behind any sidecar yet.

Estimated **3 person-days** with AI-agent assistance, split into three passes (~1d each). Pass A is the orchestrator + claim loop + state-machine plumbing (no specialist workers, no SSE). Pass B is the five specialist workers + their stub clients + the new endpoints (`reprocess`, `DELETE`, `events`). Pass C is wiring, tests, and the cross-ticket regression anchor. The estimate is rough — it could compress to 2 days if the EF Core SKIP-LOCKED patterns transfer cleanly from the portal saga; it can stretch to 3.5 days if the SSE notification fan-out hits unexpected Npgsql concurrency issues.

After this ticket, handoffs #4–#6 swap the stub clients for real sidecar HTTP clients **one worker at a time** without touching the saga, the schema, the endpoints, the SSE bus, or the cancellation logic. That isolation is the whole point of this ticket: a finished framework with five drop-in seams.

This handoff **does not** ship the schema migration (that's handoff #1, `cloud-schema-v2-handoff.md`) and does not ship the sidecar processes (those are handoff #2, `cloud-sidecars-handoff.md`). It assumes both have landed and the cloud DB has `entities`, `mentions`, `extraction_tasks`, the new `notes`/`attachments`/`ingest_jobs` columns, and the phase vocabulary check constraint.

## Where decisions live (read before doing anything)

- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — the contract. §1 worker topology (`JobOrchestratorWorker` 3-slot + 5 specialists with per-sidecar caps), §2 three-phase upload semantics (already shipped in CLOUD-002), §3 state machines (`ingest_jobs.status` 13-value phase vocabulary; `extraction_tasks.status` 5-value vocabulary; phase retry budgets in §3 table), §4 best-effort failure cascade, §5 SSE event vocabulary (`note_phase_changed | attachment_status_changed | note_succeeded | note_failed | hub_materialized`), §6 cancellation-as-delete + reprocess-as-new-job + hub-regen-as-new-job, §7 latency budget. **This ticket implements §§1–6 with stub specialist clients; §7 numbers become measurable after handoffs #4–#6.**
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — SKIP LOCKED + lease + mutable status. Applies as-is to *both* queues (`ingest_jobs` and `extraction_tasks`); the orchestrator owns one row in the first, the specialist worker owns one row in the second.
- **`docs/decisions/0020-server-push-sse.md`** — SSE shape, keep-alive cadence, `X-Accel-Buffering: no` header, no `Last-Event-ID` replay. The cloud-side SSE channel is the second consumer of this pattern (portal's `/api/clouds/{id}/events` is the first).
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the blueprint for the phase-state-machine + ownership-loss exception + `transition_version` optimistic-concurrency. Portal-side concrete code in `src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs` (claim loop, heartbeat, LISTEN/NOTIFY) and `Features/Provisioning/SagaPhaseDispatcher.cs` (per-phase handler dispatch). **Copy patterns, not code** — the cloud-side queue runs in-process with the API rather than as a separate worker binary.
- **`docs/decisions/0036-wizard-progress-transport.md`** — portal-wizard SSE translation pattern (`SagaEventTranslator` produces `WizardSseEvent` deltas). Cloud-side `IngestSseTranslator` follows the same shape.
- **`docs/decisions/0045-composite-note-schema.md`** — §"Migration plan" + §§4–6 + §11 + §12. The schema this ticket consumes was just laid down by `cloud-schema-v2-handoff.md`; column names and FK shapes match ADR-0045 verbatim.
- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — soft-delete via `deleted_at` on `notes` (and `entities`); the orchestrator's per-phase cancellation check reads this column.
- **`docs/decisions/0024-dbcontext-shape.md`** + **`docs/decisions/0028-schema-conventions.md`** — single `CloudDbContext`, snake_case via naming convention, `IClock`-driven `Instant` timestamps, `IHasUpdatedAt` interceptor. New entities follow the same pattern (already scaffolded by the schema migration).
- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers + Respawn. `PostgresFixture` and `CloudApiFactory` already exist; the new tests reuse them.
- **`plans/cloud-002-handoff.md`** — current saga baseline. The `IngestSagaWorker` + `CompositeIngestHandler` files this ticket **replaces** live at `src/ThanyMarcus.Cloud.Api/Features/Processing/`. The `IngestEndpoints.MapIngestEndpoints` + `SyncPullEndpoint.MapSyncPullEndpoint` shapes **stay byte-for-byte the same** — this ticket is purely about what happens *after* `/finalize` enqueues a row.
- **`plans/cloud-schema-v2-handoff.md`** — the schema this ticket reads/writes. The legacy `'processing'` value in the `ingest_jobs.status` check constraint is **dropped at the end of this ticket** (final migration in §"Schema cleanup"). All other schema-v2 columns (`embedding`, `deleted_at`, `is_hub`, `project_id`, `hub_entity_id`, `transition_version`, `events_log`, `kind`, the entire `extraction_tasks` table, the new partial-unique-index `ix_ingest_jobs_active_per_note`) are already present.
- **`plans/cloud-sidecars-handoff.md`** — the sidecars this ticket's *future* siblings will call. The compose stack already runs `ollama`, `docling`, `parakeet` containers; their `IngestSaga:Sidecars:*:BaseUrl` config keys are already in `appsettings.json`. This ticket adds `IngestSaga:Sidecars:*:MaxConcurrency` consumption (the keys exist; the semaphores are new) and adds the **stub clients** that satisfy the same `I{Vlm,Docling,Parakeet,UrlFetcher,VideoSplitter}Client` interfaces the real ones will eventually implement.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. The saga rewrite must preserve this invariant: one finalize → exactly one `ingest_jobs` row → fan-out into N `extraction_tasks` → one terminal `note.status`. Hub regen and reprocess each get their own `ingest_jobs` row (per `kind` discriminator) but do not produce parallel notes.
- **Memory `portal_architecture.md`** — Postgres job queue, mutable status, no event sourcing, SSE only. Applies as-is.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3. `Cloud.Api` inherits all conventions.

**Do not litigate ADR-0042.** If you find a phase-machine edge case you think is wrong, raise it as a follow-up; do not change the vocabulary in this ticket. The ticket is "implement the ADR," not "improve the ADR."

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **Single deployable.** Orchestrator + 5 specialist workers + SSE bus all run in-process inside the `Cloud.Api` host. No separate `Cloud.SagaWorker` binary (unlike the portal split). ADR-0042 §"Consequences" locks this; the cloud is one container per user, the portal is two.

2. **Specialist workers are individual `IHostedService`s, not a generic worker pool.** Each one (`VlmWorker`, `DoclingWorker`, `ParakeetWorker`, `UrlFetcherWorker`, `VideoSplitterWorker`) is a separate class, separately registered, claiming only its own `target_sidecar` value. This makes per-sidecar concurrency tuning, observability (one OTel resource per worker), and debugging (one log scope per worker) trivial. The cost is ~50 LOC of repeated claim-loop boilerplate per worker; **deduplicate into a `SpecialistWorkerBase<TClient>` abstract base** to keep total LOC sane.

3. **JobOrchestrator is one worker class, 3 concurrent slots.** Mirrors `Portal.SagaWorker.SagaWorker` but consumes one `IngestJob` row per slot via SKIP LOCKED, then drives the phase machine in-process for that job. The orchestrator polls (with `LISTEN extraction_tasks_changed`) for sub-task completion during the `extracting_attachments` phase — no in-memory wait barrier. This means a JobOrchestrator restart re-discovers in-flight sub-tasks via the DB rather than holding `Task.WhenAll` state.

4. **Five specialist worker pool sizes are baked-in (per ADR-0042 §1) and config-overridable.** Default registrations: `VlmWorker=1`, `DoclingWorker=2`, `ParakeetWorker=1`, `UrlFetcherWorker=4`, `VideoSplitterWorker=1`. Each registers itself N times if its `Replicas` config key is set higher (one worker instance per replica). The semaphore inside each worker remains `1` — the *pool size* is the inter-worker concurrency, the *semaphore* is the per-sidecar serialization within a single worker. ADR-0042 §1 conflates these slightly; the implementation uses pool size only and removes per-worker semaphores (a single worker is already serial).

5. **`extraction_tasks` claim semantics:** identical to `ingest_jobs` (SKIP LOCKED + 60s lease + heartbeat every 20s + retry on lease-expiry). Lease and heartbeat live on `extraction_tasks` rows directly (`lease_owner`, `lease_expires_at` — already in the schema). No second join through `ingest_jobs` to check the parent's lease.

6. **Stub clients live in `Infrastructure/Sidecars/Stubs/`** and implement the same interfaces (`IVlmClient`, `IDoclingClient`, `IParakeetClient`, `IUrlFetcherClient`, `IVideoSplitterClient`) as the future real clients. Real client tickets (handoffs #4–#6) swap the DI registrations from `services.AddSingleton<IVlmClient, StubVlmClient>()` to `services.AddSingleton<IVlmClient, OllamaVlmClient>()` and that's the entire delta — saga code reads only the interface, never the concrete type. **The stubs return canned text deterministically (e.g., `"[stub VLM description for {storage_key}]"`) so end-to-end tests can assert exact output.**

7. **Stub LLM (`ChatClientLlm` / routing + entity-extraction LLM)** behavior in this ticket: the `NoOpLlmClient` (already in place from CLOUD-002) is what `routing` and `extracting_entities` phases call when `cloud_settings.llm_mode='safe'`. The orchestrator treats `NoOpLlmClient`'s zero-anchor / zero-mention / `null` project result as a valid terminal — the note routes to `Inbox/` and no entity rows get written. Real LLM behavior arrives later (handoff #4 or #5 alongside VLM/Docling); this ticket only proves the **phase boundaries** are correct against the no-op result shape.

8. **Embedding phase calls `IEmbeddingClient.EmbedAsync(string text, CancellationToken)` → returns `float[]`.** The stub returns `new float[256]` (all zeros). Real Granite ONNX integration lands separately (CLOUD-EMBEDDING in handoff #5). The orchestrator writes the result into `notes.embedding` via `Pgvector.Vector` (added in schema-v2). A zero vector survives the HNSW index just fine (it sorts to a deterministic neighbor set — unhelpful but not broken).

9. **Cancellation is checked at phase boundaries only.** Mid-phase cancellation (per ADR-0042 §"Negative / accepted costs") is **explicitly out of scope**. Each phase handler's entry reads `notes.deleted_at`; if set, the handler short-circuits the job to `dead_lettered` with `last_error='user_cancelled'`, emits a final `note_failed` SSE event, and exits. In-flight extraction_tasks for cancelled jobs are NOT actively cancelled — they complete (writing rows that no orchestrator will read) or hit lease-expiry. This is honest about the architectural cost; a 45 s ASR call cannot be aborted mid-stream.

10. **Reprocess inserts a new `ingest_jobs` row with `kind='reprocess'`, `note_id=<existing>`, status=`queued`.** The orchestrator's `extracting_attachments` phase checks each attachment's existing `extraction_cache_key` against `sha256:{target_sidecar}:{model}:{version}`; on cache hit, it inserts an extraction_task with `status='skipped'` and `extracted_text=<cached>`, never enqueueing real work. Model-bump invalidates the cache naturally because the key changes. The UNIQUE INDEX `ix_ingest_jobs_active_per_note` (already in schema-v2) prevents concurrent reprocess/capture/hub-regen on the same note — second concurrent reprocess returns 409.

11. **`events_log` JSONB append pattern:** on every state transition (orchestrator phase change, extraction_task status change), the worker executes a single SQL UPDATE that appends a JSON object to the `events_log` array using `jsonb_set(events_log, '{-1}', $payload, true)` semantics — but Postgres doesn't append directly that way, so the canonical pattern is `events_log = events_log || $payload::jsonb` (concatenation operator). Each event payload is `{at: <instant>, by: <worker_id>, from: <prev_status>, to: <new_status>, error: <message-or-null>}`. The `notes.provenance` field is then built from `events_log` at terminal time — extracted_failures, model versions, total wall-clock — and overwrites whatever the legacy `CompositeIngestHandler` was writing. The schema reserves `events_log` as the immutable audit trail; `provenance` is the derived summary for the plugin.

12. **`transition_version` is incremented on every UPDATE.** The portal saga uses it to detect ownership-loss-by-double-claim (worker A's heartbeat sees the version moved by worker B → throws `SagaOwnershipLostException`). The cloud-side orchestrator and specialist workers adopt the same guard. The `notes.transition_version` column similarly increments on every `notes.status` or `notes.deleted_at` change; SSE listeners can use it as a cursor for sync-pull backfill but **do not** in this ticket (sync-pull keeps its existing `updated_at` cursor; transition_version is reserved for future optimistic concurrency).

13. **`IIngestEventBus.PublishAsync(...)` writes a row to `notes.events_log` (or `ingest_jobs.events_log`, depending on event kind) AND calls `pg_notify('ingest_events', payload)`.** Two listeners flow off `pg_notify`:
    - The SSE endpoint's per-connection `NpgsqlConnection.Notification` handler (translates payload → `IngestSseEvent` → writes to `Response.Body`).
    - Optionally, the orchestrator's own LISTEN loop (so the `extracting_attachments → composing` transition wakes immediately when the last sub-task lands, rather than poll-waiting up to 1s).
    
    The bus implementation (`PostgresIngestEventBus`) mirrors `PostgresProvisioningEventBus` line-for-line, modulo the channel name and payload shape.

14. **SSE channel is bearer-token authenticated.** `GET /api/sync/events` runs under `RequirePluginAuthFilter` (same filter that protects `/api/sync/pull`). No cookie auth; the plugin sends `Authorization: Bearer <token>` on the `EventSource` connection via the well-supported `EventSourcePolyfill` headers option, or via a query-string token (`?access_token=<>`) for environments without polyfill. **Choose query-string token in MVP** — the alternative (header-on-EventSource) requires a polyfill in the plugin, which is one more dep. Document the choice with a security note: token is logged into the cloud's request log; rotate-by-plugin-token-rotation if a log leak is suspected.

15. **No `Last-Event-ID` replay.** Per ADR-0020. Plugin reconnect calls `GET /api/sync/pull?since=<last_seen_updated_at>` and reconciles via DB state. This is correct for terminal-state arrivals (`note_succeeded`, `note_failed`) but loses transient progress events on reconnect — acceptable per ADR-0020's "no durable event store" trade.

16. **OrphanIngestSweeper (already exists from CLOUD-002) stays untouched.** It cleans `notes` left `pending` past 1h; the new saga adds nothing to its workload. The "saga left a job in `extracting_attachments` for 24h" case is handled by lease expiry + retry, not by the sweeper.

17. **Pgvector `Vector` is the wire type for `notes.embedding` writes.** Schema-v2 introduced the `Pgvector` NuGet ref; this ticket uses it. The orchestrator's `embedding` phase does `note.Embedding = new Vector(embeddingClient.EmbedAsync(...).Result)` (await, not `.Result`). No special EF mapping work — the `UseVector()` call on the DbContext was added in schema-v2.

## Scope boundary (precise)

Three passes, ~1 day each. All three ship in the same handoff because they share the saga's coordination contract; splitting at any pass-boundary would force temporary stub code that gets deleted in the next pass.

### Pass A — claim loop refactor + phase state machine + ownership exception (~1 day)

The `IngestSagaWorker` from CLOUD-002 is replaced by `JobOrchestratorWorker`. The new orchestrator runs the phase machine but with all specialist work **inlined as TODO-stubs** that flip every `extraction_tasks` row directly to `succeeded` without dispatching to specialists. SSE bus is not yet wired. This pass proves the phase machine + retry/lease/transition_version mechanics work end-to-end against the schema-v2 shape; specialist-worker dispatch lands in Pass B.

**1. Replace `IngestSagaWorker.cs` with `JobOrchestratorWorker.cs`.**
   - Copy the claim-loop shape from `src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs:25-220` (concurrency semaphore, `LISTEN/NOTIFY` wakeup, `WaitForWakeupAsync` safety-net poll, heartbeat loop, worker-id format). The portal version uses `Channels` for wakeup signalling; cloud-side uses the same pattern.
   - Concurrency: `IngestSaga:Orchestrator:MaxConcurrentJobs` (default 3 per ADR-0042 §1). `LeaseSeconds=60`, `HeartbeatSeconds=20`, `IdlePollMs=60000`. All in `appsettings.json:IngestSaga:Orchestrator`.
   - Claim SQL targets `ingest_jobs` with the new phase vocabulary:
     ```sql
     UPDATE ingest_jobs SET
         status              = CASE
                                  WHEN status = 'queued' THEN 'extracting_attachments'
                                  ELSE status
                                END,
         lease_owner         = {workerId},
         lease_expires_at    = {now + lease},
         attempts            = attempts + 1,
         transition_version  = transition_version + 1,
         started_at          = COALESCE(started_at, {now}),
         updated_at          = {now}
       WHERE id = (
         SELECT id FROM ingest_jobs
          WHERE status NOT IN ('succeeded','failed_extraction','failed_composition',
                               'failed_route','failed_entities','failed_embedding','dead_lettered')
            AND (status = 'queued' AND scheduled_at <= {now}
                 OR status IN ('extracting_attachments','composing','routing','extracting_entities','embedding')
                    AND lease_expires_at <= {now})
            AND scheduled_at <= {now}
          ORDER BY scheduled_at
          LIMIT 1 FOR UPDATE SKIP LOCKED
       ) RETURNING *;
     ```
     The `CASE` transitions newly-queued jobs to `extracting_attachments` atomically; in-flight jobs whose lease expired keep their current phase (we re-process from where they were). The terminal-set exclusion mirrors the schema-v2 `ix_ingest_jobs_active_per_note` filter exactly.

**2. Add `IngestPhaseDispatcher.cs` (`Features/Processing/`).**
   - Single class with `Task DispatchAsync(IngestJob job, CancellationToken ct)`.
   - Branches on `job.Status`:
     - `extracting_attachments` → `ExtractingAttachmentsHandler.HandleAsync`
     - `composing` → `ComposingHandler.HandleAsync`
     - `routing` → `RoutingHandler.HandleAsync`
     - `extracting_entities` → `ExtractingEntitiesHandler.HandleAsync`
     - `embedding` → `EmbeddingHandler.HandleAsync`
   - Each handler is `IPhaseHandler` (interface in `Features/Processing/Phases/IPhaseHandler.cs`), DI-registered scoped.
   - Before dispatching: check `notes.deleted_at IS NOT NULL`. If set, short-circuit to `CancelHandler.HandleAsync` (writes `dead_lettered` + `last_error='user_cancelled'`, returns).
   - After each handler returns successfully, the handler itself transitions the job to the next phase (via `UPDATE ingest_jobs SET status=<next>, transition_version=...`). The orchestrator then re-loops and the next claim picks up the same job in the new phase. **This avoids the "handler returns enum, dispatcher writes it" double-write pattern** — each handler owns its phase exit transition.

**3. Land the five phase handlers as classes, with `extraction_attachments` real and the rest no-op:**
   - `ExtractingAttachmentsHandler.cs` — **the only real handler in Pass A**:
     1. Load `note` + `attachments` for the job.
     2. For each attachment with `extraction_status='pending'`:
        - Resolve `target_sidecar` by `kind`: `image→ollama`, `file→docling` (PDF) or `url→url` discriminator on the mime / extra, `voice→parakeet`, `url→url`, `video→video`.
        - Compute `extraction_cache_key = sha256:{target_sidecar}:{model_version}` (the model version is `appsettings.json:IngestSaga:Models:<kind>:Version` for the kind, defaults to `"stub-v1"` in Pass A).
        - Check for cache hit: if `attachments` table has any other row with same `sha256` AND same `extraction_cache_key` AND `extracted_text IS NOT NULL`, copy the `extracted_text` onto the current row and skip task enqueue.
        - Otherwise, INSERT an `extraction_tasks` row: `(ingest_job_id, attachment_id, target_sidecar, status='queued', scheduled_at=now)`.
     3. Save changes.
     4. **PASS-A SHORTCUT:** immediately UPDATE all queued `extraction_tasks` for this job to `status='succeeded'` with `extracted_text='[stub Pass-A placeholder]'`. This unblocks the orchestrator's "wait until all sub-tasks terminal" gate without specialist workers. Pass B removes this shortcut.
     5. UPDATE `ingest_jobs.status = 'composing'`.
   - `ComposingHandler.cs` — calls `CompositeMarkdownAssembler.Assemble(note, attachments, /*enrichment=null*/, /*llmMode=null*/)` (the existing CLOUD-002 assembler still works; it accepts a no-anchor enrichment shape). Writes `notes.body_output`, `notes.relative_path='Inbox/{noteId}.md'`. Transitions to `routing`.
   - `RoutingHandler.cs` — calls `ILlmClient.EnrichCompositeAsync(...)` (existing CLOUD-002 client; in `safe` mode this is `NoOpLlmClient`). On a non-null `SuggestedProject`, look up/create an `entities` row with `kind='project'` and matching `canonical_name`; set `notes.project_id` to that entity's id, set `relative_path` to `Projects/{canonical_name}/{noteId}.md`. Transitions to `extracting_entities`.
   - `ExtractingEntitiesHandler.cs` — re-uses the same enrichment result from `RoutingHandler` (cache on the job, OR re-call the LLM — for Pass A simplicity, **re-call**: the no-op client returns `[]` mentions, the loop is empty). For each LLM-emitted mention, look up/create the target `entities` row, then INSERT a `mentions` row pointing back to the note. Increment `entities.mention_count` for each new mention. Transitions to `embedding`.
   - `EmbeddingHandler.cs` — calls `IEmbeddingClient.EmbedAsync(notes.body_output)`. Writes `notes.embedding = new Vector(result)`. Transitions to `succeeded`. On `succeeded`, flips `notes.status='ready'`.
   - Each handler appends one entry to `ingest_jobs.events_log` via the bus (Pass A: bus is a no-op stub; Pass B wires `pg_notify`).

**4. Failure cascade + retry budget.**
   - Each phase has its own retry budget from `appsettings.json:IngestSaga:Phases:<phase>:MaxAttempts` (defaults from ADR-0042 §3 table: `extracting_attachments=1, composing=2, routing=3, extracting_entities=3, embedding=3`) and `BackoffSecondsBase=2`.
   - `IngestPhaseDispatcher` catches handler exceptions:
     - On exception, increment `attempts` on the `ingest_jobs` row.
     - If `attempts < MaxAttempts[phase]`, set `scheduled_at = now + 2^attempts * BackoffSecondsBase`, set `status` back to its CURRENT phase (NOT `queued` — keep the phase so the next claim re-runs the same handler), clear `lease_owner`, set `last_error`.
     - If `attempts >= MaxAttempts[phase]`, transition to the matching failure terminal (`extracting_attachments` → `failed_extraction`, `composing` → `failed_composition`, etc.) and set `notes.status='failed'`.
     - Emit an event (Pass A: stub; Pass B: real `note_phase_changed` or `note_failed`).
   - Concurrency safety: every UPDATE includes `AND lease_owner = {workerId} AND transition_version = {expected}` in the WHERE clause; row-count zero throws `SagaOwnershipLostException` (lift the portal-side class from `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/SagaOwnershipLostException.cs` into a shared location at `src/ThanyMarcus.Shared/Saga/SagaOwnershipLostException.cs` and reference from both projects).

**5. Heartbeat loop** identical shape to `Portal.SagaWorker.SagaWorker.HeartbeatLoopAsync` (`src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs:154-179`), updates `lease_expires_at = now + lease` every 20s, scoped to `(id, lease_owner, status NOT IN terminals)`.

**6. LISTEN/NOTIFY wakeup** on `ingest_jobs_new` (already wired by `IngestEndpoints.NotifyIngestJobAsync`) **plus** a new channel `ingest_jobs_changed` that the dispatcher fires after every phase transition (so a job in `composing` → `routing` doesn't pay the next claim's idle-poll delay). Channel name + payload (just the job id) is the contract; the orchestrator's LISTEN loop joins both channels.

**7. Provenance event log writes** at every transition. Pass A writes them with a local helper inside the dispatcher; Pass B factors out the helper into `IIngestEventBus`. The event payload schema:
```json
{
  "at":   "2026-05-19T10:00:00.000Z",
  "by":   "ThanyMarcus.Cloud.Api@host/9a8b7c6d",
  "from": "extracting_attachments",
  "to":   "composing",
  "error": null
}
```
Applied as `UPDATE ingest_jobs SET events_log = events_log || $payload::jsonb, ...`.

**8. Schema cleanup migration** `0002_drop_legacy_processing_status.cs`:
   - Drop the legacy `'processing'` value from `ck_ingest_jobs_status` (and any in-flight rows holding it — there should be none, but defensively: `UPDATE ingest_jobs SET status = 'extracting_attachments' WHERE status = 'processing'`; the legacy code's `processing` is conceptually the new code's `extracting_attachments`).
   - Drop the `IngestJobStatus.Processing` constant.
   - Update the partial-unique-index filter to remove `'processing'` from the active-set definition (it's not in the terminal-set either; the filter `status NOT IN (terminals)` already excludes it correctly without listing it).
   - **This migration lands at the END of Pass A**, after `JobOrchestratorWorker` is verified to never emit `'processing'`.

**9. Tests for Pass A** under `tests/ThanyMarcus.Cloud.Tests/Features/Processing/`:
   - `JobOrchestratorWorkerTests.cs` — claim contention (two orchestrator instances, one job, exactly one wins; mirror `Portal.Tests/Features/Provisioning/SagaConcurrencyTests.cs`); lease expiry → re-claim → resumes from the right phase; transition_version mismatch → `SagaOwnershipLostException` swallowed + dispatch aborted.
   - `PhaseDispatcherTests.cs` — each handler runs in isolation against a seeded `ingest_jobs` row; assert phase transitions and `events_log` append; assert `notes.deleted_at` short-circuits to `dead_lettered`; assert retry-budget overshoot → correct failure terminal.
   - `RetryBudgetTests.cs` — handler throws → `attempts` increments → `scheduled_at` jumps + same phase persisted; after N exceptions, failure terminal sticks.

→ At end of Pass A: a finalize → job runs through all five phases → terminal `succeeded` → `notes.status='ready'`, in ~50ms (no real model calls, no SSE). The orchestrator + phase machine + lease/retry/transition_version mechanics are proven. **No specialist workers yet** — Pass A's `ExtractingAttachmentsHandler` does the work inline by short-circuit-fulfilling extraction_tasks.

### Pass B — five specialist workers + stub clients + SSE bus + reprocess/cancel endpoints (~1 day)

The Pass-A shortcut in `ExtractingAttachmentsHandler` is **removed**. Five `IHostedService` specialist workers now claim `extraction_tasks` rows and call stub clients. The SSE bus is wired; the reprocess + cancel endpoints land.

**1. Five specialist workers.** Each at `Features/Processing/Specialists/<Name>Worker.cs`:
   - `VlmWorker.cs` (claims `target_sidecar='ollama'`).
   - `DoclingWorker.cs` (claims `target_sidecar='docling'`).
   - `ParakeetWorker.cs` (claims `target_sidecar='parakeet'`).
   - `UrlFetcherWorker.cs` (claims `target_sidecar='url'`).
   - `VideoSplitterWorker.cs` (claims `target_sidecar='video'`).
   
   All inherit from `SpecialistWorkerBase<TClient>` (abstract):
   ```csharp
   public abstract class SpecialistWorkerBase<TClient>(...) : BackgroundService
   {
       protected abstract string TargetSidecar { get; }
       protected abstract Task<string> ExtractAsync(
           TClient client, Attachment att, CancellationToken ct);

       protected override async Task ExecuteAsync(CancellationToken stoppingToken)
       {
           // claim-loop body parameterized by TargetSidecar
       }
   }
   ```
   
   The base class:
   - Holds the claim-loop (SKIP LOCKED on `extraction_tasks` partial index `ix_extraction_tasks_claim`):
     ```sql
     UPDATE extraction_tasks SET
         status              = 'processing',
         lease_owner         = {workerId},
         lease_expires_at    = {now + lease},
         attempts            = attempts + 1,
         transition_version  = transition_version + 1,
         started_at          = COALESCE(started_at, {now}),
         updated_at          = {now}
       WHERE id = (
         SELECT id FROM extraction_tasks
          WHERE target_sidecar = {TargetSidecar}
            AND ((status = 'queued' AND scheduled_at <= {now})
                 OR (status = 'processing' AND lease_expires_at <= {now}))
          ORDER BY scheduled_at
          LIMIT 1 FOR UPDATE SKIP LOCKED
       ) RETURNING *;
     ```
   - Calls `ExtractAsync(client, att, ct)`; on success writes `attachments.extracted_text`, `attachments.extraction_status='extracted'`, `extraction_tasks.status='succeeded'`, `finished_at=now`.
   - On exception: same retry/dead-letter pattern as orchestrator. After `MaxAttempts=3` (`IngestSaga:ExtractionTasks:MaxAttempts`, default 3), flips to `failed` and sets `attachments.extraction_status='failed'` + `attachments.extraction_error=ex.Message`. **A failed extraction_task does NOT bubble up to fail the parent ingest_job** — best-effort cascade per ADR-0042 §4.
   - After every status change, fires `pg_notify('extraction_tasks_changed', '<ingest_job_id>')` so the orchestrator wakes from its "wait for terminal sub-tasks" sleep.
   - Listens to its own dedicated wakeup channel `extraction_tasks_<sidecar>_new` (fired by `ExtractingAttachmentsHandler` after inserting tasks for this sidecar).

**2. Stub clients.** All in `Infrastructure/Sidecars/Stubs/`:
   - `StubVlmClient.cs` implements `IVlmClient` (interface in `Infrastructure/Sidecars/IVlmClient.cs`). `Task<string> DescribeImageAsync(string storageKey, CancellationToken ct)` returns `$"[stub VLM description for {storageKey}; model=stub-v1]"` after `Task.Delay(50, ct)`.
   - `StubDoclingClient.cs` implements `IDoclingClient`. `Task<string> ExtractMarkdownAsync(string storageKey, string mimeType, CancellationToken ct)` returns `$"# [stub docling extraction]\n\nFile: {storageKey}\n"`.
   - `StubParakeetClient.cs` implements `IParakeetClient`. `Task<string> TranscribeAsync(string storageKey, CancellationToken ct)` returns `"[stub parakeet transcription]"`.
   - `StubUrlFetcherClient.cs` implements `IUrlFetcherClient`. `Task<string> FetchMarkdownAsync(string url, CancellationToken ct)` returns `$"# [stub URL extraction]\n\nURL: {url}\n"` (the existing `UrlExtractor` from CLOUD-002 stays; this is the stub-shaped wrapper for parity, OR delete the existing extractor and have the worker call directly. **Choice:** keep `UrlExtractor` in-place; `StubUrlFetcherClient` is for tests only, and the `UrlFetcherWorker` defaults to the real `UrlExtractor` via DI swap. Pass-B production wiring uses `UrlExtractor`; tests can override.)
   - `StubVideoSplitterClient.cs` implements `IVideoSplitterClient`. `Task<VideoSplitResult> SplitAsync(...)` returns `new VideoSplitResult(KeyframeStorageKeys: [], AudioStorageKey: null)` — the video kind ends up extraction-skipped end-to-end until handoff #6 lands real ffmpeg.
   
   Each interface lives at `Infrastructure/Sidecars/I<Name>Client.cs`; each stub at `Infrastructure/Sidecars/Stubs/Stub<Name>Client.cs`. Real client implementations (`OllamaVlmClient`, `DoclingHttpClient`, `ParakeetHttpClient`, `RealUrlFetcherClient`, `FfmpegVideoSplitterClient`) arrive in handoffs #4–#6 as drop-in DI replacements.

**3. Remove the Pass-A shortcut in `ExtractingAttachmentsHandler`.** Now the handler INSERTs `extraction_tasks` rows and **returns** without transitioning the job — the orchestrator's outer loop re-claims the job on the next wake (`extraction_tasks_changed` notify) and re-enters `ExtractingAttachmentsHandler`, which checks "are all extraction_tasks for this job terminal?":
   - If yes, transition `ingest_jobs.status='composing'`.
   - If no, return; orchestrator will re-claim again on next wake.
   
   The check is a single SELECT:
   ```sql
   SELECT bool_and(status IN ('succeeded','failed','skipped'))
     FROM extraction_tasks WHERE ingest_job_id = {jobId};
   ```
   Empty result (no extraction_tasks because the note has no binary attachments) returns NULL; treat as "all done".

   To avoid a busy-loop when sub-tasks are slow, the handler also issues a short-circuit return without renewing the lease aggressively — the orchestrator's claim loop will re-pick the job either on the next `extraction_tasks_changed` notify or after the idle poll (1 min safety net). The lease stays held by the orchestrator; the heartbeat keeps `lease_expires_at` fresh; the orchestrator releases by exiting `DispatchAsync`. Specifically: the handler does NOT release the lease on the "still waiting" return; it just returns from `DispatchAsync` and the orchestrator loops; the next claim re-acquires the same job via the `OR (status = 'extracting_attachments' AND lease_expires_at <= {now})` clause **only after the lease expires**, which is the wrong semantics for "wake immediately on notify."
   
   **Correct pattern:** the handler returns a sentinel ("waiting") that the dispatcher interprets to release the lease immediately (set `lease_owner=NULL`, `lease_expires_at=NULL`, keep `status='extracting_attachments'`). The wakeup channel fires the next claim. This adds one DB roundtrip per "still waiting" check but it's bounded by the number of attachments + a small constant.

**4. `IIngestEventBus` + `PostgresIngestEventBus`.**
   ```csharp
   // src/ThanyMarcus.Cloud.Api/Features/Sync/IIngestEventBus.cs
   public interface IIngestEventBus
   {
       Task PublishNotePhaseChangedAsync(Guid noteId, Guid jobId, string from, string to, CancellationToken ct);
       Task PublishAttachmentStatusChangedAsync(Guid noteId, Guid attachmentId, string from, string to, CancellationToken ct);
       Task PublishNoteSucceededAsync(Guid noteId, CancellationToken ct);
       Task PublishNoteFailedAsync(Guid noteId, string error, CancellationToken ct);
       Task PublishHubMaterializedAsync(Guid noteId, Guid entityId, CancellationToken ct);
   }
   ```
   `PostgresIngestEventBus` writes the event into the relevant `events_log` (jobs or attachments) **and** fires `pg_notify('ingest_events', <payload>)`. Both writes in a single transaction (the caller's DbContext transaction).

   The bus is scoped (one per request / one per dispatcher invocation); it gets a `CloudDbContext` via DI. Wire the orchestrator, dispatcher, and all five specialist workers to invoke the bus instead of doing the `events_log ||=` write inline.

**5. `GET /api/sync/events` SSE endpoint.** Add at `Features/Sync/SyncEventsEndpoint.cs`:
   ```csharp
   app.MapGet("/api/sync/events", HandleAsync)
       .AddEndpointFilter<RequirePluginAuthFilter>()
       .WithName("GetSyncEvents");
   ```
   Handler (~80 LOC, copy `CloudEventsEndpoints.MapCloudEventsEndpoints` from `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Events/CloudEventsEndpoints.cs:17-101` and adapt):
   - Set headers (`text/event-stream`, `no-cache`, `X-Accel-Buffering: no`). Flush.
   - Open an `NpgsqlConnection` (factory pattern; reuse `NpgsqlConnectionFactory` from Portal.Api or land a cloud-side equivalent).
   - `LISTEN ingest_events;`
   - On `Notification`: parse payload → write `event: <name>\ndata: <json>\n\n`.
   - Heartbeat: `: heartbeat\n\n` every 30s.
   - No initial state snapshot beyond a `connected` comment — clients backfill via `/api/sync/pull`.

   Auth pattern: the existing `RequirePluginAuthFilter` reads `Authorization: Bearer`. For `EventSource` query-string fallback, the filter must **also** accept `?access_token=<>`; extend the filter to check both sources OR add a new `RequirePluginAuthFilterFromQuery` for this endpoint only. **Choice:** extend the existing filter (one-liner) to also accept `access_token` query-string; preserves a single auth code path. Document in code (one-line "non-obvious-why" comment) that this is for `EventSource` compatibility.

**6. `POST /api/notes/{id}/reprocess` endpoint.** Add at `Features/Ingest/ReprocessEndpoint.cs`:
   ```csharp
   app.MapPost("/api/notes/{noteId:guid}/reprocess", HandleAsync)
       .AddEndpointFilter<RequirePluginAuthFilter>()
       .WithName("PostNoteReprocess");
   ```
   Handler:
   - Load `notes.id == noteId AND deleted_at IS NULL`; 404 if missing or tombstoned.
   - Check `ix_ingest_jobs_active_per_note`: if an in-flight `ingest_jobs` row already exists for this note (status NOT IN terminals), return 409.
   - INSERT `ingest_jobs (id, note_id, kind='reprocess', status='queued', scheduled_at=now, ...)`.
   - `pg_notify('ingest_jobs_new', '')`.
   - Return `202 { jobId, status: 'queued' }`.

   The orchestrator handles `kind='reprocess'` identically to `kind='capture'` in Pass A/B — the only difference is the **cache hit rate** in `ExtractingAttachmentsHandler` (same `sha256` + same `extraction_cache_key` → cached `extracted_text` → no specialist work). Model-version bumps invalidate the cache; the worker pool runs.

**7. `DELETE /api/notes/{id}` endpoint** at `Features/Ingest/NoteDeleteEndpoint.cs`:
   ```csharp
   app.MapDelete("/api/notes/{noteId:guid}", HandleAsync)
       .AddEndpointFilter<RequirePluginAuthFilter>()
       .WithName("DeleteNote");
   ```
   Handler:
   - `UPDATE notes SET deleted_at = now(), transition_version = transition_version + 1, updated_at = now() WHERE id = {noteId} AND deleted_at IS NULL`.
   - If row-count is 0, return 404.
   - `pg_notify('ingest_jobs_changed', '<noteId>')` so any active orchestrator dispatcher wakes and short-circuits on its next phase boundary.
   - Return `204 No Content`.
   - **Best-effort cancellation:** in-flight `extraction_tasks` complete normally (write rows that go unread); the orchestrator's next phase-boundary cancellation check transitions the job to `dead_lettered`. The user-visible note is gone (`note.status='failed'`, `deleted_at IS NOT NULL`).

**8. `CancelHandler` in `Features/Processing/Phases/`.** Reached by `IngestPhaseDispatcher` when `notes.deleted_at IS NOT NULL` at any phase boundary:
   - UPDATE `ingest_jobs SET status='dead_lettered', last_error='user_cancelled', finished_at=now()`.
   - UPDATE `notes SET status='failed', updated_at=now()` (the `deleted_at` is already set by the DELETE endpoint).
   - Fire `IIngestEventBus.PublishNoteFailedAsync(noteId, 'user_cancelled')`.

**9. `IngestSseTranslator`** at `Features/Sync/IngestSseTranslator.cs`. Parses an `ingest_events` notify payload into a typed `IngestSseEvent` record, then serializes. The closed event vocabulary (per ADR-0042 §5):
   - `note_phase_changed { noteId, jobId, from, to }`
   - `attachment_status_changed { noteId, attachmentId, from, to }`
   - `note_succeeded { noteId }`
   - `note_failed { noteId, error }`
   - `hub_materialized { noteId, entityId }` (this ticket emits stubs from `ExtractingEntitiesHandler` only when LLM provides a hub-triggering mention; in Pass B with `NoOpLlmClient`, this is never fired. Reserved for the M5 worker tickets.)

   Wire the translator into the SSE endpoint; on each notify, translator translates → handler writes SSE.

**10. Tests for Pass B:**
   - `Specialists/VlmWorkerTests.cs` (and one per specialist) — claim contention (two VlmWorker instances, one extraction_task, exactly one wins; both consult the same `target_sidecar='ollama'` queue); lease expiry → re-claim → attempts increments; client throws → retry → success; persistent failure → `failed` terminal.
   - `Specialists/SpecialistWorkerBaseTests.cs` — verifies the abstract base's claim loop in isolation against an arbitrary `target_sidecar`.
   - `Sync/SyncEventsEndpointTests.cs` — open SSE connection; orchestrator fires a phase change; client receives `event: note_phase_changed\ndata: {...}\n\n`. Mock `IIngestEventBus` OR use real `pg_notify` with Testcontainers (the latter is the more realistic test; pick it).
   - `Ingest/ReprocessEndpointTests.cs` — happy path; 404 on missing note; 404 on tombstoned note; 409 on active job; cache-hit path (insert pre-existing `extracted_text` row with matching `sha256` + `extraction_cache_key` → reprocess inserts `extraction_tasks` with status=`skipped` and `extracted_text` populated).
   - `Ingest/NoteDeleteEndpointTests.cs` — happy path → `204`, `deleted_at IS NOT NULL`, orchestrator's next dispatcher loop short-circuits to `dead_lettered`; 404 on missing; idempotent (second DELETE on already-deleted returns 404).
   - `Phases/CancelHandlerTests.cs` — invoking the handler directly transitions the job correctly.
   - `Sync/IngestSseTranslatorTests.cs` — every event kind round-trips through (write payload → notify → translator → SSE event) with the expected JSON shape.

→ At end of Pass B: a finalize → orchestrator splits attachments into `extraction_tasks` → five specialist workers race to claim → each calls `StubXxxClient` → returns canned text in ~50ms each → orchestrator advances through phases → SSE endpoint streams `note_phase_changed → note_succeeded` in real time → `notes.status='ready'`. Total wall-clock ~500ms for a 3-attachment composite (5 specialist workers in parallel + ~5 phase transitions of 30ms each). Real model wiring is purely an implementation swap in handoffs #4–#6.

### Pass C — provenance event log materialization + integration regression anchor (~1 day)

The previous passes wrote `events_log` directly; this pass materializes `notes.provenance` from it at terminal-time, exposes it via `/api/sync/pull`, and lands the end-to-end regression test.

**1. Provenance materialization.**
   - After `EmbeddingHandler` transitions to `succeeded`, OR after `CancelHandler` transitions to `dead_lettered`, OR after any failure terminal, a final step writes `notes.provenance` from the job's accumulated state.
   - Code lives in `Features/Processing/ProvenanceMaterializer.cs`:
     ```csharp
     public static JsonDocument Build(IngestJob job, Note note, IReadOnlyList<Attachment> atts, ...) =>
         JsonDocument.Parse(JsonSerializer.Serialize(new {
             job_id              = job.Id,
             kind                = job.Kind,
             llm_mode            = note.LlmMode,
             llm_model           = /*from llm_factory*/,
             generated_at        = job.FinishedAt,
             total_ms            = (job.FinishedAt - job.StartedAt)?.TotalMilliseconds,
             phase_events        = job.EventsLog,                 // raw event log
             extraction_failures = atts.Where(a => a.ExtractionStatus == "failed").Select(...).ToList(),
             extraction_summary  = atts.GroupBy(a => a.Kind).Select(g => new { kind = g.Key, total = g.Count(), extracted = g.Count(a => a.ExtractionStatus == "extracted"), skipped = g.Count(a => a.ExtractionStatus == "skipped"), failed = g.Count(a => a.ExtractionStatus == "failed") }).ToList(),
             cache_hits          = /*from extraction_tasks.events_log on skipped-cache-hit*/,
         }));
     ```
   - The CLOUD-002 `CompositeIngestHandler.BuildProvenance` is **deleted** in this ticket (line `Features/Processing/CompositeIngestHandler.cs:140-161`); the new materializer fully replaces it. Field names align with §22 of the cloud-pivot plan for forward compatibility with CLOUD-024.

**2. `/api/sync/pull` extension.** Add an optional `?include=provenance` query parameter (default `false`) that includes `notes.provenance` in the response. Plugin reads provenance to surface "View processing details" in a context menu. Existing default response shape stays unchanged (plugins not requesting `?include=provenance` see no diff).

**3. Cross-ticket regression test** `tests/ThanyMarcus.Cloud.Tests/Features/CompositeIngestSagaEndToEndTests.cs`:
   - Spin up `CloudApiFactory` against `pgvector/pgvector:pg16`.
   - Register stub clients for all five sidecars + `NoOpLlmClient` + `StubEmbeddingClient` (returns zero-vector).
   - Issue `/api/ingest/init` with 4 attachments: 1 URL, 1 image, 1 voice, 1 PDF file.
   - Issue presigned PUTs to MinIO (Testcontainers, already in CLOUD-002 test fixtures).
   - Issue `/api/ingest/{noteId}/finalize`.
   - Open SSE on `/api/sync/events` BEFORE finalize.
   - Assert SSE event sequence: `note_phase_changed(queued→extracting_attachments)` → `attachment_status_changed × 4 (pending→extracted)` → `note_phase_changed(extracting_attachments→composing)` → `note_phase_changed(composing→routing)` → `note_phase_changed(routing→extracting_entities)` → `note_phase_changed(extracting_entities→embedding)` → `note_phase_changed(embedding→succeeded)` → `note_succeeded`.
   - Issue `/api/sync/pull?since=...&include=provenance`.
   - Assert: 1 item, `relativePath='Inbox/{noteId}.md'` (no LLM → no routing), `body` contains all four stub-text snippets, `attachments[]` carries presigned download URLs, `provenance.extraction_summary` shows `total=4, extracted=4`.
   - Issue `POST /api/notes/{noteId}/reprocess`.
   - Assert: second saga run completes in <100ms because all four attachments cache-hit (same `sha256` + same `extraction_cache_key`).
   - Issue `DELETE /api/notes/{noteId}`.
   - Issue a third `/reprocess` → assert 404 (note is tombstoned).
   - Pull `/api/sync/pull?since=...&include=provenance` again → second-and-third events show the deletion via tombstone semantics (status=`failed`, `deleted_at` set).

   This is the **regression anchor** for the ingest-saga series. Future tickets (specialist-worker swap, embedding swap, LLM swap) must keep this test green.

**4. Documentation update.** A short paragraph in `docs/architecture.md` (or land an inline pointer to `plans/cloud-ingest-saga-foundation-handoff.md`) describing the saga's runtime topology. **One paragraph max** — the canonical doc is ADR-0042; this is a "where to find it" pointer.

**5. ADRs to author** (0.25-day batch after the ticket ships, NOT in this ticket's scope):
   - **ADR-0048: In-process ingest pipeline runtime topology.** Captures the "single deployable" decision (orchestrator + 5 specialists + SSE in one process), the `SpecialistWorkerBase<TClient>` pattern, and why no separate worker binary.
   - **ADR-0049: Cloud-side SSE channel auth.** Documents the `?access_token=<>` query-string fallback for `EventSource`, the security trade (logged token), and the rotation path.

→ At end of Pass C: provenance is comprehensive, the regression anchor catches future breakages, and handoffs #4–#6 can begin with confidence that the framework is solid.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   ├── 0042-cloud-ingest-pipeline-architecture.md         # (read-only) contract
│   ├── 0019-background-work-and-saga-durability.md        # (read-only) lease patterns
│   ├── 0020-server-push-sse.md                            # (read-only) SSE patterns
│   └── 0033-provisioning-saga-and-worker.md               # (read-only) blueprint
├── plans/
│   └── cloud-ingest-saga-foundation-handoff.md            # THIS FILE
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                         # CHANGED: + register JobOrchestratorWorker + 5 specialists + IIngestEventBus + IngestSseTranslator + 5 stub clients + IEmbeddingClient stub + MapSyncEventsEndpoint + MapReprocessEndpoint + MapNoteDeleteEndpoint; - register IngestSagaWorker
│   ├── appsettings.json                                   # CHANGED: + IngestSaga:Orchestrator:* + IngestSaga:Specialists:*:Replicas + IngestSaga:Phases:*:MaxAttempts + IngestSaga:ExtractionTasks:MaxAttempts + IngestSaga:Models:Stub:Version
│   ├── Features/
│   │   ├── Processing/
│   │   │   ├── IngestSagaWorker.cs                        # DELETED (replaced by JobOrchestratorWorker)
│   │   │   ├── CompositeIngestHandler.cs                  # DELETED (replaced by phase handlers)
│   │   │   ├── JobOrchestratorWorker.cs                   # NEW: orchestrator loop, claim, heartbeat, LISTEN/NOTIFY
│   │   │   ├── IngestPhaseDispatcher.cs                   # NEW: per-phase handler dispatch + cancellation check + retry/dead-letter
│   │   │   ├── ProvenanceMaterializer.cs                  # NEW: final notes.provenance JSONB assembly
│   │   │   ├── IngestJob.cs                               # CHANGED: + IngestJobKind constants; IngestJobStatus extended with phase + failure-terminals
│   │   │   ├── IngestJobConfiguration.cs                  # CHANGED: handled by schema-v2 migration; verify
│   │   │   ├── Phases/
│   │   │   │   ├── IPhaseHandler.cs                       # NEW
│   │   │   │   ├── ExtractingAttachmentsHandler.cs        # NEW: split into extraction_tasks; wait-for-terminal; cache-hit path
│   │   │   │   ├── ComposingHandler.cs                    # NEW: calls existing CompositeMarkdownAssembler
│   │   │   │   ├── RoutingHandler.cs                      # NEW: calls ILlmClient (NoOpLlmClient in safe mode); writes project_id
│   │   │   │   ├── ExtractingEntitiesHandler.cs           # NEW: calls ILlmClient; inserts mentions
│   │   │   │   ├── EmbeddingHandler.cs                    # NEW: calls IEmbeddingClient; writes notes.embedding
│   │   │   │   └── CancelHandler.cs                       # NEW: deleted_at short-circuit
│   │   │   └── Specialists/
│   │   │       ├── SpecialistWorkerBase.cs                # NEW: abstract claim-loop/heartbeat parameterized by TClient + TargetSidecar
│   │   │       ├── VlmWorker.cs                           # NEW
│   │   │       ├── DoclingWorker.cs                       # NEW
│   │   │       ├── ParakeetWorker.cs                      # NEW
│   │   │       ├── UrlFetcherWorker.cs                    # NEW
│   │   │       └── VideoSplitterWorker.cs                 # NEW
│   │   ├── Ingest/
│   │   │   ├── IngestEndpoints.cs                         # UNCHANGED (init + finalize stay)
│   │   │   ├── ReprocessEndpoint.cs                       # NEW: POST /api/notes/{noteId}/reprocess
│   │   │   └── NoteDeleteEndpoint.cs                      # NEW: DELETE /api/notes/{noteId}
│   │   ├── Sync/
│   │   │   ├── SyncPullEndpoint.cs                        # CHANGED: + ?include=provenance handling; rest unchanged
│   │   │   ├── SyncEventsEndpoint.cs                      # NEW: GET /api/sync/events SSE
│   │   │   ├── IIngestEventBus.cs                         # NEW
│   │   │   ├── PostgresIngestEventBus.cs                  # NEW: mirrors PostgresProvisioningEventBus
│   │   │   ├── IngestSseTranslator.cs                     # NEW: notify-payload → typed SseEvent → serialized
│   │   │   └── IngestSseEvent.cs                          # NEW: closed event vocabulary record types
│   │   └── PluginAuth/
│   │       └── RequirePluginAuthFilter.cs                 # CHANGED: + accept access_token query-string (for EventSource)
│   └── Infrastructure/
│       ├── Database/
│       │   ├── CloudDbContext.cs                          # UNCHANGED (schema-v2 already wired)
│       │   └── Migrations/0002_drop_legacy_processing.cs  # NEW (small): drop 'processing' from ck_ingest_jobs_status
│       └── Sidecars/                                      # NEW directory
│           ├── IVlmClient.cs
│           ├── IDoclingClient.cs
│           ├── IParakeetClient.cs
│           ├── IUrlFetcherClient.cs
│           ├── IVideoSplitterClient.cs
│           ├── IEmbeddingClient.cs
│           ├── VideoSplitResult.cs
│           └── Stubs/
│               ├── StubVlmClient.cs
│               ├── StubDoclingClient.cs
│               ├── StubParakeetClient.cs
│               ├── StubUrlFetcherClient.cs
│               ├── StubVideoSplitterClient.cs
│               └── StubEmbeddingClient.cs                  # returns float[256] of zeros
├── src/ThanyMarcus.Shared/
│   └── Saga/
│       └── SagaOwnershipLostException.cs                  # NEW (lifted from Portal.SagaWorker)
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Features/
    │   ├── Processing/
    │   │   ├── JobOrchestratorWorkerTests.cs              # NEW
    │   │   ├── PhaseDispatcherTests.cs                    # NEW
    │   │   ├── RetryBudgetTests.cs                        # NEW
    │   │   ├── Phases/
    │   │   │   ├── ExtractingAttachmentsHandlerTests.cs   # NEW
    │   │   │   ├── ComposingHandlerTests.cs               # NEW
    │   │   │   ├── RoutingHandlerTests.cs                 # NEW
    │   │   │   ├── ExtractingEntitiesHandlerTests.cs      # NEW
    │   │   │   ├── EmbeddingHandlerTests.cs               # NEW
    │   │   │   └── CancelHandlerTests.cs                  # NEW
    │   │   └── Specialists/
    │   │       ├── SpecialistWorkerBaseTests.cs           # NEW
    │   │       └── VlmWorkerTests.cs                      # NEW (one shape for all 5; others assert sidecar-name binding only)
    │   ├── Sync/
    │   │   ├── SyncEventsEndpointTests.cs                 # NEW
    │   │   ├── IngestSseTranslatorTests.cs                # NEW
    │   │   └── PostgresIngestEventBusTests.cs             # NEW
    │   ├── Ingest/
    │   │   ├── ReprocessEndpointTests.cs                  # NEW
    │   │   └── NoteDeleteEndpointTests.cs                 # NEW
    │   ├── CompositeIngestEndToEndTests.cs                # CHANGED: assertions updated for phase machine + SSE sequence (existing CLOUD-002 test)
    │   └── CompositeIngestSagaEndToEndTests.cs            # NEW: the full regression anchor (Pass C)
    └── Infrastructure/
        └── Sidecars/Stubs/
            └── (stub-client tests for each, asserting canned-output stability)
```

## State machine reference

### `ingest_jobs` (driven by `JobOrchestratorWorker`)

```
                       finalize | /reprocess | /hub-regen
                                │
                                ▼
                            [queued]
                                │ orchestrator claim
                                ▼
              [extracting_attachments] ────────────────┐
                                │ all extraction_tasks │ deleted_at set at any boundary
                                │ terminal             ▼
                                ▼                  [dead_lettered]
                          [composing] ────────────────►   (terminal)
                                │
                                ▼
                          [routing] ──────────►  [failed_route]       (after retry budget)
                                │
                                ▼
                  [extracting_entities] ───►  [failed_entities]
                                │
                                ▼
                          [embedding] ────►  [failed_embedding]
                                │
                                ▼
                          [succeeded]              (terminal; note.status=ready)

  Per-phase failure terminals also reachable from each phase if budget exhausted:
    extracting_attachments → failed_extraction
    composing              → failed_composition
    routing                → failed_route
    extracting_entities    → failed_entities
    embedding              → failed_embedding
    any                    → dead_lettered (cancellation)
```

Retry budget (from `appsettings.json:IngestSaga:Phases:<phase>:MaxAttempts`):

| Phase | MaxAttempts | Backoff base (s) | After exhaustion |
|---|---|---|---|
| `extracting_attachments` | 1 | — | `failed_extraction` |
| `composing` | 2 | 2 | `failed_composition` |
| `routing` | 3 | 2 | `failed_route` |
| `extracting_entities` | 3 | 2 | `failed_entities` |
| `embedding` | 3 | 2 | `failed_embedding` |

`extracting_attachments` has MaxAttempts=1 because the actual work happens in `extraction_tasks` (each of which has its own MaxAttempts=3); retrying the orchestrator's split-into-tasks step doesn't help.

### `extraction_tasks` (driven by specialist workers)

```
                    orchestrator INSERT (kind-routed target_sidecar)
                              │
                              ▼
                         [queued]
                              │ specialist worker claim (SKIP LOCKED)
                              ▼
                       [processing]
                              │
            ┌─────────────────┼──────────────────┬───────────────┐
            ▼                 ▼                  ▼               ▼
       [succeeded]        [failed]           [skipped]    (retry → queued)
       (client returned   (attempts          (cache hit
        OK; extracted_     >= MaxAttempts)    or extractor
        text written)                         skipped)
```

Cache-hit path: `ExtractingAttachmentsHandler` writes `status='skipped'` AND `extracted_text=<cached>` in the same INSERT (no specialist work runs).

### `notes.status` (projection from `ingest_jobs.status`)

| `ingest_jobs.status` | `notes.status` | `notes.deleted_at` |
|---|---|---|
| (no job rows) | `pending` | null |
| `queued`, `extracting_attachments`, `composing`, `routing`, `extracting_entities`, `embedding` | `processing` | null |
| `succeeded` | `ready` | null |
| any failure terminal except `dead_lettered` | `failed` | null |
| `dead_lettered` from cancellation | `failed` | NOT NULL |
| `dead_lettered` from retry exhaustion | `failed` | null |

The plugin sees `note.status` only; it computes "was this cancelled vs failed-on-its-own?" from `deleted_at`.

### SSE event vocabulary (channel `ingest_events`)

```
note_phase_changed         { noteId, jobId, from, to }
attachment_status_changed  { noteId, attachmentId, from, to }
note_succeeded             { noteId }
note_failed                { noteId, error }
hub_materialized           { noteId, entityId }
```

Fired by `IIngestEventBus`; serialized by `IngestSseTranslator`; written to SSE response by `SyncEventsEndpoint`.

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors. `dotnet test` green; the CLOUD-002 `CompositeIngestEndToEndTests.cs` (updated to assert phase-machine output) continues to pass.
2. ✅ `IngestSagaWorker.cs` and `CompositeIngestHandler.cs` deleted; no compile-time references remain. `IngestJobStatus.Processing` constant deleted; the schema migration `0002_drop_legacy_processing.cs` applies cleanly.
3. ✅ `JobOrchestratorWorker` registered as `IHostedService`; orchestrator pool of 3 concurrent slots verified by `JobOrchestratorWorkerTests.cs` claim-contention test.
4. ✅ Five specialist workers (`VlmWorker`, `DoclingWorker`, `ParakeetWorker`, `UrlFetcherWorker`, `VideoSplitterWorker`) registered with default replica counts from ADR-0042 §1; each verified in `SpecialistWorkerBaseTests.cs` to claim only its own `target_sidecar` rows.
5. ✅ All six new sidecar interfaces (`IVlmClient`, `IDoclingClient`, `IParakeetClient`, `IUrlFetcherClient`, `IVideoSplitterClient`, `IEmbeddingClient`) exist with stub implementations registered in DI; swapping any stub for a real impl is a one-line `Program.cs` change.
6. ✅ `IIngestEventBus` writes `events_log` AND fires `pg_notify('ingest_events', payload)` in a single transaction.
7. ✅ `GET /api/sync/events` returns `Content-Type: text/event-stream`, sends `: heartbeat\n\n` every 30s, streams `event: <kind>\ndata: <json>\n\n` on every notify; verified end-to-end against real `pg_notify` in `SyncEventsEndpointTests.cs`.
8. ✅ `POST /api/notes/{noteId}/reprocess` returns 202 with new `jobId` on happy path; 404 on missing/tombstoned note; 409 on concurrent active job; cache-hit reprocess completes in <100ms with zero specialist calls (verified via `extraction_tasks.status='skipped'` rows).
9. ✅ `DELETE /api/notes/{noteId}` returns 204; sets `notes.deleted_at`; in-flight orchestrator transitions to `dead_lettered` at next phase boundary; SSE emits `note_failed { error: 'user_cancelled' }`.
10. ✅ `notes.provenance` populated from `ProvenanceMaterializer.Build(...)` at every terminal; includes `extraction_summary`, `phase_events`, `total_ms`, `cache_hits`.
11. ✅ `/api/sync/pull?include=provenance=true` returns the provenance JSONB inline; default `include=false` matches CLOUD-002 response shape byte-for-byte.
12. ✅ `CompositeIngestSagaEndToEndTests.cs` (the Pass-C regression anchor) is green: 4-attachment composite → SSE event sequence → /sync/pull with provenance → reprocess (cache hit) → DELETE → second reprocess returns 404.
13. ✅ Manual smoke against a freshly-provisioned cloud:
    ```bash
    # 1. Init + upload + finalize (existing CLOUD-002 flow, unchanged)
    INIT=$(curl -sS -X POST https://<cloud>.thany.click/api/ingest/init \
      -H "Authorization: Bearer $PLUGIN_TOKEN" -d '{...}')
    # ...uploads...
    curl -sS -X POST https://<cloud>.thany.click/api/ingest/<noteId>/finalize \
      -H "Authorization: Bearer $PLUGIN_TOKEN" -d '{...}'
    # → 202 { noteId, status:"processing" }

    # 2. Stream events
    curl -N "https://<cloud>.thany.click/api/sync/events?access_token=$PLUGIN_TOKEN"
    # → event: note_phase_changed
    #   data: {"noteId":"...","jobId":"...","from":"queued","to":"extracting_attachments"}
    #   ...
    #   event: note_succeeded
    #   data: {"noteId":"..."}

    # 3. Reprocess (cache hit)
    curl -sS -X POST https://<cloud>.thany.click/api/notes/<noteId>/reprocess \
      -H "Authorization: Bearer $PLUGIN_TOKEN"
    # → 202 { jobId, status: "queued" }

    # 4. Delete
    curl -sS -X DELETE https://<cloud>.thany.click/api/notes/<noteId> \
      -H "Authorization: Bearer $PLUGIN_TOKEN"
    # → 204
    ```
14. ✅ No `// TODO` markers in shipped code paths. `// FORK:` markers explicitly named at: each `Stub<Name>Client` (forks to handoffs #4–#6), the `?access_token=` query-string auth (forks to a hardening pass if EventSource gains header support), the `StubEmbeddingClient` returning zeros (forks to CLOUD-EMBEDDING).
15. ✅ `dotnet ef migrations script` shows the `0002_drop_legacy_processing.cs` migration drops `'processing'` from the check constraint and no other schema changes are touched.

## Out of scope (named explicitly)

1. ❌ **Real sidecar HTTP clients.** `OllamaVlmClient`, `DoclingHttpClient`, `ParakeetHttpClient`, `RealUrlFetcherClient` adaptor, `FfmpegVideoSplitterClient` all land in handoffs #4–#6. This ticket only ships stubs.
2. ❌ **Real Granite Embedding ONNX integration.** `StubEmbeddingClient` returns `float[256]` of zeros. Real ONNX wiring lands in CLOUD-EMBEDDING (handoff #5).
3. ❌ **Real LLM routing + entity-extraction prompts.** `NoOpLlmClient` is the unconditional Pass-A/B/C LLM. Real prompts land alongside `OllamaVlmClient` (handoff #4 or #5; the LLM ticket may piggy-back on the VLM ticket if MiniCPM-V serves both, per ADR-0043 §"Single model for VLM + routing").
4. ❌ **Hub regen.** `kind='hub_regen'` rows are accepted by the orchestrator and routed through the same phase machine, but the trigger logic (entity crosses mention threshold) is implemented when `ExtractingEntitiesHandler` runs real entity dedup — that's the M5 entity ticket (handoff #5 or later). For this ticket, `hub_materialized` SSE events are reserved vocabulary but never fired.
5. ❌ **Mid-phase cancellation.** Cancellation is checked at phase boundaries only, per ADR-0042 §"Negative / accepted costs". A 45s ASR call cannot be aborted mid-stream.
6. ❌ **Plugin TS types for new endpoints.** The plugin doesn't exist yet (PLUGIN-001+). Hand-written `ThanyMarcus.Shared/Plugin/*Dto.cs` lands when the plugin needs it; for now the endpoints respond to manual `curl` + the C# test client only.
7. ❌ **Portal-side wiring of `/api/sync/events`.** The plugin (not the portal) is the SSE consumer. Portal has its own `/api/clouds/{id}/events`; the two channels are siblings, not chained.
8. ❌ **Per-attachment progress in SSE.** The closed event vocabulary includes `attachment_status_changed`; emitting it on every extraction_task transition is in scope. Per-chunk progress within a single extraction (e.g., Parakeet streaming chunks) is NOT — that requires Parakeet streaming support which is deferred future-work per ADR-0042 §"Deferred future-work hooks" item 3.
9. ❌ **Pgvector queries (kNN retrieval).** The `embedding` column is populated; the helpers that read it land in CLOUD-010 per ADR-0045's roadmap.
10. ❌ **OTel spans per phase.** ADR-0042 §"Operational" calls for spans; wiring them is a small follow-up. Acceptable to land this ticket without span-level instrumentation if budget runs tight; counters (queue depth, phase latency histograms) are NOT in scope here.
11. ❌ **Burst-worker GPU tier.** Per ADR-0042 §"Deferred future-work hooks" item 1. The `target_sidecar='ollama-gpu'` slot is reserved but not implemented.
12. ❌ **Cross-job sub-task pool optimization.** Per ADR-0042 §"Deferred future-work hooks" item 5. Each orchestrator polls its own sub-tasks.
13. ❌ **Priority field on `extraction_tasks`.** Per ADR-0042 §"Deferred future-work hooks" item 6. All tasks `ORDER BY scheduled_at`.
14. ❌ **OpenAI LLM client** (the `OpenAiLlmClient` stub from CLOUD-002 stays a `NotImplementedException`). External-API LLM wiring lands with the unsafe-mode tickets.

## Risks and gotchas

- **The `extracting_attachments` "wait for terminal sub-tasks" loop is the highest-risk piece.** If the orchestrator holds its lease while polling, it monopolizes one of 3 concurrent slots for the full duration of the slowest sub-task — a 45s ASR call ties up 33% of orchestrator capacity. The "release lease, listen for notify, re-claim" pattern fixes this but adds a DB roundtrip per check. **Recommended:** implement the release-and-re-claim pattern from the start, even if it adds a few ms; the alternative scales badly. The Pass-B section's §3 description names this explicitly.
- **`pg_notify` payload size limit (~8KB).** Notifies carry small payloads (job ids, status strings). If you stuff full event JSON into the notify payload, large `events_log` reads can exceed the limit and Postgres silently truncates. **Recommended:** notify payload = just `<note_id>` (or `<job_id>`); the listener does a second SELECT to fetch the full event row. Trade extra query for safety.
- **`LISTEN` connection management.** Each SSE connection holds an open `NpgsqlConnection` in LISTEN mode. With 50 concurrent plugin connections, that's 50 long-held connections — Postgres's default `max_connections=100` is tight. **Recommended:** use a dedicated connection pool for LISTEN connections (`NpgsqlConnectionFactory` with a separate pool key) and bump `max_connections` to 200 in the cloud's compose Postgres env (it's a tiny RAM cost).
- **`access_token` query-string leaks in logs.** nginx and the cloud-api request log will record the full URL including the token. Caddy → nginx swap (already done in `cloud-001-amendment-nginx-handoff.md`) puts the access log on the cloud's host disk; an operator with shell access can read it. **Mitigation:** add a nginx log-format directive that strips `access_token=...` from logged URLs (`map $request_uri $clean_uri { default $request_uri; "~^(.*)access_token=[^&]*(.*)$" "$1access_token=REDACTED$2"; }` then `log_format ... $clean_uri`). Same for the .NET request log (`ILoggerFilter` that scrubs the query string). This is a small follow-up; document the deviation if shipping without it.
- **Concurrent orchestrator + specialist worker writes to the same `attachments` row.** When `ExtractingAttachmentsHandler` re-enters to check "all terminal?" and a specialist worker is mid-UPDATE writing `extracted_text`, optimistic concurrency on `transition_version` will reject one of them. **Resolution:** the orchestrator only READS `extraction_tasks.status` (not `attachments`) for the terminal-check; specialist workers UPDATE `attachments` and `extraction_tasks` in their own transaction. No concurrent writes on the same row.
- **`pgvector` HNSW build cost on `notes.embedding` UPDATE.** Inserting a new embedding into an HNSW index is O(log n). At thesis scale this is negligible; at 100k+ notes it adds a few ms per insert. Document expectation; not a blocker.
- **The shared `SagaOwnershipLostException` namespace move (`ThanyMarcus.Portal.SagaWorker.Features.Provisioning` → `ThanyMarcus.Shared.Saga`).** Portal.SagaWorker has two references to this class; updating them is mechanical but easy to miss under warnings-as-errors. Run the portal test suite after the move to confirm.
- **SSE keep-alive on idle connection.** Without the 30s `: heartbeat\n\n`, nginx's default `proxy_read_timeout=60s` (and Caddy's similar default if any cloud is still on Caddy) will close idle SSE connections. The portal-side `RunHeartbeatAsync` in `CloudEventsEndpoints` is the reference; do not skip it.
- **`extraction_tasks` cache-hit path is subtle.** When a reprocess finds a cache hit, it should INSERT `extraction_tasks` with `status='skipped'` + `extracted_text` populated AND increment some "cache hits" counter. The mistake to avoid: silently skipping the INSERT (no `extraction_tasks` row). That breaks the orchestrator's terminal-check (returns "no tasks" → "all terminal" → advance) but ALSO loses observability for the cache hit. **Always insert the row** so eval can measure cache-hit rates.
- **`StubEmbeddingClient` returning zero vector.** Zero vectors put every note at distance 0 from every other note in cosine space → kNN returns arbitrary order. Tests that later check "kNN of zero-embedding note returns the right neighbors" will fail when real Granite ONNX lands. Acceptable for this ticket because no test checks kNN output yet; flag the assumption in `StubEmbeddingClient.cs` with a one-line non-obvious-why comment.
- **The five specialist worker classes share ~90% of their code via `SpecialistWorkerBase<TClient>`.** Resist the temptation to also share per-attachment dispatch — each worker dispatches differently based on its `Attachment.Kind` / `MimeType` / `extra` shape. The base owns claim-loop only; subclasses own `ExtractAsync`.
- **`Pgvector.Vector` JSON serialization.** When serializing a `Note` (with `Vector? Embedding`) through `System.Text.Json`, the default serializer either crashes or produces a useless representation. The `/api/sync/pull` response shape does NOT include `embedding` today and should not in this ticket either. Verify the existing `SyncPullItem` DTO has no embedding field; if a future test serializes a `Note` end-to-end, add `[JsonIgnore]` on `Embedding` or move to a hand-written DTO mapping (`Note → SyncPullItem`) — the latter is the cleaner path long-term.
- **EF Core 10 + pgvector + raw SQL UPDATEs in claim loops.** The `FromSqlInterpolated` calls in `JobOrchestratorWorker.ClaimNextAsync` need to project to the full `IngestJob` shape including the new schema-v2 columns (`kind`, `events_log`, `transition_version`). EF maps these correctly if the entity is up-to-date; verify by running a single `FromSqlInterpolated` test against a seeded job and asserting all columns hydrate.
- **Specialist worker pool starvation under load.** With `VlmWorker=1`, a single image-heavy capture queue can backlog while five orchestrators sit idle. This is the **intended** per-sidecar concurrency cap; the queue depth (`extraction_tasks` rows in `queued` status with `target_sidecar='ollama'`) is the observable signal. Don't try to fix it by bumping `Replicas` unless eval data shows the sidecar can serve more than one in parallel — Ollama serializes internally, so two `VlmWorker` instances thrash on the same model.
- **Test fixture latency.** End-to-end tests spin up `pgvector/pgvector:pg16` + MinIO + the full cloud-api host. Each test takes 3–5s of fixture setup; the regression-anchor test takes 10–15s. Acceptable for the regression anchor; if the suite-total grows past ~2 min, mark slow tests `[Trait("Category","Slow")]` and gate behind a CI label.
- **The `IngestSaga:Specialists:*:Replicas` config knob is per-worker-type, not per-instance.** The default DI registration calls `services.AddHostedService<VlmWorker>()` once; to honor `Replicas=2`, the registration helper would need to loop and register N instances. Implement as:
  ```csharp
  void RegisterSpecialist<TWorker>(IServiceCollection s, IConfiguration cfg, string key)
      where TWorker : class, IHostedService
  {
      int n = cfg.GetValue($"IngestSaga:Specialists:{key}:Replicas", 1);
      for (var i = 0; i < n; i++) s.AddHostedService<TWorker>();
  }
  ```
  Each instance gets its own GUID-suffixed worker-id naturally.

## Open contract decisions (carry forward)

1. **`access_token` query-string SSE auth.** Per design-decision #14 — header-on-EventSource (with polyfill) vs query-string (with logging risk). Locked to query-string in MVP; revisit if the plugin's polyfill cost is cheap.
2. **Provenance schema canonical form.** The "full §22 provenance schema" lands in CLOUD-024; this ticket's `ProvenanceMaterializer` writes a forward-compatible subset. Field renames between this ticket and CLOUD-024 are acceptable (no consumer yet).
3. **OTel span instrumentation.** Phase-boundary spans + per-specialist-worker spans are the right shape; deferred to a 0.5d follow-up. Track in the M5 demo prep if eval needs them.
4. **`max_connections` bump in Postgres compose.** Default 100 → 200. The right place to land this is in the schema-v2 ticket's Postgres image bump OR here. Land here as part of `docker-compose.yml` env (`POSTGRES_INITDB_ARGS=-c max_connections=200`) to keep the bump scoped to the LISTEN-connection cost driver.
5. **Hub regen trigger logic location.** When `extracting_entities` detects an entity crossing the mention threshold, it INSERTs a `hub_regen` job. The threshold value, the "user-created entity auto-creates hub" rule (per F9 of the data-model grilling), and the `is_hub` flag flip all live in `ExtractingEntitiesHandler`. This ticket's no-op LLM means the trigger never fires; verifying it fires correctly is the entity-dedup ticket's problem.

## What handoffs #4–#6 inherit

After this ticket lands:
- **Handoff #4** (`CLOUD-VLM-WORKER` + `CLOUD-LLM-INTELLIGENCE`) swaps `StubVlmClient` for `OllamaVlmClient` (calls `http://ollama:11434/api/generate` against MiniCPM-V) and `NoOpLlmClient` for the real LLM client; one DI line change each. No saga changes, no schema changes, no endpoint changes. The regression anchor in `CompositeIngestSagaEndToEndTests.cs` continues to pass with stub assertions; new test files exercise the real models.
- **Handoff #5** swaps `StubDoclingClient → DoclingHttpClient` and `StubParakeetClient → ParakeetHttpClient`; same shape. Also wires real `IEmbeddingClient` against in-process Granite Embedding 278m ONNX.
- **Handoff #6** swaps `StubVideoSplitterClient → FfmpegVideoSplitterClient` (uses local ffmpeg to split video into keyframes + audio, then spawns child `extraction_tasks` for each). May also wire real `RealUrlFetcherClient` if Pass-B chose to keep `UrlExtractor` independent.

The cumulative arc is:

```
CLOUD-SCHEMA-V2 (handoff #1: migration only)
  ─► CLOUD-SIDECARS (handoff #2: ollama+docling+parakeet running, models pre-pulled)
  ─► CLOUD-INGEST-SAGA-FOUNDATION (THIS TICKET — orchestrator + 5 specialists + SSE + reprocess + delete-as-cancel + provenance, with stubs)
  ─► CLOUD-VLM-WORKER + CLOUD-LLM-INTELLIGENCE (handoff #4 — stub→real for ollama + LLM)
  ─► CLOUD-DOCLING-WORKER + CLOUD-PARAKEET-WORKER + CLOUD-EMBEDDING (handoff #5 — stub→real for docling + parakeet + Granite)
  ─► CLOUD-VIDEO-SPLITTER + CLOUD-URL-FETCHER-REAL (handoff #6 — stub→real for ffmpeg + URL)
  ─► [M5 demo: composite capture with URL + image + voice + PDF + video → vault with real extracted content]
  ─► CLOUD-PROJECTS + CLOUD-ENTITY-DEDUP + CLOUD-HUB-REGEN (the LLM-driven knowledge graph)
  ─► [M6 demo: emergent graph from accumulated captures, end-to-end thesis-defensible CPU-only pipeline]
```

This ticket is the **framework beachhead** that makes the M5 worker swaps purely about HTTP-client implementations — no saga, no SSE, no schema, no auth, no provenance plumbing to redesign.

## Why a separate handoff (and not folded into the first specialist-worker ticket)

The five specialist-worker tickets (`CLOUD-VLM-WORKER`, `CLOUD-DOCLING-WORKER`, `CLOUD-PARAKEET-WORKER`, `CLOUD-EMBEDDING`, `CLOUD-VIDEO-SPLITTER` / `CLOUD-URL-FETCHER`) are each ~0.5–1d of C# adapter work once the framework exists. Folding the framework into the first of them would (a) balloon that ticket to ~4d and conflate framework design with model integration, (b) leave the other four specialist tickets blocked on the first one's framework decisions, (c) make the smoke verification ambiguous (is the test exercising the worker code or the saga foundation?). Shipping CLOUD-INGEST-SAGA-FOUNDATION first means all five M5 swap tickets can land in parallel against a stable, tested foundation. This ticket's ~3d pays off framework design once and amortizes over five sibling deliveries.

## Cross-references

- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — the contract; this ticket implements §§1–6.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — SKIP LOCKED + lease pattern.
- **`docs/decisions/0020-server-push-sse.md`** — SSE shape, keep-alive cadence.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — portal-side blueprint; code references at `src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs` + `Features/Provisioning/SagaPhaseDispatcher.cs` + `Features/Provisioning/SagaOwnershipLostException.cs`.
- **`docs/decisions/0036-wizard-progress-transport.md`** — SSE translator pattern reused.
- **`docs/decisions/0045-composite-note-schema.md`** — schema-v2 columns this ticket reads/writes.
- **`plans/cloud-002-handoff.md`** — baseline saga + ingest endpoints; the `MapIngestEndpoints` + `MapSyncPullEndpoint` shapes stay byte-for-byte.
- **`plans/cloud-schema-v2-handoff.md`** — schema this ticket consumes; the `0002_drop_legacy_processing.cs` migration ships here as the final cleanup.
- **`plans/cloud-sidecars-handoff.md`** — sidecars this ticket's future siblings will call; `appsettings.json:IngestSaga:Sidecars:*` keys are consumed by stub clients (BaseUrl/HealthPath are ignored; MaxConcurrency is consumed via the worker `Replicas` config).
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note (preserved).
- **Memory `portal_architecture.md`** — Postgres job queue + mutable status + SSE only (applied symmetrically on the cloud side).
- **Future ADRs** (not in this ticket's scope):
  - **ADR-0048: In-process ingest pipeline runtime topology.**
  - **ADR-0049: Cloud-side SSE channel auth (`?access_token=` query-string).**

## Definition of done

```
$ dotnet build
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName!~CompositeIngestSagaEndToEndTests"
Passed!  - Failed: 0, Passed: N, Skipped: 0

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName~CompositeIngestSagaEndToEndTests"
Passed!  - Failed: 0, Passed: 1, Skipped: 0   # the regression anchor

$ grep -rn "IngestSagaWorker\|CompositeIngestHandler" src tests
(no results)                                  # CLOUD-002 saga code fully removed

$ grep -rn "I\(Vlm\|Docling\|Parakeet\|UrlFetcher\|VideoSplitter\|Embedding\)Client" src/ThanyMarcus.Cloud.Api
src/ThanyMarcus.Cloud.Api/Infrastructure/Sidecars/IVlmClient.cs:...
src/ThanyMarcus.Cloud.Api/Infrastructure/Sidecars/Stubs/StubVlmClient.cs:...
...                                           # six interfaces, six stubs, no real impls yet
```

A fresh agent picking up CLOUD-VLM-WORKER (handoff #4) from this state knows:
- `IVlmClient` exists. They write `OllamaVlmClient : IVlmClient` that calls `http://ollama:11434/api/generate`.
- DI swap is a single line in `Program.cs`: `AddSingleton<IVlmClient, OllamaVlmClient>()` replaces the stub.
- The saga, SSE, retry, lease, provenance, reprocess, cancel — all already work. The handoff is just "make the model call real."
- The regression anchor in `CompositeIngestSagaEndToEndTests.cs` continues to pass against the stub; their new tests exercise the real Ollama call alongside.

A fresh agent picking up the entity-dedup / hub-regen ticket from this state knows:
- `ExtractingEntitiesHandler` exists with stubbed dedup. They land real dedup logic (pgvector kNN against `entities.embedding`, mention-threshold check) inside the existing handler — no saga rewiring needed.
- `kind='hub_regen'` and `hub_materialized` SSE events are already plumbed.
