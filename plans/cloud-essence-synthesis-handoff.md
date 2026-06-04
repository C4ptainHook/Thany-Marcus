# CLOUD-ESSENCE-SYNTHESIS — Non-destructive "essence-on-top" note redesign — Handoff Brief

Date: 2026-06-03 (v1 implemented 2026-06-04)
Status: **v1 shipped.** Design grilling complete; both forks resolved at implementation time:
- **Origin frozen vs living → living-ready shape.** v1 bakes the `%% thany:essence %%` / `%% thany:sources %%` / `%% thany:processing %%` managed fences into `finalBody` (so `BodyOutput`'s shape is not reshaped twice), but Origin is still re-rendered from immutable `note.BodyInput` each synthesis and `thany_locked` stays `true`. The plugin write-back path that makes Origin truly *editable* remains a separate ticket; when it lands it flips `thany_locked` and rewrites only inside the fences.
- **v1 form scope → full adaptive (4 forms).** The plan's "Recommended v1 cut: defer ②" contradicted decision #5a, the Mode split (which lists the form router as 0-pass), and the "guaranteed adaptive form" goal. Resolved in favour of shipping `FormRouter` (a pure heuristic, zero extra inference passes) + all four concrete per-form schemas/renderers (prose/bullets/checklist/table). The constrained-enum-decode *fallback* for form selection is what stays deferred.

This handoff reshapes what a synthesized note *looks like* and how its body is *produced*. Today `SynthesizingHandler` feeds the user's prose to an LLM, the LLM writes an entirely new body, and the user's original text is demoted into a collapsed `> [!source]- User notes` callout under `## Sources`. Nothing is deleted, but the canonical body the user opens in Obsidian is the machine's rewrite and their own words are folded away at the bottom — which reads, emotionally, as "the AI overwrote my note." This ticket inverts that: the **essence (a distilled, connective layer) goes on top**, the user's **verbatim text becomes its own visible `## Origin` zone**, attachments stay collapsed in `## Sources`, and all processing/provenance metadata (model, mode, prompt version, timestamps) moves to a **single collapsed callout at the very bottom**. The synthesis call also stops emitting free-form Markdown and instead emits a **typed essence object under a grammar/JSON-schema constraint**, which the server renders to Markdown deterministically — guaranteeing a valid form (prose / bullets / checklist / table) and letting the schema enforce a hard "rememberable" size ceiling by compression rather than truncation.

**Goal:** change the synthesized note from *AI-canonical, original-demoted* to *source-of-truth-preserved, essence-layered-on-top*, and replace prompt-stated length/format with mechanism-enforced length/format. After this ticket: (a) the first thing a user reads is the essence H1 + inline `#tags` + connective `[[wikilinks]]`; (b) their verbatim capture lives untouched under `## Origin`; (c) attachment extractions stay in collapsed `## Sources`; (d) model/mode/prompt-version live in a collapsed `> [!info]- Processing details` at the bottom; (e) the essence's *form* is structurally valid (constrained decoding) and its *length* is proportional to input with a hard, compression-enforced ceiling.

**Does NOT ship (clean drop-ins against the seams this lands):**
- Salience pre-selection (extract-then-abstract stage 1). v1 abstracts directly from full inputs; salience is a v2 token/faithfulness win.
- The verify-and-revise loop (self-refine / faithfulness critic). Deferred; gate to public mode when it lands (extra inference passes are not affordable on local CPU droplets).
- Hierarchical / recursive pre-summarization for over-context inputs (long voice transcripts). Conditional, deferred.
- Constrained decoding for public providers **other than Google Gemini** — the router only wires `google` in public mode (others throw), so v1's structured-output guarantee covers exactly Ollama (private) + Gemini (public). Adding a provider is a new `ISynthesisLlmClient` + router arm + its own schema serialization.

## Where decisions live (read before doing anything)

- **`src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/SynthesizingHandler.cs`** — the phase this ticket rewrites. `HandleAsync` line 56; cache key built at line 78 (already includes `promptVersion` — a version bump auto-invalidates and reissues every note); `SynthesisRequest` built lines 114–120 with `DefaultMaxOutputTokens = 2048` (line 23); `SourcesRenderer.Render(note.BodyInput, topLevel)` at line 159; `FrontmatterBuilder.BuildSynthesis(...)` at line 168; the `finalBody = $"---\n{frontmatter}---\n\n{body.TrimEnd()}\n\n{sources}"` assembly at **line 169** — this is the line that changes most.
- **`src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SynthesisPresetBodies.cs`** — the four presets (`Zettelkasten`, `Journal`, `Encyclopedic`, `Technical`) + `CommonGuardrails`. The hardcoded **"Aim for 4–10 sentences"** prose (lines 26, 32, 37) is exactly the prompt-stated length lever this ticket replaces with a computed budget. Versions are `preset-*-v2`; bump to `-v3` to reissue.
- **`src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SynthesisPromptBuilder.cs`** — builds a flat string prompt today (note the trailing `/no_think`, i.e. local model is Qwen-class → supports llama.cpp GBNF grammars). v1 adds the structured-output schema instruction here.
- **`src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SourcesRenderer.cs`** — `Render(string? userBody, ...)` currently prepends the user's notes via `AppendUserNotesCallout` (lines 28–33, 208–212). v1 **drops the `userBody` arg and that callout** — Origin now owns the verbatim text.
- **`FrontmatterBuilder.BuildSynthesis` + `SynthesisFrontmatterFields`** (referenced from the handler at line 160-168; carries `PrivacyMode, Model, PromptVersion, Seed, SynthesizedAt, Status, Error`) — v1 slims the top frontmatter to sync-only fields and routes the rest into a new bottom-callout renderer.
- **`Infrastructure/Llm/Synthesis/{ISynthesisLlmClient,OllamaSynthesisLlmClient,GoogleGeminiClient,SynthesisLlmRouter}.cs`** — **read 2026-06-03; both transports support structured output (see "Transports" below).** `SynthesisRequest` is `(Prompt, Model, ApiKey, Seed, Temperature, MaxOutputTokens)`; `SynthesisResponse` carries `Body` (string). Router: private → `OllamaSynthesisLlmClient` (`qwen3:1.7b-q4_K_M`), public → `GoogleGeminiClient` only (others throw `unsupported provider`).
- **Memory `composite_ingest_decision.md`** — one composite draft (body + N attachments) → one processed note. The layout change is within that single processed note; no new note rows.
- **Memory `feedback_wikilinks_independent.md`** — body wikilinks are synth-LLM judgment over core ideas (not gated on an entity registry). Consistent here: the essence carries the `[[wikilinks]]`; Origin stays link-free verbatim.
- **Memory `feedback_no_code_comments.md`** — new renderers (`ProcessingDetailsRenderer`, the essence Markdown renderer, `EssenceBudget`, `FormRouter`) carry **no narrative/section-banner comments**; only non-obvious *why* (e.g. the one line explaining `MaxOutputTokens` is a safety net above the real ceiling).
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3 (on Microsoft.Testing.Platform — run `dotnet test --project <csproj> -- --filter-class "*Name"`, no VSTest `--filter`/`--nologo`).
- **Memory `thesis_context.md`** — this redesign is thesis-relevant; the technique families below are the citable "beyond prompting" contribution.

## The trust problem this fixes

Forte's *Building a Second Brain* (2022) Progressive Summarization is **non-destructive by design**: distillation is layered *on top of* the source (bold → highlight → executive summary), never a replacement, so you can always drop to the original. Today's pipeline does the opposite — it treats distillation as replacement (synthesis-first), which is the precise failure mode Progressive Summarization was invented to prevent. This ticket makes the user's words the source of truth and the essence a derived view above them.

## Target note layout (the spec)

```markdown
---
thany_note_id: …
thany_updated_at: …
thany_locked: false
---
# Essence title                         ← first content the user reads
#focus #music-and-work                  ← tags, inline, atop the essence (NOT in frontmatter)
Essence body with [[wikilinks]]…        ← form is content-driven (see Essence behavior)
---
## Origin
[note.BodyInput verbatim — never touched by the model]
## Sources                              ← collapsed [!source]- callouts, attachments only
> [!source]- 🎵 Voice — ![[memo.wav]]
> transcript…
> [!source]- ▶ URL — [title](url)
> extract…
> [!info]- Processing details          ← collapsed; model/mode/version live HERE
> Model: qwen… · private · preset zettelkasten v3 · seed 42 · synthesized 2026-06-03 · ok
```

Frontmatter holds **only machine sync fields** (no `tags`, no model/mode). Rationale: Obsidian requires frontmatter to be the literal top of the file, so provenance cannot physically move down *as frontmatter* — but it can move down as a callout. A frontmatter strip with zero user-facing fields can be hidden entirely via Obsidian's *Settings → Properties in document → Hidden* while the sync id still lives in the file. Demoting provenance is **lossless**: the authoritative copy already exists in the cloud's LLM event log / DB.

## Decisions resolved in the 2026-06-03 grilling

Recorded so the next reader does not re-litigate.

1. **Source-first, essence-on-top.** The canonical, first-read content is the essence; the user's verbatim capture is preserved as a distinct `## Origin` zone, not demoted into `## Sources`. (Was the open values fork; resolved source-of-truth = user's words.)
2. **Essence size is proportional to input, not fixed.** Budget `B = tokens(BodyInput) + Σ tokens(attachment extractions)`. Thin input (e.g. one line + one screenshot) → up to ~3 sentences; rich input (long prose + voice transcript + several attachments) → more. Always a *compression* of `B`, never a re-narration.
3. **Hard ceiling enforced by compression, never truncation.** A tighter ceiling means *distill harder / select fewer points*, never a chopped sentence or trailing ellipsis. North star is **"rememberable at a glance"** — the ceiling is a glanceability budget, so it is **form-agnostic** (a table and a paragraph fit the same budget). `MaxOutputTokens` (2048) stays as a safety net *above* the ceiling; the real ceiling lives in the essence schema (max rows/bullets/sentences).
4. **Essence form is free / content-driven, by rule.** Pick the *lightest* form that makes the essence rememberable: prose by default; bullets for discrete points; checklist when there are actions/todos; table only when items share columns. Form serves memory, never decoration. Rich-end essence may use bullets but **not `##` sub-headings** (that drifts back into a multi-section document, which is what `encyclopedic`/`technical` presets are for).
5. **Connective layer is constant across forms:** `#tags` inline atop the essence + `[[wikilinks]]` woven through. Wikilinks live only in the essence (Origin stays verbatim/link-free).
5a. **Form-first, one concrete schema per form — NO polymorphic `content` union.** The transport read forces this: a 1.7B local model and Gemini's responseSchema both handle `oneOf`/discriminated unions poorly. So `FormRouter` (②) picks the form *before* generation, and the handler hands the transport **one of four concrete, non-union schemas** (prose / bullets / checklist / table). This is more reliable on both transports and makes the ceiling caps (`maxItems`/`maxLength`) trivial per form.
6. **Processing metadata → collapsed bottom callout.** `> [!info]- Processing details`. Lossless demotion (authoritative copy in the cloud DB/event log).
7. **Frontmatter slims to sync-only fields** (`thany_note_id`, `thany_updated_at`, `thany_locked`). Tags move inline into the essence; model/mode/version move to the bottom callout.
8. **`## Sources` stops swallowing user notes** — `SourcesRenderer` loses its `userBody` argument.
9. **Beyond prompting:** the v1 form/length guarantees come from **constrained decoding + a computed budget**, not from asking the model nicely (prompt-stated length fails ~50% even on GPT-4o — see Techniques). Verify-revise loop, salience, and hierarchical summarization are deferred (see "Does NOT ship").

