# PLUGIN-SYNC-LOCAL-STATE — Note deletion, mobile-safe durable local state, and processing-state continuity — Handoff Brief

Date: 2026-06-04
Status: **IMPLEMENTED 2026-06-04** (both forks resolved; see "STATUS: IMPLEMENTED" section at the end for what shipped and the deviations from this brief). Original design grilling complete.

This handoff specifies three intertwined things the plugin needs and currently lacks: (1) **note/folder deletion that propagates local→cloud safely**, (2) a **mobile-compatible durable local-state layer** to replace the in-memory/blob state that is being *cleared on reopen* (no queue survives a restart today), and (3) **processing-state continuity** so a note pushed for processing still shows its status (processing / finished / failed) after Obsidian or the machine restarts. The unifying principle: the **vault is the source of truth for existence**, the **cloud is the source of truth for derived/processing state**, and the **plugin is a view** that must be reconstructable. The only state the plugin must durably persist across a crash is a *tiny pending-intent queue*; everything else — the path↔ID index, the routing map, the per-note processing status — is rebuilt on startup from Obsidian's `metadataCache` + the cloud, so losing it is a non-event.

**Goal:** after this ticket — (a) deleting a note locally (incl. moving it to Obsidian/system trash) tombstones it in the cloud within a 14-day restore window, and un-trashing it revives the tombstone cheaply (no re-ingest); (b) deleting a *folder* prompts force-delete-all vs dissolve-and-reroute-to-Inbox, executed as a resumable operation; (c) a note's processing status survives any restart; (d) **no local operation is ever lost to a crash or network drop** — pending work is written ahead, resumed on restart, and converges even if the local cache is wiped, because reconciliation against the cloud is the backstop.

