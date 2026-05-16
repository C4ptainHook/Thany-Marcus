# Cloud-Pivot Plan (Obsidian-Plugin Architecture)

Date: 2026-05-13 (second same-day revision)
Status: **Supersedes `plans/consolidated-plan-2026-05-13.md`** and the earlier same-day revision of this document. The 2026-05-13 consolidated doc is preserved unchanged for historical reference.

This revision pivots the user-facing surface from "browser web app accessing a cloud-hosted vault via WebDAV" to **"Obsidian plugin against a cloud processing service, with the vault local on the user's machine."** Motivation: no real user captures into a separate web tab when Obsidian is where their attention already lives. The cross-OS pain (Avalonia + SharpHook + Wayland + PortAudio + LLamaSharp + sqlite-vec) from the locked plan stays dropped; instead of solving it with a browser web app, we solve it by **letting Obsidian be the host** — Obsidian already runs on Win/Mac/Linux (and mobile, deferred), already handles hotkeys via its command palette, and already renders the knowledge graph natively.

---

## 1. Final Thesis Direction

The project is a **platform for personal knowledge clouds**. Users sign up to a centralized **portal** that lets them provision and operate their own self-hosted single-tenant cloud across multiple infrastructure providers (DigitalOcean + Azure in MVP; extensible to others). They then install the **Thany-Marcus Obsidian plugin** in their existing Obsidian setup. The plugin adds capture commands inside Obsidian (URL, voice memo, screenshot, file drag-drop, selected text); the cloud is a pure **API-only processing service** that receives artifacts, runs the multimodal pipeline (ASR + LLM + entity extraction + wikilink anchoring), and returns processed Markdown which the plugin writes back into the user's **local Obsidian vault**. The user reads, edits, and graph-views in normal Obsidian.

Optional **"unsafe mode"** routes LLM calls to an external provider (OpenAI / Anthropic) for frontier-model quality with explicit consent.

**Desktop only in MVP.** Mobile (via Obsidian Mobile + the same plugin) is future work.

Core transformation:

```text
user is in Obsidian (normal local vault)
  -> Cmd/Ctrl+P -> "Thany-Marcus: Capture URL" (or voice, screenshot, etc.)
  -> plugin uploads artifact to user's own cloud over bearer-token API
  -> cloud-side processing pipeline:
       -> modality-specific extractors (Parakeet ASR for audio, Readability for URLs,
          PdfPig for PDFs, OCR-fallback for images)
       -> classical D filter (silence VAD, blur Laplacian, near-dup pHash)
       -> embedding generation (multilingual-e5-small ONNX in-process)
       -> pgvector retrieval (entity dedup + similar-note context)
       -> ONE structured LLM call: routing + entities + wikilink anchors
            - safe mode (default): in-cloud Ollama with 4B/8B model
            - unsafe mode (opt-in): external API (Anthropic / OpenAI)
       -> procedural Markdown note assembly
       -> per-affected entity-hub regen (N=3 mention threshold)
  -> cloud returns processed Markdown + raw asset references to plugin
  -> plugin writes Markdown to local vault (Inbox/, Projects/, _Entities/)
  -> Obsidian's file watcher picks up new files
  -> user reads/edits in Obsidian; native graph view shows emergent structure
  -> sharing: plugin asks cloud for magic link; cloud generates cached zip;
              link surfaced in plugin/portal; user shares through own channel
```

## 2. Working Title

> **Cloud Service for Personal Data Aggregation and Organization in Markdown Format**

Extended technical form:

> **A Knowledge-Cloud Management Platform with Obsidian-Plugin Client for Multimodal Personal Data Aggregation, In-Cloud LLM Processing, and Local Markdown Vault Generation with Emergent Knowledge-Graph Structure**

## 3. Problem Statement

Personal heterogeneous data accumulates across devices and is hard to find, organize, and share without losing context. Existing tools (Obsidian, Notion, Mem.ai, Reflect) are excellent editors but either require manual ingestion and organization (Obsidian) or rely on multi-tenant SaaS providers users do not own (Notion, Mem.ai, Reflect). URL bookmarks rot. Voice memos pile up untranscribed. Screenshots lose context.

This project addresses these problems by:

- Adding **low-friction multimodal capture commands inside Obsidian itself** via a plugin — users never leave their normal note-taking environment
- Routing captures through the user's **own** single-tenant cloud for processing — no SaaS vendor in the loop
- LLM auto-routing into emergent projects with project auto-creation
- LLM entity extraction populated into user-defined entity-type folders
- LLM-driven dense wikilink generation producing an emergent knowledge graph rendered by Obsidian's own graph view
- URL snapshotting as link-rot mitigation
- A **centralized portal** ("Vercel for personal knowledge clouds") that makes provisioning and lifecycle management trivial across multiple cloud providers

## 4. What The System Is / Is Not

It is:

- A **knowledge-cloud management platform** (the portal) — hosted by the project maintainer, multi-user
- A **self-hosted single-tenant cloud** (the data plane) — one per user, provisioned by the portal via Terraform, runs the multimodal processing service
- A **multi-provider** infrastructure abstraction (DO + Azure in MVP; extensible by adding `.tf` modules)
- An **Obsidian plugin** (TypeScript) that lives inside the user's Obsidian and adds capture commands
- A **multimodal processor pipeline** in the user's cloud with LLM-driven auto-routing
- A **local Markdown vault generator** — the vault lives on the user's filesystem, opened by Obsidian normally
- An **emergent knowledge graph** rendered by Obsidian's native graph view (no separate viewer)
- A **per-project sharing system** via tokenized magic links

It is not:

- A desktop application (no Avalonia, no Electron app, no native client other than the Obsidian plugin)
- A mobile application (Obsidian Mobile + plugin compatibility is future work)
- A multi-tenant SaaS for user data (control plane is multi-user; data plane is single-tenant)
- A Markdown editor (Obsidian remains the editor of choice)
- A surveillance / continuous-recording tool

## 5. Persona

**Live researcher**: continuous capturer across long-running concurrent research threads. Capture cadence target ~20 artifacts/day during active research periods. The user is already in Obsidian for their research; the plugin's capture commands sit alongside Obsidian's own commands in the command palette. Hotkeys are bound via Obsidian's own keymap UI (cross-OS for free since Obsidian is cross-OS).

## 6. Main Contribution

The novelty is the combination of:

1. **Knowledge-cloud management platform** — Vercel-style central control plane that provisions and operates self-hosted single-tenant clouds across multiple infrastructure providers (DO + Azure in MVP). Multi-provider abstraction via Terraform; the two providers are intentionally heterogeneous (DO's simple-droplet model vs Azure's Resource-Group/VNet/NSG enterprise model) to demonstrate the abstraction is real. Adding providers post-thesis is a `.tf` file.
2. **Obsidian plugin as the capture surface** — TypeScript plugin against the Obsidian Plugin API; lives where the user already is, uses Obsidian's native command palette, keymap, and file system. No cross-OS work — Obsidian's portability is inherited.
3. **Local-vault canonical, cloud as processing service** — the user's Obsidian vault stays on their disk. The cloud processes captures and returns Markdown; copies of artifacts and processed notes stay on the cloud only for sharing and backup. The locked-plan promise "local vault survives cloud destroy" is preserved natively.
4. **Server-side multimodal pipeline** — Parakeet ASR + Ollama LLM + Readability + PdfPig + pgvector; one structured LLM call per artifact (S4 from 2026-05-13).
5. **Native Obsidian knowledge-graph rendering** — emergent graph via LLM-inserted wikilink anchors; visible in Obsidian's own graph view, no separate viewer needed.
6. **User-defined entity-type folders** (E5 from 2026-05-13) — projects auto-create (life is unbounded); entity types are user-defined to prevent fragmentation.
7. **Safe / Unsafe LLM mode** — default in-cloud Ollama; opt-in external API with full-screen consent + persistent badge + per-call audit log.
8. **Tokenized per-project sharing** with strict scope (W1+P1+F1+A1 from 2026-05-13).
9. **Decent modern security** — Google SSO + optional TOTP 2FA on portal; passphrase-encrypted provider tokens; 8 dual-use recovery codes; plugin bearer-token auth between plugin and user's cloud; no Google SSO required on the data plane (plugin uses bearer token, eliminating per-cloud OAuth client complexity).

