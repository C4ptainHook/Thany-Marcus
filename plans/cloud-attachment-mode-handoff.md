# CLOUD-ATTACHMENT-MODE — Extract / Reference / Metadata classification

**Goal:** let users attach files to a note that they explicitly *do not* want processed by the extraction pipeline — a song that evokes a memory, a video they want as a citation, a bookmark where only the title+description matter. Today the system tries to process every attachment through a kind-specific sidecar and stores the result; songs come back as garbage transcripts, videos route to Docling and fail. This ticket adds an `AttachmentMode` axis (`extract` | `reference` | `metadata`) with smart per-kind defaults and an override toggle in the plugin composer. Estimated **1.5 person-days** with AI-agent assistance.

## Why this exists

Two real failure modes today, both observed in the demo vault:

1. **Video attachments fail silently.** Client-side classifier in `DraftManager.ts:69-73` only checks `image/*` and `audio/*` MIME prefixes; everything else falls through to `kind: "file"`. A dropped `.mp4` therefore routes to Docling, which doesn't handle video containers, marks `ExtractionStatus.Failed`, and the attachment lands on the synthesized note as `<input failed kind="file"/>`. The file is uploaded (counts against the 20 GB cap), stored on Spaces, and adds zero value to the note.
2. **Songs get transcribed as noise.** A music mp3 routes to Parakeet (Voice). Parakeet produces partial/garbage lyrics for instrumentals or noisy mixes. That output then enters `SynthesisPromptBuilder` as a "Voice transcript:" block, polluting the prompt with content the user never wanted in the note body in the first place.

The underlying conceptual error is that today every attachment is treated as *extraction input* (content that should influence the synthesized body). But there's a second legitimate use case: attachments-as-*citations* — the user wants the artifact attached to the note (for playback, click-through, reference) without it shaping what the LLM writes. That distinction needs to be expressible.

Adjacent prior art: Notion's "embed vs link"; Obsidian's `![[file]]` (embed) vs `[[file]]` (link); Tana's `attachment` vs `source` blocks. All three carry a similar two-axis model. Thany-Marcus today only has the embed/extract axis.

## Scope

**In scope:**
- New `AttachmentMode` string-constant class on the entity, three values: `extract`, `reference`, `metadata`. Default `extract` so existing rows and the existing wire format keep working.
- DB migration adding `mode` column with default `'extract'`.
- Ingest contract: optional `mode` field per attachment in the composite ingest payload; 422 if `mode=metadata` paired with a non-URL kind.
- Sidecar routing: `ExtractingAttachmentsHandler.ResolveSidecar()` returns `null` for `reference`; routes `metadata + url` to a new `UrlMetadataWorker`; everything else unchanged.
- New terminal status `AttachmentExtractionStatus.Referenced` (not reusing `Skipped`, which today means "we tried and bailed").
- New `UrlMetadataWorker` sidecar: lightweight `<title>` + `og:*` + canonical-link fetch; YouTube branch using oEmbed (`youtube.com/oembed?url=...`).
- `SynthesisPromptBuilder` filter: omit `reference` attachments entirely; emit `metadata` attachments as a compact `<reference title="..." description="..." url="..."/>` block instead of an `<input>` block.
- `SourcesRenderer` extension: render all attachments regardless of mode, with mode-aware formatting (reference → filename + play link; metadata → og card with title/description/thumbnail).
- Plugin: `defaultModeFor(kind, mime)` helper in `DraftManager.ts`, applied at attachment-create time. Per-attachment chip toggle in the composer UI (cycles `extract → reference` for non-URL kinds; `extract → metadata → reference` for URLs).
- Client-side music-vs-voice disambiguation: ID3v2 tag sniffer (~30 LOC, no dependency — header is well-defined). Voice attachments default to `extract` UNLESS ID3 artist/title tags are present, in which case default `reference`.
- Video MIME detection in the client: any `video/*` MIME → `kind: "file"`, `mode: "reference"` by default.
- Tests across the matrix: routing per (mode, kind), prompt-builder filter, URL metadata worker, plugin defaults, end-to-end composite saga with mixed-mode attachments.

