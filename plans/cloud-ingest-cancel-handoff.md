# CLOUD-INGEST-CANCEL — Cancel in-flight note ingest and restore the plugin draft

**Goal:** add `POST /api/ingest/{noteId}/cancel` so a user can abort a saga that is mid-flight (typical vision + synth round-trip is 3–6 min on a 4 vCPU droplet) and have the plugin composer restored to its pre-submit state. Estimated **1–1.5 person-days** with AI-agent assistance.

## Why this exists

Composite ingest (one composite draft → one processed note, per [[composite_ingest_decision]]) is a long-running operation. Today there is **no in-flight cancel**:

- `DELETE /api/notes/{id}` (`NoteDeleteEndpoint.cs`) tombstones the note row after the fact, but does not stop the saga, does not abort the ollama call, does not delete the attachment objects from Spaces, and the plugin composer has already cleared its draft so the user cannot edit-and-resubmit.
- `ingest_jobs.status` has no `cancelled` terminal state. The phase-runner (`JobPhaseRunner.cs`) keeps walking the FSM until `succeeded` or `failed_*` regardless of any "abort" signal.
- The plugin (`composer/Composer.ts`) discards body + attachment local-refs as soon as `POST /api/ingest` returns 202. There is nothing to restore from.

Observed cost: a single bad submission (wrong preset chosen, draft incomplete, attachment swapped) burns 1.5–4 minutes of CPU on the droplet and produces a note the user immediately deletes — they then have to retype the body and re-pick the attachments from scratch.

This ticket lands the missing API + plugin wiring.

## Scope

Cancel = "I want this in-flight ingest to stop, and I want my draft back." It is NOT the same as delete (`NoteDeleteEndpoint`), which says "the note finished processing and I now want it gone."

**In scope:**
- New endpoint `POST /api/ingest/{noteId}/cancel`
- New `cancelled` value in the `ingest_jobs.status` enum + terminal-state predicate in the SKIP-LOCKED claim query
- Cooperative cancellation at phase boundaries: every saga handler reads `ingest_jobs.status` before transitioning and bails if it is `cancelled`
- Spaces + DB cleanup: delete attachment objects, delete `attachments` rows, delete `notes` row (so the user can resubmit with the same `client_note_id` without uniqueness conflicts)
- Plugin: keep the composer draft snapshot (body text + attachment metadata + attachment local-file paths in the vault) from submit until the queue confirms `status = ready`
- Plugin: "Cancel" button on each `processing` row in `QueueSidebar`
- Plugin: on cancel success, restore composer from the cached draft

**Out of scope:**
- Mid-phase abort by interrupting an in-flight ollama HTTP call (phase-boundary cancel is sufficient for MVP — the user-visible delay is bounded by the longest single phase, currently vision at ~1.5 min)
- "Edit and resubmit" inline (this ticket is just "cancel and put the draft back in the composer"; editing the restored draft and resubmitting is a follow-up)
- Per-attachment cancel (we only cancel the whole note; granular per-attachment retry already exists)
- Refund of partial work (entities extracted from a cancelled note are discarded; we do not try to salvage them)

## State machine semantics

```
Eligible cancel sources (non-terminal ingest_jobs statuses):
  queued                  → cancelled
  extracting_attachments  → cancelled    (after the currently-running specialist returns)
  composing               → cancelled
  routing                 → cancelled
  extracting_entities     → cancelled
  synthesizing            → cancelled
  embedding               → cancelled

Ineligible (returns 409 Conflict with a reason):
  succeeded               → 409, use DELETE /api/notes/{id} instead
  failed_extraction       → 409, terminal failure; use DELETE
  failed_composition      → 409, ditto
  failed_route            → 409
  failed_entities         → 409
  failed_synthesis        → 409
  failed_embedding        → 409
  dead_lettered           → 409, operator intervention
  cancelled               → 409, idempotent (or 410 Gone — pick one)
```

