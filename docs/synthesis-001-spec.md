# Synthesis 001 — composer rework spec (2026-05-28)

Supersedes the deterministic composer (`CompositeNoteComposer.StitchBody` and
the `## User Notes` / `## System Output` template) with an LLM-driven
synthesis phase that produces a single coherent Zettelkasten-style note from
the user's body + extracted attachment text + extracted entities.

Locks the six design questions grilled this session. Captures the pipeline
reorder, the markdown contract, API surface changes, plugin UI changes, and
the migration plan.

## Why this exists

Smoke test on 2026-05-22 produced a `ready` note with:

- `## User Notes` empty (placeholder only — `UserNotesPreserver` returned
  empty when `previousBodyOutput` was null on first compose)
- `## System Output` with `[audio: voice.webm]` (no Obsidian wikilinks)
  and `<!-- thany-marcus:attachment status=failed -->` (image dropped
  silently on vision timeout)
- Zero `[[entity wikilinks]]` and zero `#tags`
- Note read as random pieces of OCR text + ASR transcript + URL extract,
  not as an atomic Zettelkasten idea

The current composer is the wrong abstraction. Stitching templates produce
output that doesn't participate in the user's knowledge graph. Replacing
the composer with an LLM synthesis phase produces coherent prose with
inline wikilinks while preserving raw extractions as auditable sources.

## Locked design decisions

### Q1 — Truth vs interpretation: grounded synthesis

- LLM produces first-person Zettelkasten prose from extracted inputs
- Hard rules in prompt: no invented facts, no invented quotes, no invented
  causal claims
- Wikilinks CONSTRAINED to the provided entity list (server appends the
  canonical entity list as a structured section the model cannot ignore;
  any `[[...]]` in output that doesn't match the list is a validation
  failure)
- Default voice: first-person, atomic, connecting
- v1 ships without few-shot exemplars — explicit style instruction is
  sufficient for the small models used in safe mode and unnecessary for
  the larger API models in public mode

### Q2 — Provenance: synthesis + collapsed callout sources

Every synthesized note has a `## Sources` section preserving the raw
inputs verbatim. Two render rules:

- **Visual media** (`image`, `video`): embedded VISIBLY in `## Sources`,
  with the vision/video caption shown as italic text underneath the embed
  (or italic failure reason if extraction failed)
- **Audio/voice, URL, file (PDF/doc text), user notes**: collapsed
  `> [!source]-` Obsidian callouts containing the verbatim ASR transcript
  / URL extract / file extract / user body

Order within `## Sources`: chronological by attachment creation order.
User notes always first if present.

Embeds appear ONLY in `## Sources` in v1 — synthesis prose is text-only.
Inline images in prose are a v2 add (requires LLM placement reliability).

### Q3 — Idempotency: cache by source hash

Re-synthesis triggers in the live pipeline:

| Trigger | Behavior |
|---|---|
| Saga within-phase retry | Hit cache — no second LLM call |
| User clicks reprocess | Cache invalidated, fresh LLM call |
| Hub materialize/regen | Does NOT touch source notes (regen affects hub notes only — unchanged from today) |

Cache key:
`sha256(rawExtractionsHash + mentionsHash + modelVersion + promptVersion + privacyMode)`

Cache value: the synthesized body + frontmatter snapshot.

Reprocess input is raw extractions only (no current body) — reprocess is a
"start fresh" button. `thany_locked: true` already warns the user that
local edits will be clobbered.

Temperature: 0.3 (natural prose, slight variation; cache prevents drift
across retries).

Versioning metadata always in frontmatter:
```yaml
synthesis_model: gemini-3.5-flash    # or qwen3-1.7b-q4_k_m for private
synthesis_prompt_version: preset-zettelkasten-v1
synthesis_seed: 42
synthesized_at: 2026-05-28T11:36:12Z
privacy_mode: public
```

Graph fragmentation (new canonical entities don't backfill into old
notes) is accepted for v1. The workaround is Obsidian's native
"unlinked mentions" UI. A backlink-refresh phase is v2.

### Q4 — Style control: preset + optional custom override

