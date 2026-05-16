# Consolidated Project Plan (Post-Second-Grilling)

Date: 2026-05-13
Status: Third-pass revision after the 2026-05-13 grilling session. All locks from Q1–Q16 (Q11 skipped) folded into the body. Appendices A and B preserve earlier locks; Appendix C captures this session's locks with brief reasoning.

**Supersedes** `plans/consolidated-plan-2026-05-12.md`, which is preserved unchanged for historical reference.

## 1. Final Thesis Direction

The project is a personal-cloud service plus ambient-capture desktop client that aggregates a user's heterogeneous personal artifacts (voice memos, screenshots, photos, PDFs, handwritten notes, URLs, selected text, dictation, drag-dropped files) into an Obsidian-compatible Markdown vault with emergent graph structure via dense wikilinks and tags. Desktop-only in MVP; mobile capture is future work.

Each user provisions their own cloud instance on their chosen provider (DigitalOcean or Hetzner). Default deployment is **single-tenant per cloud** (Tier 1, SSH-only, no public domain required); user can opt into Tier 2 by adding a domain, which unlocks per-project sharing via magic links surfaced in Avalonia.

Core transformation — **local-first; the cloud holds zero semantic state**:

```text
ambient desktop capture (button-driven main window; hotkey accelerator on Win/Mac/X11)
  -> local pre-filter (silence/blur/dup; classical signal processing)
  -> toast preview (fire-and-forget; expandable for voice/text annotation)
  -> local async saga queue (LIFO; orchestrated by Avalonia)
  -> local LLM processing on user's CPU
       - one structured call: routing + entity extraction + wikilink anchor selection (JSON output)
       - vector retrieval against LOCAL sqlite-vec (no cloud round-trip)
       - 4B-class model via LLamaSharp (default: Gemma 4 E2B Q4 on 8 GB RAM systems)
  -> procedural note assembly (frontmatter + ## System Output with inline wikilinks + ## User Notes)
  -> per-affected entity hub regen (only when hub crossed N=3 mention threshold)
  -> push to cloud as backup + sync transport (artifact + note + provenance JSON)
  -> cloud propagates to other devices (no LLM, no embedding regeneration on cloud)
  -> other devices re-embed pulled notes locally into their own sqlite-vec
  -> Tier 2 only: per-project sharing via magic link surfaced in Avalonia
  -> Cloud GPU opt-in for vision + complex synthesis + user-edit-aware hub regen (smart batched, cost-capped)
```

## 2. Working Title

Primary (user-stated):

> **Cloud Service for Personal Data Aggregation and Organization in Markdown Format**

Expanded technical form:

> **A Personal Cloud Service with Ambient-Capture Desktop Client for Multimodal Data Aggregation and Provenance-Aware Obsidian-Compatible Markdown Vault Generation with Emergent Knowledge-Graph Structure**

## 3. Problem Statement

Personal heterogeneous data accumulates across devices and is hard to find, organize, and share without losing context. Existing tools (Obsidian, Notion, Mem.ai, Reflect) are excellent editors but require manual ingestion and organization. URL bookmarks rot. Voice memos pile up untranscribed. Screenshots lose context.

This project addresses these problems by:

- Low-friction multimodal capture entry points (button-driven from a unified main-window surface; hotkey accelerator on supported OSes) that turn capture into a fire-and-forget action
- Automatic processing through modality-specific routes
- LLM-driven auto-routing into projects with project auto-creation
- LLM-driven entity extraction populated into user-defined entity-type folders
- LLM-driven dense wikilink generation, producing an emergent knowledge graph
- URL snapshotting as a link-rot mitigation
- Tiered deployment so users can start fully private (no domain, no external dependencies) and opt into more features as needed

## 4. What The System Is / Is Not

It is:

- A personal-cloud service (**single-tenant per cloud instance — no multi-tenant code path**)
- An ambient-capture desktop client (Avalonia 11 on .NET 10; cross-OS Windows/macOS/Linux in MVP)
- A processor pipeline with LLM auto-routing into emergent projects
- An Obsidian-compatible Markdown vault generator
- A provenance-aware system with observability built in
- A per-project sharing system via magic link surfaced in Avalonia (Tier 2)
- A knowledge-graph generator via wikilinks and tags into user-defined entity-type folders

It is not:

- A Markdown editor (Obsidian is)
- A multi-tenant SaaS (each user provisions their own cloud)
- A multi-user collaborative editing tool (read-only sharing only)
- A vector-search question-answering system (deferred to future work)
- A chatbot for the vault (deferred)
- A continuous-recording surveillance tool (ambient = on-demand, not Windows Recall)
- A clinical / medical system (pivoted away from)
- A frontier-cloud-LLM-only system (self-hosted local-first by default)

## 5. Persona

**Live researcher**: a continuous capturer across long-running concurrent research threads. The user does not actively maintain or focus on project organization. Capture is fire-and-forget; the system auto-routes and auto-organizes. The user reads the resulting Markdown vault in Obsidian, where the graph view reveals connections via wikilinks. Optionally shares per-project subsets with coauthors / advisor (Tier 2 only). **Capture cadence: ~20 artifacts/day during active research periods.**

## 6. Main Contribution

The novelty is the combination of:

1. **Low-friction multimodal capture from a unified surface**, with hotkey accelerator on Win/Mac/X11 and graceful main-window fallback on Wayland; unified expanded-preview annotation pattern across modalities
2. **LLM auto-routing** into emergent projects (auto-created when no existing project fits); user-defined entity-type folders fill an open-ended taxonomy via prompt discipline rather than LLM type-invention
3. **Emergent knowledge graph** via dense LLM-generated wikilink anchors inserted into otherwise-procedural artifact notes; entity hubs (LLM-written Generated Views) cut across many artifacts
4. **URL snapshotting** as link-rot mitigation (web/YouTube/PDF URL routes)
5. **Local-wins offline editing with local-LLM hub regen** — saga-orchestrated; cloud is a pure transport for sync, no LLM execution on the cloud in default Tier 1
6. **Tiered deployment** — Tier 1 zero-external-dependency private mode (SSH-only, no domain, no public CA); Tier 2 opt-in via domain (unlocks per-project sharing)
7. **True local-first LLM processing** — sqlite-vec vector index + multilingual-e5-small embedding model + Gemma-4-class LLM all on the user's PC. Cloud holds only artifacts/notes/provenance for sync and backup. Tier 1 ~€5/month — semantic processing never executes against user data on the cloud.
8. **Cross-OS desktop in MVP** — Avalonia + .NET 10; native hotkey integration on Win/Mac/X11 via SharpHook; main-window-driven capture as the cross-OS-uniform baseline

Strongest claim:

> The system turns ambient personal captures into an Obsidian-compatible Markdown vault with an emergent knowledge graph through fully local-first LLM processing, with the cloud acting purely as a sync/backup/sharing transport. Tier 1 deployment costs ~€5/month, holds zero semantic state on the cloud, and keeps LLM analysis on the user's own machine.

## 7. End-To-End Architecture

```text
Avalonia Desktop Client (.NET 10; Windows + macOS + Linux in MVP)
  - 6 capture surfaces (voice memo, screenshot region-select, selected-text, URL, drag-drop, dictation)
       button-driven from main window;
       hotkey accelerator on Win/Mac/X11 via SharpHook
  - toast preview + expand-to-annotate
  - local Parakeet ASR (direct Microsoft.ML.OnnxRuntime, pure .NET)
  - local D filter (silence VAD, blur Laplacian, near-dup pHash)
  - LOCAL LLM via LLamaSharp (default Gemma 4 E2B Q4; configurable to E4B / 8B)
  - LOCAL embedding model (multilingual-e5-small ONNX)
  - LOCAL vector index (sqlite-vec, loaded as extension)
  - local async saga orchestration (LIFO queue, retry, idempotency)
  - offline edit queue + sync engine (file watcher on Obsidian vault folder)
  - admin: provision / destroy / passphrase / 8 dual-use recovery codes
        |
        |  Tier 1: SSH tunnel
        |  Tier 2 (after domain added): HTTPS via Caddy + Let's Encrypt for sharing endpoints only;
        |                               Avalonia continues using SSH for its own traffic
        v
    Cloud API (ASP.NET Core 10, single-tenant) — thin transport
        |
        |  storage + sync + share-zip generation + GPU orchestration only
        |  NO LLM execution. NO embedding generation. NO vector index. NO pgvector.
        v
    Cloud Services
        - Artifact storage (filesystem-backed on encrypted VPS volume)
        - PostgreSQL 16 (account auth, project list metadata, share tokens, GPU cost tracking)
        - Sync API (push/pull endpoints)
        - Share-zip generation (W1+P1+F1+A1 stripping per §19)
        - GPU spin-up orchestration (opt-in, smart batched, cost-capped)
        |
        |  results from local LLM arrive: artifact + processed note + provenance JSON
        v
    Hierarchical Vault Store (per cloud instance)
        - Inbox/        (cold-start, uncategorized)
        - Projects/     (LLM- or user-created)
        - _Entities/    (USER-DEFINED type folders, hubs LLM-maintained inside)
        - .system/, .provenance/
        |
        v
    Sync Engine (Avalonia, saga-based)
        - file watcher on local Obsidian vault
        - local-wins for ## User Notes
        - hub regen with user-edit-aware prompt for ## System Output
        - tombstones for user deletions
        - cross-device: device-of-capture processes; other devices re-embed on pull
        - same-note user-edit conflicts: last-write-wins + .conflict-<timestamp>.md file
        |
        v
    Per-Project Sharing (Tier 2)
        - magic link surfaced in Avalonia (user shares via own channel)
        - 24h default expiry
        - revocable from Avalonia dashboard
        - read-only zip download (cross-vault wikilinks stripped, provenance stripped,
          frontmatter whitelisted, assets bundled)
```