**Rule of thumb:** cancel is valid for any ingest_job that has not reached `succeeded` AND is not already in a terminal failure state. The cancel endpoint:
1. `UPDATE ingest_jobs SET status='cancelled', finished_at=now(), updated_at=now() WHERE id=$1 AND status NOT IN (terminal_states)`
2. If no rows updated: 409 (job is already terminal)
3. Otherwise: enqueues a `cleanup` job (or does cleanup inline if synchronous is fine) — deletes attachment Spaces objects, deletes DB rows, publishes `note.cancelled` on SSE so the plugin can react

The reason for synchronous cleanup vs a separate cleanup-saga: ingest cleanup is short (a handful of S3 DELETE + DB DELETE; no terraform). Doing it inline keeps the API contract simple — when the cancel response returns 204, the plugin knows it can immediately restore the draft and resubmit if desired without race-conditioning on a leftover note row.

The reason cancel does NOT abort the in-flight ollama call: ollama generate calls are not cleanly cancellable through HTTP today (the process keeps running on the model server until it produces the response). Phase-boundary cancel is the cheap version and reduces user-visible latency from "full saga remaining" to "current phase remaining (≤ ~1.5 min)".

## Concrete files

### Backend

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/IngestJobStatus.cs` — add Cancelled

```csharp
public static class IngestJobStatus
{
    public const string Queued = "queued";
    public const string ExtractingAttachments = "extracting_attachments";
    public const string Composing = "composing";
    public const string Routing = "routing";
    public const string ExtractingEntities = "extracting_entities";
    public const string Synthesizing = "synthesizing";
    public const string Embedding = "embedding";
    public const string Succeeded = "succeeded";
    public const string FailedExtraction = "failed_extraction";
    public const string FailedComposition = "failed_composition";
    public const string FailedRoute = "failed_route";
    public const string FailedEntities = "failed_entities";
    public const string FailedSynthesis = "failed_synthesis";
    public const string FailedEmbedding = "failed_embedding";
    public const string DeadLettered = "dead_lettered";
    public const string Cancelled = "cancelled"; // NEW

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, FailedExtraction, FailedComposition, FailedRoute,
        FailedEntities, FailedSynthesis, FailedEmbedding, DeadLettered,
        Cancelled, // NEW — workers must not claim it
    };
}
```

Then audit every `WHERE status NOT IN (...)` raw-SQL claim query and add `'cancelled'`.

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/CancelIngestEndpoint.cs` — NEW

```csharp
public static class CancelIngestEndpoint
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapPost("/api/ingest/{noteId:guid}/cancel", HandleAsync)
           .RequireAuthorization(CloudAuthPolicies.Bearer);

    private static async Task<IResult> HandleAsync(
        Guid noteId,
        CloudDbContext db,
        IArtifactStore store,
        IIngestEventBus bus,
        CancellationToken ct)
    {
        // 1. Find the job + note in one query.
        // 2. If job.status IN Terminal → 409 with a reason matching the current status.
        // 3. UPDATE ingest_jobs SET status='cancelled', finished_at=now() WHERE id=$1 AND status NOT IN (terminal) — capture rows-affected.
        // 4. If 0 rows: re-read, return 409 (lost the race to a worker that just transitioned).
        // 5. Read attachments for the note; DELETE each Spaces object (best-effort, log failures, do not block).
        // 6. DELETE FROM attachments WHERE note_id=$1; DELETE FROM notes WHERE id=$1.
        // 7. bus.PublishNoteCancelledAsync(noteId).
        // 8. Return 204 NoContent.
    }
}
```

