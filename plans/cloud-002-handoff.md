# CLOUD-002 Composite-ingest spine + per-user blob bucket — Handoff Brief

Date: 2026-05-18
Status: Draft (companion to `cloud-001-handoff.md` which scaffolded Cloud.Api + bootstrap handshake, and `cloud-001-amendment-nginx-handoff.md` which replaced Caddy with nginx + certbot on the user cloud).

**Goal:** land the first end-to-end **composite-ingest** pipeline — the user drafts a quick note in Obsidian with N attachments (URLs, images, voice memos, files), the plugin posts that composite payload to their cloud, and a single processed Markdown note materializes back in their local vault. This is the walking-skeleton implementation of FR3 (in-cloud multimodal processing) and FR4 (local-vault materialization), with one structured LLM call per *note* (S4 lock, reframed from "per artifact" to "per composite note" given the composite-ingest decision recorded in memory on 2026-05-17).

Estimated **3 person-days** with AI-agent assistance, split into two passes (~1.5d each). After this ticket, the slice `plugin → presigned upload → bucket → saga → LLM → Markdown → sync pull → vault` is proven end-to-end on real DO infrastructure with real per-user DO Spaces buckets and a real LLM call. Image OCR + voice transcription remain stubbed (deferred to CLOUD-003 / CLOUD-016b/c); URL attachments get real Readability extraction in this ticket so the LLM call sees real extracted text.

This handoff **supersedes** the earlier `cloud-002-handoff.md` draft (text-only walking skeleton, per-artifact endpoint). The pivot reasons are summarized in the "Why the previous draft was wrong" section at the bottom; do not consult it.

## Where decisions live (read before doing anything)

- **`plans/cloud-pivot-plan-2026-05-13.md`** — §1 (transformation diagram lines 20–41), §9 (plugin commands), §11 (cloud backend endpoints), §12 (processor router and saga — S4 lock: one structured LLM call per note), §13 (safe / unsafe LLM strategy), §22 (provenance schema), §25 (MVP scope). **The contract this ticket implements.**
- **Memory `composite_ingest_decision.md`** — 2026-05-17 lock: `/api/ingest` takes one composite draft (body + N attachments) → one processed note. Supersedes the per-artifact ingest in the cloud-pivot plan. This ticket is the implementation.
- **Memory `user_cloud_reverse_proxy.md`** — 2026-05-17 lock: nginx-on-host + certbot `--deploy-hook` on user cloud. The Cloud.Api binds to localhost; nginx terminates TLS and proxies. Already shipped in `cloud-001-amendment-nginx-handoff.md` and verified through smoke #5 + #6.
- **`plans/cloud-001-handoff.md`** — established the project, directory structure, bootstrap callback, the `CloudDbContext` shape (currently empty). CLOUD-002 is the first ticket to populate `CloudDbContext` and the first to fill any of the `Features/{Ingest,Processing,Sync,PluginAuth,Settings,Storage}/.gitkeep` placeholders.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — Postgres job queue + `FOR UPDATE SKIP LOCKED` + lease + mutable status. **Applies as-is** to the processing saga.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the provisioning saga's *state machine* is the *blueprint* for the processing saga shape (phased status, mutable transitions, ownership predicate, lease/heartbeat, `LISTEN`/`NOTIFY` wakeups). The processing saga is **simpler** (no rollback path; failures retry then dead-letter). Copy the patterns, not the code.
- **`docs/decisions/0024-dbcontext-shape.md`** — single `CloudDbContext` with per-feature `IEntityTypeConfiguration<T>`.
- **`docs/decisions/0028-schema-conventions.md`** — snake_case via `EFCore.NamingConventions`, `created_at` everywhere + `updated_at` on mutable rows, app calls `db.Database.MigrateAsync()` at startup (migration runner pattern A).
- **`docs/decisions/0025-openapi-and-hand-written-ts-contracts.md`** — endpoints get `.WithName(...)` + `.Produces<>` + `.ProducesProblem(...)`; TS types for the plugin are hand-written in `ThanyMarcus.Shared` per ADR-0025.
- **`docs/decisions/0023-test-stack.md`** + **`docs/decisions/0028-schema-conventions.md` §2** — xUnit v3 + Shouldly + Testcontainers via `PostgresFixture` + `Respawn`. The Cloud.Tests project already has `PostgresFixture` + `CloudApiFactory` from CLOUD-001.
- **`docs/decisions/0035-burst-worker-llm-tier.md`** — safe-mode Ollama on burst worker explicitly **out of scope here**; this ticket lands unsafe mode (Anthropic direct) only.
- **Memory `portal_architecture.md`** — Postgres job queue, mutable status, no event sourcing, SSE only — applies to cloud.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly+xUnit v3, OpenAPI+Scalar, `/health/live+ready`.
- **Memory `thesis_context.md`** — S4 lock (one structured LLM call per note); the LLM is the only thing the cloud does that the client cannot.

**Do not invent new modalities, new auth schemes, or pgvector usage in this ticket.** Composite ingest (one body + N attachments) → presigned upload → saga → URL extraction (real) + image/voice extraction (NotImpl-yet) → one LLM call → procedural Markdown → sync pull with inline signed-GET URLs. Everything else is a later ticket.

## Design decisions resolved in the 2026-05-18 grilling

Recorded here so the next reader does not re-litigate them. References are to the grilling exchange; recall via memory or the plans transcript.

