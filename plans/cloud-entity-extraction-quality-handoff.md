# CLOUD-ENTITY-EXTRACTION-QUALITY — Fix entity suggestions: over-merge, extraction noise, alias pollution

**Status:** DESIGN — not started. Sibling of `cloud-related-threshold-rescale-handoff.md` (shares the compressed-embedding-cone root cause). Touches `ExtractingEntitiesHandler`, `EntitySuggestionAggregator`, `ThresholdsOptions`, the extraction prompt/DTO.

**The bug (measured live):** seeded 3 notes that repeatedly name **Sarah Chen** (person), **Project Atlas**, **OpenAI** (org), **Berlin** (place). The only suggestion that surfaced was a **garbage blob**: `"zero stock picking"` (a *concept* from an unrelated finance note, occ=4/notes=3) whose **aliases** were `["She wants the OpenAI integration shipped before the Berlin offsite.", "Project Atlas", "OpenAI's embeddings API"]`. The real entities never surfaced as themselves; Sarah Chen stalled at occ=2.

Three compounding defects:

| # | defect | where |
|---|---|---|
| A | **Over-merge** — distinct entities collapse into one blob | `EntitySuggestionAggregator.ResolveExistingAsync:120-132` gate `SuggestionMatchDistance=0.25`; gray-zone `0.45` |
| B | **Extraction noise** — sentences & generic noun phrases extracted as entities, kinds mis-assigned | LLM `extract:v1` prompt + `MentionCandidateDto` |
| C | **Alias pollution** — raw `anchor_text` (a sentence) stored as an alias | `AppendOccurrence:152-157` (and `candidate.Aliases` at `:60-64`) |

The through-line: A is the **same compressed/anisotropic cone** that made `MaxDistance 0.7` admit car washes — distinct entities sit ~0.20 apart, so a 0.25 merge gate fuses them. B and C are independent quality bugs no threshold tuning fixes.

**Estimated effort:** Part 2 (the shared guard) ~0.5d and is the load-bearing fix; Parts 1/3/4 ~0.5d each, independent.

---

## Part 1 — Re-scale the entity dedup gates (the over-merge)

