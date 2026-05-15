# Consolidated Project Plan (Revised)

Date: 2026-05-12
Status: Second-pass revision after deep design grilling. Locks captured in Appendices A and B at the end.

## 1. Final Thesis Direction

The project is a personal-cloud service plus ambient-capture desktop client that aggregates a user's heterogeneous personal artifacts (voice memos, screenshots, photos, PDFs, handwritten notes, URLs, selected text, dictation, drag-dropped files) into an Obsidian-compatible Markdown vault with emergent graph structure via dense wikilinks and tags. Desktop-only in MVP; mobile capture is future work.

Each user provisions their own cloud instance on their chosen provider (DigitalOcean or Hetzner). Default deployment is single-tenant per cloud (Tier 1, SSH-only, no public domain required); user can opt into Tier 2 by adding a domain, which unlocks per-project sharing via magic links surfaced in Avalonia.

Core transformation (local-first; cloud is a thin coordinator):

```text
ambient desktop capture (hotkeys, drag-drop, URL paste)
  -> local pre-filter (silence/blur/dup; classical signal processing)
  -> toast preview (optionally openable for voice/text annotation)
  -> local async queue (saga-orchestrated by Avalonia)
  -> local LLM processing on user's CPU (routing + entity extraction + wikilink generation + note generation)
       - fetches context from cloud (project list, top-K similar entities, top-K similar notes)
       - runs 4B model locally via LLamaSharp
  -> Markdown notes with ## System Output / ## User Notes regions + provenance JSON
  -> push results to cloud (artifact + processed note + provenance)
  -> cloud updates vector indexes; syncs back to Obsidian vault
  -> Tier 2 only: per-project sharing via magic link surfaced in Avalonia
  -> Cloud GPU opt-in for vision + complex synthesis + user-edit-aware regen (smart batched)
```

## 2. Working Title

Primary (user-stated):

> **Cloud Service for Personal Data Aggregation and Organization in Markdown Format**

Expanded technical form:

> **A Personal Cloud Service with Ambient-Capture Desktop Client for Multimodal Data Aggregation and Provenance-Aware Obsidian-Compatible Markdown Vault Generation with Emergent Knowledge-Graph Structure**

## 3. Problem Statement

Personal heterogeneous data accumulates across devices and is hard to find, organize, and share without losing context. Existing tools (Obsidian, Notion, Mem.ai, Reflect) are excellent editors but require manual ingestion and organization. URL bookmarks rot. Voice memos pile up untranscribed. Screenshots lose context.

This project addresses these problems by:

- Ambient-capture entry points (hotkey-triggered, not continuous recording) that turn capture into a fire-and-forget action
- Automatic processing through modality-specific routes
- LLM-driven auto-routing into projects with project auto-creation
- LLM-driven entity extraction and hub-note maintenance
- LLM-driven dense wikilink generation, producing an emergent knowledge graph
- URL snapshotting as a link-rot mitigation
- Tiered deployment so users can start fully private (no domain, no external dependencies) and opt into more features as needed

## 4. What The System Is / Is Not

It is:

- A personal-cloud service (single-tenant per user, multi-tenant-capable code)
- An ambient-capture desktop client (Avalonia, MVP is desktop-only)
- A processor pipeline with LLM auto-routing
- An Obsidian-compatible Markdown vault generator
- A provenance-aware system with observability built in
- A per-project sharing system via magic link surfaced in Avalonia (Tier 2)
- A knowledge-graph generator via wikilinks and tags

It is not:

- A Markdown editor (Obsidian is)
- A multi-user collaborative editing tool (read-only sharing only)
- A vector-search question-answering system (deferred to future work)
- A chatbot for the vault (deferred)
- A continuous-recording surveillance tool (ambient = on-demand, not Windows Recall)
- A clinical / medical system (pivoted away from)
- A frontier-cloud-LLM-only system (self-hosted only)

## 5. Persona

**Live researcher**: a continuous capturer across long-running concurrent research threads. The user does not actively maintain or focus on project organization. Capture is fire-and-forget; the system auto-routes and auto-organizes. The user reads the resulting Markdown vault in Obsidian, where the graph view reveals connections via wikilinks. Optionally shares per-project subsets with coauthors / advisor (Tier 2 only).

## 6. Main Contribution

The novelty is the combination of:

1. **Ambient-capture-first design** with hotkey-driven desktop ingestion and a unified expanded-preview annotation pattern
2. **LLM auto-routing** with LLM-driven project auto-creation and open-ended entity-type emergence
3. **Emergent knowledge graph** via dense LLM-generated wikilinks and open-vocabulary tags — folders are scaffolding, the graph carries semantics
4. **URL snapshotting** as link-rot mitigation
5. **Local-wins offline editing with cloud-regen of derived content** — saga-orchestrated, supports user-edit awareness via system prompt instruction
6. **Tiered deployment** — Tier 1 zero-external-dependency private mode (SSH-only, no domain, no public CA), Tier 2 opt-in via domain (unlocks per-project sharing)
7. **Local-first LLM processing with opt-in cloud GPU** — 4B model runs on user's PC via LLamaSharp; cloud holds storage + vector index + on-demand GPU only. No always-on cloud CPU LLM. Stronger privacy: LLM never executes against user data on the cloud. Lower cost: Tier 1 cloud ~€5/month (storage only).

Strongest claim:

> The system turns ambient personal captures into an Obsidian-compatible Markdown vault with an emergent knowledge graph through local-first LLM processing, with the cloud acting as a thin storage and coordination layer plus opt-in GPU for heavier tasks. Tier 1 deployment costs ~€5/month and keeps LLM analysis on the user's own machine.

## 7. End-To-End Architecture

```text
Avalonia Desktop Client (MVP is desktop-only; mobile is future work)
  - 6 capture surfaces (hotkey voice, screenshot, selected-text, URL, drag-drop, dictation)
  - toast preview + expand-to-annotate
  - local Parakeet ASR (dictation)
  - local classical D filter
  - LOCAL 4B LLM via LLamaSharp (routing + extraction + wikilinks + note generation)
  - local async queue + saga orchestration
  - offline edit queue + sync engine
  - admin: provision / destroy / passphrase
        |
        |  Tier 1: SSH tunnel
        |  Tier 2 (after domain added): HTTPS via Caddy + Let's Encrypt for sharing only
        v
    Cloud API (ASP.NET Core, single-tenant default) — thin coordinator
        |
        |  storage + sync + vector index + GPU orchestration only
        |  NO LLM running on cloud in Tier 1
        v
    Cloud Services
        - Artifact storage (per tenant)
        - PostgreSQL (metadata, project list, entity index)
        - Vector index (entity embeddings, note embeddings) — queried by local LLM for retrieval
        - Sync API (saga endpoints; local saga pushes results here)
        - GPU spin-up orchestration (opt-in, smart batched)
        |
        |  results from local LLM arrive: artifact + processed note + provenance
        v
    Hierarchical Vault Store (per tenant)
        - Inbox/        (cold-start, uncategorized)
        - Projects/     (LLM- or user-created)
        - _Entities/    (LLM-maintained hubs, open-ended types)
        - .system/, .provenance/
        |
        v
    Sync Engine (Avalonia, saga-based)
        - file watcher on local Obsidian vault
        - local-wins for ## User Notes
        - cloud-regen for ## System Output (with user-edit-aware prompt)
        - tombstones for user deletions
        |
        v
    Per-Project Sharing (Tier 2)
        - magic link surfaced in Avalonia (user shares via own channel)
        - 24h default expiry (configurable)
        - revocable from Avalonia dashboard, read-only zip download
```

