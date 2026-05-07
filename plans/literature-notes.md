# Literature Notes

## Personal Information Management Foundations

### Jones and Teevan, *Personal Information Management* (2007)

Edited by William Jones and Jaime Teevan. This is one of the core academic references for the project because it defines PIM as the study and practice of acquiring, organizing, maintaining, and retrieving personal information for everyday use. Microsoft Research describes it as an integrative treatment of PIM research and emphasizes the fragmentation of personal information across documents, email, photos, instant messages, and other media.

Source: https://www.microsoft.com/en-us/research/publication/personal-information-management/

Use in thesis:

- Ground the problem historically.
- Explain why the system should integrate context from multiple sources, not only Markdown notes.
- Use PIM terminology: keeping, organizing, maintaining, finding, re-finding, privacy, context.

### Jones, *Keeping Found Things Found* (2007/2008)

William Jones' book is a comprehensive PIM reference. Elsevier describes it as focused on the activities people perform so that information can work for them in daily life, including how to measure whether PIM practices improve.

Sources:

- https://shop.elsevier.com/books/keeping-found-things-found-the-study-and-practice-of-personal-information-management/jones/978-0-12-370866-3
- https://www.oreilly.com/library/view/keeping-found-things/9780123708663/

Use in thesis:

- Justify why "organization" should mean future retrieval and reuse, not folder cleanup.
- Use its evaluation framing for whether a PIM system works.

### Bergman and Whittaker, *The Science of Managing Our Digital Stuff* (2016)

MIT Press book on why people organize personal digital information the way they do and how new PIM systems can improve that process. The book is especially relevant because it treats PIM as curation, not just search.

Source: https://mitpress.mit.edu/9780262336284/the-science-of-managing-our-digital-stuff/

Use in thesis:

- Support the product decision to preserve user-visible structure instead of hiding everything in an opaque AI memory database.
- Relevant to folder/project-based organization, subjective user organization, and the limits of "just search."

## Re-Finding and Personal Search Systems

### Dumais et al., "Stuff I've Seen" (SIGIR 2003)

The system indexed previously seen personal information across email, web pages, documents, appointments, and other sources. Microsoft Research notes that the system used rich contextual cues and was deployed internally to more than 230 employees; time and people were important retrieval cues.

Source: https://www.microsoft.com/en-us/research/publication/stuff-ive-seen-a-system-for-personal-information-retrieval-and-re-use/

Use in thesis:

- Key predecessor for "personal context retrieval."
- Supports using metadata such as time, source app, people, and project as first-class capture fields.
- Shows that the problem is re-use of seen information, not generic web search.

### MyLifeBits, Gemmell, Bell, and Lueder (CACM 2006)

MyLifeBits explored a personal database for everything: scanned documents, born-digital files, email, photos, web pages, phone calls, meetings, room conversations, keystrokes, mouse clicks, and SenseCam photos.

Sources:

- https://cacm.acm.org/research/mylifebits/
- https://www.microsoft.com/en-us/research/publication/mylifebits-a-personal-database-for-everything/

Use in thesis:

- Foundational lifelogging predecessor.
- Useful contrast: "capture everything" is powerful but creates overwhelming retrieval, privacy, and summarization problems.
- Your project can explicitly choose selective capture as a privacy-preserving alternative.

## Local-First and Data Ownership

### Kleppmann, Wiggins, van Hardenberg, and McGranaghan, "Local-First Software" (Onward! 2019)

The local-first paper argues for software that supports collaboration and multi-device use while preserving user ownership and agency. It emphasizes offline availability, longevity, privacy, and user control, and discusses CRDTs as a foundation.

Sources:

- https://martin.kleppmann.com/2019/10/23/local-first-at-onward.html
- https://www.inkandswitch.com/essay/local-first/

Use in thesis:

- Main architectural reference for local-first/private-first design.
- Helps justify "vault as source of truth" and optional sync rather than server-authoritative cloud storage.
- Gives vocabulary for the private/public deployment profile design.

### Kleppmann, *Designing Data-Intensive Applications* (2017)

Not specific to PIM, but useful for the engineering side: storage, replication, consistency, distributed systems, logs, and data models.

Use in thesis:

- Background for sync architecture, audit logs, durability, and conflict handling.

## Note-Taking and Second Brain References

### Ahrens, *How to Take Smart Notes* (2017; revised 2022)

Explains the Zettelkasten method and why atomic notes, links, and writing-to-think workflows matter.

Sources:

- https://openlibrary.org/works/OL18635700W/How_to_Take_Smart_Notes
- https://books.google.com/books/about/How_to_Take_Smart_Notes.html?id=wzv9zgEACAAJ

