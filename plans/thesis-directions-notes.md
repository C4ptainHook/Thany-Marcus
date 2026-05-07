# Thesis Directions and Proposal Notes

## Candidate Titles

1. **Privacy-Preserving Personal Knowledge Capture and Organization in Markdown Vaults**
2. **Deployment-Aware AI Orchestration for Private Personal Knowledge Management**
3. **A Consent-First Local-First Architecture for AI-Assisted Markdown Knowledge Bases**
4. **Selective Context Capture for Personal Knowledge Management with Local LLM Curation**
5. **A Private Cloud Service for AI-Assisted Organization of Markdown Knowledge Vaults**

Best current title:

> **A Privacy-Preserving Cloud Service for AI-Assisted Organization of Markdown Knowledge Vaults**

This keeps the original diploma direction while making the research contribution clearer.

After verification, a more precise title may be stronger:

> **Privacy-Aware AI Curation of Personal Knowledge into Markdown Vaults**

This avoids overclaiming "cloud service" as the core novelty and puts the research emphasis on curation, privacy, and durable artifacts.

## Recommended Problem Statement

Personal knowledge is fragmented across documents, chats, web pages, meetings, and notes. Existing personal knowledge tools either require users to organize everything manually, use opaque cloud AI services, or rely on always-on capture that introduces privacy and trust concerns. Markdown vaults offer durable, user-owned storage, but they do not solve capture, deduplication, semantic linking, or privacy-aware AI processing.

This project proposes a cloud-capable but privacy-preserving architecture where users explicitly capture selected context, a deployment-aware AI pipeline curates it, and the result is stored as interoperable Markdown.

## Proposed Contributions

### Contribution 1: Deployment-Mode-Aware Privacy Architecture

Define profiles for local-only, self-hosted private cloud, hybrid private gateway, and public mode.

Contribution evidence:

- data-flow diagrams
- provider allowlists
- audit logs
- egress tests
- threat model

### Contribution 2: Selective Capture Workflow

Design explicit capture flows instead of always-on recording.

Contribution evidence:

- capture envelope schema
- desktop hotkey/clipboard/browser prototype
- user effort measurement
- comparison with raw manual note-taking

### Contribution 3: Local/Private AI Curation Pipeline

Use small local/self-hosted models to convert captured fragments into structured Markdown.

Contribution evidence:

- extraction quality evaluation
- duplicate/link suggestion evaluation
- generated note examples
- latency/resource measurements across models

### Contribution 4: Durable Markdown Knowledge Artifacts

Preserve user-readable notes rather than hiding memory in an opaque service.

Contribution evidence:

- Obsidian-compatible vault
- wikilinks/frontmatter/tags
- rebuildable index
- user-edit preservation

## Proposed Research Questions

1. How can a personal knowledge system enforce different privacy boundaries for local, self-hosted, hybrid, and public AI processing modes?
2. To what extent can selective user-triggered capture replace always-on capture while preserving useful retrieval context?
3. Can small local language models perform enough extraction, deduplication, and link suggestion to maintain a useful Markdown knowledge vault?
4. What audit mechanisms are sufficient to make privacy claims understandable and verifiable for users?

Revised, more cautious version:

1. How can a personal knowledge system represent and audit content flows across local, self-hosted, hybrid, and public AI processing modes?
2. What context quality is lost or preserved when selective capture is used instead of ambient capture or manual note-taking?
3. Which personal-knowledge curation tasks are feasible with small local models, and which require stronger self-hosted or cloud models?
4. How can AI-generated changes to a Markdown vault remain reviewable, reversible, and attributable to sources?

## Hypotheses

- H1: Selective capture produces less complete but more privacy-preserving personal knowledge records than always-on capture.
- H2: Small local models are sufficient for first-pass curation tasks such as tagging, entity extraction, and duplicate detection, even if they are weaker for high-quality synthesis.
- H3: Markdown artifacts plus a rebuildable index provide better user control and longevity than opaque memory databases for personal knowledge management.
- H4: Users understand privacy better when deployment profiles are tied to concrete data-flow rules rather than vague "local/private/cloud" labels.

## Scope Recommendation

### Must Have

- Markdown vault output.
- Explicit capture flow.
- Local model route.
- Privacy profiles.
- Audit log.
- Evaluation against at least one baseline.

### Should Have

- Self-hosted mode with Docker Compose.
- Deduplication and link suggestions.
- Obsidian-compatible frontmatter and wikilinks.
- Small model comparison.

### Could Have

- Browser extension.
- Mobile share sheet.
- Hybrid redaction route.
- Graph visualization.
- Terraform deployment.

### Should Not Have

- Always-on screen recording.
- Call recording.
- WhatsApp/Telegram deep integrations.
- Full real-time multi-device sync.
- Fine-tuning.
- Building a full Obsidian replacement.

## Baselines For Evaluation

### Baseline 1: Manual Obsidian notes

User manually creates notes from the same source material.

Metrics:

- time spent
- number of useful links
- retrieval success
- subjective quality

### Baseline 2: Raw capture log

Store captured snippets without curation.

Metrics:

- retrieval success
- duplicate rate
- note readability

### Baseline 3: Vector search over raw snippets

Embeddings over raw captured material.

Metrics:

- answer accuracy over a query set
- latency
- ability to handle stale/contradictory information

### Baseline 4: Cloud LLM curation

Same pipeline using a frontier cloud model.

Metrics:

- quality difference vs. local model
- privacy leakage/data egress
- cost
- latency

Optional external baseline:

- Screenpipe if evaluating ambient capture.
- Mem0 if evaluating long-term memory extraction.

## Suggested Evaluation Dataset

Create a small synthetic-but-realistic personal knowledge dataset:

- 20 work chat snippets
- 10 meeting transcript excerpts
- 10 web article selections
- 10 project notes
- 10 conflicting/stale updates
- 10 duplicate or near-duplicate captures

Then create 50-100 retrieval/QA tasks:

- "What did I decide about X?"
- "Who is responsible for Y?"
- "What changed since the earlier note?"
- "Which note is related to this new snippet?"
- "What should not be sent to a cloud model?"

This is enough for a bachelor thesis if the evaluation is honest and reproducible.

## Defense Framing

If asked "what is novel?", answer:

> The novelty is not Markdown, Obsidian, or LLM memory alone. The contribution is an architecture and prototype that combine explicit context capture, deployment-aware privacy boundaries, local/private AI curation, and durable Markdown artifacts. Existing systems usually optimize one side: ambient capture, cloud memory, or vault automation. This work focuses on the trust boundary and user-owned artifact layer between them.

If asked "why not use Screenpipe?", answer:

> Screenpipe is a strong always-on local capture baseline. This project deliberately studies selective capture and Markdown curation as a lower-risk alternative for users who do not want a full screen/audio lifelog.

If asked "why not use Mem0/Zep/Letta?", answer:

> Those are agent memory layers. This project targets personal knowledge management where the user can inspect, edit, sync, and preserve the knowledge as Markdown files.

If asked "why cloud service if private?", answer:

> Cloud does not have to mean third-party cloud. The system separates data location from trust boundary. A private mode can use user-controlled infrastructure for sync/storage/inference while forbidding third-party model or storage providers.
