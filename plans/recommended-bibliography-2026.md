# Recommended Bibliography For Thesis, Rechecked In 2026

Date checked: 2026-05-04

Purpose: provide 12-15 sources suitable for the recommended thesis structure. This list separates stable foundational literature from current 2026 SOTA/technical sources.

## Important Correction

The previous list was good as a **foundation**, but not fully current for model/SOTA discussion. In particular, Gemma 4 exists as of April 2026, so any discussion of local/small model choices should not stop at Gemma 3.

Use older books/papers for durable theory, but use recent sources for:

- local model capabilities
- agent memory SOTA
- LLM security risks
- implementation/tool comparison

## Final Recommended Shortlist To Use

Use this as the main bibliography. It is balanced for your project: domain foundations, Obsidian/second-brain evidence, cloud/local architecture, LLM memory, multimodal processing, and security.

| # | Source | Why it is included |
| --- | --- | --- |
| 1 | Jones and Teevan, *Personal Information Management* (2007) | Foundational PIM definition and domain vocabulary. |
| 2 | Bergman and Whittaker, *The Science of Managing Our Digital Stuff* (2016) | Personal digital curation and why user-owned organization matters. |
| 3 | Ferreira et al., "How People Manage Knowledge in their Second Brains" (INTERACT 2025) | Directly relevant Obsidian/second-brain user study; better fit than generic PKM books alone. |
| 4 | Dumais et al., "Stuff I've Seen" (SIGIR 2003) | Classic personal retrieval/re-use system over previously seen information. |
| 5 | Gemmell, Bell, and Lueder, "MyLifeBits" (CACM 2006) | Capture-everything/lifelogging baseline to contrast with selective capture. |
| 6 | Kleppmann et al., "Local-First Software" (Onward! 2019) | User ownership, local/cloud tradeoffs, offline-first principles. |
| 7 | Kleppmann, *Designing Data-Intensive Applications* (2017) | Architecture, queues, storage, reliability, recovery. |
| 8 | Nissenbaum, *Privacy in Context* (2010) | Theoretical basis for contextual data-flow/privacy policies. |
| 9 | Du, "Memory for Autonomous LLM Agents" (2026 survey) | Current memory-system taxonomy and SOTA survey through early 2026. |
| 10 | Wu et al., "LongMemEval" (2024) | Evaluation categories: updates, temporal reasoning, abstention, long-term memory. |
| 11 | Zhang et al., "HiMem" (2026) | Current hierarchical memory architecture; useful analogy for raw/source/knowledge layers. |
| 12 | Banerjee et al., "APEX-MEM" (2026) | Current temporal/semi-structured memory with append-only/conflict-aware framing. |
| 13 | Jin et al., "Efficient Multimodal Large Language Models: A Survey" (2025) | Relevant for image/video/audio processing under cost/resource constraints. |
| 14 | Zhan et al., "InjecAgent" (2024) | Security basis for treating captured web/app content as hostile input. |
| 15 | OWASP Top 10 for LLM Applications 2025 | Practical LLM security risks: prompt injection, sensitive disclosure, excessive agency, vector weaknesses. |

Mandatory technical/product references outside the 15, if your department allows web/software documentation:

- Gemma 4 official announcement/docs for current local model discussion: https://blog.google/innovation-and-ai/technology/developers-tools/gemma-4/ and https://ai.google.dev/gemma/docs
- QMD GitHub for semantic-search analog: https://github.com/achekulaev/obsidian-qmd
- Khoj docs for self-hosted second-brain analog: https://docs.khoj.dev/
- Screenpipe docs for ambient-capture analog: https://docs.screenpi.pe/
- OpenClaw/ZeroClaw/Goose docs for agent-runtime analogs.

If you must stay at exactly 15 total sources including technical docs, replace **APEX-MEM** with **Gemma 4 official docs**. APEX-MEM is more academically current; Gemma 4 is more directly useful for implementation/tool choice.

## Recommended Sources By Thesis Section

### Introduction

Use:

- Jones and Teevan, *Personal Information Management*
- Bergman and Whittaker, *The Science of Managing Our Digital Stuff*
- Ferreira et al., "How People Manage Knowledge in their Second Brains"
- Kleppmann et al., "Local-First Software"

