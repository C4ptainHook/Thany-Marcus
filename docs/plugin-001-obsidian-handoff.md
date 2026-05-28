# Plugin 001 — Obsidian plugin scope handoff (2026-05-22)

Captures the design decisions and build scope for the first Thany-Marcus
Obsidian plugin. End-to-end ingest pipeline is green
(`019e4dfa-7396-7b7e-be36-e2c728771ca7` proved body + URL + image + voice
compose into a single processed note). The only gap before a user-facing
demo is a client that lets a user create notes *and* see synced results
inside Obsidian. This document is the plan for that client.

## Goal

A single Obsidian community plugin that:

1. Lets the user capture a composite note (body + image + URL + voice)
   from inside Obsidian and POST it to their cloud's ingest pipeline.
2. Pulls processed notes back from the cloud into the vault as real
   markdown files (with attachments stored on disk so Obsidian renders
   them inline).
3. Surfaces queue/phase state for in-flight ingests so the user knows what
   the cloud is doing.

Sideload only for v1. No community-store submission.

## Confirmed design decisions (locked this session)

1. **Plugin is both producer and consumer.** Earlier sketch had it
   read-only; the brow design makes it a creator surface too. Uses
   `/api/ingest/init` + `/api/ingest/{id}/finalize` for capture (same
   endpoints `tmp/smoke11-ingest.sh` exercises) and `/api/sync/pull` +
   `/api/sync/events` for downstream sync.

2. **Compose-then-submit**, not fire-and-forget. The composite-ingest
   value prop (one note from many attachments) requires a draft state
   where the user assembles attachments before POSTing once. Matches
   `[[composite_ingest_decision]]`.

3. **Supernav brow** pinned to the top of the draft editor leaf
   (option `a` from the grill). Always visible while a draft is open;
   does not pollute non-draft panes.

4. **Reuse Obsidian's editor for body text.** Draft lives as a real
   markdown file at `Thany/_drafts/<draftId>.md`. The plugin decorates
   that one leaf with the brow on top; the bottom is a normal Obsidian
   editor (all keybindings, syntax highlighting, markdown features work
   for free). Draft file deleted after submit; survives Obsidian restart
   in the meantime.

5. **Override paste/drag in the draft pane only** — not vault-wide.
   Image paste → save to `Thany/_drafts/<draftId>/_attachments/`, add
   chip in brow, *do not* insert `![[]]` into the body. URL paste stays
   as text; explicit `+ URL` button promotes a URL in the body to an
   attachment chip (one click, scoped picker). Drag-drop behaves like
   paste.

6. **Vault folder layout is server-decided** via `SyncPullItem.RelativePath`.
   Plugin writes wherever the server says. No client-side routing logic.
   Project-entity rollups (`SyncPullProject.VaultFolder`) are first-class
   and shape the folder tree.

7. **Visual style is Obsidian-native chrome + Thany pixel icons.** Brow
   uses Obsidian CSS variables (`--background-secondary`, `--text-normal`,
   etc.) so it inherits the user's theme. Brand presence lives in small
   pixel-art icons (mic, cloud-arrow) and the toast animation. Pixel/VT323
   stays in the portal per `[[portal_ux_locks]]`.

8. **Read-only marker on synced notes.** Frontmatter carries
   `thany_locked: true`. Plugin overwrites synced notes on every pull
   (cloud-wins, v1). If the user edits a synced note, plugin shows a
   toast: *"Cloud sync will overwrite. Push-back not supported in v1."*
   Push-back via `/api/sync/push` is deferred to v2.

9. **Auto-open the synced result after submit.** When a draft is
   submitted, the draft file is deleted. ~5–60s later (pipeline) the
   server-composed note arrives via sync at e.g.
   `Thany/Projects/<x>/<name>.md`. Plugin remembers the leaf the draft
   was in and opens the synced file there so the user does not have to
   hunt for the result.

## Server contract (already shipped — do not change in v1)

### `POST /api/ingest/init`
Body: `{ clientNoteId, capturedAt, body, attachments: [...] }` —
attachments declare kind/sha256/byteSize and (for URL kind) the URL in
`extra.url`. Returns `{ noteId, uploads: [{ clientAttachmentId,
attachmentId, uploadUrl, requiredHeaders }, ...] }`.

### `PUT <uploadUrl>` (S3-compatible presigned)
Plugin uploads each binary directly to Spaces with the required headers.

### `POST /api/ingest/{noteId}/finalize`
Body: `{ uploaded: [{ attachmentId, sha256, byteSize }, ...] }`. Server
verifies and enqueues the ingest job.