**Does NOT ship:**
- Cross-device queue sharing — the intent queue is strictly per-device; cross-device coherence comes from the cloud + reconciliation, never from sharing local state.
- A general local query DB (SQLite). Ruled out: see "Why not SQLite" — it is desktop-only in Obsidian and mobile is in scope.
- Conflict-merge for note *content* (CRDTs etc.). Out of scope — one user, vault is master for existence; the cloud regenerates derived content, it doesn't co-edit.
- True Origin editing / plugin write-back (lives in the essence redesign's separate future ticket; unrelated here).

## Where decisions live (read before doing anything)

- **Memory `portal_architecture.md`** — the cloud already runs a **Postgres job queue (SKIP LOCKED + lease + mutable status), SSE only**. This IS the resumable-saga machinery; the cloud side of every long-running op below **reuses it** (new job kinds), it does not invent new infrastructure. The provisioning and ingest sagas are the precedent for "leased, resumable, mutable-status."
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. Deletion is per-note by `thany_note_id`.
- **Memory `essence_synthesis_redesign.md`** — `thany_note_id` / `thany_updated_at` / `thany_locked` are the sync-only frontmatter fields (shipped 2026-06-04). `thany_note_id` is the **stable per-note UID** every flow here keys on.
- **Memory `no_vault_export_feature.md`** + data-sovereignty stance — the vault stays local and is sovereign. This is *why* the model is **local-master** (vault authoritative for existence; cloud obeys), unlike Notion's cloud-master. Simpler: no existence-conflict resolution; all risk concentrates in "did we correctly detect the local change," which the circuit breaker + soft-delete net guard.
- **Memory `bearer_never_in_urls.md`** — the delete/tombstone/reroute calls are privileged; authenticate via the Authorization header, never URL credentials.
- **Memory `feedback_no_code_comments.md`** — new plugin/cloud code carries no narrative/section-banner comments; only non-obvious *why*.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3 on Microsoft.Testing.Platform (`dotnet test --project <csproj> -- --filter-class "*Name"`).
- **Existing cloud soft-delete** — `deleted_at` columns + `deleted_at IS NULL` filters + soft-delete-aware partial uniques already exist (per `plans/cloud-schema-v2-handoff.md` / `plans/cloud-llm-intelligence-handoff.md`). Tombstoning notes fits the established pattern; cascade-clean `mentions` / embeddings / hub backlinks on tombstone.
- **`/api/sync/pull`** (referenced in `plans/cloud-llm-intelligence-handoff.md`) — the existing note-pull seam. This ticket adds a *cheap status pull* alongside it (see Pass D).
- **Obsidian plugin APIs used (stable; confirm field shapes against the installed API version):** `vault.on('delete'|'rename', TAbstractFile)` (fires for both `TFile` and `TFolder`; **no pre-delete veto** — the event arrives after the file is already gone); `app.metadataCache.getFileCache(file).frontmatter` (parsed frontmatter, populated after load, **works on mobile**); `app.vault.getMarkdownFiles()`; `app.vault.adapter` (`read`/`write`/`append`/`exists`/`remove`/`rename` — the cross-platform storage abstraction, **works on mobile**, real file writes are OS-flushed); the "Deleted files" setting (move to system trash / `.trash` folder / permanent).

## Decisions resolved in the 2026-06-04 grilling

Recorded so the next reader does not re-litigate.

1. **UID is the right identity primitive — do not replace it.** `thany_note_id` already exists. Content-hash identity breaks on edit; CRDTs solve a multi-writer merge problem that doesn't exist here; git-as-transport is heavier and the cloud is a processing API, not a git host. The interesting problem is *detection + safety*, not identity.
2. **Local-master existence model.** Vault is authoritative for "does this note exist"; the cloud follows. (Cloud-originated hub notes flow the other way — see open fork.)
3. **Trash = delete, uniformly.** Any local disappearance — Obsidian `.trash`, system trash, or permanent — is one signal: **tombstone ID X**. The plugin does *not* watch the trash folder. Any *reappearance* of a known-tombstoned ID = **revive**. This handles restore-from-`.trash` and restore-from-system-trash identically (both end with the file back in the vault).
4. **Cloud soft-delete (tombstone), never hard.** On tombstone: set `deleted_at`, remove from active set (search/embeddings/dashboard), cascade-clean derived data, **keep the row + the expensive derived artifacts** (transcript, vision captions, embedding, essence) for a **14-day** retention window. Restore within the window = revive the tombstone (near-free); past it = GC → real hard delete + free storage. Retention is **≥ local trash retention** so an un-trash always lands on a live tombstone instead of triggering a full re-ingest.
5. **rename/move ≠ delete.** `thany_note_id` persists across a move → it's a re-route (update folder/project), never a tombstone.
6. **Mass-deletion circuit breaker** — the single most important safety rail. If one reconciliation pass wants to delete more than a threshold fraction/count of notes, **do not execute** — pause and require explicit confirmation. A real user mass-delete is rare; a detection bug or unmounted vault is the common cause.
7. **Folder deletion prompts for intent.** On a detected folder delete, offer **(a) force-delete project + all notes inside** vs **(b) dissolve the project, reroute its notes to another project or Inbox.** Because Obsidian has **no pre-delete veto**, the prompt is *post-hoc*: it governs cloud behavior + whether to re-materialize files, not whether the local delete happens.
8. **Re-materialization = re-pull, so the user sees the effect.** Option (b) keeps the notes in the cloud (reassign project → Inbox) and the plugin **re-pulls the files locally into the Inbox folder** — they physically reappear. **No placeholders** during the window (explicit user call); the SSE progress indicator covers the vanish-then-reappear moment.
9. **Un-askable folder-delete default = (b), preserve.** If the plugin wasn't running when the folder was deleted (reconciliation detects it later), the safe default is dissolve-and-reroute-to-Inbox — deleting a *container* must not silently nuke its *contents*. Force-delete happens *only* on explicit choice.
10. **Long-running ops are durable sagas, modeled declaratively.** Mark intent durably *first*, then execute as an **idempotent desired-state reconcile** ("make local match the cloud's desired set"), not an imperative step-replay. Interruption → re-run the reconcile → converges, pulling only what's missing. Re-running a finished op is a no-op.
11. **Mobile is in scope** (it's the primary capture surface) → **SQLite is out as the base layer** (see "Why not SQLite"). Storage = adapter-written files for the must-survive queue + rebuilt indexes.
12. **Split local state by recoverability** (the core of the persistence fix): only the *pending-intent queue* must survive a crash; the index, routing map, manifest, and processing status are all *rebuildable* from `metadataCache` + the cloud.

## Why not SQLite (mobile is in scope)

- **Native `better-sqlite3`:** node-gyp + Python + MSVC/.NET toolchain, ABI-bound to Electron (breaks on Obsidian updates), and **native modules don't load on Obsidian Mobile at all**.
- **WASM SQLite (sqlite3-wasm / absurd-sql):** needs `SharedArrayBuffer` + OPFS, which **Obsidian Mobile doesn't expose** — also desktop-only.
- **IndexedDB:** cross-platform but has documented Obsidian-Mobile reports of **transactions not being flushed to disk** (close soon after write → reopen → state gone / reindex), which is *exactly the "cleared on reopen" symptom*, plus metadata limits on large vaults. Usable as a **disposable cache**, unsafe as the home of irreplaceable data.

## The local-state taxonomy (the design)

| Tier | What | Store | On loss |
|---|---|---|---|
| **Must-survive** | pending-intent queue (submissions / tombstones / reroutes not yet acked by cloud) | **append-only journal file via `vault.adapter`** in the plugin config dir, per-device, atomic-rename compaction | reconciliation re-derives most; only an explicit *force-delete* choice degrades to the safe (preserve) default |
| **Rebuildable** | path↔ID index, folder→project routing map | in-memory, rebuilt on startup from `app.metadataCache` | rebuild — non-event |
| **Rebuildable** | last-synced manifest + **last-known processing status** per note | disposable cache (IndexedDB/JSON), re-derived from the cloud | refetch from cloud — non-event |
| **Source of truth (remote)** | authoritative processing status, derived data, desired-local-set | cloud (Postgres) | n/a |

**Why this calms the "cleared on reopen" fear:** the surface area of "must not lose" shrinks to a tiny queue. The routing state you were worried about is reconstructed from Obsidian's own parsed frontmatter every launch — robust *by reconstruction*, not by persistence.

### The durable queue — mobile-safe spec
- Append-only journal via `app.vault.adapter` (`append`; if append is flaky on the mobile adapter, fall back to write-temp + atomic rename). Real file writes are OS-flushed (unlike IndexedDB).
- Lives in the plugin config dir, **not** synced vault content. Namespace the file by a device id; ignore other devices' files (so Obsidian Sync syncing the plugin dir can't make device B replay device A's queue — harmless anyway since ops are idempotent, but don't rely on it).
- **Crash-safe:** append-only → a crash mid-write loses at most the last partial record, never the queue. Compact on size-threshold + on plugin unload (write temp → atomic rename).
- **Resume, never reset** — *this is the second half of the original bug.* On startup, read the journal and **resume** pending intents; remove an entry only when the cloud acks it. **Never truncate on load.** If load currently clears state, that line is the fix regardless of store.
- **Idempotent op-ids:** each intent has a stable id so a resumed/retried op is a no-op (tombstone twice → 204; re-pull → declarative converge).

## Startup sequence (every reopen)

1. **Load the durable queue → resume** pending intents (submissions, tombstones, reroutes).
2. **Rebuild** path↔ID + folder→project from `metadataCache` (in-memory).
3. **Show last-known processing statuses immediately** from the disposable manifest cache (offline-friendly; if absent, show "syncing…").
4. **When online:** reconcile manifest vs cloud → enqueue any divergence the queue missed (self-healing backstop); cheap status-pull for non-terminal notes; re-subscribe SSE for live updates; body-pull the notes that finished while closed (now succeeded → pull processed essence; failed → show error).

## Scope (logical passes)

### Pass A — Plugin durable persistence foundation (fixes the "cleared on reopen" bug)
The append-only intent journal (`vault.adapter`), the drain loop (retry + backoff + idempotent op-ids), resume-not-reset load semantics, startup index rebuild from `metadataCache`. **Everything else depends on this; do it first.** Ship with the queue empty-but-durable and the index rebuild, even before deletion exists — it's the foundation.

### Pass B — Note deletion propagation (single notes)
- **Detection:** maintain the path↔ID map from `vault.on('create'|'modify'|'rename')`; on `vault.on('delete')` resolve the ID *from the map* (frontmatter is already gone). `rename` → re-route (update folder/project), never delete.
- **Intent:** enqueue `tombstone {noteId}`; drain → idempotent cloud `DELETE` (soft-delete, 14-day retention, cascade-clean derived data, 204 if already tombstoned).
- **Revive:** a `create` of a file whose `thany_note_id` is currently tombstoned in the cloud → enqueue `revive {noteId}` → cloud un-sets `deleted_at` (cheap, no re-ingest) if within window; else falls through to normal re-ingest.
- **Circuit breaker:** before executing a reconciliation-detected batch of deletes, enforce the threshold guard.

### Pass C — Folder dissolve + reroute (resumable)
- **Coalesce** the burst of child `delete` events + the `TFolder` delete under a path prefix within a short window → recognize "folder deletion" vs N individual deletes → show the prompt once.
- **Prompt** (a) force-delete-all vs (b) dissolve+reroute(→Inbox or chosen project). Default for the un-askable/offline case = (b).
- **Cloud:** a new leased **job kind** (`folder_dissolve` / `reroute`) in the existing queue — reassign N notes' project (one atomic txn for the reassign; the job exists for resumability + SSE progress). Force-delete = tombstone the project entity + tombstone all child notes.
- **Plugin re-pull (option b):** declarative reconcile — "make local Inbox match the cloud's desired set for these notes." Resumable file-by-file; interruption → re-run converges. SSE shows "rerouting N notes…".

### Pass D — Processing-state continuity
- **Cheap status pull** endpoint (`{noteId, status}` for non-terminal notes) distinct from the heavy `/api/sync/pull` (full processed body). Reopen lights status dots instantly; bodies download only for things that finished.
- **Disposable last-known-status cache** (a field in the manifest cache) for offline reopen.
- **SSE reconnect-with-initial-pull:** on reopen, status-pull fills the disconnected gap, then SSE streams live updates. The "intent to process" half is already covered by the durable queue (a push that never reached the cloud resumes as a pending submission).

## Open fork — RESOLVED 2026-06-04

**Hub-note deletion (cloud-generated notes).** RESOLVED → **(a) tombstone, don't resurrect.** Deleting a hub note locally sets `entity.hub_suppressed = true`, tombstones the hub note, and clears `entity.hub_note_id`. A future mention-threshold cross **skips** regen while suppressed (`ExtractingEntitiesHandler` gate). Explicit force-regen via `POST /api/sync/entities/{id}/regenerate-hub` clears suppression and re-materialises. The note tombstone path branches on `is_hub` in `NoteTombstoneService`.

**Circuit-breaker threshold** RESOLVED → trip when a single reconciliation pass would tombstone **N > 10 OR N > 25%** of the synced set (`planReconciliation` in `Reconciler.ts`). Guards ONLY the reconciliation backstop, never live `vault.on('delete')`.

(Resolved already: retention = 14 days; re-materialization = re-pull; no placeholders; mobile in scope; folder-delete default = preserve.)

## STATUS: IMPLEMENTED 2026-06-04

Cloud (build clean, 7 integration tests green) + plugin (build clean, 75 vitest green). Deviations from the brief, with rationale:

1. **Folder ops are a synchronous transactional endpoint, not a leased job kind.** `POST /api/sync/folders/dissolve` (`FolderDissolveEndpoint`) does the reroute/force-delete in one transaction and returns `{affectedCount, desired}`. The brief assumed a new `IngestJob` kind, but `IngestJob.NoteId` is a required per-note FK with a unique-active-per-note index — a project-level op doesn't fit that table, and the reassign is a single atomic `UPDATE` anyway. **Resumability lives plugin-side** (durable journal `folder_dissolve` intent + declarative re-pull via the cursor pull, which re-materialises rerouted notes because reroute bumps `updated_at`). The dedicated SSE "rerouting N…" event was dropped (single-device tool); the synchronous response carries the count + desired set, and the plugin shows a Notice.

2. **Folders-are-projects honoured.** There is no project entity (`project_id` is vestigial). Folder = `relative_path` prefix. Dissolve-reroute rewrites the prefix to `Inbox/`; force-delete tombstones every note under the prefix via `NoteTombstoneService`. Desired-set + dissolve key on the path prefix, not `project_id`.

3. **Tombstone retention semantics (concretised).** On tombstone we KEEP the row + embedding + `body_output` + attachments/extracted-text (→ cheap content revive, no re-ingest), and CLEAN the active-set contributors: delete the note's `mentions` + decrement `entities.mention_count`, prune `entity_suggestions.occurrences`, and (if hub) suppress the entity. The HNSW/active indexes already exclude `deleted_at IS NOT NULL` rows. **Revive does NOT restore mentions** — they re-derive on reprocess. GC (`TombstoneGcSweeper`, hourly) hard-deletes tombstones past 14 days and frees attachment blobs.

4. **rename → reroute uses a new `POST /api/sync/notes/{id}/move`** that sets `relative_path` WITHOUT bumping `updated_at` (no pull echo to the originating device). Keeps the cloud's path current so a later reprocess writes to the right place.

5. **Echo suppression (`VaultEventGuard`).** Not in the brief but required: the sync `Writer`'s own `vault.delete/create/modify` re-fire `vault.on(...)`. The Writer marks each path just before mutating; handlers consume the mark and skip propagation (8s TTL safety valve). Without this a cloud-driven delete would echo back as a user tombstone.

6. **`deviceId` lives in synced `data.json`.** True per-device namespacing isn't reachable via Obsidian APIs. If the plugin dir is cross-device-synced, two devices share the journal file/id; per the brief this is "harmless — ops are idempotent." Documented, not relied upon.

7. **Submission-resume is out of this pass.** The durable journal covers the delete-family intents that are this ticket's subject (tombstone / revive / reroute / folder_dissolve). The submit/upload saga keeps its existing queue-entry + cloud orphan-sweeper backstop; journaling it (idempotent re-upload) is a separate follow-up. Consistent with "ship the queue empty-but-durable first."

New cloud surface: `DELETE /api/sync/notes/{id}`, `POST .../revive`, `POST .../move`, `GET /api/sync/status`, `GET /api/sync/desired`, `POST /api/sync/folders/dissolve`, `POST /api/sync/entities/{id}/regenerate-hub`, `entities.hub_suppressed` (migration `AddEntityHubSuppressed`). New plugin modules: `IntentJournal`, `IntentQueue`, `NoteIndex`, `VaultEventGuard`, `DeletionCoalescer`, `Reconciler`, `FolderDissolveModal`, `ConfirmMassDeleteModal`.

## Cloud-side endpoints / jobs implied (reuse existing queue + soft-delete)
- `DELETE /api/sync/notes/{id}` — idempotent soft-delete (tombstone), cascade-clean derived data; 204 if already gone.
- Revive (within window) — `POST /api/sync/notes/{id}/revive` or auto-revive on re-ingest of a tombstoned id.
- GC job — hard-delete tombstones older than 14 days; reuse the queue.
- `folder_dissolve`/`reroute` job kind — reassign project → Inbox for N notes; SSE progress.
- Cheap status pull — `GET /api/sync/status?notes=…` → `[{id, status}]`.
- Desired-local-set query — what files should exist locally for project X (drives the declarative re-pull reconcile).
- All authenticated via the Authorization header (`bearer_never_in_urls`).
