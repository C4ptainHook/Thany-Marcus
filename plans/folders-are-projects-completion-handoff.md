# CLOUD-FOLDERS-ARE-PROJECTS-COMPLETION — Folder registry so created folders are routable + drop the vestigial project_id — Handoff Brief

Date: 2026-06-04
Status: Draft. Follow-up to `plans/cloud-folders-are-projects-handoff.md`, which dropped the project *entity* + the route DTO but left two gaps. **Most of the cleanup this brief originally scoped was already shipped during the 2026-06-04 session (see "Already shipped — do NOT redo").** Two items remain.

The folders-are-projects ticket made routing classify a note into a vault folder (a `relative_path` prefix) instead of a project entity. But it left the cloud with **no model of the vault's folder tree** — `RoutingHandler.LoadCandidateFoldersAsync` reverse-engineers the candidate folder set from the distinct first-segments of existing notes' `relative_path` (`RoutingHandler.cs:172–175`). Consequence: **a folder the user just created is invisible to the router until a note already lives in it**, so the very first capture can never auto-route into a new, empty folder. That breaks the core expectation of the feature — "make a folder, it's a project, notes route there." This brief adds a **folder registry** (a synced cloud mirror of the vault folder tree) and points routing at it, so empty/just-created folders are routable. It also drops the now-vestigial `notes.project_id` column.

**Goal:** after this ticket — (a) creating a folder in the vault makes it an immediate routing target, even before any note lives in it; (b) `notes.project_id` and its FK are gone. No behavior depends on `project_id` anymore (verified — zero readers outside the model/config/migrations).

**Does NOT ship:** a dashboard "projects" list (the registry enables it, but the UI is separate); nested-folder routing (routing stays top-level-folder classification, as today — the registry just makes the candidate set complete).

## Already shipped — do NOT redo (verified 2026-06-04)

The 2026-06-04 session landed these in parallel; the handoff must not re-propose them:
- **`/api/sync/desired` filters by `relative_path` prefix**, not `project_id` — `SyncMaintenanceEndpoints.cs:81–103` (`folder` param → `RelativePath.StartsWith(prefix)`; comment: *"Folders-are-projects: a folder is a relative_path prefix, never a project entity"*).
- **`FolderDissolveRequest` is folder-keyed** — `(Folder, Mode, TargetFolder)`, `SyncMaintenance.cs:25–28`. No `ProjectId`/`TargetProjectId`.
- **Folder dissolve is implemented and `relative_path`-based** — `FolderDissolveEndpoint.cs` (`/api/sync/folders/dissolve`): `force_delete` tombstones all notes under the prefix; `reroute` is a single SQL `UPDATE` rewriting the `relative_path` prefix to the target (Inbox default), keeping the basename, returning the desired set.
- **Hub-deletion = tombstone-don't-resurrect is implemented** — `NoteTombstoneService.cs:137` sets `entity.HubSuppressed = true` when a hub note is tombstoned; `ExtractingEntitiesHandler.cs:144` skips regen when suppressed; `/api/sync/entities/{id}/regenerate-hub` (`SyncMaintenanceEndpoints.cs:105–147`) clears it. (The `sync_hub_deletion_fork` decision is in code.)
- **`/api/sync/revive`, `/status`, `/desired`** from the sync-and-local-state design are wired (`SyncMaintenanceEndpoints.cs`).

## Where decisions live

