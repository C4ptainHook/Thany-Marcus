# CLOUD-SYNC-EXTENSIONS — `/api/sync/pull` projects + tombstones + `/api/sync/push` with 409 conflict — Handoff Brief

Date: 2026-05-19
Status: Draft. Handoff #9 of the ADR-0042 / ADR-0045 implementation series. **Extends the already-shipped `/api/sync/pull` (handoff #3, `cloud-ingest-saga-foundation-handoff.md`) with the two payload sections ADR-0045 §9 names but the foundation didn't ship — `projects[]` and tombstones-as-first-class — and lands the new `/api/sync/push` endpoint with optimistic-concurrency 409 semantics so the plugin can flow user edits back into `notes.body_output`.** The phase machine, lease/heartbeat/transition_version, SSE bus, reprocess/cancel endpoints, composer, LLM phases, and Granite embedding client all stay byte-for-byte; this ticket adds two endpoints' worth of read/write code over the schema and clients that #1, #6, and #8 already left in place. After this ticket: the plugin's sync engine pulls a `since`-cursored page that includes ready notes, soft-deleted tombstones, and the user's project list in a single round-trip; the plugin's file watcher pushes `## User Notes` edits and vault-side deletions back via `POST /api/sync/push`; concurrent edits from a second device that already wrote past the pushing device's `baseUpdatedAt` get rejected with 409 and a `currentUpdatedAt` hint so the plugin can re-pull, three-way merge, and retry.

**Goal:** (1) extend `SyncPullEndpoint.HandleAsync` so the response carries (a) tombstones — rows with `deleted_at IS NOT NULL AND updated_at > since`, surfaced with `deleted: true`, an empty `body`, empty `attachments`, and the `relative_path` the plugin needs to find the vault file to move into `.trash/` — and (b) `projects[]` — every `entities` row with `kind='project' AND updated_at > since` (including soft-deleted ones), carrying `entity_id`, `canonical_name`, `aliases`, `description`, `vault_folder`, `is_user_source`, `updated_at`, `deleted_at?` — and broaden the cursor query from `status='ready'` to the ADR-0045 §9 form `(status='ready' OR deleted_at IS NOT NULL)` so tombstones surface even when the saga never reached `ready`. The `nextSince` cursor advances to `MAX(notes.updated_at, entities.updated_at)` across both pages so a single cursor drives both streams. (2) Add `POST /api/sync/push` mapped under `Features/Sync/SyncPushEndpoint.cs`: payload `{noteId, body, baseUpdatedAt, deleted}` per ADR-0045 §9; the handler does a SQL-level optimistic update — `UPDATE notes SET body_output=$body, deleted_at=$deleted ? now() : null, updated_at=now(), transition_version=transition_version+1, body_hash=null WHERE id=$noteId AND updated_at=$base` — returns 200 with the new `updated_at` + `transition_version` on success, 409 with `{currentUpdatedAt, currentTransitionVersion}` on staleness, 404 if the note doesn't exist, 401 if the bearer token is missing or invalid. Embedding is recomputed by enqueuing an `IngestJobKind.UserEditEmbed` job (new kind) that runs the existing `EmbeddingHandler` directly — handoff #8's `body_hash`-changed gate trips automatically because the push handler nulls `body_hash`, so the next embed cycle re-embeds the user-edited body and the cosine top-K stays consistent with the vault. The push handler does **not** trigger routing, entity extraction, or hub regen — ADR-0045 §9 locks "User edits become the new `body_output` verbatim — NO automatic re-processing." After this ticket: the regression anchor in `tests/ThanyMarcus.Cloud.Tests/Features/CompositeIngestSagaEndToEndTests.cs` gains a new lane that pulls a note, mutates its `## User Notes` block, pushes it back, asserts 200 + bumped `transition_version`, pulls again, and asserts the new body and a re-embedded vector (non-null, non-zero, distinct cosine from the original); a sibling test pushes with a stale baseline and asserts 409.

Estimated **1.5–2 person-days** with AI-agent assistance, split into two passes. Pass A (~0.75d) extends `/api/sync/pull` — broadens the cursor query, adds the tombstone shape, adds the `projects[]` section, extends `SyncPullResponse` in `ThanyMarcus.Shared.PluginApi`, mirrors fields into `contracts/plugin-api.ts`, lands the regression-anchor extension that asserts tombstones surface and projects[] carries created/updated/deleted user-source projects. Pass B (~0.75–1.25d) adds `POST /api/sync/push` — endpoint + handler + the optimistic UPDATE + 409 / 404 / 401 paths + the `UserEditEmbed` job enqueue + the matching `EmbeddingHandler` `LastComposeTemplate` plumbing for user-edited bodies (which never went through `ComposingHandler`, so the template tag is `"user-edit-v1"` not `"compose-v1"`) + the regression-anchor extension that round-trips an edit and asserts 200 → re-embed → distinct cosine. Compresses to ~1.25d if the existing `JobOrchestratorWorker` can dispatch the new job kind without phase-machine changes (it can — `UserEditEmbed` starts at `IngestJobStatus.Embedding` and terminates at `IngestJobStatus.Succeeded`, no new phases). Stretches to ~2d if 409 round-trip precision turns out to be lossy enough that exact-equality comparison fails and we need a microsecond-tolerance clamp; mitigation in §"Design decisions" #6 below.

This handoff **does not** ship: (1) the hub-edit-diff push path from cloud-pivot plan §19 (push of `## System Output > Generated Views` regions of entity hubs sent as diffs, triggering B4c user-edit-aware regen on next mention — that requires a new LLM prompt and prompt-input shape, and is `cloud-pivot-plan-2026-05-13.md` future work — push for hub notes in this handoff is allowed but treats the body as opaque, same as artifact notes); (2) the admin projects endpoint surface (`POST /api/admin/projects`, `PATCH /api/admin/projects/{id}`, `DELETE /api/admin/projects/{id}` per ADR-0045 §10 — those land as part of the portal admin proxy in a future handoff because they touch the cloud-admin-token auth filter, not the plugin bearer-token filter, and split nicely along the auth seam); (3) the `Re-seed from cloud` plugin command's matching `GET /api/reseed` endpoint (cloud-pivot plan §9 "for new-device install or recovery" — that's a paginated full-snapshot endpoint with different semantics; deferred to a `cloud-reseed-handoff.md` ticket); (4) bulk push (only single-note push per call — batch is post-thesis); (5) provenance push (the plugin never pushes provenance; provenance is cloud-canonical and only flows down).

## Where decisions live (read before doing anything)

- **`docs/decisions/0045-composite-note-schema.md`** — the contract. §9 "Sync model — cursor, tombstones, push" is the ADR locking the exact wire shape this handoff implements: the cursor SQL `WHERE updated_at > $since AND (status = 'ready' OR deleted_at IS NOT NULL)`, the tombstone semantics (plugin moves the vault file to `.trash/`), the push payload `{noteId, body, baseUpdatedAt, deleted}`, and the 200/409 response codes. §10 names the endpoint surface that includes `/api/sync/pull` (extended) and `/api/sync/push` (new). §12 "Soft-delete coverage" §"notes" and §"entities" rows specify the deletion contract — notes surface as tombstones in `sync_pull`; entities surface in the `projects[]` section of `sync_pull`. **Do not change the wire shape ADR-0045 §9 locks** — if the implementation surfaces an ambiguity (e.g., what field name the entity surfaces under in the `projects[]` array), pick the name that minimizes plugin parsing and document the choice here in §"Design decisions"; do not file an ADR amendment for cosmetic wire-shape choices.

- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §3 phase machine. The `embedding` phase already exists; the new `UserEditEmbed` job kind enters the phase machine directly at `IngestJobStatus.Embedding` (skipping `composing → routing → extracting_entities`) and exits at `IngestJobStatus.Succeeded`. §10c provenance schema: the materializer's `events_log` already includes `embedding_emit` per handoff #8; the user-edit lane adds one `events_log` row with `stage="user_edit_pushed"` for audit (`{actor: "plugin", base_updated_at, new_updated_at, body_bytes}`) — same `LlmEventAppender` writer, just a new stage tag.

- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — the soft-delete convention. `notes.deleted_at` is the tombstone column for notes; `entities.deleted_at` is the tombstone column for entities. **A soft-deleted row remains in the table forever** for audit / future restore; sync_pull surfaces it once (when `updated_at > since`) and the plugin caches the tombstone. Subsequent pulls with `since > deleted_at` will not re-surface the row — that's correct, the plugin already moved the file. Mirroring the row to the plugin on every pull would bloat the response without value.

- **`docs/decisions/0028-schema-conventions.md`** + **`docs/decisions/0024-dbcontext-shape.md`** — snake_case via `EFCore.NamingConventions`, NodaTime `Instant`, single `CloudDbContext`. The push handler uses the same `IClock` injection pattern as `NoteDeleteEndpoint.cs` to source `now()`; the optimistic UPDATE uses `ExecuteSqlInterpolatedAsync` for the same atomic-WHERE-clause discipline `NoteDeleteEndpoint` already follows.

