# CLOUD-002 Text-capture walking-skeleton — Handoff Brief

Date: 2026-05-17
Status: Draft (companion to `cloud-001-handoff.md` which scaffolded the project and bootstrap handshake).

**Goal:** stand up the first end-to-end capture pipeline on the user's cloud — text in, processed Markdown note out — so a `curl` against `POST /api/ingest/text` produces a note retrievable via `GET /api/sync/pull`. This is the **walking skeleton** for FR3 (in-cloud multimodal processing service) and FR4 (local-vault note materialization). Text is chosen as the first modality because (a) it is the literal core of the thesis goal ("user drafts a quick note in Obsidian → it goes to cloud pipeline → it becomes a new note"), (b) it requires no external extractor (no Readability, no Parakeet, no PdfPig), and (c) once the spine is green every other modality is "swap the extractor" against the same saga + storage + sync. The LLM enrichment lands in this ticket in **unsafe mode only** (control-plane direct call to Anthropic / OpenAI) — safe-mode Ollama on the burst worker is deferred to CLOUD-016b/c per ADR-0035.

Estimated **3 person-days** with AI-agent assistance. Split into two passes (1a spine; 1b LLM). After this ticket, the slice `auth → ingest → saga → storage → sync` is proven end-to-end on real Postgres, the LLM contract is real, and every subsequent capture modality (URL, PDF, file, voice, screenshot) is a swap of the extraction step inside the existing saga handler.

## Where decisions live (read before doing anything)

- **`plans/cloud-pivot-plan-2026-05-13.md`** — §1 (core transformation diagram lines 20–41), §9 (plugin commands incl. capture from clipboard / process selected text), §11 (cloud backend endpoints), §12 (processor router and saga — S4 lock: one structured LLM call per artifact), §13 (safe / unsafe LLM strategy), §22 (provenance schema), §25 (MVP scope). **This is the contract this ticket must implement.**
- **`plans/cloud-001-handoff.md`** — established the project, the directory structure, the bootstrap callback, the Caddy-event flow, the DbContext shape (currently empty). CLOUD-002 is the first ticket to populate `CloudDbContext` and the first to fill any of the `Features/{Ingest,Processing,Sync,PluginAuth,Settings}/.gitkeep` placeholders.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — Postgres job queue + `FOR UPDATE SKIP LOCKED` + lease + mutable status. **Applies as-is** to the processing saga; do not invent a new pattern.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the provisioning saga's *state machine* is the *blueprint* for the processing saga shape (phased status, mutable transitions, ownership predicate, lease/heartbeat, listen/notify wakeups). The processing saga is **simpler** (no rollback path; failures go to retry then dead-letter), but the worker discipline is identical. Copy the patterns; do not copy the code (the provisioning saga's domain is infra; the processing saga's domain is artifacts).
- **`docs/decisions/0024-dbcontext-shape.md`** — single `CloudDbContext` with per-feature `IEntityTypeConfiguration<T>`; same convention Portal.Api uses.
- **`docs/decisions/0028-schema-conventions.md`** — snake_case via `EFCore.NamingConventions`, audit conventions (`created_at` everywhere, `updated_at` on mutable rows, typed lifecycle timestamps), app calls `db.Database.MigrateAsync()` at startup (migration runner pattern A).
- **`docs/decisions/0025-openapi-and-hand-written-ts-contracts.md`** — endpoints get `.WithName("...")` + `.Produces<>` + `.ProducesProblem(...)`; TS types for the plugin are hand-written in `ThanyMarcus.Shared.Contracts` (or a sibling location — confirm where in this ticket; see §"Open contract decisions" below).
- **`docs/decisions/0023-test-stack.md`** + **`docs/decisions/0028-schema-conventions.md` §2** — xUnit v3 + Shouldly + Testcontainers via `PostgresFixture` + `Respawn` for inter-test isolation. The Cloud.Tests project already has `PostgresFixture` + `CloudApiFactory` from CLOUD-001.
- **`docs/decisions/0035-burst-worker-llm-tier.md`** — explicitly out of scope here; this ticket lands unsafe mode only. See "Out of scope" §2 below.
- **Memory files**: `portal_architecture.md` (Postgres job queue, mutable status, no event sourcing, SSE only — applies to cloud), `portal_tooling.md` (.NET 10, warnings-as-errors, Shouldly+xUnit v3, OpenAPI+Scalar, /health/live+ready), `thesis_context.md` (S4 lock: one structured LLM call per artifact; the LLM is the only thing the cloud does that the client cannot).

**Do not invent new modalities, new auth schemes, or pgvector usage in this ticket.** Text + bearer + procedural assembly + sync pull. Everything else is the next slice.

## Scope boundary (precise)

