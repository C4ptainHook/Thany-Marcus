# Bachelor Topic Ideas To Test During A 1-Week Thesis Hackathon

Purpose: generate concrete bachelor-work candidates from the research. Each idea is framed as something you can test in one intensive week before committing.

## How To Use This File

For each idea, the hackathon goal is not to build the final product. The goal is to answer:

- Is this novel enough?
- Is this feasible enough?
- Is this defensible as bachelor work?
- Can I evaluate it with evidence?
- Does it avoid being just a clone of Khoj, Screenpipe, OpenClaw, ZeroClaw, or an Obsidian plugin?

Best outcome after one week:

> Pick one main thesis direction, one fallback direction, and one evaluation plan.

## Idea 1: Privacy-Audited Markdown Knowledge Curation Layer

### Core Idea

Build a service that takes explicit user captures and turns them into reviewable Markdown notes. The system logs every model/provider/data-flow decision so the user can audit what happened.

### Thesis Claim

Personal knowledge systems can use AI without becoming opaque cloud memory by storing knowledge as reviewable Markdown artifacts and auditing the data flow of each AI operation.

### What To Test In One Week

- Define capture envelope schema.
- Make 20-50 sample captures.
- Design what the audit log should contain.
- Manually or semi-automatically transform captures into Markdown notes.
- Compare local/tiny model vs stronger model for:
  - tags
  - entities
  - summary
  - suggested links
  - privacy class

### Evidence To Produce

- 5-10 example generated Markdown notes.
- Audit log examples.
- Table comparing local/tiny/cloud model outputs.
- List of failure cases.

### Why It Is Strong

- Avoids cloning Khoj or Obsidian agents.
- Has privacy, architecture, and evaluation angles.
- Markdown artifacts are easy to demonstrate.

### Main Risk

Too broad unless limited to capture-to-note curation and audit.

### Key Resources

- Kleppmann et al., "Local-First Software"  
  https://www.inkandswitch.com/essay/local-first/
- Nissenbaum, *Privacy in Context*
- Jones and Teevan, *Personal Information Management*
- LongMemEval  
  https://arxiv.org/abs/2410.10813
- InjecAgent  
  https://arxiv.org/abs/2403.02691

## Idea 2: Tiny Local Models As Privacy Gatekeepers For Personal Knowledge Capture

### Core Idea

Evaluate whether super tiny local models can do first-pass privacy and curation tasks before any data leaves the user's device.

### Thesis Claim

Sub-1B local models are not sufficient as full second-brain curators, but they may be useful as privacy-preserving gatekeepers for classification, routing, PII detection, entity extraction, and duplicate candidate selection.

### What To Test In One Week

- Create 100 small capture snippets.
- Label them manually:
  - source type
  - privacy class
  - PII presence
  - entities
  - desired tags
  - cloud allowed / not allowed
- Test tiny models/tools:
  - Gemma 3 270M
  - Qwen3 0.6B
  - Qwen2.5 0.5B
  - SmolLM2 135M/360M
  - MiniLM or EmbeddingGemma for retrieval
  - GLiNER/spaCy/Presidio for entities and PII

### Evidence To Produce

- Accuracy table.
- Latency and memory table.
- Examples where tiny models fail.
- Decision: which tasks can stay local and which need bigger models.

### Why It Is Strong

- Very measurable.
- Clear bachelor-scope experiment.
- Useful even if the models fail, because failure boundaries are a result.

### Main Risk

May become too benchmark-focused and less product-like.

### Key Resources

- Gemma 3 270M  
  https://developers.googleblog.com/en/introducing-gemma-3-270m/
- Qwen3 0.6B  
  https://huggingface.co/Qwen/Qwen3-0.6B
- EmbeddingGemma  
  https://ai.google.dev/gemma/docs/embeddinggemma
- GLiNER  
  https://arxiv.org/abs/2311.08526
- Microsoft Presidio  
  https://github.com/microsoft/presidio

## Idea 3: Selective Capture vs Ambient Capture For Second-Brain Use

### Core Idea

Compare explicit user-triggered capture against always-on ambient capture for personal knowledge management.

### Thesis Claim

Selective capture may preserve enough useful context for personal knowledge retrieval while reducing privacy exposure and data volume compared with ambient capture.

