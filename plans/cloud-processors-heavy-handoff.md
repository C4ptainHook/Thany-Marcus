# CLOUD-PROCESSORS-HEAVY — DoclingWorker + ParakeetWorker + VideoSplitterWorker — Handoff Brief

Date: 2026-05-19
Status: Draft. Fifth ticket of the ADR-0042 implementation series. Drops in three real specialist workers — `DoclingWorker` (preflight + docling-serve), `ParakeetWorker` (ffprobe + silence-VAD + parakeet-server), `VideoSplitterWorker` (local ffmpeg + child-attachment fan-out) — that replace the matching stub clients seeded by handoff #3 (`cloud-ingest-saga-foundation-handoff.md`). After this ticket, four of the five M5 attachment kinds (`document`, `audio`, `video`, plus the URL fetcher inherited from CLOUD-002) flow through real extractors against the running sidecars from handoff #2 (`cloud-sidecars-handoff.md`). The `image` kind continues to use `StubVlmClient` until handoff #4 (`CLOUD-VLM-WORKER`) lands; this ticket does **not** require handoff #4 because video's child image tasks will route to whatever `IVlmClient` is currently registered (stub now, real later).

**Goal:** swap three DI registrations from stub clients to real clients (`StubDoclingClient → DoclingHttpClient`, `StubParakeetClient → ParakeetHttpClient`, `StubVideoSplitterClient → FfmpegVideoSplitterClient`); land their preflight pipelines (size cap + page count for documents, ffprobe + silence-VAD for audio, ffprobe + duration cap for video); land the `VideoSplitterWorker` fan-out that uses local ffmpeg to extract keyframes + audio, uploads them to Spaces via presigned PUT, INSERTs child `attachments` rows with `parent_attachment_id`, and INSERTs child `extraction_tasks` so the orchestrator's "wait for terminal sub-tasks" loop naturally picks them up. The end-to-end M5 demo "drop a 30 s phone-camera video into Obsidian, get a Markdown note with N keyframe descriptions + audio transcript" works after this ticket, with the image VLM step stubbed-but-not-blocking.

Estimated **3.5 person-days** with AI-agent assistance, split into three passes (~1d Docling, ~1d Parakeet, ~1.5d VideoSplitter). The Video pass is the heaviest because ffmpeg invocation + child-attachment INSERT + child-task INSERT + storage-key allocation + presigned-PUT-from-server is genuinely new code shape; Docling and Parakeet are mostly "HTTP client + preflight filter." Pass C also lands the cross-worker integration test that exercises a 4-attachment composite (URL + image-stub + voice + PDF + video) through the full pipeline — the regression anchor that handoff #4 and handoff #6 must keep green when they swap their own stubs.