### `GET /api/sync/pull?since=<DateTimeOffset>&limit=<n>&include=provenance`
Returns `SyncPullResponse` (see
`src/ThanyMarcus.Shared/PluginApi/SyncPull.cs`):
- `items[]`: notes with `relativePath`, `body`, `attachments[]` (each
  with a 1hr presigned `downloadUrl` for binary kinds), `tags`,
  `suggestedProject`, `deleted` flag, `updatedAt`.
- `projects[]`: entities of kind `Project` with `vaultFolder`,
  `canonicalName`, `aliases`, `isUserSource`.
- `nextSince`: cursor for the next call.

### `GET /api/sync/events` (SSE)
Server pushes `note.created`, `note.updated`, `note.deleted`,
`hub.materialized`, phase transition events. Plugin debounces and calls
`/api/sync/pull` in response.

### `POST /api/notes/{noteId}/reprocess`
Re-runs the pipeline for an existing note.

### `DELETE /api/notes/{noteId}`
Soft-deletes; next pull returns the note with `deleted: true`.

### `POST /api/clouds/{cloudId}/plugin-tokens` (portal-api, not cloud-api)
Already wired in portal-web (`PluginTokenCard.svelte`). Returns
`{ token: "tm_...", cloudUrl: "https://..." }` — these are the two
strings the user pastes into plugin settings.

## Vault layout (what the plugin writes)

```
<vault>/
  Thany/                                 plugin-configured root, default "Thany"
    _drafts/                             plugin-managed; never synced
      <draftId>.md                       draft body, edited in Obsidian
      <draftId>/
        _attachments/
          camaro.jpg
          voice.webm
    Inbox/                               server default for unrouted notes
      2026-05-22-smoke-24.md
      _attachments/
        019e4dfa-camaro.jpg
        019e4dfa-force.mp3
    Projects/
      Camaro/
        _hub.md                          entity rollup, auto-updated
        2026-05-22-test-drive.md
      Anthropic/
        _hub.md
```

Frontmatter on a synced note:

```yaml
---
thany_note_id: 019e4dfa-7396-7b7e-be36-e2c728771ca7
thany_updated_at: 2026-05-22T14:30:12Z
thany_locked: true
suggested_project: Camaro
llm_mode: balanced
tags: [smoke-test, vehicle]
attachment_kinds: [image, url, voice]
---
```

## The brow

Decorates the draft leaf only. Two visual states.

### Idle / empty draft
```
| [mic record] [attach] [+ URL]                       [Discard] [Send] |
```

### Drafting with attachments
```
| [mic] [attach] [+ URL]   img:camaro.jpg x  url:anthropic.com x  voice 0:08 x   [Discard] [Send] |
```

### Recording (mic active)
```
| (red dot) 0:12  [waveform...]                     [Stop & attach] [Cancel] |
```

Buttons:
- **mic** — tap-to-toggle. `MediaRecorder` captures WebM/Opus into
  `_attachments/voice.webm`. On stop, attaches as `voice` kind.
- **attach** — opens native file picker → saves selected files into
  `_attachments/` and chips them.
- **+ URL** — opens a small popover listing URLs detected in the body
  text; user picks which to promote to `url` kind attachments. Default
  is no auto-promotion.
- **Send** — disabled while recording. On click: read body, gather
  attachments, POST to `/api/ingest/init` + finalize, then delete the
  draft file and start watching sync for the result.
- **Discard** — deletes draft file + attachments folder, closes leaf.

## First-run flow

1. User opens cloud detail page in portal → clicks *Issue plugin token*
   (`PluginTokenCard.svelte`) → modal shows `cloudUrl` + `tm_...` token
   with copy buttons.
2. User sideloads plugin: drops `thany-marcus/` folder into
   `<vault>/.obsidian/plugins/` → Settings → Community Plugins →
   enables.
3. Settings tab: pastes cloud URL + token, picks vault root folder
   (default `Thany`), clicks *Test connection* → green check (calls
   `/health/ready`).
4. Plugin runs initial sync: loops `GET /api/sync/pull?since=null` with
   `nextSince` cursor until empty. Status bar shows
   `Thany: first sync — 47/120 notes`. Done → `Thany: synced just now`.

## Steady-state flows

### Capture (user creates a note)
1. Cmd+Shift+T (default) or ribbon icon → plugin creates
   `Thany/_drafts/<draftId>.md` → opens in a new leaf → decorates with
   brow.
2. User types body in editor; pastes images / drops files / records
   voice / promotes URLs → chips appear in brow.
