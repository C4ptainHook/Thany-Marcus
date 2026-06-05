# CLOUD-RELATED-NOTES-REALTIME — Quality + real-time enhancement of the related-notes panel

**Status:** DESIGN — not started. Supersedes the v1.5/follow-on bullets at the bottom of `cloud-related-notes-handoff.md` (`CLOUD-RELATED-NOTES-MMR` is folded into Step 2 here; `CLOUD-RELATED-NOTES-HYBRID` becomes the parallel doc-side track).

**Goal:** make the capture-time related-notes panel (shipped in `cloud-related-notes-handoff.md`) actually *feel* smart and stay responsive as the user types — and fix a correctness bug that currently caps its quality regardless of any other tuning. Two tracks: a **query-side real-time layer** we build ourselves (qmd has no equivalent), and a **document-side foundation** we lift from `tobi/qmd`. A pooling fix gates the quality of both.

**Estimated effort:** Step 0 ~0.5 day, Steps 1–3 (the real-time layer) ~2 days, the parallel doc-side track ~3–4 days. Steps 0–3 are independently shippable on the *current* index; the doc-side track is a separate ticket.

---

## Context — what shipped, and what this session found

The v1 panel is live: `POST /api/notes/related` → `NoteVectorQueries.NearestAsync` (raw-SQL cosine kNN over `notes.embedding`) → `RelatedNotesPanel.ts` (debounced 600ms, k=5, abort-on-keystroke). It works end to end. This ticket is the result of grilling whether the ranking is *optimal*. It is not. Findings below, ordered by impact.

### Critical findings

- **F1 — Pooling bug (highest priority, gates everything).** `GraniteEmbeddingClient.Embed` (`Infrastructure/Sidecars/Embedding/GraniteEmbeddingClient.cs:73`) **mean-pools** the token hidden states (`MeanPool`, `:119`). The model — `ibm-granite/granite-embedding-311m-multilingual-r2` — is documented to use **CLS pooling** (`model_output[0][:, 0]`, the first token). We are reading the embedding off a channel the contrastive objective never optimised. It doesn't crash and isn't obviously broken because query and document share the same distortion, so kNN *roughly* works — which is exactly why it survives tests. But none of IBM's published numbers (MTEB, the per-language retrieval quality) apply to this configuration. Every quality lever below is capped until this is fixed.
- **F2 — Self-imposed 512-token truncation.** `GraniteEmbeddingOptions.MaxTokens = 512` (`appsettings.json` → `IngestSaga:Models:Embedding`) hard-truncates input in `TokenizeAndTruncate`. It is *our* cap, not the model's (r2 supports far longer context). Anything past token 512 of a note never reaches the model. Cyrillic tokenizes to more tokens/word than Latin, so Ukrainian notes hit this ceiling after fewer real words.
- **F3 — Query/document distribution asymmetry.** Stored vectors come from `BodyOutput` (the synthesised essence; `EmbeddingHandler.cs:50`). The live query comes from the user's **raw draft**. Different text distributions compared in the same space.
- **F4 — Whole-body single-vector blur (query side).** The client embeds the *entire* draft body. As the note grows, the query vector blurs toward a topical centroid. Same single-vector limitation we have on the doc side, now on the query.
- **F5 — Dead query cache.** `QueryEmbeddingCache` keys on `SHA256(whole body)` (`:49`). Every keystroke-pause changes the body → new key → miss → full re-embed. Hit rate during active typing is ~0%; the cache only helps re-opening an identical draft.
- **F6 — Crude recency exclusion.** `NearestAsync` filters `created_at < now − 1h` (`RelatedNotesOptions.ExcludeRecentHours = 1`). Hard, global, keyed on *creation* not last-edit. Journaling for an hour makes today's notes vanish from "related." It's a clumsy proxy for "don't show the note I'm in."
- **F7 — No diversity.** Pure top-5 cosine returns near-duplicate pills when several notes cover the same facet — five slots carrying one slot of information.
- **F8 — Filtered-HNSW recall cliff.** The HNSW index (`20260530144012_NotesEmbeddingHnswPartial.cs`) is partial on `deleted_at IS NULL` only. The query adds `status='ready'`, `created_at<…`, `id<>@exclude` as **post-filters**. With default `ef_search=40` and no iterative scan, top-5 can silently under-return after filtering.
- **F9 — Aborts don't free server compute.** `EmbedAsync` awaits the semaphore on `ct` but `session.Run` is synchronous and non-cancellable. A client abort cancels the HTTP wait, not the inference — so rapid typing still burns full embeds against the 2-slot CPU semaphore.

---

## Prior art — `tobi/qmd`, and what we take