Purpose:

- explain the problem of fragmented personal information
- justify personal knowledge bases and Obsidian-compatible Markdown
- introduce user-owned/local-cloud tension

### 1.1 Statement of the Diploma Project Task

Use:

- Ferreira et al., "How People Manage Knowledge in their Second Brains"
- Kleppmann et al., "Local-First Software"
- Nissenbaum, *Privacy in Context*

Purpose:

- justify the task as selective context capture and policy-governed cloud curation for personal knowledge
- connect the task to user ownership and privacy-aware data flow

### 1.2 Domain Area Analysis

Use:

- Jones and Teevan, *Personal Information Management*
- Bergman and Whittaker, *The Science of Managing Our Digital Stuff*
- Ferreira et al., "How People Manage Knowledge in their Second Brains"
- Dumais et al., "Stuff I've Seen"
- Gemmell et al., "MyLifeBits"

Purpose:

- define PIM/PKM/ePKB/second brain
- explain retrieval/re-finding
- contrast selective capture with lifelogging

### 1.3 Analysis of Existing Solutions

#### 1.3.1 Known Software Products

Use product/technical references:

- Khoj docs
- Screenpipe docs
- QMD GitHub
- OpenClaw docs
- ZeroClaw docs
- Goose docs
- Obsidian plugin/forum sources if needed

Purpose:

- position your system against self-hosted second brains, ambient capture, semantic search, and agent runtimes

Academic support:

- Ferreira et al. for Obsidian/second-brain user needs
- MyLifeBits for ambient/lifelogging lineage

#### 1.3.2 Known Algorithmic And Technical Solutions

Use:

- Du, "Memory for Autonomous LLM Agents"
- LongMemEval
- HiMem
- APEX-MEM
- Efficient Multimodal LLMs survey
- Gemma 4 docs, if implementation model choice is discussed

Purpose:

- memory write/read/manage loop
- temporal updates and conflict handling
- multimodal processing under resource constraints
- local/cloud model choice

### 1.4 Analysis And Modeling Of Business Processes

Use:

- Jones and Teevan
- Bergman and Whittaker
- Ferreira et al.
- Local-First Software

Purpose:

- model the user flow:
  capture -> local preview -> cloud processing -> Obsidian write -> backup/recovery
- justify user-facing processes from PIM/second-brain behavior

### 2.1 Software Use Cases

Use:

- Ferreira et al.
- Stuff I've Seen
- MyLifeBits
- Local-First Software

Purpose:

- use cases around capture, retrieval, recovery, and selective context preservation

### 2.2 Functional Requirements

Use:

- Ferreira et al.
- LongMemEval
- Efficient Multimodal LLMs survey
- DDIA

Purpose:

- capture envelope
- Source/Knowledge notes
- cloud processing queue
- backup/recovery
- model-based curation

### 2.3 Non-Functional Requirements

Use:

- DDIA
- Local-First Software
- OWASP LLM Top 10
- Nissenbaum

Purpose:

- reliability
- maintainability
- privacy
- security
- recoverability
- auditability

### 2.4 Analysis Of System Requirements

Use:

- DDIA
- Local-First Software
- OWASP LLM Top 10
- Efficient Multimodal LLMs survey

Purpose:

- justify VPS/Docker Compose/Postgres/pgvector/object storage/model server choices
- justify bounded multimodal processing

### 2.5 Economic Indicators

Use:

- DDIA for scalability/reliability tradeoffs
- Gemma 4 docs for local/small model cost rationale
- Efficient Multimodal LLMs survey for cost/resource pressure

Purpose:

- explain why user-owned VPS and small/local models reduce operating costs
- compare personal backend to SaaS dependence

### 2.6 Software Development Task Statement

Use:

- refined thesis plan plus:
  - Local-First Software
  - Ferreira et al.
  - OWASP LLM Top 10

Purpose:

- formalize the implemented vertical slice and exclusions

### 3.1 Software Architecture

Use:

- DDIA
- Local-First Software
- Du memory survey
- HiMem
- APEX-MEM