## Open fork — RESOLVED 2026-06-04 (living-ready shape; see Status above)

**Is `## Origin` frozen or living?** Resolved to **living-ready shape**: fences are in `finalBody` now, Origin re-renders from immutable `BodyInput` and `thany_locked` stays `true` until the plugin write-back path lands. Original analysis kept below.
- **Frozen** (simplest): Origin is re-rendered verbatim from `note.BodyInput` on every synthesis; the user never edits it in Obsidian. No fences needed; reprocess can keep clobbering `BodyOutput` because Origin is sourced from the immutable `BodyInput`.
- **Living** (user leaned this way in the grilling): the user keeps editing Origin in Obsidian and re-distills the essence from it. Requires a **managed-fence contract** — the model owns only the essence + sources + processing-details regions (e.g. `%% thany:essence %% … %% /thany:essence %%`), is forbidden to touch text outside the fences, and `thany_locked` can drop to `false`. This also requires a plugin write-back path so edited Origin flows back to the cloud, and reprocess must rewrite *only inside the fences* instead of clobbering all of `BodyOutput`.

If living is firm, the fence markers go into the v1 assembly so `BodyOutput`'s shape is not reworked twice. **This is the gating decision.**

## Essence behavior spec (for the schema + prompt)

- **Placement:** top, above `## Origin`.
- **Size:** `target ∝ B`, clamped to `[~1 sentence … rememberable-at-a-glance]`. Compression invariant: essence is always materially shorter than `B`.
- **Ceiling:** hard, enforced by the schema (`maxItems`/`maxLength`) + re-distill on overflow; never `MaxOutputTokens` truncation.
- **Form:** enum `prose | bullets | checklist | table`, content-driven per decision #4.
- **Connective layer:** `tags[]` (rendered inline atop), `wikilinks[]` (woven into the body).

