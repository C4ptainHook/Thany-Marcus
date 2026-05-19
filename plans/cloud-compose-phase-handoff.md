# CLOUD-COMPOSE-PHASE — per-kind render templates + frontmatter + body_output write + compose-v1 versioning — Handoff Brief

Date: 2026-05-19
Status: Draft. Handoff #6 of the ADR-0042 implementation series. **Replaces the CLOUD-002-vintage `CompositeMarkdownAssembler` with a structured per-kind composer** and lands the `compose-v1` template version as a first-class artifact on `notes` + provenance. The orchestrator's `ComposingHandler` from `cloud-ingest-saga-foundation-handoff.md` (handoff #3) currently calls the legacy assembler with `enrichment=null` — that call site is rewritten here, but the phase machine, lease, retry, SSE, and cancellation logic are not touched. Real LLM phases (`routing`, `extracting_entities`) continue to pass through with `NoOpLlmClient`; the route from `composing → routing → extracting_entities → embedding` runs end-to-end against the new body output but with no project routing and no entity mentions. **LLM phases land in handoff #7.**

**Goal:** replace `CompositeMarkdownAssembler.Assemble(note, attachments, enrichment, llmMode)` with a `CompositeNoteComposer` that dispatches per attachment kind to one of six renderers (`UrlRenderer`, `ImageRenderer`, `AudioRenderer`, `DocumentRenderer`, `VideoRenderer`, `FailedHiddenRenderer`), assembles YAML frontmatter from `notes` + the resolved attachment kinds, stitches per-kind blocks into a deterministic Markdown body under the `## System Output` heading, writes `notes.body_output`, and stamps `compose-v1` into both the frontmatter and the job's provenance summary. After this ticket: a composite-ingest run produces a vault-ready Markdown file whose shape is locked under a versioned template, the renderers consume only `attachments.extracted_text` + `attachments.extra` + `attachments.kind` + `attachments.parent_attachment_id` (so the composer is oblivious to which extractor produced the text), failed extractions are hidden behind HTML comment markers (no error noise in the rendered note), and the `compose_template` field on `notes` lets future template bumps trigger plugin-side regen prompts without touching the saga.

Estimated **1.5–2 person-days** with AI-agent assistance, split into two passes. Pass A (~1d) lands the six renderers + frontmatter builder + composer + DI swap inside `ComposingHandler`. Pass B (~0.5–1d) wires `compose-v1` versioning into provenance + frontmatter, lands the `failed-hidden` marker semantics, and updates the Pass-C regression anchor from handoff #3 to assert the new body shape. Estimate is rough — it compresses to 1.25d if YamlDotNet is already a transitive dep (likely; SmartReader doesn't pull it but Caddy config tooling may); it stretches to 2d if video-children rendering (keyframe block + audio block under a parent video) surfaces edge cases the surrounding handoffs didn't anticipate.

