# CLOUD-RELATED-NOTES-ACTIONABLE — Insert-link action + threshold as a user-owned, graph-calibrated control

**Status:** SHIPPED (2026-06-05). #1, #2a, and #2b all landed; #2c (bounded calibration period) remains flagged. Follows `cloud-related-notes-realtime-handoff.md` (Steps 0–3 shipped: CLS pooling, cursor-block query, MMR, recency drop).

**As-built (deviations from the design below):**
- **#1** — `RelatedNotesPanel.onItemInsert` → "Insert link" in the pill detail; `BrowView.insertLink` + exported pure `buildFallbackWikilink`. `BrowView` ctor now takes `app: App` (was `workspace`).
- **#2b** — server `RelatedNotesOptions.MaxDistanceFloor/Ceiling`; plugin `related/strictness.ts` maps the dial: **Balanced omits `MaxDistance`** (server fills Auto ?? fallback), **Strict = 0.55**, **Loose = 0.85**; `relatedStrictness` setting + in-panel segmented control.
- **#2a** — built fully in-cloud; the `scripts/` offline calibration was built then **deleted** (no offline path left behind). Schema: `CloudSettings.RelatedNotesMaxDistanceAuto` + `RelatedNotesAuto{Note,Entity}Count` (migration `RelatedNotesAutoCalibration`). Pure core `RelatedNotesAutoCalibration` (`ResolveEffectiveMaxDistance`, `ShouldRecompute`, `ApplyHysteresis`, `RocAndYoudenThreshold`); `RelatedNotesCalibrationQueries` (raw SQL: shared-entity ∪ tag positives vs random negatives); `RelatedNotesCalibrator` (scoped: gate → sample → Youden → hysteresis → persist). Trigger = `RelatedNotesCalibrationSignal` (singleton `Channel`) poked by the endpoint each request → `RelatedNotesCalibrationWorker` (`BackgroundService`, cooldown-gated).
- **Chose always-background (non-blocking) over "inline if cheap"** — calibration never adds latency to the realtime endpoint. AUC logged at Info on recompute (the thesis metric). Config under `IngestSaga:Retrieval:Auto*`.
- **#2c** (bounded calibration period) — still flagged, not built.

Both items are plugin-/config-side — **no server retrieval-logic change** beyond one optional request param.

**Why these two:** retrieval is already fast and relevant, so the leverage moved from *finding* to *using* and *trusting*. #1 makes a surfaced thought actionable into the draft (closes the surface→act loop). #2 stops `MaxDistance` from being a hardcoded magic number — it becomes a defensible default (calibrated offline from the vault's own graph) plus a user-owned strictness control.

**Estimated effort:** #1 ~0.5 day (plugin-only); #2 ~1.5 days (per-user auto-calibration service ~1 + dial ~0.5). Independent — ship in either order.

---

## #1 — Insert-link action on related pills

**Goal:** one click drops a wikilink to a surfaced related note at the cursor in the draft.

**Approach:**
- Add `onItemInsert?: (item: RelatedNotesItem) => void` to `RelatedNotesPanelOptions` (mirrors the existing `onItemClick`). Panel stays pure-UI; `BrowView` owns the Obsidian integration.
- In `RelatedNotesPanel.renderItems`, add an **"Insert link"** button in the expanded detail beside the existing "Open note". Pill-click still expands; the action lives in the detail.
- In `BrowView`, implement `onItemInsert`: resolve the `TFile` from `item.relativePath`, then **`app.fileManager.generateMarkdownLink(file, currentNotePath)`** — *not* a hand-rolled `[[basename]]`, so it honors the vault's link settings (shortest/relative, wikilink vs md-link). Insert at cursor via `editor.replaceSelection(link)`. Fallback to `[[<basename without .md>]]` only when the file doesn't resolve locally (sync lag).
- After insert: keep the panel open, return focus to the editor. No modal/toast.

**Decisions (defaults):** link format via `generateMarkdownLink`; action in the detail (not a pill-click rebind); "insert quote" (pull `item.snippet`) is **out** (stretch follow-up).

**Note:** the inserted link lands in the captured draft body → preserved verbatim in Origin, and is strong user signal the synthesis can lean on. Feature, not side effect.

**Files:** `related/RelatedNotesPanel.ts` (+ callback-wiring test), `draft/BrowView.ts` (insert impl + a small pure `buildFallbackWikilink(relativePath)` helper for unit testing), `styles.css` (reuse `tm-related__open`).

**Out of scope:** quote insert, one-click pill insert, inserting anywhere but the active draft editor.

---

## #2 — `MaxDistance` as default-from-graph-labels + manual strictness dial

The cutoff is not a universal constant — it's a personal strictness preference. So: derive a defensible **default automatically, on the user's own cloud, from their own graph** (no dev UI, no distance shown to anyone), and expose a **user dial** to move from it. (Bounded feedback-calibration is a flagged v2.)

### #2a — Per-user auto-calibration (graph-weak-labels), on the user's own cloud

Invisible to the user. A small routine **inside Cloud.Api** computes, from the user's *own* graph, the separating distance between related and unrelated note pairs and writes it to `CloudSettings` as the **Auto default** the related endpoint reads (unless the dial overrides). Nothing leaves the user's cloud.