1. **Single `notes` table with states** (pending → processing → ready → failed). No separate `drafts` table; "draft" is internal terminology only.
2. **Per-user blob bucket** provisioned by terraform on cloud-init. DO Spaces in MVP; Azure Blob ships when the Azure provider does (PORTAL-009). Same `IArtifactStore` interface, different impl per provider.
3. **Single `attachments` table** + `kind` discriminator (`url|image|voice|file`) + JSONB `extra` for per-kind metadata. Bytes are NOT in DB; only metadata is.
4. **No pgvector in CLOUD-002.** Extension install + `embeddings` table land in the ticket that writes to them (CLOUD-010).
5. **Presigned direct-to-bucket uploads.** Two-phase: `POST /api/ingest/init` returns presigned PUT URLs; client uploads bytes directly to the bucket; `POST /api/ingest/{noteId}/finalize` verifies + enqueues the saga. Bytes never traverse the Cloud.Api process.
6. **Limits**: 50 attachments per note, 5 GB per single attachment (S3 single-PUT max), 20 GB per note total, presigned URL TTL 15 min. Init/finalize JSON envelopes capped at 1 MB. Orphan sweeper deletes `pending` notes with no `finalize` after 1 hour.
7. **Idempotency on `notes.client_note_id`** (plugin-supplied UUID, stable per capture).
8. **Saga**: one structured LLM call per note, after all extractions complete. Per-attachment extraction failure flags the row (`extraction_status='failed'`) but does NOT fail the note. The LLM sees whatever extractions succeeded.
9. **Bucket key layout**: `notes/<note_id>/<attachment_id>.<ext>` where ext is derived server-side from `mime_type` via a deterministic table.
10. **Plugin downloads attachments to the local vault on sync.** Sync-pull response carries presigned GET URLs (1-hour TTL) inline. Cloud-side Markdown uses `![[attachment:<id>]]` placeholders; the plugin substitutes local paths after download. Vault is self-sufficient after sync (preserves "local vault survives cloud destroy").
11. **Plugin never sees `pending` or `processing` notes.** Sync pull filters `WHERE status='ready'`.
12. **`notes.provenance` is a JSONB column.** CLOUD-002 populates `{ model, generated_at, input_tokens, output_tokens, anchor_count, extraction_failures }`. Full §22 provenance schema lands in CLOUD-024.
13. **Plugin auth unchanged**: static bearer token in `plugin_tokens` table. Presigned URLs use S3 SigV4 (separate auth, scoped to one operation). Two-layer auth, clean separation.
14. **Single migration** `0001_composite_ingest_spine.cs` covering notes, attachments, ingest_jobs, plugin_tokens, cloud_settings. CLOUD-001 left the DB empty; no value in splitting.

## Scope boundary (precise)

Two passes, ~1.5 days each. Both ship in the same handoff because the schema is useless without the saga, and the saga is incomprehensible without the LLM call.

### Pass A — schema + bucket provisioning + IArtifactStore + init/finalize (~1.5 days)

The cloud accepts a composite-ingest init request, issues presigned PUT URLs, accepts a finalize callback that verifies uploads landed, and enqueues a job. No saga work yet — `notes.status` flips `pending → processing` on finalize and stays there. Sync pull returns nothing because no note is `ready`. Proves: terraform provisions the bucket, cloud-init wires creds, plugin auth works, presigned URLs work, finalize verification works.

**1. Terraform delta — `infra/docker/saga-worker/terraform-modules/digitalocean/main.tf`.**
   - Add `digitalocean_spaces_bucket` resource named `thany-cloud-${var.cloud_id}` in the matching region. ACL `private`. Versioning off.
   - Add `digitalocean_spaces_bucket_cors_configuration` allowing `PUT` and `GET` from `https://app.obsidian.md` and `app://obsidian.md` (Obsidian Electron origin) on `Content-Type`, `Content-MD5`, `x-amz-*` headers, expose `ETag`. **Without CORS, the plugin's browser-context PUTs fail with opaque CORS errors.**
   - Generate a scoped Spaces access key (programmatic credential) for this bucket via `digitalocean_spaces_access_key`. Store the secret in terraform state — encrypted-at-rest via portal's existing DataProtection-wrapped state backend (already wired for the Cloudflare token).
   - Add a bucket-lifecycle rule via `digitalocean_spaces_bucket_object_lifecycle_configuration`: objects under `notes/*` with object metadata tag `finalized=false` (or missing) are deleted after 24 hours. Defense-in-depth against the orphan sweeper.
   - Output: `bucket_name`, `bucket_region`, `bucket_endpoint`, `access_key_id`, `access_key_secret`. Same callback flow as the cloud-id/enrollment-token/cloud-admin-token already feeds Cloud.Api via cloud-init.

**2. Cloud-init template — `infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl`.**
   - Extend the `Bootstrap:*` env vars passed to the `cloud-api` compose service with: `Storage__Provider=s3`, `Storage__Endpoint=https://<region>.digitaloceanspaces.com`, `Storage__Region=<region>`, `Storage__Bucket=<bucket_name>`, `Storage__AccessKeyId=<id>`, `Storage__AccessKeySecret=<secret>`.
   - The secret goes through `templatefile()` substitution; remember the terraform `$` escape rule (only collapses when followed by `{`) — verify the literal string lands intact in the rendered cloud-init.

