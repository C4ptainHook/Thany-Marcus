# Research Notes: Privacy-Preserving Markdown Knowledge Cloud

Date: 2026-05-03

## Source Project Idea

Working title from the shared document: **"cloud service for aggregation and organization of files in markdown format"**.

The initial formulation is a cloud service around Markdown files, but the actual project idea is broader:

- A personal "second brain" that stores knowledge as human-readable Markdown.
- Obsidian-compatible vault conventions are attractive because they provide plain files, backlinks, wikilinks, metadata, tags, graph views, and a mature plugin ecosystem.
- The system should organize files not only visually, but semantically: deduplicate, forget stale data, form associations, suggest mental models, and maintain a persistent personal context for LLM-assisted work.
- Privacy is central. A private mode should avoid exposing user data to third-party APIs; a public mode may allow Google Drive/iCloud/frontier model APIs with explicit consent.
- Possible self-hosting story: n8n-like deployment, maybe Docker Compose and Terraform, so users can run a private instance on a VPS.
- Capture is the hard product question: avoid becoming another generic bot/context-capture clone; explore selective capture via desktop/mobile flows, selected text, share sheets, screenshots, voice notes, and possibly local curation models.

## Main Finding

The plain "AI organizer for Obsidian/Markdown vaults" idea is crowded. The stronger thesis direction is not "another vault agent"; it is:

> **A privacy-preserving, deployment-mode-aware personal knowledge system that captures user-approved context, curates it with local or user-controlled models, and writes durable Markdown artifacts.**

This reframes the project around a defensible research/engineering contribution:

- Trust boundaries between local, self-hosted, hybrid, and public modes.
- Selective capture rather than always-on vacuum capture.
- Small local model or self-hosted model as a privacy gateway: extraction, deduplication, redaction, link suggestion, and routing.
- Markdown/Obsidian as an interoperable storage layer, not the unique innovation.

## Recommended Thesis Scope

Most feasible strong scope:

**"A privacy-preserving personal knowledge capture and organization system for Markdown vaults using deployment-mode-aware AI orchestration."**

Core demonstration:

- Cross-platform desktop shell if required, but keep the first demo path narrow.
- Explicit user-triggered capture: hotkey on selected text, clipboard capture, browser/share extension, screenshot import, or manual paste.
- Local curation pipeline:
  - extract facts/entities/tasks/questions
  - detect duplicates or contradictions
  - suggest tags and wikilinks
  - write or update Markdown notes
  - optionally prepare sanitized summaries for cloud LLMs
- Storage:
  - local vault as source of truth
  - optional encrypted sync or self-hosted object/file store
  - Obsidian-compatible Markdown output
- Deployment profiles:
  - local-only
  - self-hosted private cloud
  - hybrid redaction/routing
  - public/cloud convenience mode

## What Is Already Done

Several adjacent areas are mature:

- Personal information management research established the problem decades ago: people need to re-find and reuse previously seen information, not only search the public web.
- Lifelogging systems such as MyLifeBits explored "capture everything" personal archives.
- Local-first software established principles for ownership, offline use, sync, collaboration, privacy, and longevity.
- Obsidian and Markdown vault workflows are already heavily used for second-brain systems.
- LLM memory systems such as MemGPT/Letta, Mem0, Zep, and graph-memory systems already handle persistent agent memory.
- Ambient capture tools such as Rewind/Limitless, Microsoft Recall, and Screenpipe already index screen/audio history.

The opening is a combination that current tools do not handle cleanly:

> **consented capture + local/private curation + Markdown durability + enforceable deployment privacy modes.**

## Project Directions Worth Considering

### Direction A: Selective Capture + Local Curator

Best fit for a bachelor thesis.

Build a tool where the user explicitly captures a fragment of context. A local or private model converts it into durable Markdown knowledge:

- "Save this selected Slack message and context."
- "Turn this meeting snippet into an atomic note."
- "Link this note to related concepts already in my vault."
- "This contradicts an older note; ask before replacing."

Novelty:

- It rejects always-on surveillance capture.
- It treats the model as a curator and privacy boundary, not merely a chatbot.
- It can be evaluated with capture quality, deduplication precision, link usefulness, privacy leakage, and user effort.

### Direction B: Deployment-Mode-Aware Privacy Architecture

Research-heavy systems direction.

Define and implement privacy modes with clear data-flow guarantees:

- Local-only: data and inference stay on device.
- Private cloud: data may leave device only to user-controlled infrastructure.
- Hybrid: local model redacts/summarizes before third-party calls.
- Public: third-party storage and model APIs are allowed.

Novelty:

- Most tools offer "local" or "cloud", but do not formalize an enforceable boundary.
- The evaluation can include network egress audits, data-flow logs, and attack/threat analysis.

### Direction C: n8n-Style Self-Hosted PKM Stack

Engineering-heavy direction.

Package the system so a non-expert can deploy:

- app server
- sync/storage backend
- vector database
- local/self-hosted model adapter
- auth
- backups
- monitoring

Novelty is weaker academically, but useful if framed as making privacy-preserving personal knowledge infrastructure accessible.

## What To Avoid

- Do not build another generic Obsidian AI plugin as the main contribution.
- Do not start with always-on screen/audio/call recording. It creates legal, ethical, OS-permission, and evaluation problems.
- Do not fine-tune a model unless the thesis is specifically about model training.
- Do not make "cross-platform everything" the core risk. Cross-platform desktop + mobile + sync + local inference is too much for a bachelor thesis.
- Do not compete directly with Mem0/Zep/Letta as a general LLM memory layer.

## Key Research Questions

1. Can explicit, consented capture preserve enough useful context compared with always-on ambient capture?
2. Can a small local model perform enough curation to make Markdown notes useful: extraction, tagging, linking, deduplication, summarization?
3. How should privacy modes be represented so users and developers understand exactly where data may flow?
4. How can the system prove or at least audit that private mode does not send content to third-party services?
5. Which storage model is best for durable personal knowledge: plain files, SQLite plus export, CRDT-backed sync, object storage, or Git-like versioning?
6. How should the system handle forgetting, stale notes, contradiction, and duplicate facts without silently rewriting the user's knowledge base?

## Evaluation Ideas

- **Privacy audit:** run the app in each mode and log outbound connections/content classes.
- **Curation quality:** compare generated notes against human-written reference notes for a small dataset.
- **Deduplication/linking:** measure precision/recall for duplicate detection and suggested wikilinks.
- **Retrieval benchmark:** create 50-100 questions over a test vault and compare curated Markdown retrieval with raw capture logs and baseline vector search.
- **User effort:** measure clicks/time from "I saw something useful" to "usable note in vault."
- **Deployment UX:** time from fresh machine/VPS to first successful private capture.

## Suggested Final Thesis Claim

A defensible claim could be:

> This work proposes and evaluates a privacy-preserving architecture for personal knowledge capture in which user-approved context is curated by deployment-mode-aware AI components and stored as interoperable Markdown. Compared with always-on lifelogging and cloud-first LLM memory systems, the approach improves user control and auditability while preserving enough context for practical personal knowledge retrieval.