### What To Test In One Week

- Simulate 3 workflows:
  - manual notes
  - selective captures
  - ambient/raw capture log
- Use the same 1-2 day realistic scenario:
  - chats
  - articles
  - project notes
  - meeting excerpts
- Create questions over the captured data.
- Measure:
  - retrieval success
  - amount of sensitive data captured
  - data volume
  - time/effort
  - note usefulness

### Evidence To Produce

- Small dataset.
- Query set.
- Comparison table.
- Privacy exposure examples.

### Why It Is Strong

- Directly addresses the Microsoft Recall / Screenpipe / Rewind tension.
- Good privacy/product thesis.
- Does not require building always-on capture.

### Main Risk

Evaluation design must be honest. If the dataset is too artificial, the result is weak.

### Key Resources

- MyLifeBits  
  https://cacm.acm.org/research/mylifebits/
- Stuff I've Seen  
  https://www.microsoft.com/en-us/research/publication/stuff-ive-seen-a-system-for-personal-information-retrieval-and-re-use/
- Screenpipe docs  
  https://docs.screenpi.pe/
- Microsoft Recall privacy docs  
  https://support.microsoft.com/en-us/windows/privacy-and-control-over-your-recall-experience-d404f672-7647-41e5-886c-a3c59680af15

## Idea 4: Review Queue For AI-Written Markdown Vault Changes

### Core Idea

Do not let AI directly rewrite the user's second brain. Instead, build/evaluate a review model where AI proposes Markdown changes and the user accepts/rejects them.

### Thesis Claim

AI-assisted personal knowledge systems need reviewable, reversible, source-attributed changes to preserve user trust and prevent silent knowledge corruption.

### What To Test In One Week

- Design a patch format for Markdown vault changes.
- Create examples:
  - new note
  - append to existing note
  - add wikilink
  - mark duplicate
  - mark stale fact
  - detect contradiction
- Build a manual mock review flow or simple prototype.
- Test whether users can understand and approve/reject changes.

### Evidence To Produce

- Patch/audit format.
- 10-20 realistic proposed changes.
- Failure examples.
- User-review checklist.

### Why It Is Strong

- Narrow and thesis-friendly.
- Avoids full second-brain product scope.
- Directly relevant to agent safety and trust.

### Main Risk

May look like UX/design unless tied to provenance, auditability, and knowledge integrity.

### Key Resources

- Nissenbaum, *Privacy in Context*
- InjecAgent  
  https://arxiv.org/abs/2403.02691
- OpenClaw security docs  
  https://docs.openclaw.ai/security
- Microsoft OpenClaw runtime risk analysis  
  https://www.microsoft.com/en-us/security/blog/2026/02/19/running-openclaw-safely-identity-isolation-runtime-risk/

## Idea 5: Stale Knowledge And Contradiction Detection In Markdown Vaults

### Core Idea

Most second-brain systems help add knowledge. This idea focuses on identifying stale, duplicated, or contradictory knowledge and presenting it for review.

### Thesis Claim

A personal knowledge base needs maintenance mechanisms for stale and contradictory facts; AI can help detect candidates, but final decisions should remain reviewable.

### What To Test In One Week

- Create a small vault with:
  - duplicate notes
  - outdated decisions
  - changed deadlines
  - contradictory project facts
  - old assumptions later corrected
- Test whether embeddings + LLM extraction can find:
  - duplicate
  - elaboration
  - contradiction
  - superseded fact
- Design Markdown representation:
  - `supersedes`
  - `superseded_by`
  - `status: stale`
  - source evidence links

### Evidence To Produce

- Dataset of stale/contradictory notes.
- Detection results.
- Precision/recall rough estimate.
- Examples of useful review cards.

### Why It Is Strong

- More original than "AI writes notes".
- Strong evaluation story.
- Naturally fits Markdown vaults.

### Main Risk

Contradiction detection is hard. The thesis should frame it as candidate detection, not guaranteed truth maintenance.

### Key Resources

- LongMemEval  
  https://arxiv.org/abs/2410.10813
- Zep temporal knowledge graph paper  
  https://arxiv.org/abs/2501.13956
- Mem0  
  https://arxiv.org/abs/2504.19413
- Personal Information Management literature

## Idea 6: Markdown Memory Backend For Agent Runtimes

