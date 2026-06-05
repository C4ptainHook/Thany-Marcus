# CLOUD-XLANG-UNIFY — Cross-language entity & wikilink unification

**Status:** SHIPPED (2026-06-04). Both (A) cross-language aliases and (B) native alias resolution landed, plus `DisplayName`/hysteresis and a plugin merge action. Builds on the shipped CLS-pooling fix (`cloud-related-notes-realtime-handoff.md`, Step 0) which is what makes cross-lingual entity resolution viable. Feeds the unblocked graph leg (`CLOUD-RELATED-NOTES-GRAPH`).

**What shipped vs. the design below:**
- `Transliteration` (Cyrillic→Latin, Ukrainian National 2010 + exceptions) seeded at entity creation (`AcceptAsync`), not in `ExtractingEntitiesHandler` — entities are only minted on suggestion acceptance, never auto-created, so that is the real creation point.
- Richer embedding recipe is `EntityEmbeddingHelper.{EmbedCandidateAsync,EmbedEntityAsync}` sharing one `Compose(name, description, contexts)` so candidate and stored vectors share a space; the candidate now embeds with its surrounding context.
- Gray band (`SuggestionMatchDistance` 0.25 → `GrayZoneMergeMaxDistance` 0.45) routes to a suggestion carrying `SuggestedMergeEntityId`/`SuggestedMergeDistance`; acceptance with `?mergeInto=<id>` folds the surface form (canonical+aliases+translit) into the target's aliases + refreshes its stub. No new entity minted.
- (B) alias-append in `ExtractingEntitiesHandler` now calls `EntityStubWriter.UpdateAliasesAsync`; collision guard is `EntityStubWriter.IsAliasClaimedElsewhereAsync` (other live entity canonical/alias, or a literal note basename).
- `Entity.DisplayName` + `DisplayNameRecomputer` (hysteresis margin, default +2) run in `HubGenerationHandler`; the hub H1 uses `DisplayName ?? CanonicalName`. `CanonicalName` is never touched.
- **Backfills deliberately NOT built.** Per the disposable-cloud-data decision, the entity re-embed sweep and stub-frontmatter backfill are replaced by wipe-and-reingest: a fresh ingest re-creates every entity/stub in the new recipe-space. The migration `20260604203658_CrossLanguageUnification` only adds columns (`display_name`, `suggested_merge_entity_id`, `suggested_merge_distance`). Existing entity vectors from the bare-name recipe will under-match new context-rich candidates until re-ingested.
- Tag unification (`#проєкт`/`#project`) remains out of scope — follow-on `CLOUD-XLANG-TAGS`.

**Goal:** in a bilingual (Ukrainian + English) vault, stop the knowledge graph from fragmenting along language lines. Today `[[Київ]]` in a Ukrainian note and `[[Kyiv]]` in an English note become two disconnected nodes; `#проєкт` and `#project` two tags. Unify them into one entity with cross-language aliases, and canonicalize wikilink generation so links across languages converge on one node while still *reading* in each note's own language.

