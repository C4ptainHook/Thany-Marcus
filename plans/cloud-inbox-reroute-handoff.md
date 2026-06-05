# CLOUD-INBOX-REROUTE — Self-healing Inbox re-routing for the empty-vault cold start

**Status:** DESIGN — not started. Follows `cloud-related-notes-actionable-handoff.md` (SHIPPED) and the folders-are-projects routing model (`RoutingHandler`, `SyncMaintenanceEndpoints`).

**The problem (cold start):** A brand-new Obsidian vault is an empty folder — no `Inbox`, no project folders, zero rows in the cloud `folders` table. `RoutingHandler` is a **one-shot phase** (`RoutingHandler.cs:14`, `Phase => IngestJobStatus.Routing`): when it sees no candidate folders it emits `Decision: "skipped:no_folders"` and parks the note at `Inbox/{id}.md` (`:67-91`). There is **no re-route** anywhere in real source — once parked, a note stays in `Inbox` forever. So every capture made before the user hand-builds a folder taxonomy is permanently stranded, and the headline organizing feature (LLM folder routing) is dormant for the entire onboarding. The worse the cold start, the bigger the permanent `Inbox` pile routing will never touch.

**The decision (recorded):** **stay-empty + self-heal**, NOT scaffold-on-connect. We create no folders on vault connect (consistent with data-sovereignty / user-owns-their-taxonomy). Instead we make the `Inbox` backlog *recoverable*: when real folders appear, re-run routing over the notes still sitting in `Inbox`. Scaffold-on-connect (seed `Inbox/` + example project folders) is the **rejected alternative** — it imposes structure on a user who deliberately wanted an empty folder.

**Why now:** for the thesis demo this is the make-or-break first-run story. With self-heal, the moment an evaluator creates their first folder the org feature visibly fires over their early captures, instead of looking inert.

**Estimated effort:** #A+#B ~1.5 days (shared `InboxRerouteService` ~0.75d, cold-start trigger ~0.25d, manual triage endpoint + plugin action ~0.5d). #C ~0.25d, independent.

---

## Shared mechanism — `InboxRerouteService` (the load-bearing piece)

Re-routing is **not** a blind move — the `Inbox` notes were never classified against real folders, so we must re-run the *routing decision* (LLM) per note against the now-non-empty candidate set. It must touch **only `relative_path`** and never re-enter synthesis/embedding — the essence/body is already written and must be preserved verbatim.

**Refactor first:** extract the decide-folder logic from `RoutingHandler` into a shared, pure-ish `FolderRouter`:
- `FolderRouter.DecideAsync(noteBodyExcerpt, candidateFolders, ct) -> (string? chosenFolder, LlmEvent evt)` — owns the `PromptBuilder.BuildRoute` call, the `RouteAcceptMin` gate (`:130-131`), and the exact-match guard (`:133-135`). `RoutingHandler` calls it (no behavior change); the reroute service calls the same code (no duplicated routing logic).
- Keep `LoadCandidateFoldersAsync`'s candidate definition as the single source of truth — top-level segment, drop `_`/`.`-prefixed, drop `Inbox`, drop `StubsFolder`, cap at `RoutingFoldersMax` (`RoutingHandler.cs:169-191`). Lift it to a shared helper both call.

**The service** (scoped, modeled on the shipped `RelatedNotesCalibrator`):
1. Load candidate folders. If empty → no-op (nothing to route *to*).
2. Select source notes: `Status == NoteStatus.Ready`, `DeletedAt == null`, `relative_path LIKE 'Inbox/%'`. Cap at `RerouteMaxBatch` (bounds a pathological huge Inbox; `log()` the count dropped — no silent truncation). Skip `IsHub` notes (they never live in Inbox anyway).
3. For each note: `FolderRouter.DecideAsync`. If `chosenFolder is null` (route still picks Inbox / below confidence) → **leave it, no write, no version bump** (the `RouteAcceptMin` gate naturally limits churn — low-confidence notes stay put). If a folder is chosen → SQL update `relative_path = '{chosen}/' || regexp_replace(relative_path,'^.*/','')`, bump `updated_at` + `transition_version` (exactly the `FolderDissolveEndpoint.cs:70-77` pattern — keep basename `{id}.md`, atomic, idempotent). Append the route `LlmEvent` per note.
4. Return `(affectedCount, desired[])` where `desired` = the moved Ready notes' `(id, relative_path)` — same shape as `FolderDissolveResponse` so the plugin pull loop re-materializes them into their new folders.

**Invariants (state in thesis, enforce in code):**
- **Source is `Inbox` only.** Never touch notes already under a real folder — that would fight the user's manual filing and the local-master `MoveAsync` semantics (`SyncMaintenanceEndpoints.cs:117-136`, which deliberately does *not* bump `updated_at` to avoid a pull echo). Re-route is the inverse: it *does* bump `updated_at` because we *want* the file to move.
- **Note stays `Ready` throughout.** Re-route changes `relative_path`/`transition_version` only; it must never flip `Status` out of `Ready`, or `DesiredAsync`'s `Status == Ready` filter (`:186`) would drop the note from the pull and the file would vanish. This is why the design is a **dedicated service, not a saga job** — it sidesteps the job-status↔note-status entanglement entirely and makes "essence untouched" true by construction (it never enters `Synthesizing`).
- **Essence preserved.** Only the file location changes; `BodyOutput`/Origin are not regenerated.