## 8. Desktop Client (Avalonia)

Technology stack (locked):

- **Avalonia 11.2+ UI**, **.NET 10**
- **Parakeet-tdt 0.6B v3 ONNX** via **direct Microsoft.ML.OnnxRuntime** (pure .NET; sherpa-onnx path B not chosen)
- **LLamaSharp** (llama.cpp .NET wrapper) hosting the local LLM
- **multilingual-e5-small ONNX** via Microsoft.ML.OnnxRuntime for embeddings
- **sqlite-vec** (loaded via Microsoft.Data.Sqlite extension mechanism) for local vector index
- **PortAudio** via PortAudioSharp for cross-OS microphone capture
- **SharpHook** (libuiohook wrapper) for cross-OS global hotkeys
- **SSH.NET** for Tier 1 SSH-tunneled communication
- **HttpClient** for Tier 2 HTTPS
- **Argon2id** via Konscious.Security.Cryptography + **AES-GCM** via System.Security.Cryptography for encrypted credentials vault
- **Vertical slice architecture** — `Features/<Slice>/` owns UI + logic + per-feature persistence; `Infrastructure/` houses cross-cutting deps

Responsibilities:

- Capture surfaces (§9)
- Local pre-filter (silence/blur/dup, classical signal processing)
- Local Parakeet ASR (dictation + voice memo transcription)
- **Local LLM inference** via LLamaSharp: routing + entity extraction + wikilink anchor selection (one structured call per artifact); plus per-affected entity hub regen
- **Local vector index** (sqlite-vec) + **local embedding model** for similarity retrieval — fully local, no cloud round-trip
- **Local saga orchestration**: LIFO queue, retries, idempotency, persistent state in local SQLite
- Toast notification preview with optional expand-to-annotate
- Offline edit queueing (works fully offline)
- File watcher on local Obsidian vault folder (.NET FileSystemWatcher + polling fallback for macOS quirks)
- Sync engine (push to cloud as backup transport; pull on bootstrap + on receive; re-embed pulled notes locally per B1)
- System tray UI where available (Windows / macOS / KDE / XFCE / Cinnamon); main-window-only fallback on GNOME-Wayland
- Main app view (capture buttons + URL/text inputs + drop zone + recent captures + processing queue + dropped-log review + settings)
- Cloud lifecycle admin: provisioning wizard, destroy (passphrase-confirmed), Tier 1 → Tier 2 upgrade
- Passphrase-derived AES-GCM encrypted credentials file with **8 dual-use recovery codes** (each code can both decrypt the local vault AND reset the cloud account password)
- **Auto-detect system RAM** at first run; recommend local model tier
- **Two-path storage configuration**: Vault path (Obsidian-facing, portable, backup-able) + App Support path (machine-local internals, not for backup); OS-appropriate defaults, change-with-migration support

Not responsible for:

- Multimodal vision processing (cloud GPU only, when GPU mode enabled)
- Multi-device sync coordination as a special case — under the locked multi-device-MVP semantics, each device acts independently with cloud as transport
- Markdown editing (Obsidian is)

## 9. Capture Surfaces

Six desktop entry points, **button-driven from main window as the cross-OS-uniform baseline; hotkey accelerator on Win/Mac/X11 (SharpHook); main-window fallback only on Wayland**:

1. **Voice memo** — main-window "🎙 Record" button (or `Ctrl+Shift+R` on supported OSes); captures audio, local Parakeet transcribes, local LLM processes; results pushed to cloud
2. **Screenshot** — main-window "📷 Screenshot" button (or `Ctrl+Shift+S` on supported OSes); transparent overlay window appears, user drags region rectangle, capture happens; on Wayland uses xdg-desktop-portal Screenshot (one prompt per shot, acceptable since button-driven)
3. **URL capture** — paste into main-window URL field, or drag URL to tray icon (where tray exists); fetched and processed via URL processor route
4. **Drag-and-drop file** — drop onto main-window drop zone, or onto tray icon (where tray exists); any file type
5. **Selected text** — paste into main-window text field (cross-OS-uniform); hotkey-with-active-window-title capture deferred to §25 future work
6. **Dictation** — main-window "Start dictation" button starts streaming Parakeet transcription into a visible text area; user clicks Stop when done; transcript saved as text artifact

**Toast preview** appears after every capture (auto-dismiss after ~5s, configurable). Toast pattern: **U1 (fire-and-forget with persistent tray badge for queue depth)**. User can click toast to expand into a fuller view that shows:

- Captured artifact (image thumbnail, audio waveform, file name, URL preview)
- Filter status (passed / dropped, with reason)
- Microphone affordance (hold space → record ~30s voice annotation)
- Text field for optional typed annotation

Annotation attaches to the artifact uniformly across all capture types. Default flow remains fire-and-forget; annotation is opt-in by clicking the toast.

**Queue ordering: LIFO (last-in-first-out)** — most recent capture processes first. Mismatched to throughput maximization but matched to user-attention: if the user captured something 30 seconds ago and they alt-tab to Obsidian, they want THAT note, not a 5-minute-old one.

Ambient definition: on-demand capture (user triggers via button or hotkey), NOT continuous like Windows Recall.

## 10. Cloud Backend

Cloud is a **thin transport + storage** in Tier 1. **No LLM runs on cloud. No embedding generation on cloud. No vector index on cloud. No pgvector.** All semantic processing happens on the user's PC.

Technology (locked):

- ASP.NET Core on .NET 10 (Minimal API style)
- PostgreSQL 16 (single-tenant schema; no `tenant_id` discriminators)
- Filesystem-backed artifact storage on VPS volume; provider-native encryption
- Caddy reverse proxy with dynamic config (minimal in Tier 1, full HTTPS in Tier 2)
- Docker Compose for service orchestration
- Terraform for provisioning (Hetzner + DigitalOcean)
- **No email service** — sharing links surfaced to user in Avalonia, shared via user's own channel
- **No LLM containers in Tier 1** — cloud baseline is API + Postgres + Caddy + storage only
- Vertical slice layout for the API (`Features/Auth/`, `Features/Sync/`, `Features/Sharing/`, `Features/Gpu/`, `Features/TierUpgrade/`)

Tiered exposure:

- **Tier 1**: API binds to `localhost:8080`, reachable only via SSH tunnel from Avalonia
- **Tier 2**: Caddy reconfigured with user-supplied domain, Let's Encrypt cert auto-provisioned, public HTTPS for sharing endpoints (recipient browser access)

Responsibilities:

- User-cloud account auth (per-user email/password, argon2id, 8 dual-use recovery codes — no email reset)
- Artifact upload + storage
- Project + share-token registries
- **Sync API** — receives processed artifacts + notes + provenance from local saga; serves them to other devices on pull
- Magic-link share management (Tier 2): generates tokenized URLs, generates the share zip with W1+P1+F1+A1 stripping at link-creation time (cached blob), handles revocation
- Dynamic Caddy reconfiguration on Tier 1 → Tier 2 upgrade
- **GPU spin-up orchestration** (when GPU mode enabled): Terraform-managed GEX instance lifecycle, Wireguard tunnel, smart batching, monthly cost cap enforcement, 2h single-instance lifetime, concurrency=1

What cloud does NOT do in Tier 1:

- Run any LLM (Tier 1 has no Ollama, no model containers, no GPU)
- Run any embedding model
- Hold any vector index
- Process Markdown / generate notes (local)
- Extract entities or generate wikilinks (local)
- Make routing decisions (local)
- Fetch vector-retrieved context for the local LLM (impossible — no cloud-side embeddings)

Candidate endpoints:

```text
POST   /auth/register
POST   /auth/login
POST   /auth/reset-password           (via dual-use recovery code)
POST   /artifacts/upload
GET    /sync/pull                     (notes + provenance + project list)
POST   /sync/push                     (artifacts + notes + provenance + delete tombstones)
GET    /projects
POST   /shares                        (Tier 2: creates magic link + cached zip)
GET    /shared/{token}                (Tier 2: recipient endpoint, serves cached zip)
POST   /shares/{id}/revoke            (Tier 2: invalidates token + deletes cached zip)
POST   /admin/upgrade-tier-2          (Avalonia signals on domain add)
GET    /admin/gpu-status              (current month spend + cap + active instance state)
POST   /admin/gpu-process-now         (manual override for batched queue)
```

**No `/admin/regenerate/{note_id}`** — regen runs on the observing device per D1, not on cloud.

**No `/sync/vector-retrieve`** — local sqlite-vec is the only vector store; no cloud-side retrieval.

## 11. Processor Router and Saga (S4)

Saga is **orchestrated locally by Avalonia, not cloud**. Under S4, each artifact passes through **one structured LLM call** (routing + entity extraction + wikilink anchor selection) plus **per-affected entity-hub regen calls** (only when a hub crossed the N=3 mention threshold or already exists). No `llm_artifact_note_generator` — artifact notes are assembled procedurally from the raw extracted content (transcript / OCR / Readability output) with wikilink anchors inserted at LLM-identified positions.

Routes:

```yaml
routes:
  voice_memo_audio:
    processors:
      - audio_metadata_extractor              # local
      - parakeet_transcription                # local (Microsoft.ML.OnnxRuntime)
      - local_context_fetch                   # local (sqlite-vec: top-K similar entities/notes)
      - llm_structured_extraction             # local (LLamaSharp): routing + entities + wikilink anchors
      - procedural_note_assembly              # local (no LLM): build frontmatter + inline transcript w/ anchors
      - per_hub_regen                         # local (LLamaSharp): per affected entity hub Generated Views
      - local_drop_filter                     # local: silence VAD
      - push_to_cloud                         # transport: upload artifact + note + provenance
      - provenance_writer                     # local

  image:  # screenshots, photos, handwritten notes, scanned docs
    processors:
      - image_metadata_extractor              # local
      - vision_processor:
          gpu_path:  qwen_3.6_multimodal      # cloud-gpu (opt-in, batched)
          cpu_path:  tesseract_local          # local OCR fallback when GPU mode off
      - local_drop_filter                     # local: blur Laplacian + perceptual hash
      - local_context_fetch                   # local
      - llm_structured_extraction             # local
      - procedural_note_assembly              # local: embed image + OCR text w/ anchors
      - per_hub_regen                         # local
      - push_to_cloud                         # transport
      - provenance_writer                     # local

  pdf_document:
    processors:
      - pdf_text_extractor                    # local
      - local_context_fetch                   # local
      - llm_structured_extraction             # local
      - procedural_note_assembly              # local
      - per_hub_regen                         # local
      - push_to_cloud                         # transport
      - provenance_writer                     # local

  url_capture:
    processors:
      - url_type_detector                     # local
      - per_type_fetcher:
          web_article:  readability_extractor -> llm_markdown_transform     # local
          youtube:      yt_dlp_transcript -> markdown                       # local
          pdf_url:      download -> delegate_to_pdf_document
          github_repo:  readme_fetcher + metadata                            # local
      - local_context_fetch                   # local
      - llm_structured_extraction             # local
      - procedural_note_assembly              # local
      - per_hub_regen                         # local
      - push_to_cloud                         # transport
      - provenance_writer                     # local

  text_artifact:  # selected-text, dictation output, drag-dropped text files
    processors:
      - text_metadata_extractor               # local
      - local_context_fetch                   # local
      - llm_structured_extraction             # local
      - procedural_note_assembly              # local
      - per_hub_regen                         # local
      - push_to_cloud                         # transport
      - provenance_writer                     # local

  generic_file:
    processors:
      - file_metadata_extractor               # local
      - mime_classifier                       # local
      - text_fallback_processor               # local (if textual content extractable)
      - push_to_cloud                         # transport
      - provenance_writer                     # local
```

Saga orchestration (lives entirely in Avalonia, persistent state in local SQLite):

```text
capture received locally
  -> persist raw artifact + initial provenance to local queue (LIFO)
  -> warm-up step (if device offline > N minutes): pull latest project list + recent notes from cloud
  -> step 1: modality detection (local, synchronous)
  -> step 2: extraction processors (local, parallel where possible)
  -> step 3: local D filter (silence/blur/dup; drop → drop-log, no further processing)
  -> step 4: local context fetch (sqlite-vec: top-K similar entities, top-K similar notes, project list)
  -> step 5: ONE structured LLM call (routing + entities + wikilink anchors as JSON output)
  -> step 6: procedural note assembly (no LLM): frontmatter, body w/ inline wikilink anchors, ## User Notes empty
  -> step 7: per-affected-hub regen (LLM call per hub crossing N=3 or already existing with new mention)
  -> step 8: push to cloud as backup + sync transport
  -> step 9: GPU-eligible tasks (vision, complex hub synthesis) deferred to cloud GPU batch (when GPU mode on)
  -> compensating actions on failure at any step (retry, rollback local queue state)
```

## 12. LLM Strategy (per-device backend, GPU as orthogonal upgrade)

**Backend choice is per-device, not per-vault** (locked C2). Each Avalonia instance picks its primary backend independently. GPU mode is per-vault (because GPU provisioning is shared cloud state).

### Primary backend options (per-device)