Wire in `Program.cs` next to the other ingest endpoints. Match the existing auth policy (`CloudAuthPolicies.Bearer`).

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/JobPhaseRunner.cs` — bail-if-cancelled guard

At the top of the phase-transition loop, re-read `ingest_jobs.status` inside the lease transaction and short-circuit if it is `cancelled`:

```csharp
if (job.Status == IngestJobStatus.Cancelled)
{
    // Release the lease (someone else cancelled while we held it).
    // Do not advance to the next phase. Do not write a failure.
    log.LogInformation("ingest_job {JobId} cancelled mid-flight; releasing lease", job.Id);
    return PhaseOutcome.Released;
}
```

Apply the same guard at the top of each phase handler (`ExtractAttachmentsHandler`, `ComposingHandler`, `RoutingHandler`, `EntityExtractionHandler`, `SynthesizingHandler`, `EmbeddingHandler`) so an in-flight worker that already started a phase will still bail before writing its result.

Note: the worker MAY still be mid-way through an ollama call when cancel arrives. It will complete that call (wasting up to ~1.5 min of CPU), then notice the `cancelled` status on the next status check, and release the lease without writing the result. The endpoint does not wait for this — cleanup happens immediately and the note row goes away. The worker writes to a non-existent note row on completion; that UPDATE matches 0 rows and the worker exits cleanly.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/IIngestEventBus.cs` — add NoteCancelledAsync

```csharp
public Task PublishNoteCancelledAsync(Guid noteId, CancellationToken ct);
```

SSE channel: `cloud:notes:cancelled` payload `{ "noteId": "..." }`. The plugin subscribes and triggers the composer-restore flow.

#### Postgres migration

`migrations/0NNN_ingest_jobs_cancelled.sql`:

```sql
-- Status is stored as TEXT, not an enum, so no DDL change needed.
-- But the partial indexes that filter on non-terminal status DO need extending:

-- (Audit `_efmigrations_history` for any existing partial indexes; if they
-- enumerate terminal statuses explicitly, add 'cancelled' to the exclusion.)
```

Likely no schema migration is needed since `status` is stored as `text` and the terminal-state predicate is in C#, not in SQL. Verify by searching for `CHECK` constraints and partial indexes referencing `status`.

### Plugin

#### `plugin/thany-marcus/src/composer/Composer.ts` — keep draft snapshot

After `POST /api/ingest` returns 202, do **not** clear the composer state. Instead, move it to a `pendingDrafts: Map<noteId, DraftSnapshot>` keyed by the server-returned `noteId`:

```ts
interface DraftSnapshot {
    body: string;
    attachments: Array<{ clientId: string; vaultPath: string; mimeType: string }>;
    submittedAt: number;
}
```

The composer remains visually empty (so the user can start a new note), but the snapshot is held until either:
- The SSE `note.ready` event fires for `noteId` → discard snapshot.
- The SSE `note.cancelled` event fires for `noteId` → restore snapshot to composer.
- A configurable TTL elapses (default 30 min) → discard with a console warning.

#### `plugin/thany-marcus/src/api.ts` — cancel method

```ts
cancelIngest(noteId: string, signal?: AbortSignal): Promise<void>
```

POST to `/api/ingest/{noteId}/cancel`. 204 → success. 409 → throw with the body's reason field (queue UI shows a toast: "this note already finished — use delete instead").

#### `plugin/thany-marcus/src/queue/QueueSidebar.ts` — Cancel button

For each row where `note.status ∈ {queued, extracting_attachments, composing, routing, extracting_entities, synthesizing, embedding}`, render a small "Cancel" link. On click:
1. Confirm modal: "Cancel this note and restore the draft to your composer? Vision/text work already done will be discarded."
2. Call `api.cancelIngest(noteId)`.
3. On 204: the SSE event will trigger the restore; nothing else to do here.

#### `plugin/thany-marcus/src/main.ts` — SSE handler for note.cancelled

Subscribe to the `cloud:notes:cancelled` SSE channel (extend the existing SSE multiplexer). On event:
1. Look up `noteId` in `pendingDrafts`.
2. If found and composer is empty: restore body + attachment refs to composer.
3. If composer is non-empty: append snapshot to a "Recovered drafts" tray (out of scope for MVP; for now, just show a notice and discard).
4. Remove the row from the queue sidebar.

## Tests

