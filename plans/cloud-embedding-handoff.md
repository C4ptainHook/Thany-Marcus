# CLOUD-EMBEDDING — In-process Granite Embedding R2 ONNX + embedding phase + re-embed on body change — Handoff Brief

Date: 2026-05-19
Status: Draft. Handoff #8 of the ADR-0042 / ADR-0043 / ADR-0045 implementation series. **Swaps `StubEmbeddingClient` for an in-process ONNX Runtime client running IBM Granite Embedding R2** and tightens the `embedding` phase so reprocess re-embeds when (and only when) `notes.body_output` actually changed. The phase machine, lease/heartbeat/transition_version, SSE bus, reprocess/cancel endpoints, and the schema all stay byte-for-byte; this ticket is a DI swap + one new options class + one new column + a content-hash gate inside the existing `EmbeddingHandler`. Independent of handoff #7 (`cloud-llm-intelligence-handoff.md`) — order doesn't matter, but handoff #7's pgvector top-K dedup retrieval (`ExtractingEntitiesHandler` → `EntityVectorQueries.NearestAsync`) only becomes informative once this ticket lands. After this ticket: a composite finalize produces a non-zero, multilingually-meaningful 256-dim cosine-normalized embedding for `notes.embedding`, the same client populates `entities.embedding` at entity-creation time inside `ExtractingEntitiesHandler`, and a reprocess that re-renders the same body to the same bytes skips the embed call entirely; a reprocess whose body changes (e.g., `compose-v2` lands LLM wikilinks, or an attachment's extracted text changed) re-embeds and bumps the row's `transition_version`.

