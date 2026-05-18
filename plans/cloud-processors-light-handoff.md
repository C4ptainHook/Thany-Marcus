# CLOUD-PROCESSORS-LIGHT — UrlFetcherWorker (real) + VlmWorker (real) — Handoff Brief

Date: 2026-05-19
Status: Draft. Handoff #4 of the ADR-0042 implementation series. **Stub → real swap for the two non-LLM specialist clients** that share `SpecialistWorkerBase<TClient>` from `cloud-ingest-saga-foundation-handoff.md` (handoff #3): `IUrlFetcherClient` and `IVlmClient`. Real model wiring for Docling, Parakeet, embedding, video-splitter, and the routing/entity-extraction LLM is **out of scope** — handoffs #5+ cover those.

**Goal:** swap two DI registrations and add ~600 LOC of adapter code so that two specialist workers from handoff #3 stop returning canned text and start doing real work. After this ticket: a URL composite attachment fetches its HTML, runs through SmartReader + ReverseMarkdown, and writes real article markdown into `attachments.extracted_text`; an image composite attachment runs EXIF + pHash + dimension + blur pre-flight (skipping garbage cheap), then calls Ollama `/api/generate` against MiniCPM-V 4.6 Q4 with `format:"json"` for a `{description, text_in_image}` payload that gets stored as canonical "Description:\n<...>\n\nText:\n<...>" with `extra` carrying the EXIF + pHash + raw model JSON, and the `extraction_cache_key = sha256:ollama:minicpm-v-4.6:Q4_K_M` populated so same-attachment cross-note duplicates pay zero VLM cost. URL HEAD detection re-routes binary URLs (image/audio/video/PDF content-types) by inserting a sibling `attachments` row + `extraction_task` for the appropriate sidecar, transparently turning a pasted image-URL into a VLM job. The Pass-C regression anchor in `CompositeIngestSagaEndToEndTests.cs` continues to pass against the stubs (registered via test override); a new sibling test exercises the real adapters against a stand-in Ollama (WireMock) and a stand-in HTTP origin (also WireMock).

Estimated **1.5–2 person-days** with AI-agent assistance. ~1d for the VLM path (pre-flight + Ollama call + JSON parse + cache key), ~0.5d for the URL HEAD-and-reroute path (the AngleSharp + SmartReader extraction itself is already shipped as `UrlExtractor` from CLOUD-002; this ticket just wraps it and adds the rerouting logic), ~0.5d for tests + smoke. Cheap because the framework from handoff #3 owns the claim loop, retry, lease, SSE, cache lookup, and DI shape — this ticket reads like five focused C# classes plus their tests.

This handoff **does not** ship: real Docling, Parakeet, embedding, or video-splitter clients (handoffs #5–#6); the routing/entity-extraction LLM client (`OllamaLlmClient` — separate ticket, may piggy-back on this one's Ollama HttpClient pattern); any saga/SSE/schema changes; any plugin TS contract changes. It assumes handoff #1 (schema-v2), #2 (sidecars + pulled models), and #3 (orchestrator + 5 specialist workers + stub clients + SSE + reprocess/cancel + provenance) have all landed and are green.

## Where decisions live (read before doing anything)

- **`docs/decisions/0043-cloud-side-model-lineup.md`** — the contract. §"Per-kind dispatch" + §"Classical pre-flight per kind" + §"URL → binary re-route" + §"Sidecar binary access — presigned GET URLs, not multipart" + §"Sub-pipeline detail per worker" (specifically the `VlmWorker` and `UrlFetcherWorker` blocks) define the exact shape of the work this ticket lands. Cache-key format `sha256:ollama:minicpm-v-4.6:Q4_K_M` is from the same section.
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §1 worker topology (`UrlFetcherWorker` pool=4, `VlmWorker` pool=1), §3 `extraction_tasks` lifecycle, §4 best-effort failure cascade (a failed VLM call doesn't fail the parent job — `attachment.extraction_status='failed'` + `extraction_error=<msg>`; the orchestrator advances anyway), §7 latency budget (the targets this ticket's adapters must hit: image ~5–15 s, URL ~1–3 s).
- **`docs/decisions/0044-cloud-intelligence-layer.md`** — VLM prompt detail. The `description + text_in_image` two-field JSON schema is named here. **Read §"VLM prompt template" before writing the prompt builder** — the prompt content, JSON keys, and OCR-vs-description framing are locked. Do not re-litigate; if §"VLM prompt template" appears thin or absent (the doc is being co-drafted with this ticket), default to the prompt skeleton in this handoff's §"VLM prompt" below and raise an ADR-0044 amendment in the same PR.
- **`docs/decisions/0045-composite-note-schema.md`** — §6 `attachments.extraction_cache_key`, `attachments.extra`, `attachments.parent_attachment_id`. The URL→binary re-route INSERTs a child `attachments` row with `parent_attachment_id` set to the original URL attachment's id; the soft-delete CASCADE handles cleanup if the parent is later tombstoned.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — the lease/retry pattern. **Not directly touched by this ticket** — the base class owns it — but the VLM adapter must surface exceptions that the base class can interpret as "retry" vs "give up." `HttpRequestException` and `TaskCanceledException` from the Ollama call retry; `OllamaJsonParseException` (a new typed exception this ticket adds) does NOT retry (a deterministic parse failure won't get better on retry).
- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers + WireMock + Respawn. WireMock.Net is already a package version pin (`Directory.Packages.props:64`); reuse it for both the fake Ollama and the fake HTTP origin.
- **`plans/cloud-schema-v2-handoff.md`** — schema this ticket reads/writes. `attachments.extraction_cache_key` is non-null on success path; null is OK on `skipped` (pre-flight reject) and `failed` (real error).
- **`plans/cloud-sidecars-handoff.md`** — the running Ollama sidecar this ticket calls. `appsettings.json:IngestSaga:Sidecars:Ollama:BaseUrl` (`http://ollama:11434` in prod, `http://localhost:11434` in dev) is the endpoint; `appsettings.json:IngestSaga:Models:Vlm:OllamaTag` (`openbmb/minicpm-v4.6:q4_K_M`) is the model.
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — the framework this ticket plugs into. The `SpecialistWorkerBase<TClient>` shape, the `IVlmClient` + `IUrlFetcherClient` interfaces, and the `ExtractingAttachmentsHandler` cache-lookup logic are all already in place; this ticket reads them but does not modify them.
- **`src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/UrlExtractor.cs`** — the existing AngleSharp + SmartReader + ReverseMarkdown extractor from CLOUD-002 Pass B. **Reuse as-is.** The new `RealUrlFetcherClient` wraps this extractor and adds the HEAD-and-reroute layer; the extractor's MaxResponseBytes / Timeout / TruncationSentinel constants stay where they are.
- **`src/ThanyMarcus.Cloud.Api/Infrastructure/Storage/IArtifactStore.cs`** — used to mint presigned GET URLs that the Ollama sidecar fetches the image from. The store's `GetPresignedDownloadUrlAsync(string bucket, string key, TimeSpan ttl, CancellationToken ct)` method (added in CLOUD-002) returns the URL Ollama gets in the `images:` array.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. **Preserved.** The URL→binary re-route INSERTs a sibling attachment under the *same* note (same `note_id`), not a new note. The parent URL attachment still completes its own extraction_task; the child binary attachment runs in parallel as a separate extraction_task and gets its own row.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3. Same conventions; nothing project-specific.

**Do not litigate ADR-0043 §"VlmWorker" or §"URL → binary re-route" or §"Sidecar binary access — presigned GET URLs, not multipart".** If a model output field shape feels wrong, raise it as an ADR-0044 amendment in this PR's description; do not improvise.

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **The two adapters share `SpecialistWorkerBase<TClient>` via the workers they back, not via their own inheritance.** `RealUrlFetcherClient` and `OllamaVlmClient` both implement plain interfaces (`IUrlFetcherClient`, `IVlmClient`) and have no base class between them. The "shared base" is the `UrlFetcherWorker : SpecialistWorkerBase<IUrlFetcherClient>` and `VlmWorker : SpecialistWorkerBase<IVlmClient>` workers from handoff #3 — they share the claim loop, the heartbeat, the retry, and the lease via the base. The clients themselves are siblings. **Do not invent a `LightProcessorClientBase`** — there is no shared logic between fetching HTML and calling Ollama beyond what `IHttpClientFactory` and the `extraction_cache_key` builder already provide.

