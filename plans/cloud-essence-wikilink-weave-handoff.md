# CLOUD-ESSENCE-WIKILINK-WEAVE — Render the essence `wikilinks` array into the body

**Status:** SHIPPED 2026-06-05. `WikilinkWeaver` (inline + fallback-only) wired through `EssenceRenderer.Compose`; `RenderVersion = "essence-render-v2"` baked into `ComputeCacheKey`; prompt `:91` softened to stop encouraging model-side brackets. Full suite green (501 passed).

**The bug:** the synthesis schema **requires** a `wikilinks` array (`EssenceSchemas.cs:15-17`), the prompt tells the model to fill it (`SynthesisPromptBuilder.cs:89`), `Essence.cs` deserializes it on all four forms (`.Wikilinks`) — and then `EssenceRenderer` **throws it away**. Every `Render*` builds a body and calls `Compose(title, tags, budget, body)`; `.Wikilinks` is never referenced (`EssenceRenderer.cs`). The *only* path for `[[ ]]` to reach a note is the secondary instruction "Weave the wikilink targets inline … using [[ ]]" (`SynthesisPromptBuilder.cs:91`) — i.e. the model has to bracket inside the prose itself. Structured-output models reliably do the opposite: they populate the dedicated array and keep prose clean. Result: the array is full, the prose has no brackets, the renderer drops the array → **zero links**, deterministically. (Observed live on note `019e9922…`: tags rendered, "Thany-Marcus" sat in prose as plain text, no `[[ ]]`.)

**The fix:** make the renderer the single source of truth for bracketing — weave the `Wikilinks` array into the rendered body (first-occurrence inline bracketing + a fallback line for targets not present in the prose). Output becomes deterministic from the structured field instead of hostage to the model's inline-bracket discipline.

**Why this is the right layer:** wikilinks are independent of the entity registry (synth-LLM judgment, per the locked design) — so this is purely a render concern, no DB/graph dependency. Dangling links (no target note yet) are **intended and Obsidian-idiomatic**: they resolve when a matching note/hub/stub appears. On an empty vault they'll be unresolved until structure grows — acceptable, by design (and consistent with the cold-start story).

**Estimated effort:** ~0.5 day (pure helper + 4 one-line pass-throughs + cache-key token + tests).

---

## Core change — `WikilinkWeaver` (new, pure) + `EssenceRenderer.Compose`

New `Features/Processing/Synthesis/WikilinkWeaver.cs`: `string Weave(string body, IReadOnlyList<string>? targets, int max)`. `Compose` gains an `IReadOnlyList<string>? wikilinks` parameter; each `Render{Prose,Bullets,Checklist}` passes `essence.Wikilinks` straight through. (Table is special — see below.)

**Algorithm:**
1. **Normalise targets:** trim; strip stray `[`/`]`/leading `#`; drop empty; dedupe case-insensitively (keep first-seen casing for display); clamp to `max` (`EssenceBudget.MaxWikilinks = 12`).
2. **Detect already-linked spans:** scan `body` for existing `[[…]]`. Record (a) inner texts (case-insensitive) as already-satisfied targets, (b) span ranges so step 4 never brackets inside an existing link. (Handles the case where the model *did* inline some — no double-bracketing, no duplicate in the fallback.)
3. **Order by length desc** so `note-taking` is matched before `note` — prevents partial/nested bracketing of overlapping targets.
4. **Inline first occurrence:** for each target, find its **first literal** occurrence with **Unicode word boundaries** (char before & after is not `\p{L}\p{Nd}` — Cyrillic-safe for the bilingual vault), not inside any recorded link span. If found → wrap the matched substring as `[[<matched text>]]`, record the new span, mark satisfied. Bracket the **literal occurrence only** — `[[Thany-Marcus]]`, never `[[Canonical|display]]` (consistent with the locked native-alias resolution from `cloud-cross-language-unification-handoff.md`; Obsidian resolves case-insensitively).
5. **Fallback line:** any target neither found in prose nor already linked → append once, after the body: `\n\nRelated: [[A]] · [[B]]` (gated on non-empty). Guarantees the structured signal surfaces even when the model phrased the prose differently than the array entry — without this, a casing/wording mismatch silently drops the link again.

