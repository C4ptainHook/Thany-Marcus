# CLOUD-RELATED-THRESHOLD-RESCALE — Re-scale MaxDistance to the model's real distance range

**Status:** DESIGN — not started. Corrects the threshold constants shipped in `cloud-related-notes-actionable-handoff.md` (#2/#2a/#2b); builds on the realtime work (`cloud-related-notes-realtime-handoff.md`).

**The bug (measured live):** the `MaxDistance` band is ~4–5× too loose for this embedding model. A note about *"car wash and my bugatti"* surfaced two PKM notes as "related" because its distance to them (~0.20) is far below the `0.7` default. The constants were guessed against an imagined 0–1 spread; the model (Granite-r2 CLS, 256-dim, L2-normalized) actually packs everything into a narrow cone.

## Evidence — distance sweep (8 notes across 4 topic clusters, 15 queries)

**Doc→doc** (what auto-calibration measures):
- Related (within-cluster): **0.072 – 0.107**
- Unrelated (cross-cluster): **0.114 – 0.207** → clean separation ~**0.11**

**Query→doc** (what the live panel measures):
| | min | median | p90 | max |
|---|---|---|---|---|
| Related (n=19) | 0.064 | 0.116 | 0.144 | 0.184 |
| Unrelated (n=101) | 0.150 | 0.229 | 0.264 | 0.288 |

Knee ≈ **0.15** (related p90 0.144 vs unrelated min 0.150). Every off-topic query's *nearest* hit (car wash 0.190, weather 0.179, physics 0.208, cat 0.214, road-trip 0.227, Rome 0.230) sits above 0.15. A 0.15 cutoff keeps ~90% of genuine matches and drops ~100% of the junk; at 0.7 everything passes.

**Caveat:** small sample — treat decimals as ±0.02. The *shape* is unambiguous; per-vault auto-calibration (#2a) is the real long-term answer, and all values are config-tunable without a code change.

---

## Part 1 — Re-scale the constants (primary; this alone fixes the bug)

`RelatedNotesOptions.cs` (and **`appsettings.json` under `IngestSaga:Retrieval`** — the deployed cloud reads appsettings, so the code default alone won't take effect):

| knob | current | **new** | basis |
|---|---|---|---|
| `MaxDistance` (default/Balanced fallback) | 0.70 | **0.15** | the knee — ~90% recall, ~0 FP |
| `MaxDistanceFloor` | 0.40 | **0.10** | must sit *below* the real separating point so it can't clamp a correct Auto value up (today 0.40 clamps any sane value up → defeats calibration) |
| `MaxDistanceCeiling` | 0.90 | **0.28** | unrelated max is 0.288; above = pure noise |

Plugin `related/strictness.ts`:
| dial | current | **new** |
|---|---|---|
| Strict | 0.55 | **0.12** (core related only, ~median, precision-max) |
| Balanced | *(omits → server Auto ?? fallback)* | **unchanged** (now resolves to ~0.15) |
| Loose | 0.85 | **0.19** (includes the overlap tail; some FP) |

No logic change — `ResolveEffectiveMaxDistance` already reads these from options and `Math.Clamp(chosen, floor, ceiling)`. Only the *values* move. Once floor is 0.10, the clamp window finally contains the real operating range, so the dial and Auto can actually bite.

---

## Part 2 — Fix the doc→doc vs query→doc offset (calibration correctness)

The auto-calibrator (`RelatedNotesCalibrationQueries`) computes its separating point from **shared-entity/tag positives vs random negatives** — i.e. **doc→doc** pairs (stored `embedding <=> embedding`), which separate at ~**0.11**. But the endpoint applies that stored `RelatedNotesMaxDistanceAuto` to **both** paths, and the **body path** (live panel) measures **query→doc** distances that run ~**0.03–0.04 higher** (the draft text is shorter/noisier than the stored essence — the query/doc asymmetry flagged in the realtime handoff). So a value calibrated on doc-doc (~0.11) applied to the body path would cut off genuine matches above 0.11 — and the body-path related *median* is already 0.116. It would silently under-retrieve.

**Fix:** add `RelatedNotesOptions.QueryDocOffset` (≈ **0.04**). Apply it so the stored Auto lives on the **query-doc scale** (the live panel is the headline use):
- In `RelatedNotesCalibrator`, after `RocAndYoudenThreshold` returns the doc-doc point, persist `RelatedNotesMaxDistanceAuto = youden + QueryDocOffset` (then hysteresis/clamp as today).
- Body path: `effective = requested ?? auto ?? default`, no per-read offset (dial values 0.12/0.19 are already query-doc-scaled).
- **`noteId` path** (stored-vs-stored = doc-doc): now runs ~`QueryDocOffset` looser than ideal. Acceptable for v1 (that view is "related to this saved note" — no live-typing FP pressure). Flag subtracting the offset there as a later refinement.

**Refinement (flag, not now):** the calibrator could *measure* the offset per vault instead of using a constant — for a sample of notes, re-embed `body_input` (query-like) and compare to the stored essence vector. More accurate, but extra embed calls; the constant is fine to start.

---

## Part 3 — Widen the cone (structural follow-up; SEPARATE ticket)

The ~0.20 floor for *unrelated* text is **anisotropy**: CLS embeddings cluster around a dominant mean direction, so even random pairs look ~78% cosine-similar, leaving only a thin [0.10, 0.28] valid band and a knife-edge threshold. **Mean-centering** (subtract the corpus mean before cosine) or **whitening** would spread the cone and widen the related↔unrelated gap, making the threshold far less brittle and the per-vault calibration more robust. This is a real change to how vectors are compared (store/subtract a running corpus centroid; affects both stored and query vectors) → its own ticket `CLOUD-RELATED-WHITENING`. Not in this handoff.

---

## Tests
- `ResolveEffectiveMaxDistance`: clamps to new `[0.10, 0.28]`; Auto and dial values within range pass through; an out-of-range request clamps.
- **Regression (the bug):** with default 0.15, a candidate at distance 0.20 is excluded (0 items); a candidate at 0.10 is included. (Mirrors car-wash 0.20 vs related 0.10.)
- Strictness map round-trips: Strict 0.12 / Loose 0.19 / Balanced omits → server fills Auto ?? 0.15.
- Calibrator: persisted `RelatedNotesMaxDistanceAuto` = doc-doc Youden + `QueryDocOffset`; hysteresis still suppresses sub-margin moves; min-content gate still falls back to the (now 0.15) default.

## Files
- `Features/Ingest/RelatedNotesOptions.cs` — MaxDistance/Floor/Ceiling values + new `QueryDocOffset`.
- `ThanyMarcus.Cloud.Api/appsettings.json` (`IngestSaga:Retrieval:*`) — **mirror the new values** (deployed source of truth).
- `plugin/thany-marcus/src/related/strictness.ts` — Strict 0.12, Loose 0.19.
- `Features/Ingest/RelatedNotesCalibrator.cs` — add `QueryDocOffset` to the persisted Auto value.
- (no change) `RelatedNotesAutoCalibration.ResolveEffectiveMaxDistance` — reads the new option values.

## Out of scope
- Mean-centering/whitening (Part 3 → `CLOUD-RELATED-WHITENING`).
- Per-vault empirical offset measurement (flagged refinement in Part 2).
- Re-sweeping with a larger corpus — the constants are config-tunable and auto-calibration refines per vault; the current numbers are a defensible, conservative start.

## Note on model-dependence
These constants are specific to **Granite-embedding-311m-multilingual-r2, CLS pooling, 256-dim Matryoshka, L2-normalized**. If the embedding model, pooling, or dim changes, the distance scale shifts and this must be re-swept. State that next to the constants so they aren't mistaken for universal.