This handoff **does not** ship: real VLM (handoff #4), real LLM routing/entity prompts (handoff #4), real Granite Embedding ONNX (handoff #6), live UrlFetcherWorker re-route to binary attachments (handoff #6 — Pass B of this ticket *consumes* a stable URL → binary detection contract from the URL fetcher, but the URL fetcher's own real impl lands later). All four are independent drop-in seams behind the `I{Vlm,Embedding,UrlFetcher}Client` interfaces that the saga foundation already wired.

## Where decisions live (read before doing anything)

- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §1 worker topology (Docling=2 replicas, Parakeet=1, VideoSplitter=1 per-sidecar caps), §3 the `extraction_tasks` state machine these workers drive, §7 latency budget (Docling ~10 s for a 5-page native-text PDF, Parakeet ~45 s for a 60 s clip — these are the numbers eval will measure against), §"Schema delta" (`parent_attachment_id`, `extraction_cache_key`, `extra` JSONB shape per kind, `extraction_tasks.target_sidecar` enum). **The contract this ticket implements.**
- **`docs/decisions/0043-cloud-side-model-lineup.md`** — §"Classical pre-flight per kind" (the staged-filter table — audio = ffprobe + silence-VAD + duration cap; document = page count + size cap; video = ffprobe + duration cap), §"Video as composite extraction" (the 6-step splitter recipe: presigned GET → ffmpeg keyframes → ffmpeg audio → upload children → INSERT child attachments → INSERT child extraction_tasks → mark parent succeeded), §"Sub-pipeline detail per worker" (per-worker `extracted_text` + `extra` + `extraction_cache_key` shapes — Docling's key is `sha256:docling:granite-docling:258m`, Parakeet's is `sha256:parakeet:parakeet-tdt-0.6b-v3:int8`), §"Sidecar binary access" (every worker generates a 5-min presigned GET URL and passes it to the sidecar; binaries never transit cloud-api).
- **`docs/decisions/0045-composite-note-schema.md`** — §5 `attachments.parent_attachment_id` FK CASCADE semantics; §"Migration plan" confirms the cascade direction (delete parent → children gone). The video-splitter's child rows live under the same `note_id` as the parent and inherit cascade behavior from `notes`.
- **`plans/cloud-schema-v2-handoff.md`** — confirms `parent_attachment_id`, `extraction_cache_key`, the `ix_attachments_parent` + `ix_attachments_cache` indexes are already in the DB. This ticket reads/writes them; no migration ships here.
- **`plans/cloud-sidecars-handoff.md`** — the `docling` and `parakeet` services that this ticket's HTTP clients call. Config keys `IngestSaga:Sidecars:Docling:BaseUrl=http://docling:5001`, `IngestSaga:Sidecars:Parakeet:BaseUrl=http://parakeet:5092` are already in `appsettings.json`; pre-pulled models live in `docling-models` / `parakeet-models` named volumes; the compose stack runs them with `mem_limit` + healthcheck. **This ticket adds no compose/cloud-init/terraform changes.** One small exception: the `cloud-api` image needs `ffmpeg` + `ffprobe` binaries on PATH; the Dockerfile gets an `apt-get install ffmpeg` step in Pass C. The base image (`mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim`) does not ship ffmpeg.
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — the framework this ticket extends. The orchestrator's `ExtractingAttachmentsHandler` already INSERTs `extraction_tasks` rows with `target_sidecar` set by kind; `SpecialistWorkerBase<TClient>` is the abstract base each of the three real workers inherits; `IIngestEventBus` is the SSE bus the workers fire `attachment_status_changed` events through; the cache-hit short-circuit is already in place (matching `sha256 + extraction_cache_key` → write `extraction_tasks.status='skipped'` with cached text, never enqueue real work). **Re-read §"What handoffs #4–#6 inherit" before starting** — the contract this ticket consumes is named there explicitly.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — SKIP LOCKED + lease + transition_version on `extraction_tasks`. The base class owns the claim loop; this ticket does not touch it.
- **`docs/decisions/0026-observability-and-health-checks.md`** — `IngestSaga:Sidecars:<name>:HealthPath` is the URL the worker probes before each call (or relies on docker-compose's healthcheck-driven restart; the worker just calls and lets HTTP errors surface as retries). Pick the latter — fewer round-trips, the sidecars are local-LAN.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. Video fan-out is **internal** to the saga; the plugin never sees the child attachments as separate notes, only as multi-frame blocks inside the parent's composite Markdown body. Children get cleaned up via FK CASCADE when the parent note is deleted.

**Do not litigate ADR-0042 or ADR-0043.** If you find a sidecar latency target you think is wrong, raise a follow-up; don't change the worker's call shape inside this ticket. Same for the video-splitter recipe — it's six steps, in order, and the order matters (children must be inserted BEFORE the parent task flips to `succeeded`, otherwise the orchestrator's "all tasks terminal" check races and the saga advances with the child tasks orphaned).

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **Three real workers in one handoff, not three separate handoffs.** Each worker is ~1 person-day; bundling avoids three rounds of "open another PR, re-explain the worker base class, re-bisect any new flake." The cross-worker regression test in Pass C only makes sense when at least two real workers run alongside each other — splitting at the worker boundary means Pass C exists in three different tickets, each adding one assertion. Bundle.

2. **Docling and Parakeet are pure HTTP clients with preflight in front; no model code in-process.** The cloud-api process never loads Granite-Docling or Parakeet weights — those live inside the sidecar containers. The C# client only does: build a presigned GET URL via `IArtifactStore`, POST a JSON envelope to the sidecar, parse the response. Preflight uses standalone classical tools (`ffprobe` for audio + video, `PdfPig` for page count). Docling has a `/v1alpha/convert/source` precount endpoint we could call for page count instead of pulling PdfPig in, but adding one more sidecar round-trip per attachment for a value PdfPig computes in ~10 ms is the wrong trade. **PdfPig for page-count preflight; Docling for the real extraction call.**

3. **VideoSplitterWorker shells out to `ffmpeg` + `ffprobe` as child processes**, not via a managed wrapper like Xabe.FFmpeg. The two reasons: (a) we want exact control over invocation strings for thesis reproducibility (every keyframe extracted is auditable from the recorded command line), (b) Xabe.FFmpeg's Apache 2.0 dep brings in a chunk of P/Invoke surface that's overkill for the two ffmpeg patterns we need (keyframe extract, audio extract). `Process.Start("ffmpeg", "-i input.mp4 -vf fps=...")` with stdout/stderr capture + non-zero-exit-throws is ~80 LOC of plumbing in `Infrastructure/Sidecars/FfmpegRunner.cs` and we ship a tiny wrapper `IFfmpegRunner` for testability (test fake substitutes a canned output). **Process.Start, not Xabe.**

4. **Keyframe extraction rate is `fps=1/N` where N is configurable**, default value 5 (one keyframe every 5 seconds). With a 5-minute cap on video duration (per ADR-0043), the max keyframes per video is 60 — well under the 16-frames-per-minute soft ceiling ADR-0043 mentions for the "default" case. Make `IngestSaga:Filters:Video:KeyframeIntervalSeconds=5` configurable in `appsettings.json` so eval can sweep the value. ADR-0043's "up to 16 frames per minute" is the *upper* bound from scene-aware detection (which we are not implementing — see §"Alternatives considered" below for why); the constant-interval fallback at default settings produces 12 frames per minute, leaving room for the eval to bump up if scene-coverage looks sparse.

5. **Audio extraction format = 16 kHz mono WAV PCM 16-bit**, written as `notes/<note-id>/attachments/<parent-id>/audio.wav` in the bucket. Parakeet's INT8 model is trained on 16 kHz mono; passing anything else makes the sidecar resample internally (extra latency + occasional quality loss). The ffmpeg invocation is `-ac 1 -ar 16000 -c:a pcm_s16le -vn`. Format choice is locked here and matches what `ParakeetWorker.cs`'s real client expects when ParakeetWorker eventually picks up the child audio task.

6. **Keyframe image format = JPEG, quality 85**, written as `notes/<note-id>/attachments/<parent-id>/keyframes/<i>.jpg`. JPEG over PNG because (a) MiniCPM-V's image preprocessing internally re-encodes to JPEG for transport anyway, (b) ~10× smaller bucket footprint per frame — at 12 fpm × 5 min cap × 50 KB = ~3 MB per video instead of ~30 MB. Quality 85 is the Pillow/ffmpeg-jpeg sweet spot; nothing in MiniCPM-V's prompt benefits from preserving every pixel of compression artifact. The `i` index is zero-padded 3 digits (`000.jpg`, `001.jpg`, …) for deterministic lexicographic listing in the bucket.

7. **Child attachments inherit `note_id` from the parent and link via `parent_attachment_id`.** They are NOT visible to the plugin via `/api/sync/pull` as separate top-level items — the sync endpoint's response shape only ever includes top-level attachments (`WHERE parent_attachment_id IS NULL`). The composite Markdown body that the `ComposingHandler` produces is where children show up, rendered as a multi-frame block underneath the parent video's reference. **This ticket does NOT change `ComposingHandler` or `CompositeMarkdownAssembler`** — the composer will already iterate `attachments` and skip child rows from top-level rendering (because they have `parent_attachment_id` set). The rendering of children as a multi-frame block underneath the parent is part of `CLOUD-COMPOSITION` (probably folded into handoff #4 alongside the real VLM since that's where description text becomes meaningful). For this ticket, child rows exist in the DB and carry `extracted_text` populated by their own specialist worker; how that text is laid out in the final body is the next ticket's problem.

8. **The video splitter marks its OWN task `succeeded` AFTER inserting children, NOT before.** Order is load-bearing for the orchestrator's "all terminal" check: if the video task flips to `succeeded` before the child rows are visible, the orchestrator can race-claim, see "no unterminal tasks for this job," and advance to `composing` with no child output. Concrete order inside the splitter's `ExtractAsync`:
   - INSERT N image children + INSERT 1 audio child + INSERT N+1 extraction_tasks (all `status='queued'`).
   - Save changes.
   - Within the same `SpecialistWorkerBase.ExecuteAsync` transaction, the base class then UPDATEs the parent's `extraction_tasks` row to `succeeded` + writes `attachments.extracted_text = '[video split: N keyframes + 1 audio track]'` (placeholder; the composer reads `extra` not `extracted_text` for video kinds) + `attachments.extra = { keyframe_count, audio_duration_s, frame_offsets, keyframe_storage_keys, audio_storage_key }`.
   - Fire `pg_notify('extraction_tasks_changed', '<ingest_job_id>')` once at the end.
   The orchestrator's next claim sees: 1 parent `succeeded` + N+1 children `queued`. Its terminal-check returns false. Workers race-claim the children. After all children terminal, orchestrator advances. **Single transaction is mandatory** — split commits race.

9. **Child storage keys are server-side computed and signed BY the cloud-api**, not by the sidecar. The splitter writes keyframe bytes to a temp dir, calls `IArtifactStore.UploadBytesAsync(key, bytes)` (NEW method this ticket adds — see §"IArtifactStore extension" below) for each, then INSERTs the child attachment rows with `storage_provider='s3'`, `storage_bucket=<cloud-bucket>`, `storage_key=notes/<note-id>/attachments/<parent>/keyframes/<i>.jpg`, `byte_size=<actual>`, `sha256=<computed>`. The sidecar's presigned-GET URL is generated only when the child's specialist worker (VlmWorker for keyframes, ParakeetWorker for audio) runs — same path every other attachment uses. **The video splitter does the UPLOAD; child specialist workers do the DOWNLOAD-AS-PRESIGNED-URL.**

10. **`IArtifactStore.UploadBytesAsync(key, bytes, mimeType, ct)` is a NEW method.** Today's interface (from CLOUD-002) only does `IssueUploadUrlAsync` (returns a presigned PUT URL for the plugin to PUT against). Server-side direct upload (the video splitter's case) needs a new method that takes raw bytes and PUTs them through the in-process AWSSDK.S3 client. **Implication:** the cloud-api process now both signs URLs (plugin uploads) AND does direct PUTs (video children). For a 5-min 720p video, keyframes total ~3 MB + audio ~5 MB = ~8 MB of bytes flowing through cloud-api process memory transiently during the split. Acceptable on the 16 GB droplet; if videos get larger, revisit by chunking the ffmpeg → bucket pipe (out of scope here). The new method is on the existing `IArtifactStore` interface so future Azure Blob impl matches.

11. **Preflight failures write `extraction_status='skipped'` + `extraction_error=<reason>`, AND emit an `attachment_status_changed` SSE event with `to='skipped'`.** The orchestrator counts skipped as a terminal state for its "all tasks terminal" check; the plugin shows skipped attachments as "extraction unavailable: <reason>" in the rendered Markdown (handoff #6 wires the actual rendering — for now `extracted_text=null` + `extraction_status='skipped'` is the contract the composer reads). **Preflight failure does NOT bubble up to fail the parent job.** Best-effort cascade per ADR-0042 §4 — composing still runs, the note still lands `ready` with whatever content the other attachments produced. A 200-page PDF rejected at preflight does not block a successful note when the user also captured a one-line voice memo.

12. **Silence-VAD threshold is RMS amplitude in [0, 1] normalized space**, default `0.005` (very quiet — anything quieter than typical room background hum). Implementation: walk the WAV in 100 ms windows, compute RMS per window, if the loudest window across the whole clip is below threshold the clip is "pure silence" and Parakeet is skipped. Cheap (~50 ms for a 1-minute WAV), runs on the cloud-api process in C# (NAudio for WAV reading, then mean-square + sqrt in a hot loop). NAudio's Apache 2.0 license is fine; the dep is small. **Threshold + window size in `appsettings.json:IngestSaga:Filters:Audio:*`.** Voice memos with whispered content sometimes fall below the default threshold; if eval shows false-positive skips, tune down. Conversely, recordings from outdoor environments may have wind/traffic noise that defeats the filter — but those are exactly the recordings where Parakeet's actual transcript will be most useful, so a permissive threshold is correct.

13. **Document page-count preflight uses PdfPig 0.1.x (Apache 2.0).** PdfPig opens the PDF stream (downloaded via the same presigned-GET that Docling would use), reads `document.NumberOfPages`, closes. ~10 ms for any PDF under ~100 MB; we cap at the `IngestSaga:Filters:Document:MaxSizeBytes` (default 50 MB) so we never actually open monsters. Pages > `IngestSaga:Filters:Document:MaxPageCount` (default 200) → skipped. PdfPig handles non-PDFs by throwing on `Open(stream)` — catch and re-classify as preflight-failed (the file is a `document` kind but not a real PDF; the mime-sniff at ingest time should've caught this, but defense in depth). Non-PDF documents (DOCX, PPTX, etc.) that Docling can handle but PdfPig can't: skip the page-count preflight for non-PDF MIME types and rely on size cap only.

14. **Docling endpoint path is `POST /v1alpha/convert/source` with `{ http_source: { url } }`** per ADR-0043. The `v1alpha` is the docling-serve v0.5.0 surface — verify at apply time against the actual pinned image (the path may shift to `v1` in a future image bump). The request body is a JSON envelope; Docling fetches the URL itself from inside its container over the `cloud` bridge network. **The presigned URL must be reachable from the docling container's network position**, which means it points at the public DO Spaces endpoint (not a `127.0.0.1` loopback) — the docling container's outbound HTTPS through the droplet's NAT reaches Spaces fine. Verified by the existing CLOUD-002 plugin upload path that uses the same endpoint.

15. **Parakeet endpoint path is `POST /v1/audio/transcriptions`** (OpenAI Whisper API-compatible, per the achetronic/parakeet README). Body is `multipart/form-data` with a `file` field; URL-based input is supported per the README via the `url` field on multipart. Verify at apply time. The OpenAI-compatible response is `{ text: "..." }`; we deserialize that and write to `attachments.extracted_text`. Language detection (`extra.language_detected`) comes from the response `language` field if Parakeet emits one; if the field is absent on the pinned image, leave it `null` and document the gap.

16. **Per-worker preflight cache is keyed on `sha256` only**, not `sha256 + version`. Once an audio file's silence-VAD says "silent" the file's content is silent forever; there's no model version that changes the answer. So a reprocess of a previously-skipped audio attachment with the same sha256 → preflight short-circuits without even fetching the bytes. This is implemented by reading `attachments.extraction_status` on the current row before invoking the sidecar — if the row was previously skipped AND its sha256 matches the new task's sha256 reference (always the same on reprocess because the bucket key is content-addressed), skip again. **Same-attachment same-content reprocess = cheap; different-content = full preflight + sidecar.** This is a small optimization but the silence case in particular shows up often during eval (every empty / paused / failed audio capture).

17. **`extraction_cache_key` semantics for Docling/Parakeet match ADR-0043 verbatim.** Docling = `sha256:docling:granite-docling:258m`; Parakeet = `sha256:parakeet:parakeet-tdt-0.6b-v3:int8`. The version string comes from `appsettings.json:IngestSaga:Models:{Docs,Asr}:{DoclingVersion,ParakeetVersion}` (already in the config from handoff #2). Bumping either invalidates that worker's cache cleanly. **Video does NOT have an `extraction_cache_key`** — the splitter is deterministic-by-input-bytes (same video bytes → same keyframes → same offsets), and reprocess uses the same children that already exist (children's own caches do their work). The splitter writes `extraction_cache_key = sha256:video:ffmpeg:n-keyframes-{N}` on the *parent* attachment so reprocess with a different keyframe interval re-splits, but reprocess with the same interval reuses the existing children. Edge case: when re-splitting, the OLD children's rows must be torn down (`DELETE FROM attachments WHERE parent_attachment_id = <video> CASCADE` — the cascade drops their extraction_tasks too). Implement as a "if `extraction_cache_key` differs from expected → DELETE children + re-split" check at the top of the splitter's `ExtractAsync`. If equal → return early (children already exist; the orchestrator's terminal check picks them up directly).

18. **The video splitter is NOT a sidecar** — `target_sidecar='video'` is a queue identifier, not a separate process. ffmpeg runs in the cloud-api container; the `mem_limit` on cloud-api stays the same (~500 MB baseline + transient ~100 MB during ffmpeg invocation). Confirmed against ADR-0043 §"Steady-state resource footprint": ffmpeg's transient ~100 MB is accounted for in the headroom budget. No compose service is added.

19. **VideoSplitterWorker concurrency = 1**, per ADR-0042 §1. Two videos splitting in parallel = two ffmpeg processes = potential CPU starvation for the saga's other phase work. Sequential video splitting is fine — videos are infrequent in the thesis demo. If eval shows multi-video captures are common, bump `IngestSaga:Specialists:VideoSplitter:Replicas=2` via config — the per-worker semaphore stays at 1, but two splitter instances can claim independent jobs from the queue.

20. **No streaming transcription, no scene-aware keyframe detection, no multi-format video transcoding.** Three explicit deferrals listed in §"Out of scope" below. The constant-interval keyframe + WAV-only audio path is the thesis MVP; smarter approaches are future-work hooks per ADR-0042 §"Deferred future-work hooks."

## Scope boundary (precise)

Three passes, ~1 day for Docling (Pass A), ~1 day for Parakeet (Pass B), ~1.5 days for VideoSplitter + cross-worker integration test (Pass C). All three ship in the same handoff because Pass C's regression test depends on every worker being real.

### Pass A — `DoclingWorker` + `DoclingHttpClient` + PDF preflight (~1 day)

The `DoclingWorker` class itself stays — it's already registered as a specialist worker by handoff #3, inheriting from `SpecialistWorkerBase<IDoclingClient>` with `TargetSidecar = "docling"`. This pass swaps the DI registration from `StubDoclingClient` to `DoclingHttpClient` and adds the preflight pipeline.

**1. `Infrastructure/Sidecars/DoclingHttpClient.cs` — NEW.**
   ```csharp
   public sealed class DoclingHttpClient(
       HttpClient httpClient,
       IArtifactStore store,
       IOptions<DoclingOptions> options,
       IClock clock,
       ILogger<DoclingHttpClient> log) : IDoclingClient
   {
       public async Task<string> ExtractMarkdownAsync(
           Attachment att, CancellationToken ct)
       {
           var presignedGet = await store.IssueDownloadUrlAsync(
               att.StorageKey, TimeSpan.FromMinutes(5), ct);
           var body = JsonSerializer.SerializeToUtf8Bytes(new
           {
               http_source = new { url = presignedGet.Url.ToString() }
           });
           var req = new HttpRequestMessage(HttpMethod.Post, "/v1alpha/convert/source");
           req.Content = new ByteArrayContent(body);
           req.Content.Headers.ContentType = new("application/json");
           var resp = await httpClient.SendAsync(req, ct);
           if (!resp.IsSuccessStatusCode)
               throw new DoclingClientException(
                   $"docling returned {(int)resp.StatusCode}: " +
                   await resp.Content.ReadAsStringAsync(ct));
           var json = await resp.Content.ReadFromJsonAsync<DoclingConvertResponse>(ct);
           return json!.Document.MdContent;  // shape verified against docling-serve v0.5.0
       }
   }
   public sealed record DoclingConvertResponse(DoclingDocument Document);
   public sealed record DoclingDocument(string MdContent, int? PageCount, /*...*/);
   ```
   - The `HttpClient` is `AddHttpClient<IDoclingClient, DoclingHttpClient>` in `Program.cs` with `BaseAddress = options.BaseUrl` (default `http://docling:5001`), `Timeout = TimeSpan.FromMinutes(2)` (Docling can take ~30 s on a 50-page PDF; 2 min gives headroom + buffer for queue-wait inside the sidecar).
   - Polly retry policy: `WaitAndRetryAsync` with 2 attempts, exponential backoff (1 s, 2 s), only on transient HTTP 5xx and `TaskCanceledException`. 4xx is permanent — surface to the worker which records `extraction_status='failed'`.
   - Verify the actual response JSON shape against `docling-serve:v0.5.0-cpu` at apply time. The README shows `{document: {md_content: "..."}}` but field name casing varies between Pydantic-JSON versions.

**2. `Infrastructure/Sidecars/Preflight/PdfPreflighter.cs` — NEW.**
   ```csharp
   public sealed class PdfPreflighter(
       IArtifactStore store,
       IOptions<DocumentFilterOptions> options) : IDocumentPreflighter
   {
       public async Task<PreflightResult> CheckAsync(
           Attachment att, CancellationToken ct)
       {
           if (att.ByteSize is { } size && size > options.Value.MaxSizeBytes)
               return PreflightResult.Skip($"size {size} > max {options.Value.MaxSizeBytes}");
           if (att.MimeType != "application/pdf")
               return PreflightResult.Pass;  // non-PDF docs skip page-count
           await using var stream = await store.OpenReadAsync(att.StorageKey, ct);
           try
           {
               using var pdf = PdfDocument.Open(stream);
               if (pdf.NumberOfPages > options.Value.MaxPageCount)
                   return PreflightResult.Skip(
                       $"pages {pdf.NumberOfPages} > max {options.Value.MaxPageCount}");
               return PreflightResult.PassWith(new { page_count = pdf.NumberOfPages });
           }
           catch (PdfDocumentEncryptedException) { return PreflightResult.Skip("pdf_encrypted"); }
           catch (Exception ex) { return PreflightResult.Skip($"pdf_open_failed: {ex.GetType().Name}"); }
       }
   }
   public sealed record DocumentFilterOptions
   {
       public long MaxSizeBytes { get; init; } = 50L * 1024 * 1024;
       public int  MaxPageCount { get; init; } = 200;
   }
   public sealed record PreflightResult(bool ShouldExtract, string? SkipReason, object? Metadata)
   {
       public static PreflightResult Pass => new(true, null, null);
       public static PreflightResult PassWith(object meta) => new(true, null, meta);
       public static PreflightResult Skip(string reason) => new(false, reason, null);
   }
   ```
   - `IArtifactStore.OpenReadAsync(key, ct)` is a SECOND new method this ticket adds to `IArtifactStore` (alongside `UploadBytesAsync` in Pass C). Returns a `Stream` of the object body; backed by AWSSDK.S3's `GetObjectAsync(...).ResponseStream`. Reads stream the bytes through the cloud-api process; for PdfPig's "open + read header" usage this is ~5 KB of network traffic per PDF, negligible.
   - PdfPig 0.1.10 is the verified version at handoff time; pin in `Directory.Packages.props`.

**3. `DoclingWorker.cs` (existing from handoff #3) — modify to call preflight.**
   The current `DoclingWorker` overrides `ExtractAsync` and just calls `await client.ExtractMarkdownAsync(...)`. This pass adds the preflight call before the client call:
   ```csharp
   protected override async Task<ExtractionOutcome> ExtractAsync(
       IDoclingClient client, Attachment att, CancellationToken ct)
   {
       var pre = await preflighter.CheckAsync(att, ct);
       if (!pre.ShouldExtract) return ExtractionOutcome.Skip(pre.SkipReason!, pre.Metadata);
       var markdown = await client.ExtractMarkdownAsync(att, ct);
       return ExtractionOutcome.Success(
           extractedText: markdown,
           extra: pre.Metadata,
           cacheKey: $"sha256:docling:granite-docling:{models.DoclingVersion}");
   }
   ```
   - `ExtractionOutcome` is a NEW result type in `Features/Processing/Specialists/ExtractionOutcome.cs` (replaces the `Task<string>` return signature on `SpecialistWorkerBase<TClient>.ExtractAsync`). It encodes the three terminal states: `Success(text, extra, cacheKey) | Skip(reason, metadata?) | Fail(reason)`. The base class writes the appropriate row updates based on the outcome variant. **Change cascades to all five specialist workers' signatures** — they all migrate from `Task<string>` to `Task<ExtractionOutcome>`. The four other workers' bodies just wrap their existing return in `ExtractionOutcome.Success(text, null, $"sha256:stub:{kind}:v1")` until their real impls land.

**4. Config keys** in `appsettings.json` (replace handoff #2's placeholder section):
   ```json
   "IngestSaga": {
     "Filters": {
       "Document": { "MaxSizeBytes": 52428800, "MaxPageCount": 200 }
     },
     "Models": {
       "Docs": { "DoclingVersion": "258m" }
     }
   }
   ```

**5. Tests for Pass A:**
   - `Infrastructure/Sidecars/DoclingHttpClientTests.cs` — `HttpMessageHandler` fake; happy path (200 + JSON body → returned Markdown); 5xx → retry → eventual success; 4xx → throws `DoclingClientException`; timeout → throws `TaskCanceledException`; presigned-URL token in request body matches the URL the fake `IArtifactStore` returned.
   - `Infrastructure/Sidecars/Preflight/PdfPreflighterTests.cs` — happy path with a small in-memory PDF (use a tiny fixture PDF in `tests/.../Fixtures/sample-5-page.pdf` ~1 KB); size-cap reject; page-count reject (a fixture 200+-page generated PDF — OR a unit-test mock around PdfPig if the bigger fixture is unwieldy); encrypted-PDF reject; corrupt-PDF reject; non-PDF mime → passes without opening.
   - `Features/Processing/Specialists/DoclingWorkerTests.cs` — wire real `DoclingHttpClient` against a Testcontainers-hosted `quay.io/docling-project/docling-serve:v0.5.0-cpu` (slow — ~60 s container start); ONE test that PUTs a tiny real PDF to MinIO, runs the worker, asserts `extracted_text` contains the PDF's expected page text. Mark `[Trait("Category","Slow")]`; gate behind CI label `slow-tests`.
   - `Features/Processing/Specialists/DoclingWorkerPreflightTests.cs` — fast tests using a mock `IDoclingClient` + real `PdfPreflighter` to verify the preflight short-circuit (oversized → no client call; under-cap → client call).

→ At end of Pass A: a `document`-kind attachment with a real PDF flows through `DoclingHttpClient` → returns real Markdown extraction. Composite Markdown produced by the existing `ComposingHandler` now embeds the PDF's text. Tests are green; the slow Testcontainers test runs under a CI label so non-PR builds skip it.

### Pass B — `ParakeetWorker` + `ParakeetHttpClient` + audio preflight (~1 day)

Same shape as Pass A: swap stub, add preflight, add tests.

**1. `Infrastructure/Sidecars/ParakeetHttpClient.cs` — NEW.**
   ```csharp
   public sealed class ParakeetHttpClient(
       HttpClient httpClient,
       IArtifactStore store,
       IOptions<ParakeetOptions> options,
       ILogger<ParakeetHttpClient> log) : IParakeetClient
   {
       public async Task<ParakeetResult> TranscribeAsync(
           Attachment att, CancellationToken ct)
       {
           var presignedGet = await store.IssueDownloadUrlAsync(
               att.StorageKey, TimeSpan.FromMinutes(5), ct);
           using var form = new MultipartFormDataContent();
           form.Add(new StringContent(presignedGet.Url.ToString()), "url");
           var resp = await httpClient.PostAsync("/v1/audio/transcriptions", form, ct);
           if (!resp.IsSuccessStatusCode)
               throw new ParakeetClientException(
                   $"parakeet returned {(int)resp.StatusCode}: " +
                   await resp.Content.ReadAsStringAsync(ct));
           var json = await resp.Content.ReadFromJsonAsync<ParakeetResponse>(ct);
           return new ParakeetResult(json!.Text, json.Language);
       }
   }
   public sealed record ParakeetResponse(string Text, string? Language);
   public sealed record ParakeetResult(string Transcript, string? LanguageDetected);
   ```
   - `HttpClient` `Timeout = TimeSpan.FromMinutes(3)` — Parakeet on a 5-min audio clip is ~3 min on a 4 vCPU CPU; cap matches ADR-0043's 10-min duration cap with margin.
   - Retry policy: Polly `WaitAndRetryAsync` with 1 attempt (Parakeet calls are expensive; retry on 5xx only, never on 4xx, never on timeout — a timeout means the model is stuck and another call will likely also stall).

**2. `Infrastructure/Sidecars/Preflight/AudioPreflighter.cs` — NEW.**
   ```csharp
   public sealed class AudioPreflighter(
       IArtifactStore store,
       IFfprobeRunner ffprobe,
       IOptions<AudioFilterOptions> options) : IAudioPreflighter
   {
       public async Task<PreflightResult> CheckAsync(
           Attachment att, CancellationToken ct)
       {
           if (att.ByteSize is { } size && size > options.Value.MaxSizeBytes)
               return PreflightResult.Skip($"size {size} > max");
           await using var temp = await store.OpenReadAsync(att.StorageKey, ct);
           var (durationSec, codec, sampleRate) = await ffprobe.ProbeAsync(temp, ct);
           if (durationSec > options.Value.MaxDurationSeconds)
               return PreflightResult.Skip($"duration {durationSec:F1}s > max");
           if (codec is null)
               return PreflightResult.Skip("no_decodable_audio_stream");
           var rms = await SilenceDetector.MaxRmsAsync(att, store, ct);
           if (rms < options.Value.SilenceRmsThreshold)
               return PreflightResult.Skip($"silent_rms_{rms:F4}");
           return PreflightResult.PassWith(new
           {
               duration_s = durationSec,
               sample_rate = sampleRate,
               codec,
               max_rms = rms,
           });
       }
   }
   public sealed record AudioFilterOptions
   {
       public long  MaxSizeBytes        { get; init; } = 200L * 1024 * 1024;  // ~10 min uncompressed PCM cap
       public int   MaxDurationSeconds  { get; init; } = 600;
       public float SilenceRmsThreshold { get; init; } = 0.005f;
       public int   SilenceWindowMs     { get; init; } = 100;
   }
   ```
   - `IFfprobeRunner` is a thin wrapper around `Process.Start("ffprobe", ...)` that parses `-show_format -show_streams -of json` output. ~40 LOC including process timeout (10 s).
   - `SilenceDetector.MaxRmsAsync` uses NAudio's `AudioFileReader` to read the WAV; for non-WAV inputs, ffmpeg the input to a temp WAV first (ffprobe identifies codec; for non-WAV the silence-VAD runs on a transcoded copy). This adds ~50 ms per non-WAV preflight; acceptable since voice memos are typically captured as WAV by the plugin already.
   - For audio fetched as `kind='audio'` with a non-WAV mime (e.g., `audio/mp3`), preflight fetches → transcodes-to-WAV-in-memory → reads RMS → discards. This is the only path that streams the full audio bytes through cloud-api process memory — bounded by the 200 MB size cap.

**3. `ParakeetWorker.cs` (existing) — modify to call preflight, like Pass A.**
   ```csharp
   protected override async Task<ExtractionOutcome> ExtractAsync(
       IParakeetClient client, Attachment att, CancellationToken ct)
   {
       var pre = await preflighter.CheckAsync(att, ct);
       if (!pre.ShouldExtract) return ExtractionOutcome.Skip(pre.SkipReason!, pre.Metadata);
       var result = await client.TranscribeAsync(att, ct);
       var extra = JsonMerge(pre.Metadata, new { language_detected = result.LanguageDetected });
       return ExtractionOutcome.Success(
           extractedText: result.Transcript,
           extra: extra,
           cacheKey: $"sha256:parakeet:parakeet-tdt-0.6b-v3:{models.ParakeetVersion}");
   }
   ```
   The `JsonMerge` helper combines preflight metadata + transcription metadata into a single `extra` JSONB blob.

**4. Config keys** appended to `appsettings.json`:
   ```json
   "IngestSaga": {
     "Filters": {
       "Audio": {
         "MaxSizeBytes": 209715200,
         "MaxDurationSeconds": 600,
         "SilenceRmsThreshold": 0.005,
         "SilenceWindowMs": 100
       }
     },
     "Models": {
       "Asr": { "ParakeetVersion": "int8" }
     }
   }
   ```

**5. Tests for Pass B:**
   - `Infrastructure/Sidecars/ParakeetHttpClientTests.cs` — `HttpMessageHandler` fake; multipart-form contains `url` field with the presigned URL; happy path returns `{text, language}`; 5xx → one retry → success; timeout → throws; 4xx → throws permanent.
   - `Infrastructure/Sidecars/Preflight/AudioPreflighterTests.cs` — happy path with a tiny WAV fixture (1 second of 440 Hz sine at 16 kHz mono, generated at fixture-build time — ~32 KB); silence reject (1 second of zeros); duration reject (a synthetic >600 s WAV — generate on the fly); size reject; no-audio-stream reject (an MP4 with only video — synthetic fixture).
   - `Infrastructure/Sidecars/FfprobeRunnerTests.cs` — actual ffprobe invocation on the fixture WAVs; assert parsed duration / codec / sample_rate match expectations. Skip on platforms where ffprobe isn't installed (CI Linux runners always have it once we install ffmpeg in the cloud-api Dockerfile in Pass C; for local dev mac, document the Homebrew install in `README.md`).
   - `Features/Processing/Specialists/ParakeetWorkerTests.cs` — wire real `ParakeetHttpClient` against a Testcontainers-hosted `ghcr.io/achetronic/parakeet:v0.3.0` (very slow — model load is ~5 s + ~10 s for a 10 s test clip). Mark `[Trait("Category","Slow")]`.
   - `Features/Processing/Specialists/ParakeetWorkerPreflightTests.cs` — fast tests with mock `IParakeetClient` to verify preflight short-circuits.

→ At end of Pass B: a `audio`-kind attachment with a real WAV flows through preflight → ParakeetHttpClient → returns real transcript. Silent WAVs short-circuit. Composite Markdown embeds the transcript.

### Pass C — `VideoSplitterWorker` + `FfmpegVideoSplitterClient` + integration anchor (~1.5 days)

The largest pass. New worker, new ffmpeg invocation pipeline, new child-attachment INSERT logic, new bucket upload path (`UploadBytesAsync`), Dockerfile change, and the cross-worker regression test.

**1. `Infrastructure/Sidecars/FfmpegVideoSplitterClient.cs` — NEW. Implements `IVideoSplitterClient` (interface already in `Infrastructure/Sidecars/IVideoSplitterClient.cs` from handoff #3).**
   ```csharp
   public sealed record VideoSplitInput(
       Attachment ParentVideo,
       string PresignedVideoUrl,
       int KeyframeIntervalSeconds,
       string DestinationBucketKeyPrefix);  // "notes/<note-id>/attachments/<parent-id>"

   public sealed record VideoSplitOutput(
       IReadOnlyList<KeyframeUpload> Keyframes,
       AudioUpload Audio,
       int VideoDurationSeconds,
       string VideoCodec);

   public sealed record KeyframeUpload(int Index, double OffsetSeconds, byte[] Bytes);
   public sealed record AudioUpload(byte[] WavBytes, double DurationSeconds);

   public sealed class FfmpegVideoSplitterClient(
       IFfmpegRunner ffmpeg,
       IFfprobeRunner ffprobe,
       IOptions<VideoFilterOptions> options,
       ILogger<FfmpegVideoSplitterClient> log) : IVideoSplitterClient
   {
       public async Task<VideoSplitOutput> SplitAsync(VideoSplitInput input, CancellationToken ct)
       {
           // 1. Download video to a process-temp dir (or stream stdin → ffmpeg).
           //    Choice: temp file. ffmpeg seeks faster on a real file; -i URL works
           //    but adds an ffmpeg-internal HTTP fetch path that's harder to debug.
           //    Trade: ~25 MB temp file for a 30 s 720p video. Cleanup in finally.
           var tempDir = Path.Combine(Path.GetTempPath(), $"tm-video-{input.ParentVideo.Id}");
           Directory.CreateDirectory(tempDir);
           try
           {
               var inputPath = Path.Combine(tempDir, "input");
               await DownloadAsync(input.PresignedVideoUrl, inputPath, ct);
               var probe = await ffprobe.ProbeVideoAsync(inputPath, ct);
               if (probe.DurationSeconds > options.Value.MaxDurationSeconds)
                   throw new VideoPreflightException(
                       $"duration {probe.DurationSeconds}s > max {options.Value.MaxDurationSeconds}s");

               // 2. Keyframes: fps=1/N → up to ceil(duration/N) frames.
               var framesDir = Path.Combine(tempDir, "frames");
               Directory.CreateDirectory(framesDir);
               await ffmpeg.RunAsync(
                   $"-i \"{inputPath}\" -vf fps=1/{input.KeyframeIntervalSeconds} " +
                   $"-q:v 4 \"{framesDir}/%03d.jpg\"", ct);
               var keyframeFiles = Directory.EnumerateFiles(framesDir, "*.jpg").OrderBy(p => p).ToList();
               var keyframes = keyframeFiles.Select((path, i) => new KeyframeUpload(
                   Index: i,
                   OffsetSeconds: i * input.KeyframeIntervalSeconds + input.KeyframeIntervalSeconds / 2.0,
                   Bytes: File.ReadAllBytes(path))).ToList();

               // 3. Audio: 16 kHz mono PCM WAV.
               var audioPath = Path.Combine(tempDir, "audio.wav");
               await ffmpeg.RunAsync(
                   $"-i \"{inputPath}\" -ac 1 -ar 16000 -c:a pcm_s16le -vn \"{audioPath}\"", ct);
               var audio = new AudioUpload(File.ReadAllBytes(audioPath), probe.DurationSeconds);

               return new VideoSplitOutput(keyframes, audio, (int)probe.DurationSeconds, probe.VideoCodec);
           }
           finally
           {
               try { Directory.Delete(tempDir, recursive: true); }
               catch (Exception ex) { log.LogWarning(ex, "video temp cleanup failed for {Id}", input.ParentVideo.Id); }
           }
       }
   }
   ```
   - `IFfmpegRunner.RunAsync(args, ct)` is a Process.Start wrapper that captures stdout/stderr, throws on non-zero exit code, has a 5-minute timeout. ~60 LOC; reusable across keyframe and audio invocations.
   - The temp dir cleanup is best-effort with a logged warning; persistent leaks would surface in disk-usage Prometheus.

**2. `Features/Processing/Specialists/VideoSplitterWorker.cs` — extend the existing scaffold from handoff #3.**
   The handoff-3 stub returns `new VideoSplitResult(KeyframeStorageKeys: [], AudioStorageKey: null)` immediately. The new worker:
   ```csharp
   public sealed class VideoSplitterWorker(
       IServiceProvider sp,
       IClock clock,
       IOptions<VideoFilterOptions> options,
       IFfprobeRunner ffprobe,
       IVideoSplitterClient splitter,
       IArtifactStore store,
       ILogger<VideoSplitterWorker> log)
       : SpecialistWorkerBase<IVideoSplitterClient>(sp, clock, /*...*/)
   {
       protected override string TargetSidecar => "video";

       protected override async Task<ExtractionOutcome> ExtractAsync(
           IVideoSplitterClient client, Attachment att, CancellationToken ct)
       {
           var (durationSec, codec) = await ProbeViaPresignedUrlAsync(att, ct);
           if (durationSec > options.Value.MaxDurationSeconds)
               return ExtractionOutcome.Skip($"duration {durationSec}s > max", new { duration_s = durationSec });
           if (codec is null)
               return ExtractionOutcome.Skip("no_decodable_video_stream", null);

           var expectedKey = $"sha256:video:ffmpeg:n-{options.Value.KeyframeIntervalSeconds}s";
           if (att.ExtractionCacheKey == expectedKey)
           {
               log.LogInformation("video {Id} already split with same interval; reusing children", att.Id);
               return ExtractionOutcome.Reuse(att.ExtractedText ?? "[video reused]", null, expectedKey);
           }

           // Re-split path: tear down any existing children atomically before splitting.
           // The FK CASCADE on parent_attachment_id handles children-of-children if any
           // (currently impossible — children are leaves — but defense in depth).
           await DeleteExistingChildrenAsync(att.Id, ct);

           var split = await client.SplitAsync(new VideoSplitInput(
               att,
               PresignedVideoUrl: (await store.IssueDownloadUrlAsync(att.StorageKey, TimeSpan.FromMinutes(5), ct)).Url.ToString(),
               KeyframeIntervalSeconds: options.Value.KeyframeIntervalSeconds,
               DestinationBucketKeyPrefix: $"notes/{att.NoteId}/attachments/{att.Id}"
           ), ct);

           var childAttachments = new List<Attachment>(split.Keyframes.Count + 1);
           foreach (var kf in split.Keyframes)
           {
               var key = $"notes/{att.NoteId}/attachments/{att.Id}/keyframes/{kf.Index:D3}.jpg";
               await store.UploadBytesAsync(key, kf.Bytes, "image/jpeg", ct);
               childAttachments.Add(BuildChildImageAttachment(att, kf, key));
           }
           {
               var key = $"notes/{att.NoteId}/attachments/{att.Id}/audio.wav";
               await store.UploadBytesAsync(key, split.Audio.WavBytes, "audio/wav", ct);
               childAttachments.Add(BuildChildAudioAttachment(att, split.Audio, key));
           }

           // INSERT children + INSERT extraction_tasks atomically.
           // Order matters: the base class will UPDATE the parent's extraction_task to
           // 'succeeded' immediately after this returns, so children must be visible
           // to the orchestrator's terminal-check BEFORE the parent flips.
           await dbContext.Attachments.AddRangeAsync(childAttachments, ct);
           foreach (var child in childAttachments)
           {
               dbContext.ExtractionTasks.Add(new ExtractionTask
               {
                   Id = Guid.CreateVersion7(),
                   IngestJobId = currentJobId,
                   AttachmentId = child.Id,
                   TargetSidecar = child.Kind switch
                   {
                       "image" => "ollama",
                       "audio" => "parakeet",
                       _ => throw new InvalidOperationException()
                   },
                   Status = "queued",
                   ScheduledAt = clock.GetCurrentInstant(),
                   CreatedAt = clock.GetCurrentInstant(),
                   UpdatedAt = clock.GetCurrentInstant(),
               });
           }
           await dbContext.SaveChangesAsync(ct);

           // Wake the relevant specialist workers via per-sidecar notify channels.
           await NotifyAsync("extraction_tasks_ollama_new");
           await NotifyAsync("extraction_tasks_parakeet_new");

           return ExtractionOutcome.Success(
               extractedText: $"[video split: {split.Keyframes.Count} keyframes + 1 audio]",
               extra: new
               {
                   keyframe_count = split.Keyframes.Count,
                   audio_duration_s = split.Audio.DurationSeconds,
                   frame_offsets = split.Keyframes.Select(k => k.OffsetSeconds).ToArray(),
                   keyframe_storage_keys = childAttachments
                       .Where(c => c.Kind == "image").Select(c => c.StorageKey).ToArray(),
                   audio_storage_key = childAttachments
                       .First(c => c.Kind == "audio").StorageKey,
                   video_codec = split.VideoCodec,
                   video_duration_s = split.VideoDurationSeconds,
               },
               cacheKey: expectedKey);
       }
   }
   ```
   - `ProbeViaPresignedUrlAsync` calls `ffprobe -i <presigned-url>` directly; ffprobe handles the HTTP fetch internally. Use the *presigned* URL (5-min TTL) so we don't have to download the video locally just for the probe.
   - `BuildChildImageAttachment(parent, kf, key)` constructs an `Attachment` with `note_id=parent.note_id`, `parent_attachment_id=parent.id`, `kind='image'`, `storage_key=key`, `mime_type='image/jpeg'`, `byte_size=kf.Bytes.Length`, `sha256=<computed>`, `extraction_status='pending'`, `extra={frame_index, offset_seconds}`.
   - `BuildChildAudioAttachment(parent, audio, key)` similar with `kind='audio'`, `mime_type='audio/wav'`, `extra={parent_video_id, duration_s}`.
   - `ExtractionOutcome.Reuse(...)` is a new variant that signals "the parent's row is already in a terminal state; just leave it alone." The base class interprets this as a no-op terminal write (don't touch `attachments`; don't touch `extraction_tasks`); the orchestrator's existing terminal-check picks up the (still-existing) children.

**3. `IArtifactStore` extension — `UploadBytesAsync` + `OpenReadAsync` + corresponding `S3ArtifactStore` impls.**
   ```csharp
   Task UploadBytesAsync(string key, byte[] bytes, string mimeType, CancellationToken ct);
   Task<Stream> OpenReadAsync(string key, CancellationToken ct);
   ```
   `S3ArtifactStore.UploadBytesAsync` calls `S3Client.PutObjectAsync(new PutObjectRequest { BucketName, Key, InputStream = new MemoryStream(bytes), ContentType = mimeType, /*tagging finalized=true to bypass the 24h lifecycle*/ })`. `OpenReadAsync` calls `GetObjectAsync(...).ResponseStream` and returns it (caller disposes). **Add tagging** `x-amz-tagging: finalized=true` on the PUT so the bucket-lifecycle rule from CLOUD-002 doesn't sweep these as orphans (per CLOUD-002 §1 the bucket has a `finalized=false → 24h delete` rule that the splitter's children must opt out of).

**4. Dockerfile change — install ffmpeg in the cloud-api image.**
   ```dockerfile
   # src/ThanyMarcus.Cloud.Api/Dockerfile (existing)
   FROM mcr.microsoft.com/dotnet/aspnet:10.0-bookworm-slim AS runtime
   RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg \
    && rm -rf /var/lib/apt/lists/*
   # ... existing copy + entrypoint ...
   ```
   - `ffmpeg` on Debian Bookworm pulls in ~95 MB of dependencies (ffprobe is shipped in the same package). Image size goes from ~220 MB to ~315 MB. Acceptable on the 50 GB droplet; the image is pulled once at cloud-init time.
   - Verify with `docker run --rm <new-image> ffmpeg -version` shows ffmpeg 5.x and `ffprobe -version` works. The Bookworm-slim ffmpeg is built without GPU codecs (no `--enable-cuda`, no `--enable-nvenc`) which is fine — we only use software codecs for keyframe JPEG + PCM WAV.

**5. Config keys** in `appsettings.json`:
   ```json
   "IngestSaga": {
     "Filters": {
       "Video": {
         "MaxDurationSeconds": 300,
         "KeyframeIntervalSeconds": 5,
         "MaxKeyframes": 60
       }
     },
     "Specialists": {
       "VideoSplitter": { "Replicas": 1 }
     }
   }
   ```

**6. Cross-worker regression test** `tests/ThanyMarcus.Cloud.Tests/CompositeIngestSagaEndToEndTests.cs` — **modify the existing test from handoff #3 Pass C** to add a video attachment to the 4-attachment composite and assert the splitter fan-out works:
   - Replace the synthetic 4-attachment fixture with 5: 1 URL, 1 image, 1 voice, 1 PDF, 1 video.
   - The video fixture is a tiny 10 s 320×240 mp4 generated at fixture-build time via `ffmpeg -f lavfi -i testsrc=duration=10:size=320x240:rate=10 -f lavfi -i sine=frequency=440:duration=10 -shortest fixture.mp4`. Pre-generate once, check into `tests/.../Fixtures/sample-10s.mp4` (~50 KB).
   - The test now asserts: the video task's `extraction_tasks` row goes `queued → processing → succeeded`; after that, two child `extraction_tasks` rows (for the 2 keyframes at intervals 5 s/10 s — actually 1 at offset 2.5 s and 1 at offset 7.5 s given fps=1/5) exist for the keyframes plus 1 for the audio; each child task progresses through `queued → processing → succeeded`; the orchestrator's "all terminal" check waits for ALL FIVE attachment tasks + THREE child tasks before advancing to `composing`.
   - SSE event sequence expected (in approximate order; race between concurrent specialist workers means the per-attachment events interleave):
     - `attachment_status_changed × 5 (top-level: pending → extracted-or-skipped)`
     - `attachment_status_changed × 3 (children: pending → extracted-or-skipped)`
     - `note_phase_changed (extracting_attachments → composing)`
     - … rest unchanged.
   - The total number of `attachment_status_changed` events = 5 top-level + 3 children = 8. Assert exactly this count.
   - Assert `notes.provenance.extraction_summary.video` = `{ total: 1, extracted: 1 }` (video parent counts as extracted because the splitter succeeded); `extraction_summary.image` = `{ total: 3, extracted: 3 }` (1 top-level image + 2 keyframe children — all using StubVlmClient since handoff #4 hasn't landed); etc.

**7. Tests for Pass C:**
   - `Infrastructure/Sidecars/FfmpegVideoSplitterClientTests.cs` — fake `IFfmpegRunner` + fake `IFfprobeRunner` to exercise the orchestration logic without invoking real ffmpeg; assert the right arg strings flow through; assert keyframe list assembly + audio assembly logic; happy path + duration-cap reject + no-video-stream reject.
   - `Infrastructure/Sidecars/FfmpegRunnerTests.cs` — actual ffmpeg invocation on the fixture mp4; one happy-path test that asserts output files exist + are non-empty. Mark `[Trait("Category","Slow")]`.
   - `Features/Processing/Specialists/VideoSplitterWorkerTests.cs` — fake `IVideoSplitterClient` returning canned keyframes + audio; assert N+1 child attachments INSERTed; assert N+1 extraction_tasks INSERTed with right `target_sidecar` per kind; assert parent task transitions to `succeeded`; assert child rows have `parent_attachment_id` populated; assert duplicate-call reuse path (when `extraction_cache_key` matches expected, no children inserted, no re-split).
   - `Features/Processing/Specialists/VideoSplitterReprocessTests.cs` — verify the re-split path: insert a video with pre-existing children, change `KeyframeIntervalSeconds` config, run worker, assert old children deleted (FK CASCADE) and new ones inserted; reprocess with same interval reuses.
   - `Infrastructure/Storage/S3ArtifactStoreUploadBytesTests.cs` — MinIO; PUT byte array → assert key exists, content matches, tagging includes `finalized=true`.

**8. Documentation update.** A short paragraph in `docs/architecture.md` (or inline pointer to this plan) describing the video-splitter fan-out — "video as composite extraction, decomposed into image children + audio child via local ffmpeg." One paragraph; the canonical doc is ADR-0043 §"Video as composite extraction."

→ At end of Pass C: composite-ingest pipeline handles 5 attachment kinds end-to-end with real processors for URL (existing CLOUD-002), Document (Docling), Audio (Parakeet), Video (Splitter + child cascade). Image remains stubbed pending handoff #4; the integration test passes with stub VLM emitting canned text.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   ├── 0042-cloud-ingest-pipeline-architecture.md           # (read-only)
│   ├── 0043-cloud-side-model-lineup.md                      # (read-only)
│   └── 0045-composite-note-schema.md                        # (read-only)
├── plans/
│   └── cloud-processors-heavy-handoff.md                    # THIS FILE
├── src/ThanyMarcus.Cloud.Api/
│   ├── Dockerfile                                           # CHANGED: + apt-get install ffmpeg
│   ├── Program.cs                                           # CHANGED: swap DI registrations:
│   │                                                        #   - services.AddSingleton<IDoclingClient, StubDoclingClient>()
│   │                                                        #   + services.AddHttpClient<IDoclingClient, DoclingHttpClient>(...)
│   │                                                        #   - services.AddSingleton<IParakeetClient, StubParakeetClient>()
│   │                                                        #   + services.AddHttpClient<IParakeetClient, ParakeetHttpClient>(...)
│   │                                                        #   - services.AddSingleton<IVideoSplitterClient, StubVideoSplitterClient>()
│   │                                                        #   + services.AddSingleton<IVideoSplitterClient, FfmpegVideoSplitterClient>()
│   │                                                        #   + services.AddSingleton<IFfmpegRunner, FfmpegRunner>()
│   │                                                        #   + services.AddSingleton<IFfprobeRunner, FfprobeRunner>()
│   │                                                        #   + services.AddSingleton<IDocumentPreflighter, PdfPreflighter>()
│   │                                                        #   + services.AddSingleton<IAudioPreflighter, AudioPreflighter>()
│   │                                                        #   + Configure<DocumentFilterOptions>, AudioFilterOptions, VideoFilterOptions
│   ├── appsettings.json                                     # CHANGED: + IngestSaga:Filters:{Document,Audio,Video}:*; + Models:Asr:ParakeetVersion + Docs:DoclingVersion
│   ├── Features/Processing/Specialists/
│   │   ├── DoclingWorker.cs                                 # CHANGED: ExtractAsync calls preflight; returns ExtractionOutcome
│   │   ├── ParakeetWorker.cs                                # CHANGED: same shape
│   │   ├── VideoSplitterWorker.cs                           # CHANGED: real splitter logic, child INSERTs, cache-hit reuse
│   │   ├── VlmWorker.cs                                     # CHANGED: signature update to ExtractionOutcome (still calls stub)
│   │   ├── UrlFetcherWorker.cs                              # CHANGED: signature update to ExtractionOutcome (still uses UrlExtractor or stub per handoff #3 choice)
│   │   ├── SpecialistWorkerBase.cs                          # CHANGED: ExtractAsync signature → Task<ExtractionOutcome>; outcome.Reuse path handling
│   │   └── ExtractionOutcome.cs                             # NEW: discriminated record (Success | Skip | Fail | Reuse)
│   └── Infrastructure/
│       ├── Sidecars/
│       │   ├── DoclingHttpClient.cs                         # NEW
│       │   ├── ParakeetHttpClient.cs                        # NEW
│       │   ├── FfmpegVideoSplitterClient.cs                 # NEW
│       │   ├── DoclingOptions.cs                            # NEW (BaseUrl, etc.; mirrors existing sidecar options)
│       │   ├── ParakeetOptions.cs                           # NEW
│       │   ├── VideoFilterOptions.cs                        # NEW
│       │   ├── Stubs/
│       │   │   ├── StubDoclingClient.cs                     # DELETED (replaced by real)
│       │   │   ├── StubParakeetClient.cs                    # DELETED
│       │   │   └── StubVideoSplitterClient.cs               # DELETED
│       │   └── Preflight/                                   # NEW directory
│       │       ├── IDocumentPreflighter.cs
│       │       ├── PdfPreflighter.cs
│       │       ├── DocumentFilterOptions.cs
│       │       ├── IAudioPreflighter.cs
│       │       ├── AudioPreflighter.cs
│       │       ├── AudioFilterOptions.cs
│       │       ├── SilenceDetector.cs                       # NAudio-based RMS scan
│       │       └── PreflightResult.cs
│       ├── Ffmpeg/                                          # NEW directory
│       │   ├── IFfmpegRunner.cs
│       │   ├── FfmpegRunner.cs                              # Process.Start wrapper
│       │   ├── IFfprobeRunner.cs
│       │   └── FfprobeRunner.cs
│       └── Storage/
│           ├── IArtifactStore.cs                            # CHANGED: + UploadBytesAsync, + OpenReadAsync
│           └── S3ArtifactStore.cs                           # CHANGED: implement the two new methods
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Fixtures/                                            # NEW (or extended)
    │   ├── sample-5-page.pdf
    │   ├── sample-440hz-1s.wav
    │   ├── sample-silent-1s.wav
    │   └── sample-10s.mp4
    ├── Infrastructure/
    │   ├── Sidecars/
    │   │   ├── DoclingHttpClientTests.cs                    # NEW (HttpMessageHandler fake)
    │   │   ├── ParakeetHttpClientTests.cs                   # NEW
    │   │   ├── FfmpegVideoSplitterClientTests.cs            # NEW (fake ffmpeg)
    │   │   └── Preflight/
    │   │       ├── PdfPreflighterTests.cs                   # NEW
    │   │       └── AudioPreflighterTests.cs                 # NEW
    │   ├── Ffmpeg/
    │   │   ├── FfmpegRunnerTests.cs                         # NEW [Trait Slow]
    │   │   └── FfprobeRunnerTests.cs                        # NEW [Trait Slow]
    │   └── Storage/
    │       └── S3ArtifactStoreUploadBytesTests.cs           # NEW
    ├── Features/Processing/Specialists/
    │   ├── DoclingWorkerTests.cs                            # NEW [Trait Slow against real Docling container]
    │   ├── DoclingWorkerPreflightTests.cs                   # NEW (fast)
    │   ├── ParakeetWorkerTests.cs                           # NEW [Trait Slow]
    │   ├── ParakeetWorkerPreflightTests.cs                  # NEW
    │   ├── VideoSplitterWorkerTests.cs                      # NEW (fast; fake splitter client)
    │   └── VideoSplitterReprocessTests.cs                   # NEW
    └── CompositeIngestSagaEndToEndTests.cs                  # CHANGED: 5-attachment fixture incl. video; assertion deltas in §"Pass C"
```

## Acceptance criteria

1. ✅ `dotnet build` clean under warnings-as-errors. `dotnet test --filter "Category!=Slow"` green in CI default lane; `dotnet test` runs the slow-trait set against Testcontainers-hosted Docling + Parakeet + real ffmpeg in the nightly lane.
2. ✅ `Program.cs` DI swaps verified: a `dotnet run` against the dev Docker Compose (handoff #2's local-dev mirror) sees the cloud-api process resolve `IDoclingClient = DoclingHttpClient`, `IParakeetClient = ParakeetHttpClient`, `IVideoSplitterClient = FfmpegVideoSplitterClient`. The three `Stub*` files are deleted; no compile-time reference remains.
3. ✅ Dockerfile-installed ffmpeg verified: `docker build -t cloud-api:test src/ThanyMarcus.Cloud.Api && docker run --rm --entrypoint /bin/sh cloud-api:test -c "ffmpeg -version && ffprobe -version"` prints versions. CI runs this check as a smoke step.
4. ✅ Manual smoke against a freshly-provisioned cloud (with handoffs #1, #2, #3 applied):
    ```bash
    # 1. PDF capture
    # Init + presigned PUT + finalize for a 3-page test PDF.
    # SSE shows: attachment_status_changed (pending → extracted) for the PDF.
    # /api/sync/pull?include=provenance shows extracted_text containing the PDF text.

    # 2. Voice memo capture
    # Init + PUT a 30 s WAV + finalize.
    # SSE shows: attachment_status_changed for the WAV.
    # Pull shows extracted_text = the transcript.

    # 3. Silent voice memo capture
    # Same flow, but the WAV is 1 second of silence.
    # SSE shows: attachment_status_changed (pending → skipped).
    # Pull shows extraction_status=skipped + extraction_error="silent_rms_*".

    # 4. Video capture
    # PUT a 10 s test mp4 + finalize.
    # SSE shows: attachment_status_changed (video: pending → extracted), then
    #          attachment_status_changed × 3 (children: pending → extracted),
    #          then note_phase_changed (extracting_attachments → composing).
    # Pull shows the parent attachment with extra={keyframe_count: 2, audio_duration_s: 10, ...}
    #   and (when handoff #6 lands UrlFetcher's re-route or composer renders children) the
    #   composite body shows 2 keyframe stub-descriptions + 1 audio transcript.
    ```
5. ✅ `CompositeIngestSagaEndToEndTests.cs` is green: 5-attachment composite → SSE event sequence (8 attachment_status_changed events) → /sync/pull with provenance → reprocess (cache hit on Docling + Parakeet; video re-uses children) → DELETE → second reprocess returns 404. Cache-hit reprocess completes in <200 ms.
6. ✅ Per-worker preflight reject paths verified: `extraction_status='skipped'` rows show up with the expected `extraction_error` strings (`size`, `pages`, `duration`, `silent_rms_*`, `no_decodable_*`). The SSE event vocabulary includes the `to='skipped'` event payload.
7. ✅ `IArtifactStore.UploadBytesAsync` writes bytes to MinIO with `x-amz-tagging: finalized=true` (verified by HEAD on the key returning the tag). `OpenReadAsync` reads the same bytes back.
8. ✅ Video splitter cache-hit (re-extract with same `KeyframeIntervalSeconds`): no children re-inserted, parent task `extraction_cache_key` matches. Verified by `VideoSplitterReprocessTests.cs`.
9. ✅ Video splitter re-split (different `KeyframeIntervalSeconds`): old children CASCADE-deleted, new children inserted, old extraction_tasks gone (FK CASCADE from attachments → extraction_tasks).
10. ✅ Concurrency: with two `VideoSplitterWorker` replicas (`IngestSaga:Specialists:VideoSplitter:Replicas=2` in dev override), two concurrent video jobs split in parallel without stomping each other's temp dirs (each splitter's temp dir is keyed on `att.Id`). Verified by `VideoSplitterWorkerTests.cs` claim-contention test.
11. ✅ Failure cascade preserved: a `DoclingClientException` on a 3-attachment composite (PDF fails, voice memo OK, URL OK) → `notes.status='ready'` with `extraction_status='failed'` on the PDF row + `extraction_error` populated; composing still runs; the note lands without PDF content.
12. ✅ Polly retry behavior: Docling 503 once → retry succeeds; Docling 503 thrice → `failed` terminal. Parakeet 503 once → retry succeeds; Parakeet timeout → `failed` immediately (no retry on timeout per design decision #1).
13. ✅ Slow-test gate enforced: `dotnet test --filter "Category!=Slow"` runs in <2 min on CI. The slow tests (real Docling + real Parakeet + real ffmpeg) run in the nightly lane (~10 min).
14. ✅ No `// TODO` markers in shipped code. Allowed `// FORK:` markers: at `VlmWorker.ExtractAsync` (forks to handoff #4 swap), at the orchestrator's child-task notify (forks to per-sidecar notify channel optimization if needed), at `composer-doesn't-yet-render-video-children` (forks to CLOUD-COMPOSITION handoff #4 or follow-up).

## Out of scope (named explicitly)

1. ❌ **`StubVlmClient` swap to `OllamaVlmClient`.** Handoff #4. Video keyframe children continue to route to the stub.
2. ❌ **Real LLM prompts for routing + entity extraction.** Handoff #4. `NoOpLlmClient` continues to drive `routing` + `extracting_entities` phases.
3. ❌ **Real Granite Embedding ONNX.** Handoff #6. `StubEmbeddingClient` continues to return zero vectors.
4. ❌ **Composite Markdown rendering of video children as a multi-frame block.** The `CompositeMarkdownAssembler` may either ignore children (default) or render them as a flat list at the bottom (acceptable interim). The rich multi-frame block layout is handoff #4 or #5 follow-up — the data is in the DB, the rendering is a separate concern.
5. ❌ **Scene-aware keyframe detection.** Constant-interval `fps=1/N` only. ADR-0042 §"Deferred future-work hooks" mentions smarter detection; not required for thesis MVP and ffmpeg's `-vf select='gt(scene,0.4)'` adds noticeable extra CPU on the 4 vCPU droplet.
6. ❌ **Parakeet streaming transcription.** Per ADR-0042 §"Deferred future-work hooks" item 3. The current call is full-clip, returns full transcript at the end; partial-results streaming to SSE is a later optimization.
7. ❌ **Whisper.cpp fallback.** ADR-0043 lists whisper.cpp as a fallback if Parakeet RAM proves too high in eval. This ticket only wires Parakeet; if eval surfaces a problem, the fallback is a second `IParakeetClient` impl plus a config switch.
8. ❌ **URL fetcher real impl** (`RealUrlFetcherClient`). Handoff #6. The existing `UrlExtractor` from CLOUD-002 still serves the URL kind; integrated via whichever DI choice handoff #3 made (the in-place CLOUD-002 extractor + stub-only-for-tests pattern).
9. ❌ **DOCX, PPTX, XLSX preflight.** Docling supports them; we send them through without PdfPig preflight (size cap only). Per-format preflight (e.g., extracting slide count from PPTX) is a future-work hook if eval shows long-tail captures need it.
10. ❌ **Encrypted PDF support.** PdfPig throws `PdfDocumentEncryptedException` on encrypted PDFs; preflight skips with `pdf_encrypted` reason. We do not attempt password-prompting or PDFsharp's read-only-encrypted-content path. Document in user-facing error: "encrypted PDF; remove password before capturing."
11. ❌ **Video transcoding to a uniform input format.** ffmpeg accepts most container/codec combos directly; we do not pre-transcode. If ffmpeg can't decode (unknown codec), preflight skips with `no_decodable_video_stream`. The `mem_limit` on cloud-api accommodates ffmpeg's small transient footprint per ADR-0043's hardware contract.
12. ❌ **GPU-accelerated ffmpeg.** The base image's ffmpeg is software-only. GPU acceleration is irrelevant on the no-GPU thesis droplet.
13. ❌ **Hub regen path for videos.** Hub regen jobs don't touch attachments; this ticket's logic is irrelevant to the hub regen flow. Handoff #4 or later.
14. ❌ **Cross-cloud / multi-tenant fan-out.** Each user cloud splits its own videos; no shared compute. The thesis architecture is one cloud per user.

## Risks and gotchas

- **Child INSERTs must precede parent's terminal flip.** Design decision #8 explicitly. The mistake to avoid: returning `ExtractionOutcome.Success` from `VideoSplitterWorker.ExtractAsync` BEFORE the children have been saved. The base class writes the parent's terminal state on return — if children aren't in the DB yet, the orchestrator's next claim sees "no unterminal tasks" and advances. Either commit children before returning (current design — single transaction inside `ExtractAsync`) or change `SpecialistWorkerBase` to accept a `pendingChildren` parameter on success and commit them transactionally with the parent flip. The former is simpler and the current plan; the latter would need a non-trivial change to `SpecialistWorkerBase` — avoid scope creep.

- **Cache-hit on video re-split.** When the orchestrator re-runs a video task (e.g., from a reprocess), the splitter must NOT re-split if `extraction_cache_key` matches. Returning `ExtractionOutcome.Reuse` is the signal. The base class interprets `Reuse` as "don't touch `attachments` row; don't touch `extraction_tasks` row except to mark `succeeded`." If the row was already `succeeded`, this is idempotent. Verify with `VideoSplitterReprocessTests.cs`.

- **`IArtifactStore.UploadBytesAsync` race with bucket lifecycle.** The bucket-lifecycle rule from CLOUD-002 deletes objects tagged `finalized=false` (or missing) after 24h. Children uploaded by the splitter must be tagged `finalized=true` AT UPLOAD TIME, not later. PUT-then-PUT-tagging is two round-trips and races the lifecycle; the AWSSDK.S3 `PutObjectRequest.TagSet` field sets the tag during the initial PUT. Use `TagSet` always.

- **ffmpeg/ffprobe binary version mismatch.** Bookworm-slim ffmpeg is 5.x as of 2026-05-19. If the apt index moves to ffmpeg 7.x and any of our argument strings break (rare — the basic `-i`, `-vf`, `-ac`, `-ar`, `-c:a` flags are stable across major versions), the slow Testcontainers test catches it nightly. Pin to a known-good Bookworm point release if breakage becomes a recurring issue.

- **Temp dir cleanup on cloud-api crash.** If the cloud-api process dies mid-split, `/tmp/tm-video-*` directories leak. On the 50 GB droplet, 100 MB per leaked split adds up if leaks are persistent. Mitigation: a cloud-init `systemd-tmpfiles` rule that cleans `/tmp/tm-video-*` older than 1h. Implement as a one-line systemd config drop in handoff #2's cloud-init template — OR document the leak and rely on container restart to wipe `/tmp`. The container restart approach is cheaper; pick that and document in `risks-and-unknowns`.

- **PdfPig stream consumption.** `PdfDocument.Open(stream)` reads the entire stream into memory by default. For a 50 MB PDF this is 50 MB of cloud-api process RSS; acceptable under the `mem_limit` budget but worth noting. If preflight RAM becomes a problem, use `PdfDocument.Open(stream, ParsingOptions.LazyLoading=true)` (verify the option exists in PdfPig 0.1.10 — older versions don't expose it).

- **NAudio MP3 support.** NAudio's MP3 decoder requires native dependencies on Linux that may not be in the runtime image. If the plugin sends `audio/mp3` rather than `audio/wav`, the silence-VAD's NAudio reader fails. Mitigation: transcode-to-WAV-in-memory via ffmpeg before NAudio reads — already in the AudioPreflighter design. Verify in CI by including an mp3 fixture.

- **Parakeet response shape variance.** The achetronic/parakeet README documents `{ text, language }`; the actual response on `v0.3.0` may include extra fields (`segments`, `confidence`, etc.) or lack `language`. Verify against a real call before locking the deserializer; use `JsonElement` rather than a strict record if the shape is fluid. The `null`-tolerant `ParakeetResponse.Language` field handles the missing case gracefully.

- **Docling endpoint version (`/v1alpha` vs `/v1`).** The docling-serve v0.5.0 image we pin in handoff #2 ships `/v1alpha/convert/source`. A future image bump to `/v1` will require updating `DoclingHttpClient`'s endpoint path. Document in `DoclingOptions.cs` with a single non-obvious-why comment: `// Path may shift to /v1 in a future docling-serve image; pin via appsettings if needed`. Add `DoclingOptions.ConvertSourcePath` (default `/v1alpha/convert/source`) so config can override without rebuild.

- **`extraction_cache_key` for video reuse only works when intent matches.** A reprocess with `KeyframeIntervalSeconds` unchanged hits the cache and the splitter returns `ExtractionOutcome.Reuse`. A reprocess with the interval changed re-splits and discards old children. There's no "preserve old children for comparison" mode — the cache is a single value. Acceptable for thesis; if eval wants A/B keyframe-interval comparison, instrumented re-splits are a manual operation.

- **Concurrent multi-video burst.** With one VideoSplitterWorker replica, two videos finalized within seconds queue serially; the second video waits for the first's ~10–20 s ffmpeg pass. SSE clients see the second video stuck in `queued` until the first finishes. **Acceptable** for the thesis-load profile; if burst captures become common, bump replicas to 2 (per-worker ffmpeg process at a time is still 1).

- **The cross-worker integration test depends on three real sidecars + MinIO.** ~30 s container startup overhead before assertions run. Mark the test `[Trait("Category","Slow")]`; the CI default lane runs only the fast tests. The nightly lane runs Slow + asserts the wall-clock budget (5-attachment composite end-to-end within ~3 min including container startup) — this is a coarse regression catch for "did some sidecar grow slow?"

- **`ExtractionOutcome` signature cascade.** All five workers' `ExtractAsync` change from `Task<string>` to `Task<ExtractionOutcome>`. The three real workers (this ticket) ship updated bodies; the two stub-only workers (`VlmWorker`, `UrlFetcherWorker`) need their bodies wrapped in `ExtractionOutcome.Success(text, null, $"sha256:stub:{kind}:v1")`. Don't forget the wrapping when adapting — and don't accidentally let the build "succeed" by leaving the wrapping with the literal `"v1"` cache-key in a real worker.

- **Slow-test container churn.** Each `[Trait("Category","Slow")]` test starts a fresh `quay.io/docling-project/docling-serve:v0.5.0-cpu` container (~30 s warmup). Five Docling tests = 2.5 min wasted on warmup. Mitigation: collection-fixture lifecycle (`ICollectionFixture<DoclingFixture>`) shares one container across all tests in the collection. Same for Parakeet. Worth the ~50 LOC of fixture plumbing.

- **`feedback_no_code_comments` discipline.** The handoff has several places where the implementation needs a one-line "non-obvious why" — e.g., `DoclingOptions.ConvertSourcePath` versioning comment, `SilenceRmsThreshold` tuning comment, `// FORK:` markers. Keep each to one line; nothing decorative; nothing that just restates the code.

## Open contract decisions (carry forward)

1. **Composer rendering for video children.** This ticket lands the data; the renderer is the next concern. Handoff #4 or a follow-up "CLOUD-COMPOSITION-VIDEO-BLOCK" ticket consumes `attachments.extra.frame_offsets` + child `extracted_text` to produce the multi-frame Markdown block. The DB shape is sufficient — no schema change.

2. **Real keyframe quality tuning.** JPEG quality 85 + JPEG over PNG is locked here. Eval may want PNG for fewer compression artifacts on text-heavy keyframes (slides, code screenshots). If so, switch the format via `IngestSaga:Filters:Video:KeyframeFormat={jpeg|png}` — small config change, no schema impact.

3. **Silence-VAD false-positive rate.** The 0.005 RMS threshold is informed-guess; eval against real voice memos will reveal the right value. Whispered captures are the at-risk case. Tunable via `IngestSaga:Filters:Audio:SilenceRmsThreshold` without code change.

4. **Page-count threshold for documents.** 200 pages is the soft cap; a thesis PDF or textbook might exceed it. If users routinely capture 300-page PDFs, bump the cap (the Docling sidecar can handle them; the limit is wall-clock latency — ~2 min for 200 pages on the CPU droplet). Tunable.

5. **Parakeet response language detection.** The README claims the model is multilingual; verify the response body actually includes a `language` field in the v0.3.0 image. If absent, leave `extra.language_detected = null` and add language detection as a separate preflight (whisper-lid or a tiny classical model) in a future ticket.

6. **Video duration cap.** 5 min is the ADR default; eval against real video captures may want longer. The cap is in config; bumping it to 10 min is a one-line change. The ffmpeg invocations scale linearly; a 10 min video produces ~120 keyframes at default interval + 60 s of audio.

7. **Per-keyframe storage-key naming.** `notes/<note-id>/attachments/<parent-id>/keyframes/<i>.jpg` is symmetric with the audio key. Listing all keyframes for a video is `LIKE 'notes/<note-id>/attachments/<parent-id>/keyframes/%'` — supported by Spaces. If a future "show me all videos with > 10 keyframes" query needs efficient listing, the bucket-side prefix is already structured correctly.

8. **ADRs to author** (0.25-day batch after this ticket ships, NOT in this ticket's scope):
   - **ADR-0048: Local-process video splitting + child-attachment fan-out.** Captures the design decisions #3, #7, #8 (Process.Start over Xabe, child-after-parent INSERT, cascade semantics) for the architecture chapter.
   - **ADR-0049: Classical preflight filters as a staged-rejection lever.** Captures the "garbage in costs 10 ms not 45 s" hypothesis; eval chapter will measure the actual saved time.

## What handoff #6 inherits

After this ticket lands:
- Four of five attachment kinds (`document`, `audio`, `video`, `url`) run on real processors. The `image` kind is the only remaining stub; handoff #4 swaps it (alongside the real LLM client).
- The `ExtractionOutcome` discriminated record is the standard return shape across all five specialists; handoff #4/#6 don't need to invent new outcome types.
- The `IArtifactStore.UploadBytesAsync` + `OpenReadAsync` extensions exist; future workers needing in-process server-to-bucket transfers (e.g., a thumbnail generator on images) have them ready.
- The classical preflight pattern is established (Document, Audio, Video each have their own preflighter); future-modality preflighters follow the same shape.
- ffmpeg + ffprobe are in the image and exposed via `IFfmpegRunner` + `IFfprobeRunner`. Future use cases (e.g., image thumbnailing, audio waveform extraction) can reuse the runners.
- Slow-test infrastructure (`[Trait("Category","Slow")]` + collection-fixture sharing) is in place; handoff #4's real-VLM tests follow the same pattern.

The cumulative arc through M5:

```
CLOUD-SCHEMA-V2          (#1, shipped)  — entities, mentions, extraction_tasks, embedding columns
CLOUD-SIDECARS           (#2, shipped)  — Ollama + docling-serve + parakeet-server on the cloud
CLOUD-INGEST-SAGA-FOUND. (#3, shipped)  — Orchestrator + 5 specialists + SSE + stub clients
CLOUD-VLM-WORKER + LLM   (#4)          ▶ swap StubVlmClient + NoOpLlmClient for real
CLOUD-PROCESSORS-HEAVY   (#5, THIS)   — Docling + Parakeet + VideoSplitter real
CLOUD-EMBEDDING + URL    (#6)         ▶ swap StubEmbeddingClient + StubUrlFetcherClient
─► [M5 demo: composite capture with URL + image + voice + PDF + video → vault with real extracted content]
CLOUD-COMPOSITION-VIDEO  (#7 if needed) ▶ multi-frame Markdown block for video children
─► [M6 demo: emergent graph from accumulated captures]
```

This ticket is the **heavy lifting** of the M5 demo's processor pipeline: three of the four "real workers" handoffs sit here, with the VLM/LLM split into handoff #4 because the VLM is also the LLM (per ADR-0043 §"Single model for VLM + routing" — MiniCPM-V serves both image-description and structured-JSON routing). After this ticket, every kind has a real processing path, and the demo's content-extraction quality is bottlenecked only by the model choices (not the wiring). Handoff #4 then makes images come alive, and handoff #6 finishes the loop with real embeddings + URL fetcher + the demo-day composition polish.