**Core architectural decision (locked):** **stable identity, mutable label.** The wikilink target / hub filename is frozen at entity creation and never auto-renamed. The "most-frequent surface form" drives only the *displayed* name (hub H1 / UI / search), because moving a label moves nothing. This deliberately avoids the rewrite-cascade machine that a mutable-canonical-as-identity design would require (every counter-flip rewriting N notes + renaming the hub + re-embedding — recurring, content-mutating, and fighting Obsidian's own rename + sync).

**Render decision (REVISED after code check — supersedes the earlier `[[Canonical|local-display]]` rewrite):** cross-language links resolve **natively via Obsidian alias frontmatter**. `EntityStubWriter` already materializes each entity as a stub note whose basename = `CanonicalName` and whose `aliases:` frontmatter makes Obsidian fold surface forms onto one node. So `[[Kyiv]]` stays *as written* in an English note (language-faithful display, for free) and resolves to the `Київ` node — same reading + unified graph as `[[Київ|Kyiv]]`, but with **zero body mutation, no re-embed, no sync churn**. This is the consistent expression of "don't mutate user files." Explicit body rewrite is kept only as an optional fallback for entities with no materialized stub.

**Estimated effort:** ~4–5 person-days with AI assistance. Independently shippable in two halves: (A) cross-language aliases, then (B) canonicalized link generation. (A) has standalone value (unified entities → better related-notes/graph) even before (B) lands.

---

## Why this exists

A bilingual vault fragments every *exact-string* signal — wikilinks, tags, the entity graph — because cross-language synonymy (Київ↔Kyiv) shares no morphology, so no stemmer/lexical trick bridges it. The signal that *does* bridge it is the embedding (Granite r2 is cross-lingual; `uk` is in its 52 enhanced-support languages). So unification has to ride the vector layer, not the lexical one.

What already exists (don't rebuild):
- `Entity` has `CanonicalName`, `Aliases[]`, `Embedding`, `HubNoteId`, `StubNoteId`, `MentionCount` (`Features/Entities/Entity.cs`).
- `ExtractingEntitiesHandler` already does embedding-kNN resolution: embeds a candidate name, finds the nearest existing entity by `Kind`, and **on a within-gate match appends the surface form as an alias** (`:111–141`). The alias-on-match path is built.
- `CanonicalName` is *already de-facto stable* — the match path mutates only `Aliases`, never `CanonicalName`. Freezing it is barely a change.
- `Mention.AnchorText` stores the surface form of every mention — the data source for "most-frequent."
- `EntitySuggestions` flow exists for user curation of unknown candidates (`EntitySuggestion` has `AcceptedEntityId` for the post-acceptance link, but **no proposed-merge-target field** — that's the gap (A) closes).
- **`EntityStubWriter` already drives Obsidian-native alias resolution** (`Features/EntitySuggestions/EntityStubWriter.cs`): stub basename = `CanonicalName`, surface forms written to `aliases:` frontmatter, so `[[surface form]]` folds onto the canonical node. `UpdateAliasesAsync` refreshes that frontmatter. **This is the unification mechanism for (B)** — it just isn't fed cross-language aliases yet.

What's missing: (1) resolution won't fire *across* languages (gate too tight + entity embedded from the bare name), and (2) synthesis emits wikilinks by LLM judgment with zero reference to the registry (`SynthesisPromptBuilder:89`, `SynthesisPresets`).

---

## Identity & label model

Repurpose with the least churn:

- **`Entity.CanonicalName`** → the **stable link target / hub filename basename.** Set once at creation (first surface form seen). Changed *only* by explicit user rename, never by frequency. This is the node identity; `[[CanonicalName]]` resolves to the hub note.
- **`Entity.DisplayName`** (NEW, mutable) → the **most-frequent surface form.** Recomputed lazily from `Mention.AnchorText` with hysteresis. Drives the hub note's H1 title, UI, and search display. Changing it updates the hub H1/frontmatter only — **no link rewrite, no file rename.**
- **`Entity.Aliases[]`** → every observed surface form across languages, plus seeded transliteration variants.

"Most-frequent" recompute: `GROUP BY normalize(anchor_text) … ORDER BY count DESC` per entity, with **hysteresis** (challenger must exceed the current `DisplayName`'s count by a margin, e.g. ≥2× or +N, to flip) so the label doesn't flap. Runs on reprocess / on a cadence, not per-mention.

Why this is clean long-term: identity never moves → no rewrite cascades, no flapping, no fighting Obsidian rename, no sync churn, no routing-path churn (`relative_path` stays put). The label is free to track how you write because it's decoupled from the graph.

---

## Scope

**In scope:**
- (A) Cross-language alias resolution: richer entity embeddings, transliteration alias seeding, gray-zone merge *suggestions*.
- (B) Native alias resolution: sync cross-language aliases into stub `aliases:` frontmatter + collision guard. No note-body rewriting (see revised (B)).
- `DisplayName` field + hysteresis recompute.
- Backfills: entity re-embed sweep, stub-frontmatter refresh sweep.

**Out of scope:**
- Tag unification (`#проєкт`/`#project`). Same problem, separate ticket — tags aren't entities today. Note it as a follow-on.
- Auto-un-merge / split UI beyond what suggestions already offer.
- LLM-based transliteration (use a deterministic table; cheaper, explainable, thesis-friendly).
- Changing the synthesis LLM's *choice* of what to link — only *which node* the link resolves to.

---

## (A) Cross-language aliases

Three targeted moves — do **not** just loosen the `0.25` gate (it would false-merge distinct same-language entities sitting at 0.3).

1. **Richer entity embeddings.** `EntityEmbeddingHelper.EmbedCanonicalAsync` currently embeds the **bare name**. Embed `CanonicalName + Description + a few sample mention contexts` instead. Context carries the cross-lingual signal a lone name lacks, *and* disambiguates two same-language "John"s — raising cross-language recall while *tightening* same-language precision.
   - Requires recomputing every stored `Entity.Embedding` → **entity re-embed backfill** (one-shot; all entity vectors must live in the same recipe-space, same hazard as the note re-embed). Entity embeddings already flow through the now-CLS `IEmbeddingClient`.
2. **Transliteration alias seeding.** For `person`/`place` kinds, deterministically generate Cyrillic↔Latin variants at creation (Ukrainian National 2010 romanization + common variants: Київ→Kyiv/Kiev, Богдан→Bohdan/Bogdan) and store as `Aliases`. Catches obscure transliterated names that name-embeddings miss. Seed *multiple* variants (Kyiv vs Kiev ambiguity).
3. **Gray-zone → suggestion, not auto-merge.** Keep auto-merge for tight matches (`≤ SuggestionMatchDistance`). Route the gray band (≈0.25–0.45) to the existing `EntitySuggestions` flow as a **cross-language merge proposal** for user confirmation — high-value but risky, exactly where a human confirm earns its keep. Add `EntitySuggestion.SuggestedMergeEntityId` (`Guid?`, + the distance) to carry the proposed target — `AcceptedEntityId` already exists but is the *post-acceptance* link, not the proposal. On acceptance, the surface forms fold into the target entity's `Aliases` and stub frontmatter (→ (B)).

**Files:** `Features/Entities/EntityEmbeddingHelper.cs` (richer embed), `Features/Processing/Phases/ExtractingEntitiesHandler.cs` (gray-zone branch + translit seeding on entity creation), `Features/EntitySuggestions/*` (merge-proposal shape), a new `Transliteration` helper, entity re-embed migration.

---

## (B) Native alias resolution (no body rewriting)

The code check changed this. `EntityStubWriter` + Obsidian's resolver already unify surface forms onto one node — the only thing missing is that **cross-language aliases never reach the stub frontmatter.** So (B) is a *sync* problem, not a *rewrite* problem. No note bodies are touched.

**The mechanism (already there):** stub basename = `CanonicalName`; `aliases:` frontmatter lists the surface forms; `[[Kyiv]]` resolves to `Київ.md` if "Kyiv" is in its aliases. Link stays as written → displays "Kyiv" → edges to the one node. Unified graph + language-faithful reading, zero mutation.

**The gap to close:** the alias-append path in `ExtractingEntitiesHandler:140` mutates `entity.Aliases` but does **not** call `EntityStubWriter.UpdateAliasesAsync`, so accumulated (incl. cross-language) aliases don't propagate to the stub frontmatter until some other event fires. Wire the alias-append (and the gray-zone merge acceptance) to refresh the stub via `UpdateAliasesAsync`. That's the core of (B).

**Unification therefore requires a materialized stub or hub.** Entities below the hub/stub threshold (or `HubSuppressed`) have no node for `[[Kyiv]]` to fold onto, so low-mention entities unify only once they cross the threshold — at which point past links resolve retroactively (resolution is at read time, not write time). Acceptable; note it.

**Collision guard:** before adding a surface form to entity E's aliases, ensure no *other* live entity (or literal note) already claims it — Obsidian resolves an ambiguous alias to one target arbitrarily. Cross-language aliasing widens this surface, so the alias-write must reject/relocate a form already owned elsewhere.

**Backfill:** one pass that refreshes every stub's `aliases:` frontmatter from its entity's current `Aliases` (after the (A) re-resolution has populated cross-language aliases). No body re-canonicalization, no LLM, no note re-embed — far lighter than the earlier rewrite plan. Stub notes do get rewritten (they're cloud-owned frontmatter), but user notes are untouched.

**Files:** `ExtractingEntitiesHandler.cs` (call `UpdateAliasesAsync` on alias change), `EntityStubWriter.cs` (collision guard), suggestion-acceptance path (refresh on merge), a stub-frontmatter backfill migration.

**Optional fallback — explicit rewrite:** only if you later want unification for *stub-less* entities or distrust the resolver: a deterministic post-synthesis pass that rewrites inline `[[T]]`→`[[CanonicalName|T]]`, kept *outside* the synthesis cache (cache key has no entity-registry state) so it re-applies on a cheap LLM-free reprocess. Not in v1 — native resolution covers the case.

---

## Tests

- `EntityStubWriterTests` / alias-sync — appending a cross-language alias to an entity refreshes its stub `aliases:` frontmatter (`UpdateAliasesAsync` called); the cross-language form appears in the stub; collision guard rejects a form already owned by another live entity; `CanonicalName` (and thus stub basename) never changes.
- `EntityResolutionTests` — richer-embedding match fires across language within gate; gray-zone routes to a suggestion carrying `SuggestedMergeEntityId`, not auto-merge; tight match still auto-merges + appends alias + syncs stub.
- `TransliterationTests` — Київ → {Kyiv, Kiev}; Богдан → {Bohdan, Bogdan}; round-trip stability; non-name kinds skipped.
- `DisplayNameRecomputeTests` — most-frequent wins; **hysteresis prevents flip on a 1-count lead**; `CanonicalName` never changes on recompute.
- Cache: canonicalization re-applies on synthesis cache hit (does not bake into cached essence).

---

## Risks / open decisions

- **Cross-lingual resolution is the "enhanced" tier — good, not perfect.** It connects Київ↔Kyiv semantically; the transliteration table closes exact-name gaps. Accept some misses; the gray-zone suggestion flow is the safety net.
- **Wrong merges.** Cross-language merging is riskier than monolingual. The decoupled identity makes un-merge *cheap* (links point at stable nodes; splitting doesn't require un-rewriting the vault) — a structural argument for this design. Still, keep auto-merge tight and lean on suggestions for the gray zone.
- **Transliteration ambiguity** (Kyiv vs Kiev = Ukrainian vs Russian-derived). Seed both; let mention frequency pick the `DisplayName`.
- **Hysteresis margin** is a feel knob — start at +N or ≥2×, observe.
- **`DisplayName` vs hub H1** — changing the H1/title of a hub note *is* a (single) file edit, not a rename; confirm it doesn't trip routing or sync reconciliation.
- **Alias collisions** — cross-language aliasing widens the chance two entities (or a literal note) claim the same surface form; Obsidian resolves an ambiguous alias arbitrarily. The alias-write collision guard is load-bearing, not optional.
- **Unification needs a stub/hub** — entities below the materialize threshold (or `HubSuppressed`) have no node to fold onto, so they unify only after crossing it (then retroactively — resolution is at read time).
- **Alias-sync hook** — the alias-append path (`ExtractingEntitiesHandler:140`) doesn't refresh stub frontmatter today. Miss this wiring and aliases pile up in the DB but never reach the vault, so nothing resolves. It's the single most important integration point in (B).

## Thesis framing

The contribution: **cross-lingual knowledge-graph unification with stable-identity / mutable-label decoupling.** The interesting, defensible claims — (1) in a multilingual PKM the *vector* layer is what unifies the graph while lexical/link signals fragment it (inverts the usual "embeddings are the fuzzy part" intuition); (2) deterministic, explainable transliteration + embedding-gated resolution beats an LLM dedup pass; (3) decoupling node identity from display name avoids the rename-cascade failure mode that naive "canonical = most-frequent" designs hit. All three are citable design positions, not just engineering.

## Follow-ons

- **CLOUD-XLANG-TAGS** — extend unification to tags (`#проєкт`/`#project`); tags aren't entities today, so this needs a tag registry first.
- **CLOUD-RELATED-NOTES-GRAPH** — now unblocked: unified entities make the graph-proximity leg (co-link / shared-entity / shared-hub) meaningful in a bilingual vault.