**Table form:** weaving the assembled `| … |` markdown is unsafe (could bracket a header or inject a stray `|`). v1: **Table uses the fallback line only** — append `Related: …` under the table, no inline weaving. (Inline per-cell weaving before `EscapeCell` is a v2 flag.) Prose/Bullets/Checklist get full inline weaving; their `- ` and `- [x]`/`- [ ]` markers never collide with word-boundary target matches.

---

## Cache invalidation — one token in `ComputeCacheKey`

`ComputeCacheKey(rawHash, modelTag, promptVersion, privacyMode, preset)` (`SynthesizingHandler.cs:287-295`) does **not** include any renderer version, so a pure renderer change won't bust cached essences — cache hits (`:89-106`) would replay the old link-less `finalBody`. Add a `RenderVersion` constant (e.g. `"essence-render-v2"`) into the key so the change takes effect deterministically on reprocess and new notes. (Per `cloud_data_disposable` old notes could instead be wipe-reingested, but the token is the clean, cheap, deterministic choice.)

---

## Prompt instruction — resolve the dual-channel conflict (recommended, bundled)

The prompt currently asks for **both** the `wikilinks` array (`:89`) *and* inline `[[ ]]` (`:91`) — the two fight, and the array loses today. With the renderer owning bracketing, make the array the single source of truth:
- **Keep** the `wikilinks` array instruction (`:89`) — now the load-bearing signal.
- **Drop/soften** the inline-bracket instruction (`:91`) → e.g. "Mention these naturally in the text; do not add brackets yourself." So the model writes clean prose and the renderer does all bracketing. (Pre-bracketed links are still handled gracefully by step 2 if the model adds any — this just stops encouraging them.)

This is a decision, not a hard dependency — the weaver is correct either way. Recommend taking it to remove the contradiction.

---

## Tests (`WikilinkWeaver` — the matrix)
- First occurrence bracketed; later occurrences of the same target left untouched.
- Target absent from prose → appears in the `Related:` fallback line (and only there).
- Target the model already inlined as `[[X]]` → not double-bracketed, not duplicated in fallback.
- Substring safety: `note` not bracketed inside `note-taking`; longer overlapping target wins (length-desc order).
- Unicode/Cyrillic target (`[[Київ]]`) brackets on word boundaries; punctuated/regex-metachar target matched literally (no regex injection).
- `MaxWikilinks` clamp; empty/whitespace dropped; case-insensitive dedupe.
- Table form: fallback line only, table markdown intact (no stray `[[` in headers/cells, no broken `|`).
- `EssenceRenderer` integration: a prose essence with `wikilinks:["Thany-Marcus"]` and the word in a sentence renders `[[Thany-Marcus]]`.

## Files
- NEW `Features/Processing/Synthesis/WikilinkWeaver.cs` (pure, partial-regex for the boundary scan, matching the file's existing `[GeneratedRegex]` style).
- `Features/Processing/Synthesis/EssenceRenderer.cs` — `Compose` signature + 3 pass-throughs; Table fallback-only path.
- `Features/Processing/Phases/SynthesizingHandler.cs` — `RenderVersion` into `ComputeCacheKey`.
- `Features/Processing/Synthesis/SynthesisPromptBuilder.cs` — soften `:91` (recommended).

## Out of scope
- Per-cell inline weaving for tables (v2).
- Stripping `[[ ]]` / `%% %%` syntax from the embedding input so wiki-markup doesn't dilute the stored vector (v2 — essence is the stored vector per `cloud-related-notes-realtime-handoff.md`; the brackets are a negligible, acceptable change to the embedded text for now).
- Resolving targets against existing notes/entities, or materialising target notes — **explicitly not** the weaver's job. Wikilinks are registry-independent; dangling links are intended. Hub/stub materialisation stays in the entity pipeline.