2. **`extraction_cache_key` is computed in the specialist worker, not in the client.** The worker decides "should I bother calling the client?" by computing the key (`sha256:<target_sidecar>:<model>:<version>`) and checking the database for an existing `attachments` row with same `sha256` AND same `extraction_cache_key` AND non-null `extracted_text`. Cache-hit path copies `extracted_text` into the current row and marks the extraction_task `succeeded` without calling the client. **The handoff #3 `ExtractingAttachmentsHandler` already does this check on INSERT** — but the same cross-note cache window can open between INSERT and worker-claim (another note's task succeeded between the two). The worker re-checks; rare-but-possible double work is acceptable, double *storage* is not (the cache key is unique per `(sha256, cache_key)` pair and the worker writes only on cache miss). **Concretely:** the cache-lookup helper lives at `Features/Processing/Specialists/AttachmentExtractionCache.cs` (new file in this ticket), injected into both `UrlFetcherWorker` and `VlmWorker` (but **also** later into the other three specialist workers when handoffs #5/#6 land — extract it now, reuse later).

3. **URL HEAD-and-reroute INSERTs a sibling attachment, then succeeds the URL task with `extra.redirected_to = <child_attachment_id>`.** The parent URL attachment's `extracted_text` is left empty; `extraction_status='skipped'`; `extraction_error='redirected_to_binary'`. The plugin's vault renderer, on seeing `redirected_to` in `extra`, omits the URL-as-link rendering for that attachment slot and renders the child binary attachment in its place. **Do not delete the URL attachment row** — it preserves the audit trail of "user pasted this URL; we re-routed to this binary." Reprocess on the parent note re-fetches the URL HEAD and either confirms the cache hit (same content-type → same child) or creates a new child (origin returned a different content-type) and tombstones the old child via the standard tombstone path.

4. **URL HEAD is the *first* call**, not after a GET. The HEAD call is cheap (~50–200 ms) and gates the expensive GET. If HEAD returns a non-text/html content-type, the GET never runs; the binary URL is passed straight to the rerouted sibling's `Url` column and the child sidecar fetches it via the existing presigned-URL path (sidecar fetches the URL directly — *not* through our `IArtifactStore`, because the binary lives on the public internet, not Spaces). **Edge case:** servers that return 405 Method Not Allowed for HEAD. Fallback: a `GET` with `Range: bytes=0-0` and inspect Content-Type. If even that fails, fall through to a full GET and inspect the response's Content-Type header before downloading the body; abort the download if it's binary by closing the stream early. **Limit:** at most one fallback attempt per HEAD failure; if both fail, the URL task fails (`extraction_error='cannot_determine_content_type'`).