3. Click *Send* → POST init → upload binaries to Spaces → POST finalize
   → toast pops with phase progression.
4. Plugin deletes draft file + folder; remembers the leaf.
5. SSE event fires when synced result is ready → sync pulls it →
   plugin opens the new file in the remembered leaf.

### Sync (server pushes a note in)
1. Plugin keeps `EventSource` open to `/api/sync/events` (60s
   reconnect on drop).
2. On event → 500ms debounce → `/api/sync/pull?since=<cursor>` →
   apply diff:
   - new/updated note → write file at `<root>/<relativePath>`; download
     missing binary attachments to `_attachments/` next to the note;
     update local `noteId → [attachment filenames]` index.
   - deleted note → remove the file and orphaned attachments (consult
     index).
   - new/updated project → ensure folder exists; write `_hub.md` if
     server emitted one.
3. Update cursor in plugin state (`data.json`).
4. Status bar reflects state: `Thany: synced 12s ago` /
   `Thany: syncing...` / `Thany: error (click)`.

## Queue surface

Two complementary surfaces:

- **Per-submission toast** — sticky, shows phase progression
  (`Reading attachments → Building note → Picking project → Finding
  entities → Indexing → Ready`). Disappears on success; persists on
  failure with retry button. Phase names translated for users — never
  show `extracting_attachments` raw.
- **Right sidebar panel** — persistent list of in-flight + recent
  notes with phase badges and click-to-focus. Survives the toast
  dismissal; gives panel a view of concurrency during the defense.

Phase translation table:

| Server status | User-facing label |
|---|---|
| `extracting_attachments` | Reading attachments |
| `composing` | Building note |
| `routing` | Picking project |
| `extracting_entities` | Finding entities |
| `embedding` | Indexing |
| `succeeded` / `ready` | Ready — open in vault |
| `failed_*` | Failed (click for details) |

## Plugin file structure

```
thany-marcus/
  manifest.json
  versions.json
  main.js                                esbuild bundle output
  styles.css                             brow + sidebar styling, uses Obsidian vars
  src/
    main.ts                              Plugin class: lifecycle, ribbon, hotkey, commands
    settings.ts                          SettingTab: cloudUrl, token, vaultFolder, recordFormat
    api.ts                               fetch wrapper with Bearer header, retry, 401 surfacing
    draft/
      DraftManager.ts                    create/delete draft file + attachments folder
      BrowView.ts                        decorate draft leaf with brow header
      PasteInterceptor.ts                CodeMirror plugin scoped to draft leaves
      MicRecorder.ts                     MediaRecorder wrapper, waveform meter
      UrlPromoter.ts                     scan body for URLs, popover picker
      Submitter.ts                       POST init + uploads + finalize
    sync/
      SyncPullLoop.ts                    cursor loop driven by SSE + 60s fallback
      EventStream.ts                     EventSource with reconnect
      Writer.ts                          materialize SyncPullItem → vault file
      AttachmentDownloader.ts            fetch presigned URLs, write to _attachments
      AttachmentIndex.ts                 noteId → filenames map (for GC on delete)
    queue/
      QueueStore.ts                      in-flight + recent submissions, reactive
      QueueSidebar.ts                    right-sidebar ItemView
      PhaseToast.ts                      sticky toast per submission
      labels.ts                          phase translation table
```

Build: `esbuild` single-bundle, no React (Obsidian uses its own DOM).
Likely ~800–1200 LOC TypeScript total.

## Key technical specifics

- **Paste interception**: register a CodeMirror 6 `EditorView.domEventHandlers`
  on the draft leaf's editor with capture phase. Inspect
  `e.clipboardData.items`, prefer `image/*` over `text/html` (Safari/
  Chrome image copy ships multi-format), call `e.preventDefault()` and
  route to `DraftManager.addAttachment(blob)`.

- **Drag-drop interception**: same approach, `dragover` + `drop` handlers
  on the draft leaf root. Prevent default to suppress Obsidian's
  vault-write behavior.

- **MediaRecorder**: `new MediaRecorder(stream, { mimeType: 'audio/webm;codecs=opus' })`.
  On Mac the first `getUserMedia` call triggers the system mic
  permission dialog. Cached after grant. Recording stops on tap; the
  Blob is written to `_attachments/voice.webm` via Obsidian's
  `vault.createBinary()`.

- **Leaf decoration**: register a custom `MarkdownView` extension that
  checks if the file is under `Thany/_drafts/` — if yes, render brow
  in the leaf header container. Use `workspace.on('file-open')` to
  attach/detach as the user navigates.