## Techniques beyond prompting (thesis grounding + implementation menu)

Baseline to beat: prompt-stated length/format is unreliable — even GPT-4o fails length constraints ~50% of the time and struggles with extractiveness (controllability survey).

| Requirement | Technique family | Key refs | Cost / mode |
|---|---|---|---|
| Proportional length | control signals (length ← #keywords/units); zero-shot & black-box length control | CTRLsum (He et al. 2020), GSum (Dou et al. 2021); black-box (2412.14656), zero-shot (2501.00233); LCPO/L1 COLM 2025 (2503.04697, training-time, frontier) | budget = free; runs everywhere |
| Ceiling = compress-not-truncate + faithful + proportional | extract-then-abstract (select salient units ∝ B, then abstract) | Bottom-Up (Gehrmann 2018, 1808.10792), SEASON/salience allocation (2210.12330), entity-driven (1909.02059) | cheap; *reduces* tokens (CPU win); **v2** |
| Valid adaptive form (table/checklist/bullets) | structured output via JSON-schema-constrained decoding | concept: XGrammar (MLC 2024), JSONSchemaBench (2501.10868). **This codebase's actual levers:** Ollama `format`=JSON-schema (local), Gemini `generationConfig.responseSchema` (public) | decode overhead, **0 extra passes**; runs in both modes; **v1** |
| Faithfulness + ceiling enforcement | verify-and-revise loops | Self-Refine (Madaan 2023), Chain-of-Verification (Dhuliawala 2023), Reflexion (Shinn 2023); hallucination-detection-guided refinement (Nature 2025 s41598-025-31075-1; 2512.05387) | +1…N passes; **gate to public / cap local**; v2+ |
| Long input (long voice memo) | recursive / hierarchical (map-reduce, merge) | Context-Aware Hierarchical Merging (2502.00977); OpenAI recursive book summarization (Wu 2021) | multi-pass, conditional; computational analog of Progressive Summarization; **deferred** |

## Implementation plan

### Pipeline shape (inside `SynthesizingHandler.HandleAsync`)
`BuildInputs` → **① compute `EssenceBudget`** (pure fn over `B`) → **② FormRouter.Pick** (heuristic over inputs; constrained-enum decode if heuristic too crude) → **③ constrained generate** (`llm.CompleteAsync` with a grammar/schema; emits typed essence object) → **render essence Markdown deterministically** → assemble `finalBody`.

### Mode split (the practical constraint)
Default DigitalOcean droplet is CPU-bound (`s-4vcpu-8gb`), so local LLM inference is slow and every extra pass hurts.
- **Runs everywhere, 0 extra passes:** budget→length (①), form router (②), constrained decoding (③), and the whole layout/metadata restructure.
- **Gate to public (API) mode or hard-cap locally:** the verify-revise loop (≤1 pass on local, triggered only by a *deterministic* check — over-budget token count, wikilink syntax validity, "every input represented"; full faithfulness critic only on public mode) and hierarchical pre-summarization (only when `B > context budget`).

### Recommended v1 cut
Ship ③ + ① + the layout/metadata restructure. That delivers guaranteed adaptive form + structural ceiling + proportional length + the non-destructive layout, with **zero extra inference passes**, so it behaves identically on a local droplet and on a user's API key. Defer ②, verify-revise, and hierarchical to v2.

### Transports (read & verified 2026-06-03)

Both support JSON-schema structured output; the field differs per client. `SynthesisResponse.Body` stays a **string** — it now carries JSON text, and the **handler** deserializes + renders (transports stay "dumb", matching the existing handler-owns-rendering split).

- **Local — `OllamaSynthesisLlmClient` (`qwen3:1.7b-q4_K_M`)** POSTs `/api/generate` with `{model, prompt, stream:false, think:false, options:{…, num_predict=MaxOutputTokens}}`. **Add a top-level `format` field = the chosen per-form JSON Schema** (sibling of `prompt`/`stream`, NOT inside `options`). Ollama masks invalid tokens → structural validity is *guaranteed* even on a 1.7B model. Two musts confirmed in Ollama's docs: (a) the prompt must still instruct "respond in JSON" or the model emits whitespace spew — `SynthesisPromptBuilder` adds that line; (b) keep `think:false` (a thinking model would prepend `<think>` and pollute the constrained JSON; `ThinkingStripper` then becomes a JSON no-op, leave it). `num_predict` is the local over-ceiling safety net.
- **Public — `GoogleGeminiClient`** POSTs `/v1beta/models/{model}:generateContent` with `generationConfig{temperature, maxOutputTokens, seed}`. **Extend `GeminiGenerationConfig` with `responseMimeType="application/json"` + `responseSchema=<schema>`** (both via `[JsonPropertyName]`). Gemini's `responseSchema` is a **JSON-Schema subset** — supports `enum`, `array` `minItems`/`maxItems`, and nullable via `type:["string","null"]`; add a `propertyOrdering` list (Gemini 2.0 wants explicit ordering). **Caution (not in Google's doc, verify against the pinned public model):** on 2.5 *thinking* models, thinking tokens count against `maxOutputTokens` and can truncate the JSON — keep `MaxOutputTokens` generous in public mode.
- **Two dialects, one logical schema:** Ollama takes the JSON Schema as-is; Gemini needs the subset projection (`propertyOrdering`, drop unsupported keywords). Because of decision #5a there is **no union** — four flat per-form schemas — so the projection is near-identity. Keep the four schemas in one place and give each client a tiny `ToProviderSchema()` adapter.
- **Pre-existing inconsistency to fix while here:** `SynthesisLlmRouter.ResolveModelTag` returns the `LocalModelTag` *const*, but `OllamaSynthesisLlmClient` reads `config["IngestSaga:Models:Synthesis:OllamaTag"] ?? const`. If config overrides the tag, the **cache key + provenance** (built from `router.ResolveModelTag` at handler line 67) name a different tag than the model actually used — and this ticket leans on that cache key for auto-reissue. Unify: have `ResolveModelTag` read the same config key.