Purpose:

- local/cloud split
- queue-worker architecture
- backup/recovery
- memory artifact layers
- Source/Knowledge note model

### 3.2 Architectural Decisions And Tool Choice

Use:

- DDIA
- Local-First Software
- Gemma 4 docs
- Efficient Multimodal LLMs survey
- OWASP LLM Top 10

Purpose:

- justify Tauri/Svelte, ASP.NET Core, Postgres/pgvector, Docker Compose, Caddy, Ollama/llama.cpp/vLLM HTTP adapters, Terraform

### 3.3 Software Construction

Use:

- LongMemEval
- HiMem
- APEX-MEM
- Efficient Multimodal LLMs survey
- DDIA

Purpose:

- capture algorithm
- preprocessing pipeline
- cloud queue
- write-plan generation
- backup/recovery algorithm
- database/object storage structure

### 3.4 Data Security Analysis

Use:

- Nissenbaum, *Privacy in Context*
- InjecAgent
- OWASP LLM Top 10
- Local-First Software

Purpose:

- contextual privacy model
- prompt injection from captured content
- sensitive data disclosure
- excessive agency avoidance
- no arbitrary tool execution
- trusted user-owned cloud model

## Other Literature Options Considered

These are useful alternatives, but I would not put all of them in the main 15.

### Large multimodal agents: a survey (2025)

Source:

- https://link.springer.com/article/10.1007/s44267-025-00093-y

Use if:

- the thesis shifts toward agentic multimodal workflows.

Why not core:

- your system explicitly avoids becoming an agent runtime.

### From language to action: LLMs as autonomous agents and tool users (2026)

Source:

- https://link.springer.com/article/10.1007/s10462-025-11471-9

Use if:

- you need stronger literature for OpenClaw/ZeroClaw/Goose comparison.

Why not core:

- agent tooling is related work, not the implementation center.

### Remembering with AI: From Distributed Memory to AI-Curated and Human-AI Co-Memory (2026)

Source:

- https://link.springer.com/article/10.1007/s13164-026-00815-1

Use if:

- you want philosophical/cognitive framing of AI-curated memory.

Why not core:

- less directly useful for software requirements/design.

### Cryptography-based privacy-preserving LLMs survey (2026)

Source:

- https://link.springer.com/article/10.1007/s10462-025-11466-6

Use if:

- you decide to discuss zero-knowledge/confidential inference.

Why not core:

- your agreed privacy model is trusted user-owned cloud, not cryptographic zero-knowledge processing.

### Personal Knowledge Base Designer (SoftwareX 2020)

Source:

- https://www.sciencedirect.com/science/article/pii/S2352711019303334

Use if:

- you want more knowledge-base software engineering references.

Why not core:

- it is rule/expert-system oriented, not selective capture/Obsidian/cloud curation.

### A Hybrid System for Building a Personal Knowledge Base (2020)

Source:

- https://www.sciencedirect.com/science/article/pii/S187705092030243X

Use if:

- you need one more direct PKB system source.

Why not core:

- weaker than the 2025 Obsidian second-brain case study for your project.

## Recommended Core Set: 15 Sources

### 1. Jones and Teevan, *Personal Information Management* (2007)

Type: book

Use for:

- Chapter 1.2 Domain area analysis
- personal information management foundations
- fragmentation of personal information
- capture, organization, maintenance, retrieval

Source:

- https://www.microsoft.com/en-us/research/publication/personal-information-management/

Status:

- Old but foundational. Keep.

### 2. Bergman and Whittaker, *The Science of Managing Our Digital Stuff* (2016)

Type: book

Use for:

- Chapter 1.2 Domain area analysis
- personal information as curation
- why user-owned organization still matters
- folders/tags/search tradeoffs

Source:

- https://mitpress.mit.edu/9780262336284/the-science-of-managing-our-digital-stuff/

Status:

- Stable and highly relevant. Keep.

### 3. Dumais et al., "Stuff I've Seen" (SIGIR 2003)

Type: paper

Use for:

- Chapter 1.3.2 Known algorithmic and technical solutions
- personal retrieval over previously seen information
- contextual retrieval cues

Source:

- https://www.microsoft.com/en-us/research/publication/stuff-ive-seen-a-system-for-personal-information-retrieval-and-re-use/

Status:

- Old but classic. Keep.

### 4. Gemmell, Bell, and Lueder, "MyLifeBits" (CACM 2006)

Type: paper

Use for:

- Chapter 1.3 Existing solutions
- lifelogging and capture-everything baseline
- contrast with selective capture

Source:

- https://cacm.acm.org/research/mylifebits/

Status:

- Old but foundational. Keep.

### 5. Kleppmann et al., "Local-First Software" (Onward! 2019)

Type: paper

Use for:

- Chapter 3.1 Software architecture
- local-first principles
- user ownership despite cloud
- backup/sync tradeoffs

Sources:

- https://martin.kleppmann.com/2019/10/23/local-first-at-onward.html
- https://www.inkandswitch.com/essay/local-first/

Status:

- Stable architectural foundation. Keep.

### 6. Kleppmann, *Designing Data-Intensive Applications* (2017)

Type: book

Use for:

- Chapter 3.1 Architecture
- Chapter 3.2 architectural decisions
- database, queue, reliability, backup/recovery, maintainability

Source:

- https://martin.kleppmann.com/2017/03/27/designing-data-intensive-applications.html

Status:

- Stable engineering foundation. Keep.

### 7. Nissenbaum, *Privacy in Context* (2010)

Type: book

Use for:

- Chapter 3.4 Data security analysis
- privacy as contextual information flow
- justification for policy-driven upload/write decisions

Source:

- https://lawcat.berkeley.edu/record/385916

Status:

- Stable theory source. Keep.

### 8. LongMemEval (Wu et al., 2024)

Type: paper

Use for:

- Chapter 1.3.2 memory/evaluation solutions
- Chapter 2 requirements and evaluation
- temporal reasoning, knowledge updates, abstention

Source:

- https://arxiv.org/abs/2410.10813

Status:

- Still useful, but not newest. Keep as benchmark reference.

### 9. MemGPT (Packer et al., 2023)

Type: paper

Use for:

- Chapter 1.3.2 algorithmic solutions
- LLM memory hierarchy
- contrast with Markdown/user-owned artifact memory

Source:

- https://arxiv.org/abs/2310.08560

Status:

- Not latest SOTA, but important historical memory architecture. Keep if space allows.

### 10. Memory for Autonomous LLM Agents: Mechanisms, Evaluation, and Emerging Frontiers (Du, 2026)

Type: survey paper / preprint

Use for:

- Chapter 1.3.2 current SOTA survey
- agent memory taxonomy
- write-manage-read loop
- memory evaluation and emerging gaps
- privacy governance, contradiction handling, latency budgets

Source:

- https://huggingface.co/papers/2603.07670

Status:

- Current 2026 SOTA survey. Add/replace older memory sources where needed.

### 11. HiMem: Hierarchical Long-Term Memory for LLM Long-Horizon Agents (2026)

Type: paper / preprint

Use for:

- Chapter 1.3.2 current memory architectures
- hierarchical episode/note memory
- dynamic updates and memory reconsolidation
- useful analog for Source/Knowledge note split

Sources:

- https://huggingface.co/papers/2601.06377
- https://papers.cool/arxiv/2601.06377

Status:

- Current 2026 source. Use cautiously as preprint.

### 12. APEX-MEM: Agentic Semi-Structured Memory with Temporal Reasoning (2026)

Type: paper / preprint

Use for:

- Chapter 1.3.2 current memory SOTA
- temporal property graph
- append-only storage
- conflict/evolving information handling
- relevant to stale/contradictory note handling

Source:

- https://papers.cool/arxiv/2604.14362

Status:

- Very current 2026 source. Use cautiously as preprint.

### 13. InjecAgent (Zhan et al., 2024)

Type: paper

Use for:

- Chapter 3.4 Data security analysis
- captured web content as hostile input
- indirect prompt injection in tool-integrated agents
- justification for no arbitrary tool execution and policy validation

Source:

- https://arxiv.org/abs/2403.02691

Status:

- Still highly relevant. Keep.