**L — Local (default, recommended)**
- LLamaSharp on user's PC
- Free (already-owned CPU)
- Private (LLM analysis never leaves user's machine)
- Default: **Gemma 4 E2B Q4** on 8 GB RAM systems
- Auto-detect RAM at first run; recommend tier

**CC — Cloud CPU**
- Always-on Ollama + 4B model container in user's own cloud
- Adds ~€10/month to Tier 1 baseline (~€15/month total)
- Data stays in user's own cloud (single-tenant, private)
- Works on any hardware — no local LLM required

**API — External LLM provider** (with security flag)
- OpenAI, Anthropic, or similar — user picks provider at setup
- API key stored encrypted in passphrase vault
- Cheapest per-artifact for low volume; frontier-model quality
- **Data leaves the user's cloud to the provider** — full-screen consent at enable
- Persistent settings badge once enabled
- Per-call audit log entry (provider + timestamp + prompt hash)

Two devices on the same vault may pick different backends (e.g., powerful desktop on L, weak laptop on CC). Provenance JSON records `primary_backend` per call, so eval can be backend-stratified.

### Local model tiers (when primary backend = L)

| Tier | Model | Active RAM (Q4) | Min system RAM | Disk | Notes |
|---|---|---|---|---|---|
| **Default (low threshold)** | **Gemma 4 E2B Q4** | ~1.5 GB | **8 GB** | ~1.7 GB | Routing/extraction competent; auto-recommended at first run |
| Step-up | Gemma 4 E4B Q4 | ~3–4 GB | 12 GB | ~2.8 GB | Recommended for primary daily use; better quality |
| Power | Qwen 3.5-8B Q4 | ~5–6 GB | 16 GB | ~5 GB | Best local quality, higher latency |

Apple Silicon (M-series): unified memory + Metal acceleration; treat as one tier up. 8 GB M-series ≈ 12 GB x86 for this workload; E4B is reasonable on 8 GB Apple Silicon.

**Lazy-load strategy:** models loaded into RAM only during inference, unloaded after configurable idle timeout (default 60s). Idle baseline ~250 MB; peak ~1.7 GB on E2B. Acceptable on 8 GB machines.

Handles: routing, entity extraction (with locally-retrieved dedup context), wikilink anchor selection, entity hub Generated Views regen.

**ASR model (local, always-on):** Parakeet-tdt 0.6B v3 ONNX — Ukrainian + 25 European languages, ~1 GB active during transcription. Same model serves dictation (real-time feedback) and voice memo transcription. Via direct Microsoft.ML.OnnxRuntime (pure .NET).

**Embedding model (local, always-on):** multilingual-e5-small ONNX — ~150 MB active, 384-dim, 100+ languages. Via Microsoft.ML.OnnxRuntime. Kept loaded permanently (small enough).

### GPU mode (opt-in, cloud, default OFF)

User picks GPU model in settings:

| Tier | Model | GPU memory | Best for |
|---|---|---|---|
| Light | Qwen 3.5-8B Q4 | ~6 GB | Cost-conscious; small GEX instance |
| Mid | Qwen 3.5-14B Q4 | ~10 GB | Balanced |
| **Frontier** | **Qwen 3.6-35B-A3B** (MoE, ~3B active per token) | ~24 GB | **Default if GPU enabled**; natively multimodal — handles vision in one model |
| Alt | Gemma 4 31B Dense | ~20 GB | Apache 2.0 alternative; needs separate vision model |

Used for: vision processing (images), complex synthesis (long-form summarization), user-edit-aware hub regen quality upgrade. Smart-batched per §22.

**GPU mode is a quality upgrade, not a feature gate.** Everything functions on the local backend by default. Enabling GPU mode escalates specific tasks for higher fidelity. Disabling GPU mode degrades quality but loses no features.

**Vision fallback when GPU mode off:** local Tesseract / PaddleOCR for text-in-image; EXIF + perceptual hash for photo similarity. Acceptable baseline; degraded vs vision-LLM but functional.

### Cost matrix (per-device backend × per-vault GPU)

| Primary backend (per device) | GPU upgrade (per vault) | Total |
|---|---|---|
| L (Local) | Off | ~€5/month (Tier 1 baseline) |
| L | On | ~€5 + capped GPU (default cap €30/month) |
| CC (Cloud CPU) | Off | ~€15/month |
| CC | On | ~€15 + capped GPU |
| API (External) | Off | ~€5/month + provider API costs |
| API | On | ~€5/month + provider costs + capped GPU |

Privacy stance varies by backend:
- L: strongest — LLM never executes against user data outside the user's PC
- CC: strong — LLM runs in user's own cloud; data never reaches third parties
- API: weakest — data sent to provider; explicit consent required (security flag)

GPU upgrade (any primary backend): vision/synthesis/regen route to cloud GPU batch. Cloud GPU runs in user's own cloud (Hetzner GEX provisioned by their Terraform), so no third-party exposure.

Inference engines:

- **Local LLM**: LLamaSharp (llama.cpp .NET wrapper)
- **Local ASR**: direct Microsoft.ML.OnnxRuntime
- **Local embedding**: Microsoft.ML.OnnxRuntime
- **Cloud GPU**: Ollama in the spun-up GEX instance

Fine-tuning: future work. Task-specific fine-tune of a 4B model for routing could be a thesis extension.

## 13. Local Subsystems (Avalonia)

**Five local subsystems on the user's PC:**

**A. Parakeet-tdt 0.6B v3 ONNX for dictation + voice memo transcription**

- Real-time transcription buffer during dictation (instant user feedback)
- Same model handles voice memo artifacts (transcript created locally)
- Multilingual: Ukrainian + 25 European languages, auto-detected
- ~1 GB active RAM during inference; ~600 MB disk
- Integration: **direct Microsoft.ML.OnnxRuntime** with C# audio DSP port (mel spectrograms, TDT decoding) — ~2-3 week implementation effort, pure .NET
- Lazy-loaded; unloaded after 60s idle

**B. Local LLM via LLamaSharp**

- Handles: routing + entity extraction + wikilink anchor selection (one structured JSON call); plus per-affected hub regen
- Default model: **Gemma 4 E2B Q4** (~1.5 GB active, ~1.7 GB disk)
- Configurable tier: E2B / E4B / Qwen 3.5-8B (auto-recommended by RAM detection)
- LLamaSharp = llama.cpp .NET wrapper; pure .NET; cross-platform
- Inference latency target: ~15-40s per structured call on modern laptop CPU
- Models downloaded at first-run (non-blocking; user can start capturing once Parakeet ready)
- Lazy-loaded; unloaded after 60s idle
- Settings UI shows: "Your system has X GB RAM. Recommended local model: Y." User can override.

**C. Classical quality / duplicate filter (D filter)**

- Silent audio: energy threshold / VAD
- Blurry image: Laplacian variance threshold
- Near-duplicate screenshots: perceptual hash (dHash / pHash)
- Implementation: classical signal processing in C#, no ML model
- Privacy detection: deferred to future work
- Dropped artifacts → local drop-log queue surfaced in main app for review

**D. Local vector index + embedding model**

- Vector store: **sqlite-vec** (SQLite extension, loaded via Microsoft.Data.Sqlite) — replaces deprecated sqlite-vss
- Embedding model: **multilingual-e5-small** ONNX (~120 MB on disk, ~150 MB active)
- Stores entity embeddings + note embeddings for similarity retrieval
- Queried by the local LLM during entity dedup, wikilink anchor selection, and project routing — all local
- Lives under the App Support path (rebuildable from notes if corrupted; not part of backup)
- Re-index pass on first run (or after model swap) to populate from existing vault content
- Fallback if sqlite-vec extension loading fails on a target OS: brute-force cosine in-process (sub-100ms at projected 7-10k notes/year scale)

**E. Local saga state**

- SQLite for saga queue persistence, drop-log, settings
- Allows resume after Avalonia restart / crash
- Cross-references provenance JSON entries by artifact_id

**RAM requirements summary** (with lazy-load):

| RAM | Recommended local model | Idle / peak footprint |
|-----|---|---|
| 16 GB+ | E4B (default) or 8B | ~250 MB idle / ~4 GB peak (8B) |
| 8 GB | E2B | ~250 MB idle / ~1.7 GB peak |
| <8 GB | Skip local LLM | Use Cloud CPU (CC) backend instead |

Apple Silicon: one tier up due to unified memory.

## 14. Auto-Routing and Project Creation

**LLM-driven routing is the only routing mechanism.** No explicit user hint surface.

The LLM picks up inline `#tag` mentions in transcribed/captured content naturally via prompt context — an emergent behavior, not a separate feature.

Routing flow (one of the outputs of the §11 step 5 structured LLM call):

```text
artifact + extracted content + locally-retrieved project list (top-K if project count is large)
  -> LLM picks existing project OR proposes new project
  -> if new: LLM generates folder name (slug-case + year-suffix conventions in prompt)
  -> routing decision + confidence + top-3 alternatives written to provenance JSON
```

**Cold-start**: first artifacts land in `Inbox/` until N=10 artifacts accumulate, at which point LLM proposes initial project groupings (user accepts/edits).

**Projects: LLM auto-creates** when no existing project fits (asymmetric with entity types — see §17 for the principled reason). Project-creation events instrumented for testing-phase analysis.

**Inside-project folder structure**: fixed schema (not LLM-determined): `Artifacts/`, `Notes/`, `.provenance/`.

**User-created folders** (created directly in Obsidian by the user): equal-status routing targets. LLM sees them on next saga warm-up. No "user-managed" distinction.

## 15. Knowledge Generation

Outputs per processed artifact:

- Markdown artifact note (one per artifact) — **procedural assembly** from raw extracted content, NOT LLM-rewritten
- Updates to relevant entity hub notes (`## System Output > Generated Views` section)
- LLM-inserted `[[wikilinks]]` at LLM-identified anchor positions in the artifact note's body
- Provenance JSON record
- Evidence files (transcripts, OCR text, thumbnails, URL snapshots)

**Markdown note structure** (uniform across artifact notes and entity hubs):

```markdown
---
# YAML frontmatter, system-managed tags + metadata
---

## System Output

(LLM-inserted wikilinks within procedurally-assembled content from raw extraction)

## User Notes

(free-form user-authored content; preserved on sync; local-wins)
```

Sync behavior per region:

- **`## User Notes`** → local-wins on sync
- **`## System Output`** for **artifact notes** → does NOT auto-regenerate. Procedural assembly is run-once at creation. Manual "regenerate this note" button available in Obsidian → Avalonia round-trip if user wants a fresh pass.
- **`## System Output > Generated Views`** for **entity hubs** → regenerates when new mentions arrive (per §17, §18)
- **User edits to `## System Output`** in hub notes → accepted, but tracked: sync stores diff in provenance; next regen prompts the LLM with previous output + current local + diff + system-prompt instruction to respect user edits as meaningful input (B4c). Default: runs on the same local backend that captured the triggering artifact. When GPU mode is enabled, regen escalates to the GPU model for higher-fidelity instruction-following.
- **User deletions of LLM content (tombstones)** → tracked in `.provenance/`; LLM regenerator respects suppressions

**Wikilink generation**: as part of the §11 step 5 structured LLM call, the LLM identifies anchor positions in the raw content (e.g., "the word 'Alice' at character 142 should become `[[Alice]]`"). Procedural assembly wraps each anchor. The LLM does NOT rewrite the body; it only identifies where to insert links. This makes wikilink targets mechanically verifiable against the source.

## 16. Hierarchical Vault Design

The vault lives under the user-configurable **Vault path** (§8). Per-OS defaults: `~/Documents/<vault-name>` (macOS, Linux) or `%UserProfile%\Documents\<vault-name>` (Windows). User can change to any location, including external drives.

Per-vault structure (Obsidian-opened root):

```text
<vault>/
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
    (USER-DEFINED type folders — seeded at onboarding; system never creates new type folders)
    People/
      Alice.md
    Places/
      Reykjavik.md
    Concepts/
      Glacier_retention.md
    Papers/
    Uncategorized/
      (LLM-extracted entities that didn't match any defined type)

  .system/
    (sync metadata, configuration)

  .provenance/
    (per-artifact provenance JSON shadow tree)
```

Folders are scaffolding for browsing. The semantic structure is carried by wikilinks and tags (Zettelkasten emphasis). Project folder = shareable unit (Tier 2).

## 17. Entity Hubs

**Entity types are USER-DEFINED, not LLM-emergent.** At onboarding, vault scaffolding seeds five type folders: `_Entities/People/`, `_Entities/Places/`, `_Entities/Concepts/`, `_Entities/Papers/`, `_Entities/Uncategorized/`. User can rename / merge / add / delete via Obsidian; file watcher detects changes; LLM extraction reads the current folder list on next saga.

Extraction prompt lists current type folders verbatim and instructs the LLM to assign each entity to one of them. Entities the LLM cannot confidently classify land in `_Entities/Uncategorized/`. User periodically reviews `Uncategorized/` and either creates a new type folder (and moves entities into it) or merges into existing.

**Asymmetric with projects:** projects auto-create because the user's life is unbounded (research threads, trips, work areas); entity types form a small finite vocabulary (People/Places/Concepts/Papers/Organizations/Events/Tools/...) that converges. Forcing the LLM to invent type folders ad-hoc produced fragmentation (Person vs People vs Persons); user-defined fixes this by construction.

**Hub creation trigger**: hub note materializes when an entity accumulates **N=3 mentions** (default, tunable). Below threshold, mentions resolve to dangling Obsidian `[[wikilinks]]` (graph nodes appear unresolved).

**Entity deduplication**: LLM-driven at extraction time. When LLM extracts a candidate entity, the system retrieves the top-K most-similar existing entities (vector embedding similarity over entity surface forms in local sqlite-vec) and passes them in the extraction prompt. LLM decides "this is an alias of existing X" or "this is a new entity Y." Aliases accumulate in the hub's YAML frontmatter `aliases:` list. User can manually merge mistakes via Obsidian rename + move (sync brings the change back to cloud).

Hub note structure (uses same `## System Output` / `## User Notes` convention):

```markdown
---
type: People                            # folder name; user-defined
name: Alice
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

(derived state, regenerated when new mentions arrive — observing device runs the regen)

- TL;DR: ...
- Timeline of mentions: ...
- Related entities: ...

### Attachments
### External References

## User Notes
```

Context subsection is canonical, regenerated additively (new mentions append). Generated Views is derived — regenerated by LLM on hub-trigger events. This separation handles staleness naturally: views are always re-derivable from context.

## 18. Sync Model and Multi-Device

Offline editing is supported. Local-wins for user-authored content. Sync engine is saga-orchestrated, lives in Avalonia, watches the local Obsidian vault folder.

**Multi-device is in MVP scope** (locked M2): the user runs Avalonia on multiple machines (e.g., desktop + travel laptop), each connected to the same cloud.

### Multi-device semantics

- **Device-of-capture processes.** Whichever device the user captures on runs the full local saga (Parakeet → structured LLM call → procedural assembly → hub regen → push). The capturing device's output is canonical.
- **Receiving device re-embeds only** (locked B1). When Laptop B pulls a note authored on Laptop A, B inserts the note text into its vault and re-runs the embedding model locally on the note body. B does NOT re-run the LLM pipeline. Embeddings are deterministic (same model + same input → same vector), so both devices end up with identical sqlite-vec contents without transporting embeddings.
- **No embedding transport over cloud.** Cloud carries text + provenance only; each device computes embeddings independently from the synced text.
- **Concurrent same-note user-edit collisions:** last-write-wins on the file. The losing device drops a `<filename>.conflict-<timestamp>.md` alongside the winning file for manual reconciliation. Standard Obsidian-style idiom; no CRDT.
- **Concurrent hub regen from two devices:** both regenerate the affected hub's Generated Views independently; last push wins; conflict file dropped if both pushes diverge. Lost content is re-derivable from the canonical Context section.
- **Warm-up before processing:** if a device has been offline > N minutes (default 5), saga step 0 pulls latest project list + recent notes from cloud before running the structured LLM call. Ensures routing decisions and entity dedup happen against current vault state.

### Sync semantics per region

- `## User Notes` → local-wins; conflict file on cross-device collision
- `## System Output` of **artifact notes** → run-once at creation; no auto-regen
- `## System Output > Generated Views` of **entity hubs** → regen on new mentions; B4c user-edit-aware
- User deletions of LLM content → tombstones in provenance, respected by regen

### Saga sync flow

```text
local change detected (file watcher on Obsidian vault folder)
  -> diff step: compute diffs locally; identify user edits in ## System Output for hubs
  -> push step: send diffs + new local LLM output to cloud (as backup transport + propagation to other devices)
  -> regen step (only for hub Generated Views): when downstream dependencies trigger regen on this device:
       LOCAL backend regenerates with user-edit-aware prompt (B4c)
       when GPU mode enabled: regen task joins the GPU batch instead
       prompt includes:
         - current artifact context
         - previous system output (what LLM wrote)
         - current local version (with user edits)
         - explicit diff highlight
         - system prompt: "if user has edited the previous output, pay attention to their changes;
           treat them as meaningful input; you may still update with new info"
  -> apply step: regenerated content written back to the local hub note (Generated Views region only)
  -> compensating actions on failure at any step
```

**Provenance JSON logs** which tier ran each regen (`tier: local | gpu | cloud_cpu | external_api`).

Handled:

- Offline user edits + concurrent cloud captures → user-region preserved; system-region regenerated with user-edit awareness
- User deletes LLM content → tombstone preserves deletion against future regen
- Network partition → saga retries with idempotency
- Two-device parallel use → device-of-capture model + re-embed-on-pull keeps state consistent

Out of MVP:

- CRDT-based collaborative editing (last-write-wins is sufficient for single-user multi-device)
- Real-time collaboration (sharing is read-only)
- Multi-device LLM-output reconciliation when both devices process the same artifact (de-duplication assumed to not occur — saga uses artifact content hash for idempotency)

## 19. Sharing Model (Tier 2)

Per-project sharing via magic link surfaced to the user; user shares the link through whatever channel they prefer.

Flow:

```text
user picks a project in Avalonia dashboard
  -> Avalonia requests a share token from cloud
  -> cloud generates tokenized URL (cryptographically random, 32+ bytes)
  -> cloud generates the share zip AT TOKEN-CREATION TIME and caches it
  -> cloud returns the link to Avalonia
  -> Avalonia shows the link to user (with copy button)
  -> user shares the link through their own channel (Signal, WhatsApp, manual email, SMS, in person, etc.)
  -> recipient clicks link (no registration required)
  -> recipient downloads the cached zip (Markdown + linked assets, scoped per below)
  -> sender can revoke from Avalonia dashboard at any time → cached zip is deleted
```

### Share-zip scope (locked W1+P1+F1+A1)

- **W1 — Cross-vault wikilink stripping**: `[[Alice]]` becomes plain text `Alice` if the target is outside the shared project folder. No dangling links, no leak of out-of-scope entity context.
- **P1 — Provenance stripped entirely**: `.provenance/` files are NOT included. Hub-dedup history, top-3-alternative project names, model versions, confidence scores, user-edit diffs all stay private.
- **F1 — Frontmatter whitelist**: keep `tags`, `captured_at`, `modality`. Drop fields that reference cross-vault entities or projects (e.g., `entity_refs`, `routing_alternatives`). Whitelist, not blacklist.
- **A1 — All assets bundled**: linked images, audio files, PDFs that the project's notes reference are included. User chose to share the project; the assets are part of it.

Properties:

- **No email-service dependency** — system never sends email; user controls the sharing channel
- No recipient registration
- Read-only download
- Revocable from Avalonia dashboard; revoke deletes cached zip + invalidates token
- **Default expiry: 24 hours** (configurable per share)
- Minimum audit trail: system logs share creation and revocation; per-recipient accesses not logged
- True "minimum trail" — system has no record of who the user shared with

Recipient experience: opens zip in Obsidian or any Markdown reader; graph view is islanded to within-project links only; no live updates after download.

## 20. Security and Privacy

Trust model:

- Each user trusts their own cloud (single-tenant per cloud instance)
- Personal data never leaves the user's own cloud (no third-party LLM, no shared SaaS provider, no email service) when on backends L or CC
- Tier 2 introduces controlled external dependencies: domain DNS provider, Let's Encrypt CA
- API backend opt-in is the only path where data leaves the user's cloud to a third party (explicit consent required)

### Authentication and admin

- Per-user cloud account auth: email + password with argon2id hashing
- **8 dual-use recovery codes** generated at onboarding. Each code:
  - Cloud-side: hashed, allows account password reset
  - Local-side: KDF-derived key encrypts the master key, allows passphrase bypass for local cred vault
  - Single-use semantics: presenting a code to either path consumes it
- Cloud-provider API token (admin) stored locally in Avalonia, encrypted with Argon2id-derived AES-GCM key from user passphrase
- Two-level auth: user account login (email + password) is independent from cloud-provider admin token
- Lost passphrase + lost all 8 codes → manual fallback via DO/Hetzner web console (destroy + reprovision, or SSH-direct DB reset)

### Transport

- Tier 1: SSH tunnel only (key auth)
- Tier 2: HTTPS via Caddy + Let's Encrypt for sharing endpoints (recipient browser access); Avalonia continues using SSH for its own traffic
- Internal API binds to localhost; not exposed without Caddy in Tier 2

### At-rest

- Filesystem encryption required at the VPS volume level via DO/Hetzner provider-native encryption (Terraform flag, no LUKS complexity)
- Local Avalonia cred vault: AES-GCM with Argon2id-derived key from passphrase

### Sharing security (Tier 2)

- Cryptographically random 32+ byte tokens, single-purpose per share
- Default 24-hour expiry
- Per-share revocation invalidates token + deletes cached zip immediately
- User controls the sharing channel — system never auto-sends to recipient

### Audit trail

- Minimum: account login, share creation, share revocation, cloud destroy actions
- Provenance JSON for per-artifact processing detail (LLM calls, routing, dedup) — local + cloud copies

### Channel compromise

- Residual risk depends on the channel the user picks. If user shares via WhatsApp, compromised WhatsApp = compromised share. Mitigations baked in: 24h expiry limits exposure window; revoke from Avalonia dashboard kills the link immediately.

### External API mode residual risk

- Only when user opts into API primary backend
- Captured content (transcripts, OCR text, note bodies) sent to the chosen provider per LLM call
- Provider may store, log, or use the data per its own terms
- Mitigations: explicit security-flag consent during setup; persistent settings badge; per-call audit log entry; easy switch back to Local or Cloud CPU
- User responsibility to read provider's privacy policy before enabling

Out of MVP threat model: insider on cloud host; server-side exfiltration after compromise; side-channel attacks on LLM; compliance frameworks (HIPAA/GDPR); provider-side breaches in External API mode.

## 21. Observability and Provenance Schema

Observability is built into the architecture from MVP. Provenance JSON per artifact (and per hub note for regen tracking):

```json
{
  "artifact_id": "...",
  "ingested_at": "...",
  "capture_source": "voice_memo | screenshot | url | text | drag_drop | dictation",
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
      "processor": "parakeet_transcription",
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
      "task": "structured_extraction | hub_regen | url_markdown_transform",
      "model": "gemma-4-e2b-q4",
      "primary_backend": "local | cloud_cpu | external_api_openai | external_api_anthropic | ...",
      "tier": "local | cloud_cpu | gpu | external_api",
      "prompt_hash": "...",
      "response_hash": "...",
      "confidence": 0.83,
      "alternatives": [],
      "gpu_cost_eur": 0.00,
      "device_id": "..."
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
    {
      "name": "Alice",
      "type": "People",
      "confidence": 0.91,
      "deduplicated_to_existing": true,
      "alias_of": "alice_smith"
    }
  ],
  "wikilinks_inserted": [
    {"target": "Alice", "anchor_position": 142, "similarity_score": 0.87, "context": "..."}
  ],
  "user_suppressions": [],
  "user_system_edits": [
    {"diff": "...", "at": "...", "ack_by_regen_at": "..."}
  ],
  "regen_count": 0,
  "last_regen_at": null,
  "eval_corpus_label": null
}
```

New fields vs. 2026-05-12 schema:

- `gpu_cost_eur` per LLM call — enables month-spend computation by summing provenance, independent of provider billing API
- `device_id` per LLM call — supports per-device backend analysis under C2
- `eval_corpus_label` per artifact — nullable, populated by user during eval-period post-hoc labeling

This schema enables future evaluation of:

- Routing accuracy (against `eval_corpus_label`)
- Entity-extraction quality + dedup performance
- ASR transcription quality (Parakeet WER cited from external benchmarks, not re-run)
- Wikilink relevance (precision-at-K)
- User-edit respect rate (did regen preserve user changes?)
- Filter precision/recall
- Project growth rate
- Per-backend output comparison (L vs CC vs API; CPU vs GPU)
- GPU cost tracking

## 22. Deployment

### Tier 1 default (per-device backend L) — minimal Docker Compose on Hetzner CCX11 or smallest DO droplet (~€5/month)

- API container (ASP.NET Core on .NET 10)
- PostgreSQL 16 (auth, projects, share tokens) — **no pgvector**
- Caddy in minimal mode (SSH-only access via SSH tunnel)
- Encrypted filesystem volume (provider-native encryption)
- **No LLM containers** — LLM runs on user's PC
- **No Parakeet on cloud** — ASR runs on user's PC
- **No embedding model on cloud** — embedding runs on user's PC
- **No vector index on cloud** — sqlite-vec lives on user's PC
- Cloud is pure storage + sync transport + share-zip generation

### Tier 1 with per-device backend CC (Cloud CPU)

- Same baseline PLUS Ollama container with 4B model always-loaded (e.g., Gemma 4 E4B Q4)
- Bumps cloud spec to Hetzner CCX with ~6 GB RAM available
- Adds ~€10/month → Tier 1 total ~€15/month
- Avalonia routes LLM calls to cloud Ollama instead of running locally
- Per-device choice: another device on the same vault can stay on L

### Tier 1 with per-device backend API (External)

- Same Tier 1 baseline; no cloud-side LLM container
- Avalonia calls go directly from user's machine to external provider
- Per-call audit logged in provenance JSON

### Tier 2 (add domain) — same stack with reconfigured Caddy

- User adds domain via Avalonia Settings → Upgrade
- Caddy reconfigured via admin API for the new domain
- Let's Encrypt cert auto-provisioned (~30s)
- Sharing endpoints come online (recipient browser → magic link)
- No email service added — sharing links surfaced via Avalonia

### GPU spin-up (opt-in, smart batched, cost-capped)

For multimodal vision, heavy synthesis, regen escalation when GPU mode enabled:

- **Batching policy** (hybrid threshold + ceiling):
  - Threshold: spin up GPU when ≥N=5 GPU-eligible tasks queued (default, configurable)
  - Ceiling: spin up GPU when oldest queued task hits T=1 hour wait (default, configurable)
  - All queued tasks processed in one GPU session
- **Cost protection (locked)**:
  - **Monthly hard cap (user-configurable, default €30/month).** On exceeding cap, GPU mode auto-disables. Pending tasks fall back to local LLM. User notified.
  - **Soft warning at 75% of monthly cap** via toast notification
  - **2-hour single-instance lifetime ceiling.** Even if tasks queued, instance force-terminated at 2h. New batch can spin up immediately.
  - **Concurrency = 1.** Only one GPU instance ever active per vault.
- **Provisioning**: Cloud-side cron provisions Hetzner GEX via Terraform; instance size adapts to user's chosen GPU model
- **Communication**: Wireguard tunnel auto-established between API server and GPU instance
- **Cold start**: ~5 minutes (GPU boot + Ollama model load); saga long-polls and tolerates
- **Manual override**: user can hit "process now" on a queued task to bypass batching (still subject to cost cap)
- **Failure mode**: if GPU provisioning fails (quota, capacity), GPU-eligible tasks fall back to local LLM with degraded quality; user notified via toast

### Avalonia onboarding (5 screens, locked)

| Screen | Content | User input |
|---|---|---|
| 1. **Storage** | Vault path + App Support path (OS defaults pre-filled). Existing-vault detection here: if found → "Start fresh / Re-seed from this vault" radio. | 0–2 path overrides + re-seed choice |
| 2. **LLM** | Pick backend: Local (RAM auto-detected → recommended model) / Cloud CPU / External API. Full-screen security warning if External API picked. | 1 radio choice (+ provider + API key if API) |
| 3. **Cloud** | Pick provider (DO / Hetzner). Paste cloud-provider API token. | 1 radio + 1 paste |
| 4. **Account** | Email + password (account login) + passphrase (cred vault). **8 dual-use recovery codes shown.** User confirms they have recorded them. | 3 text fields, record codes |
| 5. **Provisioning** | Animated progress: Terraform → cloud up → SSH key installed → Parakeet + embedding models downloading (fast, ~700 MB total) → LLM model downloading in background (~1.7 GB for E2B). User can start capturing once Parakeet ready; saga waits for LLM. | None — wait |

Vault scaffolding (created during step 5): `Inbox/`, `Projects/` (empty), `_Entities/People/`, `_Entities/Places/`, `_Entities/Concepts/`, `_Entities/Papers/`, `_Entities/Uncategorized/`, `.system/`, `.provenance/`.

Re-seed path (when existing vault detected at step 1): Avalonia uploads existing artifacts + notes + provenance to fresh cloud + re-derives embeddings into local sqlite-vec.

### Tier 2 upgrade flow (Settings, not onboarding)

1. Settings → Add domain → user enters `myvault.example.com`
2. Avalonia shows A-record instructions for the user's DNS provider
3. User confirms DNS done
4. Avalonia signals cloud to reconfigure Caddy with the new domain
5. Caddy obtains Let's Encrypt cert (~30s)
6. Avalonia confirms in UI: "Tier 2 enabled at https://myvault.example.com"
7. Sharing now available

### Destroy flow

- Settings → Destroy cloud → passphrase confirmation
- Terraform destroy: VPS instance + encrypted volumes + Wireguard config + (if Tier 2) cached share zips
- Manual DNS cleanup notice if Tier 2
- **Local Obsidian vault on user's machine survives** (intentional — user keeps their data)
- **Local App Support state survives** (models, encrypted credentials, sqlite-vec index)
- Avalonia offers immediate "Provision new cloud" workflow that detects the surviving local vault and offers re-seed path
- Avalonia local state can be fully wiped via separate "Uninstall Avalonia" action

Not in MVP:

- Kubernetes
- Managed database
- Multi-node API scaling
- Enterprise object storage
- Multi-region failover

## 23. Evaluation Plan

**Four quantitative metrics + one qualitative axis** (locked).

### Quantitative

1. **Routing accuracy** (top-1, top-3)
   - Corpus: 150–200 artifacts collected over 3–4 weeks of organic use (or 300+ at 20/day cadence; 100–150 held out for reporting)
   - Ground truth: user post-hoc labels via `eval_corpus_label` field in provenance JSON
   - Baselines reported alongside:
     - **B0** Random (uniform over existing projects) — floor
     - **B1** Most-recent-project heuristic
     - **B2** TF-IDF cosine similarity (classical IR baseline)
     - **B3** The system's local 4B with retrieval
     - **B4** (optional) GPU model when GPU mode enabled

2. **Entity dedup precision/recall**
   - Sample N=50 artifacts from the routing corpus
   - Manually identify ground-truth entities + which should dedup against existing
   - Compare to provenance's `entity_extractions[].deduplicated_to_existing` decisions

3. **D filter precision/recall**
   - Constructed test set: 50 known-bad (silent recordings, blurry photos, near-dup screenshots) + 50 known-good
   - Run through filter, compute confusion matrix
   - Most defensible metric because ground truth is unambiguous

4. **Wikilink precision-at-K**
   - Sample N=100 wikilinks inserted across the corpus
   - User judges each "relevant / weakly relevant / irrelevant" in Obsidian
   - Precision = relevant / total
   - Recall undefined (can't enumerate all wikilinks the system "should have" inserted)

### Qualitative

5. **E2E weekly diary** over 4–6 weeks
   - Pre-registered prompts: what worked / what frustrated / what surprised / what felt broken
   - Quantitative supplements: capture-rate-over-time, vault-size-over-time, manual-correction-rate-over-time

### What's NOT in the eval bundle

- Parakeet WER: cited from Nvidia's published numbers on FLEURS (Ukrainian + European), not re-run
- Sync correctness under partition: integration-test spec conformance, not statistical eval
- Sharing security: spec correctness demonstration, not measurement
- User-edit respect rate: covered qualitatively in diary, not measured statistically

### Scope acknowledgment

n=1 personal-vault study. Thesis explicitly says: "for one user (the author), the system achieved metrics X, Y, Z" — not a generalization claim. Bachelor's thesis allowed this scope.

## 24. MVP Scope (locked)

Implement:

- Avalonia 11 + .NET 10 client; vertical slice architecture under `src/ThanyMarcus.Client/Features/*`
- Cross-OS (Windows + macOS + Linux); native hotkey integration via SharpHook on Win/Mac/X11; main-window fallback on Wayland
- 6 capture surfaces (voice memo, screenshot region-select, URL, drag-drop, dictation, paste-text)
- Toast preview (U1 fire-and-forget) + expand-to-annotate
- **Primary LLM backend per-device** (C2): Local (LLamaSharp with Gemma 4 E2B default, E4B/8B configurable, RAM auto-detect) / Cloud CPU (always-on Ollama container) / External API (security flag, OpenAI + Anthropic at minimum)
- **Local vector index** (sqlite-vec) + **local embedding model** (multilingual-e5-small ONNX) — no cloud-side vector retrieval
- Parakeet-tdt 0.6B v3 ONNX local ASR via direct Microsoft.ML.OnnxRuntime
- Classical D filter (silence/blur/dup) + dropped-log review surface
- Two-path storage (Vault + App Support) with OS defaults, change-with-migration
- External API security-flag UX
- Re-seed from local vault flow at onboarding
- Cloud admin: passphrase-protected credential vault (Argon2id + AES-GCM), **8 dual-use recovery codes**, provisioning wizard, destroy with confirmation, Tier 1 → Tier 2 upgrade
- ASP.NET Core on .NET 10 API with endpoints listed in §10
- PostgreSQL 16 (no pgvector, no `tenant_id` schema)
- Encrypted filesystem artifact storage (provider-native encryption)
- **Local saga orchestration in Avalonia**: LIFO queue, retries, idempotency, persistent SQLite state, warm-up step on offline-resume
- Processor router (config-driven), local-vs-cloud step tagging
- Processors per §11: voice memo, image (Tesseract local OR cloud GPU vision-LLM), PDF, URL (web/YouTube/PDF URLs), text artifact, drag-drop file
- **Single structured LLM call per artifact** (S4): routing + entity extraction + wikilink anchor selection; procedural note assembly without LLM rewrites
- LLM auto-routing with project auto-creation + Inbox-until-N=10 cold-start
- LLM entity extraction with locally-retrieved vector dedup context
- LLM wikilink anchor selection (locally-retrieved similar notes via sqlite-vec)
- **User-defined entity-type folders** (E5): vault scaffolding seeds People/Places/Concepts/Papers/Uncategorized; system never creates new type folders
- Entity hub maintenance with N=3 trigger and Context + Generated Views structure
- Hub regen on observing device (D1) with B4c user-edit-aware prompt
- GPU mode configuration: on/off toggle + cloud model size (8B / 14B / 35B-A3B) + batching thresholds + **cost cap (€30/month default, 2h instance lifetime, concurrency=1)**
- "Process now" manual override for queued GPU-eligible tasks (cost-cap respected)
- Markdown vault generator with `## System Output` / `## User Notes` convention
- Tombstones for user deletions of LLM content
- Provenance JSON with full observability schema (§21) — including `gpu_cost_eur`, `device_id`, `eval_corpus_label`
- Sync engine in Avalonia (saga, local-wins, tombstones, edit-diff propagation, re-embed-on-pull, conflict files)
- Caddy reverse proxy with dynamic config (minimal in Tier 1; full HTTPS in Tier 2)
- Magic-link sharing (Tier 2 only): link surfaced via Avalonia; **share-zip generated at link-creation with W1+P1+F1+A1 stripping**; revocation deletes cached zip
- Account password recovery via **8 dual-use recovery codes** (same codes also decrypt local cred vault)
- SSH tunneling via SSH.NET for Tier 1
- Docker Compose orchestration
- Terraform for Hetzner + DigitalOcean (encrypted volumes, GPU on-demand, Wireguard tunnel)

Defer (see §25).

## 25. Open Items and Future Work

- **Mobile native capture** (iOS Shortcut + Android stub app + share-sheet integration)
- **Snapshot / restore export-import flow** (B6b alternative): explicit zip export before destroy + restore on new cloud
- **macOS distribution polish**: notarization pipeline, signed .pkg installer (only relevant if releasing publicly)
- **Linux distribution packaging**: AppImage / Flatpak / per-distro packages
- **Hotkey-triggered selected-text capture** with active-window-title context (platform-specific APIs)
- **Region-select polish**: smart-snap, magnifier, scrollable-area capture
- **Project explosion mitigation**: minimum-artifact-count threshold before a candidate project sticks; instrument creation events now, mitigate post-experiment
- **Privacy detection in pre-filter**: face detection, sensitive-text regex on screenshots
- **Fine-tuning**: 4B model fine-tuned for routing could match larger-model quality at lower inference cost; potentially its own thesis pivot
- **Vault querying**: vector index + LLM Q&A endpoint ("ask my vault")
- **Mobile native viewing**: Obsidian Mobile + cloud sync investigation
- **Video file processing**: keyframe extraction + audio extraction + per-frame vision
- **Touchpad gesture capture**: platform-specific
- **Twitter/X URL support**: authentication complexity
- **Telegram channel/message capture**
- **Selected-text via OS-native APIs** (not just clipboard)
- **Auto-DNS cleanup on destroy**: store DNS provider API token, auto-clean
- **CRDT-based concurrent multi-device editing** for `## User Notes` (current: last-write-wins + conflict file)
- **Real-time collaboration on shared projects**
- **Encrypted-at-rest with user-held keys** (current: provider-native encryption)
- **Threat-model deepening**: insider, side-channel, server compromise
- **Wayland global shortcuts via xdg-desktop-portal-globalshortcuts** when compositor support stabilizes
- **Routing optimization**: small dedicated router model (1-2B specialized) for sub-second routing latency
- **Redacted-hub bundling in sharing** (W3 alternative): include entity hubs in share zip but only with Context entries from the shared project; partial-context regen of Generated Views
- **Vault QA via cloud GPU**: enables semantic queries against the vault; requires uplifting embeddings to cloud
- **Eval generalization**: multi-user study (currently n=1 by thesis scope)
- **Compliance frameworks**: HIPAA, GDPR data export rights

## 26. Repository Structure and Build (locked)

Monorepo, .NET 10 solution, vertical slice architecture.

```
Thany-Marcus/
├── ThanyMarcus.sln
├── README.md
├── .gitignore
├── src/
│   ├── ThanyMarcus.Client/
│   │   ├── App/                          # bootstrap, DI, Avalonia shell
│   │   ├── Features/
│   │   │   ├── Capture/{VoiceMemo,Screenshot,UrlCapture,DragDropFile,Dictation,_Common}/
│   │   │   ├── Processing/{ArtifactSaga,RoutingExtraction,ProceduralNoteAssembly,HubRegen,DropFilter,Provenance}/
│   │   │   ├── Knowledge/{EntityExtraction,EntityHubs,WikilinkAnchors,ProjectRouting}/
│   │   │   ├── Sync/{FileWatcher,CloudPush,CloudPull,ConflictResolution,ReEmbedOnReceive}/
│   │   │   ├── Sharing/
│   │   │   ├── Onboarding/{StorageScreen,LlmBackendScreen,CloudProviderScreen,AccountScreen,ProvisioningScreen}/
│   │   │   ├── Settings/{BackendSelection,GpuMode,VaultPaths,DropLogReview,RecoveryCodes}/
│   │   │   └── CloudAdmin/{Provisioning,Destroy,TierUpgrade}/
│   │   ├── Infrastructure/
│   │   │   ├── Asr/                      # Parakeet ONNX
│   │   │   ├── Llm/                      # LLamaSharp + prompt registry
│   │   │   ├── Embedding/                # multilingual-e5-small
│   │   │   ├── VectorIndex/              # sqlite-vec wrapper
│   │   │   ├── Crypto/                   # AES-GCM, Argon2id, recovery codes
│   │   │   ├── Terraform/
│   │   │   ├── Ssh/                      # SSH.NET
│   │   │   ├── Hotkeys/                  # SharpHook + platform detection
│   │   │   ├── Audio/                    # PortAudio
│   │   │   ├── Screenshot/               # region-select overlay
│   │   │   ├── FileSystem/
│   │   │   └── PersistentState/          # SQLite for saga queue + settings
│   │   └── Shared/
│   ├── ThanyMarcus.Cloud.Api/
│   │   ├── App/                          # Program.cs, DI, middleware
│   │   ├── Features/
│   │   │   ├── Auth/{Register,Login,PasswordReset,RecoveryCodes}/
│   │   │   ├── Artifacts/{Upload,Retrieve}/
│   │   │   ├── Sync/{Pull,Push}/
│   │   │   ├── Sharing/{Create,ZipBuild,RecipientDownload,Revoke}/
│   │   │   ├── Gpu/{Spinup,Batch,CostCap,Teardown}/
│   │   │   └── TierUpgrade/
│   │   ├── Infrastructure/{Database,Storage,Caddy,Terraform,Ssh}/
│   │   └── Shared/
│   ├── ThanyMarcus.Shared/               # contracts; no UI/no infra deps
│   │   ├── Contracts/
│   │   ├── Provenance/
│   │   ├── Saga/
│   │   └── Models/
│   └── ThanyMarcus.Cli/                  # admin scripts (optional)
├── tests/
│   ├── ThanyMarcus.Client.Tests/         # tests mirror Features/* layout
│   ├── ThanyMarcus.Cloud.Api.Tests/
│   ├── ThanyMarcus.IntegrationTests/
│   └── ThanyMarcus.EvalTools/            # eval corpus runner, baseline comparators
├── infra/
│   ├── terraform/
│   │   ├── infrastructure/               # CCX/CPX provisioning, encrypted volume
│   │   └── gpu/                          # Hetzner GEX on-demand
│   └── docker/
│       └── docker-compose.yml            # api + postgres + caddy
├── docs/
│   ├── architecture.md                   # current consolidated plan
│   ├── decisions/                        # ADRs 0001..0016 — one per Q1..Q16
│   ├── api.md
│   └── onboarding.md
├── plans/                                # historical
│   ├── consolidated-plan-2026-05-12.md   # pre-second-grilling (preserved)
│   ├── consolidated-plan-2026-05-13.md   # this document
│   ├── final-expanded-project-plan-2026-05-12.md   # pre-pivot medical version
│   └── archive/                          # n8n config, K8s manifests, load-test scripts from earlier passes
└── .github/
    └── workflows/
        ├── client-ci.yml                 # build + test on Win/Mac/Linux matrix
        ├── cloud-ci.yml                  # build + EF migrations + integration tests
        └── release.yml                   # cross-OS client packaging
```

Conventions:

- Each `Features/<Slice>/` owns its UI + handlers + per-feature persistence; no cross-slice references except through `ThanyMarcus.Shared`
- `Infrastructure/` houses cross-cutting heavy deps (LLM, ASR, sqlite-vec) shared by many slices
- Tests mirror slices (`tests/ThanyMarcus.Client.Tests/Features/<Slice>.Tests/`)
- ADRs in `docs/decisions/` follow the format Context / Options / Decision / Consequences; numbered 0001 onward; thesis-appendix-ready
- Plain handler classes with primary constructors + `Task<Result<T>>` returns; no MediatR overhead
- EF Core for cloud DB migrations
- Minimal API (vs MVC controllers) in `ThanyMarcus.Cloud.Api`

Archive items moved to `plans/archive/`:

- `infra/terraform/kubernetes-apps/` (K8s explicitly out of MVP)
- `infra/backups/n8n_pipeline_conf.json` (n8n from earlier design pass)
- `infra/scripts/compare-lbs.js`, `load-test-haproxy.js`, `load-test-nginx.js`, `stress-test.js` (load-balancer experiment, not relevant to local-first architecture)
- `src/Dockerfile` + `src/.devcontainer/` (replaced by per-project `Dockerfile` under `src/ThanyMarcus.Cloud.Api/`)

CI matrix:

- `client-ci.yml`: matrix on `[windows-latest, macos-latest, ubuntu-latest]`; build + unit tests
- `cloud-ci.yml`: `ubuntu-latest`; build + EF migrations + integration tests against postgres-in-container
- `release.yml`: triggered on tag; cross-OS client binaries to GitHub Releases; cloud Docker image

---

## Appendix A: Cold-read locks (2026-05-12 second-pass grilling)

Preserved verbatim from `consolidated-plan-2026-05-12.md`. Items closed during the cold-read grilling phase:

- **A2** — persona = continuous capturer; auto-routing only routing mechanism; Zettelkasten emphasis; toast preview; manual user-created folders are equal-status routing targets; ambient = on-demand
- **A3** — Markdown headers (`## System Output` / `## User Notes`) uniform across artifact notes and entity hubs
- **A4** — image route generalized; URL processor added; selected-text hotkey added; annotated capture via expanded preview; video URLs in MVP, video files future
- **B1** — LLM-driven entity dedup with vector-retrieved top-K + manual merge fallback via Obsidian
- **B2** — local dictation creates standalone text artifact; Parakeet-tdt 0.6B v3 ONNX as ASR (local-only)
- **B4** — user EDITS to LLM content (B4c + system-prompt mitigation): LLM overrides accepted; regen prompt includes diff
- **Tier 3 batch** — C1, C2 (provider-native encryption), D1 (Inbox-until-N=10), D2 (vault querying deferred), D3 (4B-on-CPU routing accepted), E (Obsidian-compatible in title)
- **B6** — GPU spin-up smart batching; configurable model size; Wireguard tunnel; manual override
- **Mobile drop** — mobile native capture dropped from MVP
- **B5 / email drop** — no email service; sharing links surfaced via Avalonia
- **Option C: local-first LLM** — original lock; 2026-05-13 grilling carried through to completion

## Appendix B: Earlier locks (pre-cold-read grilling)

Preserved verbatim from `consolidated-plan-2026-05-12.md`. Items closed in the first design pass before the cold-read:

- **Domain pivot**: medical → personal memory management
- **Deployment**: tiered single-tenant per cloud — Tier 1 SSH-only, Tier 2 add-domain
- **Avalonia is admin client**
- **Passphrase-protected credentials** — Argon2id + AES-GCM
- **Two-level auth** — cloud-provider token independent from user-cloud account login
- **Offline editing**: saga pattern with local-wins for user regions + cloud-regen for derived content (cloud-regen later superseded by local-regen per 2026-05-13 Option-A completion)
- **Tombstones** for user deletions
- **LLM strategy**: pluggable backend (updated 2026-05-13 with per-device choice)
- **Vision processing**: GPU vision-LLM when available; OCR fallback otherwise

## Appendix C: 2026-05-13 grilling locks

Sixteen design questions surfaced during the second-pass grilling. Q11 skipped at user request. Each lock has a corresponding ADR planned in `docs/decisions/`.

| # | Question | Lock | Rationale (brief) |
|---|---|---|---|
| Q1 | Vector index location | **A** — Local-only sqlite-vec; cloud has zero semantic state | Completes the Option C local-first lock that the 2026-05-12 doc didn't propagate. Strengthens privacy story; thins Tier 1; multi-device achievable via deterministic embeddings without cloud transport. |
| Q1.5 | Multi-device | **M2** — In MVP; device-of-capture-processes; last-write-wins + .conflict files | Two-laptop scenario is real for the live-researcher persona. Cost under Option A is small (re-embed-on-pull is the same code path as re-seed). CRDTs are overkill. |
| Q2 | Receiving device behavior on sync | **B1** — Re-embed only; trust capturing device's LLM output | Re-running LLM is non-deterministic + expensive. Embeddings ARE deterministic. Capturing device is privileged by design. |
| Q3 | Regen triggers + execution device | **T1+D1+B4c** — Entity hub regen on new mention only; observing device runs; user-edit-aware | Artifact notes shouldn't shapeshift retroactively; entity hubs are where "alive" feel lives. Observing device is the natural coordinator under M2. |
| Q4 | Saga LLM decomposition | **S4** — One structured call per artifact + per-hub regen; no LLM note-gen | Note-gen actively damages content (4B rewrites hallucinate). Wikilink anchor positions are verifiable against source. Latency budget becomes realistic. |
| Q5 | Sharing scope | **W1+P1+F1+A1** — Strip cross-vault wikilinks; strip provenance; whitelist frontmatter; bundle assets | Privacy-correct minimum. W3 redacted hubs is the right long-term answer but defer. |
| Q6 | Entity-type folder management | **E5+K1** — User-defined entity types; LLM never creates type folders; projects keep LLM-auto-create | LLM-emergent types fragment (Person/People/Persons). Projects justify auto-create because user's life context is unbounded; types form a converging vocabulary. |
| Q7 | Evaluation methodology | **4 quant + 1 qual** — Routing accuracy, entity dedup P/R, D filter P/R, wikilink precision-at-K, weekly diary | Nine deferred metrics not defensible. This bundle is defensible for n=1 bachelor's thesis. |
| Q8 | Latency UX + cadence | **U1 + Q-LIFO** — Fire-and-forget toast + tray badge; LIFO queue; 20/day cadence | Ambient claim requires instant toast. LIFO matches actual user attention. GPU mode's value sharpens to "vision quality" specifically under S4. |
| Q9 | Cross-OS + hotkeys | **X6** — Avalonia cross-OS in MVP; SharpHook hotkeys on Win/Mac/X11; main-window fallback on Wayland | OBS's pattern: native API per OS, accept Mac TCC prompt, graceful Wayland degradation. Browser/Tauri/Electron don't solve the OS-integration problem. |
| Q10 | Stack lock | .NET 10 + Avalonia 11 + Parakeet via direct ONNX + LLamaSharp + Gemma 4 E2B default + sqlite-vec + multilingual-e5-small + PortAudio + SharpHook + SSH.NET + Argon2id + AES-GCM | Models updated per memory (Gemma 4, not Gemma 2). E2B prioritizes low hardware threshold. sqlite-vec replaces deprecated sqlite-vss. |
| Q11 | Demo/milestones | Skipped at user request | — |
| Q12 | Backend choice scope | **C2** — Per-device | Lets asymmetric hardware setups use the right backend per device. B1 already accepted device-asymmetric outputs. Eval reproducibility solvable by discipline during measurement period. |
| Q13 | GPU cost protection | €30/month default cap + 75% soft warn + 2h instance lifetime + concurrency=1 | Layered defense against runaway bills. 2h ceiling is tight enough to interrupt hung Ollama, loose enough not to fragment normal batches. |
| Q14 | Multi-tenant hedge | **MT1** — Drop. Single-tenant only, no `tenant_id` discriminator anywhere | YAGNI applied honestly. Each user provisions their own cloud; tenant_id was always dead weight. Privacy story tightens. |
| Q15 | Onboarding | 5 screens + 8 dual-use recovery codes + non-blocking model download + Tier 2 in Settings | 16 separate codes was security theater (either set's possession grants full takeover). Dual-use halves friction without weakening security. |
| Q16 | Repo structure | .NET 10 + vertical slice architecture; tests mirror slices; ADRs 0001–0016 as thesis appendix | Each feature folder = one thesis chapter section. Solo-developer flow is feature-by-feature, not layer-by-layer. |

**All decisions locked. Plan is current as of 2026-05-13.**