`tobi/qmd` (Tobias Lütke's local markdown search CLI, TypeScript/Node, sqlite-vec) is the mature reference. Its stack:

- **Chunking:** ~900-token chunks, 15% overlap, markdown-boundary scoring (H1=100, H2=90, code fence=80, paragraph=20) + distance-decay to snap to a natural break; tree-sitter AST chunking for code files.
- **Retrieval:** hybrid **BM25 + vector + LLM rerank**, fused with **Reciprocal Rank Fusion (k=60)**, position-aware blend (RRF-heavy at the top, reranker-heavy at the tail).
- **Rerank:** Qwen3-Reranker-0.6B (yes/no + logprob confidence).
- **Multilingual:** default embedder is embeddinggemma-300M; swap to **Qwen3-Embedding-0.6B** for multilingual via `QMD_EMBED_MODEL`.

What we take vs build:

- **Take (doc-side foundation):** chunking + markdown boundary scoring, hybrid BM25+vector with RRF. Adopt LLM rerank **only on the non-live `noteId` path**.
- **Don't take:** qmd is batch — it has no real-time path. Our query-side layer (Steps 1–3) is net-new.
- **Note for the thesis:** qmd's multilingual answer is "swap to Qwen3-Embedding." That hands us a built-in comparison — Granite-311m-multilingual-r2 (uk is in its 52 enhanced-support languages) vs Qwen3-Embedding-0.6B — to benchmark on a real Ukrainian vault. Run it *after* the pooling fix or the comparison is rigged against Granite.

---

## Target architecture — three layers

1. **Foundation (take from qmd, document side):** chunked, markdown-boundary index + hybrid BM25/vector candidate generation + RRF. *Decides how good the candidates are.*
2. **Real-time query layer (build, qmd-absent):** cursor-local paragraph query (B1) → overfetch from Layer 1 → MMR (B2) → recency/current-note shaping (B3). *Decides how we query and rerank in real time.*
3. **Cross-cutting prerequisite:** the CLS-pooling fix (F1) + asymmetry decision (F3). Both layers ride on the embeddings.

The two layers meet at exactly one seam: **the candidate set.** They are *not* strictly ordered. The real-time layer is shippable against the *current* whole-note index now; the doc-side foundation is a parallel upgrade that raises candidate quality underneath it later. The pooling fix gates the quality of both, so it goes first.

---

## Sequenced plan

### Step 0 — Fix pooling + force re-embed (prerequisite, ~0.5 day)

**The fix:** in `GraniteEmbeddingClient.Embed`, replace `MeanPool(lhs, seqLen, hidden)` with CLS extraction — take `lhs[0, 0, h]` for `h ∈ [0, hidden)`. Keep the existing L2-normalise → truncate-to-256 → re-normalise (correct Matryoshka procedure for this model). Delete the `MeanPool` helper.

**The landmine — invalidation.** `EmbeddingHandler` skips re-embedding when `Reprocess && Embedding != null && BodyHash == newHash` (`:56`), and `ComputeBodyHash` keys only on `template + "\n" + body` (`:105`). Changing pooling does **not** change the body or template → the skip fires → stale mean-pooled vectors survive next to new CLS query vectors → silently broken retrieval. Two acceptable fixes:

- Fold an embed-config tag into the hash input, e.g. prepend `"granite-cls-256-v1\n"` in `ComputeBodyHash`. Every note's hash changes → forced re-embed on next reprocess.
- Or a one-shot migration that `NULL`s `notes.embedding` and enqueues a reprocess sweep.

Either way: **the entire vault must be re-embedded before the panel is trustworthy** — query (new CLS) and stored (old mean) vectors are not comparable across the boundary.

**Decide F3 here too:** for the body path, do we keep comparing raw-draft-query against essence-document? Option: also embed the raw note body (not just essence) and query against that, for distribution parity. At minimum, document the asymmetry as a known limitation.

### Step 1 — Cursor-local + paragraph-cached query (keystone, ~1 day)

Change the unit of embedding from whole-draft to the **paragraph/section at the cursor**.

- **Plugin** (`plugin/thany-marcus/src/related/RelatedNotesPanel.ts`): `onBodyChange(body)` → `onContextChange(blockText, currentNoteId?)`. The composer extracts the cursor block from the Obsidian editor. Track block identity so pure cursor moves *within* a block don't refire. Render **stale-while-revalidate** — keep showing last results while fetching; never clear to empty mid-fetch. Keep the existing debounce + abort machinery.
- **Contract** (`RelatedNotesEndpoint.RelatedNotesRequest`): the `Body` field now carries the cursor block, not the whole note. Add `Guid? ExcludeNoteId` so the body path can exclude the current note (today only the `noteId` path excludes self).
- **Cache:** mechanism unchanged — keying on the (now small) block text gives high reuse. Edit paragraph 4, paragraphs 1–3 and 5+ are hits. Hit rate ~0% → ~(N−1)/N.
- **Falls out for free:** no query-side 512 truncation (a paragraph is never 512 tokens), and F9 waste shrinks to negligible (paragraph-sized inferences).
- **Edge:** blocks below the 30-char floor → widen to the enclosing section.

**Query-semantics decision:** default to *just the cursor block*. Blending decayed neighbours is a later refinement; reassembling the whole note from cached block vectors reintroduces the blur — don't.

### Step 2 — Overfetch + MMR (~0.5 day)

- **`NoteVectorQueries.NearestAsync`:** add a `fanout` (e.g. 30) and `SELECT embedding` into the result so item↔item similarity is available. Add the vector to `RelatedNote`.
- **Endpoint:** after fetching `fanout` candidates, run MMR — greedily pick `k` maximising `λ·sim(query,item) − (1−λ)·max sim(item, picked)`. New helper `Mmr.Select(query, candidates, k, lambda)`; it's dot products over ≤30 256-dim vectors, sub-millisecond. Config `Fanout=30`, `Lambda≈0.6` (lean diverse for an ideation strip; no labels to tune against — pick by feel, expose the knob).
- **Bonus:** overfetching to 30 before post-filtering also mitigates F8 (gives the filter room before LIMIT).

### Step 3 — Recency shaping (~tiny, but answer the question first)

- Drop the hard `created_at < now − 1h` clause from `NearestAsync` (set `ExcludeRecentHours = 0` or remove the predicate).
- Always pass `ExcludeNoteId` (the current note) on both paths — that's what the 1-hour rule was clumsily approximating.
- **Open product question — settle before adding any recency term:** is "Related thoughts" meant to **contextualise** (surface notes near what you're writing *now* → favour recent) or **resurface the forgotten** (surface old notes you've lost → favour *old*)? The two point in opposite directions; the current accidental behaviour leans "resurface forgotten." Do *not* add a decay until this is decided — a wrong-signed boost is worse than none.

### Parallel track — Layer 1, doc-side foundation (separate ticket, ~3–4 days)

Lift from qmd. Not a blocker for Steps 0–3.

- **`note_chunks` table:** `(note_id, ordinal, token_range, text, embedding)` + its own HNSW index. Markdown-boundary-scored chunker (qmd's H1/H2/code/paragraph scoring + distance-decay, 15% overlap, sized to ≤ the model's token budget). The heuristic is deterministic and explainable — good thesis material.
- **`EmbeddingHandler` rewrite:** embed per chunk, upsert chunks instead of one note vector.
- **Query rewrite:** kNN over chunks → aggregate to note (`score = max(chunk sim)`) → dedup so one note appears once. Snippet becomes the *matching chunk*, not "first non-heading line."
- **Hybrid:** Postgres FTS `tsvector` + vector, fused with RRF (k=60). **Verify Postgres' Ukrainian FTS dictionary exists** before banking on the lexical leg — Cyrillic morphology, recent Snowball support.
- **LLM rerank:** only on the `noteId` path (no live typing → latency budget exists). Never on the live draft panel.

---

## Tests

- **Step 0:** `GraniteEmbeddingClientTests` — CLS vector equals `lhs[0,0,:]` post-normalise; truncation-to-256 then renormalise; `ComputeBodyHash` changes when the embed-config tag changes (forces re-embed).
- **Step 1:** plugin — `onContextChange` refires only on block-content change, not cursor move within a block; stale-while-revalidate keeps prior results during fetch; block below floor widens to section. Backend — body path honours `ExcludeNoteId`.
- **Step 2:** `MmrTests` — diverse pick beats pure top-k on a handcrafted near-duplicate set; `λ=1` reduces to top-k; fanout returns ≥k after post-filter on a seeded set.
- **Step 3:** `NearestAsync` no longer excludes by `created_at`; current note never returned on either path.
- **Layer 1:** chunker boundary scoring (heading beats paragraph beats mid-sentence); chunk overlap; note-level max-aggregation + dedup; RRF fusion ordering.

---

## Risks / open decisions

- **Re-embed cost & window (Step 0).** Whole-vault re-embed on a 2-slot CPU embedder takes real wall-clock for a large vault. The panel degrades to noise until it completes — consider gating the panel or showing a "rebuilding index" state during the sweep.
- **Recency intent (Step 3).** Unresolved above; decide before implementing.
- **MMR λ (Step 2).** No labelled data; tune by feel, expose the knob.
- **Granite vs Qwen3-Embedding for Ukrainian.** Worth a thesis-grade benchmark, but only meaningful after Step 0.
- **On-device query embedding (shelved frontier).** Embedding the query in the plugin (WASM) would kill the per-keystroke round-trip and survive offline, but founders on vector-space parity with the server's ONNX Granite (same model/pooling/dim across two runtimes). Revisit only if Steps 1–3 don't make the panel feel fast enough.

---

## Thesis framing

The contribution is not "we built related-notes" (qmd, Smart Connections, Mem all do) — it's **the real-time query layer + knowledge-graph fusion that batch tools structurally can't do**, on top of a foundation benchmarked against qmd. qmd is both the baseline and the candidate-quality ceiling; the differentiators are Layer 2 here and the graph-fusion track (see follow-ons). The Granite-vs-Qwen3 Ukrainian comparison and the explainable markdown-boundary chunker are both citable artefacts.

## Follow-ons

- **CLOUD-RELATED-NOTES-GRAPH** — fuse a graph-proximity leg (co-link / shared-neighbour / personalized PageRank over wikilinks + entity hubs + folder registry) into candidate generation or rerank. The other differentiator qmd has no answer to; pays off in proportion to how linked the vault already is.
- **CLOUD-VAULT-QA / CLOUD-COMPOSE-FROM / CLOUD-DIGEST** — unchanged from `cloud-related-notes-handoff.md`; all consume this retrieval layer.