## 8. Desktop Client (Avalonia)

Technology:

- Avalonia UI, .NET 8+
- Parakeet-tdt 0.6B v3 ONNX via sherpa-onnx C# bindings (or direct Microsoft.ML.OnnxRuntime as fallback)
- **LLamaSharp** (llama.cpp .NET wrapper) hosting the local 4B LLM
- SSH.NET for Tier 1 SSH-tunneled communication; HttpClient for Tier 2 HTTPS
- Argon2id + AES-GCM for encrypted credentials vault

Responsibilities:

- Ambient capture (Section 9)
- Local pre-filter (silence/blur/dup, classical signal processing)
- Local Parakeet ASR for dictation (creates standalone text artifact)
- **Local 4B LLM inference** via LLamaSharp: routing, entity extraction, wikilink generation, baseline note generation
- **Local vector index** (sqlite-vss) + **local embedding model** for similarity retrieval — no cloud context fetch needed during processing
- **Local saga orchestration**: queues captures, runs full LLM pipeline locally (with local indexes), pushes results to cloud as backup
- Toast notification preview with optional expand-to-annotate (hold-space voice + optional text)
- Offline edit queueing (works fully offline — no cloud dependency for processing)
- File watcher on local Obsidian vault folder
- Sync engine (push artifacts + notes + provenance to cloud as backup; pull GPU-mode results back; pull on disaster recovery)
- System tray UI (anchor + drag-drop sink + click-to-open-main + hotkey-opens-panel)
- Main app view (project list, recent captures, dropped-log review, settings, local-LLM queue)
- Cloud lifecycle admin: provisioning wizard, destroy, passphrase-protected credentials
- Passphrase-derived AES-GCM encrypted credentials file with 8 one-time recovery codes
- **Auto-detect system RAM** at first run and recommend local model size
- **Two-path storage configuration**: user picks (or accepts defaults for) a Vault path (Obsidian-facing, portable, backup-able) and an App Support path (machine-local internals, not for backup). Both individually configurable, OS-appropriate defaults, change-path-with-migration support.

Not responsible for:

- Multimodal vision processing (cloud GPU only, when GPU mode enabled)
- Complex synthesis or user-edit-aware regen (cloud GPU only, opt-in)
- Final access control (cloud)
- Multi-device sync coordination (cloud is authoritative; multi-device is MVP single)
- Markdown editing (Obsidian is)

## 9. Capture Surfaces

Six desktop entry points:

1. **Hotkey voice memo** — captures audio, local Parakeet transcribes (on user's PC), local LLM generates note, results pushed to cloud
2. **Hotkey screenshot** — captures region, local D filter (blur/dup), uploads if passes
3. **Hotkey selected-text capture** — reads OS clipboard, captures active window title as context
4. **URL capture** — drag URL to tray, or paste into Avalonia; fetches and processes URL content via cloud URL processor
5. **Drag-and-drop to system tray** — any file
6. **Local dictation hotkey** — Parakeet transcribes locally for instant feedback, then text uploads as standalone text artifact (B2d)

**Toast preview** appears after every capture (auto-dismiss, ~5s configurable). User can click to expand into a fuller view that shows:

- Captured artifact (image thumbnail, audio waveform, file name, URL preview)
- Filter status (passed / dropped, with reason)
- Microphone affordance (hold space → record ~30s voice annotation)
- Text field for optional typed annotation

Annotation attaches to the artifact uniformly across all capture types. Default flow remains fire-and-forget; annotation is opt-in by clicking the toast.

Ambient definition: on-demand capture (user triggers via hotkey), NOT continuous like Windows Recall.

## 10. Cloud Backend

Cloud is a **thin coordinator + storage** in Tier 1. **No LLM runs on cloud** in Tier 1. LLM execution happens locally on the user's PC.

Technology:

- ASP.NET Core API
- PostgreSQL (multi-tenant-capable; single-tenant in default deployment)
- Filesystem-backed artifact storage on VPS volume, encrypted via provider-native encryption
- Vector index (pgvector or sqlite-vss extension) for entity embeddings + note embeddings — queried by local Avalonia for retrieval
- Caddy reverse proxy with dynamic config (minimal in Tier 1, full HTTPS in Tier 2)
- Docker Compose for service orchestration
- Terraform for provisioning (Hetzner + DigitalOcean)
- **No email service** — sharing links surfaced to user in Avalonia, shared via user's own channel
- **No LLM containers** in Tier 1 — cloud baseline is just API + Postgres + Caddy + storage

Tiered exposure:

- **Tier 1**: API binds to `localhost:8080`, reachable only via SSH tunnel from Avalonia
- **Tier 2**: Caddy reconfigured with user-supplied domain, Let's Encrypt cert auto-provisioned, public HTTPS for sharing endpoints (recipient browser access)

Responsibilities:

- User-cloud account auth (per-user email/password, argon2id, recovery codes — no email reset)
- Artifact upload + storage
- Project + entity registries (canonical state)
- **Vector retrieval API** — Avalonia local LLM queries this for similar entities, similar notes, project descriptions
- **Sync API** — receives processed artifacts + notes + provenance from local saga; propagates back to other devices (future)
- **Result ingest** — accepts local LLM output, updates vector indexes, commits to vault
- Magic-link share management (Tier 2): generates tokenized URLs, surfaces them to user via Avalonia, handles revocation
- Dynamic Caddy reconfiguration on Tier 1 → Tier 2 upgrade
- **GPU spin-up orchestration** (when GPU mode enabled): Terraform-managed GEX instance lifecycle, Wireguard tunnel, smart batching

What cloud does NOT do in Tier 1:
- Run an LLM (Tier 1 has no Ollama, no model containers, no GPU)
- Process Markdown / generate notes (that's local)
- Extract entities or generate wikilinks (that's local)
- Do auto-routing decisions (that's local)

Candidate endpoints:

```text
POST   /auth/register
POST   /auth/login
POST   /auth/reset-password
POST   /artifacts/upload
GET    /jobs/{id}
GET    /projects
GET    /projects/{id}
POST   /shares                  (Tier 2: creates magic link for a project; returns link to user)
GET    /shared/{token}          (Tier 2: recipient endpoint)
POST   /shares/{id}/revoke      (Tier 2)
GET    /sync/pull               (sync engine API)
POST   /sync/push               (sync engine API)
POST   /admin/upgrade-tier-2    (called by Avalonia on domain add)
POST   /admin/regenerate/{note_id}  (manual regen request)
```

## 11. Processor Router and Async Pipeline

Config-driven router. Saga is **orchestrated locally by Avalonia**, not by cloud. Each route's processors are tagged with where they execute: `local` (always on Avalonia), `cloud-fetch` (Avalonia calls cloud API for data), `cloud-gpu` (only when GPU mode enabled, smart-batched).

Routes:

```yaml
routes:
  voice_memo_audio:
    processors:
      - audio_metadata_extractor          # local
      - parakeet_transcription            # local (cloud option in future-work for weak hardware)
      - cloud_context_fetch               # cloud-fetch (similar entities, similar notes, project list)
      - llm_artifact_note_generator       # local (4B LLM)
      - llm_entity_extractor              # local (4B LLM, with retrieved dedup context)
      - llm_wikilink_generator            # local (4B LLM, with retrieved similar notes)
      - llm_routing_classifier            # local (4B LLM)
      - push_to_cloud                     # cloud-fetch (upload artifact + note + provenance)
      - provenance_writer                 # local + cloud

  image:  # handles screenshots, photos, handwritten notes, scanned documents
    processors:
      - image_metadata_extractor          # local
      - vision_processor:
          gpu_path:  qwen_3.6_multimodal  # cloud-gpu (opt-in, batched)
          cpu_path:  tesseract_local      # local OCR fallback when GPU mode off
      - cloud_context_fetch               # cloud-fetch
      - llm_artifact_note_generator       # local (uses vision output as input)
      - llm_entity_extractor              # local
      - llm_wikilink_generator            # local
      - llm_routing_classifier            # local
      - push_to_cloud                     # cloud-fetch
      - provenance_writer                 # local + cloud

  pdf_document:
    processors:
      - pdf_text_extractor                # local
      - cloud_context_fetch               # cloud-fetch
      - llm_artifact_note_generator       # local
      - llm_entity_extractor              # local
      - llm_wikilink_generator            # local
      - llm_routing_classifier            # local
      - push_to_cloud                     # cloud-fetch
      - provenance_writer                 # local + cloud

  url_capture:
    processors:
      - url_type_detector                 # local
      - per_type_fetcher:                 # local fetches via user's machine
          web_article:  readability_extractor -> llm_markdown_transform (local)
          youtube:      yt_dlp_transcript -> markdown (local; user's IP fetches transcript)
          pdf_url:      download -> delegate_to_pdf_document
          github_repo:  readme_fetcher + metadata (local)
      - cloud_context_fetch               # cloud-fetch
      - llm_artifact_note_generator       # local
      - llm_entity_extractor              # local
      - llm_wikilink_generator            # local
      - llm_routing_classifier            # local
      - push_to_cloud                     # cloud-fetch
      - provenance_writer                 # local + cloud

  text_artifact:  # selected-text, dictation output, drag-dropped text files
    processors:
      - text_metadata_extractor           # local
      - cloud_context_fetch               # cloud-fetch
      - llm_artifact_note_generator       # local
      - llm_entity_extractor              # local
      - llm_wikilink_generator            # local
      - llm_routing_classifier            # local
      - push_to_cloud                     # cloud-fetch
      - provenance_writer                 # local + cloud

  generic_file:
    processors:
      - file_metadata_extractor           # local
      - mime_classifier                   # local
      - text_fallback_processor           # local (if textual content extractable)
      - push_to_cloud                     # cloud-fetch
      - provenance_writer                 # local + cloud
```

Saga orchestration (lives in Avalonia):

```text
capture received locally
  -> persist raw artifact + initial provenance to local queue
  -> enqueue processing saga in Avalonia
  -> step 1: modality detection (local, synchronous)
  -> step 2: extraction processors (local, parallel where possible)
  -> step 3: cloud context fetch (vector retrieval for similar entities + notes; project list)
  -> step 4: local LLM enrichment (4B model: routing + extraction + wikilinks + note generation)
  -> step 5: push results to cloud (upload artifact + note + provenance + entity/note embeddings)
  -> step 6: cloud applies updates to vault state, propagates to local sync engine
  -> step 7: GPU-eligible tasks (vision, regen) deferred to cloud GPU batch (when GPU mode enabled)
  -> compensating actions on failure at any step (retry, rollback local queue state, etc.)
```

## 12. LLM Strategy

**Pluggable LLM backend with three primary modes** + orthogonal GPU upgrade. User picks a primary backend in onboarding and can switch later in settings.

### Primary backend options

**L — Local 4B (default, recommended)**
- LLamaSharp on user's PC with locally-stored model
- Free (already-owned CPU)
- Private (LLM analysis never leaves user's machine)
- Requires ≥8 GB RAM
- Configurable model size (2B / 4B / 8B Q4); RAM auto-detect at first run

**CC — Cloud CPU**
- Always-on Ollama + 4B model container on cloud (Hetzner CPX or equivalent)
- Adds ~€10/month to baseline (Tier 1 becomes ~€15/month with this mode)
- Data stays in user's own cloud (single-tenant, private)
- Works on any hardware — no local LLM required

**API — External LLM provider** (with security flag)
- OpenAI, Anthropic, or similar — user picks provider at setup
- API key stored encrypted in passphrase vault
- Cheapest per-artifact for low volume; frontier-model quality
- **Data leaves the user's cloud to the provider** — explicit security warning during setup
- User must check "I understand data is sent to <provider>" before enabling
- Persistent settings badge once enabled
- Per-call audit log entry (provider + timestamp + prompt hash)

### Local model sizes (when primary backend = L)

- **Gemma 4 E2B Q4** (~1.5 GB active) — 8 GB RAM systems; faster, lower quality
- **Gemma 4 E4B Q4 or Qwen 3.5-4B Q4** (~3–4 GB active) — default recommended; needs 16 GB RAM
- **Llama 3.1 8B Q4 or Qwen 3.5-8B Q4** (~5–6 GB active) — 16 GB+ RAM, higher quality
- Apple Silicon: 8 GB M-series ≈ 16 GB x86 for inference purposes

Handles: routing, entity extraction (with locally-retrieved dedup context), wikilink generation, baseline note generation.

**ASR model (local, always available):** Parakeet-tdt 0.6B v3 ONNX — Ukrainian + 25 European languages, ~1 GB active. Same model serves both dictation (real-time feedback) and voice memo transcription. Via sherpa-onnx C# or Microsoft.ML.OnnxRuntime.

**GPU mode (opt-in, cloud, default OFF):** when enabled, user picks GPU model size:

- **Qwen 3.6-35B-A3B** (default for quality) — Apache 2.0, MoE with ~3B active params, natively multimodal
- **Qwen 3.5-14B** or **Llama 3.1 8B** — cost-conscious alternatives, smaller GPU instance, cheaper batches
- **Gemma 4 31B Dense** — Apache 2.0 alternative

Used for: vision processing (images), complex synthesis (long-form summarization), user-edit-aware regen quality upgrade. Smart-batched (Section 22).

**GPU mode is a quality upgrade, not a feature gate.** Everything functions on the local 4B model by default. Enabling GPU mode escalates specific tasks (vision, complex synthesis, user-edit-aware regen) to the larger cloud GPU model for higher fidelity. Disabling GPU mode degrades quality but loses no features.

**Vision fallback when GPU mode off:** local Tesseract / PaddleOCR for text-in-image; EXIF + perceptual hash for photo similarity. Acceptable baseline; degraded vs vision-LLM but functional.

### Cost matrix

| Primary backend | GPU upgrade | Total |
|-----------------|-------------|-------|
| L (Local) | Off | ~€5/month (Tier 1 baseline) |
| L | On | ~€5 + €10–150/month based on GPU usage |
| CC (Cloud CPU) | Off | ~€15/month (baseline + always-on Ollama container) |
| CC | On | ~€15 + €10–150/month |
| API (External) | Off | ~€5/month + provider API costs (cents to dollars per artifact) |
| API | On | ~€5/month + provider costs + €10–150/month GPU (mixed; vision/synthesis on GPU, rest on API) |

Privacy stance varies by backend choice:
- L: strongest — LLM never executes against user data outside the user's PC
- CC: strong — LLM runs in user's own cloud; data never reaches third parties
- API: weakest — data sent to provider; explicit consent required (security flag)

GPU upgrade (any primary backend): vision/synthesis/regen route to cloud GPU batch. Cloud GPU is in user's own cloud (Hetzner GEX provisioned by user's own Terraform), so no third-party exposure for the GPU tier specifically.

Fine-tuning: future work. Task-specific fine-tune of a 4B model for routing could be a thesis extension.

Inference engines:

- **Local**: LLamaSharp (llama.cpp .NET wrapper) on Avalonia for the 4B LLM
- **Local**: sherpa-onnx or Microsoft.ML.OnnxRuntime for Parakeet ASR
- **GPU mode (cloud, opt-in)**: Ollama in the spun-up GEX instance; vLLM as a future-work alternative for higher throughput

## 13. Local Subsystems (Avalonia)

**Four local subsystems on the user's PC:**

**A. Parakeet-tdt 0.6B v3 ONNX for dictation + voice memo transcription**

- Real-time transcription buffer during dictation (instant user feedback)
- Same model handles voice memo artifacts (upload not needed; transcript created locally)
- Multilingual: Ukrainian + 25 European languages, auto-detected
- ~1 GB active RAM during inference; ~600 MB disk
- Integration paths (priority order):
  1. sherpa-onnx C# bindings (lowest effort)
  2. Direct Microsoft.ML.OnnxRuntime (pure .NET; port from parakeet-rs reference, ~1–2 weeks)
  3. parakeet-rs as Rust sidecar (last resort)

**B. Local 4B LLM via LLamaSharp**

- Handles: routing, entity extraction (with cloud-fetched dedup context), wikilink generation (with cloud-fetched similar-note context), baseline note generation
- Model: configurable (2B / 4B / 8B Q4); default 4B; auto-recommended at first run based on RAM detection
- LLamaSharp = llama.cpp .NET wrapper; pure .NET; cross-platform; no external Ollama needed
- ~3–4 GB active RAM during inference (4B Q4); ~2.5 GB disk
- Model bundled with Avalonia installer OR downloaded at first-run (config decision; bundling is simpler, increases installer size to ~5 GB)
- Inference latency: ~30–60 seconds per artifact on typical laptop CPU; async, non-blocking
- Settings UI shows: "Your system has X GB RAM. Recommended local model: Y." User can override.

**C. Classical quality / duplicate filter (D filter)**

- Silent audio: energy threshold / VAD
- Blurry image: Laplacian variance threshold
- Near-duplicate screenshots: perceptual hash (dHash / pHash)
- Implementation: classical signal processing in C#, no ML model
- Privacy detection: deferred to future work
- Dropped artifacts → local drop-log queue surfaced in main app for review

**D. Local vector index + embedding model**

- Vector store: sqlite-vss (SQLite extension) embedded in Avalonia — no separate process
- Embedding model: small sentence-transformers-style model (~100 MB on disk, fast CPU inference)
- Stores entity embeddings + note embeddings for similarity retrieval
- Queried by the 4B LLM during entity dedup and wikilink generation — all local, no cloud round-trip
- Lives under the App Support path (rebuildable from notes if corrupted; not part of backup)
- Re-index pass on first run (or after model swap) to populate from existing vault content

**RAM requirements summary**:

| RAM | Recommended local model | Notes |
|-----|-------------------------|-------|
| 16 GB+ | 4B (default) or 8B | Comfortable, full feature set |
| 8 GB | 2B | Tight but workable; close other heavy apps during processing |
| <8 GB | Skip local LLM | Enable GPU mode (cloud) for all processing |

Apple Silicon (M-series): treat as one tier up due to unified memory bandwidth. 8 GB M-series ≈ 16 GB x86 for this workload.

## 14. Auto-Routing and Project Creation

**LLM-driven routing is the only routing mechanism.** No explicit user hint surface (HA inline-tags / HB tray-pre-pin both dropped during grilling).

The LLM picks up inline `#tag` mentions in transcribed/captured content naturally via prompt context — an emergent behavior, not a separate feature.

Routing flow:

```text
artifact + extracted content
  -> LLM sees existing project list + descriptions
     (vector-retrieved top-K if project count is large)
  -> LLM picks existing project OR proposes new project
  -> if new: LLM generates folder name (slug-case + year-suffix conventions in prompt)
  -> routing decision + confidence + top-3 alternatives written to provenance JSON
```

**Cold-start**: first artifacts land in `Inbox/` until N=10 artifacts accumulate, at which point LLM proposes initial project groupings (user accepts/edits).

**Project explosion**: deferred. Project-creation events instrumented for testing-phase analysis. Future mitigation: minimum-artifact-count threshold before a candidate project sticks.

**Inside-project folder structure**: fixed schema (not LLM-determined): `Artifacts/`, `Notes/`, `.provenance/`.

**User-created folders** (created directly in Obsidian by the user): equal-status routing targets. LLM sees them and routes into them when appropriate. No "user-managed" distinction.

## 15. Knowledge Generation

Outputs per processed artifact:

- Markdown artifact note (one per artifact)
- Updates to relevant entity hub notes
- LLM-inserted `[[wikilinks]]` in this note and possibly in related notes (cross-linking)
- Provenance JSON record
- Evidence files (transcripts, OCR text, thumbnails, URL snapshots)

**Markdown note structure** (uniform across artifact notes and entity hubs):

```markdown
---
# YAML frontmatter, system-managed tags + metadata
---

## System Output

(LLM-generated body — summary, references, [[wikilinks]])

## User Notes

(free-form user-authored content; preserved on sync; local-wins)
```

Sync behavior per region:

- **`## User Notes`** → local-wins on sync
- **`## System Output`** → cloud regenerates on relevant changes (new related artifacts, etc.)
- **User edits to `## System Output`** → accepted, but tracked: sync stores diff in provenance; regen prompts the LLM with previous output + current local + diff + system-prompt instruction to respect user edits as meaningful input. **Default: runs on local 4B LLM (works offline, free).** When GPU mode is enabled, regen escalates to the GPU model for higher-fidelity instruction-following. LLM has final authority but is aware of user intent.
- **User deletions of LLM content (tombstones)** → tracked in `.provenance/`; LLM regenerator respects suppressions

**Wikilink generation**: dedicated `llm_wikilink_generator` processor. For each new artifact, the LLM is given the note + a set of related-note candidates (retrieved via vector similarity over note embeddings + recent-context window). LLM inserts `[[wikilinks]]` in the note's `## System Output` body, producing the dense Zettelkasten-style graph the user wants in Obsidian's graph view.

## 16. Hierarchical Vault Design

The vault lives under the user-configurable **Vault path** (Section 8). Per-OS defaults: `~/Documents/<vault-name>` (macOS, Linux) or `%UserProfile%\Documents\<vault-name>` (Windows). User can change to any location, including external drives.

Per-tenant vault structure (Obsidian-opened root):

```text
<tenant>/
  Inbox/
    (Markdown notes for cold-start / uncategorized artifacts)

  Projects/
    (LLM-auto-created OR user-created project folders; uniform internal structure)
    Iceland-2026/
      Artifacts/
      Notes/
      .provenance/
    PhD-Glaciology/
      Artifacts/
      Notes/
      .provenance/

  _Entities/
    (LLM-maintained entity hubs; folder name IS the entity type; types are open-ended)
    People/
      Alice.md
      Bob.md
    Places/
      Reykjavik.md
    Papers/
    Concepts/
    (whatever emerges)

  .system/
    (sync metadata, configuration)

  .provenance/
    (per-artifact provenance JSON shadow tree)
```

Folders are scaffolding for browsing. The semantic structure is carried by wikilinks and tags (Zettelkasten emphasis). Project folder = shareable unit (Tier 2).

## 17. Entity Hubs

**Entity types are open-ended** — the LLM creates entity-type folders ad-hoc; folder name IS the type. Same emergent pattern as project auto-creation.

**Hub creation trigger**: hub materializes when an entity accumulates **N=3 mentions** (default, tunable). Below threshold, mentions resolve to dangling Obsidian `[[wikilinks]]` (graph nodes appear unresolved).

**Entity deduplication**: LLM-driven at extraction time. When LLM extracts a candidate entity, the system retrieves the top-K most-similar existing entities (vector embedding similarity over entity surface forms) and passes them in the extraction prompt. LLM decides "this is an alias of existing X" or "this is a new entity Y." Aliases accumulate in the hub's YAML frontmatter `aliases:` list. User can manually merge mistakes via Obsidian rename + move (sync brings the change back to cloud).

Hub note structure (uses same `## System Output` / `## User Notes` convention):

```markdown
---
type: <auto-detected entity type / folder name>
name: <canonical entity name>
aliases: [Alice, Alice Smith, Dr. Smith]
tags: [...]
---

## System Output

### Context

(canonical source-of-truth — raw mentions, quoted snippets, link references)

- [voice memo 2026-04-15 @ Iceland-2026]: "Alice mentioned glaciers..."
- [screenshot 2026-04-22 @ PhD-Glaciology]: thesis abstract link
- [auto-collected backlinks from all artifacts mentioning Alice]

### Generated Views

(derived state, regenerated on demand)

- TL;DR: ...
- Timeline of mentions: ...
- Related entities: ...

### Attachments

(images, files inlined or linked)

### External References

(URLs, paper DOIs, etc.)

## User Notes

(user-authored content; survives regen by virtue of local-wins on user region)
```

Context subsection is canonical. Generated Views is derived — regenerated by LLM on demand. This separation handles staleness naturally: views are always re-derivable from context.

## 18. Sync Model and Offline Editing

Offline editing is supported. Local-wins for user-authored content. Sync engine is saga-orchestrated, lives in Avalonia, watches the local Obsidian vault folder.

Sync semantics:

- `## User Notes` → local-wins
- `## System Output` → cloud regenerates; user edits to system region are accepted (B4c + system prompt mitigation)
- User deletions of LLM content → tombstones in provenance, respected by regen

Saga flow (lives in Avalonia; all steps local unless GPU mode escalation):

```text
local change detected (file watcher on Obsidian vault folder)
  -> diff step: compute diffs locally; identify user edits in ## System Output region
  -> push step: backup diffs + new local LLM output to cloud (as artifact backup)
  -> regen step: when downstream dependencies trigger regen (new related artifact, etc.):
       default: LOCAL 4B LLM regenerates with user-edit-aware prompt (simplified version)
       when GPU mode enabled: regen task joins the GPU batch for higher-fidelity instruction-following
       prompt includes:
         - current artifact context
         - previous system output (what LLM wrote)
         - current local version (with user edits)
         - explicit diff highlight
         - system prompt: "if user has edited the previous output, pay attention to their changes;
           treat them as meaningful input; you may still update with new information"
  -> apply step: regenerated content written back to the local note (system region only)
  -> compensating actions on failure at any step
```

**GPU mode is a quality upgrade, not a feature gate.** Regen happens regardless of GPU mode — locally on the 4B model by default; on GPU when user opts in for higher fidelity. Provenance JSON logs which tier ran (`tier: local | gpu`).

Handled:

- Offline user edits + concurrent cloud captures → user-region preserved; system-region regenerated with user-edit awareness
- User deletes LLM content → tombstone preserves deletion against future regen
- Network partition → saga retries with idempotency

Out of MVP:

- Multi-device concurrent user-region edits to the same file (CRDT or 3-way merge)
- Real-time collaboration (sharing is read-only)

## 19. Sharing Model

**Tier 2 only.** Per-project sharing via magic link surfaced to the user; user shares the link through whatever channel they prefer.

Flow:

```text
user picks a project in Avalonia dashboard
  -> Avalonia requests a share token from cloud
  -> cloud generates tokenized URL (cryptographically random, 32+ bytes)
  -> cloud returns the link to Avalonia
  -> Avalonia shows the link to user (with copy button)
  -> user shares the link through their own channel (Signal, WhatsApp, manual email, SMS, in person, etc.)
  -> recipient clicks link (no registration required)
  -> recipient downloads zip of the project (Markdown + linked assets)
  -> sender can revoke from Avalonia dashboard at any time
```

Properties:

- **No email-service dependency** — system never sends email; user controls the sharing channel
- No recipient registration
- Read-only download (Markdown + assets)
- Revocable from Avalonia dashboard
- **Default expiry: 24 hours** (configurable)
- Minimum audit trail: system logs share creation and revocation; per-recipient accesses not logged
- True "minimum trail" — system has no record of who the user shared with

Recipient experience: opens zip in Obsidian or any Markdown reader; no live updates after download.

## 20. Security and Privacy

Trust model:

- Each user trusts their own cloud (single-tenant per cloud instance)
- Personal data never leaves the user's own cloud (no third-party LLM, no shared SaaS provider, no email service)
- Tier 2 introduces controlled external dependencies: domain DNS provider, Let's Encrypt CA

**Authentication and admin**:

- Per-user account auth: email + password with argon2id hashing; **password recovery via one-time recovery codes** (generated at account creation, parallel to passphrase recovery codes) — no email-based reset since system sends no email
- Cloud-provider API token (admin) stored locally in Avalonia, encrypted with Argon2id-derived AES-GCM key from user passphrase
- Two-level auth: user account login is independent from cloud-provider admin token
- 8 one-time recovery codes generated at passphrase setup; each can decrypt the master key
- 8 one-time recovery codes generated at user-cloud-account creation; each can reset the account password
- Lost passphrase + lost recovery codes → manual fallback via DO/Hetzner web console (destroy + reprovision, or SSH-direct DB reset)

**Transport**:

- Tier 1: SSH tunnel only (key auth)
- Tier 2: HTTPS via Caddy + Let's Encrypt for sharing endpoints (recipient browser access); Avalonia continues using SSH for its own traffic
- Internal API binds to localhost; not exposed without Caddy in Tier 2

**At-rest**:

- Filesystem encryption required at the VPS volume level via DO/Hetzner provider-native encryption (Terraform flag, no LUKS complexity)

**Sharing security (Tier 2)**:

- Cryptographically random 32+ byte tokens, single-purpose per share
- Default 24-hour expiry
- Per-share revocation invalidates the token immediately
- User controls the sharing channel — system never auto-sends to recipient

**Audit trail**: minimum — account login, share creation, share revocation, cloud destroy actions

**Channel compromise**: residual risk depends on the channel the user picks. If user shares via WhatsApp, compromised WhatsApp = compromised share. If shared in person verbally, no remote attack surface. Mitigations baked in: 24h expiry limits exposure window; revoke from Avalonia dashboard kills the link immediately.

**External API mode residual risk** (only when user opts into API primary backend): captured content (transcripts, OCR text, note bodies) sent to the chosen provider per LLM call. Provider may store, log, or use the data per its own terms. Mitigations: explicit security-flag consent during setup; persistent settings badge; per-call audit log entry; easy switch back to Local or Cloud CPU. User responsibility to read provider's privacy policy before enabling.

Out of MVP threat model: insider on cloud host; server-side exfiltration after compromise; side-channel attacks on LLM; compliance frameworks (HIPAA/GDPR); provider-side breaches in External API mode.

## 21. Observability and Provenance Schema

Observability is built into the architecture from MVP. Provenance JSON per artifact (and per hub note for regen tracking):

```json
{
  "artifact_id": "...",
  "ingested_at": "...",
  "capture_source": "hotkey_voice_memo | hotkey_screenshot | hotkey_selected_text | url_capture | tray_drop | local_dictation",
  "raw_artifact_hash": "...",
  "modality": "audio | image | pdf | url | text | file",
  "filter_decisions": {
    "local_filter_passed": true,
    "filter_scores": {"blur": 0.02, "silence": null, "duplicate": 0.12}
  },
  "annotation": {
    "user_voice_annotation_id": "...",
    "user_text_annotation": "..."
  },
  "processors_run": [
    {
      "processor": "local_parakeet_transcription",
      "model_version": "parakeet-tdt-0.6b-v3",
      "input_hash": "...",
      "output_hash": "...",
      "duration_ms": 1234,
      "language_detected": "uk",
      "metadata": {}
    }
  ],
  "llm_calls": [
    {
      "task": "routing | entity_extraction | note_generation | wikilink_generation | regen",
      "model": "qwen-3.5-4B-Q4",
      "primary_backend": "local | cloud_cpu | external_api_openai | external_api_anthropic | ...",
      "tier": "local | cloud_cpu | gpu | external_api",
      "prompt_hash": "...",
      "response_hash": "...",
      "confidence": 0.83,
      "alternatives": []
    }
  ],
  "routing_decision": {
    "selected_project": "Iceland-2026",
    "confidence": 0.83,
    "top_3_alternatives": ["Inbox", "Travel-General", "PhD-Glaciology"],
    "was_new_project_created": false,
    "was_user_created_folder": false
  },
  "entity_extractions": [
    {"name": "Alice", "type": "People", "confidence": 0.91, "deduplicated_to_existing": true, "alias_of": "alice_smith"}
  ],
  "wikilinks_inserted": [
    {"target_note": "...", "similarity_score": 0.87, "context": "..."}
  ],
  "user_suppressions": [],
  "user_system_edits": [
    {"diff": "...", "at": "...", "ack_by_regen_at": "..."}
  ],
  "regen_count": 0,
  "last_regen_at": null
}
```

This schema enables future evaluation of:

- Routing accuracy
- Entity-extraction quality + dedup performance
- ASR transcription quality (per-language WER)
- Wikilink relevance
- User-edit respect (did regen preserve user changes?)
- Filter precision/recall
- Project growth rate
- Capture-source distribution

Evaluation methodology defers to the thesis testing chapter (per user preference: architecture handles observability, testing chapter handles metrics).

## 22. Deployment

**Tier 1 (default, primary backend = L)** — minimal Docker Compose on Hetzner CCX11 or smallest DO droplet (~€5/month):

- API container (ASP.NET Core)
- PostgreSQL (or SQLite alternative) for metadata; vector index lives on user's PC (sqlite-vss)
- Caddy in minimal mode (SSH-only access via SSH tunnel)
- Encrypted filesystem volume (provider-native encryption)
- **No LLM containers in primary backend = L** — LLM runs on user's PC
- **No Parakeet on cloud** — ASR runs on user's PC
- Cloud is pure storage + backup + sharing source

**Tier 1 with primary backend = CC (Cloud CPU)** — adds always-on LLM container:

- Same baseline as above PLUS
- Ollama container with 4B model always-loaded (e.g., Qwen 3.5-4B Q4)
- Bumps cloud spec to Hetzner CCX or similar with ~6 GB RAM available
- Adds ~€10/month → Tier 1 total ~€15/month
- Avalonia routes LLM calls to cloud Ollama instead of running locally

**Tier 1 with primary backend = API (External)** — same as L deployment:

- No cloud-side LLM container needed (calls go directly from Avalonia to external provider)
- Avalonia stores provider API key in passphrase-encrypted vault
- Per-call audit logged in provenance JSON

**Tier 2 (add domain)** — same stack with reconfigured Caddy:

- User adds domain via Avalonia onboarding (Section 22 wizard)
- Caddy reconfigured via admin API for the new domain
- Let's Encrypt cert auto-provisioned (~30s)
- Sharing endpoints come online (recipient browser → magic link)
- No email service added — sharing links are surfaced to the user via Avalonia and shared through the user's own channel

**GPU spin-up (opt-in, smart batching)** — for multimodal vision, heavy synthesis, regen, when GPU mode is enabled:

- **Batching policy** (hybrid threshold + ceiling):
  - Threshold: spin up GPU when ≥N=5 GPU-eligible tasks queued (default, configurable)
  - Ceiling: spin up GPU when oldest queued task hits T=1 hour wait (default, configurable)
  - All queued tasks processed in one GPU session
  - GPU torn down 5 min after last task completes (cooldown captures stragglers)
- **Provisioning**: Worker provisions Hetzner GEX instance via Terraform; **instance size adapts to user's chosen GPU model** (e.g., smaller GEX for 8B model, larger GEX for 35B MoE)
- **Communication**: Wireguard tunnel auto-established for secure CPU↔GPU traffic
- **Cold start**: ~5 minutes (GPU boot + Ollama model load); saga long-polls and tolerates
- **Manual override**: user can hit "process now" on a queued task to bypass batching; rest of batch joins the same spin-up
- **Failure mode**: if GPU provisioning fails (quota, capacity), GPU-eligible tasks fall back to CPU model with degraded quality; user notified via toast

**Avalonia onboarding flow (Tier 1)**:

1. Pick storage paths: Vault path + App Support path — OS defaults pre-filled
2. **Existing local vault detection**: if Avalonia finds an existing vault at the chosen path (from a previous install / destroy cycle), offer two paths:
   - **Start fresh**: new empty cloud + new empty local state
   - **Re-seed from this vault**: upload existing artifacts + notes + provenance to the freshly-provisioned cloud as initial state (recovers from destroy + reprovision scenario)
3. **Pick primary LLM backend**: Local (default) / Cloud CPU / External API
   - If Local: Avalonia auto-detects RAM and recommends model size (2B / 4B / 8B)
   - If Cloud CPU: cloud deployment will include always-on Ollama container (~€10/month extra)
   - If External API: full-screen security warning; user picks provider (OpenAI / Anthropic / etc.) and enters API key; explicit "I understand data is sent" consent
4. Pick cloud provider (DO or Hetzner)
5. Enter cloud provider API token
6. Create user-cloud-account: email + password + 8 recovery codes shown
7. Set passphrase for encrypted credential vault (separate from account password); 8 passphrase recovery codes shown
8. Avalonia generates SSH keypair, runs Terraform locally
9. Cloud comes up; SSH public key installed; encrypted volume attached; user-cloud-account seeded; Ollama container if CC mode
10. If re-seed chosen at step 2: Avalonia uploads local vault contents to fresh cloud + re-derives embeddings into local sqlite-vss index
11. Avalonia stores credentials (cloud-provider token + SSH key + cloud URL + LLM API key if API mode) in passphrase-encrypted vault under App Support path
12. Avalonia downloads relevant local models (LLM if L mode; Parakeet always; embedding model always) into App Support path
13. Avalonia displays cloud IP/hostname; saves to settings
14. Ready to use

**Avalonia upgrade flow (Tier 1 → Tier 2)**:

1. Settings → Add domain → user enters `myvault.example.com`
2. Avalonia shows A-record instructions for the user's DNS provider
3. User confirms DNS done
4. Avalonia signals cloud to reconfigure Caddy with the new domain
5. Caddy obtains Let's Encrypt cert (~30s)
6. Avalonia confirms in UI: "Tier 2 enabled at https://myvault.example.com"
7. Sharing now available

**Destroy flow**:

- Settings → Destroy cloud → passphrase confirmation
- Terraform destroy: VPS instance + encrypted volumes + Wireguard config
- Manual DNS cleanup notice if Tier 2
- **Local Obsidian vault on user's machine survives** (intentional — user keeps their data)
- **Local App Support state survives** (models, encrypted credentials, sqlite-vss index) — but credentials now point at a destroyed cloud; cloud-provider token still valid for provisioning a new cloud
- Avalonia offers immediate "Provision new cloud" workflow that detects the surviving local vault and offers re-seed path (see onboarding flow step 2)
- Avalonia local state can be fully wiped via separate "Uninstall Avalonia" action (separate from cloud destroy)

Not in MVP:

- Kubernetes
- Managed database
- Multi-node API scaling
- Enterprise object storage
- Multi-region failover

## 23. Evaluation Plan

Per user preference, evaluation belongs in the testing chapter, not architecture. The architecture commits to observability-rich provenance (Section 21) so any subset of these metrics can be measured later:

- Routing accuracy (top-1, top-3, invented-project rate)
- Entity-extraction precision/recall + dedup quality
- Wikilink relevance (LLM-judged or manual)
- Parakeet transcription WER (per language; Ukrainian especially)
- Filter precision/recall (against labeled keep/drop set)
- Sync correctness under concurrent-edit and partition scenarios
- Sharing security (recipient scoping, revocation behavior, link expiry enforcement)
- User-edit respect rate (how often did LLM regen preserve vs override user edits?)
- End-to-end "competent tool" qualitative use over N weeks

Recommended starting subset for the testing chapter: routing accuracy + entity extraction + qualitative E2E + user-edit respect rate.

## 24. MVP Scope

Implement:

- Avalonia client with six desktop capture surfaces (Section 9)
- Toast preview with expand-to-annotate (hold-space voice + optional text annotation)
- **Primary LLM backend selection**: Local (LLamaSharp with configurable 2B/4B/8B); Cloud CPU (always-on Ollama container); External API (with security flag, multi-provider support starting with OpenAI + Anthropic). RAM auto-detect at first run when Local picked.
- **Local vector index** (sqlite-vss) + **local embedding model** for similarity retrieval
- Parakeet-tdt 0.6B v3 ONNX local ASR (sherpa-onnx C# bindings or Microsoft.ML.OnnxRuntime fallback)
- Classical D filter (silence/blur/dup) + dropped-log review surface
- **Two-path storage configuration** in onboarding + Settings (Vault path + App Support path) with OS defaults, change-with-migration (copy/move/switch), graceful degradation on path unavailability
- **External API security flag UX**: full-screen consent modal on enable, persistent badge, per-call audit log, easy switch-back
- **Re-seed from local vault**: onboarding wizard detects existing local vault and offers to upload its contents to a freshly-provisioned cloud as initial state (recovery after destroy + reprovision; covers new-device + cloud-intact pull as the symmetric case)
- Cloud admin: passphrase-protected credential vault (Argon2id + AES-GCM), 8 recovery codes, provisioning wizard, destroy with confirmation, Tier 1 / Tier 2 upgrade
- ASP.NET Core API with endpoints listed in Section 10
- PostgreSQL with pgvector for entity + note embeddings
- Encrypted filesystem artifact storage (provider-native encryption)
- **Local saga orchestration in Avalonia** (queues captures, fetches cloud context, runs local LLM, pushes results)
- Processor router (config-driven), local-vs-cloud step tagging
- Processors: voice memo (Parakeet + local LLM note), image (Tesseract local OR cloud GPU vision-LLM), PDF, URL (web/YouTube/PDF URLs), text artifact, generic file
- LLM auto-routing with project auto-creation + Inbox-until-N=10 cold-start handling
- LLM entity extraction with cloud-fetched vector-retrieved dedup context
- LLM wikilink generation (cloud-fetched similar notes via vector retrieval)
- GPU mode configuration: on/off toggle + cloud model-size selection (8B / 14B / 35B) + batching thresholds (N tasks, T ceiling)
- "Process now" manual override for queued GPU-eligible tasks
- Cloud-side vector retrieval API (for local LLM context fetch)
- Entity hub maintenance with N=3 trigger and Context + Generated Views structure
- Markdown vault generator with `## System Output` / `## User Notes` header convention
- Tombstones for user deletions of LLM content
- B4c + system prompt mitigation: LLM-aware regen of user edits (GPU-routed)
- Provenance JSON with full observability schema (Section 21)
- Sync engine in Avalonia (saga, local-wins, tombstones, edit-diff propagation)
- Caddy reverse proxy with dynamic config (minimal in Tier 1; full HTTPS in Tier 2)
- Magic-link sharing (Tier 2 only): link surfaced to user in Avalonia; user shares via own channel; revocation from Avalonia
- User-cloud-account password recovery via 8 one-time recovery codes (parallel to passphrase recovery codes)
- SSH tunneling via SSH.NET in Avalonia for Tier 1
- Docker Compose orchestration
- Terraform for Hetzner + DigitalOcean (encrypted volumes, GPU on-demand, Wireguard tunnel)

Defer (see §25).

## 25. Open Items and Future Work

- **Mobile native capture** (iOS Shortcut + Android stub app + share-sheet integration): dropped from MVP. Adds ~1–2 weeks of Android dev surface + cross-platform testing. Desktop-only MVP carries the full 5-piece thesis story.
- **Snapshot / restore export-import flow** (B6b alternative): before destroy, optionally export full vault as a portable zip archive; restore on new cloud via uploading the archive. More explicit ceremony than the re-seed-from-local approach in MVP. Useful for offline transfer to a friend or different cloud account.
- **Multi-device LLM coordination**: when user has Avalonia on two devices, decide which one processes which artifact. Sync engine needs idempotency + designated-processor rotation. Embeddings sync alongside notes. Defer until multi-device is in scope.
- **Project explosion mitigation**: instrument creation events now; mitigate post-experiment
- **Entity-type explosion**: same — instrument and revisit
- **Privacy detection in pre-filter**: face detection, sensitive-text regex on screenshots — deferred from MVP per simplification
- **Fine-tuning**: 4B model fine-tuned for routing could match larger-model quality at lower inference cost; potentially its own thesis pivot
- **Vault querying**: vector index + LLM Q&A endpoint ("ask my vault") — significant scope, separate effort
- **Mobile native viewing**: Obsidian Mobile + cloud sync investigation
- **Video file processing**: keyframe extraction + audio extraction + per-frame vision (MVP only handles video URLs via transcript)
- **Touchpad gesture capture**: platform-specific (macOS Force Touch, Windows precision touchpad)
- **Twitter/X URL support**: authentication complexity, deferred
- **Telegram channel/message capture**: deferred
- **Selected-text via OS-native APIs** (not just clipboard): platform-specific
- **Auto-DNS cleanup on destroy**: store DNS provider API token, auto-clean
- **CRDT-based concurrent multi-device editing**
- **Real-time collaboration on shared projects**
- **Pure-.NET ONNX integration of Parakeet** (Path B — avoid C++ sherpa-onnx dependency)
- **Rust-sidecar Parakeet integration** (Path C — parakeet-rs subprocess)
- **Encrypted-at-rest with user-held keys** (current: provider-native encryption)
- **Threat-model deepening**: insider, side-channel, server compromise
- **Formal evaluation chapters with concrete metrics and corpora**
- **Compliance frameworks**: HIPAA, GDPR data export rights

---

## Appendix A: Cold-read locks (2026-05-12 second-pass grilling)

These items closed during the cold-read grilling phase, in order:

- **A2** — persona = continuous capturer (no UI implication); auto-routing as only routing mechanism; Zettelkasten emphasis (links + tags primary, folders scaffolding); toast preview pattern; manual user-created folders are equal-status routing targets; ambient explicitly defined as on-demand capture (not Windows Recall)
- **A3** — Markdown headers (`## System Output` / `## User Notes`) used uniformly across artifact notes AND entity hubs
- **A4** — image route generalized (handles screenshots, photos, handwritten notes via vision-LLM); URL processor added (web pages via Readability, YouTube via yt-dlp, PDF URLs, Markdown output); selected-text hotkey added; annotated capture via expanded preview with hold-space voice + text annotation; video URLs in MVP, video files future
- **B1** — LLM-driven entity dedup with vector-retrieved top-K context + manual merge fallback via Obsidian
- **B2** — local dictation creates standalone text artifact (B2d); Parakeet-tdt 0.6B v3 ONNX as ASR (now local-only)
- **B4** — user EDITS to LLM content (B4c + system-prompt mitigation): LLM overrides accepted; regen prompt includes diff + system-prompt instruction to respect user edits; regen routes to GPU model when GPU mode enabled
- **Tier 3 batch** — C1 (contribution reframed), C2 (provider-native encryption required), C3 (obsolete after email drop), D1 (Inbox-until-N=10), D2 (vault querying deferred), D3 (CPU routing quality accepted), E (Obsidian-compatible in expanded title)
- **B6** — GPU spin-up smart batching (N=5 threshold / T=1hr ceiling, both configurable); GPU mode opt-in; configurable GPU model size; Wireguard tunnel; manual "process now" override
- **Mobile drop** — mobile native capture dropped from MVP; 5-piece thesis story holds without it
- **B5 / email drop** — no email service in stack; sharing links surfaced to user via Avalonia; user shares via own channel
- **Option C: local-first LLM** — 4B model runs on user's PC via LLamaSharp; cloud is thin coordinator + vector index + opt-in GPU; Tier 1 baseline drops to ~€5/month; privacy story strengthens (LLM never executes against user data on cloud)

## Appendix B: Earlier locks (pre-cold-read grilling)

These items closed in the first design pass before the cold-read:

- **Domain pivot**: medical → personal memory management with multimodality emphasis (research persona, ambient capture)
- **Deployment**: tiered single-tenant per cloud — Tier 1 SSH-only (no domain, no sharing, no mobile), Tier 2 add-domain (sharing + mobile unlocked)
- **Avalonia is admin client** — provisioning wizard, destroy with confirmation, lifecycle management
- **Passphrase-protected credentials** — Argon2id KDF + AES-GCM in encrypted local file (not OS keychain); 8 one-time recovery codes
- **Two-level auth** — cloud-provider token (admin) is independent from user-cloud account login (email + password)
- **Sharing**: magic link via email (Postmark), 24h default expiry, revoke-self link in email
- **Offline editing**: saga pattern with local-wins for user regions + cloud-regen for derived content
- **Tombstones** for user deletions of LLM-generated content
- **LLM strategy**: Qwen 3.6-35B-A3B primary (multimodal, on-demand GPU); Gemma 4 E4B or Qwen 3.5-4B on CPU always-on; pluggable backend
- **Vision processing**: handled by Qwen 3.6 multimodal when GPU available; OCR-only fallback (Tesseract/PaddleOCR) otherwise

---

## Items still to grill — all closed 2026-05-12

- ~~**B3**~~ — Android share-sheet: closed by dropping mobile from MVP (see §25 future work)
- ~~**B5**~~ — email service: closed by dropping email entirely; sharing links surfaced to user via Avalonia, user shares through their own channel
- ~~**B6**~~ — GPU spin-up: locked as opt-in mode with hybrid batching (N=5 threshold / T=1hr ceiling), configurable GPU model size (8B/14B/35B), Wireguard tunnel, manual "process now" override
- ~~**C1**~~ — contribution reframed in §6 #7 as "hybrid CPU + on-demand GPU deployment pattern for thesis-budget self-hosted LLM"
- ~~**C2**~~ — filesystem encryption required, provider-native (Terraform flag), §20 and §22
- ~~**C3**~~ — email compromise obsolete after dropping email; replaced by "channel compromise" framing in §20
- ~~**D1**~~ — Inbox-until-N=10 cold-start handling, §14
- ~~**D2**~~ — vault querying deferred to future work, §25
- ~~**D3**~~ — 4B-on-CPU routing quality accepted as Tier 1 trade-off, §12
- ~~**E**~~ — expanded title form mentions Obsidian-compatibility, §2

**All cold-read items closed. Plan is final.**