### Core Idea

Instead of building another agent runtime, build a memory backend that OpenClaw/ZeroClaw/Goose-like agents could call. It stores long-term memory as user-owned Markdown.

### Thesis Claim

Agent memory should be inspectable, editable, and portable. Markdown can serve as a durable memory artifact layer if writes are structured, source-attributed, and audited.

### What To Test In One Week

- Define minimal API:
  - store memory
  - search memory
  - propose note update
  - link related notes
  - retrieve context
- Map agent memory event -> Markdown artifact.
- Compare with opaque vector-memory style.
- Prototype with manual calls or CLI.

### Evidence To Produce

- API sketch.
- Markdown memory examples.
- Comparison against Mem0/Zep/Letta concepts.
- Integration story for OpenClaw/ZeroClaw/Goose.

### Why It Is Strong

- Uses agent-runtime trend without competing directly.
- Clear architectural contribution.
- Good fit with original Markdown-cloud idea.

### Main Risk

Could become too abstract if no prototype/evaluation exists.

### Key Resources

- MemGPT  
  https://arxiv.org/abs/2310.08560
- Mem0  
  https://arxiv.org/abs/2504.19413
- Zep  
  https://arxiv.org/abs/2501.13956
- Goose docs  
  https://goose-docs.ai/
- ZeroClaw docs  
  https://docs.zeroclawlabs.ai/en/
- OpenClaw architecture  
  https://docs.openclaw.ai/concepts/architecture

## Idea 7: Self-Hosted Private Knowledge Stack With Auditable Model Routing

### Core Idea

Build a self-hostable stack for personal knowledge capture where raw content only flows to user-controlled endpoints in private mode.

### Thesis Claim

Self-hosting alone does not guarantee privacy; privacy requires explicit data-flow policy, provider allowlists, audit logs, and deployment profiles.

### What To Test In One Week

- Design deployment profiles:
  - local-only
  - private VPS
  - hybrid redaction
  - public cloud
- Draw data-flow diagrams.
- Define what is allowed in each mode.
- Create sample audit logs.
- Test whether network egress can be observed or restricted.

### Evidence To Produce

- Threat model.
- Data-flow diagrams.
- Deployment architecture.
- Network/audit proof-of-concept.

### Why It Is Strong

- Strong architecture/security direction.
- Fits original "cloud service" title.
- Avoids product clone.

### Main Risk

Can become infrastructure-heavy and less about second brain.

### Key Resources

- Local-First Software  
  https://www.inkandswitch.com/essay/local-first/
- n8n hosting docs  
  https://docs.n8n.io/hosting/
- Anytype self-host docs  
  https://doc.anytype.io/anytype-docs/advanced/data-and-security/self-hosting/self-hosted
- Nissenbaum, *Privacy in Context*

## Idea 8: Personal Knowledge Curation Benchmark

### Core Idea

Create a benchmark for converting messy personal context into useful Markdown notes.

### Thesis Claim

Existing LLM memory benchmarks do not directly evaluate whether AI can maintain human-readable personal knowledge artifacts. A small benchmark can measure curation quality, privacy leakage, and retrieval usefulness.

### What To Test In One Week

- Create 50-100 synthetic realistic captures.
- Create gold Markdown notes.
- Define tasks:
  - summarize
  - tag
  - extract facts
  - suggest links
  - detect duplicates
  - route privacy
  - answer retrieval questions
- Run 2-4 models/tools.

### Evidence To Produce

- Dataset schema.
- Example gold notes.
- Metric definitions.
- First benchmark results.

### Why It Is Strong

- Very defensible academically.
- Can support any implementation direction later.
- If done well, this can become the thesis core.

### Main Risk

Less product-like. Dataset quality matters a lot.

### Key Resources

- LongMemEval  
  https://arxiv.org/abs/2410.10813
- StructMemEval / newer agent memory benchmarks should be reviewed from current sources.
- Jones and Teevan, *Personal Information Management*
- Bergman and Whittaker, *The Science of Managing Our Digital Stuff*

## Best Candidates To Test First

### Best Overall

**Idea 1 + Idea 2 + Idea 4 combined**

Working topic:

> Privacy-aware AI curation of personal knowledge into reviewable Markdown notes using tiny local models for first-pass routing.

