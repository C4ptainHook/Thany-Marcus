# SOTA Doubt And Recheck Plan

Date checked: 2026-05-03

Purpose: mark which findings in the notes are likely stable, which are volatile, and what must be rechecked later before treating the notes as state of the art.

## Main Warning

The notes are **not a final SOTA literature review**. They are a research map.

Stable areas:

- personal information management foundations
- lifelogging predecessors
- local-first software principles
- privacy theory
- general LLM memory research framing

Volatile areas:

- ZeroClaw/OpenClaw/Goose-like agent runtimes
- Obsidian AI plugins
- local/tiny model capabilities
- self-hosted AI assistant tools
- security incidents around agent skills/extensions
- current product features and pricing

Before the thesis proposal or final diploma writing, volatile areas must be rechecked.

## What Counts As SOTA Here

There are multiple "SOTA" layers:

1. **Academic SOTA**
   - papers, benchmarks, reproducible evaluations
   - slower moving, more citable

2. **Product SOTA**
   - tools users can install now
   - fast moving and often poorly documented

3. **Open-source ecosystem SOTA**
   - GitHub repos, docs, releases, issues
   - changes weekly

4. **Model SOTA**
   - small/local models, embedding models, redaction models
   - changes monthly

5. **Security SOTA**
   - attacks, prompt injection, malicious extensions, sandboxing
   - changes quickly and needs recent sources

The thesis should separate these instead of pretending one static "current state" exists.

## Stable Findings

### Personal Information Management

Likely stable:

- Jones/Teevan PIM framing
- Keeping Found Things Found
- Bergman/Whittaker digital stuff
- Stuff I've Seen
- MyLifeBits

Why stable:

- foundational research
- not dependent on product versions

Recheck need:

- low

### Local-First Software

Likely stable:

- local-first principles from Kleppmann et al.
- ownership, offline use, sync, privacy, longevity

Recheck need:

- low for the original paper
- medium for current CRDT/sync tooling if implementation begins

### Privacy Theory

Likely stable:

- Nissenbaum contextual integrity
- data-flow framing

Recheck need:

- low

## Medium-Volatility Findings

### LLM Memory Research

Current notes include:

- MemGPT / Letta
- LongMemEval
- Zep / Graphiti
- Mem0

Need recheck:

- newer memory benchmarks
- newer personal-agent memory architectures
- whether Mem0/Zep claims were independently reproduced
- whether LongMemEval remains the right benchmark

Use cautious wording:

- "claims"
- "reports"
- "proposes"
- "evaluates on"

Avoid:

- "is best"
- "outperforms all"
- "solves memory"

### Local Tiny Models

Current notes include:

- Gemma 3 270M/1B
- Qwen3 0.6B
- Qwen2.5 0.5B/1.5B
- SmolLM/SmolLM2
- Llama 3.2 1B/3B
- EmbeddingGemma
- MiniLM
- GLiNER

Need recheck:

- current model availability
- licenses
- quantized sizes
- mobile support
- benchmark results on curation tasks
- whether newer sub-1B models exist

Important:

- The thesis should not rely on generic leaderboard claims.
- It should benchmark models on its own capture/curation dataset.

## High-Volatility Findings

### ZeroClaw / OpenClaw / Agent Runtimes

Need recheck:

- official repos
- latest docs
- release dates
- supported channels/providers
- security posture
- whether project names/domains changed
- whether forks/variants are legitimate or SEO clones

Use only official sources for core claims:

- GitHub organization
- official docs
- official website
- tagged releases

Treat third-party sites as:

- discovery aids
- commentary
- not authoritative

### Obsidian AI Plugin Landscape

Need recheck:

- plugin names
- active maintenance
- actual install counts if needed
- whether Claude/ChatGPT/OpenAI integrations changed
- whether Obsidian itself added native AI features

Avoid:

- hard plugin counts unless sourced at writing time

### Product Claims

Recheck before citing:

- star counts
- number of integrations
- pricing
- license
- self-host availability
- cloud/local mode behavior
- acquisition/shutdown status
- security incidents

## Proposed Recheck Schedule

### Before Thesis Topic Approval

Recheck:

- direct competitors: Khoj, Obsidian AI plugins, Screenpipe, Anytype, ZeroClaw/OpenClaw
- top local models for chosen tasks
- legal/privacy constraints for capture method

Output:

- 1-page competitor update
- final project positioning paragraph

### Before Architecture Chapter

Recheck:

- local-first/sync implementation options
- self-host deployment practices
- model serving options
- sandboxing/network egress enforcement

Output:

- updated architecture tradeoff table

### Before Evaluation Chapter

Recheck:

- latest memory benchmarks
- latest tiny models
- latest embedding/NER models
- latest security threat models

Output:

- benchmark candidate list
- frozen model versions and hardware

### Before Final Submission

Recheck:

- product landscape
- citations and URLs
- whether any "current" claim became false

Output:

- final related-work update
- appendix: date of landscape review

## Red Flags In Existing Notes

The following should be treated as weak unless reverified:

- "crowded" claims without quantified evidence
- exact GitHub stars
- "best" model/tool claims
- "nobody does X" claims
- acquisition/shutdown claims unless exact date and source are cited
- pricing/license claims
- tool feature lists copied from promotional pages

## Better Thesis Language

Prefer:

- "as of the May 2026 review"
- "official docs describe"
- "the authors report"
- "the project positions itself as"
- "this appears to overlap with"
- "this should be rechecked before final submission"

Avoid:

- "current SOTA is"
- "nobody has built"
- "the best tool is"
- "guarantees privacy"
- "solves memory"

## SOTA Recheck Checklist

For each important tool/model:

- official homepage checked
- official GitHub/repo checked
- latest release date checked
- license checked
- self-host/local support checked
- data-flow/privacy claim checked
- security model checked
- model/provider support checked
- active maintenance checked
- one independent source checked if using adoption/security claims

For each important paper:

- arXiv/DOI checked
- date checked
- peer-review status checked if relevant
- benchmark dataset checked
- claims separated from independent validation
- relation to project explicitly stated

## Revised Confidence Levels

High confidence:

- PIM/local-first/privacy foundations are relevant.
- Markdown artifact durability is a defensible design choice.
- Generic Obsidian AI agent is too weak as a thesis contribution.
- Self-hosting alone is not enough for privacy.

Medium confidence:

- selective capture + curation is a strong direction.
- tiny models are useful as first-pass routers/extractors.
- agent runtimes can become integration targets.

Low confidence until evaluated:

- tiny models can produce useful final notes.
- local-only setup can match cloud curation quality.
- users prefer selective capture over ambient capture.
- any specific product remains the best competitor by final submission.