**3. Migration `0001_composite_ingest_spine.cs` — `Infrastructure/Database/Migrations/`.** Tables (full schemas in the "Schema reference" section below):
   - `plugin_tokens` — populated by dev-seeder for the smoke. The portal-cloud token sync is a later ticket (PORTAL-014's `PluginTokenEndpoints` writes here; out of scope).
   - `notes` — single table with `status` enum (`pending|processing|ready|failed`), inputs (`body_input`), outputs (`relative_path`, `body_output`, `suggested_project`, `tags`, `llm_mode`, `provenance` JSONB).
   - `attachments` — single table + `kind` discriminator + `extra` JSONB + extraction columns. Bucket coords as first-class columns.
   - `ingest_jobs` — saga queue per ADR-0019 (status, attempts, lease_owner, lease_expires_at, scheduled_at).
   - `cloud_settings` — single-row table; seeded with `llm_mode='safe'` on apply.

   Entity configurations in `Features/{PluginAuth,Ingest,Processing,Sync,Settings,Storage}/Configurations/...IEntityTypeConfiguration.cs`. Register via `modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudDbContext).Assembly)`.

**4. `Features/PluginAuth/` — bearer-token validation filter.** Identical to the existing CLOUD-002 draft's plan; copy verbatim:
   - `PluginToken.cs` entity + `PluginTokenConfiguration`.
   - `IPluginTokenAuthenticator` + `PluginTokenAuthenticator` — SHA-256 hash + `CryptographicOperations.FixedTimeEquals` against `plugin_tokens.token_hash`.
   - `RequirePluginAuthFilter` — endpoint filter, returns `Results.Unauthorized()` on miss.
   - `PluginPrincipal` + `HttpContext.GetPluginPrincipal()` extension.

**5. `Infrastructure/Storage/` — `IArtifactStore` + `S3ArtifactStore`.**
   ```csharp
   public interface IArtifactStore
   {
       Task<PresignedUpload> IssueUploadUrlAsync(
           string key, string mimeType, long byteSize, string contentMd5,
           TimeSpan ttl, CancellationToken ct);
       Task<PresignedDownload> IssueDownloadUrlAsync(
           string key, TimeSpan ttl, CancellationToken ct);
       Task<ObjectMetadata?> HeadAsync(string key, CancellationToken ct);
       Task DeleteAsync(string key, CancellationToken ct);
   }
   public sealed record PresignedUpload(Uri Url, IReadOnlyDictionary<string,string> RequiredHeaders, Instant ExpiresAt);
   public sealed record PresignedDownload(Uri Url, Instant ExpiresAt);
   public sealed record ObjectMetadata(long ByteSize, string ETag, string MimeType);
   ```
   - `S3ArtifactStore` uses `AWSSDK.S3` (NuGet `AWSSDK.S3`) against the DO Spaces endpoint configured via `Storage:Endpoint`. Constructs the client with `ForcePathStyle=false` and `ServiceURL=<endpoint>` (DO Spaces is virtual-host-style by default).
   - Presigned PUT URL signs `Content-Type` + `Content-MD5` headers; client MUST send both with matching values or S3 rejects. (`Content-MD5` is the client-side integrity hash; `sha256` we store separately in the DB row is the in-DB integrity hash. They're not the same value but they're both there for defense in depth.)
   - Presigned URLs are SigV4-signed; TTL passed at signing time; nothing to store server-side.

   **AzureBlobArtifactStore is NOT in this ticket.** Lands in the ticket that lands the Azure user-cloud terraform module (PORTAL-009 / sibling). Both impls live alongside, selected at startup via `Storage:Provider=s3|azure_blob`.

**6. `Features/Ingest/` — composite-ingest init + finalize.**
   - `POST /api/ingest/init` with `RequirePluginAuthFilter`:
     ```csharp
     public sealed record IngestInitRequest(
         string ClientNoteId,
         OffsetDateTime CapturedAt,
         string Body,                                // ≤ 100 KB
         IReadOnlyList<IngestInitAttachment> Attachments);  // ≤ 50

     public sealed record IngestInitAttachment(
         string ClientAttachmentId,
         string Kind,                                // 'url'|'image'|'voice'|'file'
         string? MimeType,                           // null for url
         long? ByteSize,                             // null for url
         string? Sha256,                             // null for url; hex
         string? Filename,                           // optional original name
         JsonElement Extra);                         // per-kind metadata
     ```
     Handler: open transaction → upsert `notes` by `(client_note_id)` (return existing if present, status=`pending`); insert `attachments` rows (status=`awaiting_upload` for binary kinds, status=`pending` for URL kinds — no bytes to upload). For binary kinds, derive `storage_key = "notes/{noteId}/{attachmentId}.{ext}"`, issue presigned PUT URL with 15-min TTL. Commit. Return `{ noteId, uploads: [{ clientAttachmentId, attachmentId, uploadUrl, requiredHeaders, expiresAt }] }`. URL kinds are omitted from `uploads[]` — no upload needed.
   - `POST /api/ingest/{noteId}/finalize` with `RequirePluginAuthFilter`:
     ```csharp
     public sealed record IngestFinalizeRequest(
         IReadOnlyList<IngestFinalizeAttachment> Uploaded);

     public sealed record IngestFinalizeAttachment(Guid AttachmentId, string Sha256, long ByteSize);
     ```
     Handler: load `notes` row; assert `status='pending'`; for each `Uploaded` entry, load attachment, `HEAD` the bucket key, verify `byte_size` + `ETag` match (S3 ETag is the MD5 of the bytes for single-PUT uploads; matches what the plugin computed). On mismatch return 422 with the diff. On match, flip `attachments.status='uploaded'`. For URL-kind attachments (not in `Uploaded`), no verification needed — they were `pending` from init. Once all attachments are accounted for, flip `notes.status='processing'`, insert `ingest_jobs` row, `pg_notify('ingest_jobs_new', '')`. Commit. Return `202 { noteId, status: "processing" }`.
   - Both endpoints get `.WithName("PostIngestInit")` / `.WithName("PostIngestFinalize")` + `.Produces<>` + `.ProducesProblem(400|401|404|422)`.

**7. `Features/Sync/Pull/` — `GET /api/sync/pull`.**
   - Cursor pagination by `updated_at` (existing draft's design holds). Filter `WHERE status='ready'`.
   - Per-note response includes `attachments[]` with `downloadUrl` (presigned GET, 1-hour TTL) + `downloadUrlExpiresAt` + `filename` + `byteSize` + `sha256`.
   - **Pass A behavior**: no notes are ever `ready` because the saga doesn't exist yet. The endpoint returns `{ items: [], nextSince: <unchanged> }`. This is intentional — Pass A's smoke verifies the init/finalize/bucket round-trip; sync pull is exercised in Pass B.

**8. `infra/docker/cloud/docker-compose.yml`** — no change needed for Pass A. The bucket lives in DO Spaces; the Cloud.Api process just signs URLs.

**9. Tests** under `tests/ThanyMarcus.Cloud.Tests/`:
   - `Features/PluginAuth/PluginTokenAuthenticatorTests.cs` — happy, wrong-token-same-length (constant-time), revoked, malformed header, missing header.
   - `Features/Ingest/IngestInitEndpointTests.cs` — 202 happy path returns presigned URLs for binary kinds, omits URL kinds; 400 oversize body; 400 > 50 attachments; 401 no auth; 200 idempotent replay (same `client_note_id` returns existing `noteId` with fresh URLs).
   - `Features/Ingest/IngestFinalizeEndpointTests.cs` — 202 happy path (uses `Moq`-backed `IArtifactStore` returning known `ObjectMetadata`); 422 sha256 mismatch; 422 byte_size mismatch; 404 wrong noteId; 401 no auth; idempotent (second finalize on already-`processing` note returns 202 noop).
   - `Infrastructure/Storage/S3ArtifactStoreTests.cs` — live tests against [MinIO via Testcontainers](https://hub.docker.com/r/minio/minio) (S3-compatible local mock); verify presign roundtrip (issue URL → PUT bytes → HEAD verify → DELETE).

→ At end of Pass A: `curl … /api/ingest/init` returns presigned URLs; `curl --upload-file …` PUTs bytes to DO Spaces; `curl … /api/ingest/{id}/finalize` flips status to `processing`. Bucket has bytes; `notes.status='processing'`; the saga hasn't been written so the row sits there forever (cleaned by orphan sweep when implemented in Pass B). **No AI yet, but the upload spine is green on real DO infrastructure.**

### Pass B — saga + URL extraction + LLM call + composite assembly (~1.5 days)

The `IngestSagaWorker` (BackgroundService) drains `ingest_jobs`. For each `processing` note: extract text from URL attachments (Readability), stub image/voice extraction (`extraction_status='skipped'`, `extracted_text=null`), issue **one structured LLM call** with `{ body_input, [extracted_text per attachment] }`, assemble composite Markdown with `[[wikilink]]` anchors + `![[attachment:<id>]]` placeholders, set `notes.status='ready'`. Sync pull now returns the note + presigned GET URLs.

**1. Migration `0002_cloud_settings_seed.cs`** — if cloud_settings table needs further indexing or constraints; can be folded into 0001 if minimal. Keep 0001 single-migration unless EF Core fights you.

**2. `Features/Processing/` — saga worker + handlers.**
   - `IngestSagaWorker.cs` (`BackgroundService`): claim loop using `FOR UPDATE SKIP LOCKED` on `ingest_jobs` partial index. Lease TTL 60s. `LISTEN ingest_jobs_new` on a dedicated Npgsql connection for wake-on-enqueue (same pattern as `Portal.SagaWorker.SagaWorker.cs`).
   - `IIngestJobHandler` — single handler interface; sealed `CompositeIngestHandler.cs` implements it (one handler for now; per-modality split is premature given the LLM step is shared).
   - `CompositeIngestHandler.cs`:
     ```
     load note + all attachments (eager) from DB
     foreach attachment with extraction_status='pending':
         dispatch by kind:
             url   -> UrlExtractor.ExtractAsync(extra.url) -> readability + AngleSharp
             image -> set extraction_status='skipped'      // CLOUD-003 lands real OCR
             voice -> set extraction_status='skipped'      // CLOUD-016b/c lands Parakeet
             file  -> set extraction_status='skipped'      // CLOUD-003 lands real file handling
         persist extracted_text + extraction_status; if exception, extraction_status='failed' + extraction_error
     load cloud_settings
     llm = llmFactory.Get(settings)
     enriched = await llm.EnrichCompositeAsync(new CompositeEnrichmentRequest(
         body: note.body_input,
         attachments: attachments.Where(a => a.extracted_text != null)
                                .Select(a => new AttachmentText(a.id, a.kind, a.extracted_text))
                                .ToList()), ct)
     // enriched: { suggested_project, anchors, tags, attachment_anchors }
     // anchors apply to body_input; attachment_anchors are optional, per attachment text
     relPath = enriched.SuggestedProject is { } p
         ? $"Projects/{Sanitize(p)}/{noteId}.md"
         : $"Inbox/{noteId}.md"
     body = AssembleMarkdown(note, attachments, enriched, capturedAt)
     // frontmatter (source=composite, captured_at, llm_mode, draft_id),
     //   ## Content (note.body_input with anchors → [[wikilinks]]),
     //   ## Attachments (per-attachment entry with ![[attachment:<id>]] placeholder + kind-specific metadata),
     //   ## Extracted (per-attachment extracted text, collapsed)
     notes.relative_path = relPath
     notes.body_output    = body
     notes.suggested_project = enriched.SuggestedProject
     notes.tags           = enriched.Tags
     notes.llm_mode       = settings.LlmMode
     notes.provenance     = { model, generated_at, input_tokens, output_tokens, anchor_count, extraction_failures: [...] }
     notes.status         = 'ready'
     ingest_jobs.status   = 'succeeded'
     commit
     ```
   - Retry policy: on handler exception, `attempts++`; if `attempts < 3`, reschedule `scheduled_at = now + 2^attempts seconds`; else flip `ingest_jobs.status='dead_lettered'` + `notes.status='failed'` + `notes.provenance = { error: ... }`.

**3. `Infrastructure/Extraction/` — URL extractor (real) + stubs.**
   - `IUrlExtractor` + `UrlExtractor.cs` — fetch URL with `HttpClient` (10s timeout, max 5 MB response, User-Agent `Thany-Marcus/1.0 (+https://thany.click)`); parse with `AngleSharp`; Readability port (port `mozilla/readability` or use existing .NET port `SmartReader`) to extract main content; convert to Markdown via `ReverseMarkdown`. Cap output at 500 KB; truncate with `[…truncated]` sentinel.
   - `IImageExtractor` / `IVoiceExtractor` / `IFileExtractor` — interfaces defined but implementations throw `NotImplementedException` (caller catches and sets `extraction_status='skipped'`). Tickets that land real impls drop them in via DI.

**4. `Infrastructure/Llm/` — LLM client abstraction.** Copy verbatim from the existing CLOUD-002 draft (Pass 1b §3), but rename method `EnrichTextAsync → EnrichCompositeAsync` and expand the request shape:
   ```csharp
   public sealed record CompositeEnrichmentRequest(string Body, IReadOnlyList<AttachmentText> Attachments);
   public sealed record AttachmentText(Guid AttachmentId, string Kind, string ExtractedText);
   public sealed record CompositeEnrichmentResult(
       string? SuggestedProject,
       IReadOnlyList<WikilinkAnchor> BodyAnchors,
       IReadOnlyList<AttachmentAnchors> AttachmentAnchors,
       IReadOnlyList<string> Tags);
   public sealed record WikilinkAnchor(string Text, int Start, int End, string Target);
   public sealed record AttachmentAnchors(Guid AttachmentId, IReadOnlyList<WikilinkAnchor> Anchors);
   ```
   - `AnthropicLlmClient` — tool-use forced JSON (one tool `record_composite_metadata`); default model `claude-haiku-4-5-20251001`; API key via DataProtection unprotect; prompt-cache the system prompt.
   - `OpenAiLlmClient` — stub throwing `NotImplementedException`.
   - `NoOpLlmClient` — returns `CompositeEnrichmentResult(null, [], [], [])` when `llm_mode='safe'`. Composite assembly still runs (procedural anchors-free Markdown), note lands in `Inbox/`.
   - `LlmClientFactory.Get(settings)` resolves the right impl per-iteration.

**5. `Features/Settings/` — `GET /admin/settings` + `PUT /admin/settings`.** Copy from the existing CLOUD-002 draft (Pass 1b §2): cloud-admin-token auth, encrypted external API key via `IDataProtectionProvider`, single-row `cloud_settings`.

**6. `Features/Sync/Pull/` (Pass B body).**
   - Per item, after loading the note, iterate `attachments` and issue presigned GET URLs via `IArtifactStore.IssueDownloadUrlAsync(storage_key, TimeSpan.FromHours(1))` for binary kinds. URL kinds carry the original URL (`extra.url`) directly.
   - Response shape:
     ```json
     {
       "items": [{
         "noteId": "...",
         "relativePath": "Projects/Travel/<id>.md",
         "body": "...",
         "suggestedProject": "Travel",
         "tags": ["travel", "meeting"],
         "llmMode": "unsafe_anthropic",
         "attachments": [
           { "attachmentId": "...", "kind": "voice", "filename": "memo.wav",
             "byteSize": 4521233, "sha256": "...", "mimeType": "audio/wav",
             "downloadUrl": "https://...digitaloceanspaces.com/...?signed=...",
             "downloadUrlExpiresAt": "2026-05-18T11:00:00Z" },
           { "attachmentId": "...", "kind": "url", "extra": { "url": "https://..." } }
         ],
         "updatedAt": "..."
       }],
       "nextSince": "..."
     }
     ```

**7. `Infrastructure/Sweepers/OrphanIngestSweeper.cs` — `BackgroundService`.**
   - Every 5 minutes, find `notes` with `status='pending'` and `created_at < now() - interval '1 hour'`. For each: best-effort `IArtifactStore.DeleteAsync` per attachment storage_key; flip `notes.status='failed'` with `provenance.error='orphan_no_finalize'`. The bucket-level lifecycle rule (24h tag-based) is the second line of defense.

**8. Tests** under `tests/ThanyMarcus.Cloud.Tests/`:
   - `Features/Processing/CompositeIngestHandlerTests.cs` — happy path with one URL attachment (uses a fake `IUrlExtractor` returning known Markdown); LLM enrichment via fake `ILlmClient`; assert note body contains `## Content`, `## Attachments`, `![[attachment:<id>]]`, anchors substituted; assert `notes.status='ready'`. Failure paths: URL extraction throws → `extraction_status='failed'`, note still lands (LLM gets empty attachment text); LLM 5xx → retry → success; LLM 401 → immediate `failed`.
   - `Features/Processing/IngestSagaWorkerTests.cs` — claim contention (two workers, one job, exactly one wins; mirror `SagaConcurrencyTests.cs`); lease heartbeat; `LISTEN/NOTIFY` wake.
   - `Features/Sync/Pull/SyncPullEndpointTests.cs` — empty when no notes; only `status='ready'` returned; `since` cursor; presigned URLs present per attachment; 401 no auth.
   - `Features/Settings/SettingsEndpointTests.cs` — admin auth; write rotates DP-encrypted key; default `safe`.
   - `Infrastructure/Llm/AnthropicLlmClientTests.cs` — `HttpMessageHandler` fake (mirror `CloudflareDnsClientTests.cs`); verify tool-use shape; verify cache_control; verify anchor validation rejects out-of-range offsets + retry-once policy.
   - `Infrastructure/Extraction/UrlExtractorTests.cs` — fake HTTP responses; Readability happy path; 5 MB cap; non-200 → throws; redirects followed up to 3 hops.
   - `Infrastructure/Sweepers/OrphanIngestSweeperTests.cs` — orphan past TTL → cleaned; orphan under TTL → kept; finalize during sweep race → sweep skips.
   - `CompositeIngestEndToEndTests.cs` — **the regression anchor.** Stand up MinIO via Testcontainers, configure `S3ArtifactStore` against it, run init → upload bytes → finalize → wait for `status='ready'` (poll up to 10s) → pull → assert Markdown body matches expectations. Fake `ILlmClient`; no live API calls in this test.
   - **Live-API trait** `[Trait("Category","Live")]` — one test that hits real Anthropic with a fixed input when `ANTHROPIC_API_KEY` is set; skipped on PRs.

→ At end of Pass B: full composite-ingest demo on real DO infrastructure, real bucket, real Anthropic call, real Readability extraction. The thesis MVP-1 flow is end-to-end green.

## Schema reference

### `notes`
```sql
id                uuid primary key,
client_note_id    text unique nullable,
captured_at       timestamptz not null,
status            text not null check (status in ('pending','processing','ready','failed')),
body_input        text not null,                  -- ≤ 100 KB (app-enforced)
relative_path     text nullable,
body_output       text nullable,
suggested_project text nullable,
tags              text[] nullable,
llm_mode          text nullable check (llm_mode in (null,'safe','unsafe_anthropic','unsafe_openai')),
provenance        jsonb nullable,
created_at        timestamptz not null,
updated_at        timestamptz not null
```
Index `(status, updated_at)` for sync-pull queries; index `(client_note_id)` is the unique constraint.

### `attachments`
```sql
id                  uuid primary key,
note_id             uuid not null references notes(id) on delete cascade,
kind                text not null check (kind in ('url','image','voice','file')),
storage_provider    text not null,                -- 's3'|'azure_blob'
storage_bucket      text not null,
storage_key         text not null unique,
byte_size           bigint nullable,              -- null for URL kind
mime_type           text nullable,                -- null for URL kind
sha256              text nullable,                -- hex; null for URL kind
filename            text nullable,
extraction_status   text not null default 'pending'
                    check (extraction_status in ('pending','extracted','skipped','failed')),
extracted_text      text nullable,
extraction_error    text nullable,
extra               jsonb not null default '{}',  -- per-kind metadata
created_at          timestamptz not null,
updated_at          timestamptz not null
```
Index `(note_id)` for the saga's eager-load. JSONB `extra` shape per kind:
- `url`: `{ "url": "https://...", "fetched_at": "...", "http_status": 200, "canonical_url": "..." }`
- `image`: `{ "width": 1920, "height": 1080, "exif": {...}, "phash": "..." }` (populated by CLOUD-003)
- `voice`: `{ "duration_ms": 154200, "sample_rate": 24000, "channels": 1 }` (populated by CLOUD-016b/c)
- `file`: `{ "page_count": 12 }` (PDFs, populated by CLOUD-003)

### `ingest_jobs`
Standard ADR-0019 saga queue — `id`, `note_id`, `status`, `attempts`, `last_error`, `lease_owner`, `lease_expires_at`, `scheduled_at`, `started_at`, `finished_at`, `created_at`, `updated_at`. Partial index `(status, scheduled_at)` where `status='queued'`.

### `plugin_tokens`
Standard — `id`, `token_hash bytea unique`, `label`, `created_at`, `revoked_at`.

### `cloud_settings`
Single-row enforced by PK check — `id int pk default 1 check (id=1)`, `llm_mode text not null default 'safe'`, `encrypted_external_api_key bytea nullable`, `llm_model text nullable`, `updated_at timestamptz not null`. Seeded with `(1, 'safe', null, null, now())`.

## Saga state machine

```text
notes.status:

pending     ──[POST /api/ingest/init succeeds]──►        pending (created, no bytes yet)
pending     ──[POST /api/ingest/.../finalize succeeds]──► processing  (bucket has bytes, job queued)
pending     ──[orphan sweep, 1h with no finalize]──►     failed       (sweeper sets, attachments cleaned best-effort)
processing  ──[saga handler succeeded]──►                ready
processing  ──[saga handler failed beyond retry budget]──► failed

ingest_jobs.status:

queued ──[saga claims; FOR UPDATE SKIP LOCKED]──► processing
processing ──[handler returns ok]──► succeeded                (terminal)
processing ──[handler throws; attempts < 3]──► queued         (with scheduled_at = now + 2^attempts s)
processing ──[handler throws; attempts >= 3]──► dead_lettered (terminal, flips notes.status='failed')
processing ──[lease TTL expires; another worker claims]──► processing  (sweeps every 30s)

attachments.extraction_status (set during saga handler, never user-visible):

pending      ──[handler dispatches by kind]──► extracted | skipped | failed
```

Notes:
- `lease_owner` = `IngestSagaWorker` instance GUID. `lease_expires_at = now + 60s`; renewed on every handler heartbeat.
- `LISTEN ingest_jobs_new` on a dedicated Npgsql connection — same pattern as `Portal.SagaWorker.SagaWorker.cs`. Notify on every successful insert into `ingest_jobs`.
- No `pg_notify` ordering guarantees; the claim loop is the source of truth.

## File map

```
Thany-Marcus/
├── plans/
│   └── cloud-002-handoff.md                                # THIS FILE (replaces stale draft)
├── docs/decisions/
│   └── (no new ADRs; see "Open contract decisions" below)
├── infra/docker/saga-worker/terraform-modules/digitalocean/
│   ├── main.tf                                              # CHANGED: + digitalocean_spaces_bucket, _cors, _access_key, lifecycle
│   ├── outputs.tf                                           # CHANGED: + bucket_name, bucket_endpoint, access_key_id, access_key_secret
│   └── cloud-init.yaml.tpl                                  # CHANGED: + Storage:* env vars on cloud-api compose service
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                           # CHANGED: + DI for IArtifactStore (S3ArtifactStore selected by Storage:Provider),
│   │                                                        #          + IPluginTokenAuthenticator, IIngestJobHandler, IUrlExtractor,
│   │                                                        #          + ILlmClientFactory, INoteStore (deferred — sharing zip ticket);
│   │                                                        #          + AddHostedService<IngestSagaWorker>(), <OrphanIngestSweeper>();
│   │                                                        #          + MapIngestInitEndpoint, MapIngestFinalizeEndpoint,
│   │                                                        #          + MapSyncPullEndpoint, MapAdminSettingsEndpoints
│   ├── Features/
│   │   ├── PluginAuth/                                      # NEW (replaces .gitkeep)
│   │   │   ├── PluginToken.cs
│   │   │   ├── PluginTokenConfiguration.cs
│   │   │   ├── IPluginTokenAuthenticator.cs
│   │   │   ├── PluginTokenAuthenticator.cs
│   │   │   ├── PluginPrincipal.cs
│   │   │   ├── RequirePluginAuthFilter.cs
│   │   │   └── HttpContextExtensions.cs
│   │   ├── Ingest/                                          # NEW (replaces .gitkeep)
│   │   │   ├── IngestInitEndpoint.cs
│   │   │   ├── IngestInitRequest.cs
│   │   │   ├── IngestInitResponse.cs
│   │   │   ├── IngestFinalizeEndpoint.cs
│   │   │   ├── IngestFinalizeRequest.cs
│   │   │   ├── IngestFinalizeResponse.cs
│   │   │   ├── Note.cs                                      # entity (single-table-with-states)
│   │   │   ├── NoteConfiguration.cs
│   │   │   ├── Attachment.cs                                # entity
│   │   │   ├── AttachmentConfiguration.cs
│   │   │   └── MimeToExtension.cs                           # deterministic mime→ext mapping
│   │   ├── Processing/                                      # NEW (replaces .gitkeep)
│   │   │   ├── IngestJob.cs
│   │   │   ├── IngestJobConfiguration.cs
│   │   │   ├── IngestSagaWorker.cs                          # BackgroundService
│   │   │   ├── IIngestJobHandler.cs
│   │   │   ├── CompositeIngestHandler.cs                    # the one handler
│   │   │   ├── CompositeMarkdownAssembler.cs                # frontmatter + sections + anchor substitution
│   │   │   └── PerAttachmentDispatcher.cs                   # kind → extractor wiring
│   │   ├── Sync/Pull/                                       # NEW (replaces .gitkeep)
│   │   │   ├── SyncPullEndpoint.cs
│   │   │   ├── SyncPullResponse.cs
│   │   │   └── (Note + Attachment reused from Ingest/)
│   │   └── Settings/                                        # NEW (replaces .gitkeep)
│   │       ├── AdminSettingsEndpoints.cs
│   │       ├── CloudSettings.cs
│   │       ├── CloudSettingsConfiguration.cs
│   │       ├── RequireCloudAdminTokenFilter.cs
│   │       └── LlmMode.cs
│   └── Infrastructure/
│       ├── Database/
│       │   ├── CloudDbContext.cs                            # CHANGED: + DbSets, ApplyConfigurationsFromAssembly
│       │   └── Migrations/0001_composite_ingest_spine.cs    # NEW
│       ├── Storage/                                         # NEW (replaces .gitkeep)
│       │   ├── IArtifactStore.cs
│       │   ├── PresignedUpload.cs
│       │   ├── PresignedDownload.cs
│       │   ├── ObjectMetadata.cs
│       │   ├── S3ArtifactStore.cs                           # AWSSDK.S3 against DO Spaces
│       │   └── StorageOptions.cs                            # Provider, Endpoint, Region, Bucket, AccessKeyId, AccessKeySecret
│       ├── Extraction/                                      # NEW
│       │   ├── IUrlExtractor.cs
│       │   ├── UrlExtractor.cs                              # Readability + AngleSharp + ReverseMarkdown
│       │   ├── IImageExtractor.cs                           # stub interface (real impl: CLOUD-003)
│       │   ├── NotImplementedImageExtractor.cs
│       │   ├── IVoiceExtractor.cs                           # stub interface (real impl: CLOUD-016b/c)
│       │   ├── NotImplementedVoiceExtractor.cs
│       │   ├── IFileExtractor.cs                            # stub interface (real impl: CLOUD-003)
│       │   └── NotImplementedFileExtractor.cs
│       ├── Llm/                                             # NEW
│       │   ├── ILlmClient.cs
│       │   ├── CompositeEnrichmentRequest.cs
│       │   ├── CompositeEnrichmentResult.cs
│       │   ├── WikilinkAnchor.cs
│       │   ├── AttachmentAnchors.cs
│       │   ├── ILlmClientFactory.cs
│       │   ├── LlmClientFactory.cs
│       │   ├── AnthropicLlmClient.cs
│       │   ├── OpenAiLlmClient.cs
│       │   └── NoOpLlmClient.cs
│       └── Sweepers/                                        # NEW
│           └── OrphanIngestSweeper.cs                       # BackgroundService
├── src/ThanyMarcus.Shared/                                  # CHANGED per ADR-0025
│   ├── Plugin/                                              # NEW namespace
│   │   ├── IngestInitRequestDto.cs
│   │   ├── IngestInitResponseDto.cs
│   │   ├── IngestFinalizeRequestDto.cs
│   │   ├── IngestFinalizeResponseDto.cs
│   │   ├── SyncPullItemDto.cs
│   │   ├── SyncPullAttachmentDto.cs
│   │   └── SyncPullResponseDto.cs
│   └── CloudAdmin/
│       ├── GetSettingsResponseDto.cs
│       └── PutSettingsRequestDto.cs
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Features/
    │   ├── PluginAuth/PluginTokenAuthenticatorTests.cs
    │   ├── Ingest/
    │   │   ├── IngestInitEndpointTests.cs
    │   │   └── IngestFinalizeEndpointTests.cs
    │   ├── Processing/
    │   │   ├── CompositeIngestHandlerTests.cs
    │   │   └── IngestSagaWorkerTests.cs
    │   ├── Sync/Pull/SyncPullEndpointTests.cs
    │   └── Settings/SettingsEndpointTests.cs
    ├── Infrastructure/
    │   ├── Storage/S3ArtifactStoreTests.cs                 # MinIO via Testcontainers
    │   ├── Extraction/UrlExtractorTests.cs
    │   ├── Llm/AnthropicLlmClientTests.cs                  # HttpMessageHandler fake + Live trait
    │   └── Sweepers/OrphanIngestSweeperTests.cs
    └── CompositeIngestEndToEndTests.cs                     # the regression anchor
```

## Out of scope (named explicitly)

1. ❌ **Real image OCR extraction.** `attachments.kind='image'` rows get `extraction_status='skipped'` in this ticket. The LLM call sees no extracted text for images. CLOUD-003 lands Tesseract + EXIF + pHash.
2. ❌ **Real voice transcription.** `kind='voice'` rows are skipped. Requires `WorkerLifecycleService` (CLOUD-016b), worker process binary (CLOUD-016c), worker Terraform modules (PORTAL-008b/PORTAL-009b). Per ADR-0035.
3. ❌ **Real file extraction (PDF, etc.).** `kind='file'` rows are skipped. CLOUD-003 lands PdfPig + format-dispatched text extraction.
4. ❌ **Safe mode (Ollama on burst worker).** Per ADR-0035. The `safe` enum value resolves to `NoOpLlmClient` (no enrichment); composite Markdown is assembled procedurally without anchors/tags. **Do not stub a fake Ollama client** — the no-op path is honest.
5. ❌ **pgvector / embeddings / entity-hub generation / wikilink target deduplication.** The LLM emits `target` strings; this ticket writes them verbatim. Hub creation, entity dedup, similarity retrieval are CLOUD-005/006/CLOUD-010.
6. ❌ **`AzureBlobArtifactStore` impl.** Interface exists; impl ships with the ticket that lands the Azure user-cloud terraform module (PORTAL-009).
7. ❌ **Sync push** (`POST /api/sync/push` for user edits + tombstones). Plugin-side concern; lands when the plugin's file-watcher slice arrives (PLUGIN-003+).
8. ❌ **Sharing (magic links + zip build).** Not on the capture-flow path. CLOUD-022/023.
9. ❌ **Full §22 provenance JSON** per attachment. The `notes.provenance` JSONB seeded here is the minimal audit field. CLOUD-024 lands the full schema.
10. ❌ **Portal plugin-token sync** to the cloud's `plugin_tokens` table. CLOUD-002 seeds via dev-secret for the smoke. PORTAL-014's `/api/cloud-management/plugin-tokens` lands the portal-cloud sync.
11. ❌ **Portal `/admin/*` proxy** wiring. The cloud-side endpoints work; portal calling them is PORTAL-014.
12. ❌ **Reprocess endpoint** (`POST /api/reprocess/{noteId}`). Useful, not on the walking-skeleton path. Add when re-eval workflow exists.
13. ❌ **OpenTelemetry instrumentation** for saga + LLM. Wiring exists from CLOUD-001; using it correctly (spans per phase, attrs for `llm_mode` / `model` / `latency` / `extraction_count`) lands when there's something to measure against.
14. ❌ **In-flight progress channel** (SSE for "voice memo transcribing…"). Plugin polls `/api/sync/pull` with cursor; the note appears when ready. SSE for ingest progress is post-MVP.

## Open contract decisions (carry forward)

1. **`ThanyMarcus.Shared` namespace shape for plugin DTOs.** Same as existing draft: option (a) — put under `ThanyMarcus.Shared/Plugin/` + `ThanyMarcus.Shared/CloudAdmin/`. Defer split into a separate project until the plugin exists and friction is real.
2. **Rate limiting on `/api/ingest/init` + `/finalize`.** Out of scope. The endpoints are single-tenant; rate-limit-free is acceptable for MVP. CLOUD-007 adds limits along with `/shared/{token}`'s per-IP limit.
3. **`cloud_admin_token` storage shape** (PORTAL-016 `// FORK:` note). Still open per `cloud-pivot-plan-2026-05-13.md §28 Q-AdminTokenStorage`. CLOUD-002's `RequireCloudAdminTokenFilter` constant-time-compares against the plaintext bootstrap-seeded `BootstrapOptions.CloudAdminToken`. Portal's hashed-storage divergence is PORTAL-014's problem.
4. **DO Spaces credentials rotation.** This ticket provisions a per-bucket access key at terraform-apply time. Rotation (new key issued, old key revoked) requires a destroy/recreate or terraform import dance. Defer rotation tooling to a follow-up; the access key has no TTL but can be revoked via DO API.
5. **ADRs to author.** Two candidates as a 0.25-day batch after CLOUD-002 ships:
   - **ADR-0039: Composite ingest + two-phase presigned upload.** Captures init/finalize shape, idempotency story, bucket key layout, signed-URL TTL, orphan sweep.
   - **ADR-0040: `IArtifactStore` abstraction + per-provider impl pattern.** Captures the S3 (DO Spaces, R2, B2) vs Azure Blob split, env-var-driven selection, no shared abstraction library.

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors. `dotnet test` green; live-API trait skipped without `ANTHROPIC_API_KEY`.
2. ✅ `terraform apply` (against the saga-worker's DO module, exercised via Portal's provisioning saga) creates the bucket + CORS + access key. Smoke #7 (this ticket's smoke) verifies a real bucket appears in the DO Spaces UI for the smoke user-cloud.
3. ✅ Cloud-init brings up `cloud-api` with `Storage:*` env vars; `/health/ready` returns 200.
4. ✅ Manual smoke against a freshly-provisioned cloud:
   ```bash
   # 1. Init
   INIT=$(curl -sS -X POST https://<cloud>.thany.click/api/ingest/init \
     -H "Authorization: Bearer $PLUGIN_TOKEN" -H "Content-Type: application/json" \
     -d '{
       "clientNoteId":"00000000-0000-0000-0000-000000000001",
       "capturedAt":"2026-05-18T10:00:00Z",
       "body":"Reading this article on Berlin trains, see voice note for thoughts",
       "attachments":[
         {"clientAttachmentId":"a1","kind":"url","mimeType":null,"byteSize":null,"sha256":null,
          "extra":{"url":"https://www.dw.com/en/germany-rail/a-12345"}},
         {"clientAttachmentId":"a2","kind":"voice","mimeType":"audio/wav","byteSize":204800,
          "sha256":"<hex>","extra":{"durationMs":12500}}
       ]
     }')
   # → 200 { noteId, uploads:[{ clientAttachmentId:"a2", attachmentId, uploadUrl, requiredHeaders, expiresAt }] }

   # 2. Upload voice bytes directly to DO Spaces
   curl -X PUT "<uploadUrl>" \
     -H "Content-Type: audio/wav" \
     -H "Content-MD5: <md5>" \
     --data-binary @memo.wav

   # 3. Finalize
   curl -sS -X POST https://<cloud>.thany.click/api/ingest/<noteId>/finalize \
     -H "Authorization: Bearer $PLUGIN_TOKEN" \
     -d '{"uploaded":[{"attachmentId":"<a2-attachmentId>","sha256":"<hex>","byteSize":204800}]}'
   # → 202 { noteId, status:"processing" }

   # 4. Configure unsafe mode (once per smoke; idempotent)
   curl -sS -X PUT https://<cloud>.thany.click/admin/settings \
     -H "Authorization: Bearer $CLOUD_ADMIN_TOKEN" \
     -d '{"llmMode":"unsafe_anthropic","externalApiKey":"sk-ant-...","llmModel":"claude-haiku-4-5-20251001"}'

   # 5. Wait for processing
   sleep 8

   # 6. Pull
   curl -sS "https://<cloud>.thany.click/api/sync/pull?since=2026-01-01T00:00:00Z" \
     -H "Authorization: Bearer $PLUGIN_TOKEN" | jq
   # → items[0].relativePath = "Projects/Travel/<noteId>.md"
   # → items[0].body contains "## Content", "[[Berlin]]", "![[attachment:<a2-attachmentId>]]"
   # → items[0].attachments[0].kind == "url"
   # → items[0].attachments[1].kind == "voice" && .downloadUrl matches https://<bucket>.<region>.digitaloceanspaces.com/...
   # → items[0].llmMode == "unsafe_anthropic"
   ```
5. ✅ `CompositeIngestEndToEndTests.cs` is green using MinIO via Testcontainers + fake `ILlmClient` + fake `IUrlExtractor`. Does not require live API or live DO bucket.
6. ✅ All `Features/{PluginAuth,Ingest,Processing,Sync,Settings}/` directories have at least one real `.cs` file (CLOUD-001's `.gitkeep` policy satisfied for those features). All new `Infrastructure/{Storage,Extraction,Llm,Sweepers}/` directories exist with their real contents. Feature `.gitkeep`s under `Knowledge/` and `Sharing/` stay.
7. ✅ Migration apply on a fresh empty DB succeeds; subsequent boots are idempotent.
8. ✅ Orphan sweep evidence: insert a `notes` row with `status='pending'` + `created_at = now() - interval '90 minutes'` + attachments with bucket keys; let sweeper run; row flips to `failed` and bucket keys are gone.
9. ✅ No `// TODO` markers in shipped code paths. `// FORK:` markers explicitly named at: the dev-seeded plugin-token path (forks to portal-cloud sync), the bootstrap-token-compare in `RequireCloudAdminTokenFilter` (forks to CLOUD-003 admin auth), the `NotImplemented*Extractor` stubs (forks to CLOUD-003 / CLOUD-016b/c), the `OpenAiLlmClient` stub, the `AzureBlobArtifactStore` slot (forks to PORTAL-009).

## What CLOUD-003+ inherits

After CLOUD-002 lands:
- The composite-ingest spine is reusable. Adding real image OCR (CLOUD-003) = new `IImageExtractor` impl + DI swap; no saga changes, no schema changes, no endpoint changes.
- Adding voice transcription (CLOUD-016b/c) = new `IVoiceExtractor` impl + worker plumbing; same shape.
- The `IArtifactStore` abstraction supports any S3-compatible provider (R2, B2, Wasabi, MinIO) by env-var config. Azure Blob impl drops in as a sibling.
- The `LlmClient` abstraction supports any provider with a JSON-output-via-tool-use pattern.
- The provenance `JSONB` column accepts any future audit shape without migration.
- `notes.status='ready'` + `attachments[].downloadUrl` is the sync API contract; the plugin can be written against this without further cloud changes.

CLOUD-003's expected scope (real image OCR + PDF + file extraction) is ~2 person-days *because* CLOUD-002 paid the spine cost up front. The cumulative thesis-demo arc is:

```
CLOUD-002 (composite-ingest spine, URL extraction, LLM)
   ─► CLOUD-003 (image OCR + PDF + file)
   ─► PLUGIN-001..003 (Obsidian plugin replaces curl)
   ─► [M5 demo: URL+image draft → cloud → vault]
   ─► CLOUD-016b/c + PORTAL-008b/009b (burst worker)
   ─► CLOUD-013 (Parakeet voice)
   ─► PLUGIN-005 (voice capture command)
   ─► [M6 demo: voice+image+URL draft → cloud → vault]
   ─► CLOUD-005..010 (entity hubs, embeddings, dedup)
   ─► [M6+ demo: emergent graph from accumulated captures]
```

## Why the previous draft was wrong (read once, then discard the old draft)

The earlier `cloud-002-handoff.md` (text-only walking skeleton, per-artifact `POST /api/ingest/text`) was authored before the 2026-05-17 composite-ingest decision and the 2026-05-18 grilling that locked option C (per-user bucket in CLOUD-002, no throwaway filesystem impl). Three issues made it incompatible:

1. **Per-artifact endpoint shape.** The plugin's actual capture flow is "note + N attachments", not "one text artifact at a time". A per-artifact ingest would require the plugin to coordinate N independent POSTs and a client-side aggregation step — duplicating cloud logic and risking partial-state on the client.
2. **No artifact storage abstraction.** The old draft put note body in a `text` column and had no concept of attachments or blob storage. CLOUD-002 was supposed to be the schema-defining ticket; shipping it without attachments forces a breaking schema migration the moment the second ticket lands.
3. **Stale ticket boundary with composite-ingest decision.** Memory `composite_ingest_decision.md` (2026-05-17) explicitly supersedes per-artifact ingest in the cloud-pivot plan. The old draft pre-dates that memory.

The corrected design (this handoff) lands the composite-ingest API + per-user blob bucket + storage abstraction + saga + LLM as a single coherent slice. ~3 person-days, no throwaway code, matches every locked decision.
