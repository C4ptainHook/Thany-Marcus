# Refined Thesis Plan

Date: 2026-05-04

Status: working source of truth after design grilling.

## Working Thesis Title

**A Distributed Privacy-Aware Context Capture System for Obsidian-Compatible Personal Knowledge**

Alternative title:

**Selective Multimodal Context Capture and User-Owned Cloud Curation for Markdown Knowledge Vaults**

## Core Thesis Framing

The project is a distributed personal knowledge capture system where:

- a cross-platform desktop capture agent performs selective local capture and preprocessing
- a browser extension provides rich web context capture
- a user-owned cloud backend performs durable processing, indexing, backup, and recovery
- an Obsidian plugin acts as the view/review/write client for Markdown knowledge artifacts

The system targets Obsidian-compatible Markdown because it is human-readable, graph-friendly, and user-owned.

## Final Novelty Statement

Existing tools tend to focus on one of these categories:

- ambient capture, such as Screenpipe or Recall-like systems
- generic self-hosted agent runtimes, such as OpenClaw, ZeroClaw, Goose, and Open Interpreter
- self-hosted second-brain search/chat, such as Khoj
- Obsidian AI vault agents/plugins
- Markdown semantic search, such as QMD

This work instead focuses on:

> **A distributed selective-capture architecture where a local agent performs privacy-aware pre-upload triage, a trusted user-owned cloud backend performs durable processing/recovery, and Obsidian receives policy-governed Markdown knowledge artifacts.**

The intended contribution is not a generic AI assistant, not ambient recording, and not another Obsidian AI writer. The contribution is the capture-to-cloud-to-vault architecture, policy model, local/cloud preprocessing split, and recovery/write workflow.

## Agreed Scope

### Core

- selective context capture
- local preprocessing
- user-owned cloud backend
- cloud processing queue
- cloud backup/recovery
- Obsidian-compatible Source and Knowledge notes
- policy-driven vault writes
- Terraform deployment for DigitalOcean and Hetzner

### Implemented Vertical Slice

The thesis implementation should prove the architecture through a thin vertical slice:

- browser text/link capture
- screenshot/image capture
- Tauri/Svelte desktop capture agent
- local preprocessing with deterministic checks and optional tiny local model
- C# cloud backend with queue and processing worker
- cloud model server abstraction
- Obsidian plugin review/write client
- selected-folder backup/recovery
- Terraform for DigitalOcean and Hetzner VPS deployment

### Designed But Not Necessarily Fully Implemented

- audio capture
- short-video processing
- deep Slack/Telegram/WhatsApp integrations
- full vault sync
- Git versioning
- Kubernetes scale-out
- ZeroClaw/OpenClaw/Goose integration
- advanced contradiction/staleness detection

## Explicit Exclusions

The MVP should not become:

- a general personal AI assistant
- an arbitrary tool-executing agent runtime
- a skill marketplace
- an always-on screen/audio recorder
- a full Obsidian replacement
- a chat-with-vault product as the main feature
- a real-time multi-device sync engine
- a Kubernetes platform project
- an n8n workflow product

## Architecture

```text
Browser Extension
        |
        v
Tauri/Svelte Desktop Capture Agent
        |
        | local journal + local preprocessing + preview
        v
User-Owned Cloud Backend
        |
        | queue + object store + DB + worker + model server
        v
Generated Source/Knowledge Proposals + Backup/Recovery Store
        |
        v
Obsidian Plugin
        |
        | policy validation + write plans
        v
Obsidian Vault
```

## Component Responsibilities

### Desktop Capture Agent

Technology:

- Tauri + Svelte
- Rust only for native bridge/capabilities

Responsibilities:

- tray/status UI
- global hotkey
- clipboard/selected text capture where feasible
- screenshot/image/file intake
- local capture journal
- local pre-upload preview
- deterministic local preprocessing
- optional tiny local model integration
- upload queue to cloud backend
- backend connection settings

Non-responsibilities:

- final note writing
- heavy AI processing
- vector indexing
- Obsidian vault writes
- full review/editor UI

### Browser Extension

Responsibilities:

- selected text
- page URL
- page title
- surrounding DOM context
- selected links/images where feasible
- send capture to local Tauri agent by default

Default path:

```text
browser extension -> local Tauri agent -> local preview/preprocessing -> cloud queue
```

Direct-to-cloud is advanced/future setting, not default.

### Cloud Backend

Technology:

- ASP.NET Core API
- .NET worker / background service
- PostgreSQL + pgvector
- filesystem-backed object storage on VPS volume for MVP
- Caddy reverse proxy
- model server abstraction
- Docker Compose runtime

Responsibilities:

- authentication
- receive uploaded captures
- durable queue state
- raw/derived artifact storage
- selected-folder vault backup
- explicit restore support
- model processing
- OCR/caption/summarization where implemented
- embeddings/vector index
- generated Source/Knowledge proposals
- write plans
- audit logs
- policy source of truth

### Model Serving

Default:

- local-to-VPS model server
- no third-party model API by default

Supported model-server paths:

- Ollama HTTP
- llama.cpp HTTP/OpenAI-compatible server
- vLLM OpenAI-compatible HTTP as optional GPU path

Rejected for MVP:

- making gRPC the default model protocol
- third-party cloud model calls by default

Reason:

- Ollama, llama.cpp, and vLLM commonly expose HTTP/OpenAI-compatible APIs.
- HTTP is easier to debug, deploy, and swap providers.
- gRPC can be future optimization for custom internal services.

### Obsidian Plugin

Responsibilities:

- authenticate to user-owned backend
- display processing queue/status
- display generated Source/Knowledge notes
- apply policy-governed write plans
- write Markdown files into the vault
- upload selected vault folders for backup/indexing
- restore selected files/folders from cloud
- show digest/review queue for risky items

Non-responsibilities:

- global capture
- media processing
- model serving
- long-running uploads
- arbitrary tool execution

## Capture Envelope

The capture envelope is a core artifact and API contract.

Example:

```json
{
  "capture_id": "cap_2026_05_04_abc123",
  "created_at": "2026-05-04T12:00:00+03:00",
  "source": {
    "kind": "browser_selection",
    "app": "Chrome",
    "url": "https://example.com/article",
    "title": "Example Article",
    "window_title": "Example Article - Chrome",
    "selection": "selected text",
    "surrounding_context": "nearby page context",
    "attachments": []
  },
  "artifacts": [
    {
      "kind": "screenshot",
      "local_path": "/local/path/cap.png",
      "sha256": "hash",
      "mime_type": "image/png",
      "size_bytes": 12345
    }
  ],
  "user_intent": {
    "note": "remember this for thesis",
    "target_project": "Thesis",
    "tags": ["research"]
  },
  "local_preprocess": {
    "language": "en",
    "source_type": "web_article",
    "sensitivity": "work",
    "pii_risk": "low",
    "recommended_policy": "cloud_full",
    "requires_review": false,
    "model_used": "rules+local_model"
  },
  "policy": {
    "upload_policy": "cloud_full",
    "processing_policy": "cloud_local_model",
    "write_policy": "auto_ai_inbox"
  }
}
```

## Local Preprocessing

Default local preprocessing is hybrid:

```text
deterministic detectors -> optional tiny local model -> policy engine -> local preview
```

Always-on deterministic checks:

- language detection
- regex/rule-based PII and secret detection
- source type heuristics
- file metadata
- policy checks

Optional tiny local model:

- privacy/source classification
- short metadata extraction
- local preview summary
- upload recommendation signal

Important:

- LLM does not make final high-risk decisions.
- Policy engine makes final upload/review/block decision.
- Tiny local model is optional and can be configured via local Ollama/llama.cpp HTTP endpoint.

## Cloud Privacy/Security Model

The system is **not zero-knowledge cloud**.

It is:

> trusted user-owned cloud, hardened by default.

Claims:

- no third-party SaaS operator receives notes by default
- user controls cloud account and provider keys
- cloud backend can see plaintext when it must process it
- third-party model calls are disabled by default
- risky capabilities are explicit opt-in
- all uploads, processing, model calls, and write plans are auditable

Hardened defaults:

- TLS required
- authentication required
- no public registration
- no third-party model providers enabled by default
- no arbitrary tool execution
- no skill marketplace
- provider firewall/UFW
- SSH hardening
- fail2ban
- unattended upgrades
- minimal inbound ports
- audit logs

## Policy Model

Policy source of truth:

- cloud backend

Local enforcement:

- desktop agent caches upload-related policy
- Obsidian plugin caches write/backup policy
- local hard overrides can block upload/write before cloud sees or modifies data

Policy format:

- TOML for local/user-editable/exported policy
- JSON for API/internal representation
- no DSL in MVP

Minimum upload policies:

- `cloud_full`
- `cloud_no_raw`
- `local_only`
- `encrypted_backup_only`

Minimum write policies:

- new Source notes: auto or review depending on source
- new Knowledge notes: auto-write to AI Inbox by default if low risk
- modify manual notes: review
- update unchanged generated blocks: auto possible
- user-edited generated note: review
- delete/merge: review
- policy violation: block

Example TOML:

```toml
[upload.defaults]
policy = "cloud_full"
require_preview = true

[source.browser]
upload_policy = "cloud_full"
auto_upload = true
auto_write = "ai_inbox"

[source.screenshot]
upload_policy = "cloud_no_raw"
require_preview = true

[sensitivity.credentials]
action = "block"

[sensitivity.personal_data]
action = "review"

[write.new_source_notes]
action = "auto"

[write.new_knowledge_notes]
action = "auto_ai_inbox"

[write.modify_manual_notes]
action = "review"

[write.delete_or_merge]
action = "review"

[providers]
third_party_models = false
cloud_local_model = true
desktop_local_model = true
```

## Cloud-To-Vault Write Model

Cloud does not directly write the local vault. It creates **write plans**.

Obsidian plugin:

- pulls write plans
- validates them against local vault state and policy
- applies safe ones
- shows review for risky ones
- reports status back to cloud

Default UX:

- new generated notes can auto-write to `AI Inbox/`
- risky items go into review
- digest review shows created/review/blocked items
- modifications to existing notes are more restricted

Action classes:

- `auto_create_new_ai_inbox_note`
- `auto_create_source_note`
- `auto_append_source_reference`
- `auto_update_generated_block`
- `review_semantic_patch`
- `review_modify_manual_note`
- `review_merge_duplicate`
- `block_policy_violation`

## Note Ownership And Update Safety

Generated notes are user-owned after writing to Obsidian.

Rules:

- user can freely edit generated notes
- manual edits win
- cloud model recommends actions/patches
- deterministic policy and plugin validation decide whether to apply, review, or block
- user text outside system-owned regions is not auto-deleted or auto-modified

Update safety mechanism:

- base revision hashes
- generated block ownership markers
- three-way merge classification
- patch validation

Example generated block:

```markdown
<!-- tm:block:start id=block_123 base_hash=abc123 -->
Generated text here.
<!-- tm:block:end id=block_123 -->
```

If user edits inside the block, current hash differs from base hash, so future automatic rewrite requires review.

QMD comparison:

- QMD is mainly semantic search/indexing.
- It likely does not solve LLM patch safety.
- This project needs explicit ownership/hash/merge policy.

## Markdown Artifacts

Raw captures do not all go directly into the vault.

Artifact layers:

1. raw capture store
   - outside vault
   - local/cloud object storage
   - raw media/files/html/context

2. Source notes
   - provenance
   - transcript/OCR/extracted context
   - links to raw artifacts

3. Knowledge notes
   - curated facts/summary/insights
   - Obsidian frontmatter
   - wikilinks/tags
   - generated into AI Inbox or target folder according to policy

## Backup And Recovery

The system provides:

> policy-driven backup and explicit recovery, not real-time sync.

Full recovery is in scope, but controlled:

- user selects vault folders for backup
- exclusions are applied
- restore is explicit and previewed
- no automatic cloud-to-local overwrite in MVP

Included by default:

- selected Markdown notes
- generated Source/Knowledge notes
- selected attachments if enabled
- captures/artifacts according to policy
- audit logs

Excluded by default:

- `.obsidian/workspace*`
- `.obsidian/cache`
- plugin secrets
- `.git`
- `.thany/cache`
- local-only folders/notes
- excluded raw captures
- huge media unless explicitly included

Object storage design:

```text
/data/
  objects/
    captures/
    vaults/
    artifacts/
  postgres/
```

DB stores:

- file path
- content hash
- object key
- size
- mtime
- sync/backup policy
- deleted flag
- version number
- snapshot ID

MVP object storage:

- filesystem-backed object store on VPS volume

Future:

- S3/MinIO/Backblaze adapter

## Git Versioning

Git is optional.

Modes:

1. no Git
   - internal `.thany/snapshots/` fallback

2. use existing Git repo
   - detect `.git`
   - optionally commit after cloud-applied update batches

3. initialize Git
   - explicit user approval
   - creates `.gitignore`
   - baseline commit
   - batch commits after cloud writes

Git is not required for MVP. It is an enhanced rollback/diff mechanism.

## Cloud Deployment

Runtime:

- Docker Compose on a hardened VPS

IaC:

- Terraform for DigitalOcean
- Terraform for Hetzner
- same Docker Compose stack on both

Kubernetes:

- future scale-out path
- not MVP

Reason:

- thesis problem is capture/curation/policy/recovery
- Kubernetes adds operational complexity and provider-specific issues
- personal user-owned cloud should be simple to deploy

## Thany-Marcus Repo Influence

Old repo:

- https://github.com/C4ptainHook/Thany-Marcus

Useful inheritance:

- Terraform experience
- hardened cloud-init
- Docker Compose bootstrap
- UFW/fail2ban/unattended upgrades
- queue-mode processing thinking
- Postgres/Redis separation
- monitoring/load-testing mindset
- n8n-style self-host product thinking

Do not carry forward directly:

- n8n as core backend
- Kubernetes as MVP
- nginx vs HAProxy comparison as core thesis
- Telegram bot as primary capture path

New backend should be purpose-built. n8n can be future integration/inspiration.

## Backend Stack

Accepted stack:

- ASP.NET Core API
- .NET worker/background service
- PostgreSQL + pgvector
- DB-backed queue first
- filesystem object store first
- Caddy reverse proxy
- model server via HTTP
- Docker Compose

Optional/future:

- Redis queue
- MinIO/S3
- Qdrant
- Kubernetes
- Prometheus/Grafana

## Research Questions

1. How can selective personal context capture be split between a local preprocessing agent and a user-owned cloud backend while preserving explicit data-flow control?

2. Can captured browser and image/screenshot context be transformed into useful, reviewable Obsidian-compatible Source and Knowledge notes?

3. Which preprocessing tasks can a tiny local model or local deterministic detector perform reliably before cloud upload?

## Evaluation Axes

### 1. Capture Usefulness

Measure whether selective browser/image capture preserves enough useful context.

Artifacts:

- 30-50 sample captures
- raw capture envelope
- generated Source/Knowledge notes
- manual comparison

### 2. Local Preprocessing

Evaluate:

- source classification
- privacy/sensitivity classification
- PII/secret detection
- upload recommendation

Compare:

- deterministic rules
- optional tiny local model
- cloud model as upper bound

### 3. Cloud Curation Quality

Evaluate:

- Source note usefulness
- Knowledge note usefulness
- tag/link quality
- hallucination/failure cases
- review burden

### 4. Deployment And Recovery

Evaluate:

- deployment time on DigitalOcean
- deployment time on Hetzner
- hardened defaults
- backup success
- restore success
- queue processing latency
- basic cost/resource use

## Related-Work Comparison

### QMD

Relevant for:

- local Markdown semantic search
- indexing

Not same:

- no selective multimodal capture
- no cloud recovery architecture
- no local/cloud policy pipeline
- no write-plan safety model

### Khoj

Relevant for:

- self-hosted second brain over documents/notes

Differentiation:

- selective capture
- local pre-upload triage
- policy-driven cloud recovery
- Obsidian write plans
- not primarily chat/search

### Screenpipe

Relevant for:

- local ambient capture baseline

Differentiation:

- selective capture, not always-on recording
- user preview before upload
- Obsidian knowledge artifact workflow

### OpenClaw / ZeroClaw / Goose

Relevant for:

- self-hosted/personal agent runtime ecosystem

Differentiation:

- this project is not an agent runtime
- no arbitrary tool execution
- no skill marketplace
- can integrate later as memory/curation API

### Obsidian AI Plugins

Relevant for:

- vault AI automation

Differentiation:

- native/browser capture
- cloud recovery
- local preprocessing
- policy/audit model
- distributed architecture

## One-Week Hackathon Plan

### Day 1: Architecture And Schemas

- finalize component diagram
- define capture envelope
- define policy schema
- define Source/Knowledge note schema
- define write-plan schema

### Day 2: Dataset

- create 30-50 captures
- include browser text/link
- include screenshots/images
- include sensitive/private examples
- label source type, privacy, entities, desired notes

### Day 3: Local Preprocessing Test

- implement or manually test deterministic checks
- test one tiny local model if available
- produce failure table

### Day 4: Cloud Pipeline Mock

- create API/queue skeleton or mocked queue
- generate Source/Knowledge proposals
- design audit events
- define object storage layout

### Day 5: Obsidian Write Flow

- prototype or mock plugin queue
- write AI Inbox notes
- test Source/Knowledge note readability
- test frontmatter and wikilinks

### Day 6: Deployment Proof

- adapt Thany-Marcus cloud-init ideas
- sketch Docker Compose stack
- define Terraform DO/Hetzner modules
- deploy one provider if possible

### Day 7: Decision Memo

- final topic
- MVP scope
- exclusions
- evaluation plan
- risks
- supervisor-ready proposal outline

## Current Best Bachelor Topic Formulation

> **Design and evaluation of a distributed, privacy-aware context capture and cloud recovery system for Obsidian-compatible personal knowledge, using local preprocessing and policy-driven Markdown artifact generation.**

## Immediate Next Decisions

1. Choose exact backend queue mechanism:
   - DB-backed jobs first is recommended.

2. Choose first tiny local preprocessing path:
   - deterministic rules + optional Ollama/llama.cpp HTTP is recommended.

3. Choose first image processing path:
   - OCR-only first, captioning later.

4. Choose first Terraform implementation order:
   - DigitalOcean first, then Hetzner.

5. Decide whether browser extension UI is Svelte or minimal TypeScript:
   - minimal TypeScript unless popup UI grows.