---

## #A — Cold-start auto-heal (automatic, narrow trigger)

Fires the backlog re-route **once**, automatically, only on the candidate-set transition **empty → non-empty** — i.e. the exact moment described: a fresh vault's first real folder appears. High value, low risk (a new vault's Inbox is small), self-limiting.

**Trigger seam — cloud-side, no plugin change:** in `RegisterFolderAsync` (`SyncMaintenanceEndpoints.cs:81`), after the upsert, compute whether the **candidate** count (same filter as `LoadCandidateFoldersAsync`, not the raw folder count) just went `0 → ≥1`. If so, poke an `InboxRerouteSignal` (singleton `Channel`, copied verbatim from `RelatedNotesCalibrationSignal`). An `InboxRerouteWorker` (`BackgroundService`, cooldown-gated) drains the signal and runs `InboxRerouteService`, then `pg_notify`s the orchestrator's pull channel so moved notes materialize (mirror `NotifyAsync`, `SyncMaintenanceEndpoints.cs:253`).

**Deliberately narrow:** subsequent folder additions (`1 → 2`, `2 → 3`, …) do **not** auto-fire. Re-shuffling the whole Inbox every time the user makes a folder would be surprising and churny. The incremental case is the user's job via #B. Folders registered slightly after the trigger are still seen by any re-route job that runs later (the service reads candidates live) — good-enough; #B covers the rest.

---

## #B — On-demand "Re-file Inbox" (manual triage)

The user-initiated path for the incremental case (user adds folders later and wants the existing Inbox pile reconsidered). Legible, no surprise, reuses the same service.

- **Endpoint:** `POST /api/sync/inbox/reroute` (in `SyncMaintenanceEndpoints`, behind `RequirePluginAuthFilter`). Runs `InboxRerouteService` **inline** and returns the `FolderDissolveResponse`-shaped `(affectedCount, desired[])` so the plugin gets immediate feedback + the move list. (Manual → synchronous return is better UX than fire-and-forget; #A stays async via the worker.)
- **Plugin:** a command + ribbon/panel action **"Re-file Inbox."** Route it through the **durable intent queue** (new `inbox_reroute` intent, `IntentExecutor` entry) for offline-safety and consistency with `folder_dissolve`/`folder_register` (`main.ts:500-524`). On success, `this.syncLoop.trigger()` + a `Notice("Thany: re-filing N note(s)…")` — identical to the shipped `folderDissolve` reroute handler (`main.ts:510-519`).

---

## #C — Consolidate the two entity folders (independent cleanup)

The empty-vault first impression today materializes **two** top-level entity folders with an inconsistent prefix: stubs → `Entities/` (`LlmIntelligenceOptions.cs:41`, `appsettings.json:26`), hubs → `_Entities/{Kind}/` (`HubMaterializer.cs:27`). Same concept, two roots, one looks system-ish and one looks like a routable project folder. Fold both under one `_Entities/` system tree:
- Change the `StubsFolder` default `"Entities"` → `"_Entities/Stubs"` (or move hubs to `_Entities/Hubs/{Kind}`; pick one, keep both under `_Entities/`). The routing exclusion already drops `_`-prefixed folders (`RoutingHandler.cs:183`), so a `_Entities`-rooted stubs folder is auto-excluded from routing candidates **without** the special-case `StubsFolder` exclusion at `:185-186` — net simplification.
- **No migration / backfill** (per `cloud_data_disposable`): wipe-and-reingest re-materializes stubs/hubs under the new layout. Old `Entities/` files orphan harmlessly on a throwaway vault.

---

## Tests

- `FolderRouter.DecideAsync`: extracted logic preserves `RoutingHandler` behavior (confidence gate, exact-match guard, null on no-match) — same assertions the routing path already has.
- `InboxRerouteService`: empty candidate set → no-op; only `Inbox/%` Ready notes are sourced (a note under a real folder is never touched); a note whose re-decision is still `null`/below-threshold is left unmoved with no `transition_version` bump; a moved note keeps its basename and `Status == Ready`; `RerouteMaxBatch` caps the batch and logs the remainder.
- #A trigger: registering a folder when candidates were `0` pokes the signal exactly once; registering when candidates were already `≥1` does **not**; `_`/`.`/`Inbox`/`StubsFolder` registrations don't count as candidates.
- #B endpoint: returns the `desired[]` move list; round-trips through the intent queue; re-running on an already-filed Inbox is a no-op (idempotent).

## Out of scope
- Scaffold-on-connect (rejected above).
- Re-routing notes already under real folders (locked invariant — Inbox only).
- Re-running synthesis/embedding during re-route (essence is preserved; only `relative_path` changes).
- Auto-firing on incremental folder additions (that's #B, user-initiated).

## Follow-ons
- `CLOUD-RELATED-NOTES-GRAPH` — unchanged; the shared-entity graph still feeds it. (Re-route uses the *folder* router, not the related-notes vector path — no interaction.)