Strongest claim:

> The system is a platform for personal knowledge clouds. A centralized portal orchestrates per-user single-tenant deployments across multiple infrastructure providers via Terraform; each user's cloud runs the full multimodal-processing stack and returns processed Markdown to a plugin that lives inside the user's existing Obsidian. The vault stays local on the user's machine and survives cloud destruction. Optional unsafe mode routes LLM calls to an external provider with explicit consent. The emergent knowledge graph is rendered by Obsidian's native graph view; no separate viewer is required.

## 7. End-To-End Architecture

```text
+---------------------------------------+
|  PORTAL (Boiko's VPS, ~€5/month)      |    multi-user control plane
|  - SPA frontend (Blazor or vanilla)   |    no user content
|  - ASP.NET Core backend               |
|  - Postgres (user -> cloud mapping;   |
|              encrypted infra tokens;  |
|              recovery codes; TOTP)    |
|  - Google SSO + optional TOTP 2FA     |
|  - Terraform CLI (shell-out)          |
|  - Per-cloud workspaces in TF state   |
|  - Plugin auth token issuance         |
+---------------------------------------+
            |
            |  user signs in, sets passphrase,
            |  pastes provider API token + domain,
            |  portal runs `terraform apply` -> droplet up
            v
+---------------------------------------+
|  CONTROL PLANE (per user, always-on)  |    ~$24/mo DO, ~$30/mo Azure,
|  2 vCPU / 4 GB, single-tenant         |    ~€5/mo Hetzner
|  Provisioned by portal via Terraform; |    user owns infra + data
|  cloud-init pulls Docker Compose:     |
|                                       |
|  - api (ASP.NET Core 10)              |
|    - plugin endpoints (bearer auth)   |
|    - portal admin endpoints           |
|    - public share-recipient endpoint  |
|  - postgres + pgvector                |
|  - caddy (HTTPS via Let's Encrypt)    |
|  - embeddings (e5-small ONNX,         |
|    in-process)                        |
|  - cheap extraction (URL/PDF/text)    |
|  - classical D-filter                 |
|  - saga queue + WorkerLifecycleSvc    |
|  - cloud-side asset/artifact store    |
|  - cloud-side processed-md backup     |
|  Joined to per-cloud private VPC.     |
|  NO web client, NO WebDAV.            |
+---------------------------------------+
            |
            |  private-network lease pull
            |  (postgres SKIP LOCKED;
            |   spawn via Terraform)
            v
+---------------------------------------+
|  BURST WORKER (per user, ephemeral)   |    ~$0.125/hr DO,
|  4 vCPU / 16 GB, single-tenant        |    ~$0.166/hr Azure,
|  Spawned on demand by control plane;  |    ~€0.05/hr Hetzner
|  destroyed after 10 min idle.         |    Realistic: ~$3–8/mo
|                                       |
|  - ollama (Gemma 4 E4B Q4, safe mode) |
|  - parakeet sidecar (sherpa-onnx)     |
|  - worker process: pulls jobs from    |
|    control-plane postgres, runs       |
|    inference, posts results back      |
|                                       |
|  No public ingress (firewall locks to |
|  control plane's private IP).         |
|  No persistent state — model weights  |
|  baked into image / pulled at boot.   |
+---------------------------------------+
            ^
            |  bearer-token API (plugin <-> user's cloud)
            |
+---------------------------------------+
|  User's machine                       |
|  - Obsidian (cross-OS, user's own)    |
|  - Thany-Marcus plugin (TS, installed |
|    from Obsidian community store)     |
|  - Local vault folder on disk         |
|    (canonical copy)                   |
+---------------------------------------+
```

**Trust boundaries:**

- **Portal trusts:** Google (for SSO), DO/Azure (provider APIs), the user (to enter their own data correctly).
- **Control plane trusts:** the plugin (via bearer token issued by portal), the portal (via admin token), Let's Encrypt (cert), its own burst worker (via short-lived worker registration token issued at spawn), optionally OpenAI/Anthropic (unsafe mode only).
- **Burst worker trusts:** the control plane that spawned it (via Terraform-injected registration token + private-network constraint).
- **Plugin trusts:** the user's own control plane (URL + bearer token configured in plugin settings).

**Portal never sees user content.** After provisioning, captures flow plugin → control plane → (burst worker, transiently) → control plane → user's vault. Nothing transits the portal. The burst worker only ever holds inference data in RAM; it has no persistent disk for user content and is destroyed after idle timeout.

## 8. Provisioning Portal

**Single instance, multi-user, hosted by the project maintainer.** Acts as the control plane for per-user cloud creation, lifecycle, and ongoing app-level management.

### Stack

- **Frontend:** Blazor Server OR vanilla HTML + small JS for the SPA. Choice deferred to implementation.
- **Backend:** ASP.NET Core 10 Minimal API
- **Database:** PostgreSQL 16 (tables for users, clouds, encrypted provider tokens, recovery codes, TOTP secrets, backup codes, issued plugin tokens)
- **Auth:** Google OAuth 2.0 via `Microsoft.AspNetCore.Authentication.Google` + optional TOTP via `Otp.NET`
- **Provisioning:** Terraform CLI shelled out from .NET backend. One Terraform workspace per user-cloud. State stored in portal Postgres via Terraform's `pg` backend (built-in, supports concurrent locking, encrypted at rest).
- **Crypto:** Argon2id (Konscious.Security.Cryptography) + AES-GCM (System.Security.Cryptography) for passphrase-encrypted provider tokens.
- **Hosting:** small DO droplet (~$5/month), Caddy + Let's Encrypt for HTTPS at `app.thany.click`.

### User flow

1. User visits the portal, signs in with Google SSO.
2. (Optional) Enables TOTP 2FA in Account Settings — scans QR code with an authenticator app, saves 8 single-use backup codes.
3. Clicks "Create cloud" → wizard:
   - Step 1: pick provider (DO / Azure; more in future) + paste provider credentials (DO: API token; Azure: service principal — tenant_id, client_id, client_secret, subscription_id)
   - Step 2: set passphrase (encrypts the provider credentials); see 8 recovery codes
   - Step 3: provisioning progress — Terraform plan → apply → droplet/VM boots → cloud-init runs → Docker images pulled → Ollama model downloaded → ready (~5–10 min on DO; ~7–15 min on Azure)
   - Domain is auto-assigned: portal generates random subdomain `<random8>.thany.click` and configures Cloudflare DNS A-record pointing at the new IP before Terraform completes. User does no DNS configuration.