Use in thesis:

- Supports the idea that generated notes should be atomic and linkable, not only long summaries.
- Use carefully: it is practitioner literature, not a systems paper.

### Forte, *Building a Second Brain* (2022)

Popular knowledge-work book around a personal system for managing digital life and creative work.

Sources:

- https://openlibrary.org/works/OL26417584W/Building_a_Second_Brain
- https://books.google.com/books/about/Building_a_Second_Brain.html?id=0wZQEAAAQBAJ

Use in thesis:

- Useful for product/user motivation.
- Do not rely on it as the main academic foundation.

## Privacy Theory

### Nissenbaum, *Privacy in Context* (2010)

Introduces contextual integrity: privacy depends on appropriate information flows within social contexts, not merely secrecy or individual control.

Sources:

- https://lawcat.berkeley.edu/record/385916
- https://books.google.com/books/about/Privacy_in_Context.html?id=cxb15VjOzCYC

Use in thesis:

- Strong conceptual foundation for private/public modes.
- Helps frame capture consent by context: Slack message, work meeting, personal diary, medical note, public web page should not share the same default data-flow policy.

## LLM Memory and Agent Memory

### MemGPT / Letta

MemGPT introduced self-editing memory, memory hierarchy, and LLM OS concepts. Letta's documentation describes the architecture as core in-context memory plus out-of-context recall and archival memory.

Sources:

- https://docs.letta.com/concepts/letta
- https://docs.letta.com/guides/agents/architectures/memgpt

Use in thesis:

- Important baseline for agent memory.
- Your project should not compete directly; use the memory hierarchy concepts but store user knowledge in transparent Markdown.

### LongMemEval (2024)

Benchmark for long-term interactive memory in chat assistants. It evaluates information extraction, multi-session reasoning, temporal reasoning, knowledge updates, and abstention.

Source: https://huggingface.co/papers/2410.10813

Use in thesis:

- Borrow evaluation categories for a smaller thesis-scale benchmark.
- Especially relevant to stale facts, updates, and temporal reasoning.

### Zep: Temporal Knowledge Graph Architecture for Agent Memory (2025)

Zep proposes a temporal knowledge graph memory layer for agents and reports improvements on Deep Memory Retrieval and LongMemEval-like tasks.

Sources:

- https://huggingface.co/papers/2501.13956
- https://blog.getzep.com/zep-a-temporal-knowledge-graph-architecture-for-agent-memory/

Use in thesis:

- Strong reference for graph-based temporal memory.
- Your differentiation: personal Markdown artifact layer, local/private deployment, and consented capture.

### Mem0 (2025)

Mem0 proposes scalable long-term memory for AI agents with dynamic extraction, consolidation, retrieval, and a graph-based variant.

Sources:

- https://huggingface.co/papers/2504.19413
- https://github.com/mem0ai/mem0

Use in thesis:

- Relevant production baseline for memory extraction/consolidation.
- Useful comparison point: memory service vs. user-owned Markdown vault.

## LLM Privacy and Security

### Threats in LLM-Agent Workflows (Ferrag et al., 2025)

Survey/threat model for LLM-powered agents, covering prompt injections, tool/protocol exploits, privacy attacks, and brittle integrations.

Sources:

- https://huggingface.co/papers/2506.23260
- https://www.sciencedirect.com/science/article/pii/S2405959525001997

Use in thesis:

- Threat-model reference for tool-integrated capture systems.
- Supports treating captured external content as untrusted.

### InjecAgent (2024)

Benchmark for indirect prompt injection in tool-integrated LLM agents.

Source: https://huggingface.co/papers/2403.02691

Use in thesis:

- Important if the system reads external content such as web pages, Slack, email, or documents before invoking tools.
- Supports sandboxing model outputs and requiring user approval before destructive note rewrites or data egress.

### Confidential Prompting (2024)

Explores protecting user prompts from cloud LLM providers through secure multi-party decoding and confidential computing.

Source: https://huggingface.co/papers/2409.19134

Use in thesis:

- Reference for privacy-preserving cloud inference.
- Likely too complex to implement fully, but useful as related work.

### PII Redaction / Desensitization

DePrompt and newer redaction evaluations study reducing PII leakage in LLM prompts.

Sources:

- https://aisecurity-portal.org/en/literature-database/deprompt-desensitization-and-evaluation-of-personal-identifiable-information-in-large-language-model-prompts/
- https://huggingface.co/papers/2604.12064

Use in thesis:

- Support the hybrid mode idea: local model redacts/summarizes before cloud calls.
- A thesis prototype can evaluate simple local redaction plus semantic summarization instead of full cryptographic privacy.