**Goal:** add `GraniteEmbeddingClient : IEmbeddingClient` under `Infrastructure/Sidecars/Embedding/`, wire it via `services.AddSingleton<IEmbeddingClient, GraniteEmbeddingClient>()` (replacing the existing stub registration in `Program.cs:216`), load the Granite Embedding R2 ONNX weights + HuggingFace `tokenizer.json` at startup as a single shared singleton (~200 MB resident, eager warm-up via `IHostedService` so the first capture doesn't pay model load time), implement `EmbedAsync(string text, CancellationToken)` as `tokenize → truncate to 512 tokens → ONNX session.Run → mean-pool last-hidden-state over the attention mask → L2-normalize → Matryoshka-truncate to 256 dim → L2-normalize again` (the second normalize is the standard post-Matryoshka step), introduce a `notes.body_hash` column + EF migration so the handler can detect "body unchanged since last embed and skip", and document the ADR-0043 model-name drift (the "Granite Embedding 278m R2" the ADR names does not exist on HF; this ticket pins `ibm-granite/granite-embedding-311m-multilingual-r2`, the smallest R2 that supports Matryoshka at the 256-dim cut the schema locks). After this ticket: `notes.embedding` and `entities.embedding` carry actual semantic vectors, pgvector cosine top-K returns useful neighbors, and `ExtractingEntitiesHandler`'s dedup retrieval (already wired in handoff #7) returns informative candidates instead of "first 5 rows by deleted_at IS NULL." The regression anchor in `CompositeIngestSagaEndToEndTests.cs` continues to pass with an updated assertion: `notes.embedding` is non-null and non-zero (replacing the "is the 256-zero vector" check the saga foundation handoff #3 left in place).

Estimated **1.5–2 person-days** with AI-agent assistance, split into two passes. Pass A (~1d) lands `GraniteEmbeddingClient` + tokenizer + ONNX session + warm-up + DI swap + the regression-anchor update. Pass B (~0.5–1d) adds `notes.body_hash`, the change-detection gate inside `EmbeddingHandler`, and the entity-side re-embed policy (when does `entities.embedding` get re-embedded when an entity's canonical name + aliases change?). Estimate is rough — it compresses to ~1d if the official `ibm-granite/granite-embedding-311m-multilingual-r2` ONNX weights publish at `onnx/model.onnx` with the conventional sentence-transformers `tokenizer.json` layout (likely; the model card was verified 2026-05-19 to ship ONNX + OpenVINO weights first-party); stretches to ~2.5d if the ONNX Runtime managed package (`Microsoft.ML.OnnxRuntime`) needs a native-library trick on macOS dev machines (one of the previous ML.NET tickets hit this — runtime identifier pinning on darwin-arm64).

This handoff **does not** ship: chunked embeddings for long bodies (ADR-0043 §"Future-work hooks" #5 keeps single-vector-per-note; chunking is post-thesis); a `compose-v2` body bump (handoff #7's LLM phases run on `compose-v1` and don't mutate body_output — re-embed-on-body-change is forward-compatible with `compose-v2`, the gate just trips when `compose-v2` lands); an INT8-quantized Granite model (the safetensors / FP32 ONNX is ~195 MB, the dynamic INT8 ONNX is ~98 MB but not first-party on the 311m at handoff time — pick FP32 to avoid a quantization-quality detour, revisit in eval); query/passage prompt prefixes (R2 explicitly does **not** require a task prefix per the HF blog — single `EmbedAsync(text)` shape, no `EmbedQueryAsync` / `EmbedPassageAsync` overloads); a GPU code path (the cloud is CPU-only per ADR-0035 burst-worker spec; ONNX Runtime's CPU EP is the only execution provider registered).

## Where decisions live (read before doing anything)

- **`docs/decisions/0043-cloud-side-model-lineup.md`** — the contract. The row "(text embedding for retrieval) | IBM Granite Embedding 278m R2 (Matryoshka cut @ 256 dim) | in-process ONNX Runtime" is the locked decision *intent*. The model identifier is **wrong as written** (see "ADR-0043 drift" below), but the *capability* it locks (Apache-2.0 + first-party ONNX + ru/uk/pl multilingual + Matryoshka 256-dim + ~200 MB resident + in-process) is what this ticket implements. §"Embedding (text retrieval)" lines 216–222 lock the algorithm: cosine retrieval, ONNX Runtime, Matryoshka cut to 256, 512-token cap with truncation (chunking deferred per ADR-0043 §"Future-work hooks" #5). §"Memory budget" line 66 locks the ~200 MB resident footprint shared by routing + retrieval. §"Per-sidecar concurrency caps" line 52 names `Embedding=2` — but embedding runs in-process via ONNX Runtime, not via a sidecar HTTP client; the "concurrency cap" maps to a `SemaphoreSlim(2,2)` inside `GraniteEmbeddingClient` to bound ONNX session reentrancy. ORT sessions are thread-safe per HuggingFace + Microsoft docs but throughput plateaus past 2 concurrent infers on 4-vCPU.
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §3 phase machine. The `embedding` phase already exists in code (`Features/Processing/Phases/EmbeddingHandler.cs`) and has `MaxAttempts=3` per `appsettings.json:IngestSaga:Phases:embedding`. This ticket changes the **body** of the handler (add change-detection gate), not the **shape** of the phase. §4 best-effort failure cascade: if the ONNX session throws (model file missing, tokenizer corrupt, OOM mid-infer), the phase increments attempts and retries; after 3 attempts → `failed_embedding` terminal. The user-facing note still lands `failed` (not `ready`); `notes.embedding` is null. This is the right semantics — a note with no embedding is not retrievable via kNN but is still readable in Obsidian.
- **`docs/decisions/0045-composite-note-schema.md`** — §1 `notes` table: `embedding vector(256)` (already created by handoff #1 `cloud-schema-v2-handoff.md`). §5 `entities` table: `embedding vector(256)` (same). HNSW indexes on both already exist (`ix_notes_embedding`, `ix_entities_embedding`). No schema migration in this ticket touches those columns — only the new `notes.body_hash` is added.
- **`docs/decisions/0044-cloud-intelligence-layer.md`** — the LLM-intelligence ADR. It doesn't constrain embedding directly, but §"Dedup top-K retrieval" is the consumer that gets useful candidates once this ticket lands. ADR-0044 §"Negative / accepted costs" was written assuming embeddings are zero vectors at the time of handoff #7's drafting; this ticket dissolves that caveat.
- **`docs/decisions/0024-dbcontext-shape.md`** + **`docs/decisions/0028-schema-conventions.md`** — single `CloudDbContext`, snake_case via `EFCore.NamingConventions`, NodaTime `Instant` timestamps, `IHasUpdatedAt` interceptor. The new `notes.body_hash` column follows: `string? BodyHash { get; set; }` on `Note`, mapped to `body_hash text null` in `NoteConfiguration`.
- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers + Respawn. The embedding tests have two lanes:
  - **Fast lane:** unit tests against `GraniteEmbeddingClient` running with the real ONNX file but on tiny canned inputs ("hello world" → assert dim=256, ‖v‖₂ ≈ 1.0, two semantically-similar strings have higher cosine than two unrelated ones). Model load is one-time per test class via `IClassFixture<GraniteEmbeddingFixture>`.
  - **Slow lane:** `[Trait("Category","Slow")]` — end-to-end through the saga with a real Postgres + the real embedding client. The fast-lane fixture is the model load (~1.5 s on M-series, ~3 s on x64 CI); skipping the model is not worth the mocking complexity.
- **`plans/cloud-schema-v2-handoff.md`** — `notes.embedding vector(256)` + `entities.embedding vector(256)` + HNSW indexes are already in the DB. This ticket reads/writes the existing columns and adds **one** new column (`body_hash`) via a small follow-on migration.
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — handoff #3 lands `EmbeddingHandler` against `StubEmbeddingClient` returning `float[256]` of zeros, the `IEmbeddingClient` interface (`Task<float[]> EmbedAsync(string text, CancellationToken ct)`), the DI registration at `Program.cs:216`, and the embedding-phase retry budget. **This ticket is the seam swap** the saga-foundation `// FORK: StubEmbeddingClient` marker calls out. `IEmbeddingClient`'s shape does not change — no `EmbedQueryAsync` / `EmbedPassageAsync` overloads, no `EmbedBatchAsync` (batch is one-call-at-a-time per ADR-0043 §"Embedding"; the existing `ExtractingEntitiesHandler` already calls `EmbedAsync` per candidate, a batch interface would force a refactor there that this ticket doesn't pay for).
- **`plans/cloud-compose-phase-handoff.md`** — handoff #6 locks `notes.body_output` as the canonical body the embedding phase reads (per `EmbeddingHandler.cs:38`: `var text = note.BodyOutput ?? note.BodyInput;`). The composer is the only writer of `body_output` before this ticket; handoff #7's LLM phases don't rewrite it (per handoff #7 §24 "Wikilink anchor splicing is deferred"). `body_output` changes happen only via `composing` phase re-runs. The body-hash gate in this ticket is therefore tied to compose template versioning: `compose-v1` (current) produces a stable body for a given input; bumping to `compose-v2` (handoff #9+) will produce a different body for the same input, and the gate trips automatically. **No special-case logic for the template bump** — hash equality alone drives the gate.
- **`plans/cloud-llm-intelligence-handoff.md`** — handoff #7. Independent of this one. Its `ExtractingEntitiesHandler` calls `IEmbeddingClient.EmbedAsync(cand.CandidateCanonical, ct)` on every dedup candidate (`ExtractingEntitiesHandler.cs:101`) to feed `EntityVectorQueries.NearestAsync`. Until this ticket lands, those candidate embeddings are zero vectors → top-K is uninformative → LLM dedup picks `new_entity` more often than it should → eval P/R will look bad. After this ticket lands, the LLM sees real similar-entity candidates and dedup behaves as ADR-0044 designed it. **No code change in `ExtractingEntitiesHandler` is needed here** — the DI swap is the entire delta from the LLM handler's perspective.
- **`plans/cloud-pivot-plan-2026-05-13.md`** §13 — embedding lives on the control plane (always-on), not the burst worker, because it's CPU-cheap (~30 ms per 512-token pass on M-series, ~80–120 ms on a 4-vCPU x64 VPS). No burst-worker lifecycle interaction; `WorkerLifecycleService` is not invoked by the embedding phase.
- **`plans/tickets-2026-05-13.md`** — CLOUD-009 (Granite ONNX) + CLOUD-010 (pgvector helpers). This ticket implements CLOUD-009; CLOUD-010's helpers already exist (`EntityVectorQueries.NearestAsync` in handoff #7) — no new SQL helpers ship here.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. Embedding is a property of the note; no separate `note_chunks` table in this ticket (deferred per ADR-0043 §"Future-work hooks" #5).
- **Memory `feedback_search_before_answering.md`** — for ML model / library state, WebFetch before answering. The ADR-0043 model name was verified against `https://huggingface.co/blog/ibm-granite/granite-embedding-multilingual-r2` on 2026-05-19 as part of drafting this handoff; that's how the "278m R2 doesn't exist" drift surfaced. Re-verify the chosen tag at implementation time — model cards move quickly.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3. The ONNX Runtime + tokenizer packages compile under warnings-as-errors; pin versions explicitly (see "NuGet package decisions" below).

**Do not litigate ADR-0043's model choice.** If you find an embedding model you think is better (`BGE-M3`, `jina-embeddings-v3`, `nomic-embed-text-v2-moe`), raise a follow-up; do not change the model inside this ticket. The 311m R2 pin is the closest faithful execution of the ADR's intent. The eval chapter (M9 / EVAL-002) sweeps via config; that is the right place to discover whether a different model improves routing accuracy. Likewise, do not relitigate Matryoshka 256-vs-768 — the schema is at 256, this ticket honors it; sweeping dim in eval is a future-work hook.

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **The model name in ADR-0043 is wrong; pin to `ibm-granite/granite-embedding-311m-multilingual-r2`.** Per the verified 2026-05-19 IBM blog post: R2 does **not** ship a 278m variant. The R2 model line is `granite-embedding-97m-multilingual-r2` (384-dim, no Matryoshka), `granite-embedding-311m-multilingual-r2` (768-dim, Matryoshka cut to 768/512/384/256/128), `granite-embedding-149m-english-r2` (English-only), `granite-embedding-47m-small-english-r2` (English-only). The 278m exists only in **R1**. The schema is locked at `vector(256)`, which **requires** Matryoshka support (the 97m's 384-dim with no Matryoshka cannot fit cleanly). Therefore: pin **311m R2**, Matryoshka-cut to 256. Resident footprint is ~600 MB at FP32 (not the ADR's claimed ~200 MB — that was the R1 278m or an INT8 figure); on 4 GB control plane with Postgres ~512 MB + cloud-api ~250 MB + nginx ~50 MB + OS ~500 MB, ~600 MB embedding model leaves ~2 GB headroom. Acceptable. **An ADR-0043 amendment is the follow-up artifact for this drift** — flag in the PR, file the amendment in a separate commit. Do not block this ticket on the amendment.

2. **Single `EmbedAsync(string text, CancellationToken)` signature; no query/passage overloads.** Per the verified HF blog: R2 was explicitly trained to "require no task-specific instructions" — the model behaves like `all-MiniLM-L6-v2` at the API level. The existing `IEmbeddingClient` interface stays byte-for-byte; both callers (`EmbeddingHandler.cs:39` and `ExtractingEntitiesHandler.cs:101`) keep their current call shape. No prefix like `"passage: "` / `"query: "` is prepended inside `GraniteEmbeddingClient`.

3. **Token cap stays at 512; longer-context exploitation is eval future-work.** R2 supports 32K context (vs R1's 512), but ADR-0043 §"Embedding" line 220 locks "Token cap 512 per Granite's context; long bodies truncate to the first 512 tokens (chunking deferred per F15)." The intent is consistency with the ADR's eval baseline — eval scripts assume a single-vector-per-note at the configured cap. Bumping to higher truncation is a config sweep in M9; not in this ticket. **Configurable:** `appsettings.json:IngestSaga:Models:Embedding:MaxTokens=512` is the lever; runtime reads this on every embed (cheap — tokenizer enforces the cap, no model reload needed).

4. **Mean-pool last-hidden-state over the attention mask; L2-normalize; Matryoshka-truncate to 256; L2-normalize again.** This is the standard sentence-transformers pooling recipe for Granite Embedding R2 (the HF model card's `sentence_transformers` integration uses this internally). Concretely:
   ```csharp
   var output = session.Run(inputs);          // logits = (batch=1, seq, hidden=768)
   var lhs = output["last_hidden_state"];     // float[1, seqLen, 768]
   var mask = inputs["attention_mask"];        // long[1, seqLen]
   var pooled = MeanPool(lhs, mask);          // float[768]
   var n1 = L2Normalize(pooled);              // float[768]
   var cut = n1.AsSpan(0, 256).ToArray();     // Matryoshka truncate
   var final = L2Normalize(cut);              // float[256]
   return final;
   ```
   The second L2-normalize after the Matryoshka cut is required because truncating a unit vector breaks the norm. Cosine distance assumes both operands are unit-norm; pgvector's `<=>` operator does not normalize internally. Skipping the second normalize → pgvector cosine distances are off by a few percent → eval P/R drifts. Test this explicitly in `GraniteEmbeddingClientTests.NormIsUnit`.

5. **Singleton model load at host startup; eager warm-up via `IHostedService`.** ONNX Runtime sessions are thread-safe (per the docs and verified empirically by ML.NET) so a single `InferenceSession` shared across all requests is correct. Wire as `services.AddSingleton<IEmbeddingClient, GraniteEmbeddingClient>()`. Add a sibling `GraniteEmbeddingWarmupService : IHostedService` that, in `StartAsync`, calls `_client.EmbedAsync("warmup", CancellationToken.None)` to force-load weights + tokenizer + run one inference pass. Without warm-up, the first capture pays ~1.5–3 s of model load latency on top of its normal embedding latency; with warm-up, the first ingest is as fast as subsequent ingests. The warmup service has no readiness gate — the host can serve traffic while the model loads; the first ingest just waits on the lazy load. (Hooking warmup into `/health/ready` is a follow-up if eval shows the first-capture jitter matters; for thesis MVP, eager-best-effort is enough.)

6. **`SemaphoreSlim(2, 2)` inside `GraniteEmbeddingClient` to bound concurrent inferences.** Per ADR-0043 §"Per-sidecar concurrency caps" the embedding cap is 2. ONNX Runtime is thread-safe but its CPU EP doesn't parallelize beyond hardware threads; on a 4-vCPU control plane, two concurrent `session.Run` calls fully saturate the cores. A third concurrent call would queue inside ORT's intra-op pool anyway; making the queue explicit via SemaphoreSlim gives us a place to surface a metric (`embedding_queue_depth`) and a cancel-token-respecting wait. **No `IngestSaga:Sidecars:Embedding:MaxConcurrency` config key** is added — the cap is hardcoded at `2` matching ADR-0043. (If eval shows we want to bump to 3 or 4, add the config key in a follow-up.)

7. **Tokenizer: `Microsoft.ML.Tokenizers` (the .NET 10 first-party tokenizer package).** Granite R2 ships a HuggingFace-format `tokenizer.json` (Gemma 3 tokenizer for 311m; vocab size 262K). The .NET ecosystem has three viable loaders:
   - **`Microsoft.ML.Tokenizers`** — first-party (.NET Foundation); under active development; supports HF `tokenizer.json` via `Tokenizer.CreateFromConfig(path)`. **Picked.**
   - `Tokenizers.DotNet` (community) — wraps the HF `tokenizers` Rust library via native interop. Faster than ML.Tokenizers in benchmarks but adds a native dep that's a recurring pain on macOS dev machines.
   - Hand-rolled SentencePiece in pure C# — too much surface area for a thesis ticket.
   The Microsoft package needs version pin (target the latest stable on .NET 10; if not yet GA at implementation time, fall back to `Tokenizers.DotNet` and document in the PR description). **Bake the tokenizer file into the cloud-api container image** at build time at `/app/models/granite-embedding-311m-multilingual-r2/tokenizer.json`; no runtime download.

8. **ONNX Runtime: `Microsoft.ML.OnnxRuntime` (managed CPU package).** Version pin to 1.20.x (latest stable .NET 10 compatible at handoff time). The CPU execution provider is the only one registered; no `Microsoft.ML.OnnxRuntime.Gpu` / DirectML / OpenVINO providers. Session options: `EnableCpuMemArena=true`, `GraphOptimizationLevel.ORT_ENABLE_ALL`, `IntraOpNumThreads=0` (let ORT decide based on hardware).

9. **Bake the ONNX model file into the cloud-api container image at build time.** Granite Embedding 311m R2 ONNX is ~600 MB (FP32). Embedding it in the cloud-api image makes the image larger but eliminates a startup-time HuggingFace fetch (which would require either: (a) outbound HTTPS to huggingface.co at boot — privacy / sovereignty issue per ADR-0043 §"Trust boundaries"; or (b) ghcr.io-mirrored model artifact — extra ops surface). The image-bake path:
   ```dockerfile
   # In src/ThanyMarcus.Cloud.Api/Dockerfile (build stage)
   ARG GRANITE_HF_REPO=ibm-granite/granite-embedding-311m-multilingual-r2
   ARG GRANITE_REVISION=main
   RUN huggingface-cli download "$GRANITE_HF_REPO" \
         --revision "$GRANITE_REVISION" \
         --include "onnx/model.onnx" "tokenizer.json" "config.json" \
         --local-dir /tmp/granite
   # In runtime stage:
   COPY --from=build /tmp/granite/onnx/model.onnx /app/models/granite/model.onnx
   COPY --from=build /tmp/granite/tokenizer.json /app/models/granite/tokenizer.json
   COPY --from=build /tmp/granite/config.json /app/models/granite/config.json
   ```
   The `GRANITE_REVISION` arg pins to a commit SHA in prod builds; main during development. This ticket's PR commits the SHA pin alongside the code that consumes it. Total image size: cloud-api was ~250 MB; +600 MB Granite ONNX → ~850 MB. Acceptable for a single-tenant cloud (one user pulls it once at provisioning + redeploys). For dev builds on macOS, the model file is gitignored under `src/ThanyMarcus.Cloud.Api/models/` and a `scripts/download-granite-model.sh` shells out to `huggingface-cli` at developer setup time — same shape as the existing `scripts/install-dev.sh` flow.

10. **Body hash is SHA-256 of UTF-8(`compose_template + "\n" + body_output`).** The template version (`compose-v1`, future `compose-v2`) is part of the hash input so a template bump invalidates the gate even if `body_output` happens to be byte-identical. Implementation: in `EmbeddingHandler.HandleAsync`, before calling `embeddings.EmbedAsync(text, ct)`:
    ```csharp
    var template = job.LastComposeTemplate ?? "unknown";  // populated by ComposingHandler
    var hashInput = template + "\n" + (note.BodyOutput ?? note.BodyInput ?? "");
    var newHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hashInput)));
    if (note.BodyHash == newHash && note.Embedding is not null && job.Kind == IngestJobKind.Reprocess)
    {
        // Reprocess but body unchanged — skip embed, keep existing vector
        note.Status = NoteStatus.Ready;
        note.TransitionVersion += 1;
        await events.AppendEmbeddingEventAsync(job.Id, skipped: true, ct);
        await transitions.TransitionAsync(job, IngestJobStatus.Succeeded, ...);
        return PhaseHandlerResult.Advanced;
    }
    var vector = await embeddings.EmbedAsync(hashInput, ct);  // or just note.BodyOutput; hash includes template only for the gate
    note.Embedding = new Vector(vector);
    note.BodyHash = newHash;
    ```
    **Note:** the *embedded* text is `note.BodyOutput ?? note.BodyInput` (unchanged from current handler); the *hashed* input is `template + body` (template prefix is for cache invalidation only, never embedded). Don't conflate the two. The gate only fires on `kind == Reprocess` to be conservative: first-time capture always embeds (the row's `body_hash` is null on first run anyway). `kind == HubRegen` always embeds because the hub's `body_output` is regenerated each run.

11. **`entities.embedding` re-embed policy: embed once at creation; re-embed when canonical_name changes.** `ExtractingEntitiesHandler.cs:153` populates `Embedding = new Vector(candEmb)` at entity creation. This ticket adds a hook for *re-embedding* an entity when its canonical_name changes (today, no code path mutates canonical_name post-creation; the hook is forward-compatible for a future "rename entity" admin endpoint). Aliases changing does NOT trigger re-embed — only the canonical name is the embedded text per the ADR's "candidate_canonical" convention. **Mechanism:** add an `Entity.CanonicalEmbeddingFingerprint` `[NotMapped]` derived property + a `bool ShouldReembed(string newCanonical)` helper; the rename endpoint (not in this ticket) calls `await embeddings.EmbedAsync(newCanonical, ct)` and writes the result. Until that endpoint exists, the helper is unused but present.

12. **No `IEmbeddingClient.EmbedBatchAsync(IEnumerable<string>)` overload.** ADR-0043 says embedding is in-process + CPU + cheap; per-candidate calls in `ExtractingEntitiesHandler` (median ~3 per note) are ~30–80 ms each, summing to ~150 ms in the worst case. Batch would amortize tokenizer overhead and ORT graph-init overhead per batch, but: (a) ORT graph init is one-time per session, not per call; (b) tokenizer is a few microseconds per text; (c) the batch interface would force `ExtractingEntitiesHandler` to refactor its candidate-by-candidate loop (which currently interleaves dedup LLM calls — a batch would split the loop). Premature; revisit if eval shows embedding-CPU is the bottleneck.

13. **Failure semantics: `LlmStructuredOutputException`-style is wrong for embedding; throw raw exceptions and let `EmbeddingHandler`'s try/catch + the phase-handler retry budget handle them.** Embedding failures are not "model produced bad output" (the model always produces a 768-dim vector); they are "model file missing / corrupt / OOM / native lib not loaded." The current `EmbeddingHandler` doesn't have a try/catch (`EmbeddingHandler.cs:33–57` just lets exceptions propagate to the dispatcher, which records the attempt and re-schedules). That's correct — keep the handler simple, let the dispatcher's phase retry budget (`MaxAttempts=3` for `embedding`) absorb transient failures, and after 3 attempts → `failed_embedding` terminal. **Do not** add a `EmbeddingException` type unless the eval shows specific failure modes that the dispatcher's generic retry mishandles.

14. **Provenance integration: append one `embedding_*` event per embed call (or per skip).** Two new event stages join the LLM-stages registry from handoff #7:
    - `embedding_emit` — fired by `EmbeddingHandler` after a successful embed. Payload: `{stage, model, model_version, duration_ms, dim, body_hash}`. (No `decision`, `confidence`, `rationale` — those are LLM-only fields; leave them null.)
    - `embedding_skip` — fired when the body-hash gate skips the embed. Payload: `{stage, reason: "unchanged_body", body_hash}`.
    Append via the existing `LlmEventAppender` (which writes into `ingest_jobs.events_log`). The `ProvenanceMaterializer.ExtractLlmCalls` regex matches `stage.StartsWith("llm_")` today — extend to match `stage.StartsWith("llm_") || stage.StartsWith("embedding_")`. The `provenance.llm_calls` roll-up then includes embedding events, which eval scripts can filter by stage prefix.

15. **SSE: no new event vocabulary.** The existing `note_phase_changed(extracting_entities → embedding)` and `note_succeeded` events from handoff #3 fire as before. The plugin sees the same SSE timeline. The body-hash skip is invisible to SSE — internally faster, externally identical.

16. **No tokenizer / model unit-tests against canned ONNX outputs.** Snapshotting ORT outputs is brittle (Granite R2 weights may be updated by IBM; outputs may shift by a normalization epsilon). Test invariants instead:
    - `(await client.EmbedAsync("hello world")).Length == 256`
    - `‖await client.EmbedAsync(t)‖₂ ≈ 1.0 ± 1e-4` for any `t`
    - For semantically-related pairs (`"cat sat on mat"` vs `"feline rested on rug"`) cosine > 0.5
    - For unrelated pairs (`"quantum mechanics"` vs `"banana smoothie"`) cosine < the related-pair score
    - English + Russian translation pair (`"hello world"` vs `"привет мир"`) cosine > the unrelated-English-pair score
    The Russian test is the multilingual contract — if it fails, the wrong model is loaded.

17. **`BodyHash` column is plaintext hex (`text`), not `bytea`.** SHA-256 is 32 bytes (64 hex chars). Postgres `text` is fine; saves a `bytea` codec round-trip on every read/write; the column is read-modify-write per ingest, never indexed. **Don't add an index** — equality check on this column happens only via the EF roundtrip (`note.BodyHash == newHash` in C#), not via SQL.

18. **No `WAL`-bypass or DB-level "don't touch updated_at" tricks for the skip path.** The skip path still bumps `notes.updated_at` + `transition_version` + flips `status` from whatever-it-was back to `ready`. That's correct — the user-observable "this note was re-ingested" timeline is preserved; only the embedding compute is skipped. Don't optimize this further.

19. **Cancellation: the embedding call is best-effort interruptible.** ONNX Runtime's `Run()` API takes a `CancellationToken` (since 1.18) and aborts the inference at the next safe point. The handler passes `ct` through. If a user cancels mid-embed, the call returns `OperationCanceledException` and the dispatcher transitions the job to `dead_lettered` via `CancelHandler` on the next phase boundary. No mid-tokenize cancel (tokenization is microseconds; not worth the cancel-check overhead).

20. **No streaming.** Embeddings are single-shot; streaming is meaningless for them.

## Scope boundary (precise)

Two passes. ~1.5–2 person-days total.

### Pass A — `GraniteEmbeddingClient` + tokenizer + ONNX session + DI swap + regression-anchor update (~1d)

**1. Add NuGet packages** to `Directory.Packages.props`:
   ```xml
   <!-- Embedding (CLOUD-EMBEDDING handoff #8): in-process Granite Embedding R2 ONNX -->
   <PackageVersion Include="Microsoft.ML.OnnxRuntime" Version="1.20.1" />
   <PackageVersion Include="Microsoft.ML.Tokenizers" Version="1.0.1" />
   ```
   Add the `<PackageReference>`s to `src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj`. Confirm both packages compile under `Directory.Build.props` warnings-as-errors; if `Microsoft.ML.Tokenizers` has not yet shipped a stable 1.x by the time this ticket runs, fall back to `Tokenizers.DotNet` (latest 1.x) and document in the PR description.

**2. `Infrastructure/Sidecars/Embedding/GraniteEmbeddingOptions.cs`:**
   ```csharp
   public sealed class GraniteEmbeddingOptions
   {
       public string ModelPath { get; set; } = "/app/models/granite/model.onnx";
       public string TokenizerPath { get; set; } = "/app/models/granite/tokenizer.json";
       public int MaxTokens { get; set; } = 512;
       public int EmbeddingDim { get; set; } = 256;   // Matryoshka cut
       public int FullDim { get; set; } = 768;        // Granite 311m hidden size
       public string ModelTag { get; set; } = "ibm-granite/granite-embedding-311m-multilingual-r2";
       public string ModelRevision { get; set; } = "main";  // pin to SHA in prod
   }
   ```
   Configuration section: `appsettings.json:IngestSaga:Models:Embedding`. Register via `services.Configure<GraniteEmbeddingOptions>(builder.Configuration.GetSection("IngestSaga:Models:Embedding"))`.

**3. `Infrastructure/Sidecars/Embedding/GraniteEmbeddingClient.cs`** (~150 LOC):
   ```csharp
   public sealed class GraniteEmbeddingClient : IEmbeddingClient, IDisposable
   {
       private readonly InferenceSession session;
       private readonly Tokenizer tokenizer;
       private readonly GraniteEmbeddingOptions opts;
       private readonly SemaphoreSlim sem = new(2, 2);
       private readonly ILogger<GraniteEmbeddingClient> log;

       public GraniteEmbeddingClient(IOptions<GraniteEmbeddingOptions> opts, ILogger<GraniteEmbeddingClient> log)
       {
           this.opts = opts.Value;
           this.log = log;
           this.session = new InferenceSession(this.opts.ModelPath, BuildSessionOptions());
           this.tokenizer = Tokenizer.CreateFromConfig(this.opts.TokenizerPath);
       }

       public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
       {
           await sem.WaitAsync(ct);
           try
           {
               var encoded = tokenizer.Encode(text ?? "");
               var truncated = TruncateToMaxTokens(encoded, opts.MaxTokens);
               var inputs = BuildOrtInputs(truncated);
               using var results = session.Run(inputs);
               var pooled = MeanPool(results, truncated.AttentionMask);
               L2NormalizeInPlace(pooled);
               var cut = pooled.AsSpan(0, opts.EmbeddingDim).ToArray();
               L2NormalizeInPlace(cut);
               return cut;
           }
           finally { sem.Release(); }
       }

       private static SessionOptions BuildSessionOptions() => new()
       {
           GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
           EnableCpuMemArena = true,
           IntraOpNumThreads = 0,  // ORT default = #cores
       };

       // MeanPool, L2NormalizeInPlace, BuildOrtInputs, TruncateToMaxTokens — straightforward helpers, ~40 LOC total
       public void Dispose() { sem.Dispose(); session.Dispose(); }
   }
   ```
   **`MeanPool` exact shape:**
   ```csharp
   private static float[] MeanPool(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs, long[] attentionMask)
   {
       var lhs = outputs.First(o => o.Name == "last_hidden_state").AsTensor<float>();
       int seqLen = (int)lhs.Dimensions[1];
       int hidden = (int)lhs.Dimensions[2];
       var pooled = new float[hidden];
       long maskSum = 0;
       for (int t = 0; t < seqLen; t++)
       {
           if (attentionMask[t] == 0) continue;
           maskSum++;
           for (int h = 0; h < hidden; h++)
               pooled[h] += lhs[0, t, h];
       }
       if (maskSum == 0) return pooled;  // empty input — return zero
       for (int h = 0; h < hidden; h++) pooled[h] /= maskSum;
       return pooled;
   }
   ```

**4. `Infrastructure/Sidecars/Embedding/GraniteEmbeddingWarmupService.cs`:**
   ```csharp
   public sealed class GraniteEmbeddingWarmupService(IEmbeddingClient client, ILogger<GraniteEmbeddingWarmupService> log) : IHostedService
   {
       public async Task StartAsync(CancellationToken ct)
       {
           try
           {
               var sw = Stopwatch.StartNew();
               _ = await client.EmbedAsync("warmup", ct);
               log.LogInformation("Granite embedding warmup completed in {Ms} ms", sw.ElapsedMilliseconds);
           }
           catch (Exception ex)
           {
               log.LogWarning(ex, "Granite embedding warmup failed; first capture will load lazily");
           }
       }
       public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
   }
   ```
   Wire via `services.AddHostedService<GraniteEmbeddingWarmupService>()`.

**5. `Program.cs` swap:**
   ```csharp
   // BEFORE (line 216):
   builder.Services.AddSingleton<IEmbeddingClient, StubEmbeddingClient>();

   // AFTER:
   builder.Services.Configure<GraniteEmbeddingOptions>(
       builder.Configuration.GetSection("IngestSaga:Models:Embedding"));
   builder.Services.AddSingleton<IEmbeddingClient, GraniteEmbeddingClient>();
   builder.Services.AddHostedService<GraniteEmbeddingWarmupService>();
   ```
   Delete `Infrastructure/Sidecars/Stubs/StubEmbeddingClient.cs`. No other call site references it; the `// FORK:` marker in `Stubs/` planted by handoff #3 §"Acceptance criteria" point 5 is resolved by deletion.

**6. `appsettings.json` add:**
   ```json
   "Embedding": {
     "ModelPath": "/app/models/granite/model.onnx",
     "TokenizerPath": "/app/models/granite/tokenizer.json",
     "MaxTokens": 512,
     "EmbeddingDim": 256,
     "FullDim": 768,
     "ModelTag": "ibm-granite/granite-embedding-311m-multilingual-r2",
     "ModelRevision": "main"
   }
   ```
   Insert under `IngestSaga.Models`. For local dev, override `ModelPath` / `TokenizerPath` to repo-relative paths via `appsettings.Development.json`.

**7. Dockerfile changes** in `src/ThanyMarcus.Cloud.Api/Dockerfile`:
   - Build stage: install `python3` + `pip install -q huggingface_hub`, then download model files into `/tmp/granite`.
   - Runtime stage: `COPY --from=build /tmp/granite/onnx/model.onnx /app/models/granite/model.onnx` (+ tokenizer + config).
   - Pin `GRANITE_REVISION` as a build ARG; set in CI to the resolved SHA at the time the cloud-api image is built. Document in `infra/docker/cloud/docker-compose.yml`'s build args.

**8. Local dev script** `scripts/download-granite-model.sh`:
   ```sh
   #!/usr/bin/env sh
   set -eu
   REPO="${1:-ibm-granite/granite-embedding-311m-multilingual-r2}"
   REV="${2:-main}"
   DEST="src/ThanyMarcus.Cloud.Api/models/granite"
   mkdir -p "$DEST"
   huggingface-cli download "$REPO" --revision "$REV" \
       --include "onnx/model.onnx" "tokenizer.json" "config.json" \
       --local-dir "$DEST"
   echo "Granite model staged at $DEST"
   ```
   Add `src/ThanyMarcus.Cloud.Api/models/` to `.gitignore`. Add `appsettings.Development.json` overrides to point at the repo-relative path. Document in `CONTRIBUTING.md` that fresh clones run `scripts/download-granite-model.sh` once.

**9. Tests for Pass A:**
   - `tests/ThanyMarcus.Cloud.Tests/Infrastructure/Embedding/GraniteEmbeddingClientTests.cs`:
     - `DimensionIs256`: `(await client.EmbedAsync("hello world")).Length.ShouldBe(256)`.
     - `NormIsUnit`: `Math.Abs(L2(await client.EmbedAsync("anything")) - 1.0).ShouldBeLessThan(1e-4)`.
     - `RelatedPairCosineGreaterThanUnrelated`: cosine("cat on mat", "feline on rug") > cosine("cat on mat", "quantum mechanics").
     - `CrossLingualEnRuPair`: cosine("hello world", "привет мир") > cosine("hello world", "completely unrelated text about ducks").
     - `EmptyInputReturnsValidVector`: empty/whitespace input does not throw; returns a 256-dim vector (typically near-zero or near-pad-token embedding; just assert no crash and `Length==256`).
     - `LongInputTruncatesAt512Tokens`: a 2000-word input does not throw, returns 256-dim, `‖v‖₂ ≈ 1.0`.
     - Trait: `[Trait("Category","Slow")]` because the model load is ~1.5–3 s.
   - `tests/ThanyMarcus.Cloud.Tests/Phases/EmbeddingHandlerTests.cs` (update existing):
     - Replace zero-vector assertion with non-zero + unit-norm assertion (when the real client is in DI).
     - Add a stub-mode test that still injects `StubEmbeddingClient` for the existing "phase transition" test (the test suite should be able to choose stub vs real via fixture composition).
   - `tests/ThanyMarcus.Cloud.Tests/EndToEnd/CompositeIngestSagaEndToEndTests.cs` (regression anchor):
     - Update the embedding assertion: `note.Embedding.ShouldNotBeNull()`; `note.Embedding!.ToArray().Sum(Math.Abs).ShouldBeGreaterThan(0)`; `Math.Abs(L2(note.Embedding!.ToArray()) - 1.0).ShouldBeLessThan(1e-3)`.

**10. Provenance event:**
   - Add `embedding_emit` event in `LlmEventAppender` (or a new `EmbeddingEventAppender` if the LLM appender's shape is too LLM-specific to extend cleanly — prefer extending the existing appender to avoid duplication).
   - Extend `ProvenanceMaterializer.ExtractLlmCalls` predicate from `stage.StartsWith("llm_")` to `stage.StartsWith("llm_") || stage.StartsWith("embedding_")`.
   - Add unit test in `ProvenanceMaterializerTests.cs`: an embedding event lands in `provenance.llm_calls` with `stage: "embedding_emit"`.

→ At end of Pass A: a composite-ingest finalize through `/api/ingest/{id}/finalize` produces `notes.embedding` as a unit-norm 256-dim vector that varies meaningfully with input. `dotnet test --filter "Category!=Slow"` is green; the slow lane (real ONNX) is green in CI's slow-test job. The regression anchor `CompositeIngestSagaEndToEndTests` is green with the updated assertions. Pgvector cosine top-K on `notes.embedding` returns informative neighbors.

### Pass B — `notes.body_hash` migration + change-detection gate + entity-side re-embed seam (~0.5–1d)

**1. Migration `<ts>_AddNotesBodyHash.cs`:**
   ```csharp
   migrationBuilder.AddColumn<string>(
       name: "body_hash",
       table: "notes",
       type: "text",
       nullable: true);
   ```
   Down: `DropColumn`. No index. Run `dotnet ef migrations add AddNotesBodyHash --project src/ThanyMarcus.Cloud.Api`; commit the migration + designer + snapshot atomically.

**2. `Note.cs`:** add `public string? BodyHash { get; set; }`. `NoteConfiguration.cs`: `builder.Property(n => n.BodyHash).HasColumnType("text");`. No index, no default.

**3. `EmbeddingHandler.cs` rewrite** per design-decision #10's pseudocode. Specifically:
   - Read `note.BodyOutput ?? note.BodyInput ?? ""` as the embedded text.
   - Compute `newHash = SHA256(template + "\n" + bodyText)` where `template = job.LastComposeTemplate ?? "unknown"`.
   - Branch:
     - `kind == Reprocess && note.BodyHash == newHash && note.Embedding != null` → skip embed, fire `embedding_skip` event, transition to `Succeeded`.
     - Otherwise → embed, write `note.Embedding` + `note.BodyHash = newHash`, fire `embedding_emit` event, transition to `Succeeded`.
   - First-time capture (`note.BodyHash == null`) always embeds because the equality check fails on the null side.
   - `HubRegen` always embeds (no skip path — hub bodies are regenerated each run by `HubGenerationHandler`).

**4. Tests for Pass B:**
   - `EmbeddingHandlerTests.SkipsEmbedOnReprocessWhenBodyHashUnchanged`:
     - Pre-seed a note with `BodyHash="hash1"`, `Embedding=someVec` from a first run.
     - Insert a new `IngestJob(kind=Reprocess)`.
     - Run handler with body that produces the same hash.
     - Assert: `embeddings.EmbedAsync` was NOT called (use a counting fake), `note.Embedding` unchanged, `notes.events_log` contains an `embedding_skip` event.
   - `EmbeddingHandlerTests.ReembedsOnReprocessWhenBodyChanged`:
     - Same setup but reprocess with different body → embed called, `BodyHash` updated, `Embedding` differs, `embedding_emit` event fired.
   - `EmbeddingHandlerTests.AlwaysEmbedsOnHubRegen`:
     - `kind=HubRegen` with identical hash to a previous run → embed still called.
   - `EmbeddingHandlerTests.FirstTimeCaptureEmbeds`:
     - `kind=Capture`, `note.BodyHash=null` → embed called, hash written.

**5. Entity-side re-embed seam.** Add the helper without using it yet:
   ```csharp
   // Features/Entities/EntityEmbeddingHelper.cs
   internal static class EntityEmbeddingHelper
   {
       public static async Task<Vector> EmbedCanonicalAsync(
           IEmbeddingClient client, string canonicalName, CancellationToken ct) =>
           new Vector(await client.EmbedAsync(canonicalName ?? "", ct));
   }
   ```
   `ExtractingEntitiesHandler.cs:152` (the `new Vector(candEmb)` line) can be left as-is OR routed through `EntityEmbeddingHelper.EmbedCanonicalAsync` — both are fine; pick the route-through for symmetry. Future "rename entity" admin endpoint (not in this ticket) calls the same helper.

**6. Update ADR-0043 amendment.** Land a short amendment to `docs/decisions/0043-cloud-side-model-lineup.md`:
   ```markdown
   > **Amendment 2026-05-19 — Granite R2 model name correction**
   >
   > The original §"Embedding (text retrieval)" row named "IBM Granite Embedding 278m R2" — but R2 does not ship a 278m variant; the 278m exists only in R1. The R2 model line is 97m (384-dim, no Matryoshka), 311m (768-dim with Matryoshka cuts to 512/384/256/128), and English-only variants. The schema's locked `vector(256)` requires Matryoshka, so the implementation pins **`ibm-granite/granite-embedding-311m-multilingual-r2`**, Matryoshka-cut to 256. Resident footprint ≈ 600 MB (FP32 ONNX), not the ~200 MB the original ADR estimated. Memory budget at §"Memory budget" updated accordingly: control plane footprint ≈ 4.4 GB on the 4 GB tier → resize to 8 GB control plane OR drop one Docling replica → mark as a Tier-A capacity decision before any production rollout. For thesis MVP, the 4 GB tier still functions because the 600 MB headroom previously estimated was a floor, not a ceiling; runtime measurements at first apply will confirm.
   ```
   Land this in the same PR as the code; the amendment + the implementation move together.

**7. Tests + acceptance:**
   - `dotnet build` clean with warnings-as-errors.
   - `dotnet test --filter "Category!=Slow"` green.
   - `dotnet test --filter "Category=Slow"` green (CI slow lane).
   - The end-to-end regression anchor (`CompositeIngestSagaEndToEndTests`) is green with non-zero unit-norm embedding assertions.
   - A reprocess against an unchanged body in an integration test counts zero `embeddings.EmbedAsync` calls and one `embedding_skip` event.
   - The ADR-0043 amendment commits alongside the code.

→ At end of Pass B: reprocess is fast for the common "user clicked reprocess but nothing changed" case (skip ~50 ms of embed work), correct for the "body actually changed" case (re-embed and update hash), and forward-compatible with a future `compose-v2` bump (the template prefix in the hash auto-invalidates). Entity re-embed is a documented seam ready for the "rename entity" admin endpoint. ADR-0043's model-name drift is reconciled.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   └── 0043-cloud-side-model-lineup.md                       # CHANGED (Pass B): + Amendment 2026-05-19 — Granite R2 model name correction
├── plans/
│   └── cloud-embedding-handoff.md                            # THIS FILE
├── Directory.Packages.props                                  # CHANGED: + Microsoft.ML.OnnxRuntime, + Microsoft.ML.Tokenizers
├── scripts/
│   └── download-granite-model.sh                              # NEW (dev helper)
├── src/ThanyMarcus.Cloud.Api/
│   ├── ThanyMarcus.Cloud.Api.csproj                          # CHANGED: + Microsoft.ML.OnnxRuntime, + Microsoft.ML.Tokenizers PackageReferences
│   ├── Dockerfile                                            # CHANGED: build stage downloads Granite model + tokenizer; runtime stage copies into /app/models/granite/
│   ├── Program.cs                                            # CHANGED: replace StubEmbeddingClient registration with GraniteEmbeddingClient + warmup; configure GraniteEmbeddingOptions section
│   ├── appsettings.json                                      # CHANGED: + IngestSaga.Models.Embedding section
│   ├── appsettings.Development.json                          # CHANGED: override ModelPath / TokenizerPath to repo-relative paths
│   ├── .gitignore (or repo-level)                            # CHANGED: + src/ThanyMarcus.Cloud.Api/models/
│   ├── models/                                                # NEW dir (gitignored); holds downloaded model files in dev
│   ├── Features/
│   │   ├── Ingest/
│   │   │   ├── Note.cs                                        # CHANGED: + BodyHash property
│   │   │   └── NoteConfiguration.cs                           # CHANGED: + BodyHash column mapping
│   │   ├── Entities/
│   │   │   └── EntityEmbeddingHelper.cs                       # NEW (Pass B): forward-compatible helper for rename-driven re-embed
│   │   └── Processing/
│   │       └── Phases/
│   │           └── EmbeddingHandler.cs                        # CHANGED: body-hash gate + provenance event emission
│   └── Infrastructure/
│       ├── Database/Migrations/
│       │   ├── <ts>_AddNotesBodyHash.cs                       # NEW (Pass B)
│       │   ├── <ts>_AddNotesBodyHash.Designer.cs              # NEW
│       │   └── CloudDbContextModelSnapshot.cs                 # CHANGED: EF-regenerated
│       ├── Llm/
│       │   └── LlmEventAppender.cs                            # CHANGED: + AppendEmbeddingEmitAsync / AppendEmbeddingSkipAsync OR generic AppendStageAsync
│       └── Sidecars/
│           ├── Embedding/                                     # NEW directory
│           │   ├── GraniteEmbeddingOptions.cs                 # NEW
│           │   ├── GraniteEmbeddingClient.cs                  # NEW
│           │   └── GraniteEmbeddingWarmupService.cs           # NEW
│           └── Stubs/
│               └── StubEmbeddingClient.cs                     # DELETED
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Infrastructure/Embedding/
    │   ├── GraniteEmbeddingFixture.cs                         # NEW (IClassFixture; loads model once per test class)
    │   └── GraniteEmbeddingClientTests.cs                     # NEW
    ├── Phases/
    │   └── EmbeddingHandlerTests.cs                           # CHANGED: + 4 new tests (skip-on-unchanged, reembed-on-changed, always-on-hub-regen, first-capture-embeds); existing zero-vector assertion replaced with unit-norm
    └── EndToEnd/
        └── CompositeIngestSagaEndToEndTests.cs                 # CHANGED: regression anchor — non-zero unit-norm embedding assertion
```

## NuGet package decisions

| Package | Pinned version | Why | Fallback |
|---|---|---|---|
| `Microsoft.ML.OnnxRuntime` | `1.20.1` | First-party Microsoft; .NET 10 compatible; CPU EP; ships managed P/Invoke bindings for darwin-arm64 + linux-amd64 + win-amd64. License MIT. | Pin to latest 1.19.x if 1.20.x has a regression by impl time. |
| `Microsoft.ML.Tokenizers` | `1.0.1` (or latest GA on .NET 10) | First-party Microsoft; supports HF `tokenizer.json` via `Tokenizer.CreateFromConfig(path)`; license MIT. | `Tokenizers.DotNet` 1.x (community wrapper of Rust HF tokenizers) if Microsoft package not GA. Document the swap in the PR. |

Both packages compile under warnings-as-errors at handoff time. Verify at PR time; if a new CVE-grade warning surfaces on `Microsoft.ML.OnnxRuntime`, suppress with a `NoWarn` only on the offending package reference (not globally) and flag in the PR.

## ADR-0043 drift summary

| ADR-0043 said | Reality (verified 2026-05-19 via HF blog) | Action |
|---|---|---|
| "IBM Granite Embedding 278m R2" | 278m only exists in R1; R2 = 97m, 311m, English variants | Pin 311m R2 (Matryoshka 256). Land amendment. |
| "~200 MB at 384 dim" | 311m FP32 ONNX ≈ 600 MB on disk + RAM | Update memory budget in amendment; 4 GB control plane has 2 GB headroom not 6.6 GB. |
| "Token cap 512 per Granite's context" | R2 supports 32K context | Stick at 512 (matches eval baseline); document the 32K headroom as future-work in §"Future-work hooks" #5. |
| "Apache 2.0; first-party ONNX shipped" | Confirmed Apache 2.0; ONNX shipped first-party at `onnx/model.onnx` | No change. |
| "explicit ru/uk/pl training" | R2 lists 52 enhanced-support languages including ru, uk, pl, plus 200+ general | No change; cross-lingual test asserts the contract. |
| "require no task-specific instructions" | Confirmed; behaves like `all-MiniLM-L6-v2` at API level | No prefix overloads on `IEmbeddingClient`; single `EmbedAsync(text)` shape. |

The amendment text in Pass B step 6 is the artifact that closes this drift.

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors.
2. ✅ `dotnet test --filter "Category!=Slow"` green; `dotnet test --filter "Category=Slow"` green in CI's slow-test job.
3. ✅ `GraniteEmbeddingClient.EmbedAsync` returns a `float[256]` whose L2-norm is `1.0 ± 1e-4` for any non-empty input.
4. ✅ The cross-lingual En↔Ru test (`cos("hello world", "привет мир") > cos("hello world", "banana smoothie recipe")`) passes — proves the right multilingual model is loaded.
5. ✅ `Program.cs` no longer references `StubEmbeddingClient`; `StubEmbeddingClient.cs` is deleted; `grep -rn "StubEmbeddingClient" src` returns zero results.
6. ✅ `appsettings.json` has the new `IngestSaga.Models.Embedding` section with all six keys; `appsettings.Development.json` points the two paths at the repo-relative model dir.
7. ✅ The `<ts>_AddNotesBodyHash` migration applies cleanly on a fresh Postgres + on a Postgres at the prior schema; rolls back cleanly.
8. ✅ `Note.BodyHash` is populated on every successful capture; equals SHA-256(template + "\n" + body) hex; reads round-trip via EF.
9. ✅ A reprocess on a note whose body+template hash is unchanged calls `IEmbeddingClient.EmbedAsync` **zero times**, and `events_log` contains one `embedding_skip` event.
10. ✅ A reprocess on a note whose body+template hash changed re-embeds (one `EmbedAsync` call), updates `BodyHash`, updates `Embedding`, fires one `embedding_emit` event.
11. ✅ A first-time capture always embeds; `Note.BodyHash` is null before the first run and populated after.
12. ✅ Hub-regen always embeds (no skip path).
13. ✅ `provenance.llm_calls` array in `notes.provenance` includes `embedding_emit` / `embedding_skip` events (the materializer's predicate now matches both `llm_*` and `embedding_*` prefixes).
14. ✅ End-to-end regression anchor (`CompositeIngestSagaEndToEndTests`) is green with non-null + non-zero + unit-norm embedding assertions.
15. ✅ Warmup logs `Granite embedding warmup completed in X ms` at host startup; first-capture latency is comparable to subsequent-capture latency.
16. ✅ ADR-0043 amendment commits in the same PR as the code; amendment narrative matches the table in "ADR-0043 drift summary" above.
17. ✅ No `// TODO` markers in shipped code. Allowed `// FORK:` markers: at `EntityEmbeddingHelper` (forks to the future "rename entity" admin endpoint), at `GraniteEmbeddingOptions.ModelTag` (forks to a `compose-v2` body-hash-key bump if needed).

## Risks and gotchas

- **ONNX Runtime native dep on macOS.** `Microsoft.ML.OnnxRuntime` ships a `runtimes/osx-arm64/native/libonnxruntime.dylib` that .NET's runtime identifier graph picks up automatically; usually works. If a dev hits "DllNotFoundException: onnxruntime" on macOS, force the runtime identifier via `--runtime osx-arm64` on `dotnet test` OR add `<RuntimeIdentifier>$(NETCoreSdkRuntimeIdentifier)</RuntimeIdentifier>` to the test csproj. Document if encountered.
- **Tokenizer drift between `Microsoft.ML.Tokenizers` and HF's Rust impl.** Granite R2's tokenizer.json is straight HF format; ML.Tokenizers should round-trip identically. If a tokenization mismatch is found (e.g., handling of byte-level BPE on a specific multilingual char class), the symptom is sentence-level embeddings drift by a few percent vs the HF Python reference. **Mitigation:** add a tokenizer-parity test that compares `ML.Tokenizers.Encode("hello world")` token IDs against a checked-in reference array produced by HF's Python `transformers.AutoTokenizer.from_pretrained(MODEL).encode("hello world")`. Pin to two checked-in arrays (one English, one mixed-script).
- **Model file size in cloud-api image.** ~600 MB ONNX in the image pushes the image to ~850 MB. Pull time at provisioning is +30–60 s on most networks; image-pull-while-cloud-init runs is already the bottleneck per `cloud-001-handoff.md`. Document in the PR; if a future ticket migrates to GHCR-hosted model artifact (separate from cloud-api image) the embedding model becomes a separately-cached layer.
- **HNSW index rebuild after first real embedding.** The HNSW index built by handoff #1 was built on an empty `notes.embedding` column (all NULLs). After this ticket lands, the first ingest run populates real vectors; HNSW adds them incrementally per insert, not via a one-shot build. No rebuild step needed. At smoke time, expect the first few captures' INSERTs to be ~10 ms slower than steady-state (HNSW graph initialization).
- **Memory footprint conflict with ADR-0043 §"Memory budget".** The original ADR estimated ~200 MB for embedding; reality is ~600 MB (FP32 ONNX). The 4 GB control plane absorbs the difference (the 2 GB headroom drops to ~600 MB), but if the eval workload also runs Docling at 1 GB + Postgres at 512 MB + cloud-api at 250 MB + nginx at 50 MB + OS at 500 MB → total ~2.85 GB, leaves ~1.15 GB. **Risk:** under load, OS kills cloud-api (which loaded the ONNX model). **Mitigation 1:** the burst worker already takes the heavy LLM weight off the control plane (Ollama + Parakeet live on the 16 GB worker tier). **Mitigation 2:** consider INT8 quantized Granite (~200 MB) if the FP32 model causes OOM on the 4 GB tier; quantization-quality trade-off is named in the ADR amendment but not measured in this ticket. **Mitigation 3:** resize control plane to 8 GB (~$48/mo DO instead of $24/mo) — a real cost decision the user opts into via the portal.
- **Warmup race vs first ingest.** If a finalize POST arrives before warmup completes, the first ingest's embed call serializes behind the warmup call (both go through the `SemaphoreSlim(2,2)` — no problem) but the warmup's ONNX session-init work happens once globally, so the second caller waits ~1.5–3 s for the first to finish. Not a bug, just a UX wrinkle; warmup at boot ensures the user never sees this in practice unless they ingest within the first ~3 s of the host's life.
- **`Microsoft.ML.Tokenizers` API surface churn.** The package is still under active development at handoff time. The `Tokenizer.CreateFromConfig(path)` API was confirmed stable for the HF tokenizer.json format; if a major version breaks the API, the test suite catches it (the tokenizer-parity test is the canary).
- **L2-normalize numerical precision on small vectors.** A 256-dim vector with very small magnitude (e.g., embed of an empty string) can produce NaN after L2-normalize (`1/0` if all components are zero). Guard with an epsilon check: `if (norm < 1e-12) return vector; // un-normalized; pgvector cosine returns NaN against this — acceptable for empty inputs because no real query will be near-zero.`. Test covers this case.

## What handoffs #9+ inherit

- **A working in-process embedding client** that the LLM-intelligence handoff #7's `ExtractingEntitiesHandler.cs:101` (candidate-canonical embedding) and `EntityVectorQueries.NearestAsync` (top-K dedup retrieval) consume invisibly via DI. **Handoff #7 needs no code change** — its zero-vector caveat dissolves the moment this handler swap commits.
- **A body-hash gate ready for `compose-v2`.** When a future handoff bumps to `compose-v2` (LLM wikilink splicing per ADR-0044 §3 + handoff #6 §"What handoffs #7+ inherit"), the template-prefix in the hash automatically invalidates existing rows' `BodyHash` → next reprocess re-embeds. No additional plumbing needed.
- **An entity re-embed helper** (`EntityEmbeddingHelper.EmbedCanonicalAsync`) wired to the same singleton client; the future "rename entity" admin endpoint (`PATCH /api/admin/entities/{id}` per ADR-0045 §"Migration plan") can call it without further wiring.
- **Provenance roll-up containing embedding events.** Eval scripts that count `provenance.llm_calls.length` and filter by stage prefix will see embedding events alongside LLM events; M9 / EVAL-002 routing-accuracy and EVAL-003 entity-dedup analyses can now stratify by "did this note have a real or stub embedding?" — useful when reproducing eval runs.
- **A working cross-lingual retrieval substrate.** Once `entities.embedding` is populated with real vectors, the pgvector top-K query in `EntityVectorQueries.NearestAsync` returns informative neighbors regardless of script (Latin / Cyrillic / Greek / Devanagari); the multilingual eval slice (M9 / EVAL-003 stratified by source language) is unblocked.
- **The pgvector `<=>` cosine operator's correctness** is now load-bearing. Schema-v2's HNSW index (`USING hnsw (embedding vector_cosine_ops)`) is exercised for the first time on real data. If eval shows recall@K is unexpectedly low, the HNSW `ef_construction` / `m` parameters can be tuned via raw SQL migration (out of scope here).

```
CLOUD-SCHEMA-V2 (handoff #1)
  ─► CLOUD-SIDECARS (handoff #2)
  ─► CLOUD-INGEST-SAGA-FOUNDATION (handoff #3)
  ─► CLOUD-PROCESSORS-LIGHT (handoff #4)
  ─► CLOUD-PROCESSORS-HEAVY (handoff #5)
  ─► CLOUD-COMPOSE-PHASE (handoff #6)
  ─► CLOUD-LLM-INTELLIGENCE (handoff #7) ─┐
  ─► CLOUD-EMBEDDING (this handoff #8) ───┴─► (#7 + #8 are order-independent peers; pgvector dedup becomes informative after #8)
```

## What "done" looks like

```
$ rg -n "StubEmbeddingClient" src
(no matches)

$ dotnet build
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet test --filter "Category!=Slow"
Passed!  - Failed: 0, Passed: N, Skipped: 0

$ dotnet test --filter "Category=Slow" --logger "console;verbosity=detailed" 2>&1 | grep "warmup completed"
Granite embedding warmup completed in 2143 ms
Granite embedding warmup completed in 1872 ms   (second test-class load)
...

# Manual smoke against a dev cloud:
$ curl -X POST -H "Authorization: Bearer $PLUGIN_TOKEN" \
       -F 'body=Test note about Berlin and Acme Corp.' \
       https://my-cloud.thany.click/api/ingest/init | jq .id
"<note-uuid>"

$ curl -X POST -H "Authorization: Bearer $PLUGIN_TOKEN" \
       https://my-cloud.thany.click/api/ingest/<note-uuid>/finalize

# Wait ~10s for the saga, then:
$ psql -d cloud_db -c "SELECT id, body_hash IS NOT NULL AS hashed, \
                              array_length(embedding::float[], 1) AS dim, \
                              embedding::float[] <-> embedding::float[] AS self_dist \
                         FROM notes WHERE id = '<note-uuid>';"
   id   | hashed | dim | self_dist
--------+--------+-----+-----------
 ...    | t      | 256 | 0
(1 row)

# Reprocess with same body — no embed call expected
$ curl -X POST https://my-cloud.thany.click/api/notes/<note-uuid>/reprocess
$ psql -d cloud_db -c "SELECT events_log->-1->>'stage' FROM ingest_jobs ORDER BY created_at DESC LIMIT 1;"
 ?column?
-----------------
 embedding_skip
(1 row)
```

The cloud now produces semantically-meaningful 256-dim embeddings for every captured note, re-embeds only when content actually changed, and is ready for eval-quality kNN retrieval and entity dedup. The next handoff in the series (handoff #9, TBD — likely `compose-v2` for LLM-anchor wikilink splicing, or sharing + W1+P1+F1+A1 zip-build) picks up against this seam unchanged.