Plugin sends one of two shapes in `IngestInit`:

```json
{ "synthesisPreset": "zettelkasten" }
```
or
```json
{ "synthesisPreset": "custom", "customPrompt": "..." }
```

Presets shipped in v1 (server-side bodies):

- `zettelkasten` (default) — first-person, atomic, connecting
- `journal` — first-person, dated, narrative
- `encyclopedic` — third-person, neutral
- `technical` — terse, structured, code-friendly
- `custom` — user-supplied prompt body

Editing the textarea in plugin settings silently flips preset → `custom`.
Reset button restores the active preset's default body.

The cloud ALWAYS appends a structured-context block after the user-controlled
prompt:

```
<user system prompt — preset or custom>

Available entities (use ONLY these for [[wikilinks]]):
- {Canonical Name 1}
- {Canonical Name 2}
...

Inputs:
{user notes verbatim, or <empty/> if no body}
{voice transcript, or <input failed kind="voice" reason="..."/> if extraction failed}
{image caption, or <input failed kind="image" reason="..."/> if extraction failed}
{url extract, or <input failed kind="url" reason="..."/> if extraction failed}
{file extract, or <input failed kind="file" reason="..."/> if extraction failed}
```

User cannot edit this structured-context block — it's the contract that
keeps wikilinks grounded and failures honest.

Frontmatter records: `preset-zettelkasten-v1` or `custom-{sha256[:8]}`.
Custom prompt body stored in cloud DB row (`notes.synthesis_prompt_body`),
not in note frontmatter (too verbose for daily reading).

### Q5 — Failure UX: synthesize from what survived

Per-input failure (vision timeout, ASR crash, URL 404):

- Synthesis ALWAYS runs with available inputs
- Failed inputs are passed as explicit `<input failed reason="..."/>` tokens
  so the model knows they exist but has no content to invent from
- Prompt rule: "acknowledge failed sources by presence only ('the attached
  image'); do not invent details"
- File still embeds in `## Sources` with the failure reason visible:
  - Image: `![[gmail-icons.png]]\n*vision extraction failed (600s timeout)*`
  - Voice: `> [!source]- Voice — ![[voice.webm]]\n> ASR failed: model crashed`