- **`src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/RoutingHandler.cs`** — `LoadCandidateFoldersAsync` (`:169–194`) is the *only* code to change for routing: today it reads `db.Notes...Select(n => n.RelativePath)`; it must read the **folder registry** instead. The rest of routing (the `route:v1` LLM classify, the exact-match guard at `:133`, the Inbox fallback, the `RoutingFoldersMax` cap, the `Inbox`/stubs/`_`/`.` exclusions at `:186–189`) stays byte-for-byte.
- **The plugin sync layer is fully IMPLEMENTED** (`plans/sync-and-local-state-handoff.md` shipped). The registry rides it — **not new infrastructure, a new intent kind on the existing queue.** Concrete seams (verified 2026-06-04):
  - `plugin/thany-marcus/src/sync/IntentJournal.ts` — `IntentKind`/`Intent` union + append-only NDJSON journal. Add `"folder_register" | "folder_unregister"` here.
  - `plugin/thany-marcus/src/sync/IntentQueue.ts` — `IntentExecutor` interface + `run()` switch (`:83–94`). Add two methods + two cases.
  - `plugin/thany-marcus/src/main.ts` — vault hooks `registerVaultEvents()` (`:262–270`); `onVaultCreate` (`:318`) **currently `return`s on `TFolder` — this is the gap**; `onFolderRenamed` (`:372`); `onVaultDelete` folder branch (`:392`); `intentExecutor()` (`:453`); `isSyncedFolderPath()` (`:541`); `cloudRelativePath()` (`:311`); `reconcileOnStartup()` (`:484`) is the pattern to mirror for folders.
  - `plugin/thany-marcus/src/api.ts` — `ApiClient`; mirror `tombstoneNote`/`moveNote`/`desiredSet` for the new folder calls.
  - `plugin/thany-marcus/src/sync/Reconciler.ts` — note reconcile + circuit breaker (`ABSOLUTE_THRESHOLD=10`, `FRACTION_THRESHOLD=0.25`). **Folders do NOT use the breaker** (unregistering a folder removes a routing candidate, it doesn't delete notes).
- **Memory `ef_migrations_remove_hazard.md`** — never `ef migrations <add|remove> --no-build`. Build first, then add the drop migration.
- **Memory `feedback_no_code_comments.md`** — no narrative/section-banner comments in the new registry code; only non-obvious *why*.
- **Memory `bearer_never_in_urls.md`** — the folder-registry sync endpoints are privileged; Authorization header.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3 (MTP: `dotnet test --project <csproj> -- --filter-class "*Name"`).
- **`Note.cs:28` + `NoteConfiguration.cs:37,71–72,84–85`** — the vestigial `ProjectId` property, `Property`, `ix_notes_project` index, and `HasOne<Entity>...HasForeignKey(n => n.ProjectId)` FK (`fk_notes_entities_project_id`, `OnDelete SetNull`) to remove. Leave the parallel `HubEntityId` FK (`:86–87`) alone — it's live.

## Pass 1 — Folder registry (the user-facing fix)

**1. New table `folders`** (per cloud). Columns: `id` (Guid PK), `path` (text — folder path relative to the vault root, e.g. `Projects/Foo`), `created_at`, `updated_at`, `deleted_at` (soft-delete). Soft-delete-aware unique on `path` (mirror the `entities` partial-unique pattern). Migration via EF (build first — see hazard memory).

**2. Plugin folder-sync (two new intent kinds on the existing queue).** Cloud-relative paths throughout (`cloudRelativePath(localPath)` strips the `vaultFolder` root, exactly as notes do).
- **Intent kinds** (`IntentJournal.ts`): add `folder_register {opId, folder}` and `folder_unregister {opId, folder}` to `IntentKind` + the `Intent` union.
- **Executor + drain** (`IntentQueue.ts`): add `folderRegister(folder)` / `folderUnregister(folder)` to `IntentExecutor` + two `run()` cases. Wire the impls in `main.ts:intentExecutor()` → `this.api.registerFolder(f)` / `this.api.unregisterFolder(f)`.
- **Create** (`main.ts:onVaultCreate`): replace the `if (file instanceof TFolder) return;` short-circuit (`:319`) with — if `TFolder` && `isSyncedFolderPath(path)` → `enqueueIntent(folder_register, cloudRelativePath(path))`. **This single line is what makes a brand-new empty folder routable.**
- **Rename** (`main.ts:onFolderRenamed`, `:372`): after the existing note reroutes, also enqueue `folder_unregister(old)` + `folder_register(new)`.
- **Delete** (`main.ts:onVaultDelete` folder branch, `:392`): alongside `coalescer.folderDeleted(path)`, enqueue `folder_unregister(cloudRelativePath(path))`. **Required and orthogonal to dissolve:** `onFolderDeleted` early-returns when `count===0` (`:424`), so an *empty* folder delete never reaches dissolve — without this explicit unregister it would leave a phantom routing candidate forever.
- **Startup reconcile** (`main.ts`, mirror `reconcileOnStartup`): enumerate `app.vault.getAllLoadedFiles().filter(f => f instanceof TFolder)` (portable — avoid the uncertain `getAllFolders()`), keep those passing `isSyncedFolderPath`, map via `cloudRelativePath`; diff against `api.listFolders()` → enqueue `folder_register` for vault-not-cloud, `folder_unregister` for cloud-not-vault. Persist a `folderManifest?: string[]` in `PersistedState` as the baseline (mirror `manifestIds`). **No circuit breaker** (low-risk; see decisions-live).
- **Exclude** to keep the registry clean: `isSyncedFolderPath` already drops the root + `_drafts`; also skip `_`-prefixed folders (e.g. `_Entities`) and `Inbox` plugin-side. The cloud routing exclusions (`RoutingHandler:186–189`, `_`/`.`/Inbox/stubs) remain as the backstop.

**3. Cloud endpoints** (reuse the `/api/sync` group + `RequirePluginAuthFilter`, matching `SyncMaintenanceEndpoints`): `POST /api/sync/folders` `{folder}` (idempotent upsert — register-existing is a no-op), `DELETE /api/sync/folders` `{folder}` (idempotent tombstone — 404/absent → done, so retries are safe per the `IntentQueue` contract), `GET /api/sync/folders` → `{folders: string[]}` for the startup reconcile. Keep the registry ops **explicit** — do *not* have `FolderDissolveEndpoint` secretly touch the registry; the plugin's `folder_unregister` on delete owns that.

**4. Point routing at the registry.** Change `RoutingHandler.LoadCandidateFoldersAsync` (`:169–194`) to read the `folders` registry (`deleted_at IS NULL`) instead of the `notes.relative_path` GROUP BY, keeping the same `Inbox`/stubs/`_`/`.` exclusions and `RoutingFoldersMax` cap. Empty folders now appear → routable on first capture. Everything downstream (the `route:v1` classify, exact-match guard, Inbox fallback) is unchanged.

**5. The create→capture race (corrected).** Ingest does **not** ride the IntentQueue — drafts submit through `Submitter`→`api.ingestInit` (`main.ts:onDraftSubmitted`), a separate path — so there is no single ordering guarantee between `folder_register` and a note's ingest. In practice the register wins: it's one fast POST the `IntentQueue` kicks immediately on enqueue, while the note's cloud-side `Routing` phase is gated behind the extraction phase (seconds of latency). For v1, rely on that timing. If the race is ever observed (offline create-then-immediately-capture), the guaranteed closer is to carry the candidate folder list in the `IngestInitRequest` payload and have routing use `payload ∪ registry` — add that only if needed.

## Pass 2 — Drop the vestigial `notes.project_id`

Safe: verified zero behavioral readers remain (routing writes only `relative_path`; `/desired` and dissolve are folder/`relative_path`-based now).
- Remove `Note.ProjectId` (`Note.cs:28`); remove `builder.Property(n => n.ProjectId)` (`NoteConfiguration.cs:37`), the `ix_notes_project` index (`:71–72`), and the `HasOne<Entity>...HasForeignKey(n => n.ProjectId)` FK (`:84–85`).
- New migration `DropNotesProjectId`: `DROP CONSTRAINT fk_notes_entities_project_id`, `DROP INDEX ix_notes_project`, `DROP COLUMN project_id`. Build first; do **not** use `--no-build`.

## Test anchors
- `RoutingHandlerTests` — a registered-but-empty folder (registry row, zero notes under its prefix) appears as a routing candidate and the LLM can route into it; routing still falls back to Inbox below `RouteAcceptMin` / on no exact match; `_`/`.`/Inbox/stubs folders stay excluded.
- Plugin `main.ts` handlers — folder create enqueues `folder_register`; **empty-folder delete still enqueues `folder_unregister`** (the `count===0` dissolve early-return must not suppress it); folder rename enqueues unregister(old)+register(new); a synced-note delete is unaffected.
- Folder startup reconcile — vault-folder-set vs `api.listFolders()` diff yields the right register/unregister intents, with **no** circuit-breaker prompt (folders are low-risk).
- Idempotency — `registerFolder` on an existing folder and `unregisterFolder` on an absent one are both no-ops (so a resumed intent is safe).
- A migration round-trip test that `project_id` is gone and nothing references it.