`ThresholdsOptions` (`LlmIntelligenceOptions.cs:25`) **and `appsettings.json:10-14`** (deployed source of truth — code default alone won't take effect; this is the same trap that left the related-notes fix un-shipped):

| gate | current | direction | role |
|---|---|---|---|
| `SuggestionMatchDistance` | 0.25 | **tighten (~0.10–0.12)** | auto-merge a mention into an existing suggestion/entity |
| `GrayZoneMergeMaxDistance` | 0.45 | **tighten (~0.15–0.18)** | propose a cross-language merge for user confirmation |

**Caveat — don't reuse the related-notes numbers blindly.** Entity embeddings are `EmbedCandidate` (canonical + description + mention contexts), a *different recipe* than note-essence vectors, so their distance scale may differ. The 0.25 gate is *demonstrably* too loose (it merged `Project Atlas` into a finance concept), but the exact tightened value needs an **entity-distance sweep**: measure same-entity surface-form / cross-language pairs (e.g. `Київ`↔`Kyiv`, `Sarah Chen`↔`Sarah`) vs distinct-entity pairs, and set the gates so they (a) auto-merge true same-entity, (b) gray-zone-propose genuine cross-language candidates, (c) never merge distinct entities. **This gate also drives the cross-language merge-proposal flow** (`xlang_entity_unification`) — tightening must not silence legitimate `Київ`/`Kyiv` proposals. Tighten to the interim values now to stop the bleeding; pin by sweep.

Note: Part 2's guard *also* relieves merge pressure — cleaner canonicals embed more distinctly than sentence-blobs, so distinct entities separate better once the garbage is gone.

---

## Part 2 — `LooksLikeName` guard (fixes B's worst case + C; do first)

One pure predicate, reused at two sites:

```
LooksLikeName(s) := trimmed; 1–4 tokens; ≤ ~40 chars;
                    no sentence punctuation (. ? ! ; :);
                    contains ≥1 capitalized token (Latin or Cyrillic capital)
```

**Site 1 — candidate filter** (`ExtractingEntitiesHandler` ~`:102`, alongside `m.Confidence >= MentionMin`): drop any mention whose `CandidateCanonical` fails `LooksLikeName`. The **capitalized-token rule is the decisive one** — on the live test it cleanly separates signal from noise:

| canonical | capital? | verdict |
|---|---|---|
| Sarah Chen · Project Atlas · OpenAI · Berlin | ✓ | keep |
| zero stock picking · automatic monthly contributions · low-cost broad-market index funds | ✗ | **drop** |

Works for uk+en (both capitalize proper nouns; common nouns are lowercase). It trades a little `concept` recall for precision — and `concept`/`other` are exactly where the over-extraction lives, so that's the right trade. Make the capital requirement relaxable per-kind if named-concept linking is wanted later.

**Site 2 — alias guard** (`AppendOccurrence:152-157` for `anchor`, and the `candidate.Aliases` projection at `:60-64`): only append a string to `Aliases` if it passes `LooksLikeName`. This keeps the *intended* behavior — capturing real surface variants like `Київ` for canonical `Kyiv` (the cross-language alias-seeding) — while rejecting the sentence-anchors the LLM currently emits. The anchor still lands in the `Occurrence` record (for surrounding-text context); it just no longer pollutes aliases.

Pure function, fully unit-testable, model-independent. Alone, this would have made the test produce Sarah Chen / Project Atlas / OpenAI / Berlin correctly.

---

## Part 3 — Constrained extraction schema (cut garbage at the source, if supported)

Extraction calls `llm.CompleteAsync<EntityExtractionDto>(...)` with **no response schema** (synthesis, by contrast, passes `ResponseSchema` built by `EssenceSchemas`). If `CompleteAsync<T>` can carry a JSON-schema constraint, add one for the mention DTO:
- `candidate_kind` → **enum** (`person|organization|place|concept|other`)
- `anchor_text` `maxLength ~40`, `candidate_canonical` `maxLength ~60`, `aliases[]` items `maxLength ~40`

Then the decoder *physically cannot* emit a sentence as an anchor/alias. If the extraction path doesn't support response schemas, this is moot — Part 2's post-parse guard enforces the same caps deterministically and is the real backstop.

---

## Part 4 — Tighten the extraction prompt (`extract:v1`, `PromptTemplates.cs:36`)

- Extract **only proper-noun named entities** — specific people, organizations, places, named products/projects.
- Add **negative examples**: "do NOT extract generic noun phrases, activities, or descriptions — not *index funds*, not *monthly contributions*, not *stock picking*."
- **Define the anchor precisely**: "the 1–4 word span where the name appears, never the surrounding sentence," with one worked example.
- **Narrow `concept`/`other`**: named concepts only (e.g. *Zettelkasten*, *Bauhaus*) — not common-noun topics like *note-taking*. Consider dropping `other` entirely.

---

## Tests
- `LooksLikeName`: accepts `Київ`, `OpenAI`, `Sarah Chen`, `S. Chen`; rejects the three garbage canonicals, sentences (terminal punctuation / >4 tokens), and all-lowercase common-noun phrases. Unicode-capital aware.
- Candidate filter: a no-capital / sentence-length mention below the guard is dropped before aggregation.
- Alias guard: a sentence `anchor_text` is **not** appended to `Aliases` but **is** retained in the `Occurrence`; a clean surface form (`Київ`) **is** appended.
- Gate re-scale: `ResolveExistingAsync` does **not** merge two candidates ~0.2 apart at the tightened `SuggestionMatchDistance`; a same-entity surface-form pair within it still merges. (Pin thresholds with the entity-distance sweep; assert the relation, report the numbers.)
- Regression (the live blob): the 3-note Sarah-Chen corpus yields **separate** surfaced suggestions for Sarah Chen / Project Atlas / OpenAI, not one `zero stock picking` blob.

## Files
- `Infrastructure/Llm/LlmIntelligenceOptions.cs` (`ThresholdsOptions`) + **`appsettings.json:10-14`** — gate values (Part 1).
- NEW `Features/EntitySuggestions/EntityNameHeuristic.cs` (`LooksLikeName`, pure) — Part 2.
- `Features/Processing/Phases/ExtractingEntitiesHandler.cs` (~`:102`) — candidate filter (Part 2 site 1).
- `Features/EntitySuggestions/EntitySuggestionAggregator.cs` (`:60-64`, `:152-157`) — alias guard (Part 2 site 2).
- `Infrastructure/Llm/Prompts/PromptTemplates.cs:36` + (if supported) the `CompleteAsync` schema — Parts 3/4.

## Recommendation / ordering
**Part 2 first** (one shared predicate — biggest quality jump per effort, deterministic, model-independent), then **Part 1** (stops over-merge; pin via sweep), then **Parts 4 → 3** (reduce garbage at the source). Each is independently shippable.

## Out of scope
- **Whitening / mean-centering** the embedding space (the structural cure for the compressed cone that also helps related-notes) → `CLOUD-RELATED-WHITENING`.
- A full entity-extraction eval harness / model swap (the local Qwen is weak at NER; prompt + guard + schema are the pragmatic path for private mode).
- The entity-distance sweep itself is a prerequisite *measurement* for Part 1's exact numbers, not a code deliverable.

## Model-dependence
The gates and the embedding behavior are specific to **Granite-r2 CLS / 256-dim / L2** and the **local Qwen** extractor. Re-measure if either changes.