Backend:
- `CancelIngestEndpointTests` — cancel from each non-terminal status (parameterised), assert 204 + DB cleanup + Spaces DELETE called + SSE published.
- `CancelIngestEndpointTests.Cancel_after_succeeded_returns_409`.
- `CancelIngestEndpointTests.Cancel_after_failed_returns_409`.
- `CancelIngestEndpointTests.Cancel_idempotent_returns_409_on_second_call`.
- `JobPhaseRunnerCancelTests` — start a phase, flip status to cancelled, assert worker releases lease without writing result.
- `*HandlerCancelTests` — for each phase handler, assert cancel-status check is honoured.

Plugin:
- `Composer.draft_persists_until_ready` — submit, then receive `note.ready` SSE, assert snapshot discarded.
- `Composer.cancel_restores_draft` — submit, then receive `note.cancelled`, assert composer body + attachment refs match pre-submit state.
- `Composer.cancel_with_dirty_composer_does_not_overwrite` — submit, user types into composer, cancel fires; assert composer not clobbered (snapshot goes to recovered-drafts tray or is discarded with a notice).
- `QueueSidebar.cancel_button_visibility` — assert visible iff status is non-terminal.

## Risks and edge cases

- **Race: cancel arrives while a worker just claimed the job.** The terminal-state check in the SKIP-LOCKED query plus the per-phase bail-if-cancelled guard handles this. Worst case: worker wastes one phase of CPU.
- **Race: SSE `note.ready` and `note.cancelled` arrive in the wrong order.** Order is enforced by the cancel endpoint: it deletes the note row BEFORE publishing the SSE, so any subsequent `note.ready` from a slow worker will be for a non-existent note and the plugin ignores it.
- **Attachment Spaces objects orphaned.** If the Spaces DELETE fails (network blip, permissions), log and continue. Add a daily cleanup cron (out of scope) that scans for `attachments` rows tombstoned > 24h ago.
- **Plugin draft snapshot grows unbounded.** The TTL discard prevents leaks. Cap `pendingDrafts.size` at 10; reject new submits with a clear error if at cap (extremely unlikely — a user with 10 in-flight notes is not a normal use case).
- **User cancels and immediately resubmits with the same `client_note_id`.** Cleanup deletes the row with that `client_note_id`, so the resubmit creates a fresh row. No uniqueness conflict.
- **Cancel during `embedding`.** Embedding is fast (~5s) and CPU-only ONNX. The bail-if-cancelled guard at the start of `EmbeddingHandler.HandleAsync` makes cancel a no-op race (the note will likely embed before cancel arrives). Document this in the cancel response: "this note is in the final phase; cancel may not take effect."

## Out-of-band cleanup

The existing `NoteDeleteEndpoint` (DELETE /api/notes/{id}) remains the right tool for post-`ready` cleanup. Document the split in the OpenAPI summary:

- `POST /api/ingest/{noteId}/cancel` — abort an in-flight ingest and restore the draft. 409 if the note already reached a terminal state.
- `DELETE /api/notes/{id}` — soft-delete a successfully-processed note. 409 if the ingest is still in flight (point to cancel).

## Effort breakdown

- Backend endpoint + status flip + cleanup: 3h
- Phase-runner + handler guards: 2h
- Tests: 3h
- Plugin draft cache + restore + cancel button: 4h
- Plugin tests: 2h
- Manual smoke (cancel from each phase): 1h
- ADR + handoff revision: 1h

Total: **~1.5 person-days**. MVP cut (no plugin restore, just abort + cleanup with a toast): **~0.5 person-days**.

## Decision points to confirm before starting

1. Cancel during `embedding` — refuse (409) or allow (no-op race)?
2. Spaces DELETE failure — log-and-continue or fail-the-cancel?
3. Plugin draft TTL — 30 min default OK, or tie to vault session?
4. SSE channel name — `cloud:notes:cancelled` or piggyback on existing `cloud:notes` with a status field?

Owner picks at kickoff; this ticket does not block on the answers.