5. **Binary URL "sidecar fetches directly from public internet"** is the asymmetric path versus user-uploaded binaries (which sit in Spaces and get a presigned GET URL). The Ollama `images:` array accepts arbitrary HTTPS URLs — Ollama fetches them itself. **Trade-off:** the Spaces presigned URL is short-lived (5 min) and credential-scoped to the user's bucket; an arbitrary internet URL is neither. If a user pastes a public image URL, the sidecar makes an outbound HTTPS request to that origin from the user's droplet, exposing the droplet's IP to the origin. **This is acceptable** because (a) the user just *asked* us to process that URL, so the origin already knows they're interested; (b) the droplet has a public IP anyway (it's a cloud-init droplet); (c) the alternative — proxy through cloud-api to download then re-upload to Spaces — adds storage + bandwidth + a binary-bytes path through cloud-api, which we explicitly avoided per ADR-0042 §"Sidecar binary access". Document the trade in the PR description; do not gate behind a feature flag in MVP.

6. **VLM pre-flight order matters: dimension check FIRST (cheapest), then EXIF, then pHash, then blur.** Dimension comes from the image header (read the first ~few KB only), no full decode. EXIF is also header-only. pHash requires a full decode + downsample, so do it after the cheaper checks. Laplacian blur is the most expensive (full decode + edge detection); it's optional and gated by `appsettings.json:IngestSaga:Filters:Image:EnableBlurCheck` (default `false` — enable opt-in after eval calibrates the threshold). Pre-flight bails early on any skip condition so the next check doesn't run.

7. **Image dimension lower bound = 100 px, upper bound = 16384 px** (per ADR-0043 §"Classical pre-flight per kind"). Tunable via `appsettings.json:IngestSaga:Filters:Image:MinDimension` and `MaxDimension`. Out-of-bounds → `extraction_status='skipped'`, `extraction_error='dimensions_out_of_range:<w>x<h>'`. Aspect-ratio is not gated (a 100x10000 panorama still goes through).

8. **EXIF stripping after read.** We *read* EXIF for the `extra` blob (`{ camera_make, camera_model, taken_at, orientation, gps_lat, gps_lng }` — selectively stored; never include the full EXIF blob), but the image we send to Ollama via presigned URL is the *original* bytes (we don't strip EXIF before sending). The plugin / vault rendering side handles redaction if the user opts in (post-thesis feature). **Privacy note:** GPS coordinates ARE included in `extra` if present. This is the user's own image on the user's own cloud; it's their data. Document in the user-facing settings copy when the LLM mode UI lands; not gated in code here.

9. **pHash is 64-bit DCT pHash** (8x8 luminance DCT, median threshold). Library: `ImageHash` NuGet (`CoenM.ImageHash`, MIT, ~2 KB pulled in transitive). Stored in `extra.phash` as a 16-character hex string. **Not** used in this ticket for dedup logic — the field is populated for future-work cross-image deduplication (a future ticket compares pHashes across the cache for near-duplicate skip). Populating the field now is cheap (~10 ms per image) and avoids a schema-style migration later.

10. **Image decode library = `ImageSharp` (SixLabors.ImageSharp).** Apache 2.0 in 2026 (re-licensed from CSGo trade in 2023). Already a transitive dependency via SmartReader? **Verify at apply time** by running `dotnet list package --include-transitive | grep -i imagesharp`. If absent, add `SixLabors.ImageSharp` to `Directory.Packages.props` (`Version="3.*"`). ImageSharp handles JPEG/PNG/WebP/AVIF/HEIC reading + EXIF + downsample-for-pHash in one library. **Note:** ImageSharp v3.x is Apache-2.0 but Six Labors Studios offers a paid plus tier; we use only the OSS API surface — confirm by linking only `SixLabors.ImageSharp` and not `SixLabors.ImageSharp.Drawing.Plus`.

11. **Laplacian blur uses ImageSharp's edge-detection filter on a grayscale-downsampled copy** (256 px on long edge). Output: variance of Laplacian. Threshold: `appsettings.json:IngestSaga:Filters:Image:LaplacianVarianceThreshold` (default 50; below = blurry). Opt-in behind `EnableBlurCheck`. **Wall-clock cost:** ~50–100 ms per image at 256 px. If `EnableBlurCheck=true` on every image, that's a steady-state 5–10% throughput tax on `VlmWorker`. Default off; turn on after eval shows what the threshold should be on the user's actual capture stream.

12. **Ollama call uses `/api/generate` with `stream:false`.** Not `/api/chat` — the VLM prompt is a single-turn instruction, no chat history needed. `stream:false` simplifies the JSON parse (one response payload, not a stream of chunks); the small latency cost (~50–200 ms of TTFT savings forgone) is fine because we cannot show partial VLM output to the user anyway — the result is two fields (`description`, `text_in_image`) that go straight into `attachments.extracted_text`. **`format:"json"` is set** to force the model to emit a JSON object; Ollama supports this for vision models by passing the flag in the request body. If the model emits non-JSON despite the flag (rare but observed in MiniCPM-V on poorly-lit inputs), the parser raises `OllamaJsonParseException` and the `extraction_task` retries up to `IngestSaga:ExtractionTasks:MaxAttempts` (default 3 from handoff #3); after exhaustion, the task `failed` and the orchestrator advances per §"best-effort cascade." **Do not** add a JSON-repair pass (BSON-style recovery, balanced-brace counting, etc.) — accept the failure and move on; if eval shows this happening frequently, escalate to a model-prompt tweak in ADR-0044.

13. **The Ollama HttpClient is a typed `IHttpClientFactory` client** named `"OllamaVlm"` with timeout = `appsettings.json:IngestSaga:Sidecars:Ollama:RequestTimeoutSeconds` (default 90s — accommodates worst-case ~45s inference + network jitter). Polly is **not** added on top — the specialist worker base class owns retry. The HttpClient's only role is "make one HTTP request, return the body or throw."

14. **JSON parse uses `System.Text.Json` with a typed record (`OllamaGenerateResponse`).** No `JsonNode`/`JsonDocument` dynamic navigation. The inner `response` field of Ollama's payload is a *string* containing the JSON the model emitted; parse it a second time into `VlmOutput { string Description, string? TextInImage }`. Both nulls are tolerated on `TextInImage` (image had no readable text); a null `Description` is an error (the prompt explicitly asks for one) and triggers a retry. Strict `JsonSerializerOptions { PropertyNamingPolicy = SnakeCaseLower }` to match the prompt's instructed key names.

15. **`extracted_text` canonical form:**
    ```
    Description:
    <model description, trimmed>

    Text:
    <model text_in_image, trimmed>
    ```
    With the `Text:` block omitted entirely if `TextInImage` is null/whitespace. This format is what `CompositeMarkdownAssembler` will read when it builds the note body (assembler logic in CLOUD-002, unchanged here). Do not invent a different separator (no `---` rule, no markdown heading) — the assembler does the framing.

16. **`extra` blob shape (JsonDocument, schema-free but conventional):**
    ```json
    {
      "exif": { "camera_make": "...", "camera_model": "...", "taken_at": "...", "orientation": 1, "gps_lat": null, "gps_lng": null },
      "phash": "a1b2c3d4e5f6a7b8",
      "dimensions": { "width": 1920, "height": 1080 },
      "blur_score": null,
      "model_response": { "...full Ollama JSON, including response, done_reason, eval_count, eval_duration_ns..." }
    }
    ```
    The full `model_response` is included for forensic value (verifying inference latency, eval counts, etc.) and is bounded (~1–4 KB per image). Do not strip it. **Do** strip the `context` field if Ollama returns it (token-id arrays can be 50+ KB; we never need them — we always run single-turn).

17. **URL extractor `extra` blob shape:**
    ```json
    {
      "final_url": "https://...",
      "title": "...",
      "byline": "...",
      "excerpt": "first ~200 chars of body",
      "published_at": "...",
      "lang": "en",
      "http_status": 200,
      "truncated": false,
      "redirected_to": null
    }
    ```
    Fields beyond what `UrlExtractor.UrlExtractionResult` exposes today (byline, excerpt, published_at, lang) come from SmartReader's `Article` object — already extracted, just not propagated into the result record. **This ticket extends `UrlExtractionResult`** to include them and updates the one caller (the new `RealUrlFetcherClient`); the existing `CompositeIngestHandler` from CLOUD-002 was already deleted by handoff #3, so no other call site exists. **Set `redirected_to` to the child attachment id on binary re-route**; null otherwise.

18. **No mutation of the URL extractor.** The existing `UrlExtractor` class stays; the new client wraps it. If the extractor needs new fields (byline, excerpt, etc.), they go on `UrlExtractionResult` as additional record properties (records are pure data; adding fields is non-breaking). The extractor's `ExtractAsync` method signature and behavior stays byte-for-byte.

19. **Stub clients stay in the codebase.** `StubUrlFetcherClient.cs` and `StubVlmClient.cs` (from handoff #3) are **kept** at `Infrastructure/Sidecars/Stubs/`. They become test-only doubles registered via `CloudApiFactory`'s `services.Replace(...)` pattern in `CompositeIngestSagaEndToEndTests` (the Pass-C regression anchor) — the anchor continues to assert against canned stub output. The new "real" tests register the real clients and stub the *sidecar* (WireMock) and the *HTTP origin* (also WireMock). Two independent test paths; one stable regression anchor, one new integration check.

20. **Failure surface for the orchestrator:** `extraction_status='failed'` on the attachment, `extraction_error=<short reason>`, `extraction_tasks.status='failed'` after retries exhaust. The orchestrator's `ExtractingAttachmentsHandler` ("all terminal?" check) treats `failed` as terminal — it does NOT bubble up to fail the parent ingest_job. The note still composes (with the failed attachment's slot rendered as "Image (extraction failed: <reason>)" by the assembler), still routes, still embeds, still ends `succeeded`. **The user-visible signal is the failed attachment block in the rendered Markdown plus `provenance.extraction_failures[]`.**

## Scope boundary (precise)

One pass. Two new client classes + one shared cache-lookup helper + small extractor record extension + tests + smoke. ~1.5–2 days because the WireMock fixture for Ollama needs careful payload setup (the multimodal generate request) and the URL-binary-reroute path needs three distinct test cases (HEAD-200-html, HEAD-200-image, HEAD-405-fallback-GET).

### In scope

1. **`RealUrlFetcherClient` (`src/ThanyMarcus.Cloud.Api/Infrastructure/Sidecars/RealUrlFetcherClient.cs`).**
   - Implements `IUrlFetcherClient` (interface lives in `Infrastructure/Sidecars/IUrlFetcherClient.cs`, from handoff #3). The interface signature `Task<string> FetchMarkdownAsync(string url, CancellationToken ct)` does **not** cover the binary-reroute path because the reroute writes to *other tables* (inserts a new `attachments` row + `extraction_task`). **Extend the interface** in this ticket:
     ```csharp
     public interface IUrlFetcherClient
     {
         Task<UrlFetchOutcome> FetchAsync(Guid noteId, Attachment att, CancellationToken ct);
     }

     public sealed record UrlFetchOutcome(
         string?  ExtractedText,         // null if redirected
         string?  ExtractionCacheKey,    // null if redirected (cache key applied on child instead)
         JsonDocument Extra,             // always populated
         Guid?    RedirectedToAttachmentId);  // non-null on binary-reroute
     ```
     The `UrlFetcherWorker` (from handoff #3) is updated to use the new signature. **This is a breaking change to the `IUrlFetcherClient` shape** — but only one caller exists (`UrlFetcherWorker`) and one impl (`StubUrlFetcherClient`); update both in this ticket. The change is justified because the redirect path is fundamentally not "return markdown" — it's "INSERT a sibling and tell me the id."
   - Workflow:
     1. Validate URL (`Uri.TryCreate` with `UriKind.Absolute`, scheme http/https only); throw on invalid.
     2. HEAD the URL (timeout = `IngestSaga:Sidecars:Url:HeadTimeoutSeconds`, default 5s).
     3. Inspect `Content-Type`:
        - `text/html`, `application/xhtml+xml`, `text/plain`, missing → proceed to GET + SmartReader path.
        - `image/*`, `audio/*`, `video/*`, `application/pdf`, `application/zip` (for DOCX/XLSX/PPTX) → reroute path.
        - Other → error: `extraction_error='unsupported_content_type:<mime>'`, throw `UrlFetcherException`.
     4. On HEAD 405 / Method Not Allowed: fall back to `GET` with `Range: bytes=0-0`. Same content-type inspection. If even that fails (no `Content-Type` header, HTTP error), throw with `cannot_determine_content_type`.
     5. **HTML path:** call existing `UrlExtractor.ExtractAsync(url, ct)`. Wrap result in `UrlFetchOutcome(ExtractedText: result.Markdown, ExtractionCacheKey: null, Extra: BuildExtraFromExtractor(result), RedirectedToAttachmentId: null)`. **Note:** the URL extractor's output is content-derived (Markdown), not model-derived — no `extraction_cache_key` because there's no model to invalidate against. The cache-lookup helper SKIPS URL attachments (they re-extract on reprocess, which is fast + idempotent).
     6. **Reroute path:** map content-type to new attachment kind via `BinaryRerouteMap.Resolve(mimeType)` (new static class):
        ```
        image/*               → AttachmentKind.Image,   target_sidecar='ollama'
        audio/*               → AttachmentKind.Voice,   target_sidecar='parakeet'
        video/*               → AttachmentKind.Video,   target_sidecar='video'
        application/pdf       → AttachmentKind.File,    target_sidecar='docling' (mime_type populated)
        application/{zip,..ms-office,..opendocument,..}→ AttachmentKind.File,    target_sidecar='docling'
        other                 → throw UnsupportedRerouteException
        ```
        (Note: `AttachmentKind` only knows `Url|Image|Voice|File` per `Features/Ingest/Attachment.cs:38`. Documents are `File` with mime_type populated; Docling's target_sidecar is selected by `ExtractingAttachmentsHandler`'s existing dispatch on mime — verify this works after the schema-v2 vocabulary extension; if not, extend `AttachmentKind` in a small companion change. **DO NOT extend `AttachmentKind` if it requires a schema migration** — the kinds are stored as text; no DB change needed beyond updating the `IsValid` whitelist.)
     7. INSERT the new `attachments` row: same `note_id`, `parent_attachment_id = <original URL att.id>`, `kind = <new>`, `mime_type = <from HEAD>`, `storage_provider = "external"` (special sentinel), `storage_bucket = ""`, `storage_key = ""`, `url = <original URL>`, `status = 'uploaded'` (the binary is "already uploaded" by virtue of living on the public internet — no plugin upload step), `extraction_status = 'pending'`, `extra = JsonDocument.Parse("{}")`, `created_at = now`.
     8. INSERT the matching `extraction_tasks` row: `ingest_job_id = <current job id>` (carry forward), `attachment_id = <new att.id>`, `target_sidecar = <from map>`, `status = 'queued'`, `scheduled_at = now`, `attempts = 0`.
     9. `pg_notify('extraction_tasks_<sidecar>_new', '<ingest_job_id>')` so the corresponding specialist worker wakes immediately.
     10. Return `UrlFetchOutcome(ExtractedText: null, ExtractionCacheKey: null, Extra: BuildRerouteExtra(headResponse, childId), RedirectedToAttachmentId: childId)`.
   - The worker (`UrlFetcherWorker`) on receiving an outcome with `RedirectedToAttachmentId != null` writes the parent URL attachment as `extraction_status='skipped'`, `extraction_error='redirected_to_binary'`, `extracted_text=null`, `extra=<from outcome>`, `extraction_cache_key=null`. The base class transitions the task to `succeeded`.

2. **`OllamaVlmClient` (`src/ThanyMarcus.Cloud.Api/Infrastructure/Sidecars/OllamaVlmClient.cs`).**
   - Implements `IVlmClient`. Extend the interface in this ticket (same justification as URL above — single caller, single stub):
     ```csharp
     public interface IVlmClient
     {
         Task<VlmExtractionOutcome> ExtractAsync(Attachment att, CancellationToken ct);
     }

     public sealed record VlmExtractionOutcome(
         string ExtractedText,           // "Description:\n...\n\nText:\n..."
         string ExtractionCacheKey,      // "sha256:ollama:minicpm-v-4.6:Q4_K_M"
         JsonDocument Extra,
         bool Skipped,                   // pre-flight reject path
         string? SkipReason);
     ```
   - Workflow:
     1. **Pre-flight** (all stages bail early on first skip):
        - Mint a presigned GET URL for the attachment from `IArtifactStore` (5-min TTL — the pre-flight steps + Ollama call MUST complete inside that window; on the 16 GB droplet they will, with ~3 min slack).
        - Open the image via `Image.Load` from `SixLabors.ImageSharp` with `ReadFromUrlAsync` (or fetch with HttpClient + load from stream — ImageSharp's URL helper is async).
        - **Dimension check:** `image.Width`, `image.Height`. Out of `[MinDimension, MaxDimension]` → return `Skipped=true`, `SkipReason=$"dimensions_out_of_range:{w}x{h}"`. Both `extracted_text` and `cache_key` empty/null but the outcome's `Extra` carries the dimensions for diagnostics.
        - **EXIF read:** `image.Metadata.ExifProfile?.Values` — extract whitelisted tags (camera_make, camera_model, taken_at, orientation, gps_lat, gps_lng) into `extra.exif`. Never crashes; missing EXIF → empty object.
        - **pHash compute:** `new PerceptualHash().Hash(image)` (CoenM.ImageHash). Stored as hex string in `extra.phash`.
        - **Blur check (opt-in):** if `EnableBlurCheck`, downsample to 256 px long edge, apply Laplacian filter, compute variance. Below `LaplacianVarianceThreshold` → return `Skipped=true`, `SkipReason=$"too_blurry:variance={var:F1}"`.
     2. **Build prompt** via `VlmPromptBuilder.Build()` (new static helper, ~20 LOC) per §"VLM prompt" below.
     3. **Call Ollama** `/api/generate` with body:
        ```json
        {
          "model": "openbmb/minicpm-v4.6:q4_K_M",
          "prompt": "<from PromptBuilder>",
          "images": ["<presigned URL>"],
          "format": "json",
          "stream": false,
          "options": { "num_ctx": 4096, "temperature": 0.2 }
        }
        ```
        Via the typed HttpClient (named `"OllamaVlm"`). Deserialize the outer response into `record OllamaGenerateResponse(string model, string response, bool done, string done_reason, long eval_count, long eval_duration)`.
     4. **Parse `response` as JSON** (second parse) into `VlmOutput(string description, string? text_in_image)`. On parse failure throw `OllamaJsonParseException(rawResponse)`. On `description == null` throw `OllamaJsonParseException(...)` (treated as parse failure — same retry semantics).
     5. **Compose canonical extracted_text:**
        ```csharp
        var text = $"Description:\n{output.Description.Trim()}";
        if (!string.IsNullOrWhiteSpace(output.TextInImage))
            text += $"\n\nText:\n{output.TextInImage.Trim()}";
        ```
     6. **Build extra JSON:** EXIF + pHash + dimensions + (optional) blur_score + full `OllamaGenerateResponse` (minus `context`).
     7. **Cache key:** `sha256:ollama:minicpm-v-4.6:Q4_K_M` (constant per model — the tag is in `appsettings.json:IngestSaga:Models:Vlm:OllamaTag`, and the cache key is derived from that, snake_cased). Format: `"sha256:ollama:" + sanitize(modelTag)`. Helper: `ExtractionCacheKeys.ForOllama(modelTag)` (new static class — siblings for `ForDocling`, `ForParakeet` come in handoff #5).
     8. Return `VlmExtractionOutcome(extractedText, cacheKey, extra, Skipped: false, SkipReason: null)`.
   - The worker (`VlmWorker`) writes `extracted_text`, `extraction_cache_key`, `extra` on the attachment; on `Skipped=true` writes `extraction_status='skipped'` + `extraction_error=skipReason` + leaves `extracted_text=null` + `extraction_cache_key=null`. Base class transitions the task to `succeeded` either way (skipped is a clean terminal).

3. **`AttachmentExtractionCache` (`src/ThanyMarcus.Cloud.Api/Features/Processing/Specialists/AttachmentExtractionCache.cs`).**
   - Single class, single method:
     ```csharp
     public sealed class AttachmentExtractionCache(CloudDbContext db)
     {
         public async Task<CachedExtraction?> LookupAsync(string sha256, string cacheKey, CancellationToken ct)
         {
             return await db.Attachments
                 .Where(a => a.Sha256 == sha256
                          && a.ExtractionCacheKey == cacheKey
                          && a.ExtractedText != null
                          && a.ExtractionStatus == AttachmentExtractionStatus.Extracted)
                 .OrderByDescending(a => a.UpdatedAt)
                 .Select(a => new CachedExtraction(a.ExtractedText!, a.Extra))
                 .FirstOrDefaultAsync(ct);
         }
     }
     public sealed record CachedExtraction(string ExtractedText, JsonDocument Extra);
     ```
   - Used by both `VlmWorker` and `UrlFetcherWorker` before invoking the client. Cache hit → write the same `extracted_text` + a derivative `extra` (mark `extra.from_cache=true`, preserve other fields) + same `extraction_cache_key` onto the current attachment; mark `extraction_status='extracted'`. **Do not** mark `extraction_status='skipped'` — that's reserved for pre-flight rejections; cache hit is a clean `extracted`.
   - **Cache check is post-claim, pre-client-call.** The base class claim loop already happened; the lookup is one DB query before the expensive client call. Adds ~5 ms to the warm path; saves ~5–15 s on hit. **Note** that the orchestrator's `ExtractingAttachmentsHandler` also checks the cache at INSERT time (per handoff #3); the worker-side check catches the race where another note's attachment with same sha256+cache_key finished between INSERT and claim. Belt-and-suspenders, but cheap.

4. **`UrlExtractionResult` record extension** (`src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/IUrlExtractor.cs:8`).
   - Add `Byline`, `Excerpt`, `PublishedAt`, `Lang` fields:
     ```csharp
     public sealed record UrlExtractionResult(
         string Markdown,
         string? CanonicalUrl,
         string? Title,
         string? Byline,
         string? Excerpt,
         string? PublishedAt,
         string? Lang,
         int HttpStatus,
         bool Truncated);
     ```
   - Update `UrlExtractor.ExtractAsync` (`src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/UrlExtractor.cs:80–86`) to populate the new fields from `article.Byline`, `article.Excerpt`, `article.PublicationDate?.ToString("O")`, `article.Language`. The SmartReader `Article` already exposes them.
   - **One caller** (the new `RealUrlFetcherClient`); no other call site updates needed (handoff #3 already deleted `CompositeIngestHandler` which used the old result shape).

5. **`appsettings.json` additions** (`src/ThanyMarcus.Cloud.Api/appsettings.json`):
   ```json
   "IngestSaga": {
     "Sidecars": {
       "Ollama": {
         "BaseUrl": "http://localhost:11434",
         "HealthPath": "/api/version",
         "MaxConcurrency": 1,
         "RequestTimeoutSeconds": 90
       },
       "Url": {
         "HeadTimeoutSeconds": 5,
         "GetTimeoutSeconds": 10
       },
       ...
     },
     "Models": {
       "Vlm":  { "OllamaTag": "openbmb/minicpm-v4.6:q4_K_M", "NumCtx": 4096, "Temperature": 0.2 },
       ...
     },
     "Filters": {
       "Image": {
         "MinDimension": 100,
         "MaxDimension": 16384,
         "EnableBlurCheck": false,
         "LaplacianVarianceThreshold": 50
       }
     }
   }
   ```

6. **DI swaps in `Program.cs`** (single-line each):
   - `services.AddSingleton<IUrlFetcherClient, RealUrlFetcherClient>()` replaces `StubUrlFetcherClient`.
   - `services.AddSingleton<IVlmClient, OllamaVlmClient>()` replaces `StubVlmClient`.
   - `services.AddHttpClient<RealUrlFetcherClient>(...)` for the URL-fetcher HttpClient (configures HEAD/GET timeouts, User-Agent).
   - `services.AddHttpClient("OllamaVlm", ...)` for the Ollama HttpClient (configures BaseAddress, RequestTimeout).
   - `services.AddScoped<AttachmentExtractionCache>()`.

7. **Tests** under `tests/ThanyMarcus.Cloud.Tests/`:
   - `Infrastructure/Sidecars/OllamaVlmClientTests.cs`:
     - Happy path: stand-in Ollama (WireMock) returns canonical `{response:"{\"description\":\"...\",\"text_in_image\":\"...\"}"}`; client returns `VlmExtractionOutcome` with composed text + cache key.
     - JSON parse failure: stand-in Ollama returns non-JSON in `response`; client throws `OllamaJsonParseException`.
     - HTTP failure: stand-in Ollama returns 503; client throws `HttpRequestException` (retried by base class).
     - Pre-flight dimension reject: 50x50 image → `Skipped=true`, `SkipReason="dimensions_out_of_range:50x50"`, Ollama never called.
     - Pre-flight blur reject (opt-in): blurry 256x256 image + `EnableBlurCheck=true` + low threshold → `Skipped=true`, `SkipReason="too_blurry:..."`, Ollama never called.
     - EXIF + pHash populated: image with embedded EXIF → `outcome.Extra` has matching `exif.camera_make` and a non-empty `phash`.
     - Cache key format: `outcome.ExtractionCacheKey == "sha256:ollama:openbmb-minicpm-v4.6-q4_K_M"` (or whatever sanitize-rule produces).
   - `Infrastructure/Sidecars/RealUrlFetcherClientTests.cs`:
     - Happy path (HTML): stand-in origin (WireMock) returns HEAD 200 + `text/html` then GET 200 + HTML body; client returns `UrlFetchOutcome` with markdown.
     - Reroute (image): stand-in origin returns HEAD 200 + `image/jpeg`; client INSERTs a new `attachments` row + `extraction_tasks` row; returns `UrlFetchOutcome { RedirectedToAttachmentId=<new>, ExtractedText=null, ExtractionCacheKey=null }`; `pg_notify('extraction_tasks_ollama_new', ...)` observed (via a LISTEN-side test helper or directly via `pg_notification_queue_usage()` query).
     - Reroute (PDF): HEAD returns `application/pdf`; INSERTs `kind=AttachmentKind.File`, `target_sidecar='docling'`, `url=<original>`.
     - HEAD 405 fallback: stand-in origin returns 405 on HEAD then 200 + `image/png` on `GET Range: bytes=0-0`; client correctly reroutes.
     - HEAD unknown mime: stand-in returns `application/octet-stream`; client throws `UnsupportedRerouteException` → caller marks attachment failed (via base class retry-exhaust path).
     - SmartReader unreadable: HEAD 200 + `text/html` + body of just `<html><body></body></html>`; client throws (matches existing `UrlExtractor` behavior of "could not extract readable content"); base class retries up to MaxAttempts.
   - `Features/Processing/Specialists/AttachmentExtractionCacheTests.cs`:
     - Single insert of an attachment with matching `(sha256, cache_key)` → lookup returns that row.
     - Two attachments with same sha256 but different cache_key (model bump) → lookup with newer key returns null until that key is also populated.
     - Attachment with `extraction_status='failed'` → lookup ignores it.
   - `Infrastructure/Sidecars/VlmPromptBuilderTests.cs`:
     - Builder produces the deterministic prompt string (anchor against the §"VLM prompt" reference); test exists so any future prompt tweak deliberately updates this test.
   - **CompositeIngestSagaEndToEndTests update:** the Pass-C regression anchor from handoff #3 stays unchanged in structure; the test registers `StubVlmClient` and `StubUrlFetcherClient` via test override (`CloudApiFactory.OverrideServices(s => { s.Replace(...); })`). Add a sibling test `CompositeIngestSagaEndToEndTests_RealLightProcessors.cs` that does NOT override — it stands up WireMock for Ollama + the URL origin and runs the full 4-attachment flow against real adapters. Assertions: `extracted_text` contains "Description:" prefix on the image attachment; `extracted_text` is real ReverseMarkdown on the HTML URL attachment; `extra.phash` is a 16-char hex string; `extraction_cache_key` matches `sha256:ollama:...` on the image; the second run after `/reprocess` cache-hits on both image and URL (URL re-fetches; image hits cache because sha256 + cache_key match).

### Out of scope (named so they don't sneak in)

- ❌ **Real `DoclingHttpClient`, `ParakeetHttpClient`, `RealEmbeddingClient`, `FfmpegVideoSplitterClient`.** Handoffs #5–#6.
- ❌ **Real `OllamaLlmClient` for routing + entity extraction.** Even though we wire `IHttpClientFactory` against `OllamaVlm` here, the routing/entity-extraction LLM is a separate adapter (`Infrastructure/Llm/OllamaLlmClient.cs`) with a different prompt, different model (or same model in text mode — TBD by ADR-0044), and lands in CLOUD-LLM-INTELLIGENCE. It MAY share `IHttpClientFactory` config — extract the Ollama client name (`"OllamaVlm"`) to a constant so the LLM ticket can decide to reuse or create a sibling (`"OllamaLlm"`) without grepping. **Choice:** add `OllamaClientNames.Vlm = "OllamaVlm"` constant in `Infrastructure/Sidecars/OllamaClientNames.cs` (placeholder for `Llm` and any future siblings).
- ❌ **Schema changes.** `attachments`, `extraction_tasks` already carry every column this ticket writes to. No migration.
- ❌ **Endpoint changes.** `/api/ingest/*`, `/api/sync/*`, `/api/notes/*` shapes are byte-for-byte the same.
- ❌ **Plugin TS types.** Same as handoff #3 — plugin not yet shipped; contracts internal.
- ❌ **SSE event additions.** The closed event vocabulary (`note_phase_changed | attachment_status_changed | note_succeeded | note_failed | hub_materialized`) from handoff #3 is unchanged. Reroute events surface as `attachment_status_changed(pending → skipped)` for the parent URL attachment and `attachment_status_changed(pending → extracted)` for the child, in sequence — no new event kind needed.
- ❌ **Mid-extraction cancellation.** Per ADR-0042 / handoff #3. A 15s VLM call cannot be cut mid-stream.
- ❌ **Cross-image pHash dedup.** The `extra.phash` field is populated but never queried for "is this a near-duplicate of another image?" Reserved for future-work ticket.
- ❌ **EXIF stripping on uploaded artifacts.** We read EXIF, store selected fields in `extra`, but do not modify the binary stored in Spaces. GPS handling is settings-driven; not in scope here.
- ❌ **JS-rendered URL fallback** (Playwright sidecar). Per ADR-0043 deferred future-work item 3. If SmartReader returns empty body on a JS-heavy page, the URL task fails; user gets a "extraction failed" attachment block. Future ticket adds the headless-browser path.
- ❌ **`OllamaLlmClient` for entity extraction in `ExtractingEntitiesHandler`.** Stub `NoOpLlmClient` (from handoff #3) is still wired; routing returns null project; entities array is empty. Real LLM ticket is the next one.
- ❌ **Real video-routing target_sidecar.** Handoff #6. Until then, a video URL reroute would INSERT a `target_sidecar='video'` task that no specialist worker claims (it just sits in `queued`). **Mitigation in this ticket:** if `BinaryRerouteMap.Resolve` returns `target_sidecar='video'` AND `appsettings.json:IngestSaga:Specialists:Video:Enabled=false` (default true; flip false until handoff #6), the URL fetcher fails with `extraction_error='video_reroute_disabled'` instead of orphaning a task. Reset to true when handoff #6 lands.
- ❌ **OTel spans inside the new clients.** Same as handoff #3; follow-up. The base class already wraps the client call in a span; adding sub-spans (HEAD vs GET, pre-flight stage vs Ollama call) is a follow-up.
- ❌ **Prompt versioning / A/B prompt eval framework.** Out of scope. The prompt is a constant string; changing it bumps the cache key (because the cache key includes the model tag, not the prompt — so a prompt change without a model bump leaks stale cache; **document explicitly**: prompt changes must also bump `appsettings.json:IngestSaga:Models:Vlm:OllamaTag` to a synthetic suffix like `:q4_K_M+prompt-v2` to invalidate). Future ticket: include a prompt-hash in the cache key.

## VLM prompt

The single prompt below is the contract that ADR-0044 §"VLM prompt template" should ratify. Pasted in full so a fresh reader has everything in one place:

```text
You are an image-understanding assistant. Look at the image and return a JSON object with exactly two fields:

{
  "description": "<1–3 sentence semantic description of what the image shows; include subject, context, and any notable visual elements>",
  "text_in_image": "<verbatim transcription of all readable text visible in the image, preserving line breaks. If no text is present, return null.>"
}

Rules:
- Output ONLY the JSON object. No prose before or after.
- "description" must be non-empty. If the image is too low-quality or empty, write "An image with no discernible content."
- "text_in_image" may be null OR a string. Empty string is not allowed — use null.
- Do not editorialize or speculate beyond what is visible.
- Do not include EXIF metadata, file information, or guesses about the photographer.
```

Builder helper (`Infrastructure/Sidecars/VlmPromptBuilder.cs`) returns this string verbatim. **No string interpolation** — the prompt is constant per model. If a future ticket needs per-attachment prompt variation, add an overload; do not retrofit interpolation into the existing builder.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   ├── 0043-cloud-side-model-lineup.md                                # (read-only) contract
│   ├── 0042-cloud-ingest-pipeline-architecture.md                     # (read-only) saga shape
│   ├── 0044-cloud-intelligence-layer.md                               # (read-only OR co-amended) VLM prompt
│   └── 0045-composite-note-schema.md                                  # (read-only) schema this ticket writes
├── plans/
│   └── cloud-processors-light-handoff.md                              # THIS FILE
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                                     # CHANGED: + AddHttpClient for OllamaVlm + RealUrlFetcherClient; replace 2 stub DI registrations; + AttachmentExtractionCache scoped
│   ├── appsettings.json                                               # CHANGED: + IngestSaga:Sidecars:Ollama:RequestTimeoutSeconds (90); + IngestSaga:Sidecars:Url:* (HeadTimeoutSeconds, GetTimeoutSeconds); + IngestSaga:Models:Vlm:NumCtx + Temperature; + IngestSaga:Filters:Image:* (MinDimension, MaxDimension, EnableBlurCheck, LaplacianVarianceThreshold)
│   ├── Features/
│   │   ├── Processing/
│   │   │   └── Specialists/
│   │   │       ├── AttachmentExtractionCache.cs                       # NEW: sha256+cache_key lookup helper, used by both light workers (and later siblings)
│   │   │       ├── UrlFetcherWorker.cs                                # CHANGED: consume the new IUrlFetcherClient signature; write RedirectedToAttachmentId-aware result
│   │   │       └── VlmWorker.cs                                       # CHANGED: consume the new IVlmClient signature; handle Skipped path
│   │   └── Ingest/
│   │       └── Attachment.cs                                          # UNCHANGED (kinds already there; mime_type already there)
│   └── Infrastructure/
│       ├── Extraction/
│       │   ├── IUrlExtractor.cs                                       # CHANGED: + Byline, Excerpt, PublishedAt, Lang on UrlExtractionResult
│       │   └── UrlExtractor.cs                                        # CHANGED: populate new UrlExtractionResult fields from SmartReader's Article
│       └── Sidecars/
│           ├── IUrlFetcherClient.cs                                   # CHANGED: new shape (Task<UrlFetchOutcome> FetchAsync(Guid noteId, Attachment att, CancellationToken ct)); + UrlFetchOutcome record
│           ├── IVlmClient.cs                                          # CHANGED: new shape (Task<VlmExtractionOutcome> ExtractAsync(Attachment att, CancellationToken ct)); + VlmExtractionOutcome record
│           ├── RealUrlFetcherClient.cs                                # NEW: HEAD-or-fallback-Range-GET dispatch; HTML path delegates to UrlExtractor; binary path INSERTs sibling + pg_notify
│           ├── OllamaVlmClient.cs                                     # NEW: pre-flight (dim/EXIF/pHash/blur) + Ollama /api/generate + JSON parse + cache key
│           ├── OllamaGenerateResponse.cs                              # NEW: typed record for Ollama's response shape
│           ├── VlmOutput.cs                                           # NEW: typed record { string description; string? text_in_image }
│           ├── VlmPromptBuilder.cs                                    # NEW: returns the constant prompt
│           ├── OllamaJsonParseException.cs                            # NEW: deterministic-failure exception (no retry by base class)
│           ├── UrlFetcherException.cs                                 # NEW: typed exception for URL-side failures with structured reason codes
│           ├── BinaryRerouteMap.cs                                    # NEW: static helper mapping mime → (AttachmentKind, target_sidecar)
│           ├── ExtractionCacheKeys.cs                                 # NEW: ForOllama(modelTag) → "sha256:ollama:..." (siblings come in handoff #5)
│           ├── OllamaClientNames.cs                                   # NEW: constant string "OllamaVlm" (and reserved slot for "OllamaLlm")
│           └── Stubs/
│               ├── StubUrlFetcherClient.cs                            # CHANGED: implement the new signature; returns deterministic UrlFetchOutcome(markdown, null, ...) — RedirectedToAttachmentId always null
│               └── StubVlmClient.cs                                   # CHANGED: implement the new signature; returns deterministic VlmExtractionOutcome(text, key, ...) — Skipped always false
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Features/Processing/Specialists/
    │   └── AttachmentExtractionCacheTests.cs                          # NEW
    ├── Infrastructure/Sidecars/
    │   ├── OllamaVlmClientTests.cs                                    # NEW (WireMock for Ollama; ImageSharp for synthetic test images)
    │   ├── RealUrlFetcherClientTests.cs                               # NEW (WireMock for origin; verifies HEAD/GET fallback, reroute INSERTs, pg_notify)
    │   ├── VlmPromptBuilderTests.cs                                   # NEW (golden-string anchor)
    │   ├── BinaryRerouteMapTests.cs                                   # NEW (one row per mime → kind/sidecar mapping)
    │   └── ExtractionCacheKeysTests.cs                                # NEW (verifies sanitize-rule for model-tag → cache-key segment)
    └── Features/Ingest/
        └── CompositeIngestSagaEndToEndTests_RealLightProcessors.cs    # NEW (sibling of the handoff-#3 stub regression anchor; real clients + WireMock sidecars)
```

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors. `dotnet test` green; the existing `CompositeIngestSagaEndToEndTests.cs` (stub-anchored) passes unchanged.
2. ✅ The new `CompositeIngestSagaEndToEndTests_RealLightProcessors.cs` passes: 4-attachment composite (1 HTML URL, 1 image-URL → reroute, 1 user-uploaded image, 1 user-uploaded file) → SSE event sequence → `/sync/pull` returns Markdown with real article markdown (URL), real `Description:\n... \nText:\n...` blocks (images), and the reroute parent shows as a skipped attachment with `extra.redirected_to=<child_id>` while the child shows extracted via VLM.
3. ✅ `IUrlFetcherClient` and `IVlmClient` signatures match the handoff §"Scope boundary" definitions; both implementations and both stubs compile against the new shape.
4. ✅ `RealUrlFetcherClient`:
   - HEAD-200 + `text/html` → HTML path; returns markdown.
   - HEAD-200 + `image/jpeg` → reroute path; INSERTs sibling `attachments` row with `kind='image'`, `parent_attachment_id=<orig>`, `url=<orig>`, `status='uploaded'`; INSERTs `extraction_tasks` row with `target_sidecar='ollama'`; `pg_notify('extraction_tasks_ollama_new', ...)` observed.
   - HEAD-405 → falls back to `GET Range: bytes=0-0`; content-type inspection same as HEAD path.
   - Unknown mime → throws; base class retry-exhaust path marks attachment failed.
5. ✅ `OllamaVlmClient`:
   - Pre-flight order: dimension → EXIF → pHash → blur (opt-in). Each stage exits early on skip.
   - Skip outcomes use the configured threshold names in `SkipReason` (e.g., `dimensions_out_of_range:50x50`).
   - Ollama request body matches the §"Scope boundary" §2.3 schema; `format:"json"` set.
   - Response parse: outer `OllamaGenerateResponse`, inner `VlmOutput { description, text_in_image }`. Strict typed.
   - On non-JSON inner response → `OllamaJsonParseException`. Retried by base class up to `MaxAttempts=3`.
   - Canonical `extracted_text`: `"Description:\n<...>\n\nText:\n<...>"` (Text block omitted when text_in_image null).
   - `extra` blob contains `exif`, `phash` (16-char hex), `dimensions`, `blur_score` (null when disabled), `model_response` (full Ollama JSON minus `context`).
   - `extraction_cache_key` matches `sha256:ollama:<sanitized-tag>`.
6. ✅ `AttachmentExtractionCache.LookupAsync`:
   - Returns the most-recent `extracted` attachment with matching `(sha256, cache_key)`.
   - Ignores `failed` or `skipped` rows.
   - Model-tag-bump (different cache_key) returns null until repopulated.
7. ✅ Cache-hit on reprocess: `CompositeIngestSagaEndToEndTests_RealLightProcessors` reprocesses the same composite → second VLM call count = 0 (WireMock assertion); URL refetch = 1 (URLs always re-extract, no cache key); image cache hits both for the user-uploaded image AND the URL-rerouted child (same sha256 + same cache_key).
8. ✅ DI: `IUrlFetcherClient` resolves to `RealUrlFetcherClient`; `IVlmClient` resolves to `OllamaVlmClient`. Stub registrations exist only via test overrides.
9. ✅ `appsettings.json` carries all the new keys; defaults match the §"Scope boundary" / §"Design decisions" values; `IngestSaga:Filters:Image:EnableBlurCheck=false` by default.
10. ✅ No `// TODO` markers in shipped code. `// FORK:` markers only at:
    - `OllamaClientNames.Llm` placeholder constant (forks to CLOUD-LLM-INTELLIGENCE handoff).
    - `BinaryRerouteMap`'s `application/zip` row (forks to "do we really want Docling to parse zip files?" — let docling decide; if it can't, the attachment fails and the user sees the error).
    - `IngestSaga:Specialists:Video:Enabled` gate in `BinaryRerouteMap.Resolve` (forks to handoff #6 — flip default to true when video specialist worker ships real).
11. ✅ Manual smoke against a freshly-provisioned cloud:
    ```bash
    # Init/upload/finalize a composite with a URL pointing at an image and a real uploaded image
    INIT=$(curl -sS -X POST https://<cloud>.thany.click/api/ingest/init \
      -H "Authorization: Bearer $PLUGIN_TOKEN" \
      -d '{"draftId":"...","attachments":[{"clientAttachmentId":"u","kind":"url","url":"https://upload.wikimedia.org/example.jpg"},{"clientAttachmentId":"i","kind":"image","mimeType":"image/jpeg","byteSize":...,"sha256":"..."}]}')
    # ...upload image via presigned PUT...
    curl -sS -X POST https://<cloud>.thany.click/api/ingest/$NOTEID/finalize \
      -H "Authorization: Bearer $PLUGIN_TOKEN" -d '{...}'
    # → 202

    # Stream events
    curl -N "https://<cloud>.thany.click/api/sync/events?access_token=$PLUGIN_TOKEN"
    # → event: attachment_status_changed
    #   data: {"noteId":"...","attachmentId":"<url>","from":"pending","to":"skipped"}
    #   event: attachment_status_changed
    #   data: {"noteId":"...","attachmentId":"<rerouted-child>","from":"pending","to":"extracted"}
    #   event: attachment_status_changed
    #   data: {"noteId":"...","attachmentId":"<uploaded-image>","from":"pending","to":"extracted"}
    #   ...
    #   event: note_succeeded
    #   data: {"noteId":"..."}

    # Pull and inspect
    curl -sS "https://<cloud>.thany.click/api/sync/pull?since=0&include=provenance" \
      -H "Authorization: Bearer $PLUGIN_TOKEN" | jq .
    # → body contains "Description:" + "Text:" blocks for the two images; URL parent shows the skip note;
    #   provenance.extraction_summary shows total=4, extracted=3 (URL-parent skipped intentionally), skipped=1, failed=0
    ```
12. ✅ `OllamaVlmClient` log scope is `OllamaVlmClient[{worker_id} attempt={attempt}]` and includes the model tag + eval_duration_ms on success at INFO; only the exception type at WARN on retry; full stack at ERROR on dead-letter.
13. ✅ `RealUrlFetcherClient` log scope is `UrlFetcher[{worker_id} url={host_only}]` (path stripped from log to avoid leaking user content in logs); content-length and final content-type at INFO on success; HEAD-405 fallback path logged at INFO so we can observe its frequency.
14. ✅ Smoke against the live Ollama: a 1024x1024 photo of a whiteboard returns `description` + `text_in_image` within ~15s on the 16 GB droplet. Wall-clock measured via `eval_duration` from the Ollama response, recorded in `extra.model_response.eval_duration`.

## Risks and gotchas

- **Presigned URL TTL vs Ollama queue depth.** A 5-min TTL is comfortable for normal load (one VLM job inflight + ~30s pre-flight + ~15s inference). Under backlog (10 image jobs queued behind a slow VLM), the 10th job's presigned URL might be 60–90 s old when the worker picks it up — still inside the window, but tightening. **Mitigation:** mint the presigned URL inside the worker right before the Ollama call, not when the extraction_task is created. Adds one S3 call per VLM job; trivial cost. **Trade:** when MaxConcurrency increases (post-eval), the TTL is still 5 min, so the window stays sufficient as long as `worker_throughput × TTL > queue_depth × per_job_latency`. For thesis demo loads (1 worker, ~15 s per job, TTL 300 s), the safe queue depth is ~20 — plenty.

- **`format:"json"` reliability on MiniCPM-V 4.6.** Ollama documents this flag as supported across all loaded models. **Verify at apply time** by running a smoke against a known-good image — if the model emits Markdown-wrapped JSON (```json fenced```) instead of pure JSON, either (a) update the prompt to explicitly say "do not wrap in markdown" (already covered by "Output ONLY the JSON object"), or (b) add a one-line strip-fence pre-parse step. Don't add the strip-fence pre-emptively — measure first.

- **`SixLabors.ImageSharp` License Plus drift.** ImageSharp 3.x is Apache-2.0 on the core, with a paid "Plus" tier for advanced features. Confirm at install time that the `SixLabors.ImageSharp` (not `.Plus`) NuGet is what gets pulled. The Plus tier license is incompatible with the thesis's open-source intent.

- **EXIF GPS as inadvertent PII surface.** We do store `gps_lat` / `gps_lng` in `extra.exif` when present. That's the user's own data on their own cloud — strictly speaking, fine. **But** if they later share a project via the magic-link sharing flow, the shared zip's provenance JSON includes the `extra.exif` blob, which leaks GPS to the recipient. The sharing-side W1+P1+F1+A1 strip from cloud-pivot §20 already strips provenance (`P1`). **Document explicitly** in this ticket's PR: GPS leaks ONLY if `P1` stripping is bypassed; never enable a "share with provenance" mode without first running EXIF redaction over GPS fields.

- **Reroute child's `storage_provider="external"` is a special sentinel** that the `IArtifactStore` does NOT understand (its providers are MinIO / DO Spaces / R2 — all S3-compatible buckets we own). Make sure `S3ArtifactStore.GetPresignedDownloadUrlAsync` throws cleanly if called with `storage_provider="external"` and that the specialist workers for rerouted children (VlmWorker, ParakeetWorker etc.) check the provider value FIRST and skip the presigning step (pass the `url` column directly to the sidecar instead). **This is per-worker logic** — each specialist worker that consumes a rerouted attachment needs to handle `storage_provider="external"`. For VlmWorker (this ticket), branch: if `att.StorageProvider == "external"`, use `att.Url` as the image source; else presign and use the presigned URL.

- **Cache key invalidation across prompt changes.** The cache key is derived from the model tag, not the prompt. If a future ticket tweaks the prompt without changing the model tag, stale cached extractions (computed under the old prompt) get reused. **Workaround for prompt-only changes:** bump the tag to `openbmb/minicpm-v4.6:q4_K_M+prompt-v2` (a synthetic suffix Ollama tolerates) — invalidates cache cleanly. **Long-term fix** (future ticket): include `sha256(prompt)` in the cache key. Out of scope here.

- **WireMock's HTTPS handling for the URL-fetcher tests.** Production URL-fetcher hits arbitrary HTTPS origins. The tests should stand up WireMock on HTTP (TLS termination is unnecessary; the `UrlExtractor`'s scheme check accepts both http and https). **Verify** that no test asserts on `https://` specifically.

- **Concurrent reroute races.** Two ingest jobs each rerouting the same URL (e.g., user pastes the same image-URL into two notes simultaneously) → both INSERT child attachments concurrently. **This is fine** — each child has its own `attachments` row, FK'd to its own parent URL attachment, FK'd to its own note. The sha256 of the bytes is computed once the sidecar fetches the URL and writes the result; the cache-key hit on the second concurrent job won't apply because the first job's child hasn't completed yet. After both complete, future jobs against the same URL hit the cache normally. Minor double-work in the narrow race window; acceptable.

- **MiniCPM-V 4.6 sometimes emits Chinese characters in `description` even when the prompt is English.** Observed on training-set-leaning inputs. Acceptable for the thesis (the model is multilingual; user can ask to re-process or change LLM mode). Don't add a "force English" instruction; it degrades quality on actually-Chinese photos.

- **`UrlExtractor.MaxResponseBytes = 5 MB` is the GET cap.** A poorly-tuned origin that streams an arbitrary-size text/html body forever (theoretically possible via a slowloris-style payload) is bounded by both the byte cap and the `FetchTimeout = 10s`. The HEAD-fallback `GET Range: bytes=0-0` returns just 1 byte; safe. The full GET path is also safe.

- **Image URL paste with weird Content-Length-Lying servers.** Some image CDNs return wrong/missing Content-Length on HEAD. Mitigate by relying on `Content-Type` only; bytes are downloaded by the sidecar from the URL (not by us), so a wrong Content-Length only matters if the sidecar later fails to fetch — which surfaces as a normal extraction_task failure, not a silent corruption.

- **The `Url` column on `attachments` is new (per schema-v2 from handoff #1).** It's nullable. Existing user-uploaded attachments have `Url=null`. Rerouted children have `Url=<original_url>`. Both supported.

- **The `ParentAttachmentId` FK has CASCADE on parent delete** per ADR-0032 / handoff #1. **Verify** by checking the schema-v2 migration — `attachments_parent_attachment_id_fkey ON DELETE CASCADE`. If it's `SET NULL` instead, parents being tombstoned would orphan children; for soft-delete the practical impact is small (the child still belongs to the note via `note_id`), but the CASCADE is the intent.

- **`pg_notify` for `extraction_tasks_<sidecar>_new` channel names** must use the EXACT sidecar value that the specialist worker LISTENs on. Specifically: `ollama`, `parakeet`, `docling`, `video`, `url`. **Centralize** the channel name building in a static helper:
  ```csharp
  public static class IngestNotifyChannels
  {
      public const string IngestJobsNew = "ingest_jobs_new";
      public const string IngestJobsChanged = "ingest_jobs_changed";
      public const string ExtractionTasksChanged = "extraction_tasks_changed";
      public static string ExtractionTasksNewForSidecar(string s) => $"extraction_tasks_{s}_new";
  }
  ```
  Worker LISTEN side and `RealUrlFetcherClient` notify side both consume the same helper. Misspellings are catastrophic-silent (the sub-task sits in `queued` indefinitely until the orchestrator's safety-net poll wakes everything every minute — which is wrong but not broken).

## Open contract decisions (carry forward)

1. **Whether to share the Ollama HttpClient between VLM and the (forthcoming) text LLM.** ADR-0044 may decide MiniCPM-V serves both vision and text (single model swap); if so, `OllamaClientNames.Vlm` and `OllamaClientNames.Llm` could be aliases. **For this ticket:** ship two distinct named clients with the same default config; the LLM ticket can collapse them later if appropriate. Cost of two named clients: zero (HttpClientFactory pools per-name; configs match).

2. **Cache key per-prompt vs per-model.** Locked to per-model in this ticket (see Risks). The long-term shape is `sha256:<sidecar>:<model_tag>:<prompt_hash>:<schema_version>`; out of scope to fully implement here.

3. **Image dimension thresholds.** Defaults from ADR-0043. Eval may show 100 px lower-bound is wrong (e.g., favicon-sized images that *do* have useful text). Open as a tunable in `appsettings.json`; revisit after capture-stream data is in.

4. **Whether to enable blur check by default.** Default off. Eval-driven decision. The threshold also unknown — tune after seeing the user's actual capture stream.

5. **GPS-stripping policy.** Open. Default: capture into `extra` for the user's own use; rely on the sharing-side W1+P1+F1+A1 strip. Future ticket may add a "redact GPS at ingest" toggle.

## What handoff #5+ inherit

After this ticket lands:

- **Two of the five specialist clients are real.** `IUrlFetcherClient → RealUrlFetcherClient`, `IVlmClient → OllamaVlmClient`. The other three (`IDoclingClient`, `IParakeetClient`, `IEmbeddingClient`, `IVideoSplitterClient`) are still stubs; handoffs #5–#6 swap them.
- **`AttachmentExtractionCache`, `ExtractionCacheKeys`, `OllamaClientNames`, `IngestNotifyChannels`** all exist and the next handoff reuses them for free.
- **`BinaryRerouteMap`** is in place; when handoff #6's video-splitter lands, flipping `IngestSaga:Specialists:Video:Enabled=true` activates the video reroute path with zero saga changes.
- **The Ollama HttpClient (named `OllamaVlm`)** can be cloned to a sibling `OllamaLlm` client by the LLM ticket with no shared-state implications; or, if ADR-0044 decides one client serves both, the LLM ticket can register `IChatClient` against `OllamaVlm` directly.
- **`UrlExtractionResult`** has the SmartReader-sourced fields populated; the assembler in CLOUD-002 can be extended to use `byline`, `published_at`, etc. in rendered output if/when the plugin asks for them.

## Why a separate handoff (and not folded into a single "swap all stubs" ticket)

Five stub→real swaps share a *DI shape* but not a *test surface* or a *failure mode*. The URL fetcher's failure modes (HEAD 405, unsupported mime, slowloris payloads) have nothing in common with the VLM's failure modes (model OOM, JSON parse failures, pre-flight skip). Bundling them would (a) balloon the test fixture setup (WireMock for Ollama AND WireMock for arbitrary HTTP origins AND WireMock for docling AND a parakeet fake AND ffmpeg-on-CI AND ONNX runtime for embeddings) into one PR, (b) make a single test failure ambiguous as to which adapter broke, (c) leave the docling/parakeet/embedding work blocked behind the trickier VLM pre-flight + JSON parsing. Splitting at "two non-LLM, no-extra-runtime-deps light processors" lets this ticket ship in 1.5–2 days against well-understood failure modes, and lets the heavier docling/parakeet/ffmpeg/ONNX work proceed in parallel siblings. The "light" in the name is descriptive: no model weights bundled into the .NET process; no native deps beyond ImageSharp.

## Cross-references

- **`docs/decisions/0043-cloud-side-model-lineup.md`** — the contract; this ticket implements the URL and image rows of §"Sub-pipeline detail per worker".
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — saga framework; this ticket consumes its `extraction_tasks` shape unchanged.
- **`docs/decisions/0044-cloud-intelligence-layer.md`** — VLM prompt template; co-amend with this PR if the §"VLM prompt template" section is thin.
- **`docs/decisions/0045-composite-note-schema.md`** — `attachments.extraction_cache_key`, `attachments.extra`, `attachments.parent_attachment_id`, `attachments.url` columns this ticket reads/writes.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — exception → retry semantics surfaced by the new exception types.
- **`docs/decisions/0023-test-stack.md`** — WireMock + Testcontainers + xUnit v3 + Shouldly.
- **`plans/cloud-schema-v2-handoff.md`** — schema-v2 (handoff #1); columns this ticket consumes.
- **`plans/cloud-sidecars-handoff.md`** — running Ollama (handoff #2); the sidecar this ticket calls.
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — framework (handoff #3); the workers + stubs this ticket extends.
- **`src/ThanyMarcus.Cloud.Api/Infrastructure/Extraction/UrlExtractor.cs`** — existing extractor; wrapped by `RealUrlFetcherClient` unchanged.
- **`src/ThanyMarcus.Cloud.Api/Infrastructure/Storage/IArtifactStore.cs`** — presigned-URL minting for the VLM happy path.
- **Memory `composite_ingest_decision.md`** — one composite → one note; reroute keeps the invariant.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3.

## Definition of done

```
$ dotnet build
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName~CompositeIngestSagaEndToEndTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0   # stub-anchor (unchanged) + new real-light-processors sibling

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName~Sidecars"
Passed!  - Failed: 0, Passed: N, Skipped: 0   # the new client-level test suites

$ grep -rn "StubUrlFetcherClient\|StubVlmClient" src/ThanyMarcus.Cloud.Api/Program.cs
(no results)                                  # production wiring uses real clients

$ grep -rn "RealUrlFetcherClient\|OllamaVlmClient" src/ThanyMarcus.Cloud.Api/Program.cs
src/ThanyMarcus.Cloud.Api/Program.cs:...      # exactly two new DI lines

$ grep -rn "IDoclingClient\|IParakeetClient\|IEmbeddingClient\|IVideoSplitterClient" src/ThanyMarcus.Cloud.Api/Program.cs
src/ThanyMarcus.Cloud.Api/Program.cs:...      # still stubs — handoff #5/#6 swap them
```

A fresh agent picking up handoff #5 (`CLOUD-DOCLING-WORKER` + `CLOUD-PARAKEET-WORKER` + `CLOUD-EMBEDDING`) from this state knows:
- The `SpecialistWorkerBase<TClient>` framework is fully exercised by two real clients now; the patterns (named HttpClient, typed response records, pre-flight skip path, cache-key population, exception → retry mapping) are concrete examples to copy.
- `AttachmentExtractionCache`, `ExtractionCacheKeys`, `BinaryRerouteMap`, `IngestNotifyChannels` exist and have sibling slots reserved (`ForDocling`, `ForParakeet`, `Llm`, etc.).
- `OllamaClientNames` reserves an `Llm` slot for the next routing/entity-extraction LLM ticket; whether that ticket uses the same Ollama HttpClient or a sibling is the only open question.

A fresh agent picking up handoff #6 (`CLOUD-VIDEO-SPLITTER`) knows:
- `BinaryRerouteMap` already routes `video/*` → `target_sidecar='video'`; flipping `IngestSaga:Specialists:Video:Enabled=true` activates the path with zero saga or schema changes.
- The reroute child for video carries `storage_provider="external"` and `url=<original>`; the video splitter worker handles the same dual-path (presigned-from-Spaces vs direct-from-URL) that `OllamaVlmClient` already demonstrates.