4. On first boot, the user's cloud generates a **cloud admin token** and POSTs it to the portal's `/api/cloud/{cloud_id}/register-with-token` endpoint.
5. Portal stores `(user_id, cloud_id, droplet_ip, domain, cloud_admin_token, encrypted_provider_token, terraform_workspace)`.
6. Portal navigates user to "Plugin Setup" page — generates a **plugin bearer token** scoped to that cloud, shows it once for the user to copy into Obsidian's plugin settings.

### Ongoing management (the portal as a real platform surface)

User returns to the portal for:

- **App-level settings on their cloud** (uses cloud admin token, no passphrase): switch LLM model, toggle safe/unsafe mode, configure entity-type folders, tweak filter thresholds, view cost estimates, view audit log
- **Plugin token management**: issue new plugin tokens, revoke compromised ones
- **Infrastructure ops** (uses encrypted provider token, passphrase prompt): resize VPS, destroy cloud, update Docker images to latest release

### Credential storage (decent modern security)

- **Cloud admin token** — plaintext in portal Postgres; scoped only to app-level admin endpoints on the specific user's cloud; cannot destroy infra; cannot read vault content. Low-impact if portal is breached.
- **Plugin bearer token** — hashed in user's cloud's Postgres; plaintext shown once to user at issuance and never persisted in portal.
- **Provider API token** — `AES-GCM(token, Argon2id(passphrase, salt))`. Decrypted only in-memory when user supplies passphrase. Held only during the infra op, dropped after.
- **8 dual-use recovery codes (passphrase)** — hashed at rest with Argon2id; single-use; each can substitute for passphrase.
- **8 single-use TOTP backup codes** — separate set; hashed at rest; covers loss of authenticator app.
- **TOTP shared secret** — AES-GCM encrypted at rest with a server-side master key from env.

If portal Postgres is breached: attacker gets cloud admin tokens (low-impact damage) + encrypted provider tokens (useless without per-user passphrase) + hashed recovery codes (useless). User passphrases never persist in plaintext form on the portal.

## 9. Obsidian Plugin

**TypeScript plugin against the Obsidian Plugin API.** Distributed via Obsidian's community plugin store + GitHub Releases.

### Stack

- TypeScript
- Obsidian Plugin API (`obsidian` npm package)
- Build: esbuild (Obsidian template default)
- Cross-OS for free — Obsidian runs Win/Mac/Linux/iOS/Android; plugin marked `isDesktopOnly: true` for MVP
- Distribution: Obsidian community plugin store (post-thesis); GitHub Releases for direct install during development + thesis evaluation

### Settings (Obsidian → Community Plugins → Thany-Marcus)