This handoff **does not** ship: real LLM client wiring (handoff #7); any change to `routing` / `extracting_entities` / `embedding` phase handlers (they continue to pass through unchanged); any schema migration (every column the composer reads/writes already exists from `cloud-schema-v2-handoff.md`); any new SSE event vocabulary; any plugin-side parser. The composer's output is consumed only by the plugin's vault writer, which already reads YAML frontmatter + Markdown body — no contract change.

## Where decisions live (read before doing anything)

- **`docs/decisions/0045-composite-note-schema.md`** — §"`notes` table" + §"`attachments` table". The columns this composer reads (`notes.id`, `notes.captured_at`, `notes.llm_mode`, `notes.relative_path`, `notes.body_output`, `attachments.kind`, `attachments.extracted_text`, `attachments.extraction_status`, `attachments.extra`, `attachments.parent_attachment_id`, `attachments.url`, `attachments.mime_type`, `attachments.storage_key`, `attachments.original_filename`) are the read-set; only `notes.body_output` is written here. The `attachments.extracted_text` canonical form (`Description:\n...\n\nText:\n...` for images; raw transcription text for audio; raw Docling markdown for documents; SmartReader markdown for URLs) was locked by handoffs #4 and #5 and is the composer's input contract.
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §3 phase machine (the `composing` phase is what this ticket rewrites). §4 best-effort cascade lands here as the "failed-hidden" semantics: a failed extraction does NOT block compose, it renders as a hidden HTML comment marker. §10c provenance schema — the `compose_template` field is added to the materializer in Pass B.
- **`docs/decisions/0044-cloud-intelligence-layer.md`** — §"VLM prompt template" + §"VLM output canonical form". Image attachments arrive at the composer with `extracted_text = "Description:\n<desc>\n\nText:\n<ocr>"` (or just `Description:\n<desc>` if `text_in_image` was null). **The composer does not parse this** — it copies it verbatim under a `### Image` heading. If a future ticket wants to re-shape the description/OCR split, the renderer changes; this ticket trusts the canonical form. Same logic for Docling (`extracted_text` is already valid Markdown) and Parakeet (`extracted_text` is plain transcription text).
- **`docs/decisions/0020-server-push-sse.md`** — **not directly applied** (no new SSE events) but the `note_phase_changed(composing → routing)` event continues to fire after the composer writes `body_output`. The composer should write `body_output` BEFORE the phase transition (handoff #3's `ComposingHandler` already does this; the new composer slots into the same call site).
- **`docs/decisions/0028-schema-conventions.md`** — `Instant` timestamps via NodaTime; the frontmatter `captured_at` field must serialize as ISO-8601 with `Z` suffix (no offset; the cloud's writer normalizes all timestamps to UTC). Use `Instant.ToString("uuuu-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)` to match the portal's existing pattern.
- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers. The composer is pure (no DB writes inside the renderer code path; only `ComposingHandler` writes `notes.body_output`), so renderer-level tests don't need Testcontainers — plain xUnit theories with seeded `Attachment` records suffice. Only `CompositeNoteComposerTests` (the integration test for the composer-against-the-DB call) needs the Postgres fixture.
- **`plans/cloud-schema-v2-handoff.md`** — schema this ticket reads. `notes.relative_path` defaults to `Inbox/{noteId}.md` from handoff #3's `ComposingHandler` and stays that way until handoff #7's router writes project paths. `attachments.kind` is a constants-class string (`AttachmentKind.Url`, `Image`, `Voice`, `Video`, `File`) — the composer's dispatch keys off this exact string set.
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — the framework this ticket modifies. **Only `ComposingHandler.cs` is touched.** The orchestrator, dispatcher, retry/lease/heartbeat, SSE bus, reprocess/cancel endpoints, provenance materializer (for everything except the `compose_template` field) all stay byte-for-byte. The handler's contract — "called with the locked job + note + attachments; transitions to `routing` on success; throws on failure for retry budget" — is preserved.
- **`plans/cloud-processors-light-handoff.md`** — handoff #4 fixes the canonical input shape for image + URL attachments (the composer's renderers read this shape). The `extra` JSONB blob's `from_cache`, `phash`, `dimensions`, `exif`, `model_response` keys are populated by VLM/URL/Docling/Parakeet; the composer **does not consume** any of these for the rendered body, but the frontmatter assembly does consume `attachments.url` on URL kind for the `## Source` line.
- **`plans/cloud-processors-heavy-handoff.md`** — handoff #5 nails down the video parent + child attachment shape. The parent video attachment carries `extracted_text=null` and `extraction_status='extracted'` (the splitter "succeeds" by fan-out; there's nothing to extract from the parent bytes directly). The children are `kind='image'` (keyframes) and `kind='voice'` (audio track), each with `parent_attachment_id = <video id>` and their own `extracted_text` from `VlmWorker` and `ParakeetWorker`. **The composer renders top-level attachments and groups children under their parent's block** — the SQL filter is `parent_attachment_id IS NULL` for the top-level iteration, then a second query (or in-memory grouping) pulls children per parent.
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note. The composer respects this: one `notes.body_output` write per composing phase entry. No fan-out into multiple files; child attachments render inline.
- **Memory `portal_tooling.md`** — .NET 10, warnings-as-errors, Shouldly + xUnit v3.

**Do not litigate the per-kind canonical-text shape from handoffs #4/#5.** If a renderer feels like it needs to re-format the image OCR or re-flow the Docling markdown, raise it as an ADR-0045 amendment in a follow-up; the composer copies extractor output verbatim under a heading and trusts the extractor.

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **The composer is a pure C# class, no IHostedService, no DB writes.** `CompositeNoteComposer.Compose(Note note, IReadOnlyList<Attachment> attachments)` returns a `ComposedNote { string Frontmatter, string Body, string ComposeTemplateVersion }` record. `ComposingHandler` is the only caller; it concatenates `"---\n" + Frontmatter + "---\n\n" + Body` and assigns to `note.BodyOutput`. The composer never opens a transaction, never queries pgvector, never calls a sidecar. Renderers are also pure — they take an `Attachment` (+ children list for video) and return a string. **This is deliberate** — it makes the composer trivially unit-testable against fixture data and avoids the temptation to fold orchestration concerns ("should this re-run the LLM?") into rendering.

2. **`compose-v1` is a code-level constant.** `public const string ComposeTemplateVersion = "compose-v1";` lives at `Features/Processing/Composing/CompositeNoteComposer.cs`. Bumping to `compose-v2` is a single source-code change; old notes carry `compose_template: compose-v1` in frontmatter and the saga's reprocess path re-composes them under the new template (because `ComposingHandler` always runs against current code). **No DB-side enumeration of valid versions** — the field is a free-form string in frontmatter + provenance. The plugin can warn the user "your note was composed under compose-v1; current is compose-v2; reprocess to update?" using a simple string compare.

3. **Six renderers, one interface, dispatch via dictionary.**
   ```csharp
   public interface IKindRenderer
   {
       string Kind { get; }
       string Render(Attachment attachment, IReadOnlyList<Attachment> children, IRenderContext ctx);
   }
   ```
   Implementations:
   - `UrlRenderer` (kind `url`)
   - `ImageRenderer` (kind `image`)
   - `AudioRenderer` (kind `voice`)
   - `VideoRenderer` (kind `video`)
   - `DocumentRenderer` (kind `file`)
   - `FailedHiddenRenderer` (special — selected when `attachment.extraction_status == "failed"` regardless of kind)
   
   The composer's dispatch:
   ```csharp
   if (att.ExtractionStatus == AttachmentExtractionStatus.Failed)
       return _renderers["failed"].Render(att, [], ctx);
   return _renderers[att.Kind].Render(att, childrenByParent[att.Id], ctx);
   ```
   `_renderers` is an `IReadOnlyDictionary<string, IKindRenderer>` populated by DI from the registered `IKindRenderer` set, keyed on `Kind`. **The `FailedHiddenRenderer` registers under the synthetic key `"failed"`** — it never appears in `attachments.kind`; the dispatch table reserves it explicitly. Mis-registration (two renderers with the same `Kind`) throws at DI build time, not at runtime.

4. **`failed-hidden` semantics.** A failed extraction renders as a single HTML comment marker, nothing else:
   ```markdown
   <!-- thany-marcus:attachment id=<guid> kind=<kind> status=failed reason=<error_msg> -->
   ```
   The comment is on its own line, surrounded by blank lines so Obsidian's renderer doesn't fold it into adjacent paragraphs. The `<reason>` is `attachment.extraction_error` (already populated by handoff #3's worker-base retry-exhaust path). Comments are invisible in rendered Markdown but addressable by a plugin "View processing details" UI in a future ticket — the canonical machine-readable form is the same shape on every failed attachment, so a regex can find them. **Skipped attachments are NOT hidden** — `extraction_status='skipped'` means either a pre-flight reject (image was too small) OR a cache hit (which renders normally because `extracted_text` is populated). The distinction:
   - `skipped` + `extracted_text` is non-null → cache hit; renders as if extracted.
   - `skipped` + `extracted_text` is null → pre-flight reject; renders as a hidden marker with `status=skipped reason=<extraction_error>`.
   - `failed` → always hidden marker.
   
   **The `FailedHiddenRenderer` handles both `failed` and `skipped-with-null-text`.** Its key is still `"failed"` in the dispatch table; the composer's branch checks `(status == Failed) OR (status == Skipped AND extracted_text IS null)` to route there. Document in code with a one-line "non-obvious why" comment.

5. **Frontmatter is YAML serialized via `YamlDotNet`.** Hand-rolled string concatenation invites quoting bugs (`captured_at: 2026-05-19T10:00:00.000Z` is fine, but a note title containing a colon or a special char breaks naive concat). YamlDotNet is the canonical .NET YAML library, MIT-licensed, in active use across the ecosystem. **Verify at apply time** that it's not already pulled in transitively (`dotnet list package --include-transitive | grep -i yaml`); if absent, add to `Directory.Packages.props` (`Version="15.*"`). The serializer uses `Serializer.WithNamingConvention(LowerCaseNamingConvention.Instance)` so C# `CapturedAt` → YAML `capturedat` (no underscore — match the convention by spelling field names lowercase in C# `record FrontmatterDto`). **Alternative:** hand-spell each field in `record FrontmatterDto(string Id, string CapturedAt, string Modality, ...)` and use `UnderscoredNamingConvention` so the YAML reads `captured_at:`. Pick this — readable YAML matters more than ergonomic C#.

6. **Frontmatter field set is exactly these keys, in this order:**
   ```yaml
   id: <noteId-uuid>
   captured_at: <ISO-8601-Z>
   modality: composite
   attachment_kinds: [url, image, voice, file, video]
   source: plugin
   llm_mode: safe
   compose_template: compose-v1
   ```
   - `id` is the note UUID.
   - `captured_at` is `notes.captured_at` (set by `/finalize` in CLOUD-002).
   - `modality` is always `composite` until the schema admits other shapes (it doesn't, per ADR-0045).
   - `attachment_kinds` is the **distinct sorted list** of `attachments.kind` values for this note (filtered to `parent_attachment_id IS NULL`; children's kinds are implied by their parent). Empty list `[]` is valid (note with no attachments).
   - `source` is hardcoded `plugin` for MVP (the only ingest path is the plugin's composite-ingest endpoint).
   - `llm_mode` is `notes.llm_mode` (`safe` until unsafe mode lands).
   - `compose_template` is the constant `compose-v1`.
   
   **No `title:` field.** The plugin titles notes from filename, not frontmatter. **No `tags:` field.** Tags are user-edited; the composer doesn't invent them. **No `entities:` or `wikilinks:` fields.** Both are populated by handoff #7's LLM phases; until then, neither field would have a value. Bumping the template to `compose-v2` is the path for adding fields when LLM lands.

7. **`## User Notes` and `## System Output` are the body's two-heading skeleton.** Per cloud-pivot plan §15:
   ```markdown
   ## User Notes
   
   <!-- user edits preserved across reprocess; left empty on initial compose -->
   
   ## System Output
   
   <per-attachment blocks here>
   ```
   The user-notes section is initialized **empty** (one blank line between the heading and the HTML comment hint; the hint is for vault readers who open the raw file). On reprocess, the composer **preserves whatever is currently between `## User Notes` and `## System Output` markers in `notes.body_output`** — re-running compose splices new System Output but does not touch User Notes. This is the user-edit-survives-reprocess invariant from the cloud-pivot plan §19. **Implementation:** the composer's `Compose(...)` signature accepts an optional `string? previousBodyOutput` parameter; on non-null, it parses the previous body for the User Notes block (regex `## User Notes\n([\s\S]*?)\n## System Output` matches; group 1 is the preserved content) and re-uses it. On null (first compose), it writes the empty placeholder. `ComposingHandler` passes `note.BodyOutput` (which may be null on first run, non-null on reprocess).

8. **Per-kind block shape — locked.** Each renderer emits a `### <Heading>` followed by attachment-specific content, with a trailing blank line. Headings are:
   - URL: `### Source: <title-or-canonical-url-or-original-url>`
   - Image: `### Image` (no per-image title — the description is the content)
   - Audio: `### Voice memo` (use `notes.original_filename` if non-null for `### Voice memo: <filename>`)
   - Video: `### Video` (or `### Video: <filename>`)
   - Document: `### Document: <filename-or-"untitled">`
   
   Content per kind:
   - **URL:** rendered article markdown verbatim from `extracted_text`. Above it, on its own line, a link: `Source: <attachments.url>` (canonical URL preferred — if `extra.canonical_url` is set, use it; otherwise the original `attachments.url`).
   - **Image:** the canonical `Description:\n...\n\nText:\n...` blob from `extracted_text` (image's `extracted_text` already has this exact shape from `OllamaVlmClient`; the renderer copies it verbatim). Above it, the image embed: `![](<presigned-url>)`. The presigned URL is minted at render time via `IArtifactStore.GetPresignedDownloadUrlAsync(bucket, attachment.StorageKey, ttl: TimeSpan.FromDays(7), ct)` — **NOT** at upload time, because presigned URLs have TTLs and a 5-min TTL would expire before the user opens Obsidian. The 7-day TTL is the compromise; on URL expiry the plugin re-fetches via `/api/sync/pull` which re-mints. Document the TTL in the renderer with one line; the constant lives at `CompositeNoteComposer.ImagePresignedUrlTtl`.
   - **Audio:** plain transcription text from `extracted_text`. Above it, a short non-clickable reference: `[audio: <original-filename-or-storage-key>]` on its own line. No presigned URL — Obsidian doesn't render audio inline from external URLs reliably, and the plugin separately downloads asset bytes to the vault's `.attachments/` folder (post-thesis feature; for now the reference is informational only).
   - **Document:** the Docling markdown verbatim from `extracted_text`. Above it: `[document: <original-filename>]` reference. Same non-clickable reasoning as audio.
   - **Video:** the parent video has `extracted_text=null` but children carry the real content. Layout:
     ```markdown
     ### Video: <filename>
     
     [video: <storage-key>]
     
     **Audio:**
     <child voice's extracted_text>
     
     **Keyframes:**
     1. <child image 1's extracted_text>
     2. <child image 2's extracted_text>
     ...
     ```
     Each keyframe block is the canonical `Description:\n...\n\nText:\n...` from its child image attachment. If a child is `failed-hidden`, its block is replaced by the hidden marker (still inside the `1.` numbering — the marker takes one slot). If the video has no children (`StubVideoSplitterClient` returns empty splits), the `**Audio:**` and `**Keyframes:**` subheadings are omitted and the block is just the heading + reference line. **Ordering of children** is by `attachments.created_at ASC` for keyframes (the splitter inserts them in keyframe-time order); the audio child is rendered above keyframes regardless of insertion order (one audio per video by construction).

9. **`### Source:` heading title resolution for URLs.** Title order of preference (composer picks the first non-null/non-empty):
   1. `extra.title` (SmartReader's article title, populated by `UrlExtractor`)
   2. `extra.canonical_url` (if it differs from `attachments.url`, gives a stable id)
   3. `attachments.url` (always present)
   
   Truncate the displayed title to 80 chars (with `…` suffix on overflow) so it doesn't blow up the heading line. The full URL still appears below as the `Source:` link.

10. **The composer accepts a missing `extracted_text` gracefully.** Every kind's renderer checks `string.IsNullOrWhiteSpace(att.ExtractedText)` and either:
    - For URL/Image/Audio/Document: renders the heading + reference + a single line `<!-- no extracted content -->` (this case shouldn't happen — `extraction_status='extracted'` implies non-null text — but defending against it costs nothing).
    - For Video: renders heading + reference, omits `**Audio:**` / `**Keyframes:**` subblocks if children are also empty.

11. **Two-pass attachment loading.** `ComposingHandler` issues one EF query:
    ```csharp
    var attachments = await db.Attachments
        .Where(a => a.NoteId == job.NoteId)
        .OrderBy(a => a.CreatedAt)
        .ToListAsync(ct);
    ```
    The composer partitions into `topLevel = attachments.Where(a => a.ParentAttachmentId == null)` and `childrenByParent = attachments.Where(a => a.ParentAttachmentId != null).ToLookup(a => a.ParentAttachmentId!.Value)`. Iteration is over `topLevel` in `CreatedAt` order; child lookup is O(1). **No second DB query.** This keeps the composer pure; `ComposingHandler` owns the IO.

12. **`ComposingHandler` retains its retry budget.** Per handoff #3 §3 table: `composing` has `MaxAttempts=2`, `BackoffSecondsBase=2`. If the composer throws (e.g., YamlDotNet serialization fails on an exotic note title), the handler increments attempts and re-schedules; second failure → `failed_composition` terminal. The composer's renderers should be exception-free against the input contract; the only realistic throw is the presigned-URL mint call hitting Spaces/MinIO transiently. Wrap that call in a try/catch that downgrades to the bare `[image: <storage-key>]` reference (no embed) and logs a warning; the embed is a nice-to-have, not a correctness gate. **Decision:** swallow the Spaces error in the renderer, render without embed, do NOT throw. Composer-throwing semantics are reserved for genuine assembly bugs (a kind the dispatch table doesn't know about; a YamlDotNet exception).

13. **`compose-v1` lands in two places.**
    - **Frontmatter:** the YAML field `compose_template: compose-v1`.
    - **Provenance:** the `notes.provenance.compose_template` JSON field, written by `ProvenanceMaterializer.Build(...)` at terminal time. **Extend the existing materializer** from handoff #3 to read `note.BodyOutput.Substring(...)` — no, easier — read it from the constant directly. Pass the value into the materializer as a parameter from `ComposingHandler` after compose runs:
      ```csharp
      job.LastComposeTemplate = CompositeNoteComposer.ComposeTemplateVersion;
      ```
      where `IngestJob` gets a new `[NotMapped]` property `LastComposeTemplate` set in the handler and read by the materializer at the `succeeded` terminal. **Why `[NotMapped]`** — we don't want a DB column for this (it's a derived value from frontmatter), but we do want to pass it through the handler chain without re-parsing YAML in the materializer.

14. **Routing + extracting_entities + embedding phases stay unchanged.** Their handlers from handoff #3 continue to run with `NoOpLlmClient` + `StubEmbeddingClient`. The composer writes `body_output`; routing reads `body_output` to decide project (always returns null in stub mode); extracting_entities reads `body_output` to find mentions (always empty); embedding reads `body_output` to embed (zero vector). **None of these handlers reach into the per-attachment renderers.** They read the final assembled string. This is the seam the LLM phases in handoff #7 inherit.

15. **DI registration shape.** Each renderer is registered as `services.AddSingleton<IKindRenderer, UrlRenderer>()` etc. The composer takes `IEnumerable<IKindRenderer>` in its constructor and builds the dispatch dictionary itself, throwing on duplicates. The `FailedHiddenRenderer` registers separately under the same interface; its `Kind` property returns the synthetic `"failed"` value. **Test convenience:** registering a fake renderer in tests overrides via `services.RemoveAll<IKindRenderer>()` + `services.AddSingleton<IKindRenderer, FakeUrlRenderer>()` — tests can swap individual renderers without rebuilding the whole stack.

16. **Empty composite note.** A note with zero attachments (technically supported by the schema but not by the plugin yet) renders:
    ```markdown
    ---
    <frontmatter with attachment_kinds: []>
    ---
    
    ## User Notes
    
    <!-- user notes -->
    
    ## System Output
    
    <!-- no attachments -->
    ```
    The composer does NOT fail; the System Output section gets a single hidden comment instead of per-kind blocks. This keeps round-tripping deterministic and avoids a "this note has no body" edge case downstream.

17. **Markdown escape semantics.** Inside renderer content, the composer assumes its extractors return well-formed Markdown OR plain text — it does not escape characters. `Description:\n...` from VLM contains plain text; `<docling markdown>` is intentional Markdown; SmartReader output is intentional Markdown. **The composer does not re-escape.** If an extractor returns content that contains `## ` at the start of a line, it WILL render as a heading inside our `### Image` block — that's the extractor's contract to behave. The renderer **does** check that `extracted_text` does not contain the literal string `\n## System Output\n` (which would break the User Notes / System Output split on reprocess); if it does, the renderer replaces with `\n\\## System Output\n` (backslash-escaped first hash). This is paranoid but free — one `.Contains()` check per attachment.

## Scope boundary (precise)

Two passes, totaling ~1.5–2 days. The split is "lay the structure" then "version + cleanup + regression integration."

### Pass A — six renderers + frontmatter builder + composer + ComposingHandler rewrite (~1d)

The composer + renderers are all new code under `Features/Processing/Composing/`. `CompositeMarkdownAssembler.cs` is **deleted** (legacy CLOUD-002 file). `ComposingHandler` keeps its existing call-site shape but invokes the new composer.

**1. `IKindRenderer` interface + the six implementations.** Each at `Features/Processing/Composing/Renderers/<Name>Renderer.cs`:
   - `IKindRenderer.cs` — the interface (see §3 above).
   - `UrlRenderer.cs` — reads `extracted_text` + `extra.title` + `extra.canonical_url` + `attachments.url`; emits `### Source: <title>\n\n<url-line>\n\n<markdown>\n`.
   - `ImageRenderer.cs` — reads `extracted_text` + `attachments.storage_key`; mints presigned URL via `IArtifactStore`; emits `### Image\n\n![](<presigned-url>)\n\n<extracted-text>\n`. Wraps the presign call in try/catch; on failure emits `### Image\n\n[image: <storage-key>]\n\n<extracted-text>\n` + structured warn log.
   - `AudioRenderer.cs` — reads `extracted_text` + `attachments.original_filename` + `attachments.storage_key`; emits `### Voice memo[: <filename>]\n\n[audio: <filename-or-key>]\n\n<extracted-text>\n`.
   - `VideoRenderer.cs` — reads parent + children list; emits the heading + `[video: ...]` reference + optional `**Audio:**` block (from the single voice child) + optional `**Keyframes:**` numbered list (each item is the keyframe child's `extracted_text` or its failed-hidden marker). Children are passed in via the `IRenderContext` parameter; the renderer does not re-query the DB.
   - `DocumentRenderer.cs` — reads `extracted_text` + `attachments.original_filename`; emits `### Document: <filename>\n\n[document: <filename>]\n\n<extracted-text>\n`.
   - `FailedHiddenRenderer.cs` — reads `attachments.id` + `attachments.kind` + `attachments.extraction_status` + `attachments.extraction_error`; emits `<!-- thany-marcus:attachment id=<id> kind=<kind> status=<status> reason=<error-or-"unknown"> -->\n`. The `<reason>` value is `Regex.Replace(att.ExtractionError ?? "unknown", @"[\r\n-]+", " ").Trim()` — strips newlines and dashes so the comment stays on one line and can't be mistaken for an HTML comment closer.

**2. `IRenderContext` shape:**
   ```csharp
   public interface IRenderContext
   {
       IReadOnlyList<Attachment> ChildrenFor(Guid parentAttachmentId);
       string? TryGetPresignedDownloadUrl(string storageKey);  // null on failure
   }
   ```
   Concrete: `RenderContext(IReadOnlyDictionary<Guid, IReadOnlyList<Attachment>> childrenByParent, IArtifactStore artifactStore, ILogger logger)`. The `TryGetPresignedDownloadUrl` swallows `IArtifactStore` exceptions and returns null; the renderer falls back to the bare `[image: <key>]` reference. **Decision:** keep the context narrow; do NOT pass `Note` or `IngestJob` — renderers care only about their own attachment + children + presigning.

**3. `CompositeNoteComposer.cs`:**
   ```csharp
   public sealed class CompositeNoteComposer(
       IEnumerable<IKindRenderer> renderers,
       IArtifactStore artifactStore,
       ILogger<CompositeNoteComposer> logger)
   {
       public const string ComposeTemplateVersion = "compose-v1";

       public ComposedNote Compose(Note note, IReadOnlyList<Attachment> attachments, string? previousBodyOutput)
       {
           // 1. Partition into top-level + children-by-parent.
           // 2. Build frontmatter (FrontmatterBuilder.Build(note, attachments, ComposeTemplateVersion)).
           // 3. Extract preserved User Notes block from previousBodyOutput (or empty placeholder).
           // 4. Render each top-level attachment via dispatch table → list of strings.
           // 5. Stitch: "## User Notes\n\n<preserved>\n\n## System Output\n\n" + string.Join("\n", blocks).
           // 6. Return ComposedNote(frontmatter, body, ComposeTemplateVersion).
       }
   }

   public sealed record ComposedNote(string Frontmatter, string Body, string ComposeTemplateVersion);
   ```
   The composer's constructor validates the renderer dictionary (no duplicate `Kind` keys; `"failed"` key exists; each first-class kind from `AttachmentKind` constants has a renderer registered). Throws `InvalidOperationException` at construction if the invariants don't hold; the host fails to start. **This is the right place** — catching it at DI registration time is too early (renderers register before composers); at request time is too late.

**4. `FrontmatterBuilder.cs` (`Features/Processing/Composing/`):**
   ```csharp
   public static class FrontmatterBuilder
   {
       public static string Build(Note note, IReadOnlyList<Attachment> topLevelAttachments, string composeTemplate)
       {
           var dto = new FrontmatterDto(
               Id: note.Id.ToString(),
               CapturedAt: note.CapturedAt.ToString("uuuu-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
               Modality: "composite",
               AttachmentKinds: topLevelAttachments.Select(a => a.Kind).Distinct().OrderBy(k => k).ToList(),
               Source: "plugin",
               LlmMode: note.LlmMode,
               ComposeTemplate: composeTemplate);
           
           var serializer = new SerializerBuilder()
               .WithNamingConvention(UnderscoredNamingConvention.Instance)
               .DisableAliases()
               .Build();
           return serializer.Serialize(dto);
       }

       private sealed record FrontmatterDto(
           string Id,
           string CapturedAt,
           string Modality,
           IReadOnlyList<string> AttachmentKinds,
           string Source,
           string LlmMode,
           string ComposeTemplate);
   }
   ```
   `DisableAliases()` prevents YamlDotNet from emitting `&anchor`/`*alias` references on repeated strings (which would baffle a Markdown reader). The resulting YAML is plain, no aliases, with `attachment_kinds: [url, image]` inline-flow for the list.

**5. `UserNotesPreserver.cs` (`Features/Processing/Composing/`):**
   ```csharp
   public static class UserNotesPreserver
   {
       private static readonly Regex Pattern = new(
           @"## User Notes\n([\s\S]*?)\n## System Output",
           RegexOptions.Compiled);

       public static string Extract(string? previousBody)
       {
           if (string.IsNullOrEmpty(previousBody)) return EmptyPlaceholder;
           var m = Pattern.Match(previousBody);
           return m.Success ? m.Groups[1].Value.Trim() : EmptyPlaceholder;
       }

       public const string EmptyPlaceholder = "<!-- user notes -->";
   }
   ```
   Trim removes leading/trailing whitespace from the preserved block — re-stitching adds blank lines around the section anyway. Regex is `RegexOptions.Compiled` (one match per compose; the cost is negligible).

**6. `ComposingHandler.cs` rewrite (`Features/Processing/Phases/`):**
   The existing handler (from handoff #3) calls `CompositeMarkdownAssembler.Assemble(...)`. Replace with:
   ```csharp
   var attachments = await _db.Attachments
       .Where(a => a.NoteId == job.NoteId)
       .OrderBy(a => a.CreatedAt)
       .ToListAsync(ct);
   var note = await _db.Notes.FirstAsync(n => n.Id == job.NoteId, ct);
   var composed = _composer.Compose(note, attachments, note.BodyOutput);
   note.BodyOutput = $"---\n{composed.Frontmatter}---\n\n{composed.Body}";
   note.RelativePath ??= $"Inbox/{note.Id}.md";
   note.UpdatedAt = _clock.GetCurrentInstant();
   job.LastComposeTemplate = composed.ComposeTemplateVersion;
   // existing phase transition: ingest_jobs.status = 'routing'
   ```
   The `RelativePath` `??=` operator keeps backward compatibility: routing-phase handler (handoff #3 + future #7) can overwrite. **Do not** touch `RelativePath` here unless it's null — preserves the LLM-routing semantics for reprocess.

**7. `Program.cs` registrations:**
   ```csharp
   services.AddSingleton<IKindRenderer, UrlRenderer>();
   services.AddSingleton<IKindRenderer, ImageRenderer>();
   services.AddSingleton<IKindRenderer, AudioRenderer>();
   services.AddSingleton<IKindRenderer, VideoRenderer>();
   services.AddSingleton<IKindRenderer, DocumentRenderer>();
   services.AddSingleton<IKindRenderer, FailedHiddenRenderer>();
   services.AddSingleton<CompositeNoteComposer>();
   ```
   Inject `CompositeNoteComposer` into `ComposingHandler`'s primary constructor.

**8. `IngestJob.cs` change:**
   ```csharp
   [NotMapped]
   public string? LastComposeTemplate { get; set; }
   ```
   The `[NotMapped]` attribute keeps it out of the schema; the materializer reads it in Pass B.

**9. Delete `CompositeMarkdownAssembler.cs`** (`Features/Processing/`). It was the CLOUD-002-vintage assembler; handoff #3's `ComposingHandler` called it; no other call site exists after this ticket.

**10. Tests for Pass A** under `tests/ThanyMarcus.Cloud.Tests/Features/Processing/Composing/`:
   - `Renderers/UrlRendererTests.cs` — title fallback chain (extra.title → extra.canonical_url → attachments.url); 80-char truncation with `…` suffix; empty `extracted_text` emits the no-content comment.
   - `Renderers/ImageRendererTests.cs` — presign success → embed; presign failure → bare reference; `extracted_text` containing the canonical `Description:\n...\n\nText:\n...` reproduces verbatim.
   - `Renderers/AudioRendererTests.cs` — with and without `original_filename`; transcription text verbatim.
   - `Renderers/DocumentRendererTests.cs` — same shape as audio.
   - `Renderers/VideoRendererTests.cs` — parent + 1 audio child + 3 keyframe children; child ordering by `created_at`; failed child renders as marker inside numbered list; no children → just heading + reference.
   - `Renderers/FailedHiddenRendererTests.cs` — failed kind with error message; skipped kind with null text; multi-line error gets newline-stripped; missing error → `"unknown"` reason.
   - `FrontmatterBuilderTests.cs` — golden YAML output for representative composite (3 kinds); empty `attachment_kinds` for no-attachments note; sorted distinct kinds.
   - `UserNotesPreserverTests.cs` — null previous body → empty placeholder; previous body with non-empty user notes → preserved; previous body with no `## System Output` marker (corrupt) → empty placeholder; both markers present but user-notes section is whitespace → empty placeholder.
   - `CompositeNoteComposerTests.cs` — full 4-kind composite renders deterministically; reprocess with preserved user notes round-trips; duplicate-renderer registration throws at construction; unknown attachment kind throws with a clear message.

→ At end of Pass A: a composite-ingest run from `/finalize` produces a Markdown file with YAML frontmatter, `## User Notes` + `## System Output` skeleton, per-kind blocks, and `compose_template: compose-v1` in frontmatter. The handoff-#3 regression anchor still passes (with assertions adjusted to expect the new body shape — see Pass B). LLM phases continue to run with no-op clients; routing returns null project; entities are empty; embedding writes zero vector. **The note lands `ready` with the new body.**

### Pass B — provenance integration + failed-hidden semantics + regression anchor update (~0.5–1d)

**1. `ProvenanceMaterializer.cs` extension** (`Features/Processing/`):
   Add the `compose_template` field to the materialized provenance JSON:
   ```csharp
   public static JsonDocument Build(IngestJob job, Note note, IReadOnlyList<Attachment> atts, ...) =>
       JsonDocument.Parse(JsonSerializer.Serialize(new {
           // ...existing fields from handoff #3...
           compose_template    = job.LastComposeTemplate,   // ADD
           // ...rest unchanged...
       }));
   ```
   On failure terminals (`failed_composition`, etc.) the value is null because compose never completed. On `succeeded`, it's always `compose-v1`. On `dead_lettered` (cancellation), it's whatever value compose set before cancellation; if cancellation hit before compose, null.

**2. Failed-hidden integration in the composer.** The renderer for `FailedHiddenRenderer` is wired in Pass A; this pass adds the dispatch-side check for `(status == Skipped AND extracted_text IS null)`:
   ```csharp
   private static bool ShouldRouteToFailedRenderer(Attachment att) =>
       att.ExtractionStatus == AttachmentExtractionStatus.Failed
       || (att.ExtractionStatus == AttachmentExtractionStatus.Skipped
           && string.IsNullOrWhiteSpace(att.ExtractedText));
   ```
   Co-locate inside `CompositeNoteComposer` as a private static helper. Add a one-line non-obvious-why comment: `// skipped-with-text is a cache hit (renders normally); skipped-without-text is a preflight reject (hidden)`. **This is the rare comment allowed by `feedback_no_code_comments`** — it captures a non-obvious branch that the reader cannot derive from the code itself.

**3. Regression anchor update** (`tests/ThanyMarcus.Cloud.Tests/Features/CompositeIngestSagaEndToEndTests.cs`):
   The handoff-#3 test asserts `body` contains "all four stub-text snippets." Update the assertions to:
   - Frontmatter parses as YAML with the expected key set; `compose_template == "compose-v1"`; `attachment_kinds` is sorted distinct.
   - Body starts with `---\n...---\n\n## User Notes\n\n<!-- user notes -->\n\n## System Output\n\n`.
   - For each of the four stub attachments, the matching renderer's heading appears (`### Image`, `### Source:`, `### Voice memo`, `### Document:`) followed by the canonical stub `extracted_text`.
   - **No** `### Video` block (the test's 4-attachment composite from handoff #3 has no video kind).
   - Provenance read via `?include=provenance` includes `compose_template == "compose-v1"`.
   - Reprocess preserves a manually-injected `## User Notes` block: pre-test, the test mutates `notes.body_output` to `"---\n...---\n\n## User Notes\n\nmy notes here\n\n## System Output\n\nOLD"`, calls `/reprocess`, and asserts that the new body's User Notes section still says `"my notes here"`.

**4. New cross-renderer test** `tests/ThanyMarcus.Cloud.Tests/Features/Processing/Composing/CompositeNoteComposerFailedHiddenTests.cs`:
   A composite with two attachments, one failed and one extracted. Assert:
   - The failed attachment's block in the body is exactly the hidden marker comment.
   - The extracted attachment's block renders normally with its `### <Heading>` and content.
   - `attachment_kinds` in frontmatter includes BOTH kinds (frontmatter doesn't hide failed kinds — it lists what the user uploaded, not what extracted successfully).
   - No human-readable error text leaks into the rendered body (only inside the HTML comment).

**5. Plugin-side parser sketch (no shipped code).** Document in this handoff's §"What handoffs #7+ inherit" the regex shape a future plugin "View processing details" feature should use to find hidden markers:
   ```regex
   <!--\s*thany-marcus:attachment\s+id=(?<id>[a-f0-9-]+)\s+kind=(?<kind>\w+)\s+status=(?<status>\w+)\s+reason=(?<reason>[^>]*?)\s*-->
   ```
   Document only; do not ship.

**6. Documentation update.** Add one paragraph to `docs/architecture.md` (or land an inline pointer to this handoff) describing the compose template version semantics — what `compose-v1` means, what bumping to `compose-v2` implies for existing notes, and the User Notes preservation rule. One paragraph max.

→ At end of Pass B: provenance carries `compose_template`, the regression anchor catches body-shape regressions, failed extractions are invisible in rendered Markdown but addressable by the future "details" feature, and reprocess preserves user edits. Handoff #7 inherits a stable body skeleton it slots LLM-driven wikilinks + entity rendering into.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   ├── 0042-cloud-ingest-pipeline-architecture.md            # (read-only) phase machine, best-effort cascade
│   ├── 0045-composite-note-schema.md                         # (read-only) columns the composer reads
│   └── 0044-cloud-intelligence-layer.md                      # (read-only) VLM output canonical form
├── plans/
│   └── cloud-compose-phase-handoff.md                        # THIS FILE
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                            # CHANGED: + 6 IKindRenderer registrations + CompositeNoteComposer singleton
│   ├── Features/
│   │   └── Processing/
│   │       ├── CompositeMarkdownAssembler.cs                 # DELETED (legacy CLOUD-002 assembler)
│   │       ├── ProvenanceMaterializer.cs                     # CHANGED: + compose_template field in built JSON
│   │       ├── IngestJob.cs                                  # CHANGED: + [NotMapped] LastComposeTemplate
│   │       ├── Phases/
│   │       │   └── ComposingHandler.cs                       # CHANGED: replaces CompositeMarkdownAssembler call with CompositeNoteComposer; sets job.LastComposeTemplate
│   │       └── Composing/
│   │           ├── CompositeNoteComposer.cs                  # NEW: pure dispatch + stitching + ComposeTemplateVersion constant
│   │           ├── ComposedNote.cs                           # NEW: record (Frontmatter, Body, ComposeTemplateVersion)
│   │           ├── FrontmatterBuilder.cs                     # NEW: YamlDotNet-backed serializer + DTO
│   │           ├── UserNotesPreserver.cs                     # NEW: regex extraction of preserved User Notes block
│   │           ├── IRenderContext.cs                         # NEW: ChildrenFor + TryGetPresignedDownloadUrl
│   │           ├── RenderContext.cs                          # NEW: concrete impl wrapping IArtifactStore
│   │           ├── IKindRenderer.cs                          # NEW: interface
│   │           └── Renderers/
│   │               ├── UrlRenderer.cs                        # NEW
│   │               ├── ImageRenderer.cs                      # NEW
│   │               ├── AudioRenderer.cs                      # NEW
│   │               ├── VideoRenderer.cs                      # NEW
│   │               ├── DocumentRenderer.cs                   # NEW
│   │               └── FailedHiddenRenderer.cs               # NEW
│   └── Directory.Packages.props                              # CHANGED (root): + YamlDotNet if not transitively present
└── tests/ThanyMarcus.Cloud.Tests/
    └── Features/
        ├── Processing/Composing/
        │   ├── CompositeNoteComposerTests.cs                 # NEW
        │   ├── CompositeNoteComposerFailedHiddenTests.cs     # NEW
        │   ├── FrontmatterBuilderTests.cs                    # NEW (golden YAML anchors)
        │   ├── UserNotesPreserverTests.cs                    # NEW
        │   └── Renderers/
        │       ├── UrlRendererTests.cs                       # NEW
        │       ├── ImageRendererTests.cs                     # NEW
        │       ├── AudioRendererTests.cs                     # NEW
        │       ├── VideoRendererTests.cs                     # NEW
        │       ├── DocumentRendererTests.cs                  # NEW
        │       └── FailedHiddenRendererTests.cs              # NEW
        └── CompositeIngestSagaEndToEndTests.cs               # CHANGED: assertions updated for new body shape + compose_template provenance + reprocess preserves User Notes
```

## Body shape reference

The composed Markdown for a 4-attachment composite (URL + image + voice + document, no video, no failures):

```markdown
---
id: 9b3c4f...
captured_at: 2026-05-19T10:00:00.000Z
modality: composite
attachment_kinds:
- document
- image
- url
- voice
source: plugin
llm_mode: safe
compose_template: compose-v1
---

## User Notes

<!-- user notes -->

## System Output

### Source: Example article — How frontmatter works

Source: https://example.org/articles/frontmatter

<readable article markdown from SmartReader>

### Image

![](https://spaces.example.com/.../presigned)

Description:
A whiteboard sketch of a state machine with five labeled nodes.

Text:
queued → processing → done

### Voice memo: 2026-05-19-meeting.m4a

[audio: 2026-05-19-meeting.m4a]

<plain transcription text from Parakeet>

### Document: spec.pdf

[document: spec.pdf]

<Docling markdown — headings, paragraphs, tables>
```

For the same composite with a failed image (e.g., Ollama returned junk JSON for the second-attempt retry exhaustion):

```markdown
## System Output

### Source: ...

...

<!-- thany-marcus:attachment id=8a2b... kind=image status=failed reason=ollama_json_parse_after_3_attempts -->

### Voice memo: ...

...
```

For a video with one audio child + three keyframe children (one failed):

```markdown
### Video: lecture.mp4

[video: lecture.mp4]

**Audio:**
<parakeet transcription of the audio track>

**Keyframes:**
1. Description:
   A title slide reading "Introduction to Distributed Saga Patterns."

   Text:
   Introduction to Distributed Saga Patterns
2. <!-- thany-marcus:attachment id=4d... kind=image status=failed reason=preflight_dimensions_out_of_range:50x50 -->
3. Description:
   A diagram of a 3-phase commit protocol.

   Text:
   prepare → vote → commit
```

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors. `dotnet test` green; every test file from handoff #3 continues to pass with assertions updated to match the new body shape.
2. ✅ `CompositeMarkdownAssembler.cs` deleted; no compile-time references remain (`grep -rn "CompositeMarkdownAssembler" src tests` returns nothing).
3. ✅ Six `IKindRenderer` implementations registered; `CompositeNoteComposer` constructed at host startup verifies the dispatch dictionary (no duplicates; `"failed"` key present; each first-class `AttachmentKind` covered).
4. ✅ `CompositeNoteComposer.ComposeTemplateVersion == "compose-v1"`; the constant appears in:
   - YAML frontmatter `compose_template: compose-v1`
   - `notes.provenance.compose_template == "compose-v1"` (read via `/api/sync/pull?include=provenance`)
5. ✅ Frontmatter is valid YAML (round-trippable via a YamlDotNet `Deserializer` on the resulting string); keys are exactly `id, captured_at, modality, attachment_kinds, source, llm_mode, compose_template` in that order.
6. ✅ `## User Notes` block survives `/reprocess`: a note with `## User Notes\n\nmy notes\n\n## System Output\n\n<old>` is reprocessed → new body contains `## User Notes\n\nmy notes\n\n## System Output\n\n<new>`.
7. ✅ Failed extractions render as the canonical hidden marker (`<!-- thany-marcus:attachment id=<guid> kind=<kind> status=failed reason=<msg> -->`), one line, no surrounding visible text. Pre-flight skipped extractions (null `extracted_text`) render the same shape with `status=skipped`.
8. ✅ Cache-hit attachments (`extraction_status='skipped'` with non-null `extracted_text`) render normally — indistinguishable from `extracted` in the body.
9. ✅ Image attachments mint a presigned download URL with 7-day TTL; on Spaces failure, fall back to `[image: <storage-key>]` reference; no exception bubbles out of the composer.
10. ✅ Video children are grouped under their parent's `### Video` block; ordering: audio child above keyframes (regardless of `created_at`), keyframes by `created_at ASC`. No top-level rendering of children.
11. ✅ Empty composite (zero top-level attachments) renders without throwing; System Output section contains a single `<!-- no attachments -->` marker.
12. ✅ Routing, extracting_entities, embedding phases continue to run with `NoOpLlmClient` + `StubEmbeddingClient` from handoff #3; no behavior change. The note still lands `ready`; `notes.project_id` is null; no `mentions` rows; `notes.embedding` is the zero vector.
13. ✅ The handoff-#3 regression anchor (`CompositeIngestSagaEndToEndTests.cs`) is green with updated assertions:
    - SSE event sequence unchanged.
    - Body contains the new structured shape (frontmatter + skeleton + per-kind blocks).
    - Provenance carries `compose_template`.
    - Reprocess cache-hits on all attachments AND preserves User Notes.
14. ✅ Manual smoke against a freshly-provisioned cloud (with handoffs #1, #2, #3 applied; #4 + #5 optional — works with stubs alone):
    ```bash
    # Ingest a composite (existing handoff-#3 flow)
    curl -sS -X POST .../api/ingest/init -H "Authorization: Bearer $TOKEN" -d '{...}'
    # uploads + finalize ...
    # Wait for note.status=ready (SSE or poll /api/sync/pull)
    curl -sS ".../api/sync/pull?since=0&include=provenance" -H "Authorization: Bearer $TOKEN" \
      | jq '.items[0].body' -r | head -50
    # → frontmatter visible at the top with compose_template: compose-v1
    # → ## User Notes section
    # → ## System Output section with per-kind ### headings
    ```
15. ✅ No `// TODO` markers in shipped code. Allowed `// FORK:` markers: at `FrontmatterBuilder` (forks to handoff #7 for `entities:` + `tags:` fields), at the `ImageRenderer` presign-TTL constant (forks to a configurable TTL if eval shows 7 days is wrong), at `RoutingHandler` no-op behavior (forks to handoff #7).
16. ✅ `compose-v1` as a string literal appears in **exactly one place** (`CompositeNoteComposer.ComposeTemplateVersion`). All other references go through the constant. `grep -rn '"compose-v1"' src` returns one hit.

## Out of scope (named explicitly)

1. ❌ **Real LLM client wiring.** `NoOpLlmClient` continues to drive `routing` + `extracting_entities`. The composer doesn't see LLM output. Handoff #7.
2. ❌ **Wikilink anchor insertion.** Per cloud-pivot plan §17, LLM emits wikilink-anchor positions inside `extracted_text` snippets for the composer to splice as `[[entity-name]]`. Until LLM lands, the composer copies extractor text verbatim with no wikilinks. Handoff #7 adds a post-render wikilink-splice pass that operates AFTER per-kind rendering (so the wikilink positions are LLM-determined relative to final text). The compose-v1 → compose-v2 bump is the natural carrier.
3. ❌ **Project routing.** `notes.relative_path` stays at `Inbox/{noteId}.md` for every note. Real routing reads `notes.project_id` and rewrites the path; that's handoff #7.
4. ❌ **Entity mentions / hub trigger.** No `mentions` rows are inserted. `notes.entities` frontmatter field is not added until LLM lands.
5. ❌ **Tags.** No automatic tag extraction. Manual `tags:` in frontmatter is a user concern (not edited by the cloud); the composer doesn't write a `tags:` line.
6. ❌ **`compose_template` value validation.** No enum, no DB check constraint, no migration. It's a free-form string in YAML + JSON. Bumping requires changing the source constant and recompiling; old notes are recognizable by their `compose-v1` value.
7. ❌ **Plugin "View processing details" UI.** This handoff defines the hidden-marker shape and the regex to find them. The plugin's UI feature is a separate ticket (post-MVP).
8. ❌ **Real video splitter.** `StubVideoSplitterClient` returns empty children list; `VideoRenderer` handles this (heading + reference, no audio/keyframe subsections). Real ffmpeg splitter is handoff #5 (already shipped per the handoff-numbering reconciliation); if running against pre-#5 code, video composites render as a single header + bare reference.
9. ❌ **EXIF / pHash / dimensions in rendered body.** `attachments.extra` holds these but the renderer doesn't surface them — they're audit/dedup data, not user-facing content. A future "image metadata" plugin view can read them via `/api/sync/pull?include=provenance`.
10. ❌ **Body output diff / patch semantics.** Reprocess always rewrites `body_output` end-to-end (with User Notes preserved). No "minimal diff" mode. Plugin sees a full file replacement and applies it.
11. ❌ **Frontmatter key reordering / merging on reprocess.** Reprocess regenerates frontmatter from scratch from `notes` columns; any user edits to frontmatter are lost. **Document this clearly in the plugin-setup docs once they exist** — for now, the user shouldn't be hand-editing frontmatter of cloud-managed notes.
12. ❌ **Image presigned-URL refresh.** When the 7-day TTL elapses, the user re-fetches via `/api/sync/pull` which re-runs compose. The composer always mints a fresh URL. Stale URLs in the user's local vault are the user's vault-sync problem (Obsidian Sync / git / etc. doesn't re-pull on its own). This is acceptable for MVP; a "background refresh" job is future work.
13. ❌ **Markdown linting.** The composer doesn't enforce trailing newlines, heading spacing, or any style rule beyond what's specified above. If a future code review wants stricter Markdown, add a single `MarkdownLinter.Polish(string body)` pass at the composer's exit; that ticket is not this one.
14. ❌ **OTel spans inside renderers.** The composer is fast enough (single-digit ms total) that per-renderer spans add noise without insight. The phase-level span on `composing` (from handoff #3) is sufficient.

## Risks and gotchas

- **`extracted_text` containing `## System Output` literally.** A user uploads a PDF that itself contains the text `## System Output` (e.g., a software-architecture doc). Docling extracts it; the composer splices it into the body; on reprocess, `UserNotesPreserver`'s regex matches the WRONG `## System Output` (the one inside the extracted PDF text) and the User Notes section gets corrupted. **Mitigation:** the renderer's `\\##` escape (§17 above) handles `## System Output` at the start of a line specifically — escape it to `\## System Output` before splicing. The User Notes preserver regex remains anchored to unescaped `## System Output` and won't match. Add a renderer-level test asserting this behavior.
- **YamlDotNet ordering inconsistency.** YamlDotNet emits properties in declaration order by default — but if anyone re-orders `FrontmatterDto`'s fields, the YAML output changes silently and downstream tools that parse positionally break. **Mitigation:** the golden-string anchor test (`FrontmatterBuilderTests`) catches reordering immediately.
- **`Distinct().OrderBy()` on `attachment_kinds` for very large attachment counts.** Trivially fast at thesis scale (<100 attachments per note). Not a real risk; named here only to defuse a future "shouldn't this be a HashSet?" comment.
- **Presigned URL TTL of 7 days vs vault-sync replication latency.** If the user's vault-sync mechanism (e.g., git via cron) only runs daily, a freshly-composed note's image URL has 6 effective days before expiry on Device B. Comfortable margin. **Less comfortable:** if the user's vault sync is weekly (Syncthing on rare connections), 7 days is tight. Document the assumption in `docs/plugin-setup.md` when that doc lands. **Acceptable** for MVP because the plugin can re-fetch on demand via `/api/sync/pull?since=...`.
- **`NotMapped` `LastComposeTemplate` lost on orchestrator restart mid-compose.** If the orchestrator crashes between `ComposingHandler` setting `LastComposeTemplate` and the materializer reading it (at terminal time), the materializer reads null. The note still has `compose-v1` in its frontmatter (already written to `body_output`), so the provenance `compose_template == null` is a forensic-only loss; readers can fall back to parsing the body's YAML. **Acceptable.** Document as a known forensic-loss edge case; not a correctness issue.
- **`Note` query order — read note BEFORE attachments?** EF Core 10 with the cloud's connection pool serializes both queries fine; ordering doesn't matter for the composer's correctness. But the existing CLOUD-002 / handoff #3 pattern is "read attachments first, then note." Match that pattern to avoid a needless review comment.
- **The deleted `CompositeMarkdownAssembler.cs` is referenced by handoff-#3 documentation.** Update handoff-#3's `ComposingHandler` description to point at `CompositeNoteComposer` after this ticket lands — a one-line edit. Easy to forget under warnings-as-errors (the build is clean either way; only the prose drifts). **Not a blocker** but a follow-up; track in the PR description.
- **Failed-hidden marker as plugin-parsed contract.** The regex shape in §5 of Pass B is the parser contract. Changing the marker shape later (e.g., adding a `phase=` field) breaks parsers built against compose-v1. Treat the marker as part of the compose-v1 contract; bumps go with `compose-v2`. Document in the handoff that the marker shape is locked.
- **Empty `extracted_text` after a successful extraction.** Per §10 above, the composer falls back to a no-content comment. If this happens in production with any frequency, it indicates an extractor bug (a sidecar returning empty success). The renderer logs at info level on this branch so it's visible in dashboards but doesn't fail. **Acceptable.**
- **Renderer registration ordering.** `IEnumerable<IKindRenderer>` order is registration order. The composer doesn't care (it dispatches by `Kind` key), but a test that snapshots the dispatch dictionary by iterating it must sort first. Add a one-line sort in the dictionary-build path to make the iteration order deterministic regardless of DI registration shuffling.
- **YAML `>` and `|` block scalars.** `YamlDotNet` may emit multi-line strings as block scalars (`>` or `|`) which look odd in frontmatter but parse correctly. The default emitter prefers double-quoted single-line for short strings; long strings get block-scalar treatment. **Risk:** a captured URL or note title that's >80 chars gets wrapped as `>` block scalar, which surprises a reader of the raw file. **Mitigation:** the `SerializerBuilder` chain includes `.WithDefaultScalarStyle(ScalarStyle.Plain)` — verify this option exists in the YamlDotNet version pulled in (it does, since 11.x). Plain scalars handle the captured-at timestamp and UUIDs cleanly.
- **Reprocess race with concurrent user edits.** The plugin pushes user edits via `/api/sync/push` (not yet implemented — that's a later ticket); until then, reprocess only round-trips edits already saved in `notes.body_output`. If the user is mid-edit on Device A and Device B triggers reprocess, the edit is lost. This is a vault-sync concern, not a compose concern. Document but don't gate.
- **EF change tracking + `[NotMapped]` `LastComposeTemplate`.** Verify EF doesn't try to track or update it. A simple test: set the property, call `db.SaveChangesAsync()`, observe no SQL UPDATE column for `last_compose_template`. `[NotMapped]` should handle this but verify in `IngestJobConfigurationTests` (or write a one-off test in `CompositeNoteComposerTests` if no such test class exists yet).
- **Renderer thread-safety.** Singletons + stateless → thread-safe by construction. The shared `IArtifactStore` is already thread-safe per CLOUD-002. The regex in `UserNotesPreserver` is `RegexOptions.Compiled` and immutable — thread-safe. **No concurrency risk.**

## Open contract decisions (carry forward)

1. **`compose-v2` content roadmap.** Handoff #7's LLM phases will want `entities:` and `tags:` frontmatter fields, plus wikilink splicing in the body. The bump to `compose-v2` is locked to coincide with LLM landing; until then, the field set in §6 above is stable. **Open:** whether the bump also rewrites existing compose-v1 notes opportunistically (on next `/sync/pull`) or only on user-triggered reprocess. Defer to the handoff-#7 ticket.
2. **Image presigned-URL TTL.** 7 days is a guess. If eval shows users routinely opening notes >7 days after capture without reprocess, bump to 30 days or move to a settings-driven value. **Not blocking.**
3. **Failed-hidden surfacing in the plugin UI.** The regex contract is defined here; the actual "click to view processing details" UI is a plugin ticket. Until then, failed extractions are silently hidden, which is the right MVP default but means a user with persistent extraction failures has no signal in Obsidian. The provenance summary's `extraction_summary.failed > 0` counter is the only current signal; surfacing it in the status bar is a future plugin ticket.
4. **`source:` frontmatter field value space.** Currently always `plugin`. When the portal grows an admin-side "manual upload" surface (post-MVP), the value space extends to `portal`. Lock the field's existence now; defer the value space to that ticket.
5. **`attachment_kinds:` for video children.** Currently `attachment_kinds` is top-level-only (children are not listed). **Open:** whether to include `image, voice` in `attachment_kinds` for a video composite (because the children effectively contribute image + audio content). **Decision:** no — `attachment_kinds` is "what the user uploaded," not "what's in the body." A video composite shows `attachment_kinds: [video]` and the keyframes/audio are an implementation detail of how video extracts. If the plugin wants to know "does this note have an image?" it can scan the body for `### Image`. Document this in the renderer with one comment if it surprises a reviewer.

## What handoffs #7+ inherit

After this ticket lands:
- **Handoff #7** (`CLOUD-LLM-INTELLIGENCE` — routing + entity extraction) plugs into the saga at the `routing` and `extracting_entities` phase handlers from handoff #3. Those handlers' contracts don't change. The new LLM client emits structured output (`{project, mentions[], wikilink_anchors[]}`); routing sets `notes.project_id` + `notes.relative_path`; extracting_entities INSERTs `mentions` rows + maybe triggers hub regen. **Compose is re-run via `/reprocess`** if the user wants wikilinks back-applied to existing notes — this means handoff #7 should ALSO bump compose to `compose-v2` (with wikilink splicing inside `extracted_text` blobs) so the new LLM output produces user-visible Markdown. **Two coupled changes:** the LLM client AND a `compose-v2` renderer pass. Plan them as one ticket OR split with `compose-v2` first (renders LLM output as no-op if LLM returns null, ready for the LLM swap).
- **Plugin sync push** (separate ticket, post-#7) wires `/api/sync/push` for user edits to User Notes sections. The composer's preservation logic accepts whatever is currently in `notes.body_output` between markers — the push endpoint just updates that column, and the next compose run picks it up. **No composer change needed for sync push.**
- **Plugin "View processing details" UI** (post-MVP plugin ticket) consumes the hidden-marker regex from §5 of Pass B above. Composer-side contract is locked at compose-v1.
- **Cross-image pHash dedup** (future ticket) reads `attachments.extra.phash` but doesn't touch the composer. The renderer ignores `phash` today and will continue to ignore it.

The cumulative arc is:

```
CLOUD-SCHEMA-V2 (handoff #1)
  ─► CLOUD-SIDECARS (handoff #2)
  ─► CLOUD-INGEST-SAGA-FOUNDATION (handoff #3)
  ─► CLOUD-PROCESSORS-LIGHT (handoff #4 — UrlFetcher + VLM real)
  ─► CLOUD-PROCESSORS-HEAVY (handoff #5 — Docling + Parakeet + VideoSplitter real)
  ─► CLOUD-COMPOSE-PHASE (THIS TICKET — compose-v1 templates + frontmatter + body_output)
  ─► CLOUD-LLM-INTELLIGENCE (handoff #7 — routing + entity extraction + compose-v2 wikilink splice)
  ─► [M5 demo: composite capture → real extraction → composed Markdown → LLM-enriched + emergent graph]
  ─► CLOUD-EMBEDDING + CLOUD-ENTITY-DEDUP + CLOUD-HUB-REGEN
  ─► [M6 demo: end-to-end thesis-defensible CPU-only pipeline with knowledge graph]
```

This ticket is the **last piece before LLM lands**. After it, the composed body is the stable carrier into which LLM intelligence is added in handoff #7 (without renderer rewrites, without saga changes, without schema migrations).

## Why a separate handoff (and not folded into handoff #7)

Compose is mechanically distinct from LLM enrichment. Folding them would (a) blur the rendering contract (per-kind shape) with the intelligence contract (project routing, wikilink positions, entity mentions); (b) make the compose-v1 → compose-v2 boundary fuzzy (does compose-v1 exist without LLM, or only with LLM?); (c) leave the regression anchor unable to assert "compose works against stubs" without also waiting on the LLM ticket; (d) couple the failure modes of two independent systems (a YAML serialization bug + a model-prompt regression in the same PR). Shipping compose first means the body skeleton is exercised end-to-end against handoff-#3 stubs, the User Notes preservation rule is locked, and handoff #7 is "wire the LLM and bump to compose-v2" rather than "design the body shape AND the LLM contract simultaneously." This ticket's ~1.5d pays off the body design once.

## Cross-references

- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — the contract; this ticket touches §3 `composing` phase and §10c provenance.
- **`docs/decisions/0044-cloud-intelligence-layer.md`** — VLM canonical output form (image attachment input contract).
- **`docs/decisions/0045-composite-note-schema.md`** — columns this ticket reads/writes (only `notes.body_output` is written).
- **`plans/cloud-ingest-saga-foundation-handoff.md`** — the framework this ticket modifies. **Only `ComposingHandler.cs` + `ProvenanceMaterializer.cs` change.**
- **`plans/cloud-processors-light-handoff.md`** — locks the image + URL canonical input shape the composer consumes.
- **`plans/cloud-processors-heavy-handoff.md`** — locks the video parent + child relationship the composer renders.
- **Cloud-pivot plan §15** — `## User Notes` / `## System Output` convention.
- **Cloud-pivot plan §16** — vault structure (`Inbox/{noteId}.md` default path).
- **Cloud-pivot plan §17** — entity hubs and wikilink anchors (deferred to handoff #7).
- **Memory `composite_ingest_decision.md`** — one composite draft → one processed note.
- **Future ADRs** (not in this ticket's scope):
  - **ADR-0050: Composite-note template versioning (`compose-v1` and the bump cadence).**
  - **ADR-0051: Failed-extraction hidden-marker contract.**

## Definition of done

```
$ dotnet build
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName~Composing"
Passed!  - Failed: 0, Passed: N, Skipped: 0

$ dotnet test tests/ThanyMarcus.Cloud.Tests --filter "FullyQualifiedName~CompositeIngestSagaEndToEndTests"
Passed!  - Failed: 0, Passed: 1, Skipped: 0   # the regression anchor with updated assertions

$ grep -rn "CompositeMarkdownAssembler" src tests
(no results)                                  # legacy assembler fully removed

$ grep -rn '"compose-v1"' src
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/CompositeNoteComposer.cs:    public const string ComposeTemplateVersion = "compose-v1";
                                              # one and only one source of truth

$ grep -rn "IKindRenderer" src/ThanyMarcus.Cloud.Api
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/IKindRenderer.cs:...
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/Renderers/UrlRenderer.cs:...
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/Renderers/ImageRenderer.cs:...
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/Renderers/AudioRenderer.cs:...
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/Renderers/VideoRenderer.cs:...
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/Renderers/DocumentRenderer.cs:...
src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/Renderers/FailedHiddenRenderer.cs:...
                                              # interface + 6 implementations
```

A fresh agent picking up handoff #7 (`CLOUD-LLM-INTELLIGENCE`) from this state knows:
- The body shape is locked under `compose-v1`. They bump to `compose-v2` when adding LLM-driven wikilinks.
- The composer is pure C# — they extend it (e.g., add a `WikilinkSplicer` post-pass) without rewriting saga or DB code.
- `RoutingHandler` and `ExtractingEntitiesHandler` from handoff #3 are the seams for LLM output; the composer reads `notes.project_id` (set by routing) only to influence frontmatter — wait, no, the current `compose-v1` doesn't read `project_id`. Handoff #7's `compose-v2` adds that.
- The failed-hidden marker shape is a plugin-parser contract; don't change it without bumping the template version.
- The User Notes preservation rule is a hard invariant; LLM-driven recompose must not destroy user edits.