**Labels & metric:**
- **Positives** = note pairs the vault's structure says are related: *primary* **share a unified entity** (`Mentions` self-join `m1.entity_id = m2.entity_id AND m1.note_id < m2.note_id` — the cross-language-unified graph just shipped); *secondary* share a tag (`Note.Tags`). (Wikilink note→note edges only if a link table exists; shared-entity is the stronger signal regardless.)
- **Negatives** = random note pairs (sampled, positives excluded).
- Cosine **distance** via pgvector `<=>` over the stored CLS note vectors:
  ```sql
  SELECT n1.embedding <=> n2.embedding AS dist
    FROM mentions m1
    JOIN mentions m2 ON m1.entity_id = m2.entity_id AND m1.note_id < m2.note_id
    JOIN notes n1 ON n1.id = m1.note_id
    JOIN notes n2 ON n2.id = m2.note_id
   WHERE n1.embedding IS NOT NULL AND n2.embedding IS NOT NULL;
  ```
- Two distributions → pick the separating operating point (Youden's J / target precision); a small C# pass over the two distance arrays (or percentile SQL). Report AUC/separation for the thesis.

**When it runs — growth-proportional, no cron:**
- **Min-content gate** — skip until there are enough shared-entity pairs (and ready CLS-embedded notes) for the distributions to separate. Below it, the endpoint uses a **conservative shipped fallback** (`RelatedNotesOptions.MaxDistance`, a dev one-off is fine for *that* seed number).
- **Staleness on delta** — record the note/entity counts at last calibration; mark Auto stale once the vault grows by +N notes / +M entities since.
- **Lazy recompute** — the next related-query that sees a stale Auto kicks the recompute (inline if cheap, else a low-priority queued job). Stable vault → never recomputes; growing vault → re-tunes as it grows.
- **Hysteresis** — overwrite the stored Auto value only if the new number differs by a margin, so it doesn't wobble and silently change what the panel shows.
- **Manual override is sticky** — recompute drives only the *Auto* position; a user-moved dial stands until they reset to Auto.

**Effective threshold** = dial-override ?? `CloudSettings.RelatedNotesMaxDistanceAuto` ?? shipped fallback, clamped to [floor, ceiling].

**Caveats (state in the thesis, don't hide):**
- **Circularity:** these graph labels calibrate the *vector* threshold (independent signals — fine), but must **not** also validate the graph-fusion leg (`CLOUD-RELATED-NOTES-GRAPH`).
- **Weak-label noise:** shared-entity ≠ always semantically related; use the *aggregate* distribution, optionally weight by entity rarity. Don't over-fit per pair.
- **Cold start:** until the gate opens, users get the shipped fallback — so still set that number sensibly.

**Stored state:** `CloudSettings.RelatedNotesMaxDistanceAuto` (nullable until first calibration) + last-calibration counts for the staleness check.

**Files:** a Cloud.Api calibration service + `CloudSettings` columns + the staleness/lazy-trigger hook on the related path.

### #2b — Manual strictness dial (user-facing "correct it")

- Plugin setting / panel control: **Loose · Balanced · Strict** (Balanced = the calibrated default).
- Maps to a `maxDistance` value passed on the related request. `RelatedNotesEndpoint.RelatedNotesRequest` gains an optional `MaxDistance`, **clamped server-side** to a sane `[floor, ceiling]` so the client can't send junk (mirror the existing `Math.Clamp` on `k`). Add `MaxDistanceFloor`/`Ceiling` to options.
- Persist as a plugin setting (local, survives sessions). User can move it both ways anytime → no feedback-starvation, fully legible.

**Files:** `RelatedNotesOptions.cs` (shipped fallback default + floor/ceiling), `RelatedNotesEndpoint.cs` (optional clamped `MaxDistance`; effective = override ?? Auto ?? fallback), `api.ts` (pass-through), plugin settings + panel control + `BrowView` wiring.

### #2c — Bounded calibration period (FLAGGED v2 — not now)

The "corrects over usage time" version, done so it can't starve itself: for the first N interactions the panel **over-shows** (includes a band *below* the cutoff) with a one-tap relevant/not on those borderline items; after the window, fit `MaxDistance` to the user's judgments and settle, manual dial as permanent override. The deliberate over-show during the window is what defeats feedback-starvation. Build only if the default + dial prove insufficient.

**Explicitly not doing:** dev distance badge/log; pure implicit always-on adaptation (ignore ≠ negative; one-directional tightening starves itself).

---

## Tests

- #1: panel renders "Insert link" in detail and the click calls `onItemInsert(item)`; `buildFallbackWikilink` strips `.md`/path correctly.
- #2b: endpoint clamps an out-of-range `MaxDistance` to `[floor, ceiling]`; absent param falls back to the configured default; dial value round-trips through the request.
- #2a calibration service: separating-point pick from the two distance arrays; min-content gate → shipped fallback; staleness fires only past the growth delta; hysteresis suppresses sub-margin moves; manual override never clobbered. (AUC/separation reported, not asserted.)

## Follow-ons
- `CLOUD-RELATED-NOTES-GRAPH` — the one cheap retrieval *signal* still worth adding (shared-entity / co-link / same-folder, RRF-fused). Uses the same graph; do **not** evaluate it on the #2a labels.
- #2c bounded calibration period (above).