Why:

- Product artifact exists.
- Evaluation exists.
- Privacy claim exists.
- Tiny-model experiment exists.
- Avoids direct cloning.

### Most Academic

**Idea 8 + Idea 2**

Working topic:

> Benchmarking tiny local models for privacy-preserving personal knowledge curation.

Why:

- Clear measurements.
- Strong if supervisor likes research.
- Less engineering risk.

### Most Product-Like

**Idea 1 + Idea 3**

Working topic:

> Selective capture and Markdown curation for a privacy-preserving second brain.

Why:

- Easy to explain.
- User-facing.
- Directly contrasts with Screenpipe/Recall.

### Most Original Narrow Topic

**Idea 5**

Working topic:

> Detecting stale and contradictory knowledge in Markdown personal knowledge bases.

Why:

- Not many tools focus on forgetting/maintenance.
- Strong thesis problem.
- Hard but controllable if framed as candidate detection.

## One-Week Hackathon Plan

### Day 1: Choose Three Candidate Ideas

Pick:

- one product-like idea
- one research/evaluation idea
- one fallback idea

Recommended:

- Idea 1
- Idea 2
- Idea 5 or 8

### Day 2: Build Dataset

Create 50-100 realistic captures:

- chat snippets
- article excerpts
- project decisions
- duplicated facts
- stale updates
- sensitive/private snippets
- multilingual examples if relevant

### Day 3: Manual Gold Standard

For each capture, label:

- privacy class
- entities
- tags
- duplicate/related notes
- expected Markdown output
- whether cloud processing is allowed

### Day 4: Run Model/Tool Tests

Test:

- embeddings
- NER/PII tools
- one tiny model
- one medium/local or cloud model as upper bound

### Day 5: Analyze Failures

Find:

- where tiny models are enough
- where they fail
- where audit/review is necessary
- which thesis direction survives evidence

### Day 6: Draft Thesis Proposal Skeleton

Write:

- problem statement
- related work
- proposed contribution
- evaluation plan
- risks

### Day 7: Decide

Choose:

- final topic
- implementation scope
- evaluation scope
- what to explicitly exclude

## Shortlist Of Most Helpful Resources

### Books

- William Jones, *Keeping Found Things Found*
- William Jones and Jaime Teevan, *Personal Information Management*
- Ofer Bergman and Steve Whittaker, *The Science of Managing Our Digital Stuff*
- Helen Nissenbaum, *Privacy in Context*
- Martin Kleppmann, *Designing Data-Intensive Applications*
- Sönke Ahrens, *How to Take Smart Notes*

### Core Papers

- Kleppmann et al., "Local-First Software"  
  https://www.inkandswitch.com/essay/local-first/
- Dumais et al., "Stuff I've Seen"  
  https://www.microsoft.com/en-us/research/publication/stuff-ive-seen-a-system-for-personal-information-retrieval-and-re-use/
- Gemmell et al., "MyLifeBits"  
  https://cacm.acm.org/research/mylifebits/
- MemGPT  
  https://arxiv.org/abs/2310.08560
- LongMemEval  
  https://arxiv.org/abs/2410.10813
- Zep  
  https://arxiv.org/abs/2501.13956
- Mem0  
  https://arxiv.org/abs/2504.19413
- InjecAgent  
  https://arxiv.org/abs/2403.02691
- GLiNER  
  https://arxiv.org/abs/2311.08526

### Tools To Inspect

- Khoj  
  https://docs.khoj.dev/
- Screenpipe  
  https://docs.screenpi.pe/
- OpenClaw  
  https://docs.openclaw.ai/concepts/architecture
- ZeroClaw  
  https://docs.zeroclawlabs.ai/en/
- Goose  
  https://goose-docs.ai/
- Anytype self-hosting  
  https://doc.anytype.io/anytype-docs/advanced/data-and-security/self-hosting/self-hosted

## Final Recommendation

Start the hackathon with three concrete hypotheses:

1. **Tiny models can safely handle privacy routing and simple curation, but not final note synthesis.**
2. **Reviewable Markdown patches are a safer memory-write model than autonomous vault rewriting.**
3. **Selective capture can provide useful personal knowledge while reducing privacy exposure compared with ambient capture.**

If at least two of these survive the week, the bachelor topic is strong.

