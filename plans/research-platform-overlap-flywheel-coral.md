# Overlap With Research Platforms: Flywheel And CORAL

Date checked: 2026-05-11

Purpose: assess whether the proposed medical Markdown knowledge-base system overlaps with research tools/platforms such as Flywheel and the Human-Agent-Society CORAL framework.

## Short Answer

Yes, there is overlap with Flywheel at the broad workflow level.

No, the thesis should not try to compete with Flywheel as a full medical imaging research platform.

The defensible position is:

> Flywheel is an enterprise imaging research data-management and AI workflow platform. This thesis implements a smaller, user-owned, Markdown-first knowledge-base pipeline for brain MRI research artifacts, model comparison, provenance, and Obsidian-compatible review.

The specific CORAL repo discussed here is:

- https://github.com/Human-Agent-Society/CORAL

This CORAL is not a medical imaging platform. It is a multi-agent autonomous self-evolution/autoresearch framework. It overlaps with the **research orchestration** idea, not with the medical data-management or Markdown knowledge-base product.

## Flywheel

Sources:

- https://flywheel.io/
- https://flywheel.io/flywheel-is-research-data-management/
- https://flywheel.io/neuroimaging-research-platform/
- https://docs.flywheel.io/data_transfer/inbound/bulk_import/bulk-import-deidentification/

Flywheel capabilities:

- medical imaging data management
- imaging/video ingestion from multiple sources
- DICOM/raw data capture
- de-identification workflows
- research data curation
- metadata management
- annotation and labeling
- radiology-grade viewers
- automated processing pipelines
- containerized plugin applications called Gears
- machine-learning workflow support
- provenance for reproducibility
- role-based collaboration and compliance
- APIs and SDKs for Python, MATLAB, R
- neuroimaging support including BIDS conversion, BIDS apps, MRIQC, fMRIPrep, DICOM-to-NIfTI, and many Gears

### Where It Overlaps

Flywheel overlaps strongly with:

- artifact ingestion
- imaging dataset organization
- metadata management
- processing pipelines
- model workflow support
- provenance
- collaboration
- de-identification
- research reproducibility

In other words, Flywheel is a much larger professional version of some infrastructure ideas in this thesis.

### Where Your System Differs

Your system differs by being:

- bachelor-scope
- self-hosted/user-owned VPS oriented
- Markdown/Obsidian-first
- focused on generated research notes, not enterprise imaging operations
- centered on processor-router architecture for note generation
- intended for small labs/students/individual research workflows
- not a PACS/VNA connector platform
- not an annotation/reader-study platform
- not a compliance-certified clinical research product

Good differentiation:

> Flywheel manages medical imaging research data at enterprise scale. This project focuses on turning selected brain MRI research artifacts and model outputs into auditable Markdown knowledge objects that can be read, versioned, and connected in an Obsidian-compatible vault.

## CORAL: Human-Agent-Society Multi-Agent Autoresearch Framework

Sources:

- https://github.com/Human-Agent-Society/CORAL
- https://arxiv.org/abs/2604.01658

CORAL describes itself as:

> robust, lightweight infrastructure for multi-agent autonomous self-evolution, built for autoresearch.

It is built around:

- multiple coding/research agents
- isolated git worktrees
- shared persistent state in `.coral/`
- attempts, notes, and skills
- evaluator/grader loop
- heartbeat-triggered reflection/intervention
- web dashboard
- support for Claude Code, Codex, Cursor Agent, Kiro, and OpenCode
- optional LiteLLM gateway for model routing
- deep-research warm-start mode

### Where It Overlaps

It overlaps with:

- autonomous research workflow
- multi-agent model/code experimentation
- shared notes and persistent memory
- repeated attempts and evaluator-based improvement
- model/tool orchestration
- experiment tracking and leaderboard-style evaluation

### Where It Differs

Your system is:

- a medical research artifact processing and knowledge-base service
- centered on MRI datasets, model outputs, Markdown notes, and provenance
- meant to produce a durable Obsidian-compatible vault
- not meant to let autonomous coding agents rewrite the system or run open-ended experiments by default

CORAL is:

- an agent orchestration/evolution framework
- task/grader/attempt oriented
- optimized for open-ended discovery and code/algorithm improvement
- not domain-specific to medical imaging
- not an artifact-to-Markdown medical KB

### Relevance To This Thesis

Use CORAL as related work for the **orchestration and autoresearch angle**, not as a direct medical competitor.

Possible thesis relevance:

- It validates the idea of persistent research memory and shared notes.
- It shows one way to organize repeated model-improvement attempts.
- It gives vocabulary for attempts, evaluators, skills, workspaces, and dashboards.
- It can inspire the processor-router/job architecture.
- It can be cited as an alternative if you discuss autonomous research agents.

Do not use it as core MVP infrastructure unless the thesis pivots to agentic model optimization.

### Possible Future Integration

Future idea:

```text
Medical KB system
  -> defines model-training task
  -> exports dataset subset + grader
  -> CORAL-style agent swarm tries model/preprocessing variants
  -> best attempt is imported back as ModelRun note
```

This is attractive, but too much for the bachelor MVP.

Safer MVP:

- deterministic processor router
- explicit model processors
- explicit evaluation scripts
- generated model-run notes
- no autonomous agent loop

## Positioning Table

| Tool / Framework | What It Is | Overlap | Differentiation |
| --- | --- | --- | --- |
| Flywheel | Enterprise medical imaging research data and AI platform | High infrastructure overlap | Your system is self-hosted, Markdown-first, smaller, Obsidian-compatible, thesis-scale |
| CORAL by Human-Agent-Society | Multi-agent autonomous self-evolution/autoresearch framework | Medium orchestration/research-memory overlap | Your system is a medical artifact-to-Markdown KB, not an autonomous code/model evolution platform |

## What To Say In Thesis

Use Flywheel in "known software products":

> Flywheel demonstrates that medical imaging research requires ingestion, curation, compute workflows, provenance, and collaboration. However, it is an enterprise imaging platform, while this work investigates a lightweight self-hosted Markdown-first architecture for organizing brain MRI research artifacts and model outputs.

Use Human-Agent-Society CORAL in "known algorithmic and technical solutions":

> CORAL demonstrates a multi-agent approach to open-ended research through isolated workspaces, shared persistent notes, evaluation loops, and agent collaboration. This thesis does not implement autonomous multi-agent evolution; it uses a narrower processor-router architecture to run configured medical data processors and generate auditable Markdown notes.

Use it in future work:

> A CORAL-like autoresearch loop could later optimize preprocessing/model variants and import successful attempts as model-run notes.

## Final Verdict

Flywheel remains the closest product analog and should be treated seriously.

The thesis should not claim novelty in general medical imaging data management. The novelty should be narrower:

> a lightweight, self-hosted, Markdown-first medical research knowledge-base pipeline with processor routing, model comparison, and provenance-aware note generation.

Human-Agent-Society CORAL is not a direct competitor. It is related work for autonomous research orchestration. The useful lesson is:

> Keep the processor router explicit and evaluable now; consider autonomous multi-agent optimization only as future work.