**Out of scope:**
- Hooking up `VideoSplitterWorker` for actual video frame/transcript extraction. The worker class exists but its routing was never wired; wiring it is a separate ticket (`CLOUD-VIDEO-EXTRACTION`) with non-trivial cost/latency implications. This ticket only unblocks video-as-reference, which is the common case.
- LLM-based intent inference ("the user wrote 'transcript' in the body, so extract"). Too clever, too surprising, not worth the trust cost — see "Risks" below.
- Per-attachment user captions for reference items. Captions are a follow-up (`CLOUD-ATTACHMENT-CAPTION`) that adds UI surface but doesn't depend on this ticket.
- Re-classification of historical attachments. Existing rows stay `mode = extract`; nothing migrates.
- A separate "Add bookmark" button distinct from "Add attachment". One add gesture, mode chosen by the default-by-kind rule, user one-clicks to override.

## Default-by-kind rules

The decision table the client classifier and the server validator both honor:

| Kind  | MIME signal                         | Default `mode` |
|-------|-------------------------------------|----------------|
| image | `image/*`                           | `extract`      |
| voice | `audio/*`, no ID3 tags              | `extract`      |
| voice | `audio/*`, ID3 artist/title present | `reference`    |
| file  | `video/*` (any subtype)             | `reference`    |
| file  | application/* and everything else   | `extract`      |
| url   | (no MIME)                           | `extract`      |

Rationale recap: voice memos (the primary `voice` use case) keep transcription by default; songs with ID3 metadata are almost certainly music and downgrade automatically; videos default to reference because extraction is unwired today; URLs default to full-content fetch because the user explicitly went out of their way to attach a link.

The toggle exists for the ambiguous middle — raw mp3 of a song without ID3, a lecture recording the user wants kept as a reference rather than transcribed, an article URL the user wants as a bookmark card rather than ingested wholesale.

## State machine semantics

`Referenced` is a new terminal value for `AttachmentExtractionStatus`. Once an attachment lands there, the extracting phase advances normally; no retry, no fallback. The status differs from `Skipped` (which means "we attempted routing but no sidecar matched") and from `Failed` (which means "we tried and got an error") — `Referenced` means "user opted out, nothing to do".

For `metadata` mode, the existing `Extracted` / `ExtractedMinimal` / `Failed` states still apply — the URL metadata worker can succeed (Extracted), succeed partially (ExtractedMinimal, e.g. only title found), or fail (network error, 404).

## Concrete files

### Backend

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/Attachment.cs`

Add the mode constants and extend the entity:

```csharp
public sealed class Attachment : IHasUpdatedAt
{
    // ...existing fields...
    public string Mode { get; set; } = AttachmentMode.Extract;
}

public static class AttachmentMode
{
    public const string Extract = "extract";
    public const string Reference = "reference";
    public const string Metadata = "metadata";

    public static bool IsValid(string mode) =>
        mode is Extract or Reference or Metadata;

    public static bool IsValidFor(string mode, string kind) => mode switch
    {
        Extract => true,
        Reference => true,
        Metadata => kind == AttachmentKind.Url,
        _ => false,
    };
}

public static class AttachmentExtractionStatus
{
    // ...existing constants...
    public const string Referenced = "referenced";
}
```

#### Postgres migration `0NNN_attachments_mode_column.sql`

```sql
ALTER TABLE attachments
    ADD COLUMN mode TEXT NOT NULL DEFAULT 'extract';

ALTER TABLE attachments
    ADD CONSTRAINT chk_attachments_mode
    CHECK (mode IN ('extract', 'reference', 'metadata'));

ALTER TABLE attachments
    ADD CONSTRAINT chk_attachments_metadata_only_url
    CHECK (mode <> 'metadata' OR kind = 'url');
```

Generated via `dotnet ef migrations add AddAttachmentMode` (no `--no-build`, per the known hazard).

#### `src/ThanyMarcus.Cloud.Api/Features/Ingest/IngestEndpoints.cs:16-103`

Extend the per-attachment validator: accept optional `mode` field, default `extract`, reject with 422 when `AttachmentMode.IsValidFor(mode, kind)` is false. Persist `mode` onto the new `Attachment` row.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Phases/ExtractingAttachmentsHandler.cs:263`

Replace the `ResolveSidecar(att)` switch with a tuple over `(Mode, Kind)`:

```csharp
private IAttachmentSidecar? ResolveSidecar(Attachment att) =>
    (att.Mode, att.Kind) switch
    {
        (AttachmentMode.Reference, _) => null,
        (AttachmentMode.Metadata, AttachmentKind.Url) => _urlMetadata,
        (AttachmentMode.Extract, AttachmentKind.Image) => _vlm,
        (AttachmentMode.Extract, AttachmentKind.Voice) => _parakeet,
        (AttachmentMode.Extract, AttachmentKind.File) => _docling,
        (AttachmentMode.Extract, AttachmentKind.Url) => _urlFetcher,
        _ => null,
    };
```

When the result is null AND `att.Mode == Reference`, set `att.ExtractionStatus = Referenced` and continue (do NOT mark `Skipped` — semantics differ). When null AND mode is anything else, retain today's `Skipped` semantics.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Specialists/UrlMetadataWorker.cs` — NEW

Sibling of `UrlFetcherWorker` (same constructor shape, implements `IAttachmentSidecar`). Logic:

1. If URL host matches `youtube.com` / `youtu.be` / `m.youtube.com`: call `https://www.youtube.com/oembed?url=<url>&format=json`, parse `title`, `author_name`, `thumbnail_url`. Serialize as JSON into `ExtractedText`.
2. Otherwise: GET the URL with a 5s timeout, parse HTML for (in priority order) `<meta property="og:title">` → `<title>`; `<meta property="og:description">` → `<meta name="description">`; `<meta property="og:image">`; `<link rel="canonical">`. Use AngleSharp (add to central package management if absent).
3. If neither title nor description found: `ExtractionStatus.ExtractedMinimal` with whatever was found (or `Failed` if nothing).

`ExtractedText` schema for metadata mode is a JSON object, not raw text:

```json
{
  "title": "...",
  "description": "...",
  "imageUrl": "...",
  "canonicalUrl": "...",
  "author": "...",
  "siteName": "..."
}
```

`SynthesisPromptBuilder` and `SourcesRenderer` both need to deserialize this — extract a small `AttachmentMetadata` record into a shared spot in `Features/Processing/`.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SynthesisPromptBuilder.cs:32-54`

Branch by mode when iterating attachments:

- `mode == Reference` → skip entirely. Do not emit anything to the prompt.
- `mode == Metadata` → emit `<reference id="att-N" title="..." description="..." url="..."/>` (self-closing, compact, ~50 tokens) instead of an `<input>` block.
- `mode == Extract` → today's behavior unchanged.

#### `src/ThanyMarcus.Cloud.Api/Features/Processing/Synthesis/SourcesRenderer.cs`

Iterate all attachments regardless of `ExtractionStatus`. Format by mode:

- `Reference` → `- 🎵 song.mp3` (or 📹 / 📄 by mime-prefix) with a relative link to the attachment storage path.
- `Metadata` → og card: title (linked to URL), one-line description, optional thumbnail.
- `Extract` → today's behavior (linked filename, no extra metadata).

### Plugin

#### `plugin/thany-marcus/src/draft/DraftManager.ts:5-14`

Extend `DraftAttachment`:

```ts
export interface DraftAttachment {
  clientAttachmentId: string;
  kind: "image" | "voice" | "url" | "file";
  mode: "extract" | "reference" | "metadata";
  // ...rest unchanged...
}
```

#### `plugin/thany-marcus/src/draft/DraftManager.ts:61-85`

Extend the rescan loop to detect video MIME and call `defaultModeFor`:

```ts
const mimeType = guessMimeType(f.extension);
const kind = classifyKind(mimeType);
const mode = await defaultModeFor(kind, mimeType, bytes);
```

`classifyKind` matches today's logic but folds `video/*` into `file`. `defaultModeFor` implements the decision table above; for voice it calls a tiny `hasId3Tags(bytes)` sniffer.

#### `plugin/thany-marcus/src/draft/Id3Sniffer.ts` — NEW

~30 LOC. Read first 10 bytes; if they start with `49 44 33` (`"ID3"`) treat as ID3v2 and check the tag size header for non-zero artist/title frames. If the file ends with `54 41 47` (`"TAG"`) at offset `length - 128`, that's ID3v1 — check the artist/title fields are non-empty (not all-null). Return true if either tag carries populated artist or title. No external dependency.

#### `plugin/thany-marcus/src/draft/Submitter.ts`

Include `mode` in the per-attachment payload sent to `/api/ingest`. Backward-compatible (server defaults to `extract` if absent), but new client always sends.

#### `plugin/thany-marcus/src/composer/AttachmentChip.ts` (or wherever chips are rendered today)

Add a small icon button per chip. Single click cycles:

- Non-URL kinds: `extract ⇄ reference`
- URL kind: `extract → metadata → reference → extract`

Icon set:
- `extract` → ⚙️ (or a circled-check)
- `reference` → 🔗
- `metadata` → 📇

Tooltip on hover explains the mode. Toggle updates the draft attachment in place; submitter picks up the new value at send time.

### Shared / wire types

#### `src/ThanyMarcus.Shared/Ingest/IngestDraftDtos.cs` (or wherever the composite-ingest DTO lives)

Add `Mode` field to the attachment DTO (optional, default `"extract"`). Hand-write the matching TypeScript type in the plugin's `api.ts` (per the no-generated-types policy in `portal_tooling`).

## Tests

Backend:
- `AttachmentModeTests.IsValidFor_metadata_only_with_url` — true for url, false for image/voice/file.
- `IngestEndpointTests.Metadata_mode_with_non_url_kind_returns_422`.
- `IngestEndpointTests.Missing_mode_defaults_to_extract`.
- `ExtractingAttachmentsHandlerTests.Reference_mode_skips_sidecar_and_marks_referenced` — no sidecar invocation, status = `Referenced`, saga advances.
- `ExtractingAttachmentsHandlerTests.Metadata_mode_url_routes_to_url_metadata_worker`.
- `ExtractingAttachmentsHandlerTests.Extract_mode_preserves_today_routing` — image→VLM, voice→Parakeet, file→Docling, url→UrlFetcher.
- `UrlMetadataWorkerTests.Youtube_url_uses_oembed_endpoint` — mocked `IHttpClientFactory`, asserts oembed endpoint hit.
- `UrlMetadataWorkerTests.Generic_url_parses_og_tags`.
- `UrlMetadataWorkerTests.Url_without_metadata_marks_extracted_minimal`.
- `UrlMetadataWorkerTests.Network_failure_marks_failed`.
- `SynthesisPromptBuilderTests.Reference_attachments_absent_from_prompt`.
- `SynthesisPromptBuilderTests.Metadata_attachments_emit_reference_block_not_input_block`.
- `SourcesRendererTests.All_modes_appear_in_sources_section_with_mode_specific_formatting`.
- `CompositeIngestSagaEndToEndTests.Mixed_mode_draft_processes_extract_skips_reference_fetches_metadata` — saga test with one extract pdf + one reference mp3 + one metadata YouTube URL, asserts final note body integrates the PDF only, song appears in Sources unprocessed, YouTube link appears in Sources with og card.

Plugin:
- `DraftManagerTests.Video_mime_classifies_as_file_kind_with_reference_mode`.
- `DraftManagerTests.Audio_with_id3_tags_defaults_to_reference_mode`.
- `DraftManagerTests.Audio_without_id3_tags_defaults_to_extract_mode`.
- `DraftManagerTests.Image_always_defaults_to_extract_mode`.
- `Id3SnifferTests.Detects_id3v2_with_populated_artist` / `Detects_id3v1_trailer` / `Returns_false_on_empty_tags`.
- `AttachmentChipTests.Cycle_button_advances_mode_correctly_per_kind`.

## Risks and edge cases

- **ID3 sniffing on the client touches Uint8Array.** The rescan loop already reads bytes for SHA-256 (`DraftManager.ts:66`); reuse those bytes — don't re-read the file. Otherwise a 4 GB song file gets buffered twice.
- **oEmbed depends on YouTube being available.** If the oEmbed endpoint times out, fall through to HTML scraping of the canonical YouTube URL. Worker should not hard-fail on oembed alone.
- **AngleSharp dependency size.** Adding the package adds ~1.5 MB to the cloud-api container. Acceptable; the alternative (regex over HTML) is fragile and well-known to fail on malformed pages.
- **Reference attachments still cost storage.** A user attaching a 1 GB video as reference still uploads 1 GB to Spaces. Worth thinking about a follow-up `CLOUD-EXTERNAL-ATTACHMENT` to support reference-by-URL-only for very large media, but out of scope here.
- **Inference vs explicit user choice.** Default-by-kind + ID3 sniffing is *inference*. Users will be wrong-defaulted sometimes (a song without ID3 → extract → garbage transcript; a podcast with `og:author` tags interpreted as music → reference). The one-click chip toggle is the safety valve. Resist the temptation to add more inference layers (body-text intent, ML audio classifiers); each new signal that *silently* changes the default erodes user trust in the system. The explicit toggle is the contract.
- **Backward compat.** Existing drafts in the demo vault have no `mode` field in their submitter payloads. Server defaults to `extract`. Saga retries on existing rows still work because the `Mode` column has a DB-level default.
- **Plugin v1 with old server.** Plugin sends `mode`, old server ignores unknown field. Forward-compat is fine. Old plugin against new server is also fine (server defaults to extract).
- **The synth-LLM might still see a reference attachment's filename.** `SourcesRenderer` produces text that ends up in the final note body. If the LLM is shown the in-progress note state during a follow-up edit, it could pick up the filename. This is acceptable — filenames are descriptive metadata, not extraction content — but worth noting.

## Effort breakdown

- Schema, migration, entity, ingest contract: 2h
- ResolveSidecar refactor + new Referenced status wiring: 1h
- UrlMetadataWorker (oEmbed + AngleSharp + tests): 3h
- SynthesisPromptBuilder + SourcesRenderer changes: 2h
- Backend tests: 2h
- Plugin: defaultModeFor + Id3Sniffer + classifier extension: 2h
- Plugin: chip toggle UI + tests: 2h
- End-to-end mixed-mode saga test: 1h
- Polish + manual smoke on demo vault: 1h

Total: **~1.5 person-days**. MVP cut (no metadata mode, no ID3 sniffer, video gets a hardcoded `reference` default, plugin has a checkbox not a tri-state chip): **~0.5 person-days**, but loses the bookmark-card UX which is half the user value.

## Decision points to confirm before starting

1. **`Referenced` as a new extraction status, or reuse `Skipped` with a populated `ExtractionError = "user_opted_out"`?** Recommend new status — `Skipped` today is searched for as a failure-class signal in logs/metrics; conflating "user choice" with "we bailed" muddies that signal. New status is one constant + minor enum-handler updates.
2. **Default mode for `voice` kind.** Recommended: `extract` (transcribe), auto-downgrade to `reference` only on ID3 detection. The opposite default (reference by default, extract only on toggle) was considered and rejected because voice memos are the primary use case.
3. **Chip UI vs separate dropdown.** Recommended: chip toggle (single icon, cycles). Dropdown is more discoverable but adds visual weight to every attachment.
4. **Where does `metadata` mode's JSON live — `ExtractedText` column or a new `Metadata` JSONB column?** Recommended: reuse `ExtractedText` (text-typed JSON blob). New column is more correct but a migration cost the v1 doesn't earn.
5. **Show og:image thumbnails inline in Sources?** Recommended: yes, when present. The metadata mode's whole point is producing bookmark cards; cards without thumbnails are noticeably weaker.

Owner picks at kickoff; this ticket does not block on the answers.

## Follow-on tickets this unlocks

- **CLOUD-VIDEO-EXTRACTION** — wire `VideoSplitterWorker` for actual frame extraction + audio transcription on `kind=file mode=extract` with video MIME. Becomes meaningful once a user explicitly wants a video processed rather than referenced.
- **CLOUD-ATTACHMENT-CAPTION** — per-attachment user caption field, surfaced in `Sources` next to the reference link. Adds UI surface; doesn't depend on this ticket but composes naturally with `reference` mode.
- **CLOUD-EXTERNAL-ATTACHMENT** — support attaching very large media (video files, full albums) by URL only, no upload. Eliminates the storage cost of pure-reference attachments. Depends on this ticket's mode plumbing.
- **CLOUD-AUTOTAG-AUDIO** — if the ID3 sniffer proves valuable, extend it to extract artist/album/title and surface them as note tags or wikilinks. Cheap follow-up once the sniffer exists.
- **CLOUD-METADATA-MODE-FOR-FILE** — extend `metadata` mode to non-URL kinds (e.g. PDF where only the document title + author + abstract are pulled, not the full body). Defer until evidence the URL-only metadata mode is being used.