All-fail + no user body edge case: produce a stub note (frontmatter +
`## Sources` listing the failed attachments + body line *"Composite ingest
captured at {timestamp}; no extractions succeeded."*). User can reprocess
or delete.

Synthesis itself fails (LLM OOM, timeout, API error, etc.): body becomes
a sentinel marker (*"Synthesis failed — see Sources below."*), `## Sources`
is rendered as normal. Frontmatter records `synthesis_status: failed` +
`synthesis_error: "<reason>"`. User reprocess retries.

Critical consequence: the deterministic stitch orchestration
(`CompositeNoteComposer.StitchBody` and the `## User Notes` /
`## System Output` template) is **retired entirely**. The
`## Sources` section IS the fallback content; no parallel
template path is maintained. Per-kind renderers (`AudioRenderer`,
`ImageRenderer`, etc.) are repurposed to produce callout/embed
content for the `## Sources` block.

Server signals partial failures to plugin via a new field on
`SyncPullItem`:
```typescript
extractionFailures: [
  { kind: "image", attachmentId: "019e4ed3-fa9d-75a3-...",
    reason: "vision timeout 600s", soft: true }
]
```

Plugin uses this to badge the queue entry red and surface a toast with
a Reprocess button. All failures soft for v1 (reprocess retries
everything). Hard-fail classification is v2.

### Q6 — Model choice: binary privacy mode, user-picked public model

Two modes, mutually exclusive:

**Private** (fully local — nothing leaves the cloud):
- Vision: MiniCPM-V local
- Routing: Qwen3-1.7B-Q4 local
- Entity extraction: Qwen3-1.7B-Q4 local
- **Synthesis: Qwen3-1.7B-Q4 local**

**Public** (synthesis goes to a user-chosen API; everything else still local):
- Vision: MiniCPM-V local
- Routing: Qwen3-1.7B-Q4 local
- Entity extraction: Qwen3-1.7B-Q4 local
- **Synthesis: user-picked from supported list**

Supported public models (v1):

| Model | Provider | Cost/ingest (~2k in / ~500 out) |
|---|---|---|
| `gemini-2.5-flash-lite` | Google | ~$0.0004 |
| `gemini-2.5-flash` | Google | ~$0.002 |
| `gemini-3.5-flash` (default) | Google | ~$0.008 |
| `claude-haiku-4-5` | Anthropic | ~$0.005 |
| `claude-sonnet-4-6` | Anthropic | ~$0.02 |

API keys are PLUGIN-LEVEL, sent per `IngestInit`, NEVER persisted by the
cloud. The privacy posture is: cloud admin cannot snoop keys even with
filesystem access.

Cloud-api validation: in public mode, if `llmApiKey` is missing or the
matching provider key is missing for the requested model, return 400.

Honest tradeoff: private-mode synthesis quality is bounded by Qwen3-1.7B-Q4
output quality (passable summaries, weak voice transfer, occasional
encyclopedic regression). Public-mode synthesis on Gemini 3.5 Flash or
Claude Haiku 4.5 is genuinely good. Mode is the user's call per the
privacy/quality tradeoff they want.

## Pipeline reorder

Current order:
```
extracting_attachments → composing → routing → extracting_entities → embedding → ready
```

New order:
```
extracting_attachments → extracting_entities → routing → synthesizing → embedding → ready
```

Why this order:

- `extracting_entities` runs against raw concatenated extractions
  (works the same as today). Mentions are written to DB.
- `routing` uses raw extractions + extracted entities for richer
  classification ("mentions [[Anthropic]] and [[Claude]] → project: AI")
- `synthesizing` runs LAST among the body-producing phases — it has
  raw extractions, mentions, project context, and user body as inputs.
  This kills the chicken-and-egg problem of "compose before entities
  exist" that prevented inline wikilinks in the current design.
- `embedding` runs against the synthesized body so the vector reflects
  the canonical wiki-linked prose, not raw extractions.

## Note structure (final markdown)

```markdown
---
thany_note_id: 019e4ed3-fa9d-73b8-af17-7e0d2f8550ac
thany_updated_at: 2026-05-28T11:36:12Z
thany_locked: true
suggested_project: AI Tools
llm_mode: balanced
tags: [ai, ui-design, brand-language]
attachment_kinds: [image, voice, url]
privacy_mode: public
synthesis_model: gemini-3.5-flash
synthesis_prompt_version: preset-zettelkasten-v1
synthesis_seed: 42
synthesized_at: 2026-05-28T11:36:12Z
synthesis_status: ok
---

# Gmail's gradient redesign signals Google's identity drift

Google rolled out a [[Material 3]] gradient on the Gmail iconography this week.
The [[9to5Google]] post frames it as alignment with the broader
[[Workspace]] visual refresh. My screenshot of the side-by-side suggests
the change is more than ornamental: the new mark drops Gmail's red-envelope
semantics for a prismatic surface, pulling it visually closer to
[[Photos]] and [[Drive]].

Worth tracking against my running [[brand erosion]] note — when a product
icon stops meaning something specific and starts looking like every other
app in the suite, you've lost the recognition affordance that made it work.

## Sources

![[gmail-icons-2026-05-28.png]]
*Side-by-side of Gmail's old red envelope vs the new gradient mark*

> [!source]- Voice — ![[voice-1779437954957.webm]]
> Note to self — Gmail icon change today, the new gradient looks like
> every other Workspace app. Worth a writeup.

> [!source]- URL — [9to5google.com — Gmail redesign 2026-04-26](https://9to5google.com/2026/04/26/gmail-google-gradient-redesign/)
> Gmail icons redesigned with Google's gradient language. The new mark
> uses the [Material 3] color system… [extract continues]

> [!source]- User notes
> I want to capture note about how and when google redesigned app icons
```

Failure variant (vision timed out, synthesis succeeded):

```markdown
---
...
synthesis_status: ok
---

# (synthesis prose using available inputs — references "the attached image" without describing content)

## Sources

![[gmail-icons-2026-05-28.png]]
*vision extraction failed (600s timeout)*

> [!source]- Voice — ![[voice-1779.webm]]
> [transcript]
```

Failure variant (synthesis itself failed):

```markdown
---
synthesis_status: failed
synthesis_error: "LLM timeout after 60s"
synthesis_model: gemini-3.5-flash
---

*Synthesis failed — see Sources below.*

## Sources

(same Sources rendering as normal)
```

## API contract changes

### `IngestInit` request — additions

```typescript
{
  // existing fields unchanged: clientNoteId, capturedAt, body, attachments[]

  privacyMode: "private" | "public",
  publicModel?: "gemini-2.5-flash-lite" | "gemini-2.5-flash"
              | "gemini-3.5-flash" | "claude-haiku-4-5" | "claude-sonnet-4-6",
  llmApiKey?: string,           // required if privacyMode = "public"
  synthesisPreset: "zettelkasten" | "journal" | "encyclopedic" | "technical" | "custom",
  customPrompt?: string,        // required if synthesisPreset = "custom"
}
```

Validation (cloud-api side):
- `privacyMode === "public"` && (`publicModel` missing || `llmApiKey` missing) → 400
- `synthesisPreset === "custom"` && `customPrompt` missing → 400
- Both fields whitelisted against fixed enums; reject unknown values

### `SyncPullItem` response — additions

```typescript
{
  // existing fields unchanged

  extractionFailures?: [
    {
      kind: "image" | "voice" | "url" | "file",
      attachmentId: string,
      reason: string,
      soft: boolean,
    }
  ],
}
```

Frontmatter fields (now in note body, not API):
- `privacy_mode`, `synthesis_model`, `synthesis_prompt_version`,
  `synthesis_seed`, `synthesized_at`, `synthesis_status`,
  `synthesis_error` (only when failed)

## Cloud-api changes

### New files

- `src/.../Features/Processing/Phases/SynthesizingHandler.cs` — new phase
- `src/.../Features/Processing/Synthesis/SynthesisPromptBuilder.cs` —
  builds (preset body or custom body) + structured context block
- `src/.../Features/Processing/Synthesis/SynthesisPresets.cs` — preset
  bodies (`zettelkasten` / `journal` / `encyclopedic` / `technical`)
- `src/.../Features/Processing/Synthesis/SourcesRenderer.cs` — builds
  `## Sources` block from per-kind renderers (consumes the existing
  `AudioRenderer`, `ImageRenderer`, etc., repurposed)
- `src/.../Infrastructure/Llm/GoogleGeminiClient.cs` — new provider
  implementing `ILlmClient`
- `src/.../Infrastructure/Llm/AnthropicClient.cs` — new provider
  implementing `ILlmClient`

### Modified files

- `src/.../Features/Processing/JobPhases.cs` — reorder phases, add
  `synthesizing` between `routing` and `embedding`
- `src/.../Features/Processing/Phases/ExtractingEntitiesHandler.cs` —
  runs against raw concatenated extractions (no longer against
  `BodyOutput`)
- `src/.../Features/Processing/Phases/RoutingHandler.cs` — uses raw
  extractions + entities for classification input
- `src/.../Features/Ingest/IngestEndpoints.cs` — `IngestInit` accepts
  the new fields; validation per the contract above
- `src/.../Features/Sync/SyncPullEndpoint.cs` — emits
  `extractionFailures[]` on items where applicable
- `src/.../Infrastructure/Llm/LlmClientFactory.cs` — resolves provider
  based on `(privacyMode, publicModel)`; Google + Anthropic in addition
  to existing local Ollama
- `src/.../Infrastructure/Storage/S3ArtifactStore.cs` — line 36: drop
  `Content-Length` from `requiredHeaders` (independent fix not affected
  by synthesis rework but bundled with this deploy)
- Logging middleware: redact `llmApiKey` and `customPrompt` from
  request logs

### Retired files / code

- `src/.../Features/Processing/Composing/UserNotesPreserver.cs` —
  delete (user body becomes a source callout, not a preserved section)
- `src/.../Features/Processing/Composing/CompositeNoteComposer.cs:StitchBody`
  and related orchestration — retire the deterministic stitch path
  (per-kind renderers preserved, used by new `SourcesRenderer`)
- The `## User Notes` / `## System Output` template entirely

### Database additions

```sql
ALTER TABLE notes ADD COLUMN synthesis_prompt_body TEXT NULL;
ALTER TABLE notes ADD COLUMN synthesis_cache_key TEXT NULL;
ALTER TABLE notes ADD COLUMN synthesis_cache_value TEXT NULL;

CREATE INDEX ix_notes_synthesis_cache_key ON notes (synthesis_cache_key)
  WHERE synthesis_cache_key IS NOT NULL;
```

The `synthesis_cache_key` is the sha256 hash from Q3. The
`synthesis_cache_value` is the full synthesized body. On cache hit, the
saga skips the LLM call and writes `cache_value` to `BodyOutput`.

## Plugin changes

### Settings UI additions

```
─── Synthesis ───
Privacy mode: ( ) Private    (•) Public

(when Public)
Model: [Gemini 3.5 Flash (recommended)            ▼]

Synthesis style: [Zettelkasten                    ▼]
[▶ Show prompt]   (expands to textarea; editing flips to "Custom")
[Reset to preset default]

─── API keys ───  (only the key for your chosen model is required)
Google AI Studio:  [••••••••••••]  [Test]
Anthropic:         [••••••••••••]  [Test]
```

### Plugin file changes

- `src/settings.ts` — new fields: `privacyMode`, `publicModel`,
  `synthesisPreset`, `customPrompt`, `googleApiKey`, `anthropicApiKey`
- `src/draft/Submitter.ts` — populate the new fields in `IngestInit`
  body from settings
- `src/api.ts` — `IngestInitRequest` shape extended with new fields;
  add `extractionFailures` to `SyncPullItem` type
- `src/sync/Writer.ts` — render the `## Sources` block from server data
  per Q2+ rules (visible image + italic caption, collapsed callouts for
  voice/URL/user-notes); NO LONGER appends `![[]]` at the bottom
  defensively (server's `## Sources` owns embeds)
- `src/queue/QueueSidebar.ts` + `PhaseToast.ts` — surface
  `extractionFailures[]` per item with reprocess CTA

### Key validation flow

`[Test]` button in plugin settings hits the provider's `list models`
endpoint directly (NOT through the cloud) with the entered key:

- Anthropic: `GET https://api.anthropic.com/v1/models`
- Google: `GET https://generativelanguage.googleapis.com/v1beta/models?key=...`

Reports valid/invalid. Bad keys never touch the cloud.

### Key storage

`data.json` plaintext for v1 with a README warning. `safeStorage`
(Electron OS keychain) is a v2 add.

## Migration

Existing notes in DB at deploy time:

- Notes already in `ready` status: leave alone. Old composed body remains
  until a reprocess.
- Notes mid-flight at deploy: saga will pick up at its current phase. If
  in `composing`, the deploy replaces that phase with the new
  `synthesizing` after `routing`. Need careful rollout — drain in-flight
  jobs before deploying the schema change, or write a saga adapter that
  routes legacy phase names to the new ones.

Practical migration: kill the saga worker, deploy schema + new code,
restart worker. In-flight jobs whose state machine references the old
`composing` phase will fail with a "phase not found" error and need
manual reprocess. Acceptable for v1 (test environment has at most a
handful of in-flight jobs).

## Independent fixes bundled with this deploy

These are unrelated to synthesis but should ship in the same image build
since the reprovision is forced anyway:

- **`S3ArtifactStore.cs:36`** — drop `Content-Length` from
  `requiredHeaders`. Confirmed root cause of the `ERR_INVALID_ARGUMENT`
  Electron rejection from earlier in the session. Defensive client-side
  filter in `Submitter.ts:put` stays as belt-and-suspenders.
- **CORS** — add `app://obsidian.md` to allowed origins on
  `/api/sync/*` and `/api/ingest/*`. Without this, SSE `/api/sync/events`
  fails preflight and the plugin falls back to 60s polling.
- **SSH key wiring** — authorize the user's local `id_ed25519` in the
  cloud-init key list. Currently the cloud is provisioned with
  `marcus_dev` (DO key fingerprint `ea:e0:7c:9e:92:d1:e6:b1:db:fa:aa:49:49:96:4b:24`)
  which does not match the local `id_ed25519` (`f4:be:86:95:20:53:91:76:cf:7b:84:bb:73:b9:32:f7`).
  Add `~/.ssh/id_ed25519.pub` content to the saga-worker's authorized
  keys list so future reprovisions are SSH-able from this machine.

## Acceptance criteria

1. Compose an ingest with body text + image + voice + URL via the plugin
   brow. Submit. Within ~60s (Gemini Flash) or ~10min (private-mode
   Qwen3-1.7B with vision), the synced note arrives in the vault.
2. The synced note has a coherent first-person prose body that
   references entities from the user's graph as `[[CanonicalName]]`
   wikilinks. None of those wikilinks point to entities that aren't in
   the user's existing graph.
3. The `## Sources` section contains: the image embedded visibly with
   an italic caption, the voice transcript in a collapsed callout, the
   URL extract in a collapsed callout, the user's typed body in a
   collapsed callout — in chronological order.
4. Frontmatter contains all six synthesis metadata fields
   (`synthesis_model`, `synthesis_prompt_version`, `synthesis_seed`,
   `synthesized_at`, `privacy_mode`, `synthesis_status`).
5. Toggle privacy mode to `Private` in plugin settings. Submit another
   ingest. Note arrives with `privacy_mode: private` and
   `synthesis_model: qwen3-1.7b-q4_k_m` — synthesis quality is
   noticeably lower but the structure is identical.
6. Submit an ingest where vision is forced to fail (large image / known
   timeout case). The note arrives with the image still embedded
   visibly under `## Sources` with an italic failure note; synthesis
   prose acknowledges the image's presence without inventing content;
   `extractionFailures[]` is populated in the sync pull payload and the
   queue sidebar shows a red badge.
7. Click Reprocess on a synced note. Synthesis runs again with a
   different seed; cached value is bypassed; result is fresh prose.
8. Change `synthesisPreset` from `zettelkasten` to `technical` in plugin
   settings. Submit a new ingest. Output style demonstrably changes
   (terser, more structured).
9. Edit the system prompt textarea in settings (anything). Preset
   silently switches to `custom`. Submit a new ingest. Frontmatter
   shows `synthesis_prompt_version: custom-{sha256[:8]}`.

When all nine pass: synthesis is demo-ready.

## Out of scope for v1

- Few-shot exemplars (Q4 — explicit instruction is enough for v1)
- RAG-style synthesis with hub note context (Q1's Point C)
- Hard-fail classification for unsupported attachment formats
- Backlink-refresh phase for graph fragmentation (Q3)
- Inline images in synthesis prose (vs Sources-only)
- Per-user API keys with overrides (vs per-cloud) — already plugin-level
- `safeStorage`-encrypted key storage (Q6 sub-decision)
- Sync push-back from plugin
- Mobile (Obsidian on iOS/Android)
- Local model upgrade path (Qwen3-7B / Llama 3.1 70B on bigger droplets)

## Related docs

- `docs/plugin-001-obsidian-handoff.md` — plugin design; this spec
  extends the IngestInit and SyncPullItem contracts it referenced
- `docs/smoke-2026-05-21-two-model-handoff.md` — model lineup context;
  the vision-timeout scenario this spec accommodates
- `docs/decisions/0042-cloud-ingest-pipeline-architecture.md`
- `docs/decisions/0044-cloud-intelligence-layer.md`
- `docs/decisions/0045-composite-note-schema.md` — superseded by this
  spec's note structure
- `src/ThanyMarcus.Shared/PluginApi/SyncPull.cs` and
  `IngestInit.cs` — DTOs to extend
- `src/ThanyMarcus.Cloud.Api/Features/Processing/Composing/` —
  composer code to retire (renderers preserved, orchestration
  retired)