This ticket lands in **two passes** sized at ~1.5 days each. Both passes ship in the same handoff because the LLM step is meaningless without the spine, and the spine is unsatisfying without the LLM step (it's an echo server). Splitting them across two tickets creates a no-op middle state — undesirable.

### Pass 1a — spine only (no LLM, ~1.5 days)

The cloud accepts a text artifact, runs an identity-transform "processing" step, writes a stub Markdown note, and the same artifact comes back via sync pull. No LLM call. No external dependencies. Proves auth + ingest + saga + storage + sync end-to-end.

**1. Migration `0001_walking_skeleton.cs` — `Infrastructure/Database/Migrations/`.** Tables:
   - `plugin_tokens (id uuid pk, token_hash bytea unique, label text, created_at timestamptz, revoked_at timestamptz nullable)` — populated out-of-band by CLOUD-003 (admin token-issuance endpoint) or via a dev seeder for the smoke. For CLOUD-002 the seeder is **a single dev-only `dotnet user-secrets` value the smoke writes through a temporary fixture**; we do not introduce the admin issuance endpoint here.
   - `artifacts (id uuid pk, kind text not null check (kind in ('text')), client_artifact_id text not null, content text not null, captured_at timestamptz not null, status text not null check (status in ('queued','processing','ready','failed')), created_at timestamptz not null, updated_at timestamptz not null)` — unique constraint on `(client_artifact_id)` to make ingest idempotent per-token. (`kind` is a check now; CLOUD-003+ relaxes to `('text','url','pdf','file','audio','image')`.)
   - `ingest_jobs (id uuid pk, artifact_id uuid not null references artifacts(id) on delete cascade, status text not null check (status in ('queued','processing','succeeded','failed','dead_lettered')), attempts int not null default 0, last_error text nullable, lease_owner uuid nullable, lease_expires_at timestamptz nullable, scheduled_at timestamptz not null, started_at timestamptz nullable, finished_at timestamptz nullable, created_at timestamptz not null, updated_at timestamptz not null)` — partial index `(status, scheduled_at)` where `status='queued'`.
   - `notes (id uuid pk, artifact_id uuid not null references artifacts(id) on delete cascade, relative_path text not null, body text not null, suggested_project text nullable, tags text[] nullable, llm_mode text nullable check (llm_mode in (null, 'none','unsafe_anthropic','unsafe_openai')), created_at timestamptz not null, updated_at timestamptz not null)` — unique on `relative_path`.

   No `cloud_settings` table in Pass 1a. (Lands in Pass 1b.)

   Entity configurations in `Features/{PluginAuth,Ingest,Processing,Sync}/Configurations/...IEntityTypeConfiguration.cs`. Register on `CloudDbContext.OnModelCreating(modelBuilder)` via `modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloudDbContext).Assembly)`.

**2. `Features/PluginAuth/` — bearer-token validation filter.**
   - `PluginToken.cs` (entity + IEntityTypeConfiguration).
   - `IPluginTokenAuthenticator.cs` + `PluginTokenAuthenticator.cs` — reads `Authorization: Bearer <t>`, SHA-256 hashes the token, looks up `plugin_tokens` by hash, constant-time-compares the hash bytes (use `CryptographicOperations.FixedTimeEquals`), returns `PluginPrincipal { TokenId, IssuedAt }` or 401. Mirror the constant-time discipline already in `Portal.Api/Features/CloudManagement/Callback/CloudCallbackEndpoints.cs`.
   - `RequirePluginAuthFilter.cs` — endpoint filter applied via `.AddEndpointFilter<RequirePluginAuthFilter>()`. On 401 returns `Results.Unauthorized()` with no body (no enumeration oracle).
   - `PluginPrincipal.cs` — sealed record attached via `HttpContext.Items["PluginPrincipal"]`; expose `HttpContext.GetPluginPrincipal()` extension.

   **No JWT, no OAuth, no session.** Static bearer per ADR-0024 of the pivot plan (Plugin → user's cloud). The token is issued by the portal at "Plugin Setup" (already scaffolded in `Portal.Api/Features/CloudManagement/PluginTokens/`) — for CLOUD-002 the smoke seeds the cloud-side row directly; the portal-cloud token sync is a later ticket.

**3. `Features/Ingest/Text/` — `POST /api/ingest/text`.**
   - `IngestTextEndpoint.cs` — `MapPost("/api/ingest/text", ...)` with `RequirePluginAuthFilter`.
   - `IngestTextRequest.cs` — `{ string ClientArtifactId, string Content, OffsetDateTime CapturedAt, string? Title }`. Validate: content length ∈ [1, 100 KB]; `ClientArtifactId` non-empty + ≤ 128 chars.
   - `IngestTextResponse.cs` — `{ Guid ArtifactId, string Status }`.
   - Handler: open transaction → check `artifacts` for existing `(client_artifact_id)` — if found, return `200` with the existing `ArtifactId` (idempotency). Else insert `artifacts (kind=text, status=queued)` + `ingest_jobs (status=queued, scheduled_at=now, attempts=0)` → commit → `pg_notify('ingest_jobs_new', '')` → return `202 Accepted` with `ArtifactId`.
   - Annotate `.WithName("PostIngestText")` + `.Produces<IngestTextResponse>(202)` + `.ProducesProblem(400)` + `.ProducesProblem(401)`.

**4. `Features/Processing/` — saga worker for the new queue.**
   - `IngestSagaWorker.cs` (`BackgroundService`): claim loop using `FOR UPDATE SKIP LOCKED` on `ingest_jobs` partial index. Lease TTL 60s (text is fast; bump per modality later). `LISTEN ingest_jobs_new` on a dedicated Npgsql connection for wake-on-enqueue (same pattern as `Portal.SagaWorker.SagaWorker.cs`).
   - `IIngestJobHandler.cs` — single handler interface; sealed `TextProcessingHandler.cs` implements it.
   - `TextProcessingHandler.cs` (Pass 1a body): load `artifacts` row → procedurally assemble Markdown:
     ```
     ---
     source: text
     captured_at: <iso8601>
     artifact_id: <uuid>
     ---

     ## Content

     <verbatim content>

     ## User Notes

     <empty>
     ```
     Write to `NoteStore` at `Inbox/<artifact_id>.md` (computed `relative_path`). Insert `notes` row (`llm_mode=null`, `suggested_project=null`). Flip `artifacts.status='ready'`, `ingest_jobs.status='succeeded'`, set `finished_at`. Commit.

   - `IngestSagaHostBuilder` — registered in `Program.cs` as `builder.Services.AddHostedService<IngestSagaWorker>()`.
   - Retry policy: on handler exception, `attempts++`; if `attempts < 3`, reschedule `scheduled_at = now + 2^attempts seconds`; else flip to `dead_lettered` with `last_error`. No rollback path needed — failed processing leaves `artifacts.status='failed'` and the row stays.

**5. `Features/Sync/Pull/` — `GET /api/sync/pull`.**
   - `SyncPullEndpoint.cs` — `MapGet("/api/sync/pull", ...)` with `RequirePluginAuthFilter`. Query string: `since` (ISO 8601, optional; default = `1970-01-01T00:00:00Z`), `limit` (default 100, max 500).
   - Returns `{ items: [{ artifactId, relativePath, body, suggestedProject, tags, updatedAt }], nextSince: <iso8601> }`. `nextSince` = max `updated_at` in the returned page (cursor-style; the plugin polls with that next time).
   - **Single-tenant in MVP**: no `tokenId` scoping on the query — the plugin sees all notes in the cloud, which matches the single-user-per-cloud framing (see open question Q-CloudAccess below). If Q-CloudAccess resolves to multi-identity per cloud, this query gains a `WHERE token_id = ?` clause; until then, no scope.
   - Annotate `.WithName("GetSyncPull")` + `.Produces<SyncPullResponse>(200)` + `.ProducesProblem(401)`.

**6. `Infrastructure/Storage/NoteStore.cs`.**
   - `INoteStore { Task WriteAsync(string relativePath, string body, CancellationToken ct); Task<string?> ReadAsync(string relativePath, CancellationToken ct); }` — filesystem-backed at `BootstrapOptions.NoteStoreRoot` (configurable; default `/var/lib/thany-marcus/notes`). Add `NoteStoreRoot` to `BootstrapOptions.cs`.
   - Pass 1a only needs `WriteAsync`. `ReadAsync` is not called yet; the body is also persisted in `notes.body` (denormalized) because the API serves it from DB on `sync/pull`. The filesystem write is the "vault-shaped" artifact for sharing zips (CLOUD-008+); both stores live in parallel from day one.
   - Path sanitization: `relativePath` must not contain `..`, leading `/`, or null bytes; reject with `ArgumentException` (saga maps to `failed`). `NoteStore` writes are idempotent (overwrite on collision; `relative_path` is unique in DB so collisions only happen on artifact-id reuse, which is fine).

**7. `infra/docker/cloud/docker-compose.yml`** — add a named volume `cloud_notes:/var/lib/thany-marcus/notes` mounted on the `cloud-api` service. The Postgres volume is already present from CLOUD-001.

**8. Tests** under `tests/ThanyMarcus.Cloud.Tests/`.
   - `Features/PluginAuth/PluginTokenAuthenticatorTests.cs` — happy path, wrong-token-same-length (constant-time), revoked token, malformed header, missing header.
   - `Features/Ingest/Text/IngestTextEndpointTests.cs` — 202 happy path; 400 oversize; 400 empty; 401 no auth; 200 idempotent replay (same `client_artifact_id` returns existing `artifactId`); `pg_notify` fires (assert via side-channel `LISTEN` connection).
   - `Features/Processing/TextProcessingHandlerTests.cs` — happy path (note written, statuses flip); failed handler reschedules; 3rd failure → dead_lettered.
   - `Features/Processing/IngestSagaWorkerTests.cs` — claim contention (two workers, one job, exactly one wins, mirror `SagaConcurrencyTests.cs` pattern); lease heartbeat extension; `LISTEN/NOTIFY` wake-up.
   - `Features/Sync/Pull/SyncPullEndpointTests.cs` — empty result when no notes; ordered by `updated_at`; `since` cursor pagination; 401 no auth.
   - `WalkingSkeletonEndToEndTests.cs` — ingest → wait for `status=ready` (poll up to 5s) → pull → assert markdown body matches. This is the one test that, if it stays green forever, means CLOUD-002 didn't regress.

→ At end of Pass 1a: `curl -H "Authorization: Bearer <t>" -d '{"clientArtifactId":"x","content":"hello world","capturedAt":"2026-05-17T10:00:00Z"}' …/api/ingest/text` returns `202 {artifactId}`. Two seconds later, `curl -H "Authorization: Bearer <t>" …/api/sync/pull` returns the assembled Markdown. **No AI magic, but the spine is green.**

### Pass 1b — drop LLM into the same handler (~1.5 days)

The `TextProcessingHandler` from Pass 1a gains one structured LLM call between "load artifact" and "assemble Markdown." The output augments the procedural assembly with `[[wikilink]]` anchors and routes the note out of `Inbox/` into `Projects/<suggested>/`. **Unsafe mode only** in this ticket (control-plane call to Anthropic or OpenAI); safe mode returns a "not configured" sentinel that bypasses LLM enrichment and falls back to Pass 1a's identity-transform behavior. Per the privacy-by-default discussion in this thread: **default `llm_mode` is `safe` in code**; unsafe is explicitly enabled via Settings.

**1. Migration `0002_cloud_settings.cs`.**
   - `cloud_settings (id int pk default 1 check (id=1), llm_mode text not null default 'safe' check (llm_mode in ('safe','unsafe_anthropic','unsafe_openai')), encrypted_external_api_key bytea nullable, llm_model text nullable, updated_at timestamptz not null)` — single-row enforcement via primary-key check constraint.
   - Seed: insert `(1, 'safe', null, null, now())` on migration apply. The cloud boots in safe mode; LLM enrichment is a no-op until the user configures unsafe.

**2. `Features/Settings/` — read + write the single row.**
   - `GET /admin/settings` (cloud-admin-token auth, **not** plugin-bearer) — returns `{ llmMode, llmModel, hasApiKey: bool }`. Never returns the key itself.
   - `PUT /admin/settings` (cloud-admin-token auth) — accepts `{ llmMode, llmModel?, externalApiKey? }`. When `llmMode='unsafe_*'`, `externalApiKey` is required; encrypt via `IDataProtectionProvider` (purpose string `"cloud-settings:external-api-key:v1"`) and store as `encrypted_external_api_key`. When `llmMode='safe'`, blank the key.
   - **Cloud-admin-token middleware**: identical pattern to `PluginAuth` but reads the bootstrap-seeded `Bootstrap:CloudAdminToken` and constant-time-compares against the inbound header. (Once CLOUD-003 lands a proper admin auth surface, replace with that; for CLOUD-002, the bootstrap-token compare is sufficient since `/admin/settings` is only called by the portal proxy.)
   - **Defer the portal-cloud `/admin/*` proxy work to PORTAL-014.** This ticket only lands the endpoint; portal calling into it is not on the CLOUD-002 smoke path. The endpoint is testable via direct `WebApplicationFactory<Program>` calls with the bootstrap token in the header.

**3. `Infrastructure/Llm/` — LLM client abstraction.**
   - `ILlmClient.cs`:
     ```csharp
     public interface ILlmClient
     {
         Task<LlmEnrichmentResult> EnrichTextAsync(EnrichmentRequest req, CancellationToken ct);
     }
     public sealed record EnrichmentRequest(string Content, string? Title);
     public sealed record LlmEnrichmentResult(
         string? SuggestedProject,
         IReadOnlyList<WikilinkAnchor> Anchors,
         IReadOnlyList<string> Tags);
     public sealed record WikilinkAnchor(string Text, int Start, int End, string Target);
     ```
   - `AnthropicLlmClient.cs` — talks to `https://api.anthropic.com/v1/messages`. Forces JSON output via the **tool-use** pattern: define a single tool `record_artifact_metadata` with input schema matching `LlmEnrichmentResult`, set `tool_choice = { type: "tool", name: "record_artifact_metadata" }`. Read the tool-call args from the response. Default model `claude-haiku-4-5-20251001` (per memory: "default to the latest and most capable Claude models" — Haiku is the appropriate speed/cost tier for routing/extraction; the user can override via `cloud_settings.llm_model`). Prompt-cache the system prompt + few-shot examples (5-min TTL, exposed via the SDK's `cache_control: { type: "ephemeral" }`). API-key load via `IDataProtectionProvider` unprotect on first use; hold in memory for process lifetime; rotate on settings change via `IOptionsMonitor`-like trigger.
   - `OpenAiLlmClient.cs` — placeholder stub for symmetry; one method, throws `NotImplementedException`. Adding it is one ticket of follow-up; the abstraction must support it from day one so the second-provider claim in the thesis is not vapor.
   - `NoOpLlmClient.cs` — returned when `llmMode='safe'`. Returns `LlmEnrichmentResult(null, [], [])`. Pass 1a behavior preserved.
   - `LlmClientFactory.cs` — `ILlmClientFactory.Get(CloudSettings)` returns the right concrete client. Registered as scoped DI; resolved fresh per saga iteration so settings changes propagate without process restart.

**4. `TextProcessingHandler.cs` — add the LLM step.**
   - Pseudocode (after Pass 1a's "load artifact"):
     ```
     settings  = await db.CloudSettings.SingleAsync(ct);
     llm       = llmFactory.Get(settings);
     enriched  = await llm.EnrichTextAsync(new EnrichmentRequest(content, title), ct);
     relPath   = enriched.SuggestedProject is { } p ? $"Projects/{Sanitize(p)}/{id}.md" : $"Inbox/{id}.md";
     body      = AssembleMarkdown(content, enriched, capturedAt);  // inserts [[wikilink]] at anchor offsets, frontmatter gets tags
     await noteStore.WriteAsync(relPath, body, ct);
     ...
     ```
   - LLM call failure modes: timeout (10s default) → `attempts++`, retry per Pass 1a policy; malformed-JSON-tool-output (Anthropic returned something weird) → same retry; HTTP 4xx from provider (e.g. invalid key) → flip job to `failed` immediately with `last_error="provider_rejected"`, do not retry; HTTP 5xx → retry with backoff.
   - **`notes.llm_mode` is set to the settings value at the time of the call** (audit trail per `cloud-pivot-plan-2026-05-13.md §22`). This is the per-call audit-log invariant for the unsafe-mode consent UX.

**5. Prompt template.** Inline in `AnthropicLlmClient.cs` (do not over-extract). Asks for:
   - `suggested_project`: short folder name (1–3 words; PascalCase; null if "this doesn't clearly belong anywhere yet")
   - `anchors`: list of `{ text, start, end, target }` where `text` is a verbatim substring of the input, `start`/`end` are UTF-16 code-unit offsets, `target` is the wikilink target (entity name)
   - `tags`: 0–5 lowercase short tags
   - Constraint: anchors must be sorted, non-overlapping, fall within `[0, len(content))`. Validated in C# before applying; reject the whole result and retry once on violation; if second attempt also violates, drop anchors silently (keep `suggested_project` + `tags`).

**6. Tests** under `tests/ThanyMarcus.Cloud.Tests/`.
   - `Features/Settings/SettingsEndpointTests.cs` — read returns default safe; write requires admin token; write rotates key (old key cannot decrypt new ciphertext); 401 wrong token.
   - `Infrastructure/Llm/AnthropicLlmClientTests.cs` — uses `HttpMessageHandler` fake (same pattern as `CloudflareDnsClientTests.cs` from PORTAL-010b). Verifies request shape (tool definition, tool_choice, cache_control); verifies anchor validation rejects out-of-range offsets; verifies retry-once-then-drop-anchors behavior.
   - `Features/Processing/TextProcessingHandlerLlmTests.cs` — happy path: enrichment runs, note lands in `Projects/<suggested>/`, body contains `[[wikilink]]` at correct positions, `notes.llm_mode='unsafe_anthropic'`. Failure path: provider timeout → retry → second-attempt success. Provider 401 → immediate `failed` (no retry).
   - **Live-API smoke test** behind `[Trait("Category","Live")]` — set `ANTHROPIC_API_KEY` in env, hit real API once per CI run on `main` (skipped on PRs to avoid burning credits). One assertion: the call returns a non-empty result for a fixed input.

→ At end of Pass 1b: `curl … /api/ingest/text -d '{"clientArtifactId":"x","content":"meeting with Anna about Berlin trip","capturedAt":"…"}'` and 3–5 seconds later, `/api/sync/pull` returns a Markdown file with frontmatter `tags: [travel, meeting]`, relative path `Projects/Travel/<uuid>.md`, body containing `meeting with [[Anna]] about [[Berlin]] trip`. **This is the thesis demo, end to end, on real infrastructure.**

## Out of scope (named explicitly so it doesn't sneak in)

1. ❌ **Other modalities.** No URL ingest, no PDF, no audio, no image, no drag-drop file. Those are CLOUD-003 (URL + PDF + file) and CLOUD-004 (audio + image, gated on burst worker). The `artifacts.kind` check constraint accepts only `'text'` in this ticket's migration.
2. ❌ **Safe mode (Ollama on burst worker).** Per ADR-0035, safe mode requires `WorkerLifecycleService` (CLOUD-016b), worker process binary (CLOUD-016c), and worker Terraform modules (PORTAL-008b for DO, PORTAL-009b for Azure). All deferred. The `safe` enum value exists in the settings and resolves to `NoOpLlmClient` (no enrichment) until those tickets land. **Do not stub a fake Ollama client** — the no-op path is honest about what's available.
3. ❌ **pgvector / embeddings / entity-hub generation / wikilink target deduplication.** The LLM emits `target` strings; this ticket writes them verbatim into the Markdown. Hub-creation threshold (N=3), entity dedup, similarity retrieval are CLOUD-005/006.
4. ❌ **Sync push** (`POST /api/sync/push` for user edits + tombstones). The plugin can't push edits if there's no plugin yet, and we're not writing the plugin in this ticket. Add when the plugin's file-watcher slice lands (PLUGIN-003+).
5. ❌ **Sharing (magic links + zip build).** Not on the capture-flow critical path.
6. ❌ **Provenance JSON** persistence per artifact. The `notes.llm_mode` column is the **minimal audit field** for this ticket (covers the safe/unsafe-mode-per-call thesis requirement). Full provenance schema per `cloud-pivot-plan-2026-05-13.md §22` lands when entity extraction does (CLOUD-005).
7. ❌ **Portal plugin-token sync** to the cloud's `plugin_tokens` table. CLOUD-002 seeds tokens via dev-secret for the smoke. The portal-cloud token sync (called by Portal.Api/Features/CloudManagement/PluginTokens/PluginTokenEndpoints at "Plugin Setup") is a separate ticket; without it, this ticket's seed-via-secret path is the **only** way a token gets into the cloud, and that's the documented limitation.
8. ❌ **Portal `/admin/*` proxy** wiring. The cloud-side `/admin/settings` endpoint works; portal calling it is PORTAL-014's problem. PORTAL-016's `// FORK:` note on `cloud_admin_token` storage shape is still the open dependency.
9. ❌ **Reprocess endpoint** (`POST /api/reprocess/{noteId}`). Useful, but not on the walking-skeleton path.
10. ❌ **OpenTelemetry traces for the saga + LLM call.** The infrastructure is already wired in `Program.cs` (CLOUD-001); using it correctly (spans per phase, attributes for `llm_mode` / `model` / `latency`) is polish that lands when we have something to measure against (CLOUD-002 itself sets the baseline; eval instrumentation is its own ticket).

## File map

```
Thany-Marcus/
├── plans/
│   └── cloud-002-handoff.md                                # THIS FILE
├── docs/decisions/
│   └── (no new ADRs in this ticket — see "Open contract decisions" below)
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                          # CHANGED: + DI for ILlmClientFactory, INoteStore, IPluginTokenAuthenticator, IIngestJobHandler; + AddHostedService<IngestSagaWorker>(); + MapIngestTextEndpoint, MapSyncPullEndpoint, MapAdminSettingsEndpoints
│   ├── Features/
│   │   ├── PluginAuth/                                     # NEW (replaces .gitkeep)
│   │   │   ├── PluginToken.cs                              # entity
│   │   │   ├── PluginTokenConfiguration.cs                 # IEntityTypeConfiguration<PluginToken>
│   │   │   ├── IPluginTokenAuthenticator.cs
│   │   │   ├── PluginTokenAuthenticator.cs
│   │   │   ├── PluginPrincipal.cs
│   │   │   ├── RequirePluginAuthFilter.cs
│   │   │   └── HttpContextExtensions.cs                    # GetPluginPrincipal()
│   │   ├── Ingest/Text/                                    # NEW (replaces Ingest/.gitkeep)
│   │   │   ├── IngestTextEndpoint.cs
│   │   │   ├── IngestTextRequest.cs
│   │   │   ├── IngestTextResponse.cs
│   │   │   ├── Artifact.cs                                 # entity (kind=text only in this ticket)
│   │   │   └── ArtifactConfiguration.cs
│   │   ├── Processing/                                     # NEW (replaces Processing/.gitkeep)
│   │   │   ├── IngestJob.cs
│   │   │   ├── IngestJobConfiguration.cs
│   │   │   ├── IngestSagaWorker.cs                         # BackgroundService; FOR UPDATE SKIP LOCKED + LISTEN/NOTIFY
│   │   │   ├── IIngestJobHandler.cs
│   │   │   ├── TextProcessingHandler.cs                    # Pass 1a body + Pass 1b LLM step
│   │   │   └── MarkdownAssembler.cs                        # procedural Markdown + anchor insertion
│   │   ├── Sync/Pull/                                      # NEW (replaces Sync/.gitkeep)
│   │   │   ├── SyncPullEndpoint.cs
│   │   │   ├── SyncPullResponse.cs
│   │   │   ├── Note.cs                                     # entity
│   │   │   └── NoteConfiguration.cs
│   │   └── Settings/                                       # NEW (replaces Settings/.gitkeep) — Pass 1b
│   │       ├── AdminSettingsEndpoints.cs                   # GET + PUT
│   │       ├── CloudSettings.cs                            # entity (single-row)
│   │       ├── CloudSettingsConfiguration.cs
│   │       ├── GetSettingsResponse.cs
│   │       ├── PutSettingsRequest.cs
│   │       ├── RequireCloudAdminTokenFilter.cs             # bootstrap-token compare
│   │       └── LlmMode.cs                                  # enum: Safe, UnsafeAnthropic, UnsafeOpenAi
│   └── Infrastructure/
│       ├── Database/
│       │   ├── CloudDbContext.cs                           # CHANGED: + DbSet<Artifact>, IngestJob, Note, PluginToken, CloudSettings; ApplyConfigurationsFromAssembly
│       │   └── Migrations/                                 # NEW directory
│       │       ├── 0001_walking_skeleton.cs                # artifacts, ingest_jobs, notes, plugin_tokens
│       │       └── 0002_cloud_settings.cs                  # cloud_settings (+ seed row)
│       ├── Storage/                                        # NEW
│       │   ├── INoteStore.cs
│       │   └── FilesystemNoteStore.cs
│       └── Llm/                                            # NEW — Pass 1b
│           ├── ILlmClient.cs
│           ├── EnrichmentRequest.cs
│           ├── LlmEnrichmentResult.cs
│           ├── WikilinkAnchor.cs
│           ├── ILlmClientFactory.cs
│           ├── LlmClientFactory.cs
│           ├── AnthropicLlmClient.cs
│           ├── OpenAiLlmClient.cs                          # stub; throws NotImplementedException
│           └── NoOpLlmClient.cs
├── src/ThanyMarcus.Shared/                                 # CHANGED (per ADR-0025 hand-written TS-shadowing DTOs)
│   ├── Plugin/                                             # NEW namespace
│   │   ├── IngestTextRequestDto.cs                         # shape mirrors Ingest/Text/IngestTextRequest.cs
│   │   ├── IngestTextResponseDto.cs
│   │   ├── SyncPullItemDto.cs
│   │   └── SyncPullResponseDto.cs
│   └── CloudAdmin/
│       ├── GetSettingsResponseDto.cs                       # for portal → cloud proxy use (PORTAL-014)
│       └── PutSettingsRequestDto.cs
├── infra/docker/cloud/
│   └── docker-compose.yml                                  # CHANGED: + named volume cloud_notes mounted on cloud-api at /var/lib/thany-marcus/notes
└── tests/ThanyMarcus.Cloud.Tests/
    ├── Features/
    │   ├── PluginAuth/PluginTokenAuthenticatorTests.cs     # NEW
    │   ├── Ingest/Text/IngestTextEndpointTests.cs          # NEW
    │   ├── Processing/
    │   │   ├── TextProcessingHandlerTests.cs               # NEW — Pass 1a
    │   │   ├── TextProcessingHandlerLlmTests.cs            # NEW — Pass 1b
    │   │   └── IngestSagaWorkerTests.cs                    # NEW (claim contention, lease, LISTEN/NOTIFY)
    │   ├── Sync/Pull/SyncPullEndpointTests.cs              # NEW
    │   └── Settings/SettingsEndpointTests.cs               # NEW — Pass 1b
    ├── Infrastructure/
    │   ├── Storage/FilesystemNoteStoreTests.cs             # NEW
    │   └── Llm/AnthropicLlmClientTests.cs                  # NEW — Pass 1b; HttpMessageHandler fake; live trait
    └── WalkingSkeletonEndToEndTests.cs                     # NEW — the regression anchor for this whole ticket
```

## Saga state machine (processing)

Simpler than provisioning — no rollback. Mirrors `ADR-0033`'s discipline but with two terminal-non-success states (`failed` for the artifact, `dead_lettered` for the job after retry budget).

```text
ingest_jobs.status:

queued ──[saga claims; FOR UPDATE SKIP LOCKED]──► processing
processing ──[handler returns ok]──► succeeded                (terminal)
processing ──[handler throws; attempts < 3]──► queued         (with scheduled_at = now + 2^attempts s)
processing ──[handler throws; attempts >= 3]──► dead_lettered (terminal)
processing ──[lease TTL expires; another worker claims]──► processing  (sweeps every 30s)

artifacts.status:

queued ──[job claimed]──► processing
processing ──[job succeeded]──► ready
processing ──[job dead_lettered]──► failed
```

Notes:
- `lease_owner` is the `IngestSagaWorker`'s instance GUID (one per process). `lease_expires_at = now + 60s`; renewed on every handler heartbeat. Sweeper compares `lease_expires_at < now()` to detect crashed workers.
- `LISTEN ingest_jobs_new` on a dedicated Npgsql connection (separate from the worker's EF connection) — same pattern as `Portal.SagaWorker.SagaWorker.cs`. Notify on every successful insert into `ingest_jobs`.
- No `pg_notify` ordering guarantees; `LISTEN` is a wakeup hint, not a queue. The claim loop is the source of truth.

## Open contract decisions

These are not blockers for CLOUD-002 but should be **named in the handoff** so the next reader doesn't re-open them silently:

1. **`ThanyMarcus.Shared` namespace for plugin DTOs.** ADR-0025 specifies hand-written TS types for the SPA; the plugin is a different consumer (TypeScript, Obsidian-side, no SvelteKit toolchain). Two reasonable answers:
   - (a) put plugin DTOs in `ThanyMarcus.Shared/Plugin/` and emit TS by re-running ADR-0025's hand-write discipline against the plugin's own type file
   - (b) create `ThanyMarcus.Shared.Plugin` as a sibling project so the cloud doesn't drag the SPA's Svelte assumptions into the plugin's build

   **Decision in this ticket:** option (a). Add files under `src/ThanyMarcus.Shared/Plugin/` and `src/ThanyMarcus.Shared/CloudAdmin/`. Defer (b) until the plugin actually exists and the friction is real.

2. **Bearer-token rate limit on `/api/ingest/text`.** Portal.Api ships with `RateLimiterServiceCollectionExtensions.cs`; the cloud doesn't have a rate-limit story yet. Out of scope: add in CLOUD-007 along with `/shared/{token}`'s per-IP limit. For CLOUD-002, the ingest endpoint is **single-tenant by definition** — the user attacks themselves if anyone — so rate-limit-free is acceptable for the walking skeleton.

3. **`cloud_admin_token` storage shape** (the PORTAL-016 `// FORK:` note). Still open per `cloud-pivot-plan-2026-05-13.md §28 Q-AdminTokenStorage`. CLOUD-002's `RequireCloudAdminTokenFilter` constant-time-compares against the **plaintext bootstrap-seeded value** (which is what the cloud already has from CLOUD-001's `BootstrapOptions.CloudAdminToken`). This is consistent with ADR-0034 §1's design intent; the *portal* still stores a hash today. The portal-cloud `/admin/*` proxy (PORTAL-014) will need to resolve this divergence before it can call into the endpoints CLOUD-002 lands.

4. **ADRs to author.** No new ADRs are strictly required for CLOUD-002 — every decision is already covered by ADR-0019/0024/0028/0033/0035 + `cloud-pivot-plan-2026-05-13.md`. If a reviewer wants explicit documentation, two candidates:
   - **ADR-0039: Processing saga state machine** (parallels ADR-0033 for provisioning) — captures `queued→processing→succeeded|dead_lettered` shape, lease/retry policy, `LISTEN/NOTIFY` wakeup.
   - **ADR-0040: LLM-client abstraction + unsafe-mode default-off posture** — captures the safe/unsafe enum, no-op safe-mode in MVP, tool-use JSON pattern, encrypted key storage.
   Author them as a 0.25-day batch after CLOUD-002 ships, before CLOUD-003 picks up.

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors. `dotnet test` green including the live-API trait when `ANTHROPIC_API_KEY` is present (live trait is skipped if not).
2. ✅ `docker compose -f infra/docker/cloud/docker-compose.yml up` boots `cloud-api` + `postgres` + `caddy`, applies migrations on `cloud-api` startup, and `/health/ready` returns 200.
3. ✅ With a dev-seeded plugin token + bootstrap admin token, the manual smoke is:
   ```bash
   # 1. ingest
   curl -sS -X POST https://localhost/api/ingest/text \
     -H "Authorization: Bearer $PLUGIN_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"clientArtifactId":"smoke-001","content":"meeting with Anna about Berlin trip","capturedAt":"2026-05-17T10:00:00Z"}'
   # → 202 {"artifactId":"<uuid>","status":"queued"}

   # 2. (Pass 1a) sync immediately — Pass 1a returns identity-transform Markdown
   sleep 2
   curl -sS https://localhost/api/sync/pull -H "Authorization: Bearer $PLUGIN_TOKEN"
   # → items[0].relativePath = "Inbox/<uuid>.md"
   # → items[0].body contains "## Content\n\nmeeting with Anna about Berlin trip"

   # 3. (Pass 1b) configure unsafe mode, re-ingest with new client_artifact_id
   curl -sS -X PUT https://localhost/admin/settings \
     -H "Authorization: Bearer $CLOUD_ADMIN_TOKEN" \
     -d '{"llmMode":"unsafe_anthropic","externalApiKey":"sk-ant-...","llmModel":"claude-haiku-4-5-20251001"}'
   curl -sS -X POST https://localhost/api/ingest/text \
     -H "Authorization: Bearer $PLUGIN_TOKEN" \
     -d '{"clientArtifactId":"smoke-002","content":"meeting with Anna about Berlin trip","capturedAt":"2026-05-17T10:00:00Z"}'
   sleep 5  # LLM call adds 2-3 s
   curl -sS "https://localhost/api/sync/pull?since=$(date -u -v+1S +%Y-%m-%dT%H:%M:%SZ)" \
     -H "Authorization: Bearer $PLUGIN_TOKEN"
   # → items[0].relativePath = "Projects/Travel/<uuid>.md"
   # → items[0].body contains "meeting with [[Anna]] about [[Berlin]] trip"
   # → items[0].tags includes "travel" or similar
   ```
4. ✅ `WalkingSkeletonEndToEndTests.cs` is green and **does not require the live API** (uses a fake `ILlmClient` registered in the test factory; the LLM path is exercised separately by `TextProcessingHandlerLlmTests.cs` + the live trait).
5. ✅ All seven `Features/{PluginAuth,Ingest,Processing,Sync,Settings}/` directories now have at least one real `.cs` file (the `.gitkeep` policy from CLOUD-001 is satisfied table-by-table). The remaining `.gitkeep`s (`Knowledge/`, `Sharing/`) stay.
6. ✅ Migration apply on a fresh empty DB succeeds; migration apply on a DB already at `0001` advances to `0002` cleanly; rolling back `0002` and re-applying is idempotent (manual verification via `dotnet ef database update` against a scratch DB).
7. ✅ No `// TODO` markers left in shipped code paths. Where a future ticket is genuinely needed, leave a `// FORK:` block-comment marker (the same convention CLOUD-001 introduced) explicitly naming the follow-on ticket — currently expected at: the dev-seeded plugin-token path (forks to portal-cloud token sync), the `RequireCloudAdminTokenFilter` bootstrap-token compare (forks to CLOUD-003's proper admin auth), the `OpenAiLlmClient.cs` stub (forks to its own ticket).

## What the next ticket (CLOUD-003) inherits

After CLOUD-002 lands:
- The saga, storage, sync, and plugin-auth spine are reusable for any modality. Adding URL = new `IIngestJobHandler` impl + new request type + new endpoint + Readability call inside the handler. **No saga changes.** No new tables. The existing `artifacts.kind` check constraint gets relaxed in a tiny migration.
- The LLM client abstraction is reusable for any text-bearing modality. URL/PDF/file all funnel into the same `EnrichTextAsync` call after their respective text-extraction step.
- The provenance audit field (`notes.llm_mode`) is the seed for the eventual full provenance JSON.
- The settings surface is reusable for "switch LLM model", "rotate API key", "toggle safe/unsafe", and (later) "set burst worker size."

CLOUD-003's expected scope (URL + PDF + drag-drop file) is ~2 person-days *because* CLOUD-002 paid the spine cost up front. The cumulative thesis-demo arc is: CLOUD-002 (text demo) → CLOUD-003 (URL/PDF/file demo) → PLUGIN-001 (Obsidian plugin replaces curl) → CLOUD-016b/c + voice (burst worker demo) → CLOUD-005 (entity hubs, the "emergent graph" demo). Each adds one chapter to the FR table in §26 of `cloud-pivot-plan-2026-05-13.md`.
