# Possible Implementation Notes

These are implementation directions only. No code has been written.

## Architecture Overview

Recommended components:

1. **Capture client**
   - desktop hotkey / clipboard / selected text
   - optional browser extension or share target later
   - mobile companion only if scope allows

2. **Capture envelope**
   - raw captured text or media reference
   - timestamp
   - source application
   - source URL/file/conversation if available
   - user-provided note/comment
   - privacy class
   - retention policy

3. **Privacy router**
   - decides whether content can be processed locally, on self-hosted infrastructure, or by a third-party API
   - should be deterministic and auditable
   - should block egress in private mode by default

4. **Curation pipeline**
   - normalize input
   - extract entities/facts/tasks/questions
   - detect PII/sensitive data
   - deduplicate against existing vault
   - suggest links/tags/frontmatter
   - identify contradictions/stale facts
   - generate Markdown patch proposal

5. **Vault writer**
   - writes Obsidian-compatible Markdown
   - preserves user edits
   - avoids silent destructive rewrites
   - keeps an audit trail of generated changes

6. **Index**
   - local full-text search
   - embeddings/vector search
   - optional graph index over wikilinks/entities
   - optional temporal index

7. **Sync/storage**
   - local filesystem source of truth
   - optional Git, S3-compatible object storage, WebDAV/Nextcloud, Syncthing, or encrypted backup
   - cloud storage should be independent from model placement

## Deployment Profiles

### Profile A: Fully Local

- Vault: local folder.
- Model: local Ollama/llama.cpp/LM Studio.
- Index: local SQLite + vector index.
- Network: disabled or blocked for content routes.

Good for:

- strongest privacy story
- demo of no-egress mode
- laptop/desktop users

Risks:

- weak devices may be slow
- local model quality may be insufficient for nuanced summarization

### Profile B: Self-Hosted Private Cloud

- Vault: user-owned VPS/storage.
- Model: user-owned VPS or home server.
- Client: desktop/mobile thin capture client.
- Network: allowed only to user-controlled endpoints.

Good for:

- weak phone/laptop
- private multi-device sync
- n8n-like deploy story

Risks:

- VPS setup complexity
- securing the server is nontrivial
- CPU-only inference on cheap VPS may be slow

### Profile C: Hybrid Private Gateway

- Local model performs classification, PII redaction, summarization, and routing.
- Only sanitized snippets go to frontier APIs.
- Raw vault remains local/private.

Good for:

- realistic product quality
- measurable privacy/utility tradeoff

Risks:

- redaction can fail
- semantic leakage remains even after PII removal
- needs clear user consent and logs

### Profile D: Public Convenience

- Frontier APIs and Google Drive/iCloud-like storage allowed.
- Best quality and easiest sync.
- Weakest privacy story.

Good for:

- practical users who choose convenience
- comparison baseline

Risks:

- not thesis-novel unless compared against private modes

## Local Model Candidates

Use off-the-shelf models. Do not fine-tune unless the thesis changes.

### Phi-4-mini

Microsoft released Phi-4-mini-instruct as a 3.8B model in 2025. Ollama lists it as a 3.84B model with Q4 size around 2.5GB and 128K context.

Sources:

- https://techcommunity.microsoft.com/blog/educatordeveloperblog/welcome-to-the-new-phi-4-models---microsoft-phi-4-mini--phi-4-multimodal/4386037
- https://ollama.com/library/phi4-mini%3A3.8b

Potential use:

- local extraction
- routing decisions
- lightweight summarization
- function/tool calling experiments

### Gemma 3

Gemma 3 has 270M, 1B, 4B, 12B, and 27B variants according to Google AI for Developers. The 270M/1B variants are text-only with 32K context; 4B/12B/27B support image+text and 128K context.

Sources:

- https://ai.google.dev/gemma/docs/core
- https://ai.google.dev/gemma/docs/core/model_card_3
- https://huggingface.co/blog/gemma3

Potential use:

- 1B/4B for local classification and extraction
- 4B+ if screenshot/visual context becomes part of the capture path

### Qwen2.5

Qwen2.5 has open model sizes from 0.5B to 72B. Qwen model information lists small 0.5B/1.5B models with 32K context and 7B+ with 128K context.

Sources:

- https://qwen2.org/qwen2-5/
- https://huggingface.co/Qwen/Qwen2.5-72B

Potential use:

- 0.5B/1.5B for very weak devices or cheap VPS
- 7B for higher-quality local curation on decent machines

### Qwen3

Qwen3 is a newer alternative to Qwen2.5. Hugging Face Transformers documentation describes dense Qwen3 models from 0.6B to 32B with both thinking and non-thinking modes.

Source: https://huggingface.co/docs/transformers/model_doc/qwen3

Potential use:

- compare against Qwen2.5 for extraction and routing
- test "thinking mode" only where latency and privacy budget allow it
- avoid assuming it is better without evaluation

### Llama 3.2 1B / 3B

Meta's Llama 3.2 release includes lightweight text-only 1B and 3B models intended for edge/mobile use, with 128K context according to Meta's announcement coverage.

Source: https://about.fb.com/br/news/2024/09/conheca-o-llama-3-2-da-nuvem-para-a-borda-e-agora-com-visao/

Potential use:

- local classification and summarization on weak devices
- benchmark against Gemma 3 1B and Qwen small models
- useful if mobile/edge deployment becomes central

### Model choice warning

Do not make the thesis depend on one model family. The curation pipeline should define tasks and interfaces first, then test multiple model/provider implementations.

## Storage Design

### Markdown as artifact, not database

Use Markdown as the durable user-facing representation. Keep indexes disposable:

- `/notes/` or Obsidian vault folder: source of truth.
- `.system/index.sqlite`: rebuildable index.
- `.system/audit/`: generated changes, model decisions, privacy routing logs.
- `.system/cache/`: embeddings, OCR, transcripts.

### Suggested note format

```markdown
---
created: 2026-05-03T12:00:00+03:00
updated: 2026-05-03T12:00:00+03:00
source_type: selected_text
source_app: Slack
privacy: private
capture_id: 2026-05-03-abc123
entities:
  - Example Person
  - Example Project
tags:
  - project/example
---

# Short Human Title

Atomic summary in the user's words or clearly marked generated text.

## Facts

- ...

## Links

- [[Related note]]

## Open Questions

- ...
```

### Audit log idea

Each AI operation should create an append-only record:

- input hash
- model/provider used
- privacy profile
- whether network was allowed
- files proposed to change
- files actually changed
- user approval state

This helps defend the privacy-boundary thesis claim.

## Capture Flow Options

### Hotkey on selected text

Most aligned with the user's idea.

Pros:

- explicit consent
- lightweight demo
- works across many apps in principle
- avoids always-on capture

Cons:

- platform APIs differ
- selected text access may require accessibility permissions
- Wayland/Linux, macOS, and Windows behave differently

### Clipboard capture

Fallback flow: user copies text, presses capture hotkey.

Pros:

- simpler and cross-platform
- easy prototype

Cons:

- less magical
- weaker headline feature

### Browser extension

Good for web pages, docs, issue trackers, Slack web, GitHub, etc.

Pros:

- rich metadata: URL, title, selected DOM text
- easier than native global selection

Cons:

- does not cover native apps

### Mobile share sheet

Good future companion scope.

Pros:

- captures Telegram/WhatsApp/browser/shareable content without deep integrations
- avoids call recording

Cons:

- mobile development overhead
- iOS/Android differences

## Privacy Boundary Implementation Ideas

### Minimal thesis version

- Explicit configuration profile.
- Provider allowlist per profile.
- Network egress logging.
- Block third-party model providers in private mode.
- Show user-visible audit log.
- Use local firewall/container/network namespace if practical.

### Stronger version

- Separate local daemon for private processing.
- Private mode runs with no network entitlement where OS supports it.
- Remote self-host endpoint pinned by key/fingerprint.
- Content-addressed audit log for captured artifacts.
- Red-team tests for accidental egress.

### Important distinction

Private mode should not necessarily mean "data never leaves the physical device." A better definition:

> Private mode means raw user content only flows to endpoints controlled by the user, and never to third-party storage/model providers.

This allows a private VPS while preserving the privacy claim.

## Deduplication and Forgetting

Possible approach:

- Compute embeddings for each atomic fact/note.
- Search nearest neighbors before creating new note.
- Classify relationship:
  - duplicate
  - elaboration
  - contradiction
  - stale update
  - unrelated
- For duplicates, append source evidence or skip.
- For contradictions, create a review item instead of overwriting.
- For stale data, mark superseded with timestamp and source.

Do not silently delete old information. "Forgetting" should mean demotion, archiving, summarization, or marked supersession unless the user explicitly deletes.

## Security Risks To Design Around

- Prompt injection in captured web/email/chat content.
- Sensitive data leakage through cloud model calls.
- Local index or cache becoming a high-value target.
- Generated notes corrupting or overwriting user-authored notes.
- Sync conflicts between devices.
- Misleading summaries that remove uncertainty or source context.
- Legal risk from recording calls or third-party conversations.

## Practical MVP

Smallest useful implementation:

1. Desktop capture from clipboard or selected text.
2. Local model provider via Ollama.
3. Markdown vault writer.
4. Local SQLite/embedding index.
5. Dedup/link suggestions.
6. Private/public profile switch with audit log.
7. Evaluation dataset and network egress test.

Stretch:

- browser extension
- self-host Docker Compose
- mobile share sheet
- hybrid redaction route
- graph visualization