### Code seams (files, exact changes)
1. **`SynthesisRequest`** — add one nullable field carrying the chosen per-form schema, e.g. `JsonElement? ResponseJsonSchema`. `OllamaSynthesisLlmClient` maps it to the top-level `format`; `GoogleGeminiClient` maps it to `generationConfig.responseMimeType`+`responseSchema` (subset projection). When null, both behave exactly as today (free-text). `SynthesisResponse` is **unchanged** — `Body` carries the JSON text. Keep `MaxOutputTokens=2048` (Ollama `num_predict` / Gemini `maxOutputTokens`) as the over-ceiling safety net.
2. **Essence object + deterministic renderer** — the LLM stops emitting raw Markdown body and emits the typed object; the **handler deserializes `SynthesisResponse.Body`** into it. Per decision #5a, the object is one of four concrete shapes (the `form` is chosen upstream, not a field the model picks), e.g. shared head `{ title: string, tags: string[], wikilinks: string[] }` + a per-form `content` (`prose`→string; `bullets`→`string[]` maxItems=N; `checklist`→`{text,checked}[]`; `table`→`{columns:string[], rows:string[][]}`), `maxItems`/`maxLength` = the ceiling. A new renderer turns it into Markdown (H1 + inline `#tags` + form-appropriate body), extending the existing server-side frontmatter/`## Sources` rendering pattern.
3. **`EssenceBudget.For(B)`** (new, pure) — replaces the "Aim for 4–10 sentences" prose in `SynthesisPresetBodies`. Unit-testable: thin `B` → short band; rich `B` → larger band, clamped.
4. **`FormRouter.Pick(inputs)`** (new) — heuristic per decision #4. (Constrained-enum decode is the fallback.)
5. **`SourcesRenderer.Render`** — drop `userBody` param + `AppendUserNotesCallout`.
6. **`FrontmatterBuilder.BuildSynthesis`** — emit only `thany_note_id`/`thany_updated_at`/`thany_locked`. Tags emit from the essence renderer (inline), not here.
7. **`ProcessingDetailsRenderer`** (new) — consumes the existing `SynthesisFrontmatterFields` → bottom `> [!info]- Processing details` collapsed callout.
8. **`finalBody` (handler line 169)** — becomes `frontmatter + essenceMarkdown + "\n\n---\n\n## Origin\n" + note.BodyInput + "\n\n" + sources + "\n\n" + processingDetails` (insert fence markers around essence/sources/processing-details **iff** Origin is "living").
9. **Version bump** — `preset-*-v2 → v3` (and the prompt-builder/template version). The cache key (handler line 78) already includes `promptVersion`, so every note auto-reissues in the new layout. No migration.