- **Vault watcher noise**: exclude `Thany/_drafts/**` from any sync
  filewatcher. Draft writes must not trigger sync push attempts.

- **First sync large vault**: paginate (`limit=200`), show progress
  bar in status bar / settings tab. Binary downloads in parallel
  (limit 4 concurrent).

## Mac specifics

- `paste` event fires on Cmd+V — code reads `e.clipboardData.items`,
  no key handling needed.
- Mic permission: first click on the mic triggers macOS system dialog.
  Cached after grant. **Rehearse for defense** — first-click can have
  ~2s delay and the dialog is jarring.
- Screenshot paste: Cmd+Shift+Ctrl+4 to clipboard, then Cmd+V into the
  brow draft editor. PNG, handled fine.
- Multi-format clipboard (browser image copy): prefer `image/*` over
  `text/html` to avoid attaching the surrounding webpage HTML.

## Open edges (acknowledged, deferred — not blockers for demo)

- **Attachment GC on note delete** — `AttachmentIndex` tracks
  `noteId → filenames`. On `deleted: true`, remove the file *and*
  the indexed attachments. Without the index we leak files.
- **Token revoke surfacing** — re-issue in portal silently invalidates
  the plugin's token; first 401 must show a clear *"Token revoked.
  Re-paste from portal."* prompt, not a generic error.
- **VaultFolder rename on server** — if user renames a project's
  `vaultFolder` in portal, server starts sending notes with new
  `relativePath`; plugin can't distinguish rename from new file and
  leaves stale files at old paths. v1 acceptable; document in README.
- **Conflict UX** — v1 is cloud-wins with a toast. v2 candidate:
  `thany_locked: false` opt-in marker that switches to local-wins
  for selected notes.
- **Sync push (push-back)** — server endpoint exists
  (`/api/sync/push`), plugin does not call it in v1. v2 work item.
- **Large vault first sync UX** — 500+ notes with binaries will take
  minutes; status bar progress bar is the minimum; a settings-tab
  detailed progress is nicer-to-have.
- **Failure deep-link** — failed phase toast should deep-link to the
  portal's job detail page for the user to inspect provenance.
  Requires portal route; not built yet.

## Explicitly out of scope for v1

- Community plugin store submission (sideload only).
- Push-back (`POST /api/sync/push`) for local edits.
- Hub note regeneration triggered from plugin.
- Project creation / rename from plugin (portal only).
- Mobile (iOS / Android Obsidian) — desktop only. Mobile lacks
  reliable `MediaRecorder` and the leaf-decoration API differs.
- Multiple cloud connections from one vault.
- Local LLM inference / offline mode.
- Search / semantic search UI inside Obsidian.

## Acceptance criteria for "demo-ready"

1. From a fresh vault, sideloading the plugin and pasting URL + token
   from `PluginTokenCard` results in successful initial sync within
   30s (no test data yet — empty response is the green path).
2. Pressing Cmd+Shift+T opens a draft leaf with the brow visible.
3. Cmd+V on a copied image attaches it as a chip and saves the file
   under `_attachments/`.
4. Clicking the mic, speaking, clicking stop attaches a voice chip.
5. Typing a URL in the body and clicking *+ URL* lets the user promote
   it to a `url` attachment chip.
6. Clicking *Send* with body + image + url + voice POSTs to `/api/ingest/init`,
   uploads binaries, finalizes. Toast appears showing phase progression.
7. Within ~60s (current vision CPU constraint), the synced note appears
   at its `relativePath` and the leaf auto-switches to it. Image and
   voice are embedded via `![[...]]` and render/play inline in Obsidian.
8. Right sidebar shows the note in the recent list with `ready` badge.
9. Deleting the note via `DELETE /api/notes/{id}` (from anywhere) causes
   the vault file and its `_attachments/` to disappear within one sync
   cycle.

When all nine pass: defense-ready.

## Related docs

- `docs/decisions/0042-cloud-ingest-pipeline-architecture.md`
- `docs/decisions/0045-composite-note-schema.md`
- `docs/smoke-2026-05-21-two-model-handoff.md` (current model lineup
  used during ingest)
- `src/ThanyMarcus.Shared/PluginApi/SyncPull.cs` (authoritative DTO
  shapes)
- `src/ThanyMarcus.Cloud.Api/Features/Sync/` (server endpoints)
- `src/ThanyMarcus.Portal.Web/src/lib/PluginTokenCard.svelte` (token UX)
- `tmp/smoke11-ingest.sh` (reference for the ingest call sequence the
  plugin must reproduce)