- Cloud URL (e.g. `https://mynotes.example.com`)
- Plugin bearer token (paste from portal)
- LLM mode: safe (default) / unsafe (with persistent badge)
- External API key (only if unsafe selected; stored encrypted via Obsidian's secure-storage API)
- Default capture target folder (default: `Inbox/`)
- Entity-type folders (default: `_Entities/People`, `Places`, `Concepts`, `Papers`, `Uncategorized`; user-editable)
- Offline-capture queue location (default: `<vault>/.obsidian/plugins/thany-marcus/queue/`)

### Commands (in Obsidian's command palette, user-assignable hotkeys via Obsidian keymap)

1. **Thany-Marcus: Capture URL** — dialog prompts for URL → plugin POSTs → cloud processes → Markdown returned → written to default target folder
2. **Thany-Marcus: Capture voice memo** — recording dialog using MediaRecorder via Electron's webview → audio uploaded → Parakeet transcribes → Markdown returned → vault
3. **Thany-Marcus: Capture screenshot** — Electron `desktopCapturer` → region select → upload → OCR/vision → vault
4. **Thany-Marcus: Process selected text** — captures the current selection in the active note → cloud enriches → either replaces selection with linked version or creates new note (user-chosen behavior in settings)
5. **Thany-Marcus: Capture from clipboard** — reads clipboard (URL, image, or text) → routes through appropriate processor
6. **Thany-Marcus: Re-seed from cloud** — pulls all artifacts + processed Markdown from cloud into the local vault; for new-device install or recovery

### Drag-drop handler

Plugin registers a drag-drop handler on the Obsidian editor. Drop any file (PDF, audio, image) → plugin intercepts → uploads to cloud → cloud processes → asset stored cloud-side + reference written into local vault, optional Markdown note created.

### Status bar

A small Obsidian status-bar item shows:
- Connection state (●︎ green = online, ○︎ amber = offline)
- Queue depth ("3 pending")
- Current LLM mode ("SAFE" / "⚠ UNSAFE")
- Last sync timestamp

### File watcher + sync

Plugin uses Obsidian's `vault.on('modify' | 'create' | 'delete')` events. Periodic sync pass (configurable, default every 60s when online):

1. **Push** local changes since last sync to cloud:
   - User edits to `## User Notes` regions of any note
   - User edits to `## System Output > Generated Views` regions of entity hubs (sent as diffs, will trigger B4c user-edit-aware regen on next mention)
   - User deletions tracked as tombstones (cloud respects on regen)
2. **Pull** anything the cloud has new for this vault:
   - Captures processed on another device pointing at the same cloud
   - Queued cloud-side regenerations of hub Generated Views
3. **Drain** offline-capture queue when reconnected

### Offline capture queue

When a capture command is invoked offline, plugin saves the raw artifact (audio blob, file bytes, URL string + metadata) to `<vault>/.obsidian/plugins/thany-marcus/queue/<uuid>.json`. Plugin polls connectivity; when online, drains queue one at a time.

### Not responsible for

- Cross-OS distribution (Obsidian's platform handles it)
- Hotkey registration (Obsidian's keymap handles it; user binds whatever they want in Obsidian's UI)
- Markdown rendering (Obsidian renders)
- Graph visualization (Obsidian's native graph view renders)
- LLM inference / ASR / embeddings (all in user's cloud)
- Multi-device vault sync (that's the user's choice — Obsidian Sync, git, Syncthing, iCloud, etc.)

## 10. Capture Surfaces

Six capture pathways via the Obsidian plugin (§9):

1. **URL** — command palette dialog
2. **Voice memo** — MediaRecorder via Electron
3. **Screenshot** — Electron desktopCapturer
4. **Selected text** — operates on Obsidian's current selection
5. **Drag-drop file** — plugin intercepts editor drag-drop
6. **Clipboard** — single command that routes based on clipboard content type

All six are exposed as Obsidian commands. User binds hotkeys via Obsidian's normal keymap settings — works on Win/Mac/Linux uniformly because Obsidian handles cross-OS keymap entirely.

**Toast preview**: Obsidian's built-in `Notice` API for transient on-screen messages after each capture (auto-dismiss ~5s).

**Queue ordering: LIFO** — most recent capture processes first (preserved from 2026-05-13 Q-LIFO).

## 11. Cloud Backend

**The user's cloud is API-only.** No web client, no human-facing browser UI, no WebDAV server. As of DEC-001 (revised 2026-05-16) it is a two-tier deployment: an always-on **control plane** and an ephemeral **burst worker**.

### Stack — Control plane (always-on, 2 vCPU / 4 GB)

- ASP.NET Core 10 Minimal API
- PostgreSQL 16 + **pgvector**
- Filesystem-backed asset/artifact storage on encrypted VPS volume
- Filesystem-backed processed-Markdown backup store
- **multilingual-e5-small** ONNX loaded in-process (embeddings are cheap, stay here)
- Cheap extractors in-process: AngleSharp + Readability (URL), PdfPig (PDF), text/clipboard
- Classical D-filter (silence VAD, blur Laplacian, perceptual hash) in-process
- Caddy + Let's Encrypt for HTTPS on the user's domain
- Saga queue (Postgres-backed) + **WorkerLifecycleService** (`IHostedService`)
- Docker Compose for orchestration

### Stack — Burst worker (ephemeral, 4 vCPU / 16 GB, spawned on demand)

- **Ollama** container (default Gemma 4 E4B Q4; configurable per ADR-0035)
- **Parakeet sidecar** container (sherpa-onnx server image)
- **Worker process** (binary): polls the control plane's Postgres queue over the private network, calls local Ollama / Parakeet, writes results back. Stateless; no persistent volume.
- Docker Compose for orchestration. No Caddy, no Postgres, no public ingress.
- Lifecycle managed by the control plane; see §12.

### Endpoints

```text
# Plugin endpoints (bearer-token auth)
POST   /api/ingest                         (multipart for file; JSON for URL/text)
GET    /api/sync/pull?since=<ts>           (poll for new processed notes / regen output)
POST   /api/sync/push                      (user edits, tombstones, hub-edit diffs)
POST   /api/reprocess/{note_id}            (manual "process this note" trigger)
GET    /api/reseed                         (full snapshot of artifacts + Markdown for new device)

# Sharing
POST   /api/shares                         (create magic link + cached zip for a project)
POST   /api/shares/{id}/revoke
GET    /shared/{token}                     (public; recipient endpoint; no auth)

# Portal admin endpoints (cloud-admin-token auth)
POST   /admin/settings                     (model swap, safe/unsafe toggle, filters)
POST   /admin/plugin-tokens                (issue/revoke plugin bearer tokens)
GET    /admin/health
POST   /admin/register-with-portal         (user's cloud calls portal at first boot)
GET    /admin/audit-log
```

No `/api/auth/*` endpoints — no human-facing browser auth on the data plane.
No `/webdav/*` endpoints — the plugin uses the bearer-token API instead.
No `/api/graph` endpoint — Obsidian renders the graph from local vault files.

## 12. Processor Router and Saga (server-side)

S4 preserved from 2026-05-13. One structured LLM call per artifact: routing + entity extraction + wikilink anchor selection. Procedural Markdown assembly. Per-affected hub regen.

Saga runs as ASP.NET Core `IHostedService` background workers with a Postgres-backed queue. LIFO active ordering; failed items go to a retry queue with exponential backoff and a dead-letter destination after N retries.

**Where each step runs (DEC-001 revised 2026-05-16):**

Steps that are CPU-cheap or memory-cheap run on the **control plane**. Steps that need the LLM or ASR model loaded run on the **burst worker**. The saga step that hands off to the worker only enqueues a lease; the worker pulls the lease itself.

- Control plane (in-process or sidecar): URL fetch, HTML→Markdown, PDF text extraction, embeddings (e5-small), classical D-filter, pgvector retrieval, procedural Markdown assembly, hub regen orchestration
- Burst worker (pulled from queue): Parakeet transcription, LLM structured extraction (safe mode), LLM hub-view regen
- Unsafe mode bypasses the worker entirely — the LLM step calls the external API directly from the control plane

Final step: emit processed Markdown + assets via `/api/sync/pull` for the plugin to fetch.

### WorkerLifecycleService

A new `IHostedService` in the control plane owns the worker's lifecycle. State machine:

```text
none ──[first LLM/ASR job arrives]──► spawning
spawning ──[worker calls /admin/worker-ready within 7 min]──► alive
spawning ──[7 min timeout]──► destroying (and retry from none on next job)
alive ──[10 min with no new jobs leased]──► draining
draining ──[in-flight jobs complete or lease TTL expires]──► destroying
destroying ──[Terraform destroy returns success]──► none
```

- `none → spawning`: shell out to `terraform apply` against the worker module (PORTAL-008b / PORTAL-009b), injecting a short-lived worker registration token + the control plane's private IP + Postgres connection string + LLM model choice.
- Worker cloud-init pulls the worker Docker Compose, starts Ollama (which loads the model into RAM), starts the worker process, which POSTs `/admin/worker-ready` to the control plane.
- `alive`: worker leases LLM/ASR jobs from the Postgres queue using `FOR UPDATE SKIP LOCKED` with a 10-min lease TTL. Control plane has no direct RPC to the worker — all coordination is via shared Postgres state.
- `alive → draining`: control plane sets a `worker_should_drain=true` flag in the workers table; worker sees it on its next 30s heartbeat and stops leasing new jobs.
- `destroying`: shell out to `terraform destroy` with the same workspace. ~60s.

**Sovereignty implications:** the worker never has its own public DNS or persistent disk for user content. Inference inputs traverse the per-cloud private VPC only. After destruction, all user data lived only on the control plane's encrypted volume; the worker's ephemeral disk is gone.

**Failure semantics:** lease TTL of 10 min plus `SKIP LOCKED` means a wedged or destroyed worker can never block the queue — its leases expire and the next worker (or retry of the same spawn cycle) picks them up. The user-facing observation is increased latency, not data loss.

**UX implications:** first capture in an idle session pays a ~5 min cold-start (worker spawn + cloud-init + model load). Subsequent captures within the 10 min idle window are warm (~30 s). The plugin surfaces this via the status bar ("Worker waking up — first note ~5 min" / "Worker warm").

Routes preserved from 2026-05-13 §11 with these adjustments:
- `parakeet_transcription` calls the Parakeet sidecar HTTP endpoint *on the burst worker*
- `local_context_fetch` becomes `pgvector_context_fetch` (control plane)
- `llm_structured_extraction` runs on the burst worker (safe mode) or calls external API from the control plane (unsafe mode)

## 13. LLM Strategy

**Two modes:**

- **Safe (default)** — Ollama running on the user's **burst worker** (DEC-001 revised 2026-05-16). Default model: Gemma 4 E4B Q4 on the 16 GB worker tier. The worker is spawned on demand by the control plane and destroyed after 10 min idle. User content traverses the per-cloud private VPC only; the worker has no persistent disk for user data.
- **Unsafe (opt-in)** — external API (Anthropic or OpenAI), called directly from the control plane (bypasses the burst worker entirely). API key stored on the control plane, encrypted at rest. Full-screen consent at enable. Persistent badge in plugin status bar. Per-call audit log entry in provenance JSON.

**Cold-start UX (safe mode):**

| Capture position in session | Latency to Markdown in vault |
|---|---|
| First capture (no warm worker) | ~5 min (spawn + cloud-init + model load + inference) |
| Subsequent captures within 10 min idle window | ~30 s (worker warm) |
| Capture after 10+ min of inactivity | ~5 min again |

This is the explicit trade for ~3× lower operating cost vs. an always-on 16 GB tier. Status bar surfaces worker state so the user knows whether the next capture is cold or warm.

**Why the burst design and not always-on 16 GB:** the workload is bursty (humans don't capture continuously), so always-on inference capacity is paid-for-and-idle 95%+ of the time. Burst architecture pays ~$3–8/mo for the worker's actual usage hours instead of $84/mo for 24/7 capacity. The trade is first-capture latency, which is honestly surfaced rather than hidden. See ADR-0035 for the full argument.

C2 per-device backend lock from 2026-05-13 is dropped (only one data plane per user under this architecture). GPU mode is dropped from MVP — if user needs more horsepower, they resize their burst worker tier via the portal. Vision processing in MVP uses Tesseract + EXIF + perceptual hash (control-plane-side classical, no GPU needed).

## 14. Server-Side Subsystems

Split across the two tiers per DEC-001 (revised 2026-05-16):

**Control plane (always-on):**
- **multilingual-e5-small** ONNX in-process (embeddings)
- **pgvector** (vector index)
- **Classical D filter** (silence VAD, blur Laplacian, perceptual hash)
- **Postgres-backed saga queue** (LIFO active, exponential backoff retry, dead-letter, `FOR UPDATE SKIP LOCKED` lease semantics shared with worker)
- **WorkerLifecycleService** (`IHostedService` — state machine + TerraformRunner invocation)
- Cheap extractors (AngleSharp + Readability, PdfPig, Tesseract for image OCR baseline)

**Burst worker (ephemeral):**
- **Ollama** container (LLM)
- **Parakeet sidecar** (ASR via sherpa-onnx)
- Worker process (queue-puller, results-writer)

## 15–18. Auto-Routing, Knowledge Generation, Vault Structure, Entity Hubs

**Preserved verbatim from 2026-05-13 §§14–17** with one location change:

- The vault lives **on the user's local filesystem**, opened by Obsidian as a normal vault
- LLM auto-routing + project auto-create + Inbox-until-N=10 cold-start preserved
- User-defined entity-type folders (E5 lock) — seeded at first plugin install: `_Entities/People/Places/Concepts/Papers/Uncategorized/`
- N=3 hub trigger preserved
- Procedural note assembly preserved
- Wikilink anchor insertion at LLM-identified positions preserved
- Context vs Generated Views split in hub notes preserved

## 19. Offline + Multi-Device

### Offline

- **Reading, editing, browsing, graph-view, search** — fully offline (Obsidian + local vault, plugin not needed)
- **New captures** — plugin queues them locally in `<vault>/.obsidian/plugins/thany-marcus/queue/`; drains when online
- **User edits to existing notes while offline** — saved locally as normal; plugin file watcher detects and pushes diffs on next sync

### Multi-device

The system does NOT provide vault-level sync. The user chooses their own mechanism:

- Obsidian Sync ($8/month official)
- git
- Syncthing
- iCloud / OneDrive / Dropbox
- Self-hosted file sync (Resilio, etc.)

Each device runs its own copy of the plugin pointing at the same cloud. Captures processed on Device A are written to Device A's local vault by the plugin; the user's chosen vault-sync mechanism then propagates the resulting Markdown to Device B. The cloud is a stateless processing service per device — no multi-device state to coordinate beyond what the cloud already keeps (artifact backups, processed-Markdown backup, audit logs).

### Sync semantics (plugin ↔ cloud)

- `## User Notes` → local-wins; plugin pushes edits up as part of sync
- `## System Output` of artifact notes → no auto-regen; user edits preserved
- `## System Output > Generated Views` of entity hubs → regen on new mention; B4c user-edit-aware via diff fed to LLM prompt
- User deletions → tombstones in `.provenance/`; cloud respects on regen

## 20. Sharing Model

Plugin-initiated, cloud-served. Preserved from 2026-05-13 §19 with locations updated:

```text
user picks a project folder in Obsidian
  -> command palette: "Thany-Marcus: Share project"
  -> plugin asks cloud to generate magic link
  -> cloud generates tokenized URL + cached zip AT TOKEN-CREATION TIME
  -> zip scoped per W1+P1+F1+A1:
       W1: cross-vault wikilinks stripped to plain text
       P1: provenance stripped entirely
       F1: frontmatter whitelisted (tags, captured_at, modality)
       A1: linked assets bundled
  -> cloud returns link to plugin
  -> plugin shows modal: "Copy link or open in browser"
  -> user shares through their own channel
  -> recipient downloads zip from cloud's public /shared/{token} endpoint
  -> revocation from plugin or portal deletes cached zip and invalidates token
```

24h default expiry, configurable. No email service. No registration for recipients.

## 21. Security and Privacy (decent modern security)

### Trust model

- User trusts: their cloud provider (DO/Hetzner), Google (SSO), Let's Encrypt (TLS), the project maintainer (portal operator), Obsidian (the platform we extend), optionally OpenAI/Anthropic (unsafe mode only).
- Project maintainer sees: user identity (Google sub), provisioning metadata, encrypted provider tokens (cannot decrypt without passphrase), cloud admin tokens (low-impact), hashed recovery codes, hashed TOTP backup codes.
- Project maintainer does **not** see: user content, vault, captures, plugin bearer tokens (only hashed), share tokens.

### Authentication

- **Portal:** Google SSO + optional TOTP 2FA (`Otp.NET`)
- **Plugin → user's cloud:** static bearer token issued by portal at "Plugin Setup," shown once, hashed in cloud's auth DB, revocable from portal
- **Portal → user's cloud (admin proxy):** cloud admin token issued at provisioning, hashed in cloud's auth DB, used by portal for all app-level admin ops
- **User's cloud (data plane):** no human-facing browser auth at all — pure programmatic auth (bearer tokens) + public share-recipient access (token-in-URL)

The Q-OAuth question from earlier (shared OAuth client across user clouds, wildcard redirect URIs, etc.) is **fully dissolved** under this architecture — no Google SSO on the data plane means no per-user-cloud OAuth client registration.

### Token storage

- Cloud admin tokens: hashed in user's cloud's Postgres; plaintext exists only in portal Postgres (for proxy auth).
- Plugin bearer tokens: hashed in user's cloud's Postgres; plaintext shown once to user at issuance; portal stores nothing.
- Provider API tokens: AES-GCM with Argon2id-derived key from passphrase. Decrypted only during infra ops.
- 8 dual-use recovery codes (passphrase): hashed at rest with Argon2id; single-use.
- 8 TOTP backup codes: hashed at rest with Argon2id; single-use; separate from passphrase codes.
- TOTP shared secret: AES-GCM encrypted with portal master key in env.
- External API keys (unsafe mode): on the user's cloud, AES-GCM encrypted with server-side master key.

### Transport

- HTTPS everywhere (Caddy + Let's Encrypt on both portal and user's cloud)
- No SSH tunnels
- No WebDAV (plugin uses HTTPS REST)

### At-rest

- VPS volumes: provider-native encryption (Terraform flag on DO + Hetzner)
- Portal DB + user cloud DB: encrypted at rest at the volume layer

### Rate limits + abuse

- Portal: per-Google-account rate limit on cloud creation (default max 3 active clouds per user)
- User's cloud: per-bearer-token rate limit on `/api/ingest`
- Public `/shared/{token}` endpoint: per-IP rate limit + token-existence check (no enumeration of tokens)

### Audit trail

- Provenance JSON per artifact (preserved from 2026-05-13 §21)
- Portal logs: cloud provision, cloud destroy, infra resize, admin proxy calls, plugin token issuance/revocation
- User's cloud logs: ingest events, LLM calls with backend tag, shares created/revoked

## 22. Provenance Schema

Preserved from 2026-05-13 §21. Minor field changes:

- `device_id` → `device_id` retained, populated by plugin (each plugin install has a UUID)
- `primary_backend`: `safe_ollama | unsafe_anthropic | unsafe_openai`
- `tier` field removed
- `served_by_cloud_id` field added for portal-side cross-cloud eval aggregation

## 23. Deployment (Terraform-driven)

### Portal deployment (Boiko's responsibility)

- Single small DO droplet (s-1vcpu-2gb, ~$12/month or s-1vcpu-1gb at ~$6/month)
- Docker Compose: `portal-api`, `portal-web`, `caddy`, `portal-postgres`
- Caddy + LE for HTTPS at `app.thany.click`
- Terraform CLI installed on the host
- Maintained in a public Git repository; updates via `git pull && docker compose up -d`

### User's cloud deployment (portal-orchestrated, Terraform-driven)

Portal flow per user-cloud:

1. Portal creates a Terraform workspace for the user-cloud, scoped in portal Postgres via Terraform's `pg` backend.
2. Portal selects the `.tf` module for the user's chosen provider (`infra/terraform/digitalocean/` or `infra/terraform/azure/`).
3. Portal generates random subdomain `<random8>.thany.click` and calls Cloudflare API to create A record pointing at the future droplet/VM IP (placeholder; updated post-Terraform).
4. Portal injects variables into Terraform: provider credentials, subdomain, droplet/VM size, region, SSH key (auto-generated per cloud), cloud-init user-data containing the bootstrap script.
5. Portal runs `terraform apply -auto-approve` via subprocess; captures outputs (droplet/VM IP, etc.).
6. Portal calls Cloudflare API again with the actual IP to update the A record.
5. Cloud-init on the droplet:
   - Installs Docker
   - Pulls Docker Compose YAML from project's release URL
   - Generates `.env` (cloud admin token, JWT signing key, OAuth client creds for portal callback, domain)
   - `docker compose up -d`
   - Caddy acquires LE cert for the supplied domain
   - On first boot, cloud's API POSTs cloud admin token to the portal's registration endpoint
6. Portal marks cloud ready, generates plugin bearer token, redirects user to "Plugin Setup" page.

### Adding a new provider post-thesis

1. Write `infra/terraform/<provider>/` `.tf` module (compute resource + networking + DNS/IP exposure as appropriate to the provider's model)
2. Register the provider name + size mapping in portal's UI dropdown
3. Add provider credential schema (single token vs service principal vs other) to portal's credential vault
4. Test with a fresh user-cloud
5. Done — minimal .NET code changes (just the credential schema)

### Destroy flow

- User clicks "Destroy" in portal
- Portal prompts for passphrase, decrypts provider token in memory
- Portal runs `terraform destroy` in the cloud's workspace
- Portal deletes its records of the cloud
- Local vault on user's machine survives untouched (preserved promise)
- Manual DNS cleanup notice for the A-record (auto-DNS-cleanup is future work)

### Onboarding wizard (3 screens)

| Screen | Content | User input |
|---|---|---|
| 1. **Cloud target** | Pick provider + paste provider API token + paste domain | 3 fields |
| 2. **Security** | Set passphrase + see 8 recovery codes | 1 field + acknowledge codes |
| 3. **Provisioning** | Live progress: terraform plan → apply → cloud-init → Docker pull → model download → ready | None — wait |

Optional 2FA setup is offered in Account Settings post-onboarding.

After provisioning completes, a "Plugin Setup" page shows:
- The bearer token (one-time copy)
- The cloud URL
- Step-by-step instructions to install the Obsidian plugin and paste both into Settings

## 24. Evaluation Plan

Preserved verbatim from 2026-05-13 §23:

1. Routing accuracy (top-1, top-3) vs B0–B4 baselines
2. Entity dedup precision/recall
3. D filter precision/recall
4. Wikilink precision-at-K
5. 4–6 week qualitative diary

Eval corpus collection: ~150–200 self-collected artifacts over 3–4 weeks of organic use. `eval_corpus_label` field in provenance JSON populated post-hoc. n=1 personal study explicitly acknowledged.

## 25. MVP Scope

**Portal:**
- ASP.NET Core 10 + Blazor (or vanilla SPA)
- Google SSO + per-user identity
- Optional TOTP 2FA with 8 single-use backup codes
- Terraform CLI integration: per-user workspaces, `pg` backend for state
- `.tf` modules for DO + Azure; documented path for adding more
- Cloudflare DNS API client (per-cloud subdomain A-record management on `thany.click`)
- Cloud lifecycle: provision, configure (proxied), resize, destroy
- Plugin bearer-token issuance + revocation UI
- App-level settings UI (proxied to user's cloud admin endpoints)
- Passphrase-encrypted provider token vault (Argon2id + AES-GCM)
- 8 dual-use recovery codes for passphrase recovery
- TOTP shared-secret storage encrypted at rest

**User's cloud — control plane (per instance, always-on, API-only data plane):**
- ASP.NET Core 10 API + Docker Compose stack
- Postgres 16 + pgvector
- multilingual-e5-small in-process (embeddings)
- Cheap extractors in-process (AngleSharp, Readability, PdfPig, Tesseract)
- Caddy + Let's Encrypt
- Plugin endpoints with bearer-token auth
- Portal admin endpoints with cloud admin token auth
- Worker-registration endpoint (short-lived token issued at spawn)
- Public share-recipient endpoint
- Server-side saga (Postgres-backed queue, LIFO, exponential backoff retry, dead-letter, lease semantics shared with burst worker)
- WorkerLifecycleService (`IHostedService`): state machine + Terraform invocation
- Procedural Markdown assembly + entity-hub regen with B4c
- LLM auto-routing with project auto-create
- User-defined entity-type folders (E5)
- N=3 hub trigger + Context + Generated Views
- Tombstones for user deletions
- Provenance JSON (§22 schema)
- Per-project sharing with W1+P1+F1+A1 stripping
- Safe/unsafe mode toggle + consent UX + audit log

**User's cloud — burst worker (per instance, ephemeral, spawned on demand):**
- Docker Compose stack: Ollama (Gemma 4 E4B Q4 default) + Parakeet sidecar + worker process
- Worker process: pulls saga LLM/ASR jobs from control-plane Postgres over private VPC, runs inference, writes results back
- No public ingress; firewall locks to control plane's private IP
- 10 min idle timeout → self-shutdown handshake → Terraform destroy
- Cold-start ~5 min on first capture per session; warm ~30 s thereafter
- Hosts the single structured LLM call per artifact (S4) — runs here in safe mode

**Obsidian plugin:**
- TypeScript against Obsidian Plugin API
- 6 capture commands (URL, voice, screenshot, selected text, drag-drop, clipboard)
- Settings UI (cloud URL, bearer token, LLM mode, default folders, entity-type folders)
- File watcher + sync engine (push edits, pull new processed content, drain offline queue)
- Status bar (connection state, queue depth, LLM mode badge)
- Offline-capture queue
- "Re-seed from cloud" command for new-device install / recovery
- Per-note "Process through cloud" command for user-authored notes

**Deferred / Future work:**
- Mobile via Obsidian Mobile + plugin (clear extension path; one prerequisite is making the plugin `isDesktopOnly: false` and porting capture commands to mobile-capable APIs)
- GPU mode for vision (Tesseract baseline suffices for MVP)
- Auto-DNS cleanup on destroy (currently the Cloudflare A-record is auto-cleaned because portal owns the zone; only BYO-domain path needs manual cleanup)
- Vault-QA via cloud LLM ("ask my vault")
- Multi-user collaboration
- CRDT editing
- Additional cloud providers beyond DO + Azure (Hetzner, Vultr, Linode, OVH — Terraform-ready, ship post-thesis)
- Obsidian community plugin store listing (post-thesis verification + submission)
- Plugin auto-update via Obsidian's plugin store (works automatically once listed)

## 26. Five Functional Requirements (thesis framing)

| FR | Chapter title | Named third-party integrations |
|---|---|---|
| **FR1** | Knowledge-cloud management platform (portal): control plane with Google SSO, optional TOTP 2FA, Terraform-driven multi-provider provisioning | Google SSO, Otp.NET (TOTP), Terraform + DO/Azure providers, Cloudflare DNS API, Docker Compose, cloud-init, Caddy + Let's Encrypt, Argon2id + AES-GCM |
| **FR2** | Obsidian plugin for ambient capture and processing | Obsidian Plugin API (TypeScript), Electron desktopCapturer, MediaRecorder, HTML5 drag-and-drop |
| **FR3** | In-cloud multimodal processing service | sherpa-onnx (Parakeet ASR), Ollama (LLM), multilingual-e5-small, pgvector, AngleSharp + Readability, PdfPig |
| **FR4** | Local Obsidian vault with emergent knowledge graph rendered by native graph view | Obsidian (as platform we extend), Obsidian's native graph view |
| **FR5** | Safe / unsafe LLM mode + per-project magic-link sharing | OpenAI / Anthropic API, tokenized URL sharing pattern |

Plus eval chapter (§24) and future-work chapter (§25). Six thesis chapters from FRs + intro/arch/methodology/conclusion = a real thesis structure.

## 27. Repository Structure

```
Thany-Marcus/
├── ThanyMarcus.sln                          # .NET solution (portal + cloud)
├── README.md
├── src/
│   ├── ThanyMarcus.Portal.Api/              # ASP.NET Core, .NET 10
│   │   ├── Features/
│   │   │   ├── Auth/                        # Google SSO + TOTP 2FA
│   │   │   ├── Provisioning/                # Terraform subprocess + workspaces
│   │   │   ├── CloudManagement/             # proxy to cloud admin
│   │   │   ├── PluginTokens/
│   │   │   ├── Destroy/
│   │   │   └── RecoveryCodes/
│   │   └── Infrastructure/{Database,Crypto,TerraformRunner}/
│   ├── ThanyMarcus.Portal.Web/              # Blazor or static SPA
│   ├── ThanyMarcus.Cloud.Api/               # ASP.NET Core, runs on user VPS
│   │   ├── Features/
│   │   │   ├── PluginAuth/                  # bearer-token validation
│   │   │   ├── Ingest/{Url,Pdf,Audio,Image,Text,DragDrop}/
│   │   │   ├── Processing/{Saga,RoutingExtraction,NoteAssembly,HubRegen,Provenance}/
│   │   │   ├── Knowledge/{Entities,Hubs,Wikilinks,Routing}/
│   │   │   ├── Sync/{Pull,Push,Reseed}/
│   │   │   ├── Sharing/{Create,ZipBuild,Recipient,Revoke}/
│   │   │   ├── Settings/{LlmMode,ModelChoice}/
│   │   │   └── Admin/                       # endpoints proxied from portal
│   │   └── Infrastructure/{Database,Storage,OllamaClient,ParakeetClient,Embedding,VectorIndex}/
│   ├── ThanyMarcus.Plugin/                  # TypeScript Obsidian plugin
│   │   ├── manifest.json
│   │   ├── main.ts                          # plugin entry
│   │   ├── commands/{CaptureUrl,CaptureVoice,CaptureScreenshot,CaptureSelection,CaptureClipboard,DragDrop,Reseed,Reprocess}.ts
│   │   ├── settings/SettingsTab.ts
│   │   ├── sync/{Push,Pull,OfflineQueue}.ts
│   │   ├── api/CloudClient.ts               # bearer-token HTTP client
│   │   └── ui/{StatusBar,Notices,Dialogs}/
│   └── ThanyMarcus.Shared/                  # contracts shared by portal + cloud (TS types hand-written per ADR-0025; no codegen)
├── tests/
│   ├── ThanyMarcus.Portal.Tests/
│   ├── ThanyMarcus.Cloud.Tests/
│   ├── ThanyMarcus.Plugin.Tests/            # plugin unit tests + Obsidian-API mocks
│   ├── ThanyMarcus.IntegrationTests/
│   └── ThanyMarcus.EvalTools/
├── infra/
│   ├── terraform/
│   │   ├── digitalocean/                    # DO droplet module
│   │   ├── azure/                           # Azure RG + VNet + NSG + VM module
│   │   └── shared/                          # cloud-init script template (provider-agnostic)
│   └── docker/
│       ├── portal/docker-compose.yml
│       └── cloud/docker-compose.yml          # pulled by cloud-init at provisioning
├── docs/
│   ├── architecture.md                       # mirrors this plan
│   ├── decisions/                            # ADRs 0001+; 0017–0028 for cloud-pivot decisions
│   ├── api.md
│   ├── plugin-setup.md                       # how to install + configure the plugin
│   └── onboarding.md                         # portal onboarding flow
├── plans/
│   ├── consolidated-plan-2026-05-12.md       # pre-second-grilling (preserved)
│   ├── consolidated-plan-2026-05-13.md       # locked plan (preserved; superseded by this doc on architecture)
│   ├── cloud-pivot-plan-2026-05-13.md        # this document
│   └── final-expanded-project-plan-2026-05-12.md
└── .github/workflows/
    ├── portal-ci.yml
    ├── cloud-ci.yml
    ├── plugin-ci.yml                         # build + test the TypeScript plugin
    └── release.yml                           # builds portal/cloud Docker images + plugin GitHub release
```

## 28. Open Design Questions

Resolved by this revision:
- ~~Q2 Offline capture~~ — resolved: native offline Obsidian use + plugin offline-capture queue
- ~~Q3 Latency UX without system tray~~ — resolved: Obsidian status bar + Notice API
- ~~Q-OAuth Google OAuth client topology~~ — resolved: no Google SSO on data plane
- ~~Q8 Capture surface scope~~ — resolved: 6 plugin commands via Obsidian command palette

Still open:

### Tier A — affect core claims, must resolve before implementation

- ~~**Q4. VPS minimum spec.**~~ **Resolved (DEC-001, revised 2026-05-16 — see ADR-0035):** Two-tier burst architecture. **Control plane (always-on):** 2 vCPU / 4 GB — DO `s-2vcpu-4gb` (~$24/mo), Azure `B2s` (~$30/mo), Hetzner `CPX21` (~€5/mo). **Burst worker (ephemeral, spawned on demand):** 4 vCPU / 16 GB — DO `s-4vcpu-16gb` ($0.125/hr), Azure `B4ms` (~$0.166/hr), Hetzner `CPX41` (~€0.05/hr). Realistic monthly cost: ~$28–32 DO, ~$35–40 Azure, ~€11 Hetzner. Original always-on 16 GB / 4 vCPU spec ($84–120/mo) rejected as not cost-credible for a single-tenant sovereign-LLM deployment.
- **Q-CloudAccess.** Is the user's cloud single-user (only one Google identity can issue plugin tokens), or can multiple Google identities share one cloud? My lean: single-user in MVP.
- **Q-DNS-cert.** What if DNS hasn't propagated when LE tries to issue the cert? Retry strategy + user-visible error state.
- **Q-Name.** Project name + portal domain. "Thany-Marcus" is the working name; final?
- **Q-ImageDistribution.** Where do user clouds pull Docker images from? GitHub Container Registry (public, free)? Image signing strategy?

### Tier B — design refinements

- **Q5. Vault durability.** Local-vault model already gives "vault survives cloud destroy." Open: does the cloud need scheduled backup of the user's vault as a separate safety net (e.g., periodic pg_dump + asset rsync to user-supplied S3-compatible storage)?
- **Q6. Safe/unsafe granularity.** Per-vault toggle + persistent badge + audit log (lock in)?
- **Q7. GPU mode.** Confirmed dropped from MVP; Tesseract baseline for vision.
- **Q12. User cloud DB backup.** Provider-native snapshots (cheap, automatic) + user-triggered backups (additional)?
- **Q-2FA-scope.** Portal-only TOTP (locked); no separate TOTP on user's cloud (covered by bearer-token + provider's Google SSO).
- **Q-Updates.** How do user clouds receive new Docker image versions? Portal-triggered (user clicks "Update" → portal docker-compose pulls latest → restart) vs auto-pull on cloud reboot.
- **Q-VaultSync.** Do we recommend a specific vault-sync mechanism to users (Obsidian Sync, git, Syncthing), or stay neutral?
- **Q-Resilience.** Saga failure modes — when Ollama crashes mid-call, when JSON output is malformed, retry caps, dead-letter handling.
- **Q-Scale.** pgvector behavior at 10k+ notes; index tuning.
- **Q-Limits.** Max upload size per artifact; max audio length for Parakeet; max vault size.
- **Q-FileSecurity.** Anti-malware on uploaded files. ClamAV in a sidecar, or trust the user?
- **Q-Region.** Region selection UX in the onboarding wizard.
- **Q-Sizing.** Cross-provider size enum (Small/Medium/Large) vs pass-through native names.
- **Q-ModelSwap.** Behavior when user changes Ollama model mid-flight (drain queue first, or restart and let in-flight fail?).
- **Q-Secrets.** Distribution of JWT signing key, cloud admin token, etc. into cloud-init without logging.

### Tier C — polish, edge cases

- **Q-Crosslang.** Cross-language entity matching (multilingual-e5-small handles partially).
- **Q-ShareUX.** Recipients get a zip. Add a small static HTML render for Obsidian-less viewers?
- **Q-Telemetry.** Does Boiko collect any telemetry from user clouds? Probably no — privacy. Confirm.
- **Q-Legal.** Portal Terms of Service / Privacy Policy. Bachelor's-thesis-scope wording.
- **Q-ObsidianBoot.** How much hand-holding in the README for Obsidian-newcomers.
- **Q-AccountDeletion.** Portal account deletion semantics. Block while clouds exist?
- **Q-AuditRetention.** Audit-log retention period.
- **Q-CompromiseRecovery.** Cloud-compromise recovery playbook.
- **Q9. Demo / milestones.** Concrete defense storyboard.
- **Q13. Portal abuse limits.** Default 3 clouds per Google account (lock in)?

---

## Appendix A: What survives unchanged from `consolidated-plan-2026-05-13.md`

- §6 main contribution items 3 (emergent knowledge graph), 4 (URL snapshotting)
- §11 S4 lock (one structured LLM call + per-hub regen; no LLM note-gen)
- §14 LLM auto-routing + Inbox-until-N=10 cold-start + project auto-create
- §15 procedural Markdown assembly + `## System Output` / `## User Notes` convention
- §16 vault structure (Inbox/Projects/_Entities/.system/.provenance)
- §17 entity hubs (E5 user-defined types + N=3 threshold + Context/Generated Views split)
- §19 sharing W1+P1+F1+A1 stripping
- §21 provenance JSON schema (with field changes per §22)
- §23 evaluation methodology (4 quant + 1 qual)
- ADRs 0001–0016 as appendix evidence (where their decisions still hold)

## Appendix B: What changes vs `consolidated-plan-2026-05-13.md`

- §7 architecture: portal + user-VPS + Obsidian plugin + local vault, replaces Avalonia + thin cloud
- §8: Avalonia client → Obsidian plugin (TypeScript) + provisioning portal as the platform
- §9: 6 desktop hotkey/button surfaces → 6 Obsidian commands via command palette + user-bound hotkeys (cross-OS via Obsidian)
- §10: thin cloud transport → full data-plane cloud with LLM/ASR/embeddings, **API-only**, no web client, no WebDAV
- §12: {L, CC, API} backends → {safe in-cloud, unsafe external API}
- §13: local LLamaSharp + sqlite-vec → Ollama container + pgvector
- §18: M2 multi-device re-embed-on-pull → user's own choice of vault-sync mechanism; cloud is stateless processing per device
- §20: SSH-tunnel Tier 1 vs HTTPS Tier 2 → always HTTPS via Caddy + LE on the user's own domain
- §21: passphrase + recovery codes → adds optional TOTP 2FA on portal; adds plugin bearer-token auth
- §22 deployment: Avalonia-driven Terraform → portal-driven Terraform (subprocess from .NET, per-user workspaces in `pg` backend)
- Q1 + Q10 stack: SharpHook / PortAudio / LLamaSharp / sqlite-vec / SSH.NET dropped
- Q15 onboarding: 5-screen Avalonia → 3-screen portal + Plugin Setup page
- New: mobile and PWA dropped entirely (mobile reachable via Obsidian Mobile + plugin in future)
- New: vault canonical location flips back to LOCAL (preserving the locked plan's "local vault survives cloud destroy" promise via Obsidian's native local-vault model)
- New: multi-provider abstraction via Terraform `.tf` modules (DO + Azure in MVP — intentionally heterogeneous platforms)
- New: per-cloud subdomain on Boiko-owned `thany.click` via Cloudflare DNS API (user does no DNS setup)

## Appendix C: ADRs to author for this revision

Continuing numbering from 0017:

- ADR-0017: Cloud-pivot — drop local-first Avalonia in favor of cloud-side processing service
- ADR-0018: Knowledge-cloud management platform (portal) as the control plane
- ADR-0019: Terraform-driven multi-provider provisioning (replaces hand-rolled REST clients) — DO + Azure in MVP, intentionally heterogeneous to stress the abstraction
- ADR-0031: Platform-owned domain `thany.click` with per-cloud random subdomain via Cloudflare DNS API (user does zero DNS configuration; BYO-domain optional in advanced settings)
- ADR-0020: Drop M2 multi-device coordination — user's choice of vault-sync mechanism
- ADR-0021: Safe / unsafe LLM mode (collapsed from L/CC/API)
- ADR-0022: pgvector restored (Q1 cloud-zero-semantic-state lock reversed)
- ADR-0023: Two-credential portal: cloud admin token + passphrase-encrypted provider token
- ADR-0024: Obsidian plugin as the user-facing capture surface (replaces browser web app, replaces Avalonia client)
- ADR-0025: Local vault canonical; cloud as backup + processing service
- ADR-0026: Optional TOTP 2FA on portal login; no 2FA on user's cloud (covered by bearer-token + provider-side Google 2FA if user has it on)
- ADR-0027: Plugin bearer-token auth between plugin and user's cloud
- ADR-0028: Drop Google SSO on user's cloud; data plane has zero human-facing browser auth; Q-OAuth dissolved
- ADR-0029: Drop mobile and PWA from MVP; Obsidian Mobile + same plugin is the future-work mobile path
- ADR-0030: Drop Cytoscape.js graph viewer — Obsidian's native graph view is the rendering surface

**Plan status:** architecture locked at the level resolved during grilling so far. Tier-A open questions (§28) need explicit decisions before implementation begins. Tier-B and Tier-C items can be drafted as defaults and revisited mid-implementation.