### 14. OWASP Top 10 for LLM Applications 2025

Type: security standard / industry report

Use for:

- Chapter 3.4 Data security analysis
- prompt injection
- sensitive information disclosure
- excessive agency
- vector/embedding weaknesses
- unbounded consumption

Source:

- https://owasp.org/www-project-top-10-for-large-language-model-applications/assets/PDF/OWASP-Top-10-for-LLMs-v2025.pdf

Status:

- Current practical security source. Include.

### 15. Gemma 4 official Google announcement / docs (2026)

Type: official technical/product source

Use for:

- Chapter 3.2 tool/model choice justification
- local/cloud small model discussion
- update from Gemma 3 to Gemma 4
- edge models and Apache 2.0 licensing

Sources:

- https://blog.google/innovation-and-ai/technology/developers-tools/gemma-4/
- https://ai.google.dev/gemma/docs

Status:

- Current as of April-May 2026. Include as technical documentation, not academic theory.

## If The Supervisor Wants Only Academic Books/Papers

Use these 12:

1. Jones and Teevan, *Personal Information Management*
2. Bergman and Whittaker, *The Science of Managing Our Digital Stuff*
3. Dumais et al., "Stuff I've Seen"
4. Gemmell et al., "MyLifeBits"
5. Kleppmann et al., "Local-First Software"
6. Kleppmann, *Designing Data-Intensive Applications*
7. Nissenbaum, *Privacy in Context*
8. LongMemEval
9. MemGPT
10. Memory for Autonomous LLM Agents, 2026 survey
11. HiMem, 2026
12. APEX-MEM, 2026

Then cite OWASP, NIST, Gemma 4, Screenpipe, Khoj, OpenClaw, ZeroClaw, and QMD separately as standards/product/technical documentation, not core academic bibliography.

## If You Need Exactly 15 And Want Strong Practical Coverage

Use the full 15 above.

This gives a balanced bibliography:

- 7 stable foundations
- 5 current memory/LLM sources
- 2 security/risk sources
- 1 current model/tool source

## Sources To Drop Or Demote

### Drop from core list if limited

- Zep and Mem0:
  - still useful, but newer 2026 memory papers/surveys now cover the field better.
  - cite in related work if discussing products/memory layers.

- GLiNER:
  - useful if local entity extraction becomes central.
  - otherwise keep in implementation notes, not core bibliography.

- NIST AI RMF:
  - useful but broad.
  - OWASP LLM Top 10 is more directly relevant to the LLM security chapter.

### Keep as product/analog references

- Khoj docs
- Screenpipe docs
- QMD GitHub
- OpenClaw / ZeroClaw docs
- Goose docs
- Obsidian plugin/forum sources

These are important for Chapter 1.3.1 known software products, but they are not "books and papers."

## Mapping To Thesis Structure

### Introduction

- Jones and Teevan
- Bergman and Whittaker
- Kleppmann local-first

### 1.1 Statement of the diploma project task

- Jones and Teevan
- Local-First Software
- Nissenbaum

### 1.2 Domain area analysis

- Jones and Teevan
- Bergman and Whittaker
- Stuff I've Seen
- MyLifeBits

### 1.3 Analysis of existing solutions

Known software products:

- Khoj
- Screenpipe
- QMD
- OpenClaw
- ZeroClaw
- Obsidian AI plugins

Known algorithmic/technical solutions:

- LongMemEval
- MemGPT
- Memory for Autonomous LLM Agents
- HiMem
- APEX-MEM
- Gemma 4 docs

### 1.4 Business process modeling

- PIM books
- Local-first software
- DDIA

### 2 Requirements

- PIM books for user needs
- Nissenbaum for privacy/contextual policy
- OWASP for security-driven non-functional requirements
- DDIA for reliability/backup/maintainability

### 3 Architecture / Design / Development

- DDIA
- Local-First Software
- Gemma 4 docs
- OWASP LLM Top 10
- InjecAgent
- current memory papers for curation pipeline

### 3.4 Data Security Analysis

- Nissenbaum
- InjecAgent
- OWASP LLM Top 10
- optionally NIST AI RMF if you need broader risk-management framing