- **`docs/decisions/0020-server-push-sse.md`** — the SSE channel. Push **does** fire a `note_updated` SSE event (existing event vocabulary; same payload shape as the saga's phase-transition events) so the same user's other devices subscribed to `/api/sync/events` see the edit propagate without polling. The `JobOrchestratorWorker.ChangedChannel` `pg_notify` already exists; the push handler invokes it the same way `NoteDeleteEndpoint.NotifyAsync` does (`SyncPushEndpoint.cs:Notify` is a literal copy of `NoteDeleteEndpoint.cs:NotifyAsync`).

- **`docs/decisions/0025-openapi-and-hand-written-ts-contracts.md`** — hand-written TS types in `contracts/plugin-api.ts`. The wire-shape extensions in `SyncPullResponse` (new `projects[]`, new `deleted` flag on items) and the new `SyncPushRequest` / `SyncPushResponse` / `SyncPushConflict` types are added to **both** `src/ThanyMarcus.Shared/PluginApi/SyncPull.cs` + a new `src/ThanyMarcus.Shared/PluginApi/SyncPush.cs` **and** `contracts/plugin-api.ts` in the same commit. Cross-file drift is a review-blocker per ADR-0025.

- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers + Respawn. The extension tests run in the existing `CompositeIngestSagaEndToEndTests.cs` collection (same Postgres fixture). Pass A's new test lane: `SyncPullSurfacesTombstones`, `SyncPullSurfacesProjects`. Pass B's: `SyncPushApplies200`, `SyncPushReturns409OnStaleBaseline`, `SyncPushReembedsBody`.

- **`plans/cloud-schema-v2-handoff.md`** — handoff #1. The `notes.deleted_at` + `entities.deleted_at` + `notes.project_id FK` + `entities.kind` + `entities.vault_folder` columns + the partial index `ix_notes_updated_at WHERE deleted_at IS NULL OR status='ready'` are all in place. The partial index does NOT cover the ADR-0045 §9 cursor query for tombstones with `status != 'ready'` (e.g., a note that was cancelled mid-`composing` and tombstoned before reaching `ready`). For thesis scale (≲ 1k notes per user), a sequential scan is fine; for post-MVP we may want a second partial index `ix_notes_tombstones_updated_at ON notes(updated_at) WHERE deleted_at IS NOT NULL` — out of scope for this ticket, flag in §"Open questions".

- **`plans/cloud-compose-phase-handoff.md`** — handoff #6 locks `notes.body_output` as the canonical body, `compose-v1` as the template version baked into frontmatter, and the `## User Notes` / `## System Output` two-heading skeleton with `UserNotesPreserver` regex-preserving the User Notes block on reprocess. **The push handler does not invoke `UserNotesPreserver` or `CompositeNoteComposer`** — the plugin pushes the full body verbatim (including its own edits to `## User Notes`) and the cloud writes it verbatim. The User Notes preservation rule is a *reprocess-side* invariant, not a *push-side* invariant. The next reprocess after a push (manual `/api/notes/{id}/reprocess`) will see the pushed body in `notes.body_output`, parse User Notes from it via the regex, and round-trip them through the composer normally — no special case needed in the composer.

- **`plans/cloud-llm-intelligence-handoff.md`** — handoff #7. LLM phases (`routing`, `extracting_entities`, `hub_generation`) are skipped by the `UserEditEmbed` job kind. **The push handler does not delete `notes.project_id`, `mentions` rows, or `entities.embedding`** — the user's edit doesn't invalidate the routing / entity decisions the LLM made on the original body. Reprocess is the explicit lever for re-running LLM phases; push is for the body-text-only path.

- **`plans/cloud-embedding-handoff.md`** — handoff #8. The `notes.body_hash` column + `EmbeddingHandler`'s body-hash-changed gate are in place. The push handler nulls `body_hash` (`SET body_hash=NULL` in the same UPDATE that writes `body_output`) so the next embed run's gate equality check fails and the embed proceeds. The `EmbeddingHandler`'s embed input is `template + "\n" + body_output`; for user-edit-lane jobs, the template prefix is `"user-edit-v1"` (defined alongside `compose-v1` in `ComposingHandler` constants). The pushed body never went through the composer, so `IngestJob.LastComposeTemplate` is null on `UserEditEmbed` jobs — the handler treats null as `"user-edit-v1"` to keep hash inputs distinct from compose-v1-vintage embeds.

- **`plans/cloud-pivot-plan-2026-05-13.md`** §9 ("Plugin file watcher + sync") and §19 ("Sync semantics"). The cloud-pivot plan specifies four sync write paths (User Notes edits, hub Generated Views edits, deletions, drain offline queue); this ticket implements three of them (User Notes via body-replace; deletions via `deleted:true`; offline-queue drain via plugin retry against the same endpoint). Hub-edit diffs (the fourth path) are deferred per §"Out of scope" #1 above.

- **`plans/cloud-ingest-saga-foundation-handoff.md`** — handoff #3. The `JobOrchestratorWorker` dispatches jobs by `IngestJobKind` + current `Status`. The new `IngestJobKind.UserEditEmbed` enters at `Status=Embedding`; the dispatcher's switch (`IngestPhaseDispatcher.cs:DispatchAsync`) already routes `Status=Embedding` to `EmbeddingHandler` regardless of `Kind`. **No dispatcher change needed.** The only new code path is the enqueue from `SyncPushEndpoint`: `INSERT INTO ingest_jobs (id, note_id, kind, status, ...) VALUES (..., 'user_edit_embed', 'embedding', ...)`. The orchestrator's claim loop picks it up on its next tick.

- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. Push respects this: one push per note per call.

- **Memory `feedback_bearer_never_in_urls.md`** — Authorization header only. The push endpoint uses `[AddEndpointFilter<RequirePluginAuthFilter>]` identical to `SyncPullEndpoint` and `NoteDeleteEndpoint`; the `baseUpdatedAt` cursor is in the JSON body, not a query param. **Do not** add a query-param fallback for `baseUpdatedAt` even as a convenience — keep the contract clean and JSON-body-only.

- **Memory `portal_tooling.md`** — warnings-as-errors, OpenAPI + Scalar, Shouldly + xUnit v3. The new endpoints get `.Produces<T>` / `.ProducesProblem` annotations matching the existing pattern in `SyncPullEndpoint.cs:19–21`.

**Do not litigate the ADR-0045 §9 wire shape.** If the implementation surfaces an ambiguity — e.g., should tombstones share the `items[]` array with ready notes, or get their own `tombstones[]` section? — pick the option that minimizes plugin parsing AND minimizes API surface area, and lock it here in §"Design decisions" below. Filing an ADR-0045 amendment is reserved for things that *change the contract* (e.g., adding a new endpoint that wasn't in §10) — wire-shape choices within ADR-0045's stated contract belong in this handoff.

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **Tombstones share the `items[]` array with ready notes; distinguished by `deleted: true` (boolean, defaults false).** ADR-0045 §9 says "Plugin sees rows with `deleted_at != null` and moves the vault file to `.trash/`" — it does not say tombstones live in a separate array. Keeping them in `items[]` lets the plugin's iteration loop do one pass: `for item in items: if item.deleted: trash(item.relativePath); else: write(item.relativePath, item.body)`. A separate `tombstones[]` array would force two loops + duplicate cursor handling. **Tombstone item shape:** `{noteId, relativePath, deleted: true, deletedAt, updatedAt, body: "", attachments: []}`. `body` is empty string (not null) and `attachments` is empty array (not null) so the plugin can use the same DTO type for both cases — no nullable-field branching in TypeScript.

2. **`projects[]` is a top-level field on `SyncPullResponse`, sibling to `items[]` and `nextSince`.** ADR-0045 §9 phrases it as "every sync_pull response includes a `projects[]` section" — top-level. The cursor `nextSince` advances to `MAX(MAX(notes.updated_at), MAX(entities.updated_at))` across both queries so a single cursor drives both streams. **If both queries return zero rows, `nextSince` is null** (same as the current contract). **If only one query returns rows, `nextSince` is that query's max.** Projects with `deleted_at != null` appear in `projects[]` with `deletedAt` set; the plugin removes them from its local project cache. **Project shape:** `{entityId, canonicalName, aliases, description, vaultFolder, isUserSource, updatedAt, deletedAt?}` where `isUserSource = (source == "user")` per `EntityKind` / `EntitySource` constants in `Entity.cs:25–39`.

3. **`projects[]` is paginated together with `items[]` under a single `limit`.** The default page size is 50 (same as today); `projects[]` and `items[]` each take up to `limit` rows independently. Worst case: 50 notes + 50 projects = 100 rows in one response. Project rows are tiny (~200 bytes JSON); the response stays well under any reasonable size cap. **Do not** add a separate `projectsLimit` query param — premature. The plugin's two streams catch up to the cursor naturally over a few polls if either stream is large.

4. **Notes that are both `status='ready'` AND `deleted_at IS NOT NULL` surface as tombstones (the `deleted` flag wins).** The cursor query returns them; the handler checks `note.DeletedAt is not null` first and emits a tombstone-shape item regardless of the status. Rationale: the plugin doesn't care about the status of a deleted note — it just trashes the file. Surfacing the ready body alongside the tombstone would waste bandwidth and let the plugin race itself (write file → trash file).

5. **The cursor query is broadened from the current `Where(n => n.Status == NoteStatus.Ready && n.UpdatedAt > sinceInstant)` to `Where(n => n.UpdatedAt > sinceInstant && (n.Status == NoteStatus.Ready || n.DeletedAt != null))` per ADR-0045 §9.** The `includeProvenance` branch is collapsed: the same query covers both branches; `includeProvenance` only gates whether `Provenance`, `Status`, and `DeletedAt` are populated on the DTO (the current code accidentally surfaces `DeletedAt` only when `includeProvenance` is set — that bug is fixed here by always populating `DeletedAt` when the note is a tombstone, and always populating `Status` when `includeProvenance` is set). **Do not preserve the bug for backwards compatibility** — no plugin code consumes the current behavior; the contract is forward-only.

6. **Optimistic concurrency uses `notes.updated_at` as the token, compared by SQL exact-equality.** ADR-0045 §9 names `baseUpdatedAt` in the wire shape — honored. The push UPDATE's WHERE clause is `WHERE id = $noteId AND updated_at = $baseUpdatedAt`; if zero rows match, the handler does a follow-up `SELECT updated_at, deleted_at FROM notes WHERE id = $noteId` to disambiguate 404 (note missing) from 409 (note exists but baseline stale). Round-trip precision: the cloud encodes `notes.updated_at` via the existing JSON converter as a `DateTimeOffset` (`SyncPullItem.UpdatedAt`) — the plugin echoes it back verbatim in `baseUpdatedAt`. NodaTime `Instant` → Postgres `timestamptz` → JSON `DateTimeOffset` round-trips at microsecond precision (Postgres native unit); the same round-trip through the plugin keeps the same string. Exact-equality holds. **If precision turns out lossy in practice** (e.g., the plugin's TypeScript `Date` truncates to milliseconds), fall back to `transition_version` as the comparison column — it's a `BIGINT`, always exact, already incremented on every saga write. The wire field stays `baseUpdatedAt` for ADR alignment but its semantic becomes "echo this value back unchanged"; the cloud compares via whatever column gives exact equality. **Default is `updated_at` exact-equality; the `transition_version` fallback is one config flip if eval shows the lossy path.**

7. **409 response body is `{currentUpdatedAt, currentTransitionVersion, code: "stale_baseline"}` — the plugin re-pulls, three-way merges (last-write-wins with `.conflict` files per cloud-pivot plan §6 T73), and retries.** The 200 response body is `{noteId, updatedAt, transitionVersion}` — the plugin updates its local cursor and proceeds. The 404 response body is `{code: "note_not_found"}`. The 401 path is owned by `RequirePluginAuthFilter` and emits its standard ProblemDetails — no custom shape.

8. **The push handler nulls `body_hash` in the same UPDATE that writes `body_output`.** The optimistic UPDATE becomes:
   ```sql
   UPDATE notes SET
     body_output = $body,
     body_hash = NULL,
     deleted_at = CASE WHEN $deleted THEN $now ELSE deleted_at END,
     updated_at = $now,
     transition_version = transition_version + 1
   WHERE id = $noteId AND updated_at = $baseUpdatedAt
   RETURNING updated_at, transition_version
   ```
   Nulling `body_hash` ensures handoff #8's gate (`note.BodyHash == newHash && note.Embedding is not null && job.Kind == IngestJobKind.Reprocess`) trips on the next embed cycle — the equality check fails on the null side, the embed runs, `body_hash` is repopulated with the new value. **Do not** compute the new `body_hash` inside the push handler (that would couple the push code path to handoff #8's hashing convention); let `EmbeddingHandler` own the hash lifecycle.

9. **A `deleted: true` push tombstones the note; the body field is ignored when `deleted` is true.** The UPDATE in that case sets `deleted_at = now()`, leaves `body_output` unchanged, and still nulls `body_hash` (defensive; a future restore that flips `deleted_at` back to null will then re-embed on the next cycle). **The push handler does NOT enqueue a `UserEditEmbed` job when `deleted: true`** — deleted notes don't need an embedding refresh. The handler's enqueue logic gates on `!req.Deleted`.

10. **`IngestJobKind.UserEditEmbed` is a new kind alongside `IngestJobKind.Reprocess` and `IngestJobKind.HubRegen`.** The phase machine entry point is `IngestJobStatus.Embedding` (skipping `Composing`, `Routing`, `ExtractingEntities`). The `JobOrchestratorWorker`'s claim query (`WHERE status IN (valid-claimable-statuses)`) already covers `Embedding`; no change to the worker. The phase handler dispatch (`IngestPhaseDispatcher.cs`) routes by status, not kind, so `EmbeddingHandler` runs unchanged. **The one tiny change** is `EmbeddingHandler.HandleAsync`'s template-tag resolution: it currently reads `job.LastComposeTemplate ?? "unknown"` (per handoff #8 §"Design decisions" #10); for `Kind == UserEditEmbed`, it reads `"user-edit-v1"` instead. One `if (job.Kind == IngestJobKind.UserEditEmbed)` branch at the top of the hash-input construction.

11. **The push handler returns 200 *before* the embedding job runs.** The job enqueue is fire-and-forget — the handler INSERTs the `ingest_jobs` row and returns 200 immediately. The `JobOrchestratorWorker` picks up the new job on its next ~1s claim tick. Embed latency is hidden behind SSE: the plugin sees a `note_phase_changed(embedding → succeeded)` event ~100ms later (Granite is ~80ms on the control plane). **Do not** block the push response on the embed — that would couple the HTTP latency to ONNX session contention and inflate p99 push latency to ~200ms unnecessarily.

12. **The push handler fires the `pg_notify('note_changed', $noteId)` exactly like `NoteDeleteEndpoint`.** The SSE translator already maps `note_changed` to a `note_updated` SSE event; no new event vocabulary. The user's other devices see the edit propagate without polling. **Do NOT** add a separate `note_user_edited` event — the SSE channel is a coarse "this note moved" signal; the plugin re-pulls to learn what moved.

13. **Hub notes (`notes.is_hub = true`) can be pushed.** The push handler does not special-case `is_hub`; the user can edit a hub note's `## System Output > Generated Views` region in Obsidian and the edit lands as `body_output`. The B4c user-edit-aware regen (cloud-pivot plan §19, fourth sync write path) is deferred — until that handoff lands, the next hub regen overwrites the user's edit. **Document this limitation in `docs/architecture.md`** as part of this PR: one paragraph naming the gap and pointing at the future handoff. The plugin can warn the user when they edit a hub note's Generated Views (UI work, not in this handoff).

14. **No bulk push.** `POST /api/sync/push` is single-note per call. The plugin's sync engine serializes pushes (one at a time per online tick) — same shape as the existing offline-capture queue drain. Bulk would let the plugin amortize HTTP overhead across many edits, but: (a) edits are rare (humans don't edit at machine speed); (b) bulk multiplexes failure modes (one 409 in a batch of 50 — what does the response look like?); (c) the queue-one-at-a-time pattern matches handoff #3's ingest queue. Premature optimization.

15. **The push handler does not check `notes.status` before writing.** A note in `status='processing'` (mid-saga) can still be pushed by the plugin — the optimistic UPDATE will succeed if `updated_at` matches. The next saga phase boundary will observe the new `body_output` and either: (a) re-render it via the composer (if the phase is `composing` — but the composer would preserve User Notes anyway); (b) re-embed it (if the phase is `embedding`); (c) finalize on it (if it was about to terminate). No data corruption — the saga's lease + `transition_version` discipline absorbs the race. **Document but do not gate.** A "note is processing, edits will be applied after" toast is a plugin-side UX choice, not a cloud-side guard.

16. **The new `UserEditEmbed` job kind enters `events_log` with `kind: "user_edit_embed"` and adds a `user_edit_pushed` event stage at enqueue time.** The provenance materializer (`ProvenanceMaterializer.cs:ExtractLlmCalls`) extended in handoff #8 to match `stage.StartsWith("embedding_")` is extended again to match `stage.StartsWith("user_edit_")` — same one-line regex change. The roll-up under `provenance.llm_calls` then includes user-edit events; eval scripts that filter by stage prefix get the audit trail for free.

17. **`SyncPushRequest.BaseUpdatedAt` is a `DateTimeOffset` (not `Instant`).** The Shared DTOs use `DateTimeOffset` per the existing `SyncPullItem.UpdatedAt` precedent (`SyncPull.cs:18`). Cloud-side, the handler converts via `Instant.FromDateTimeOffset(req.BaseUpdatedAt)` for the SQL comparison. **Don't** add a `NodaTime.Instant` JSON converter dependency to the plugin — the plugin is TypeScript and reads ISO strings; `DateTimeOffset` round-trips cleanly via `JsonSerializer`'s default ISO-8601 handling.

18. **Tests for the new endpoints live in `tests/ThanyMarcus.Cloud.Tests/Features/SyncEndpointsTests.cs` (new file).** The existing `CompositeIngestSagaEndToEndTests.cs` is the saga regression anchor; it gets two new test lanes that extend its existing setup (saga runs → assert pull surfaces tombstones; push edit → assert re-embed). The new `SyncEndpointsTests.cs` exercises the endpoint in isolation: handler logic for 200 / 409 / 404 / 401, the cursor query under various tombstone + project scenarios, the optimistic UPDATE under concurrent pushes, the SSE `pg_notify` is invoked.

19. **No new SSE event types.** The existing `note_changed` `pg_notify` channel + the `IngestSseTranslator`'s mapping to `note_updated` covers both push (body-replace) and delete (tombstone). The translator already exists per handoff #3.

20. **The push endpoint is not idempotent at the application level.** If the plugin retries a 200 (network blip), the second push's `baseUpdatedAt` won't match (the first push advanced it) and returns 409. The plugin's correct retry behavior is: re-pull, check if the body it tried to push is already present, and skip the re-push if so. **Idempotency keys are not added** — adds DB surface area for an edge case the plugin can already handle via re-pull. Premature.

## Scope boundary (precise)

Two passes. ~1.5–2 person-days total.

### Pass A — `/api/sync/pull` tombstones + `projects[]` extension (~0.75d)

**1. Extend `ThanyMarcus.Shared.PluginApi.SyncPullResponse`** (`src/ThanyMarcus.Shared/PluginApi/SyncPull.cs`):
   ```csharp
   public sealed record SyncPullResponse(
       [property: JsonPropertyName("items")]     IReadOnlyList<SyncPullItem> Items,
       [property: JsonPropertyName("projects")]  IReadOnlyList<SyncPullProject> Projects,
       [property: JsonPropertyName("nextSince")] DateTimeOffset? NextSince);

   public sealed record SyncPullProject(
       [property: JsonPropertyName("entityId")]      Guid EntityId,
       [property: JsonPropertyName("canonicalName")] string CanonicalName,
       [property: JsonPropertyName("aliases")]       IReadOnlyList<string> Aliases,
       [property: JsonPropertyName("description")]   string? Description,
       [property: JsonPropertyName("vaultFolder")]   string? VaultFolder,
       [property: JsonPropertyName("isUserSource")]  bool IsUserSource,
       [property: JsonPropertyName("updatedAt")]     DateTimeOffset UpdatedAt,
       [property: JsonPropertyName("deletedAt"),    JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? DeletedAt = null);
   ```
   Extend `SyncPullItem` with a `Deleted` flag:
   ```csharp
       [property: JsonPropertyName("deleted")] bool Deleted = false,
   ```
   The existing `DeletedAt`, `Status` fields stay; `Deleted` is set to true when `notes.deleted_at IS NOT NULL` (regardless of whether `includeProvenance` is set).

**2. Mirror to `contracts/plugin-api.ts`:** add `SyncPullProject`, add `deleted: boolean` and `relative_path` to `SyncPullItem` if not already present (the current TS contract has the wrong wire shape entirely — `artifact_id`, `draft_id`, `status`, `processed_note`; clean that up to mirror the C# shape using snake_case). Same commit.

**3. Rewrite `SyncPullEndpoint.HandleAsync`** (`src/ThanyMarcus.Cloud.Api/Features/Sync/SyncPullEndpoint.cs`):
   - Broaden the cursor query: `db.Notes.Where(n => n.UpdatedAt > sinceInstant && (n.Status == NoteStatus.Ready || n.DeletedAt != null)).OrderBy(n => n.UpdatedAt).Take(pageSize)`.
   - For each note: if `note.DeletedAt is not null`, emit a tombstone item (`body=""`, `attachments=[]`, `deleted=true`, `deletedAt`, `relativePath`, `updatedAt`). Otherwise: emit the current ready-shape item with `deleted=false`.
   - Query projects in parallel: `db.Entities.Where(e => e.Kind == EntityKind.Project && e.UpdatedAt > sinceInstant).OrderBy(e => e.UpdatedAt).Take(pageSize).ToListAsync(ct)`. Project to `SyncPullProject` DTO.
   - Compute `nextSince = items.Concat(projects).Max(x => x.UpdatedAt)`, null if both arrays are empty.
   - Return `SyncPullResponse(items, projects, nextSince)`.

**4. Test extension** (`tests/ThanyMarcus.Cloud.Tests/Features/SyncEndpointsTests.cs` — new file):
   - `SyncPull_SurfacesTombstones`: seed a note → `DELETE /api/notes/{id}` → pull with `since=epoch` → assert `items.length == 1`, `items[0].deleted == true`, `items[0].body == ""`.
   - `SyncPull_SurfacesProjects`: seed a `kind='project'` entity via direct DB insert → pull → assert `projects.length == 1`, `projects[0].canonicalName` matches.
   - `SyncPull_PaginatesCursorAcrossBothStreams`: seed 30 notes + 30 projects → pull with `limit=10` → expect `items.length == 10`, `projects.length == 10`, `nextSince` is the max across both → re-pull with `since=nextSince` → next page advances correctly.
   - `SyncPull_DeletedReadyNoteSurfacesAsTombstone`: seed a note → drive saga to `ready` → `DELETE /api/notes/{id}` → pull → assert tombstone (not ready note).

**5. Regression-anchor update** (`tests/ThanyMarcus.Cloud.Tests/Features/CompositeIngestSagaEndToEndTests.cs`):
   - Existing end-to-end test asserts pull surfaces the ready note; extend to also assert `projects.length == 0` (no project assigned by routing when threshold isn't met) and `items[0].deleted == false`.

→ At end of Pass A: a saga run → ready note → pull surfaces the note with `deleted: false` and `projects: []`; a soft-delete via `DELETE /api/notes/{id}` → next pull surfaces the same note as a tombstone with `deleted: true`; an admin-created project (out of scope for the endpoint but inserted directly into `entities` for the test) → next pull surfaces the project under `projects[]`. The plugin can now reconcile its local vault state and project cache from a single endpoint.

### Pass B — `POST /api/sync/push` + `UserEditEmbed` job kind (~0.75–1.25d)

**1. New DTOs** (`src/ThanyMarcus.Shared/PluginApi/SyncPush.cs`):
   ```csharp
   public sealed record SyncPushRequest(
       [property: JsonPropertyName("noteId")]         Guid NoteId,
       [property: JsonPropertyName("body")]           string Body,
       [property: JsonPropertyName("baseUpdatedAt")]  DateTimeOffset BaseUpdatedAt,
       [property: JsonPropertyName("deleted")]        bool Deleted = false);

   public sealed record SyncPushResponse(
       [property: JsonPropertyName("noteId")]            Guid NoteId,
       [property: JsonPropertyName("updatedAt")]         DateTimeOffset UpdatedAt,
       [property: JsonPropertyName("transitionVersion")] long TransitionVersion);

   public sealed record SyncPushConflict(
       [property: JsonPropertyName("code")]                     string Code,                     // "stale_baseline"
       [property: JsonPropertyName("currentUpdatedAt")]         DateTimeOffset CurrentUpdatedAt,
       [property: JsonPropertyName("currentTransitionVersion")] long CurrentTransitionVersion);
   ```
   Mirror to `contracts/plugin-api.ts`. Same commit.

**2. New endpoint** (`src/ThanyMarcus.Cloud.Api/Features/Sync/SyncPushEndpoint.cs`):
   ```csharp
   public static class SyncPushEndpoint
   {
       public static void MapSyncPushEndpoint(this IEndpointRouteBuilder app) =>
           app.MapPost("/api/sync/push", HandleAsync)
               .AddEndpointFilter<RequirePluginAuthFilter>()
               .WithName("PostSyncPush")
               .Produces<SyncPushResponse>(StatusCodes.Status200OK)
               .Produces<SyncPushConflict>(StatusCodes.Status409Conflict)
               .ProducesProblem(StatusCodes.Status401Unauthorized)
               .ProducesProblem(StatusCodes.Status404NotFound);

       private static async Task<IResult> HandleAsync(
           SyncPushRequest req,
           CloudDbContext db,
           IClock clock,
           CancellationToken ct)
       {
           var now = clock.GetCurrentInstant();
           var baseInstant = Instant.FromDateTimeOffset(req.BaseUpdatedAt);

           // Optimistic UPDATE with RETURNING.
           var rows = await db.Database
               .SqlQueryRaw<UpdateResult>(@"
                   UPDATE notes SET
                       body_output        = {0},
                       body_hash          = NULL,
                       deleted_at         = CASE WHEN {1} THEN {2}::timestamptz ELSE deleted_at END,
                       updated_at         = {2}::timestamptz,
                       transition_version = transition_version + 1
                     WHERE id = {3} AND updated_at = {4}::timestamptz
                   RETURNING updated_at AS UpdatedAt, transition_version AS TransitionVersion",
                   req.Body, req.Deleted, now, req.NoteId, baseInstant)
               .ToListAsync(ct);

           if (rows.Count == 0)
           {
               // Disambiguate 404 vs 409.
               var existing = await db.Notes
                   .Where(n => n.Id == req.NoteId)
                   .Select(n => new { n.UpdatedAt, n.TransitionVersion })
                   .SingleOrDefaultAsync(ct);
               if (existing is null) return Results.NotFound(new { code = "note_not_found" });
               return Results.Conflict(new SyncPushConflict(
                   Code: "stale_baseline",
                   CurrentUpdatedAt: existing.UpdatedAt.ToDateTimeOffset(),
                   CurrentTransitionVersion: existing.TransitionVersion));
           }

           var result = rows[0];

           // Enqueue re-embed unless deleted.
           if (!req.Deleted)
           {
               db.IngestJobs.Add(new IngestJob
               {
                   NoteId = req.NoteId,
                   Kind   = IngestJobKind.UserEditEmbed,
                   Status = IngestJobStatus.Embedding,
                   // ... lease, attempts, etc. per existing convention
               });
               await db.SaveChangesAsync(ct);
           }

           await NotifyAsync(db, JobOrchestratorWorker.ChangedChannel, req.NoteId.ToString(), ct);

           return Results.Ok(new SyncPushResponse(
               NoteId: req.NoteId,
               UpdatedAt: result.UpdatedAt.ToDateTimeOffset(),
               TransitionVersion: result.TransitionVersion));
       }
   }
   ```
   Match `NoteDeleteEndpoint.NotifyAsync` for the `pg_notify` invocation — copy verbatim.

**3. `IngestJobKind.UserEditEmbed` constant** (`src/ThanyMarcus.Cloud.Api/Features/Processing/IngestJob.cs`):
   ```csharp
   public static class IngestJobKind
   {
       public const string Capture       = "capture";
       public const string Reprocess     = "reprocess";
       public const string HubRegen      = "hub_regen";
       public const string UserEditEmbed = "user_edit_embed";   // NEW
   }
   ```
   Update the `ck_ingest_jobs_kind` check constraint (if it exists in `IngestJobConfiguration.cs`) to include the new kind. Add a migration `<ts>_AddUserEditEmbedJobKind.cs` that drops + recreates the check constraint:
   ```csharp
   migrationBuilder.Sql("ALTER TABLE ingest_jobs DROP CONSTRAINT IF EXISTS ck_ingest_jobs_kind;");
   migrationBuilder.Sql("ALTER TABLE ingest_jobs ADD CONSTRAINT ck_ingest_jobs_kind CHECK (kind IN ('capture','reprocess','hub_regen','user_edit_embed'));");
   ```

**4. `EmbeddingHandler` template-tag branch** (`src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/EmbeddingHandler.cs`):
   ```csharp
   var template = job.Kind switch
   {
       IngestJobKind.UserEditEmbed => "user-edit-v1",
       _                           => job.LastComposeTemplate ?? "unknown",
   };
   ```
   One-line change at the top of the hash-input construction in `HandleAsync`.

**5. Wire the new endpoint** (`src/ThanyMarcus.Cloud.Api/Program.cs`):
   ```csharp
   app.MapSyncPullEndpoint();
   app.MapSyncPushEndpoint();   // NEW
   app.MapSyncEventsEndpoint();
   ```

**6. Provenance materializer extension** (`src/ThanyMarcus.Cloud.Api/Features/Processing/ProvenanceMaterializer.cs`):
   Extend `ExtractLlmCalls` to match `stage.StartsWith("user_edit_")` in addition to `llm_` and `embedding_`. One regex / `StartsWith` predicate change.

**7. Tests** (`tests/ThanyMarcus.Cloud.Tests/Features/SyncEndpointsTests.cs`):
   - `SyncPush_AppliesBody_Returns200`: seed a note → pull → push with matching `baseUpdatedAt` → assert 200, response carries new `updatedAt` and `transitionVersion+1` → re-pull → assert `body` is the pushed value.
   - `SyncPush_StaleBaseline_Returns409`: seed a note → pull → mutate `updated_at` directly in DB to simulate concurrent write → push with old `baseUpdatedAt` → assert 409, response carries the current values.
   - `SyncPush_UnknownNote_Returns404`: push with a fabricated `noteId` → assert 404.
   - `SyncPush_MissingAuth_Returns401`: push without the bearer header → assert 401 from `RequirePluginAuthFilter`.
   - `SyncPush_DeletedTrue_TombstonesNote`: seed a note → push with `deleted: true` → assert 200, re-pull, assert tombstone item.
   - `SyncPush_EnqueuesUserEditEmbedJob`: push a body edit → assert an `ingest_jobs` row exists with `kind='user_edit_embed'`, `status='embedding'`.
   - `SyncPush_DeletedTrue_DoesNotEnqueueEmbed`: push with `deleted: true` → assert no new `ingest_jobs` row.
   - `SyncPush_Reembeds_BodyHash_Refreshed`: seed a note → drive saga to `ready` → capture `notes.body_hash` + `notes.embedding` → push a body edit → wait for the user-edit-embed job to complete (poll `notes.status` or use the SSE channel in the test) → assert `notes.body_hash` is non-null and different, `notes.embedding` is non-null and cosine-distinct from the original.

**8. Regression-anchor update** (`tests/ThanyMarcus.Cloud.Tests/Features/CompositeIngestSagaEndToEndTests.cs`):
   - Add a `EndToEnd_PushUserEdit_Reembed` lane: seed a composite → finalize → wait for `ready` → pull → mutate `## User Notes` block → push → wait for `user_edit_embed` job to terminate → re-pull → assert body is the pushed value, embedding refreshed.

**9. Documentation update.** Add one paragraph to `docs/architecture.md` describing the push semantics: User Notes edits land here; hub Generated Views edits are accepted but not yet diff-merged (future work); deletions land as tombstones; 409 is plugin's signal to three-way merge. Cross-reference ADR-0045 §9.

→ At end of Pass B: the plugin can push an edit to `## User Notes`, get a 200 with the new cursor, and observe re-embedding via SSE. A second device's stale push gets 409 and re-pulls. The `## User Notes` round-trip from local-vault → cloud → re-pull → local-vault works end-to-end against real Granite embeddings.

## Files touched

```
contracts/
  plugin-api.ts                                                 # CHANGED: SyncPullResponse extended; SyncPullProject + SyncPushRequest/Response/Conflict added
docs/
  architecture.md                                               # CHANGED: one paragraph on push semantics + the hub-edit-diff deferral
src/ThanyMarcus.Cloud.Api/
  Program.cs                                                    # CHANGED: app.MapSyncPushEndpoint();
  Features/Sync/
    SyncPullEndpoint.cs                                         # CHANGED: cursor query broadened; tombstones surfaced; projects[] populated
    SyncPushEndpoint.cs                                         # NEW: handler + optimistic UPDATE + 200/409/404 dispatch + enqueue UserEditEmbed
  Features/Processing/
    IngestJob.cs                                                # CHANGED: + IngestJobKind.UserEditEmbed
    IngestJobConfiguration.cs                                   # CHANGED: check constraint includes 'user_edit_embed' (if constraint exists)
    ProvenanceMaterializer.cs                                   # CHANGED: ExtractLlmCalls includes "user_edit_" stage prefix
    Phases/EmbeddingHandler.cs                                  # CHANGED: template tag switch on job.Kind
  Infrastructure/Database/Migrations/
    <ts>_AddUserEditEmbedJobKind.cs                             # NEW: drop + recreate ck_ingest_jobs_kind
src/ThanyMarcus.Shared/PluginApi/
  SyncPull.cs                                                   # CHANGED: + Projects field; + Deleted flag on SyncPullItem; SyncPullProject added
  SyncPush.cs                                                   # NEW: SyncPushRequest, SyncPushResponse, SyncPushConflict
tests/ThanyMarcus.Cloud.Tests/Features/
  SyncEndpointsTests.cs                                         # NEW: pull tombstones + projects[] + push 200/409/404/401 + re-embed
  CompositeIngestSagaEndToEndTests.cs                           # CHANGED: + EndToEnd_PushUserEdit_Reembed lane
```

## Acceptance criteria

1. ✅ `GET /api/sync/pull?since=<ts>` returns `{ items, projects, nextSince }`. The `items[]` array carries ready notes AND tombstones (`deleted: true`). The `projects[]` array carries all `entities` rows with `kind='project'` and `updated_at > since` (including soft-deleted ones).
2. ✅ A note soft-deleted via `DELETE /api/notes/{id}` surfaces in the next pull as a tombstone item with `body: ""`, `attachments: []`, `deleted: true`, `deletedAt: <ts>`, and the correct `relativePath` for the plugin to trash.
3. ✅ A note that was tombstoned BEFORE reaching `ready` (e.g., cancelled mid-saga) still surfaces as a tombstone — the cursor query covers `(status='ready' OR deleted_at IS NOT NULL)`.
4. ✅ The cursor `nextSince` advances to the max `updated_at` across both notes and entities; the plugin's next pull resumes correctly from that point.
5. ✅ `POST /api/sync/push` with a matching `baseUpdatedAt` returns 200 with the new `{updatedAt, transitionVersion}` and writes the new `body_output`. `body_hash` is nulled in the same UPDATE.
6. ✅ `POST /api/sync/push` with a stale `baseUpdatedAt` returns 409 with `{code: "stale_baseline", currentUpdatedAt, currentTransitionVersion}`.
7. ✅ `POST /api/sync/push` with an unknown `noteId` returns 404 with `{code: "note_not_found"}`.
8. ✅ `POST /api/sync/push` without the bearer token returns 401 via `RequirePluginAuthFilter`.
9. ✅ `POST /api/sync/push` with `deleted: true` sets `deleted_at = now()` and does NOT enqueue an embedding job.
10. ✅ A successful body-edit push enqueues an `ingest_jobs` row with `kind='user_edit_embed'`, `status='embedding'`. The `JobOrchestratorWorker` claims it, `EmbeddingHandler` runs, the new embedding lands, and the body-hash refreshes. The note's `transition_version` advances twice (once on push, once on embed terminal).
11. ✅ The push triggers `pg_notify('note_changed', $noteId)` and `/api/sync/events` clients see a `note_updated` SSE event.
12. ✅ Hub notes (`is_hub=true`) can be pushed and their body lands verbatim; the hub-edit-diff B4c regen is documented as deferred.
13. ✅ Provenance log includes a `user_edit_pushed` event stage at push time; the `provenance.llm_calls` roll-up includes user-edit events (via the `ExtractLlmCalls` regex extension).
14. ✅ All new test lanes pass (`SyncEndpointsTests.cs` + the extended regression anchor).
15. ✅ `dotnet build` clean under warnings-as-errors. `dotnet test tests/ThanyMarcus.Cloud.Tests` green.
16. ✅ Manual smoke against a freshly-provisioned cloud (with handoffs #1, #6, #8 applied):
    ```bash
    # 1. Pull (empty vault).
    curl -sS -H "Authorization: Bearer $TOKEN" \
      "https://$CLOUD_DOMAIN/api/sync/pull?since=1970-01-01T00:00:00Z"
    # → { items: [], projects: [], nextSince: null }

    # 2. Ingest a composite (existing flow).
    # ... handoff #3's /api/ingest/init + /api/ingest/finalize ...
    # Wait ~10s for the saga.

    # 3. Pull again.
    curl ... "...&since=1970-01-01T00:00:00Z"
    # → { items: [{noteId, body: "...# Note\n\n## User Notes\n\n...", deleted: false, ...}], projects: [], nextSince: "<ts>" }

    # 4. Push a body edit.
    curl -X POST -H "Content-Type: application/json" -H "Authorization: Bearer $TOKEN" \
      -d '{"noteId":"<id>","body":"...edited body...","baseUpdatedAt":"<ts from pull>","deleted":false}' \
      "https://$CLOUD_DOMAIN/api/sync/push"
    # → 200 { noteId, updatedAt: "<new ts>", transitionVersion: 2 }

    # 5. Push with the OLD baseUpdatedAt.
    curl ... -d '{"noteId":"<id>","body":"...","baseUpdatedAt":"<OLD ts>",...}' ...
    # → 409 { code: "stale_baseline", currentUpdatedAt: "<new ts>", currentTransitionVersion: 2 }

    # 6. Delete the note via push.
    curl ... -d '{"noteId":"<id>","body":"","baseUpdatedAt":"<new ts>","deleted":true}' ...
    # → 200

    # 7. Pull again.
    curl ... "...&since=<new ts>"
    # → { items: [{noteId, deleted: true, deletedAt: "...", body: "", ...}], projects: [], nextSince: "<even newer ts>" }
    ```

## Out of scope

1. ❌ **Hub-edit-diff push.** The B4c user-edit-aware regen path (cloud-pivot plan §19, fourth sync-write path) for `## System Output > Generated Views` regions of entity hubs. This handoff accepts hub-note pushes but treats them as opaque body replaces; the next hub regen will overwrite the user's edit. A future handoff (`cloud-hub-edit-diff-handoff.md`) is the home for that work — it requires a new LLM prompt and prompt-input shape that this handoff is not the right ticket to design.
2. ❌ **Admin projects endpoints.** `POST /api/admin/projects`, `PATCH /api/admin/projects/{id}`, `DELETE /api/admin/projects/{id}` (ADR-0045 §10). Those endpoints use the cloud-admin-token filter (`RequireCloudAdminTokenFilter`), not the plugin bearer-token filter — different auth seam, splits naturally into a separate handoff. They write to the same `entities` table this ticket reads from, so `projects[]` in pull will surface their writes automatically once they land.
3. ❌ **`GET /api/reseed` full-vault snapshot.** Cloud-pivot plan §9 "Re-seed from cloud" for new-device install or recovery. Paginated, includes all notes regardless of `updated_at`, includes asset URLs. Separate handoff; the cursor + paging shape is different enough that folding it into `/api/sync/pull` would muddy the contract.
4. ❌ **Bulk push.** One note per call. Bulk amortizes HTTP overhead but multiplexes failure modes (per-item 409 mid-batch is awkward) and is premature for thesis-scale edit cadence.
5. ❌ **Idempotency keys on push.** The plugin's correct retry behavior on a 200 timeout is re-pull + check + skip; idempotency keys add DB surface area for an edge case the plugin handles client-side.
6. ❌ **Chunked / partial body push.** Push is single-shot full-body. Diff/patch semantics would let the plugin send `{userNotesBlock: "..."}` and the cloud splice it in — but that re-introduces the same composer / hub-regen coupling this ticket explicitly avoids. Plugin sends the full body it wants the cloud to store.
7. ❌ **A second partial index** `ix_notes_tombstones_updated_at ON notes(updated_at) WHERE deleted_at IS NOT NULL`. The existing partial index covers `WHERE deleted_at IS NULL OR status='ready'` — the broadened cursor query falls back to a sequential scan for tombstones that never reached `ready`. For thesis scale (≲ 1k notes per user, single-tenant), sequential scan is fine; add the second index in a post-MVP perf pass if eval shows it matters.
8. ❌ **Compression / streaming for large pull responses.** A page of 50 notes with bodies and attachments fits comfortably in a single HTTP response (≲ 5 MB worst case). Streaming would help if a single user ever has 1M+ notes; out of scope for thesis-scale.
9. ❌ **A `since` query param on `/api/sync/push`.** Push is a write, not a poll — the `baseUpdatedAt` in the body is the only cursor it needs. No symmetry with pull's `?since=`.

## Open questions

1. **Concurrency token: `updated_at` exact-equality or `transition_version` exact-equality?** Default is `updated_at` (matches ADR-0045 wire shape). If the plugin's TypeScript `Date` round-trip turns out to drop microsecond precision, fall back to comparing `transition_version` server-side while keeping `baseUpdatedAt` on the wire. The fallback is a one-line SQL change (`WHERE id = $id AND transition_version = (SELECT transition_version FROM notes WHERE id = $id AND updated_at = $base)`). Resolve at implementation time; pick whichever is exact.
2. **Should `projects[]` include `kind='person'`, `kind='place'`, etc. — i.e., all entity types — or only `kind='project'`?** ADR-0045 §9 says "every sync_pull response includes a `projects[]` section with entities updated since `since` (kind='project', including tombstones)." — projects only. The plugin caches the project list for its routing dropdown UI; it doesn't need a person/place list for that. If a future plugin UI surfaces, say, an entity-hub navigator, a sibling `entities[]` section can be added without breaking projects[]. Default: projects only.
3. **Is `relative_path` on a tombstone always populated?** Notes that were tombstoned before routing assigned a project may have `relative_path = NULL`. The handler should fall back to `relative_path ?? $"Inbox/{noteId}.md"` (the same default the existing code uses for ready notes) so the plugin always has a path to trash. If the plugin's vault has the file at a different path (because a previous pull put it there), the trash operation is the plugin's responsibility. Document but don't gate.
4. **Should the `user_edit_pushed` event include the body bytes or just a hash?** Bytes would let the audit trail reconstruct exactly what the user pushed; bytes also bloat `events_log` JSONB. Compromise: include `body_sha256` (hex) and `body_bytes` count. The full body is reconstructible from the next reprocess's input anyway. Lock to hash + length.
5. **Does push fire an HTTP 202 instead of 200, since the embedding is async?** 200 is the right code for "the body write succeeded synchronously"; the embedding is downstream and the plugin can observe its completion via SSE. 202 would suggest the body write itself is async, which it isn't. Stick with 200.
6. **Pagination order for `projects[]`: by `updated_at` ascending (cursor convention) or by `canonical_name` (UI-friendly)?** Cursor convention wins — `OrderBy(e => e.UpdatedAt)` so the plugin can advance `nextSince` against the same order. The plugin can re-sort by canonical name client-side for UI display.

## Forward dependencies

- **`cloud-hub-edit-diff-handoff.md`** (future, not yet drafted): B4c user-edit-aware hub regen. Reads `notes.body_output` for hub notes, parses the user's `## System Output > Generated Views` edits, feeds them as a diff into the hub-regen LLM prompt. Touches `HubGenerationHandler` from handoff #7 and the `hub-generate-v1` prompt. This ticket's push handler already accepts hub pushes — that handoff just changes what the next regen does with them.
- **`cloud-admin-projects-handoff.md`** (future, not yet drafted): ADR-0045 §10 admin endpoints — `POST /api/admin/projects` (create user-source project), `PATCH /api/admin/projects/{id}` (rename), `DELETE /api/admin/projects/{id}` (soft-delete). Writes to the same `entities` table this ticket reads; auth filter is the cloud-admin-token, not plugin bearer.
- **`cloud-reseed-handoff.md`** (future, not yet drafted): `GET /api/reseed` paginated full-vault snapshot for new-device install. Different cursor semantics (no `since`; pages over the full vault).
- **`plugin-001-handoff.md`** (future, plugin-side): the Obsidian plugin's `Push.ts` / `Pull.ts` / `OfflineQueue.ts` modules consume the contract this handoff lands. The plugin's three-way merge on 409 + the trash-on-tombstone behavior are plugin-side; this handoff's only responsibility is the cloud-side contract.

## Diagram of handoff chain

```
CLOUD-SCHEMA-V2 (handoff #1)
  │  notes.deleted_at, entities (kind='project'), notes.project_id FK
  │
  ▼
CLOUD-SIDECARS (handoff #2)
  │
  ▼
CLOUD-INGEST-SAGA-FOUNDATION (handoff #3)
  │  /api/sync/pull (ready notes only), DELETE /api/notes/{id} → deleted_at
  │  IngestJobKind, JobOrchestratorWorker, EmbeddingHandler-against-stub
  │
  ▼
CLOUD-PROCESSORS-LIGHT (handoff #4)
  │
  ▼
CLOUD-PROCESSORS-HEAVY (handoff #5)
  │
  ▼
CLOUD-COMPOSE-PHASE (handoff #6)
  │  notes.body_output, compose-v1, UserNotesPreserver, ## User Notes block
  │
  ▼
CLOUD-LLM-INTELLIGENCE (handoff #7)
  │  notes.project_id assignment, entities (mention_count), hub notes
  │
  ▼
CLOUD-EMBEDDING (handoff #8)
  │  notes.body_hash, body-hash gate in EmbeddingHandler, Granite client
  │
  ▼
CLOUD-SYNC-EXTENSIONS (handoff #9, this ticket)
   /api/sync/pull tombstones + projects[]
   /api/sync/push body-replace + 409 conflict + UserEditEmbed job kind
```

## Risks and gotchas

- **EF Core check-constraint update.** Adding `'user_edit_embed'` to the `ck_ingest_jobs_kind` constraint requires a DROP + ADD migration step. Don't try to `ALTER ... ADD VALUE` (that's enum-specific, this is a CHECK). Test the migration on a non-empty `ingest_jobs` table to confirm the rewrite is online (it should be — CHECK constraint validation on an existing table is fast for a few thousand rows).

- **Optimistic UPDATE under concurrent push from two devices.** Both pushes have the same `baseUpdatedAt` (both pulled at the same cursor). First push lands → bumps `updated_at`. Second push's WHERE clause now fails → 409. Test this explicitly: spawn two concurrent push requests with the same baseline, assert exactly one 200 and exactly one 409.

- **`pg_notify` from inside a transaction.** Postgres `pg_notify` only fires when the transaction commits. The push handler's UPDATE + `db.SaveChangesAsync` for the enqueue + the `pg_notify` need to be in the right order. Match `NoteDeleteEndpoint.cs`'s pattern: do the UPDATE via `ExecuteSqlInterpolatedAsync` (its own implicit transaction commits immediately), then do `db.SaveChangesAsync` for the IngestJob insert (separate transaction), then `pg_notify` (no transaction). Each step's success is independent; partial-failure leaves the system in a recoverable state.

- **`SyncPullResponse` is a record with positional parameters.** Adding `Projects` between `Items` and `NextSince` is a breaking C# API change for any internal caller of the constructor (the endpoint is the only caller). The TS contract is property-name-based and doesn't care about position. Safe.

- **`IngestJob.Kind` is a string, not an enum.** The new `UserEditEmbed` constant is a plain `public const string`; no enum migration needed. The check constraint is the only place where the kind set is enumerated.

- **`SyncPushEndpoint.HandleAsync` parameter binding.** The endpoint takes the request from JSON body (`SyncPushRequest req`) and the DI services from the parameter list. ASP.NET Core's parameter binding picks JSON body for record types automatically; no `[FromBody]` needed but adding it is harmless and explicit.

- **TypeScript snake_case discipline.** The existing `contracts/plugin-api.ts` uses snake_case wire field names (`artifact_id`, `draft_id`). The Shared C# DTOs use camelCase via `JsonPropertyName`. Pick one convention per the existing precedent. Actually — the existing `SyncPullResponse` C# DTO already uses camelCase `JsonPropertyName` values (`"items"`, `"nextSince"`, `"deletedAt"`); the TS contract is stale and uses snake_case from an older revision. **Fix the TS contract in Pass A to match the C# convention** — camelCase across the wire. The cleanup is mechanical (rename `artifact_id → noteId`, etc.) and matches what's already actually shipping.

## Rationale: why this ticket is independent of #4, #5, #7

Pull and push are read/write paths over the `notes` table and the `entities` table; they do not invoke specialist workers (handoffs #4–#5), do not invoke LLM phases (handoff #7), do not produce or consume sidecar HTTP calls (handoff #2). They depend only on (a) the schema (handoff #1: tombstone columns, entity kind, projects FK), (b) the body shape (handoff #6: `body_output` + User Notes section), and (c) the embedding lifecycle (handoff #8: `body_hash` column + the gate). The push handler enqueues a `UserEditEmbed` job that runs through `EmbeddingHandler` exclusively — no routing, no extraction, no hub regen. Shipping this independently of #4/#5/#7 means the plugin's sync engine can land + be tested against a saga running on stub workers (which still write `body_output` via the composer in handoff #6); the real-worker readiness gates a richer user experience but not the sync contract itself.

## Notes for the next reader

- The cleanup of `contracts/plugin-api.ts` (which currently mirrors an older Avalonia-vintage contract, not the cloud-pivot one) is part of Pass A. Don't skip it; the plugin-001 handoff will copy this file into the plugin repo and will be confused by stale fields.
- The `UserEditEmbed` job kind is the first IngestJobKind that *doesn't* enter the phase machine at `Composing`. Verify the orchestrator's claim query and the dispatcher's switch handle this — they should, since they route by `Status`, not `Kind`, but add a `EmbeddingHandler_AcceptsUserEditEmbedKind` test.
- The 409 conflict body's `currentUpdatedAt` round-trips through the same JSON converter as the 200 response's `updatedAt` — the plugin can use the conflict response directly as its next `baseUpdatedAt` after a re-pull + merge.
- The push handler does NOT clear `notes.project_id`. A user editing a note's User Notes doesn't change which project the LLM routed it to. Reprocess is the lever for re-routing.
- The `relative_path` on a ready note today is `Inbox/<noteId>.md` or `Projects/<canonical>/<noteId>.md` depending on routing. On a tombstone, it's whatever was last written to `relative_path`. The plugin trashes whatever path that is; if the file isn't there (because routing renamed it after the plugin's last pull), the trash is a no-op. Plugin's responsibility, not cloud's.
