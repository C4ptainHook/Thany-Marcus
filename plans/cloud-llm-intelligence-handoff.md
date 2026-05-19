# CLOUD-LLM-INTELLIGENCE — ILlmClient refactor + prompt registry + routing/entities/hub-regen wiring + pgvector dedup — Handoff Brief

Date: 2026-05-19
Status: Draft. Handoff #7 of the ADR-0042 / ADR-0044 implementation series. **Replaces the CLOUD-002-vintage merged `EnrichCompositeAsync` shape with the four-prompt registry from [[0044-cloud-intelligence-layer]]** and wires the saga's three LLM-phase handlers (`RoutingHandler`, `ExtractingEntitiesHandler`, plus a new `HubGenerationHandler`) to real Ollama calls against the MiniCPM-V text endpoint pre-pulled by handoff #2. The phase machine, lease/heartbeat/transition_version, SSE bus, reprocess/cancel endpoints, schema, and composer all stay byte-for-byte. After this ticket: a composite finalize routes the note to a project (or `Inbox/`), populates `mentions` rows pointing at entities resolved via pgvector top-K + LLM judge, and on-mention-count crossing the materialization threshold spawns a `kind='hub_regen'` ingest_job that produces a dossier note for the entity.

**Goal:** swap the single `ILlmClient.EnrichCompositeAsync(...)` shape for `ILlmClient.CompleteAsync<T>(PromptId, object inputContext, CancellationToken)` per ADR-0044 §1, land the four prompt constants (`route-v1`, `extract-v1`, `dedup-v1`, `hub-generate-v1`) as code-level versioned strings with matching strongly-typed DTOs, wire JSON-schema validation + bounded retry (default 3) on malformed Ollama output, fold every LLM call's prompt-id + model + retries + confidence + duration into the existing `events_log` audit trail and the `notes.provenance` summary, replace the existing `RoutingHandler` body with the `route-v1` call (one LLM call, threshold-gated project assignment), replace the existing `ExtractingEntitiesHandler` body with the `extract-v1` → per-mention `dedup-v1` (with pgvector top-K=5 cosine retrieval over `entities.embedding`) two-step flow, and add `HubGenerationHandler` (`hub-generate-v1`) plus the mention-count threshold trigger that INSERTs `kind='hub_regen'` jobs whose own saga produces the hub's `body_output` + re-extracted mentions + embedding. After this ticket: every LLM call in the cloud runs against the same `SafeLlmClient` (Ollama text mode) with prompt+model+retry recorded in `events_log`, the unsafe-mode dispatch is a documented seam wired but stubbed (handoff #8+), and the regression anchor from handoff #6 (`CompositeIngestSagaEndToEndTests.cs`) is green with non-null routing + non-empty mentions + (optionally) one hub note materialized.

Estimated **3.5–4 person-days** with AI-agent assistance, split into three passes. Pass A (~1.5d) lands `ILlmClient` refactor + `SafeLlmClient` + prompt registry + JSON-validation + retry + provenance plumbing, all behind the existing `RoutingHandler` slot (so handler still calls one LLM, just via the new interface against the `route-v1` prompt). Pass B (~1d) rewrites `ExtractingEntitiesHandler` to call `extract-v1` for the body, then iterates candidates through pgvector top-K + `dedup-v1`, inserts mentions, and increments `entities.mention_count` with the auto-hub trigger. Pass C (~1–1.5d) lands `HubGenerationHandler` + the `composing` phase dispatch on `job.Kind`, the hub-regen seed flow (create hub `notes` row + `ingest_jobs(kind='hub_regen')`), the diff-aware regen path (passes `previous_body` to the prompt when re-running), and the cross-handler regression anchor. Estimate is rough — it compresses to ~3d if MiniCPM-V's text-mode JSON output is well-behaved out of the box (verify against the pinned `minicpm-v` Ollama tag at start of Pass A); stretches to ~4.5d if JSON validation retry rates climb above the 5% revisit-threshold from ADR-0044 §2 and we need to fall back to grammar-constrained decoding (out of scope for thesis MVP, but the time cost of discovering this is real).

This handoff **does not** ship: real `UnsafeLlmClient` against Anthropic/OpenAI (the seam exists; `UnsafeLlmClient.CompleteAsync<T>` is a one-line `throw new NotImplementedException("CLOUD-026 / M7")` for this ticket and dispatch falls back to `SafeLlmClient` with a `llm_mode_fallback` provenance note — that ticket lives downstream as `CLOUD-026` in `plans/tickets-2026-05-13.md`); real Granite embedding ONNX (the `IEmbeddingClient` stub still returns zero vectors — `notes.embedding` and `entities.embedding` writes happen but cosine top-K is uninformative until real embeddings land); a `compose-v2` bump that splices LLM-derived wikilinks into the body (LLM-anchor-driven wikilink splicing is documented as a deferred follow-up here — `compose-v1` from handoff #6 stays the body template, and rendered Markdown carries no `[[entity]]` links from this ticket). Each of those three is a clean drop-in against the seams this ticket lands.

## Where decisions live (read before doing anything)

- **`docs/decisions/0044-cloud-intelligence-layer.md`** — the contract. §1 the `ILlmClient` interface + `PromptId(Name, Version)`. §2 `format: "json"` + post-parse validation + bounded retry (default `MaxAttempts=3`). §3 the four prompts column-for-column (`route-v1`, `extract-v1`, `dedup-v1`, `hub-generate-v1`), including their input shape + JSON output schema + threshold semantics + the per-prompt provenance fields. §4 the four named thresholds in `appsettings.json:LlmIntelligence:Thresholds` (`RouteAcceptMin=0.5`, `MentionMin=0.6`, `DedupAliasMin=0.8`, `DedupNewMin=0.6`). §5 prompts-as-code-constants pattern (no DB, no live tuning). §6 retry-on-malformed shape. §7 safe-vs-unsafe transport switch (same prompts, different HTTP client). §8 streaming explicitly deferred. **Every prompt template, schema, threshold, and retry decision in this ticket must trace to a section of ADR-0044 verbatim.**
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §3 phase machine (this ticket lights up the `routing`, `extracting_entities`, and `composing` phases for `kind='hub_regen'`). §4 best-effort failure cascade (an LLM call that exhausts retries flips the matching phase terminal: `routing → failed_route`, `extracting_entities → failed_entities`; the note still lands `failed` for the user but the saga doesn't crash). §6 `kind='hub_regen'` runs through a stripped-down saga (skip `extracting_attachments`; start at `composing`). §7 latency budget — the route + entities calls together are ~10 s of per-note tax, and a hub regen is ~6 s of model time end-to-end. §10c provenance schema — every LLM call appends `{stage, prompt_id, model, model_version, duration_ms, decision, confidence, retry_index}` to `ingest_jobs.events_log`.
- **`docs/decisions/0043-cloud-side-model-lineup.md`** — MiniCPM-V 4.6 Q4_K_M on Ollama is **the** safe-mode LLM transport. The same container that serves vision describes also serves text-mode JSON. Single model, single sidecar, single per-cloud RAM footprint — no second Ollama tag pulled for LLM-only work. The pinned tag from handoff #2 (`OLLAMA_PULL_TAG` env var → default `minicpm-v:8b-2.6-q4_K_M`) is what `SafeLlmClient` POSTs against; if MiniCPM-V 4.6 has landed on Ollama by apply time, bump the tag in lockstep across handoff #2's terraform var + `appsettings.json:IngestSaga:Models:Vlm:OllamaTag` (already symmetric per ADR-0043). **No separate text-only LLM tag.**
- **`docs/decisions/0045-composite-note-schema.md`** — §5 `entities` table (the `kind | canonical_name | aliases | embedding | hub_note_id | mention_count | source | is_provisional | vault_folder | deleted_at` columns the dedup + hub-spawn paths read/write). §6 `mentions` table (the `entity_id | note_id | anchor_text | start_offset | end_offset | confidence` columns the entity extraction inserts). §"Migration plan" + §F6 + §F8 — the threshold + dedup decision-table (`alias_of` ≥ `DedupAliasMin` → INSERT mention pointing to matched entity + add anchor_text to aliases; `new_entity` ≥ `DedupNewMin` → INSERT new entity + INSERT mention; `ambiguous` or below threshold → drop mention with provenance breadcrumb; `source='user'` auto-hubs).
- **`docs/decisions/0024-dbcontext-shape.md`** + **`docs/decisions/0028-schema-conventions.md`** — `CloudDbContext` is single-instance per request; the new handlers add no DbSets (everything they touch already exists via `db.Entities`, `db.Mentions`, `db.Notes`, `db.IngestJobs`). Snake_case naming, NodaTime `Instant` timestamps, `IClock`-driven now() — all preserved.
- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers + Respawn. The LLM-handler tests use a `FakeLlmClient` that returns canned JSON per prompt-id, so they don't need a real Ollama container in the fast lane. The slow lane (`[Trait("Category","Slow")]`) runs against a Testcontainers-hosted `ollama/ollama:0.5.1` with the pinned MiniCPM-V tag pre-pulled in a collection fixture — same pattern as the Docling/Parakeet slow tests from handoff #5.
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — the framework this ticket modifies. `JobOrchestratorWorker`, `IngestPhaseDispatcher`, `JobStateTransitions`, `IIngestEventBus`, `IPhaseHandler` — all stay byte-for-byte. **The only handler-side files changed are `RoutingHandler.cs`, `ExtractingEntitiesHandler.cs`, `ComposingHandler.cs`, plus the NEW `HubGenerationHandler.cs`.** The dispatcher gets one new `case` arm for `HubGenerationHandler` when `job.Kind == HubRegen` and `job.Status == Composing`.
- **`plans/cloud-schema-v2-handoff.md`** — confirms `entities.embedding vector(256)` + HNSW index + the soft-delete-aware partial unique on `(kind, canonical_name)` are already in the DB. This ticket reads/writes those columns; no migration ships here.
- **`plans/cloud-sidecars-handoff.md`** — `IngestSaga:Sidecars:Ollama:BaseUrl=http://ollama:11434` is the URL the `SafeLlmClient` POSTs to; `Models:Vlm:OllamaTag` is the model tag every prompt names in the request body's `model:` field. The compose stack already runs Ollama with `OLLAMA_KEEP_ALIVE=30m` and `OLLAMA_NUM_PARALLEL=1` — the per-sidecar concurrency cap (semaphore in cloud-api) is `IngestSaga:Sidecars:Ollama:MaxConcurrency=1`, set by handoff #2 — the same semaphore that throttles VLM calls now also throttles LLM JSON-mode calls. No new compose/cloud-init/terraform changes.
- **`plans/cloud-compose-phase-handoff.md`** — handoff #6 locks `compose-v1` as the body template + the `## User Notes` / `## System Output` skeleton + the YAML frontmatter shape. **This handoff does not bump to `compose-v2`.** Routing writes `notes.project_id` + `notes.relative_path`; the existing `compose-v1` frontmatter does not surface entities or wikilinks; the rendered body is the same shape after this ticket as before, with two visible differences: (a) the file lives under `Projects/<canonical>/<noteId>.md` instead of `Inbox/<noteId>.md` when routing accepts, and (b) a new hub note (`is_hub=true`) may appear under `_Entities/<kind>/<canonical>.md` after the threshold crosses.
- **`plans/cloud-pivot-plan-2026-05-13.md`** §B4 — diff-aware hub regen pattern. When a hub note already exists and its mention list grows, the new `HubGenerationHandler` invocation reads the existing `notes.body_output` for that hub and passes it as `previous_body` to the prompt. The prompt instructions then say "update additively, preserve existing structure" rather than the first-time "generate from scratch" instructions. **This is the only place in this ticket where prompt body branches on input** — a single template with a conditional `IF previous_body IS NOT null` block (per ADR-0044 §3.4).
- **`plans/tickets-2026-05-13.md`** — CLOUD-017 (routing), CLOUD-019 (entity dedup), CLOUD-020 (entity hubs / hub creation), CLOUD-021 (hub regen with diff-aware), CLOUD-026 (external API client for unsafe mode — deferred to M7). The first four ride this ticket; CLOUD-026 is the documented future-work hook.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. Hub regen is **internal** to the saga; the plugin sees a hub note as a normal note (`/api/sync/pull` returns it like any other) but it is generated by the cloud, not uploaded by the user. The "one processed note per ingest" invariant still holds: each `ingest_jobs` row produces exactly one `notes` write (capture writes the user note; hub-regen writes the hub note).
- **Memory `composite_ingest_decision.md`** + **memory `portal_architecture.md`** — Postgres job queue, mutable status, no event sourcing, SSE only. Hub-regen reuses the same queue + claim loop; no second worker class.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3.

**Do not litigate ADR-0044.** If you find a threshold value, a prompt shape, or a retry policy you think is wrong, raise a follow-up; do not change them inside this ticket. The eval chapter (M9 / EVAL-002 / EVAL-003 / EVAL-005) sweeps thresholds via config; that is the right place to discover better values. Same for the four-prompts-as-code-constants choice — the alternative (DB-backed prompt table) is named in ADR-0044 §"Alternatives considered" and rejected; do not relitigate.

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **Three separate LLM calls per capture, not one merged call.** Routing → extract → per-candidate dedup is three logical phases, two of which are one HTTP call each (routing, extract) and one of which is N calls (one per candidate mention; ~3 on average per note per ADR-0044 §"Negative / accepted costs"). The pre-existing `EnrichCompositeAsync` shape that merged routing + entities + wikilink-anchors into one call is **deleted**. Eval clarity (EVAL-002 routing accuracy independently from EVAL-003 entity dedup P/R) was the deciding factor; the "merge into one call" optimization is named explicitly as a deferred future-work hook (ADR-0044 §"Alternatives considered" #2, ADR-0042 §"Deferred future-work hooks").

2. **`ILlmClient.CompleteAsync<T>(PromptId, object inputContext, CancellationToken)` is the only public method.** Replaces `EnrichCompositeAsync`. The generic `T` is the strongly-typed DTO matching the prompt's JSON output schema. `inputContext` is an anonymous object the prompt-template-builder turns into the prompt body. **No per-prompt convenience methods on the interface** — `RoutingHandler` calls `client.CompleteAsync<RouteDecision>(new PromptId("route", "v1"), inputContext, ct)` directly. This keeps the interface narrow and adding a fifth prompt later is one new template constant + one new DTO + one new call site, no interface change.

3. **`PromptId` is a record, not an enum.** `public sealed record PromptId(string Name, string Version)`. The provenance value is `$"{Name}-{Version}"` (`"route-v1"`, `"extract-v1"`, etc.). Versions are bumped by adding a new constant; old constants are not deleted until a release-note guarantees no in-flight job references them.

4. **Prompt templates are static class constants in `Infrastructure/Llm/Prompts/PromptTemplates.cs`.** Four constants in one file: `Route_V1`, `Extract_V1`, `Dedup_V1`, `HubGenerate_V1`. Bodies are verbatim from ADR-0044 §3, transcribed as C# raw-string literals (`"""..."""`). Token interpolation uses `string.Format(CultureInfo.InvariantCulture, template, args)` with positional `{0}`, `{1}` markers — **not** Razor templates, **not** Scriban, **not** Roslyn-source-generators. One C# `static string Build(...)` method per prompt (in the same file or a `PromptBuilder` static helper) takes the anonymous input context, validates required keys are present, and returns the final prompt string. **The template-with-positional-markers form is the only thing the constant holds** — formatting logic + IO + DTO conversion lives in the builder.

5. **The DTOs for the four prompt outputs live alongside the templates** at `Infrastructure/Llm/Prompts/<Name>Dtos.cs`. One file per prompt for navigability:
   - `RouteDecisionDto.cs` → `record RouteDecisionDto(Guid? ProjectEntityId, double Confidence, string Rationale)`.
   - `EntityExtractionDto.cs` → `record EntityExtractionDto(IReadOnlyList<MentionCandidateDto> Mentions)` + `record MentionCandidateDto(string AnchorText, int StartOffset, int EndOffset, string CandidateKind, string CandidateCanonical, IReadOnlyList<string> Aliases, double Confidence)`.
   - `DedupDecisionDto.cs` → `record DedupDecisionDto(string Decision, Guid? MatchedEntityId, IReadOnlyList<Guid> Candidates, double Confidence, string Rationale)`. `Decision` is the string literal `"alias_of" | "new_entity" | "ambiguous"`; not an enum because System.Text.Json's enum binding is finicky and the validation step rejects unknown values cleanly already.
   - `HubGenerateDto.cs` → **no JSON DTO**. The hub-generate prompt's output IS the Markdown body; the client returns the raw string. `ILlmClient.CompleteAsync<string>(...)` is special-cased to skip JSON parsing when `T == string` (just trim and return the response text). One non-obvious-why comment at the special case is allowed.

6. **`SafeLlmClient` is a typed `HttpClient` against `IngestSaga:Sidecars:Ollama:BaseUrl`.** Pattern: `services.AddHttpClient<ILlmClient, SafeLlmClient>((sp, client) => { client.BaseAddress = ...; client.Timeout = TimeSpan.FromMinutes(2); })` registered alongside the existing Ollama HTTP client. The Ollama call shape:
   ```http
   POST /api/generate
   { "model": "minicpm-v:8b-2.6-q4_K_M",
     "prompt": "<built prompt>",
     "format": "json",
     "stream": false,
     "options": { "temperature": 0, "num_ctx": 8192 } }
   ```
   Response body: `{ "response": "<json-string>", "done": true, "total_duration": <ns>, ... }`. The client parses `response.GetProperty("response").GetString()` as the raw JSON, then `JsonSerializer.Deserialize<T>(rawJson, JsonOptions)` against the DTO. **The `format: "json"` flag is the contract that the response field contains a serialized JSON object**; combined with the prompt's explicit "Respond with JSON ONLY" footer, MiniCPM-V on Ollama returns parseable JSON ~95% of the time per ADR-0044 §2.

7. **Retry on malformed JSON: append a corrective suffix + re-send.** Per ADR-0044 §2 + §6 the retry sequence:
   - Attempt 1: original prompt.
   - Attempt 2 (after `JsonException` or schema mismatch): append `"\n\nYour previous response was not valid JSON. Respond with ONLY valid JSON matching the schema."` to the prompt and re-send.
   - Attempt 3: same suffix, one more try.
   - After `MaxAttempts=3`: throw `LlmStructuredOutputException(promptId, lastError)`. The phase handler catches this, increments the phase's `attempts` counter, and re-schedules with backoff (per the existing dispatcher retry budget); after the phase-level retry budget exhausts, the saga transitions to the matching `failed_<phase>` terminal.
   - **The 3-attempt retry is at the LLM-client layer, separate from the phase-handler's retry budget.** Per-attempt records go into provenance under the same stage with `retry_index = 0, 1, 2`. Phase-handler retries record a separate `stage_attempt` counter. Counting these two retries separately is the only honest way to attribute "did the model produce bad JSON?" vs "did the model just take too long?" in eval.

8. **`UnsafeLlmClient` ships as a NotImplementedException seam.** `class UnsafeLlmClient : ILlmClient { public Task<T> CompleteAsync<T>(...) => throw new NotImplementedException("CLOUD-026 / M7"); }`. The DI factory's `Resolve(settings)` switches on `settings.LlmMode`:
   - `'safe'` → `SafeLlmClient`.
   - `'unsafe_anthropic'` / `'unsafe_openai'` → `UnsafeLlmClient` if a future ticket has registered it; otherwise falls back to `SafeLlmClient` and adds a `llm_mode_fallback=true` entry to provenance. **This is the explicit ADR-0044 §1 "until then, `llm_mode='unsafe'` falls back to `SafeLlmClient` with a warning in provenance" guarantee.**
   - For this ticket, no `UnsafeLlmClient` registration ships; the fallback always fires for unsafe mode. The existing `LlmClientFactory` from CLOUD-002 (which uses `LlmTornado.AsChatClient`) is **deleted** along with the legacy `ChatClientLlm` and `EnrichmentSchema` files — the new factory is purely the safe/fallback switch.

9. **`notes.llm_mode` records the actual mode used per-note, set by `RoutingHandler` at the first LLM call.** The cloud-settings `llm_mode` is the *intent*; the per-note `llm_mode` is what *happened*. If the user has `cloud_settings.llm_mode='unsafe_anthropic'` but `UnsafeLlmClient` isn't registered → routing runs against `SafeLlmClient` → `notes.llm_mode='safe'` AND a provenance `llm_mode_fallback=true` flag fires. Eval can join on `notes.llm_mode` to compare actual-safe vs actual-unsafe runs (zero rows for unsafe until CLOUD-026 ships; documented limitation).

10. **Threshold semantics — per ADR-0044 §3 + §4 verbatim.** All four thresholds live in `appsettings.json:LlmIntelligence:Thresholds`:
    - `RouteAcceptMin=0.5` — below this, `RouteDecisionDto.ProjectEntityId` is treated as `null` regardless of the model's output; `notes.project_id` stays null; `relative_path` stays `Inbox/{noteId}.md`.
    - `MentionMin=0.6` — below this, the candidate mention is dropped (still recorded in provenance for EVAL-005 sensitivity analysis under `dropped_mentions: [{ anchor_text, confidence, reason: "below_min" }]`).
    - `DedupAliasMin=0.8` — above this for `decision='alias_of'`, INSERT mention pointing at `matched_entity_id` + add `anchor_text` to `entities.aliases` if novel + increment `entities.mention_count`.
    - `DedupNewMin=0.6` — above this for `decision='new_entity'`, INSERT new `entities` row with `source='llm', is_provisional=true` + INSERT mention + set `mention_count=1`.
    - `decision='ambiguous'` (any confidence) → drop mention; record `candidates: [<uuid>]` in provenance.
    - Below threshold for either accept-decision → drop mention; record `decision`, `confidence`, `reason: "below_threshold"`.
    - Eval can sweep all four without code changes; EVAL-002/003/005 directly consume them.

11. **pgvector top-K dedup retrieval.** Per-candidate, before invoking `dedup-v1`:
    ```sql
    SELECT id, kind, canonical_name, aliases, description
      FROM entities
     WHERE deleted_at IS NULL
       AND kind = {candidateKind}
     ORDER BY embedding <=> {candidateEmbedding}::vector
     LIMIT 5;
    ```
    The `{candidateEmbedding}` is the embedding of `candidate_canonical` produced by the existing `IEmbeddingClient`. **With the stub embedding client returning zero vectors**, the cosine ordering is deterministic-but-uninformative — the same top-5 rows come back regardless of input. That's acceptable: dedup's correctness comes from the LLM's `decision`, not the candidate set's ranking; with stubs, every dedup call sees the first 5 rows by deleted_at-null + kind-filter ordering, the LLM compares against them, picks `new_entity` for genuinely new + `alias_of` for old. Once real Granite embeddings land (handoff #8 or later), the same SQL becomes informative. **The `<=>` cosine operator is the canonical pgvector cosine-distance operator** (used as `ORDER BY embedding <=> $1` to return nearest neighbors first); register it explicitly via raw SQL since EF Core doesn't have a built-in mapping for `<=>`. Encapsulate in `Infrastructure/Database/EntityVectorQueries.cs` as `static Task<List<EntityNeighbor>> NearestAsync(CloudDbContext db, string kind, float[] emb, int k, CancellationToken ct)`.

12. **`K=5` for dedup retrieval, config-locked.** `appsettings.json:LlmIntelligence:Pgvector:DedupTopK=5`. The dedup prompt is calibrated against ~5 candidates in its `EXISTING SIMILAR ENTITIES` block; bumping K materially changes prompt length and the model's behavior. Tunable, not load-bearing — sweep in eval if dedup accuracy plateaus.

13. **The dedup prompt is invoked one-call-per-candidate, NOT batched.** Per ADR-0044 §"Alternatives considered" #9: batching all candidates into one LLM call makes the response too long for reliable JSON output. Sequential calls are slower but bounded by `MentionMin` (only above-threshold candidates run dedup) and the median 3-mentions-per-note (~3 dedup calls = ~9 s of dedup work, acceptable). The Ollama semaphore (`MaxConcurrency=1`) serializes them naturally — no parallel-call concern.

14. **Per-mention dedup writes happen in a single transaction at the end of the handler.** The handler builds a `List<EntityWriteOp>` (one per accepted mention, holding either "new entity to INSERT" or "existing entity to UPDATE aliases + mention_count"), then runs `db.SaveChangesAsync()` once. **Inside that same transaction**: after all entity ops apply, scan for entities whose `mention_count` newly crosses the materialization threshold (`LlmIntelligence:HubMaterializeMin=3`) AND don't already have a `hub_note_id`. For each, INSERT a hub `notes` row (`is_hub=true, hub_entity_id=<entity.id>, status='pending'`) AND set the entity's `hub_note_id` to the new note's id AND INSERT an `ingest_jobs(kind='hub_regen', note_id=<hub_note.id>, status='composing', scheduled_at=now)`. `pg_notify('ingest_jobs_changed', '')` after commit. **One transaction for everything that the current handler invocation produces** — keeps the "all-terminal" check honest, avoids partial-state visibility.

15. **`HubMaterializeMin=3` default; `source='user'` entities auto-hub regardless.** Per ADR-0045 §F8: user-created entities (not LLM-emitted) are auto-hubbed at creation time. The current `RoutingHandler` already creates `entities` rows with `source='llm'` when projecting; there is no user-create path in this ticket. The auto-hub trigger for `source='user'` is **deferred** — it lands when the plugin's "create entity manually" endpoint exists (post-MVP). For now, all hubs come from the mention-count threshold for `source='llm'` entities.

16. **`HubGenerationHandler` runs as the `composing` phase for `kind='hub_regen'` jobs.** ADR-0044 §3.4: "A new `ingest_jobs` row with `kind='hub_regen'` runs through the saga's `composing → extracting_entities → embedding` phases." The dispatcher's existing branch on `job.Status == Composing` becomes:
    ```csharp
    case IngestJobStatus.Composing:
        return job.Kind == IngestJobKind.HubRegen
            ? hubGenerationHandler.HandleAsync(job, ct)
            : composingHandler.HandleAsync(job, ct);
    ```
    The original `ComposingHandler` (the per-kind composer from handoff #6) handles `kind='capture'` + `kind='reprocess'`. The new `HubGenerationHandler` handles `kind='hub_regen'`. **Same phase name, different handler — dispatch on kind.**

17. **Hub regen seed.** When inserting the hub `notes` row + `ingest_jobs(kind='hub_regen')`:
    - `notes.id` = newly generated.
    - `notes.is_hub = true`.
    - `notes.hub_entity_id = <entity.id>`.
    - `notes.status = 'pending'`.
    - `notes.captured_at = clock.GetCurrentInstant()`.
    - `notes.body_input = ""` (no user-provided body; the prompt synthesizes it).
    - `notes.relative_path = $"_Entities/{entity.Kind}/{entity.CanonicalName}.md"`.
    - `ingest_jobs.kind = 'hub_regen'`.
    - `ingest_jobs.note_id = <new hub note id>`.
    - `ingest_jobs.status = 'composing'` (NOT `'queued'` — skip `extracting_attachments` entirely).
    - `ingest_jobs.scheduled_at = now`.
    The orchestrator's existing `extracting_attachments` claim does NOT pick up `status='composing'` rows; the `composing` claim picks them up the same way it does for capture jobs. The phase transitions `composing → routing → extracting_entities → embedding → succeeded` are reused as-is. **Hub-regen's routing phase is a no-op** (a hub note doesn't get routed to a project — it lives in `_Entities/`; `RoutingHandler` already short-circuits when `note.IsHub == true` — adding that branch is part of this ticket).

18. **Hub-regen's extracting_entities re-extracts mentions from the generated hub body.** The hub note has its own body output, and that body mentions entities (like any other note). `ExtractingEntitiesHandler` runs against it normally. This produces back-references: the hub note for "John Smith" mentions "Acme Corp" → a `mentions` row links the John hub note to the Acme entity. Cross-entity links emerge naturally; no special handler logic. **Caveat:** if hub-regen's extracted_entities crosses ANOTHER entity's mention threshold, it can spawn another hub-regen. Cycle risk: hub A mentions B; B's hub mentions A; both regen. **Mitigation:** the `mention_count` increment from a hub note's mentions counts the same as a capture note's mentions, but the threshold check `mention_count >= 3 AND hub_note_id IS NULL` filters out entities that already have hubs. So A's hub regen can increment B's mention count but only triggers B's hub creation if B didn't have one. After both hubs exist, threshold-crossing on either side does not re-trigger creation. **Re-trigger on update** lives in the diff-aware regen path below.

19. **Diff-aware regen — when a hub already exists and a new mention lands.** ADR-0044 §3.4 + plan §B4: subsequent hub-regen calls pass the existing body as `previous_body`. Trigger: when `ExtractingEntitiesHandler` writes a new mention against an entity that **already has a hub**, INSERT a *new* `ingest_jobs(kind='hub_regen')` for that hub note. The hub-regen handler reads the hub note's current `body_output` and passes it as `previous_body` to `hub-generate-v1`. The prompt's `IF previous_body IS NOT null` branch fires; the LLM updates additively. **Throttling:** if a hub-regen is already in-flight for a given hub (active row in `ix_ingest_jobs_active_per_note`), no second one is inserted — the existing in-flight job will see the latest mentions when it runs `extracting_entities` next. The `ix_ingest_jobs_active_per_note` partial-unique index from handoff #1 enforces this; an INSERT with the same `note_id` while the prior job is in-flight throws a unique constraint violation, which the handler catches and swallows (log at info level; "hub regen already queued"). **This is the right semantics** — coalesces bursts of mentions into a single regen pass.

20. **Hub-generate prompt input shape — locked to ADR-0044 §3.4.** The handler builds:
    ```csharp
    var inputContext = new
    {
        entity = new
        {
            kind = entity.Kind,
            canonical_name = entity.CanonicalName,
            aliases = entity.Aliases,
        },
        mentions = recentMentions.Select(m => new
        {
            note_title = m.NoteTitle,                 // notes.relative_path filename
            note_captured_at = m.NoteCapturedAt.ToString("uuuu-MM-ddTHH:mm:ss'Z'", InvariantCulture),
            surrounding_text = m.SurroundingText,     // 200 chars on each side of the anchor
        }).ToList(),
        previous_body = hubNote.BodyOutput,           // null on first regen
    };
    ```
    The mentions are pulled via:
    ```sql
    SELECT m.*, n.captured_at, n.relative_path, n.body_output
      FROM mentions m
      JOIN notes n ON n.id = m.note_id
     WHERE m.entity_id = {entity.Id}
       AND n.deleted_at IS NULL
       AND n.is_hub = false
     ORDER BY n.captured_at DESC
     LIMIT {LlmIntelligence:HubMentionWindow:=20};
    ```
    The 200-char `surrounding_text` is computed from `notes.body_output` + `mentions.start_offset` (`body_output.Substring(Math.Max(0, start-200), Math.Min(body_output.Length, end+200) - Math.Max(0, start-200))`). If `body_output` is null (compose failed for that source note), skip the mention. The window of 20 is config-tunable.

21. **The hub-generate prompt returns free-form Markdown, NOT JSON.** Per ADR-0044 §3.4. The `SafeLlmClient.CompleteAsync<string>` special case sends `format: "json"` REMOVED from the Ollama call (it's only needed for structured output) and returns the response text trimmed. Test that this special case actually doesn't break the others (the four-prompt registry has three `T : record` outputs and one `T == string` output; the `if (typeof(T) == typeof(string))` branch handles the last).

22. **Provenance integration — every LLM call writes an event.** The `IIngestEventBus.PublishAsync` writes into `ingest_jobs.events_log`. New event kinds for this ticket:
    - `llm_route` — fired by `RoutingHandler` after the route-v1 call.
    - `llm_extract` — fired by `ExtractingEntitiesHandler` after the extract-v1 call (one per call).
    - `llm_dedup` — fired by `ExtractingEntitiesHandler` per candidate dedup call (one per mention).
    - `llm_hub_generate` — fired by `HubGenerationHandler` after the hub-generate-v1 call.
    Payload shape (one row per call, per ADR-0044 §5 + ADR-0042 §10c):
    ```json
    {
      "stage": "llm_route",
      "prompt_id": "route-v1",
      "model": "minicpm-v",
      "model_version": "8b-2.6-q4_K_M",
      "llm_mode": "safe",
      "duration_ms": 4120,
      "retry_index": 0,
      "decision": "project:9b3c4f...",
      "confidence": 0.87,
      "rationale": "the note mentions Acme Corp twice and discusses..."
    }
    ```
    Failures (LLM exhausted retries) record `decision: "failed"` + `error: "..."`. **Inside `ProvenanceMaterializer` (which lands its compose_template field in handoff #6), add a roll-up `llm_calls: [{stage, prompt_id, retry_index, confidence}, ...]` field at terminal time** — this is the field eval scripts iterate.

23. **`notes.tags` is populated by the new route-v1 prompt? — No.** The existing `RoutingHandler` populated `notes.tags` from the merged enrichment. The new `route-v1` prompt does NOT emit tags (the prompt's output schema is just `{project_entity_id, confidence, rationale}`). **Drop the `tags` write path entirely** — `notes.tags` stays null until a future ticket adds tag extraction. The existing `notes.tags` column stays in the schema (no migration); just nothing writes it. If eval shows tags would help discoverability, add a fifth prompt (`tag-v1`) in a follow-up; not in scope here.

24. **Wikilink anchor splicing into rendered Markdown is deferred.** The pre-existing `RoutingHandler` populated `BodyAnchors` + `AttachmentAnchors` which a future `WikilinkSplicer` (not built in this ticket; not built in handoff #6 either) would splice into the rendered body. The new `extract-v1` prompt produces `MentionCandidateDto` with `StartOffset` + `EndOffset` — those offsets are stored in `mentions.start_offset` / `mentions.end_offset` for the future splicer. Splicing itself lands when handoff #6's `compose-v1` bumps to `compose-v2` (per cloud-compose-phase handoff #6 §"What handoffs #7+ inherit"). **In this ticket: write the offsets; do not splice.** The body remains wikilink-free; mentions are stored as a side-table only.

25. **Cancellation check inside the dedup loop.** A note's `deleted_at` flips mid-dedup-loop (user clicked Delete while the saga was running). Per ADR-0042 §"Negative / accepted costs" — mid-phase cancellation is best-effort. The dedup loop checks `note.DeletedAt IS NOT NULL` at the top of each iteration; if set, it breaks out, the handler's transition becomes "go back to dispatcher, dispatcher detects cancellation, transitions to dead_lettered." No mid-call abort of the LLM HTTP request (Ollama doesn't cancel on connection close mid-stream reliably).

26. **No streaming.** Per ADR-0044 §8. Every LLM call is `stream: false`. The token-streaming-to-SSE optimization is a deferred future-work hook.

## Scope boundary (precise)

Three passes. ~3.5–4 person-days total. Each pass produces a runnable end-to-end and the regression anchor catches breakage between them.

### Pass A — `ILlmClient` refactor + `SafeLlmClient` + prompt registry + `RoutingHandler` real (~1.5d)

The `ILlmClient` interface rotates from the merged-enrichment shape to the generic `CompleteAsync<T>` shape. `SafeLlmClient` is the only registered implementation. The prompt registry's four constants + four DTOs are scaffolded (all four constants land in Pass A so the codebase has them as code; only `route-v1` is wired through a handler in Pass A — `extract-v1`/`dedup-v1` wire in Pass B; `hub-generate-v1` in Pass C). `RoutingHandler` is rewritten to call `route-v1` and write `notes.project_id` + `relative_path` based on the threshold.

**1. `Infrastructure/Llm/ILlmClient.cs` — REPLACE the existing interface.**
   ```csharp
   public interface ILlmClient
   {
       string Mode { get; }
       string ModelName { get; }
       string ModelVersion { get; }
       Task<T> CompleteAsync<T>(
           PromptId promptId,
           object inputContext,
           CancellationToken ct) where T : class;
   }

   public sealed record PromptId(string Name, string Version)
   {
       public override string ToString() => $"{Name}-{Version}";
   }

   public sealed class LlmStructuredOutputException : Exception
   {
       public PromptId PromptId { get; }
       public int Attempts { get; }
       public LlmStructuredOutputException(PromptId pid, int attempts, Exception? inner = null)
           : base($"LLM output failed to parse after {attempts} attempts for prompt {pid}", inner)
       {
           PromptId = pid;
           Attempts = attempts;
       }
   }
   ```
   - Delete `CompositeEnrichmentRequest`, `CompositeEnrichmentResult`, `WikilinkAnchor`, `AttachmentAnchors`, `AttachmentText` records.
   - Delete `ChatClientLlm.cs`, `EnrichmentSchema.cs`, `NoOpLlmClient.cs`, `LlmOptions.cs` (the existing LlmTornado-backed implementation). The package references for `LlmTornado` and `LlmTornado.Microsoft.Extensions.AI` come out of `Directory.Packages.props` + `ThanyMarcus.Cloud.Api.csproj`.
   - Delete `LlmClientFactory.cs` + `ILlmClientFactory.cs` (replaced by the new factory below).

**2. `Infrastructure/Llm/Prompts/PromptTemplates.cs` — NEW.**
   ```csharp
   public static class PromptTemplates
   {
       public const string Route_V1 = """
           You are a note-routing assistant. Given a note's content and a list of
           projects, decide which project this note belongs to.

           PROJECTS:
           {0}

           NOTE CONTENT:
           {1}

           Respond with JSON ONLY:
           { "project_entity_id": "<uuid or null>", "confidence": <0.0-1.0>, "rationale": "<one-sentence reasoning>" }
           """;

       public const string Extract_V1 = """ ... """;  // ADR-0044 §3.2 verbatim
       public const string Dedup_V1 = """ ... """;    // ADR-0044 §3.3 verbatim
       public const string HubGenerate_V1 = """ ... """;  // ADR-0044 §3.4 verbatim
   }
   ```
   - Verbatim transcription of ADR-0044 §3.1 through §3.4. Each template uses `{0}, {1}, ...` positional markers; no template-language dependency.
   - The MaterializeBuilders + per-prompt builder methods live in `PromptBuilder.cs` alongside the templates.

**3. `Infrastructure/Llm/Prompts/PromptBuilder.cs` — NEW.** One builder per prompt, taking the relevant input shape and returning the formatted prompt string:
   ```csharp
   public static class PromptBuilder
   {
       public static string BuildRoute(IReadOnlyList<ProjectListItem> projects, string bodyExcerpt)
       { /* format Route_V1 with projects + bodyExcerpt */ }

       public static string BuildExtract(string body) { /* ... */ }
       public static string BuildDedup(MentionCandidateDto candidate, string surroundingText,
                                       IReadOnlyList<EntityNeighbor> neighbors) { /* ... */ }
       public static string BuildHubGenerate(Entity entity, IReadOnlyList<HubMentionContext> mentions,
                                              string? previousBody) { /* ... */ }
   }

   public sealed record ProjectListItem(Guid Id, string CanonicalName, string? Description);
   public sealed record EntityNeighbor(Guid Id, string Kind, string CanonicalName,
                                        IReadOnlyList<string> Aliases, string? Description);
   public sealed record HubMentionContext(string NoteTitle, string NoteCapturedAt,
                                           string SurroundingText);
   ```

**4. `Infrastructure/Llm/Prompts/RouteDecisionDto.cs` + `EntityExtractionDto.cs` + `DedupDecisionDto.cs` — NEW.** Records as in design-decision #5 above. `[JsonPropertyName("project_entity_id")]` etc. attributes match the prompt's JSON output schema verbatim.

**5. `Infrastructure/Llm/SafeLlmClient.cs` — NEW.**
   ```csharp
   public sealed class SafeLlmClient(
       HttpClient httpClient,
       IOptions<LlmIntelligenceOptions> options,
       IClock clock,
       ILogger<SafeLlmClient> log) : ILlmClient
   {
       public string Mode         => "safe";
       public string ModelName    => "minicpm-v";
       public string ModelVersion => options.Value.OllamaTag;

       public async Task<T> CompleteAsync<T>(PromptId promptId, object inputContext, CancellationToken ct)
           where T : class
       {
           var promptText = BuildPromptText(promptId, inputContext);
           var maxAttempts = options.Value.Retry.MaxAttempts;
           var suffix = "";
           for (int attempt = 0; attempt < maxAttempts; attempt++)
           {
               var requestBody = new
               {
                   model   = options.Value.OllamaTag,
                   prompt  = promptText + suffix,
                   stream  = false,
                   format  = typeof(T) == typeof(string) ? null : "json",
                   options = new { temperature = 0, num_ctx = 8192 }
               };
               var start = clock.GetCurrentInstant();
               var resp = await httpClient.PostAsJsonAsync("/api/generate", requestBody, ct);
               var elapsed = clock.GetCurrentInstant() - start;
               if (!resp.IsSuccessStatusCode)
               {
                   var err = await resp.Content.ReadAsStringAsync(ct);
                   throw new LlmStructuredOutputException(promptId, attempt + 1,
                       new HttpRequestException($"Ollama returned {(int)resp.StatusCode}: {err}"));
               }
               var ollamaResp = await resp.Content.ReadFromJsonAsync<OllamaGenerateResponse>(ct);
               try
               {
                   if (typeof(T) == typeof(string))
                       return (T)(object)(ollamaResp!.Response.Trim());
                   var parsed = JsonSerializer.Deserialize<T>(ollamaResp!.Response, JsonOpts)
                                ?? throw new JsonException("null deserialization");
                   return parsed;
               }
               catch (JsonException ex)
               {
                   log.LogWarning(ex, "LLM JSON parse failed on attempt {Attempt} for {PromptId}",
                                  attempt, promptId);
                   suffix = "\n\nYour previous response was not valid JSON. Respond with ONLY valid JSON matching the schema.";
               }
           }
           throw new LlmStructuredOutputException(promptId, maxAttempts);
       }
   }
   ```
   The `OllamaGenerateResponse` record is `record OllamaGenerateResponse(string Response, bool Done, long? TotalDuration)`. JsonOpts uses snake_case naming + lenient enums.

**6. `Infrastructure/Llm/LlmClientFactory.cs` — REWRITE.**
   ```csharp
   public sealed class LlmClientFactory : ILlmClientFactory
   {
       private readonly SafeLlmClient safe;
       private readonly IServiceProvider sp;

       public LlmClientFactory(SafeLlmClient safe, IServiceProvider sp) { this.safe = safe; this.sp = sp; }

       public ILlmClient Resolve(CloudSettings settings, out bool fallbackToSafe)
       {
           fallbackToSafe = false;
           if (settings.LlmMode is LlmModes.Safe) return safe;
           var unsafeClient = sp.GetService<IUnsafeLlmClient>();
           if (unsafeClient is null) { fallbackToSafe = true; return safe; }
           return unsafeClient;
       }
   }
   public interface IUnsafeLlmClient : ILlmClient { }
   ```
   The `IUnsafeLlmClient` marker interface is not registered in this ticket; `Resolve` always returns `safe` with `fallbackToSafe=true` when settings say unsafe.

**7. `Infrastructure/Llm/LlmIntelligenceOptions.cs` — NEW.**
   ```csharp
   public sealed class LlmIntelligenceOptions
   {
       public string OllamaTag { get; init; } = "minicpm-v:8b-2.6-q4_K_M";
       public ThresholdsOptions Thresholds { get; init; } = new();
       public PgvectorOptions  Pgvector    { get; init; } = new();
       public RetryOptions     Retry       { get; init; } = new();
       public int HubMaterializeMin    { get; init; } = 3;
       public int HubMentionWindow     { get; init; } = 20;
       public int SurroundingTextChars { get; init; } = 200;
       public int RoutingProjectsMax   { get; init; } = 50;
   }

   public sealed class ThresholdsOptions
   {
       public double RouteAcceptMin { get; init; } = 0.5;
       public double MentionMin     { get; init; } = 0.6;
       public double DedupAliasMin  { get; init; } = 0.8;
       public double DedupNewMin    { get; init; } = 0.6;
   }
   public sealed class PgvectorOptions { public int DedupTopK { get; init; } = 5; }
   public sealed class RetryOptions    { public int MaxAttempts { get; init; } = 3;
                                          public int BackoffSecondsBase { get; init; } = 2; }
   ```

**8. `appsettings.json` — APPEND `LlmIntelligence` section** (mirrors the defaults above; `OllamaTag` shares its value with `IngestSaga:Models:Vlm:OllamaTag` — pick a single source of truth and have both keys read it via configuration binding; the cleanest is to read `IngestSaga:Models:Vlm:OllamaTag` from `LlmIntelligenceOptions.OllamaTag` via a one-line `services.Configure<LlmIntelligenceOptions>(cfg.GetSection("LlmIntelligence"))` + `services.PostConfigure<LlmIntelligenceOptions>(o => o.OllamaTag = cfg["IngestSaga:Models:Vlm:OllamaTag"] ?? o.OllamaTag)`.

**9. `Features/Processing/Phases/RoutingHandler.cs` — REWRITE.**
   ```csharp
   public sealed class RoutingHandler(
       CloudDbContext db,
       ILlmClientFactory llmFactory,
       IIngestEventBus eventBus,
       IOptions<LlmIntelligenceOptions> opts,
       IClock clock,
       JobStateTransitions transitions) : IPhaseHandler
   {
       public string Phase => IngestJobStatus.Routing;

       public async Task HandleAsync(IngestJob job, CancellationToken ct)
       {
           var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);

           // Hub-regen jobs skip routing.
           if (note.IsHub)
           {
               await transitions.TransitionAsync(job, IngestJobStatus.ExtractingEntities, null, false, false, ct);
               return;
           }

           var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
           var llm = llmFactory.Resolve(settings, out var fellBackToSafe);

           var projects = await db.Entities
               .Where(e => e.Kind == EntityKind.Project && e.DeletedAt == null)
               .OrderByDescending(e => e.UpdatedAt)
               .Take(opts.Value.RoutingProjectsMax)
               .Select(e => new ProjectListItem(e.Id, e.CanonicalName, e.Description))
               .ToListAsync(ct);

           var bodyExcerpt = Truncate(note.BodyOutput ?? note.BodyInput, 1500);

           var start = clock.GetCurrentInstant();
           RouteDecisionDto decision;
           try
           {
               decision = await llm.CompleteAsync<RouteDecisionDto>(
                   new PromptId("route", "v1"),
                   new { projects, body_excerpt = bodyExcerpt },
                   ct);
           }
           catch (LlmStructuredOutputException ex)
           {
               await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_route", new PromptId("route","v1"),
                   llm, fellBackToSafe, clock.GetCurrentInstant() - start, attemptsUsed: ex.Attempts,
                   decision: "failed", confidence: null, rationale: null, error: ex.Message), ct);
               throw;
           }

           note.LlmMode = llm.Mode;
           if (decision.ProjectEntityId is { } pid &&
               decision.Confidence >= opts.Value.Thresholds.RouteAcceptMin)
           {
               var project = await db.Entities.SingleOrDefaultAsync(
                   e => e.Id == pid && e.Kind == EntityKind.Project && e.DeletedAt == null, ct);
               if (project is not null)
               {
                   note.ProjectId    = project.Id;
                   note.RelativePath = $"Projects/{project.CanonicalName}/{note.Id}.md";
                   project.UpdatedAt = clock.GetCurrentInstant();
               }
           }

           await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_route", new PromptId("route","v1"),
               llm, fellBackToSafe, clock.GetCurrentInstant() - start, attemptsUsed: 1,
               decision: note.ProjectId is null ? "unrouted" : $"project:{note.ProjectId}",
               confidence: decision.Confidence, rationale: decision.Rationale, error: null), ct);

           await db.SaveChangesAsync(ct);
           await transitions.TransitionAsync(job, IngestJobStatus.ExtractingEntities, null, false, false, ct);
       }
   }
   ```
   The `BuildLlmEvent` helper (in a new `Infrastructure/Llm/LlmEventBuilder.cs`) produces an `IngestEvent` with the §22 payload shape. `Truncate` lives at the same helper class.

**10. `ExtractingEntitiesHandler.cs` for Pass A — strip out the merged-call body; insert a no-op transition.** The handler will be fully rewritten in Pass B; for the duration of Pass A, replace its body with:
   ```csharp
   public async Task HandleAsync(IngestJob job, CancellationToken ct)
   {
       var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
       // Pass A: pass-through. Pass B rewires to extract-v1 + dedup-v1.
       await transitions.TransitionAsync(job, IngestJobStatus.Embedding, null, false, false, ct);
   }
   ```
   This keeps the saga green during Pass A while the merged `EnrichCompositeAsync` is gone. **One TODO marker allowed at the top of the file**: `// TODO PASS-B: real extract+dedup wiring (CLOUD-LLM-INTELLIGENCE handoff #7).` This is the rare TODO permitted because Pass B in this same handoff removes it.

**11. `ComposingHandler.cs` — pass-through dispatch when `kind != hub_regen`.** No code change in Pass A; the existing compose-v1 logic from handoff #6 still runs. Pass C adds the kind-branch.

**12. `Program.cs` DI updates** (replace the existing LLM registrations):
   ```csharp
   services.Configure<LlmIntelligenceOptions>(cfg.GetSection("LlmIntelligence"));
   services.PostConfigure<LlmIntelligenceOptions>(o =>
       o.OllamaTag = cfg["IngestSaga:Models:Vlm:OllamaTag"] ?? o.OllamaTag);
   services.AddHttpClient<SafeLlmClient>((sp, client) =>
   {
       var sidecars = sp.GetRequiredService<IOptions<SidecarOptions>>().Value;
       client.BaseAddress = new Uri(sidecars.Ollama.BaseUrl);
       client.Timeout = TimeSpan.FromMinutes(2);
   });
   services.AddSingleton<ILlmClient>(sp => sp.GetRequiredService<SafeLlmClient>());
   services.AddSingleton<ILlmClientFactory, LlmClientFactory>();
   // Removed: services.AddSingleton<LlmOptions>(...) + AddSingleton<ILlmClientFactory, old LlmClientFactory>().
   ```

**13. Tests for Pass A:**
   - `Infrastructure/Llm/SafeLlmClientTests.cs` — `HttpMessageHandler` fake; happy path returns parsed DTO; malformed-JSON-then-good-JSON → 2 attempts → success; 3 malformed in a row → `LlmStructuredOutputException(attempts=3)`; HTTP 5xx → `LlmStructuredOutputException`; `T == string` skips JSON parsing.
   - `Infrastructure/Llm/Prompts/PromptBuilderTests.cs` — golden-string output for each prompt against a representative input; verifies positional-marker substitution + empty-list edge cases.
   - `Infrastructure/Llm/Prompts/RouteDecisionDtoTests.cs` — JSON round-trip; null project_entity_id; missing rationale (validation throws).
   - `Features/Processing/Phases/RoutingHandlerTests.cs` — fake `ILlmClient` returning `{project_entity_id: <existing-id>, confidence: 0.9, rationale: "..."}` → handler writes `notes.project_id` + `relative_path = Projects/...`; confidence below threshold → `project_id` stays null + `relative_path = Inbox/{id}.md`; `is_hub=true` note short-circuits to ExtractingEntities; LLM throws → handler throws (dispatcher's retry budget catches); event bus receives one `llm_route` event with the right payload.
   - `Features/Processing/Phases/RoutingHandlerNoLlmFallbackTests.cs` — `cloud_settings.llm_mode='unsafe_anthropic'`, no `IUnsafeLlmClient` registered → factory returns safe + `fallbackToSafe=true` → event has `llm_mode_fallback=true`.

→ At end of Pass A: a capture's `routing` phase calls real Ollama (against `route-v1`), writes `notes.project_id` when confident, falls back to `Inbox/` otherwise, and records the call in events_log. `extracting_entities` is a no-op pass-through; `embedding` continues with zero vectors. The note lands `ready` under the right project folder.

### Pass B — `ExtractingEntitiesHandler` real (extract-v1 + per-mention dedup-v1 + auto-hub trigger) (~1d)

The Pass-A pass-through in `ExtractingEntitiesHandler` is **removed**. Real extract + dedup writes mentions and increments entity mention_count; the hub-spawn block fires at the end.

**1. `Features/Entities/EntityVectorQueries.cs` — NEW.**
   ```csharp
   public static class EntityVectorQueries
   {
       public static async Task<List<EntityNeighbor>> NearestAsync(
           CloudDbContext db, string kind, float[] candidate, int k, CancellationToken ct)
       {
           // Raw SQL because Pgvector EFC does not surface the <=> cosine operator.
           var conn = db.Database.GetDbConnection();
           if (conn.State != ConnectionState.Open) await conn.OpenAsync(ct);
           await using var cmd = conn.CreateCommand();
           cmd.CommandText = """
               SELECT id, kind, canonical_name, aliases, description
                 FROM entities
                WHERE deleted_at IS NULL
                  AND kind = @kind
                ORDER BY embedding <=> @emb::vector
                LIMIT @k
               """;
           cmd.Parameters.Add(new NpgsqlParameter("@kind", kind));
           cmd.Parameters.Add(new NpgsqlParameter("@emb", new Vector(candidate)));
           cmd.Parameters.Add(new NpgsqlParameter("@k", k));
           await using var rdr = await cmd.ExecuteReaderAsync(ct);
           var list = new List<EntityNeighbor>(k);
           while (await rdr.ReadAsync(ct))
               list.Add(new EntityNeighbor(
                   rdr.GetGuid(0), rdr.GetString(1), rdr.GetString(2),
                   (string[])rdr.GetValue(3), rdr.IsDBNull(4) ? null : rdr.GetString(4)));
           return list;
       }
   }
   ```
   The raw-SQL path is intentional — `Pgvector.EntityFrameworkCore` 0.x doesn't surface the `<=>` operator into LINQ. Documented as a one-line non-obvious-why comment at the method top.

**2. `Features/Processing/Phases/ExtractingEntitiesHandler.cs` — REWRITE.** Pseudocode for the handler body:
   ```csharp
   public async Task HandleAsync(IngestJob job, CancellationToken ct)
   {
       var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
       if (note.DeletedAt is not null) { /* dispatcher will cancel */ return; }

       var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
       var llm = llmFactory.Resolve(settings, out var fellBackToSafe);

       var body = note.BodyOutput ?? note.BodyInput;

       // 1. Extract phase
       var extractStart = clock.GetCurrentInstant();
       EntityExtractionDto extraction;
       try
       {
           extraction = await llm.CompleteAsync<EntityExtractionDto>(
               new PromptId("extract", "v1"),
               new { body },
               ct);
       }
       catch (LlmStructuredOutputException ex)
       {
           await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_extract", ..., decision: "failed",
                                                      error: ex.Message), ct);
           throw;
       }
       await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_extract", ..., decision: $"mentions:{extraction.Mentions.Count}"), ct);

       // 2. Filter by MentionMin
       var thresholds = opts.Value.Thresholds;
       var droppedBelowMin = new List<MentionCandidateDto>();
       var candidates = new List<MentionCandidateDto>();
       foreach (var m in extraction.Mentions)
       {
           if (m.Confidence < thresholds.MentionMin) { droppedBelowMin.Add(m); continue; }
           candidates.Add(m);
       }

       // 3. Per-candidate dedup
       var newMentions = new List<Mention>();
       var hubSpawnCandidates = new HashSet<Guid>();  // entity ids whose mention_count crosses threshold

       foreach (var cand in candidates)
       {
           if (note.DeletedAt is not null) break;     // cancellation check

           // 3a. Compute candidate embedding (uses stub embedding client until handoff #8)
           var candEmb = await embeddings.EmbedAsync(cand.CandidateCanonical, ct);

           // 3b. pgvector top-K
           var neighbors = await EntityVectorQueries.NearestAsync(
               db, cand.CandidateKind, candEmb, opts.Value.Pgvector.DedupTopK, ct);

           // 3c. dedup-v1 call
           var surrounding = ExtractSurrounding(body, cand.StartOffset, cand.EndOffset,
                                                  opts.Value.SurroundingTextChars);
           var dedupStart = clock.GetCurrentInstant();
           DedupDecisionDto dedup;
           try { dedup = await llm.CompleteAsync<DedupDecisionDto>(
                       new PromptId("dedup", "v1"),
                       new { candidate = cand, surrounding_text = surrounding, similar_entities = neighbors },
                       ct); }
           catch (LlmStructuredOutputException ex)
           {
               await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_dedup", ..., decision: "failed",
                                                          error: ex.Message), ct);
               continue;   // skip this mention; do not fail the whole phase
           }
           await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_dedup", ...,
                                                      decision: dedup.Decision, confidence: dedup.Confidence,
                                                      rationale: dedup.Rationale), ct);

           // 3d. Apply decision per ADR-0045 §F6
           Entity? targetEntity = null;
           if (dedup.Decision == "alias_of" && dedup.MatchedEntityId is { } mid &&
               dedup.Confidence >= thresholds.DedupAliasMin)
           {
               targetEntity = await db.Entities.SingleOrDefaultAsync(e => e.Id == mid && e.DeletedAt == null, ct);
               if (targetEntity is not null)
               {
                   if (!targetEntity.Aliases.Contains(cand.AnchorText, StringComparer.OrdinalIgnoreCase))
                       targetEntity.Aliases = targetEntity.Aliases.Append(cand.AnchorText).ToArray();
               }
           }
           else if (dedup.Decision == "new_entity" && dedup.Confidence >= thresholds.DedupNewMin)
           {
               targetEntity = new Entity
               {
                   Id            = Guid.CreateVersion7(),
                   Kind          = cand.CandidateKind,
                   CanonicalName = cand.CandidateCanonical,
                   Aliases       = cand.Aliases.ToArray(),
                   Source        = EntitySource.Llm,
                   IsProvisional = true,
                   Embedding     = new Vector(candEmb),
               };
               db.Entities.Add(targetEntity);
           }
           // else: ambiguous or below-threshold → drop; record in provenance

           if (targetEntity is null) continue;
           targetEntity.MentionCount += 1;
           if (targetEntity.MentionCount >= opts.Value.HubMaterializeMin &&
               targetEntity.HubNoteId is null)
           {
               hubSpawnCandidates.Add(targetEntity.Id);
           }
           newMentions.Add(new Mention
           {
               Id          = Guid.CreateVersion7(),
               EntityId    = targetEntity.Id,
               NoteId      = note.Id,
               AnchorText  = cand.AnchorText,
               StartOffset = cand.StartOffset,
               EndOffset   = cand.EndOffset,
               Confidence  = (float)cand.Confidence,
               CreatedAt   = clock.GetCurrentInstant(),
           });
       }
       db.Mentions.AddRange(newMentions);

       // 4. Hub spawn — for each entity whose mention_count just crossed
       foreach (var entityId in hubSpawnCandidates)
       {
           var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
           await HubMaterializer.MaterializeAsync(db, entity, clock, ct);
       }

       // 5. Diff-aware regen — for any entity that already has a hub AND got a new mention
       foreach (var entityId in newMentions.Select(m => m.EntityId).Distinct())
       {
           if (hubSpawnCandidates.Contains(entityId)) continue;  // freshly materialized
           var entity = await db.Entities.SingleAsync(e => e.Id == entityId, ct);
           if (entity.HubNoteId is not null)
               await HubMaterializer.EnqueueRegenAsync(db, entity, clock, ct);
       }

       await db.SaveChangesAsync(ct);
       await transitions.TransitionAsync(job, IngestJobStatus.Embedding, null, false, false, ct);
   }
   ```
   The `HubMaterializer` static helper class (NEW at `Features/Entities/HubMaterializer.cs`) implements the seed flow from §17 + §19 above. `MaterializeAsync` inserts the hub note + initial ingest_jobs row. `EnqueueRegenAsync` inserts only the ingest_jobs row, catching `DbUpdateException` if `ix_ingest_jobs_active_per_note` already holds an in-flight job for that hub.

**3. `Features/Entities/HubMaterializer.cs` — NEW.**
   ```csharp
   public static class HubMaterializer
   {
       public static async Task MaterializeAsync(CloudDbContext db, Entity entity, IClock clock, CancellationToken ct)
       {
           var now = clock.GetCurrentInstant();
           var hubNote = new Note
           {
               Id            = Guid.CreateVersion7(),
               CapturedAt    = now,
               BodyInput     = "",
               IsHub         = true,
               HubEntityId   = entity.Id,
               RelativePath  = $"_Entities/{entity.Kind}/{entity.CanonicalName}.md",
               Status        = NoteStatus.Pending,
           };
           db.Notes.Add(hubNote);
           entity.HubNoteId = hubNote.Id;
           db.IngestJobs.Add(new IngestJob
           {
               Id          = Guid.CreateVersion7(),
               NoteId      = hubNote.Id,
               Kind        = IngestJobKind.HubRegen,
               Status      = IngestJobStatus.Composing,    // skip extracting_attachments
               ScheduledAt = now,
           });
       }

       public static async Task EnqueueRegenAsync(CloudDbContext db, Entity entity, IClock clock, CancellationToken ct)
       {
           if (entity.HubNoteId is null) return;
           try
           {
               db.IngestJobs.Add(new IngestJob
               {
                   Id          = Guid.CreateVersion7(),
                   NoteId      = entity.HubNoteId.Value,
                   Kind        = IngestJobKind.HubRegen,
                   Status      = IngestJobStatus.Composing,
                   ScheduledAt = clock.GetCurrentInstant(),
               });
               // SaveChanges happens at the handler's outer transaction; an active-per-note
               // collision throws DbUpdateException which the handler catches.
           }
           catch (DbUpdateException) { /* coalesced — existing in-flight regen will pick up new mention */ }
       }
   }
   ```
   The `DbUpdateException` catch is paranoid — at this point `SaveChanges` hasn't run yet, so the actual collision detection lives in the handler's try-around-SaveChanges block. Move the catch to the handler if cleaner; the placement is a refinement.

**4. Tests for Pass B:**
   - `Features/Processing/Phases/ExtractingEntitiesHandlerTests.cs` — fake LLM emits `[mention(person, "John")]` → handler runs dedup → fake LLM returns `new_entity` → handler INSERTs `Entity` + `Mention`; below-MentionMin candidate dropped + recorded; ambiguous decision dropped + recorded; alias_of with existing entity → mention points at existing + alias added.
   - `Features/Entities/HubMaterializerTests.cs` — entity with mention_count crossing threshold + null hub_note_id → MaterializeAsync inserts hub note + ingest_jobs; entity with hub already → EnqueueRegenAsync inserts second ingest_jobs row; active-per-note collision → second insert raises DbUpdateException → handler swallows.
   - `Features/Entities/EntityVectorQueriesTests.cs` — Testcontainers Postgres with seeded entities + zero vectors → NearestAsync returns K rows of the matching kind in deleted_at-null order.
   - `Features/Processing/Phases/ExtractingEntitiesCancellationTests.cs` — set `notes.deleted_at` mid-loop → handler breaks; mentions inserted before the break persist; the partial state is acceptable per ADR-0042 §"Negative".

→ At end of Pass B: a capture's `extracting_entities` phase makes 1 extract call + N dedup calls, inserts `mentions` rows + new/aliased `entities`, and spawns hub_regen jobs when thresholds cross. Hub regen jobs are queued but **`HubGenerationHandler` is not yet a real handler** — Pass C lands it. Hub jobs will sit at status='composing' until Pass C; the regression anchor's "ready note" assertion still passes for capture jobs (hub jobs aren't expected to terminate in Pass B).

### Pass C — `HubGenerationHandler` + dispatch on `job.Kind` + cross-handler regression (~1–1.5d)

The composing phase branches on `job.Kind`. `HubGenerationHandler` (the new handler) drives `kind='hub_regen'` through compose → routing-skip → entities → embedding. The regression anchor from handoff #6 + the new hub-spawn assertions are wired.

**1. `Features/Processing/Phases/HubGenerationHandler.cs` — NEW.**
   ```csharp
   public sealed class HubGenerationHandler(
       CloudDbContext db,
       ILlmClientFactory llmFactory,
       IIngestEventBus eventBus,
       IOptions<LlmIntelligenceOptions> opts,
       IClock clock,
       JobStateTransitions transitions) : IPhaseHandler
   {
       public string Phase => IngestJobStatus.Composing;   // SAME phase name; dispatcher branches by kind

       public async Task HandleAsync(IngestJob job, CancellationToken ct)
       {
           var note = await db.Notes.SingleAsync(n => n.Id == job.NoteId, ct);
           if (!note.IsHub) throw new InvalidOperationException("hub-regen job for non-hub note");
           if (note.HubEntityId is null) throw new InvalidOperationException("hub note missing hub_entity_id");

           var entity = await db.Entities.SingleAsync(e => e.Id == note.HubEntityId.Value, ct);
           var settings = await db.CloudSettings.SingleAsync(s => s.Id == CloudSettings.SingletonId, ct);
           var llm = llmFactory.Resolve(settings, out var fellBackToSafe);

           // Pull recent mentions for the entity
           var recent = await (from m in db.Mentions
                                 join n in db.Notes on m.NoteId equals n.Id
                                where m.EntityId == entity.Id
                                  && n.DeletedAt == null
                                  && !n.IsHub
                                orderby n.CapturedAt descending
                                select new { m, n })
               .Take(opts.Value.HubMentionWindow)
               .ToListAsync(ct);

           var contexts = recent
               .Where(x => !string.IsNullOrEmpty(x.n.BodyOutput))
               .Select(x => new HubMentionContext(
                   NoteTitle:        x.n.RelativePath ?? x.n.Id.ToString(),
                   NoteCapturedAt:   x.n.CapturedAt.ToString("uuuu-MM-ddTHH:mm:ss'Z'", InvariantCulture),
                   SurroundingText:  ExtractSurrounding(x.n.BodyOutput!, x.m.StartOffset, x.m.EndOffset,
                                                          opts.Value.SurroundingTextChars)))
               .ToList();

           var start = clock.GetCurrentInstant();
           string markdown;
           try
           {
               markdown = await llm.CompleteAsync<string>(
                   new PromptId("hub-generate", "v1"),
                   new { entity, mentions = contexts, previous_body = note.BodyOutput },
                   ct);
           }
           catch (LlmStructuredOutputException ex)
           {
               await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_hub_generate", ...,
                                                          decision: "failed", error: ex.Message), ct);
               throw;
           }
           await eventBus.PublishAsync(BuildLlmEvent(job.Id, "llm_hub_generate", ...,
                                                      decision: note.BodyOutput is null ? "initial" : "diff_aware"), ct);

           // Wrap in compose-v1 skeleton (frontmatter + User Notes + System Output sections).
           // Re-use compose-v1's User Notes preservation rule via UserNotesPreserver from handoff #6.
           note.BodyOutput = ComposeHubBody(note, entity, markdown, note.BodyOutput);

           await db.SaveChangesAsync(ct);
           await transitions.TransitionAsync(job, IngestJobStatus.Routing, null, false, false, ct);
       }

       private string ComposeHubBody(Note hubNote, Entity entity, string generatedMarkdown, string? previousBody)
       {
           // Reuse handoff #6's FrontmatterBuilder + UserNotesPreserver.
           var frontmatter = FrontmatterBuilder.Build(hubNote, topLevelAttachments: [], composeTemplate: "compose-v1");
           var userNotes = UserNotesPreserver.Extract(previousBody);
           var body = $"## User Notes\n\n{userNotes}\n\n## System Output\n\n{generatedMarkdown.TrimEnd()}\n";
           return $"---\n{frontmatter}---\n\n{body}";
       }
   }
   ```
   - `RoutingHandler` already short-circuits for `note.IsHub == true` (added in Pass A). After hub-generate runs and `routing` is no-opped, `extracting_entities` runs against the hub body and produces cross-entity mentions.
   - `EmbeddingHandler` embeds the hub body the same way as any other note. Writes `notes.embedding` + `entities.embedding` is left untouched here (entity embedding is updated only when the entity is created or aliased; not on every hub regen).

**2. `Features/Processing/IngestPhaseDispatcher.cs` — ADD kind branch.**
   ```csharp
   case IngestJobStatus.Composing:
       return job.Kind == IngestJobKind.HubRegen
           ? hubGenerationHandler.HandleAsync(job, ct)
           : composingHandler.HandleAsync(job, ct);
   ```
   Register `HubGenerationHandler` as scoped in `Program.cs`.

**3. `JobOrchestratorWorker.cs` — ADD `'composing'` to the in-flight phase list for hub_regen jobs.** The existing claim SQL transitions `'queued' → 'extracting_attachments'`. Hub-regen jobs are seeded at `status='composing'` directly, so the claim SQL must NOT auto-transition them. Update the CASE:
   ```sql
   status = CASE
       WHEN status = 'queued' AND kind <> 'hub_regen' THEN 'extracting_attachments'
       ELSE status
   END
   ```
   Hub-regen jobs never enter `'queued'` (per §17 above), so the `kind <> 'hub_regen'` guard is defense-in-depth.

**4. `Features/Processing/ProvenanceMaterializer.cs` — ADD `llm_calls` roll-up.** The materializer (existing from handoff #3, extended by handoff #6 with `compose_template`) gets a new field:
   ```csharp
   llm_calls = job.EventsLog.RootElement.EnumerateArray()
       .Where(e => e.GetProperty("stage").GetString()!.StartsWith("llm_"))
       .Select(e => new
       {
           stage         = e.GetProperty("stage").GetString(),
           prompt_id     = e.GetProperty("prompt_id").GetString(),
           retry_index   = e.GetProperty("retry_index").GetInt32(),
           confidence    = e.TryGetProperty("confidence", out var c) ? c.GetDouble() : (double?)null,
           llm_mode      = e.GetProperty("llm_mode").GetString(),
       })
       .ToList(),
   ```
   Eval scripts iterate this field directly without parsing the full events_log.

**5. Cross-handler regression** — extend `tests/ThanyMarcus.Cloud.Tests/Features/CompositeIngestSagaEndToEndTests.cs` from handoff #6:
   - 5-attachment composite (URL + image + voice + PDF + video) runs as before; assert `notes.project_id` is non-null and `notes.relative_path` starts with `Projects/...` (the fake LLM is configured to return a high-confidence project routing).
   - The same composite produces ≥ 3 mentions; one of the mentioned entities has its mention_count crossed by this single note (test seeds the entity with mention_count=2 before running the saga, so adding 1 crosses to 3); after the saga lands, assert: (a) the entity's `mention_count = 3`, (b) `entity.hub_note_id` is non-null, (c) a `notes` row exists with `is_hub=true` and `relative_path = _Entities/...`, (d) an `ingest_jobs` row with `kind='hub_regen'` for that hub note ran through to `succeeded`, (e) the hub note's `body_output` contains a `### Context` heading (matches the prompt's instructions).
   - SSE events: in addition to the 8 attachment_status_changed + note_phase_changed + note_succeeded events from handoff #6, assert one `hub_materialized` event fires when the hub spawn lands.

**6. Tests for Pass C:**
   - `Features/Processing/Phases/HubGenerationHandlerTests.cs` — fake LLM returns canned Markdown body → handler writes `notes.body_output` with `compose-v1` frontmatter wrapper + `## User Notes` + `## System Output` + the LLM's markdown; initial regen (`previous_body=null`) uses the from-scratch prompt; subsequent regen (`previous_body=<existing>`) uses the diff-aware prompt; missing hub_entity_id throws.
   - `Features/Processing/IngestPhaseDispatcherKindBranchTests.cs` — capture+composing → ComposingHandler; hub_regen+composing → HubGenerationHandler.
   - `Features/Entities/HubRegenCycleTests.cs` — entity A mentioned in entity B's hub-regen output → does NOT re-trigger A's hub (A already has hub); confirms the threshold check `hub_note_id IS NULL` prevents cycles.

→ At end of Pass C: a capture creates a note + spawns a hub regen for any entity newly crossing the threshold; the hub regen runs its own saga and lands a hub note. The plugin sees both notes via `/api/sync/pull`. Eval scripts can read `notes.provenance.llm_calls` to compute routing accuracy + dedup P/R + hub-coverage.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   ├── 0042-cloud-ingest-pipeline-architecture.md         # (read-only) phase machine, best-effort, hub_regen kind
│   ├── 0043-cloud-side-model-lineup.md                    # (read-only) MiniCPM-V text mode, same Ollama tag
│   ├── 0044-cloud-intelligence-layer.md                   # (read-only) THE CONTRACT
│   └── 0045-composite-note-schema.md                      # (read-only) entities/mentions/hub_note_id semantics
├── plans/
│   └── cloud-llm-intelligence-handoff.md                  # THIS FILE
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                         # CHANGED: swap LLM registrations (delete LlmTornado factory; add SafeLlmClient HttpClient + factory + HubGenerationHandler + LlmIntelligenceOptions)
│   ├── appsettings.json                                   # CHANGED: + LlmIntelligence:{Thresholds,Pgvector,Retry,HubMaterializeMin,...}
│   ├── ThanyMarcus.Cloud.Api.csproj                       # CHANGED: − LlmTornado*, − Microsoft.Extensions.AI (if not used elsewhere)
│   ├── Infrastructure/Llm/
│   │   ├── ILlmClient.cs                                  # REWRITE: CompleteAsync<T> + PromptId + LlmStructuredOutputException
│   │   ├── ILlmClientFactory.cs                           # CHANGED: Resolve returns ILlmClient + out fallbackToSafe
│   │   ├── LlmClientFactory.cs                            # REWRITE: safe-or-fallback
│   │   ├── SafeLlmClient.cs                               # NEW: Ollama /api/generate + JSON-mode + retry
│   │   ├── LlmIntelligenceOptions.cs                      # NEW: thresholds, pgvector, retry, hub params
│   │   ├── LlmEventBuilder.cs                             # NEW: builds IngestEvent with the §22 payload shape
│   │   ├── ChatClientLlm.cs                               # DELETED (LlmTornado-backed legacy)
│   │   ├── NoOpLlmClient.cs                               # DELETED
│   │   ├── EnrichmentSchema.cs                            # DELETED
│   │   ├── LlmOptions.cs                                  # DELETED
│   │   └── Prompts/                                       # NEW directory
│   │       ├── PromptTemplates.cs                         # 4 raw-string constants
│   │       ├── PromptBuilder.cs                           # 4 builders + ProjectListItem, EntityNeighbor, HubMentionContext records
│   │       ├── RouteDecisionDto.cs
│   │       ├── EntityExtractionDto.cs
│   │       ├── DedupDecisionDto.cs
│   │       └── HubGenerateDto.cs                          # tiny — declares HubGenerateInput + the T==string special-case marker
│   ├── Features/Entities/
│   │   ├── EntityVectorQueries.cs                         # NEW: raw-SQL pgvector top-K
│   │   └── HubMaterializer.cs                             # NEW: MaterializeAsync + EnqueueRegenAsync
│   └── Features/Processing/
│       ├── Phases/
│       │   ├── RoutingHandler.cs                          # REWRITE: route-v1 call + threshold; short-circuit for IsHub
│       │   ├── ExtractingEntitiesHandler.cs               # REWRITE: extract-v1 + per-mention dedup-v1 + hub spawn
│       │   ├── HubGenerationHandler.cs                    # NEW: hub-generate-v1 + compose-v1 wrapper
│       │   └── ComposingHandler.cs                        # UNCHANGED (handoff #6 composer)
│       ├── IngestPhaseDispatcher.cs                       # CHANGED: branch on job.Kind for Composing phase
│       ├── JobOrchestratorWorker.cs                       # CHANGED: claim SQL CASE guards 'hub_regen' from auto-transition
│       ├── ProvenanceMaterializer.cs                      # CHANGED: + llm_calls roll-up field
│       └── IIngestEventBus.cs                             # CHANGED: + event vocabulary for llm_route / llm_extract / llm_dedup / llm_hub_generate (typed payload records)
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Infrastructure/Llm/
    │   ├── SafeLlmClientTests.cs                          # NEW (HttpMessageHandler fake; retry behavior; T==string)
    │   └── Prompts/
    │       ├── PromptBuilderTests.cs                      # NEW (golden strings)
    │       ├── RouteDecisionDtoTests.cs                   # NEW (JSON round-trip)
    │       ├── EntityExtractionDtoTests.cs                # NEW
    │       └── DedupDecisionDtoTests.cs                   # NEW
    ├── Features/Entities/
    │   ├── EntityVectorQueriesTests.cs                    # NEW (Testcontainers; seeded entities; cosine ordering)
    │   └── HubMaterializerTests.cs                        # NEW
    ├── Features/Processing/Phases/
    │   ├── RoutingHandlerTests.cs                         # NEW
    │   ├── RoutingHandlerNoLlmFallbackTests.cs            # NEW
    │   ├── ExtractingEntitiesHandlerTests.cs              # NEW
    │   ├── ExtractingEntitiesCancellationTests.cs         # NEW
    │   ├── HubGenerationHandlerTests.cs                   # NEW
    │   └── IngestPhaseDispatcherKindBranchTests.cs        # NEW
    ├── Features/Entities/
    │   └── HubRegenCycleTests.cs                          # NEW
    └── Features/
        └── CompositeIngestSagaEndToEndTests.cs            # CHANGED: assert routing landed, mentions ≥ 3, hub spawned, hub_materialized SSE event
```

## Prompt reference (locked to ADR-0044 §3)

The four templates are transcribed verbatim from ADR-0044 §3.1 through §3.4 as C# raw-string constants in `PromptTemplates.cs`. Below is a one-line summary of each plus the DTO shape; the full template body lives only in the ADR + the constant.

| PromptId | Input context | Output DTO | Threshold (config key) | Provenance stage |
|---|---|---|---|---|
| `route-v1` | `{projects: [{id, name, description}], body_excerpt}` | `RouteDecisionDto(ProjectEntityId?, Confidence, Rationale)` | `RouteAcceptMin=0.5` | `llm_route` |
| `extract-v1` | `{body}` | `EntityExtractionDto(Mentions: [MentionCandidateDto])` | `MentionMin=0.6` (per-mention) | `llm_extract` |
| `dedup-v1` | `{candidate, surrounding_text, similar_entities: [EntityNeighbor]}` | `DedupDecisionDto(Decision: "alias_of"\|"new_entity"\|"ambiguous", MatchedEntityId?, Candidates, Confidence, Rationale)` | `DedupAliasMin=0.8`, `DedupNewMin=0.6` | `llm_dedup` |
| `hub-generate-v1` | `{entity, mentions: [HubMentionContext], previous_body?}` | `string` (Markdown body) | — (no JSON; raw text) | `llm_hub_generate` |

## Provenance event payload (locked to ADR-0042 §10c + ADR-0044 §5)

Every LLM call writes one event into `ingest_jobs.events_log`:

```json
{
  "at":            "2026-05-19T10:00:04.120Z",
  "by":            "ThanyMarcus.Cloud.Api@host/9a8b7c6d",
  "stage":         "llm_route",
  "prompt_id":     "route-v1",
  "model":         "minicpm-v",
  "model_version": "8b-2.6-q4_K_M",
  "llm_mode":      "safe",
  "llm_mode_fallback": false,
  "duration_ms":   4120,
  "retry_index":   0,
  "decision":      "project:9b3c4f...",
  "confidence":    0.87,
  "rationale":     "the note mentions Acme Corp twice and...",
  "error":         null
}
```

`retry_index` records LLM-client retries (separate from phase-handler retries). `llm_mode_fallback` is set when unsafe mode was requested but the safe client was used. Hub-generate events have `decision: "initial" | "diff_aware"` and `confidence: null`. Failure events have `decision: "failed"` and a non-null `error`.

The `ProvenanceMaterializer` rolls these up into a `notes.provenance.llm_calls` array at terminal time, plus a top-level `extraction_summary.mentions_extracted`, `entities_created`, `entities_aliased`, `mentions_dropped` count.

## Acceptance criteria

1. ✅ `dotnet build` clean under warnings-as-errors. `dotnet test --filter "Category!=Slow"` green in CI default lane.
2. ✅ `Program.cs`: `ILlmClient` resolves to `SafeLlmClient`; `ILlmClientFactory.Resolve(settings)` returns safe for `LlmMode == safe`, returns safe + `fallbackToSafe=true` for unsafe modes. No `LlmTornado`-related code remains (`grep -rn "LlmTornado" src tests` → empty).
3. ✅ The four prompt constants exist in `PromptTemplates.cs` with bodies matching ADR-0044 §3.1–§3.4 verbatim (modulo whitespace from raw-string indentation). `grep -c "PromptId(" src/ThanyMarcus.Cloud.Api/Features` returns at least four call sites (route, extract, dedup, hub-generate).
4. ✅ `SafeLlmClient.CompleteAsync<RouteDecisionDto>(...)` against a `HttpMessageHandler` fake:
   - Returns parsed DTO on first valid JSON response.
   - Retries up to 3 attempts on `JsonException`; the appended suffix appears in attempts 2+; final attempt failure throws `LlmStructuredOutputException(Attempts=3)`.
   - `T == string` skips JSON parsing and trims the response.
5. ✅ `RoutingHandler` against a fake LLM emitting `confidence=0.9` + an existing project_id → writes `notes.project_id` + `relative_path = Projects/<canonical>/<id>.md`; `confidence=0.4` → leaves `project_id=null` + `relative_path = Inbox/{id}.md`; `note.IsHub=true` → short-circuits to ExtractingEntities transition without an LLM call.
6. ✅ `ExtractingEntitiesHandler` against fake LLM:
   - `extract-v1` returns 3 candidates, 1 below `MentionMin` → 1 dropped, 2 dedup'd.
   - Per candidate, pgvector top-K returns 5 entities, dedup-v1 emits `new_entity` confidence 0.9 → new `Entity` INSERTed + 1 `Mention` row + `entity.MentionCount=1`.
   - Adding 2 more captures pointed at the same entity → mention_count crosses 3 → hub note + ingest_jobs(kind='hub_regen') inserted.
   - `ambiguous` decision → mention dropped + provenance records the candidates list.
   - `alias_of` with confidence 0.85, existing entity's aliases don't contain anchor_text → alias added; same call with confidence 0.7 → mention dropped (below DedupAliasMin).
7. ✅ `HubGenerationHandler` against fake LLM emitting canned Markdown:
   - Initial regen (`previous_body=null`) → `notes.body_output` contains frontmatter + `## User Notes` + `## System Output` + the LLM's markdown.
   - Subsequent regen (`previous_body=<existing>`) preserves User Notes block; System Output reflects new generated content.
   - `note.HubEntityId=null` → throws `InvalidOperationException`.
8. ✅ `IngestPhaseDispatcher` branches on `job.Kind` for `Composing`: `Capture/Reprocess` → `ComposingHandler`; `HubRegen` → `HubGenerationHandler`. Unit test asserts dispatch decision in both cases.
9. ✅ `JobOrchestratorWorker` claim does NOT auto-transition `hub_regen` jobs from `queued` to `extracting_attachments`. Hub-regen jobs seeded at `status='composing'` are claimed directly into composing.
10. ✅ pgvector top-K query (`EntityVectorQueries.NearestAsync`) returns 5 rows of the matching kind, deleted_at-null only, in cosine-distance order. With zero-vector embeddings (stub) the order is deterministic; test asserts kind filter + count + ordering.
11. ✅ Provenance integration:
    - Every LLM call emits an `llm_<stage>` event into `ingest_jobs.events_log`.
    - `ProvenanceMaterializer` rolls these into `notes.provenance.llm_calls` at terminal time.
    - A retried-then-succeeded call appears with `retry_index=1` in events_log; failed-after-3-retries appears with `decision="failed"` + non-null `error`.
12. ✅ `CompositeIngestSagaEndToEndTests.cs` (handoff #6 regression anchor, updated):
    - 5-attachment composite produces a `notes.body_output` matching the compose-v1 shape.
    - Fake LLM routing returns project_id pointing at a pre-seeded project entity → `notes.relative_path` starts with `Projects/`.
    - Fake LLM extract returns ≥ 3 mentions; ≥ 1 is dedup'd as `new_entity` → ≥ 1 new `Entity` row + ≥ 3 `Mention` rows.
    - With a pre-seeded entity at mention_count=2, the capture's 1 mention crosses the threshold → a hub note + hub-regen ingest_jobs land, both run to `succeeded`.
    - SSE events include one `hub_materialized` event.
    - Provenance includes `llm_calls` with stages `llm_route`, `llm_extract`, `llm_dedup` (multiple), and (for the hub) `llm_hub_generate`.
13. ✅ `LlmIntelligenceOptions` defaults match ADR-0044 §4: `RouteAcceptMin=0.5, MentionMin=0.6, DedupAliasMin=0.8, DedupNewMin=0.6, DedupTopK=5, MaxAttempts=3, HubMaterializeMin=3`.
14. ✅ `OllamaTag` is sourced from a single place (`IngestSaga:Models:Vlm:OllamaTag`); `LlmIntelligenceOptions.OllamaTag` post-configures from that key. Bumping the tag in one place flows to both VLM workers and `SafeLlmClient`.
15. ✅ Slow-lane test (`[Trait("Category","Slow")]`) hits a Testcontainers-hosted `ollama/ollama:0.5.1` with the pinned tag pre-pulled in a collection fixture (`OllamaLlmFixture`), runs one `route-v1` call against a synthetic prompt with a stubbed project list, asserts the response parses to `RouteDecisionDto`. Marked Slow; nightly lane only.
16. ✅ Manual smoke against a freshly-provisioned cloud (handoffs #1–#6 applied):
    ```bash
    # Capture a composite (existing flow)
    curl -sS -X POST .../api/ingest/init -H "Authorization: Bearer $TOKEN" -d '{...}'
    # uploads + finalize ...
    # Watch SSE for note_succeeded then hub_materialized
    curl -sS ".../api/sync/pull?since=0&include=provenance" -H "Authorization: Bearer $TOKEN" \
      | jq '.items[] | {id, relative_path, is_hub, project_id}'
    # → first item is the capture, relative_path starts with Projects/ (if routing accepted)
    # → second item (if threshold crossed) is the hub, is_hub=true, relative_path=_Entities/...
    curl -sS ".../api/sync/pull?since=0&include=provenance" -H "Authorization: Bearer $TOKEN" \
      | jq '.items[0].provenance.llm_calls'
    # → array with route, extract, dedup×N entries
    ```
17. ✅ Allowed `// FORK:` markers: `RoutingHandler.cs` at the `IsHub` short-circuit (forks to a future "user-routed hub regen" scenario), `SafeLlmClient.cs` at the `T==string` special case, `LlmClientFactory.cs` at the `IUnsafeLlmClient` fallback (forks to CLOUD-026). One TODO marker (Pass A's `ExtractingEntitiesHandler` no-op) is removed by end of Pass B; no TODOs survive to ship.
18. ✅ `grep -rn '"route-v1"\|"extract-v1"\|"dedup-v1"\|"hub-generate-v1"'` finds each string in exactly one place: at the `new PromptId(name, version)` call site. The `ToString()` override on `PromptId` produces the dash-joined form for provenance.

## Out of scope (named explicitly)

1. ❌ **`UnsafeLlmClient` against Anthropic/OpenAI.** The seam (`IUnsafeLlmClient` marker interface + factory fallback) exists; the implementation lives in CLOUD-026 / M7. Until then, unsafe mode silently falls back to safe with `llm_mode_fallback=true` in provenance.
2. ❌ **Granite ONNX embedding integration.** `IEmbeddingClient` continues to return `new float[256]`. The pgvector top-K SQL runs against zero vectors. Real embeddings land in a follow-up ticket (`CLOUD-EMBEDDING`).
3. ❌ **Wikilink anchor splicing into rendered Markdown.** The `extract-v1` prompt produces `start_offset` / `end_offset` per candidate; these are stored in `mentions` but NOT spliced into the rendered body. Splicing requires a `compose-v2` bump (per handoff #6 §"What handoffs #7+ inherit"). Document only.
4. ❌ **Tag extraction.** The legacy `EnrichCompositeAsync` populated `notes.tags`. The new prompt set does not emit tags. `notes.tags` stays null. A future `tag-v1` prompt is a clean drop-in.
5. ❌ **User-created entity auto-hub.** ADR-0045 §F8 says `source='user'` entities auto-hub. No user-create entity endpoint exists yet; lands when the plugin's "create entity manually" endpoint ships. All hubs in this ticket come from the LLM mention-count threshold.
6. ❌ **Hub-regen for entities with no recent mentions.** Manual "rebuild this hub" is a future operator endpoint; not in scope.
7. ❌ **Routing-prompt projects-pagination.** The prompt receives at most `RoutingProjectsMax=50` projects. With > 50 projects, the LLM never sees the older ones. Thesis-scale OK; document as a known constraint.
8. ❌ **Real-time prompt template editing.** Prompts are code constants; changing them requires a deploy. Per ADR-0044 §"Operational" + §"Negative".
9. ❌ **Streaming LLM output.** Per ADR-0044 §8. `stream: false` always.
10. ❌ **Grammar-constrained decoding (GBNF).** Per ADR-0044 §2 + §"Alternatives considered" #1. Revisit if eval shows retry rate > 5%.
11. ❌ **Per-prompt A/B in production.** The prompt-versioning seam exists (add `Route_V2` constant, switch by config key); the experiment plumbing does not.
12. ❌ **Cancellation of in-flight LLM HTTP calls.** Ollama's HTTP cancellation semantics are unreliable mid-stream. Cancel happens at phase boundaries.
13. ❌ **Per-cloud RAM budget tuning.** The Ollama sidecar's `mem_limit=3g` from handoff #2 covers VLM + text-mode JSON workloads. If text-mode JSON proves heavier than VLM (unlikely; same model, smaller input), revisit.
14. ❌ **Multi-step / chain-of-thought / ReAct prompts.** Per ADR-0044 §"Alternatives considered" #5. Future-work hook for harder dedup cases.
15. ❌ **Per-user fine-tuned routing model.** Per ADR-0044 §"Alternatives considered" #4. Out of thesis scope.
16. ❌ **Hub note plugin-side rendering changes.** The plugin sees a hub note as a normal note; whatever rendering the plugin already does for `is_hub=true` (if any) stays. The hub's `_Entities/<kind>/<canonical>.md` path is the user-visible signal.

## Risks and gotchas

- **MiniCPM-V 4.6 text-mode JSON output reliability.** The ADR was authored assuming ~95% valid-JSON rate on first attempt (ADR-0044 §2). The MiniCPM-V 4.6 Q4_K_M Ollama tag (or its 2.6 fallback) may behave differently. **Mitigation:** the slow-lane test against the real model is the canary. If the test surfaces a > 5% retry rate, file a follow-up to evaluate grammar-constrained decoding before eval starts.
- **Ollama JSON-mode constrains output structure but not semantics.** `format: "json"` guarantees the model emits *some* JSON object, not necessarily one matching the prompt's schema. The post-parse `JsonSerializer.Deserialize<T>` against the typed DTO catches the gap; the retry-with-suffix loop handles it. **Verify** that System.Text.Json's default deserialization is strict enough — unknown properties allowed by default but missing required (non-nullable) properties throw on the DTO constructor. Test against deliberately-mangled responses.
- **`Pgvector.Vector` parameter binding in raw SQL.** The `EntityVectorQueries.NearestAsync` raw SQL uses `@emb::vector` cast on the bound parameter. Npgsql binds `Pgvector.Vector` to the `vector` type natively, but the `::vector` cast in the SQL is defensive against any binding ambiguity. Verify in `EntityVectorQueriesTests` that the cast doesn't double-encode the parameter (no `"[0.1, 0.2]"::vector::vector` weirdness).
- **HNSW index probe count.** pgvector's HNSW search uses an `ef_search` parameter (default 40) that controls accuracy vs latency. At thesis scale (a few thousand entities), defaults are fine. If eval shows top-K misses high-quality candidates, set `SET LOCAL hnsw.ef_search = 100` at the connection scope for the query — single-line change.
- **Hub-regen + extracting_entities + threshold cycles.** A capture writes mentions → entity A crosses threshold → hub note for A spawns → hub note's extracting_entities mentions entity B → B's mention_count increments → if B also crosses, B's hub spawns. Two hubs from one capture is fine. **The cycle break is `hub_note_id IS NULL` in the threshold check** — once a hub exists, threshold-crossing on subsequent mentions only triggers diff-aware *regen* (via `EnqueueRegenAsync`), not a second hub creation. Test `HubRegenCycleTests.cs` is the regression guard.
- **`ix_ingest_jobs_active_per_note` collision on hub-regen diff-aware enqueue.** If two captures land in rapid succession, both can trigger `EnqueueRegenAsync` for the same entity's hub. The second one's INSERT throws a unique-constraint violation; the handler swallows. Acceptable — the first in-flight regen will pick up the second capture's mentions in its `extracting_entities` phase. **Test** that the swallowed exception is logged at info, not warn (signal would noise dashboards).
- **`UserNotesPreserver` on hub notes.** The compose-v1 skeleton includes `## User Notes`. A user can edit a hub note's User Notes section in their vault. On subsequent regen, those edits must survive. `HubGenerationHandler.ComposeHubBody` reuses `UserNotesPreserver.Extract(note.BodyOutput)` (the handoff #6 utility); test that user edits round-trip across at least one diff-aware regen.
- **Provenance JSON size.** Each LLM call adds a ~300-byte event. A note with 5 mentions runs 1 route + 1 extract + 5 dedup = 7 events ≈ 2 KB. A hub-regen adds 1 hub-generate event. At thesis scale, JSONB column size is fine. **Worst case:** a single failed-LLM retry exhausts adds 3 events per call → 21 events for 7 calls → ~6 KB. Still fine; document as a known constraint.
- **`route-v1` prompt's projects list truncation at 50.** When a user has > 50 projects, the prompt sees only the 50 most-recently-updated. Older projects can never be routed to. Acceptable at thesis scale; document.
- **Empty `body_output` at routing time.** Routing runs after composing; `note.BodyOutput` is non-null per handoff #6 contract. If a future ticket reorders phases, this assumption breaks. Defensive: `var bodyExcerpt = Truncate(note.BodyOutput ?? note.BodyInput, 1500);` — fall back to `BodyInput` if `BodyOutput` happens to be null (shouldn't, but cheap to guard).
- **`note.LlmMode` write race.** RoutingHandler writes `note.LlmMode = llm.Mode`. If both RoutingHandler and HubGenerationHandler run for the same note in close succession (shouldn't — hub notes don't route, normal notes don't hub-regen), the last writer wins. Hub notes are flagged with `IsHub=true` so RoutingHandler short-circuits; this isn't actually a race in practice. **Documented** to defuse a future reviewer.
- **`hub-generate-v1` empty `mentions` list.** A hub-regen fires with `mention_count >= 3` but a concurrent delete tombstones the source notes → `recent` returns 0 rows. The prompt sees `mentions: []`. The model can still produce a body ("no information available" or similar). **Acceptable** — the hub note will say so honestly. Add a defensive log if `mentions.Count == 0` and continue.
- **`entities.aliases` array uniqueness.** Adding an anchor_text to aliases uses `Contains` with `OrdinalIgnoreCase`. If the alias differs only by trailing whitespace or punctuation, it's added twice. Trim + normalize-whitespace before the contains check.
- **Pgvector raw-SQL binding + EF change tracking.** `EntityVectorQueries.NearestAsync` opens a raw connection from `db.Database.GetDbConnection()`. If EF's connection is already in a transaction, the raw cmd participates correctly (same connection); but if EF hasn't opened the connection yet, the raw cmd opens it implicitly. Verify by running the cosine query inside a SaveChanges scope; expect no "this command requires an open connection" exceptions.
- **`HubMaterializer.MaterializeAsync` partial commits.** The materializer adds rows to `db` but doesn't `SaveChangesAsync`. The handler's outer `SaveChangesAsync` commits everything atomically. If a different developer factors `MaterializeAsync` out of the handler later and forgets the surrounding SaveChanges, hub notes never actually land. **Document** the dependency in `HubMaterializer.cs` with a one-line non-obvious-why comment.
- **Test fixture leak — Ollama Testcontainer per slow test.** Each slow test starting its own Ollama container is wasteful. Use `ICollectionFixture<OllamaLlmFixture>` to share one container across all slow LLM tests. Container startup + model pre-pull dominates wall-clock time; sharing saves ~30 s × N tests in the nightly lane.
- **DTO property casing.** ADR-0044's prompts emit `project_entity_id`, `start_offset`, etc. (snake_case). The DTO records are PascalCase C#. Use `[JsonPropertyName("project_entity_id")]` attributes; verify each DTO field's JSON name in a round-trip test.

## Open contract decisions (carry forward)

1. **MiniCPM-V 4.6 tag finality.** Handoff #2's `OLLAMA_PULL_TAG` is currently a placeholder pointing at 2.6. Once 4.6 lands on ollama.com/library, bump in lockstep across handoff #2's terraform var + `appsettings.json:IngestSaga:Models:Vlm:OllamaTag` + the slow-lane test's `OllamaLlmFixture`. No code change in this handoff; tracked via the open-contract from handoff #2.
2. **`compose-v2` bump for wikilink splicing.** Documented in handoff #6 §"What handoffs #7+ inherit". A natural extension after this ticket: introduce `WikilinkSplicer` as a post-render pass that consumes `mentions` rows and inserts `[[<canonical>]]` at the recorded offsets, then bump `compose_template = "compose-v2"`. Splits naturally as its own ticket because the body shape change is independent of the LLM client.
3. **Tag prompt (`tag-v1`).** If eval shows manual tagging is too high-friction, add a fifth prompt. Schema: `{tags: [string], confidence: 0..1}`. Slot at the end of `ExtractingEntitiesHandler` after the entities are written. ~0.5d.
4. **Dedup batching.** Per ADR-0044 §"Alternatives considered" #9. If eval shows median 5+ mentions per note and dedup latency becomes a bottleneck, revisit batched dedup. The interface change is small (a new `BatchedDedup_V1` prompt + DTO).
5. **`HubMentionWindow=20` value.** If hubs get stale because the prompt only sees 20 recent mentions, bump. Currently a guess; eval will inform.
6. **`SurroundingTextChars=200` value.** Dedup context window. Larger → better disambiguation, longer prompts. 200 is the default per ADR-0044 §3.3 implication.
7. **`RoutingProjectsMax=50`.** Bump if users routinely have > 50 active projects. Pagination of projects in the prompt is a more invasive change; not in scope.
8. **ADRs to author** (0.25-day batch after this ticket ships, not in scope here):
   - **ADR-0046: Prompt registry + retry-on-malformed contract.** Concretizes ADR-0044's §2 + §5 + §6 into a single thesis-defense reference.
   - **ADR-0047: Mention threshold + hub auto-spawn.** Captures the `HubMaterializeMin=3` + cycle-break + diff-aware regen rationale.

## What handoffs #8+ inherit

After this ticket lands:

- **`CLOUD-EMBEDDING` (Granite ONNX in-process)** plugs into `IEmbeddingClient`. The pgvector top-K SQL is already wired; switching from stub to real changes only the embedding values. No handler-side change. The HNSW index from handoff #1 starts being informative.
- **`CLOUD-026` (`UnsafeLlmClient` against Anthropic/OpenAI)** plugs into `IUnsafeLlmClient`. The factory's `Resolve` already returns it when `cloud_settings.llm_mode != safe`. The provenance fields (`llm_mode`, `model`, `model_version`) are already populated by the unsafe client's own properties. **The same four prompts work against the external API** because the prompt body is provider-agnostic JSON-mode instructions; the unsafe client just constructs Anthropic/OpenAI message envelopes around the same prompt string.
- **`compose-v2` (wikilink splicing)** consumes `mentions.start_offset/end_offset` to splice `[[<canonical>]]` into the rendered body. No saga or LLM change; just a new compose-template renderer pass.
- **`CLOUD-022` / `tag-v1`** is a clean fifth prompt — slot at end of `ExtractingEntitiesHandler` after the existing dedup loop. No interface change.
- **Plugin-side hub note rendering** is a future plugin ticket; the cloud stores hubs at `_Entities/<kind>/<canonical>.md` and the plugin writes them as ordinary Markdown files. No coupling.
- **EVAL-002 / 003 / 005 scripts** iterate `notes.provenance.llm_calls` to compute routing accuracy + entity dedup P/R + wikilink P@K (the offsets are stored even though splicing is deferred). Eval can already start measuring against this ticket's output.

The cumulative arc through M5/M6:

```
CLOUD-SCHEMA-V2 (handoff #1)
  ─► CLOUD-SIDECARS (handoff #2)
  ─► CLOUD-INGEST-SAGA-FOUNDATION (handoff #3)
  ─► CLOUD-PROCESSORS-LIGHT (handoff #4)
  ─► CLOUD-PROCESSORS-HEAVY (handoff #5)
  ─► CLOUD-COMPOSE-PHASE (handoff #6 — compose-v1 templates)
  ─► CLOUD-LLM-INTELLIGENCE (THIS TICKET — routing + entities + dedup + hub-regen)
  ─► CLOUD-EMBEDDING (Granite ONNX real)
  ─► CLOUD-026 / UnsafeLlmClient (Anthropic/OpenAI external API)
  ─► compose-v2 (wikilink splicing)
  ─► [M5 demo: composite capture → real extraction → composed Markdown → LLM-enriched + emergent graph]
  ─► [M6 demo: end-to-end thesis-defensible CPU-only pipeline with hubs + cross-cloud comparison]
```

This ticket is **the intelligence beachhead.** After it, the four LLM calls run real against MiniCPM-V; eval can begin measuring routing accuracy, dedup P/R, and hub coverage. Downstream tickets (real embeddings, unsafe-mode dispatch, wikilink splicing) are all clean drop-ins against the seams this ticket lands.

## Why a separate handoff (and not folded into the composer ticket)

LLM intelligence is conceptually distinct from body composition. Folding them would (a) blur the prompt-registry contract (4 prompts, versioned, eval-attributable) with the render contract (per-kind blocks, compose-v1 frontmatter), (b) couple a YAML serializer bug with a model-prompt regression in the same PR, (c) leave the regression anchor unable to assert "compose works without LLM" (which is what handoff #6 needs to prove), (d) double the size of an already-large compose ticket. Shipping compose first means the body skeleton is exercised against no-op LLM and stub embeddings; shipping intelligence second means the prompt set + dedup + hub-spawn is exercised against the stable body. This ticket's 3.5–4d pays off the intelligence layer once and amortizes across eval (M9), the embedding swap, the external-API swap, and the wikilink-splice bump.

## Cross-references

- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — phase machine, best-effort cascade, hub_regen kind, provenance schema.
- **`docs/decisions/0043-cloud-side-model-lineup.md`** — MiniCPM-V text mode, single Ollama sidecar, OLLAMA_KEEP_ALIVE=30m.
- **`docs/decisions/0044-cloud-intelligence-layer.md`** — THE CONTRACT. ILlmClient interface, four prompts, thresholds, retry, safe/unsafe dispatch, deferred future-work.
- **`docs/decisions/0045-composite-note-schema.md`** — entities/mentions/hub_note_id semantics; threshold + dedup decision table (§F6, §F8).
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — the framework this ticket modifies. Phase machine, lease, transition_version, IIngestEventBus all preserved.
- **`plans/cloud-sidecars-handoff.md`** — Ollama at `http://ollama:11434`, model tag, MaxConcurrency=1.
- **`plans/cloud-compose-phase-handoff.md`** — compose-v1 frontmatter + skeleton; `UserNotesPreserver` reused by `HubGenerationHandler`.
- **`plans/cloud-pivot-plan-2026-05-13.md`** §B4 — diff-aware hub regen pattern.
- **`plans/tickets-2026-05-13.md`** — CLOUD-017, CLOUD-019, CLOUD-020, CLOUD-021, CLOUD-026.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note; hubs as cloud-generated notes.
- **Memory `portal_architecture.md`** — Postgres job queue + SSE only (applies symmetrically to hub-regen).
- **Future ADRs** (not in this ticket's scope):
  - **ADR-0046: Prompt registry + retry-on-malformed contract.**
  - **ADR-0047: Mention threshold + hub auto-spawn + diff-aware regen.**

## Definition of done

```
$ dotnet build
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "Category!=Slow"
Passed!  - Failed: 0, Passed: N, Skipped: 0

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName~CompositeIngestSagaEndToEndTests"
Passed!  - Failed: 0, Passed: 1, Skipped: 0   # the regression anchor with routing + mentions + hub-materialized assertions

$ grep -rn "LlmTornado\|EnrichCompositeAsync\|CompositeEnrichmentResult\|WikilinkAnchor" src tests
(no results)                                  # legacy LLM shape fully removed

$ grep -rn "PromptTemplates\." src/ThanyMarcus.Cloud.Api
src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/Prompts/PromptBuilder.cs:    ... PromptTemplates.Route_V1 ...
src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/Prompts/PromptBuilder.cs:    ... PromptTemplates.Extract_V1 ...
src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/Prompts/PromptBuilder.cs:    ... PromptTemplates.Dedup_V1 ...
src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/Prompts/PromptBuilder.cs:    ... PromptTemplates.HubGenerate_V1 ...
                                              # four constants, four builders, no other references

$ grep -rn '"route-v1"\|"extract-v1"\|"dedup-v1"\|"hub-generate-v1"' src
src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/RoutingHandler.cs:           ... new PromptId("route", "v1") ...
src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/ExtractingEntitiesHandler.cs: ... new PromptId("extract", "v1") ...
src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/ExtractingEntitiesHandler.cs: ... new PromptId("dedup", "v1") ...
src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/HubGenerationHandler.cs:      ... new PromptId("hub-generate", "v1") ...
                                              # one call site per prompt

$ grep -rn "IUnsafeLlmClient" src
src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/LlmClientFactory.cs:public interface IUnsafeLlmClient : ILlmClient { }
src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/LlmClientFactory.cs:    var unsafeClient = sp.GetService<IUnsafeLlmClient>();
                                              # seam exists; no implementation registered
```

A fresh agent picking up `CLOUD-EMBEDDING` (Granite ONNX) from this state knows:
- `IEmbeddingClient.EmbedAsync` is already called by `ExtractingEntitiesHandler` per candidate; swap stub → real → `EntityVectorQueries.NearestAsync` starts ranking informatively. No handler change.
- The HNSW index from handoff #1 is already covering `entities.embedding` and `notes.embedding`.
- `IEmbeddingClient` returns `float[256]` matching the Matryoshka-cut dimension from ADR-0043 §1.

A fresh agent picking up `CLOUD-026` (`UnsafeLlmClient`) knows:
- `IUnsafeLlmClient` marker interface + `LlmClientFactory.Resolve` fallback wiring are already in place.
- Register `services.AddSingleton<IUnsafeLlmClient, AnthropicLlmClient>()` (or the OpenAI variant) → factory routes unsafe-mode notes to it.
- The four prompts already serialize to provider-agnostic JSON-mode instructions; the unsafe client just constructs Anthropic/OpenAI message envelopes around the same prompt string.
- Provenance carries `llm_mode` + `model` + `model_version` per call — eval can cleanly compare safe-vs-unsafe quality.

A fresh agent picking up `compose-v2` (wikilink splicing) knows:
- `mentions.start_offset` + `mentions.end_offset` are populated for every accepted mention.
- The splicer runs as a post-render pass after the existing compose-v1 renderers — read mentions for the note, build a sorted-by-offset insertion plan, splice `[[<canonical>]]` at each anchor, bump `compose_template` constant to `compose-v2`. The composer is the only file touched; the LLM layer is unchanged.

The cumulative arc through M6: this ticket lights up the intelligence layer; the three follow-ups make it sharper (real embeddings), wider (unsafe-mode comparison), and more visible (wikilinks in the rendered vault). After all four, the M6 thesis demo — "drop a composite into Obsidian, watch the cloud route + extract entities + spawn hubs + emerge a knowledge graph" — runs end-to-end against real models with eval-grade provenance.