### Ordering wrinkle (for v2 salience)
The `Embedding` phase runs **after** `Synthesizing` (handler transitions to it at the end), so embeddings are not available to power salience selection cheaply in v2. Use a non-embedding salience signal (TextRank / lexical centroid — no model load) to avoid reordering the saga, or move embedding earlier (bigger change).

## Thesis framing

Baseline = prompt-only synthesis (cite the ~50% length-failure result). Ablation ladder for the eval chapter: **+budget → +constrained form → +salience → +verify-revise loop**, each measured against length-compliance and faithfulness metrics (FactCC, SummaC, QAGS, QuestEval). Conceptual anchor: Forte (2022) Progressive Summarization as the non-destruction principle, with recursive/hierarchical summarization as its computational analog.

## Sources

- Controllable Text Summarization survey — https://arxiv.org/html/2311.09212v3
- CTRLsum — https://arxiv.org/abs/2012.04281
- Black-box length control — https://arxiv.org/pdf/2412.14656 · Zero-shot length control — https://arxiv.org/pdf/2501.00233 · LCPO/L1 (COLM 2025) — https://arxiv.org/pdf/2503.04697
- Bottom-Up Summarization — https://arxiv.org/pdf/1808.10792 · Salience Allocation / SEASON — https://arxiv.org/abs/2210.12330 · Entity-driven selection — https://arxiv.org/pdf/1909.02059
- XGrammar — https://blog.mlc.ai/2024/11/22/achieving-efficient-flexible-portable-structured-generation-with-xgrammar · JSONSchemaBench — https://arxiv.org/html/2501.10868v1
- Hallucination detection + mitigation (Nature 2025) — https://www.nature.com/articles/s41598-025-31075-1 · Self-critique faithful summarization — https://arxiv.org/html/2512.05387
- Context-Aware Hierarchical Merging — https://arxiv.org/pdf/2502.00977
