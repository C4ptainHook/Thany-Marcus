# SOTA Recheck: 2026-05-03

Purpose: perform the "recheck later" step now for the volatile areas: agent runtimes, second-brain competitors, local/tiny models, and security claims.

## Verdict

The previous high-level direction still holds, but the justification should be sharper:

> Do not build a generic self-hosted personal AI assistant or generic Obsidian vault agent. That space is now clearly active and crowded. The defensible thesis direction is a **privacy-audited Markdown knowledge curation layer** with explicit capture, reviewable writes, model-routing policy, and evaluation.

The most important update is that agent runtimes and second-brain competitors are stronger than the first notes implied.

## Major Corrections

### 1. Goose Repository Changed

Earlier notes used `github.com/block/goose`. That is now stale.

Goose docs say Block donated goose to the Agentic AI Foundation under the Linux Foundation. The repository moved from `block/goose` to:

- https://github.com/aaif-goose/goose

Source:

- https://goose-docs.ai/blog/2026/04/07/goose-moves-to-aaif/

Implication:

- Goose is more institutionally relevant than first noted.
- It is not just a Block side project; it is now part of the AAIF ecosystem together with MCP and AGENTS.md.
- Any thesis that builds a generic desktop/CLI/API local agent should treat Goose as a serious baseline/competitor.

### 2. Khoj Is A Direct Second-Brain Competitor

Khoj docs explicitly position it as "Your Second Brain". It is open source, personal AI, works with shared files, understands PDFs, plaintext, Markdown, org-mode, and Notion pages, integrates with Emacs/Obsidian/desktop/web, and can be self-hosted on consumer hardware.

Sources:

- https://docs.khoj.dev/
- https://docs.khoj.dev/get-started/setup/

Implication:

- A broad "self-hosted AI second brain over notes/docs" is too close to Khoj.
- Differentiation must be narrower:
  - explicit capture curation
  - audit logs
  - privacy-mode enforcement
  - local tiny-model triage
  - reviewable Markdown writes
  - stale/contradiction handling

### 3. Obsidian Vault Agents Are Active In 2026

Obsidian forum posts show multiple 2026 projects that put Claude Code/Codex/Gemini-like agents inside Obsidian or make the vault an agent workspace.

Examples:

- Cortex: an AI Obsidian vault agent powered by Claude Code, with read/write/create/move access, session persistence, context system, and autonomous memory.
- Obsidian Vault Agent: terminal/agent workflow integration.
- Claude Sidebar / Agent Client / other plugins that embed Claude Code or agent CLIs in Obsidian.

Sources:

- https://forum.obsidian.md/t/plugin-cortex-an-ai-obsidian-vault-agent-powered-by-claude-code/112430
- https://forum.obsidian.md/t/new-plugin-obsidian-vault-agent-terminal-with-claude-code-codex/110024
- https://forum.obsidian.md/t/claude-code-from-the-sidebar/109634
- https://forum.obsidian.md/t/new-plugin-agent-client-bring-claude-code-codex-gemini-cli-inside-obsidian/108448

Implication:

- "AI can read/write/organize Obsidian vault" is not novel enough.
- The thesis must not be an Obsidian plugin as the main contribution.
- Obsidian compatibility should remain an output format/integration, not the core novelty.

### 4. Screenpipe Is A Stronger Ambient Capture Baseline

Screenpipe docs now explicitly mention:

- 24/7 screen/audio capture
- local storage
- OCR/accessibility extraction
- audio transcription
- AI search/API
- pipes for summaries/time tracking/Obsidian sync
- MCP server for Claude/Cursor/ChatGPT and other AI tools
- 45+ integrations

Sources:

- https://docs.screenpi.pe/
- https://screenpi.pe/about
- https://github.com/screenpipe/screenpipe

Implication:

- The project should not compete on ambient capture.
- Selective capture must be justified and evaluated as a privacy/quality tradeoff.
- Screenpipe is the right ambient baseline if comparing capture strategies.

### 5. Anytype Private Modes Are Real

Anytype docs confirm local-only and self-hosted network modes. Local-only disables the backup node; self-hosted mode uses a self-hosted network configuration and dedicated identities.

Sources:

- https://doc.anytype.io/anytype-docs/advanced/data-and-security/self-hosting/local-only
- https://doc.anytype.io/anytype-docs/advanced/data-and-security/self-hosting/self-hosted
- https://doc.anytype.io/anytype-docs/data-and-security/self-hosting

Implication:

- Do not claim "nobody has local/private modes."
- Better claim: existing private modes do not center **AI content-routing audit for Markdown curation**.

## Agent Runtime Recheck

### ZeroClaw

Official docs describe ZeroClaw as:

- personal AI assistant owned by the user
- written in Rust
- agent runtime
- single binary
- talks to LLM providers including Anthropic, OpenAI, Ollama, and others
- reaches the world through channels such as Discord, Telegram, Matrix, email, voice, webhooks, CLI
- acts through tools such as shell, browser, HTTP, hardware, custom MCP servers
- runs on the user's machine with the user's keys and workspace

Sources:

- https://docs.zeroclawlabs.ai/en/
- https://github.com/zeroclaw-labs/zeroclaw
- https://www.zeroclaw.dev/

Relevance:

- Strong competitor if the thesis becomes an agent runtime.
- Useful integration target if the thesis becomes a Markdown memory/curation service.
- Promotional performance claims such as `<5MB RAM`, `<10ms startup`, `3.4MB binary`, and `22+ providers` should be cited only as project claims or measured independently.

### OpenClaw

Official sources describe OpenClaw as a personal AI assistant with a gateway architecture. The GitHub repo currently shows heavy activity and a very large public footprint. Docs describe:

- gateway daemon
- messaging surfaces
- WebSocket control plane
- nodes with explicit capabilities/commands
- model provider setup through onboarding

Sources:

- https://github.com/openclaw/openclaw
- https://openclaw.ai/
- https://docs.openclaw.ai/start/getting-started
- https://docs.openclaw.ai/concepts/architecture

Relevance:

- Direct threat to a broad "self-hosted personal AI assistant" project.
- Useful warning for security.
- Less direct threat to a narrow Markdown curation/evaluation thesis.

### Goose

Goose is now under AAIF. Current docs describe:

- native open-source AI agent
- desktop app, CLI, and API
- for code, workflows, research, writing, automation, data analysis
- runs on the user's machine
- Rust implementation
- extensions through MCP

Sources:

- https://goose-docs.ai/
- https://goose-docs.ai/blog/2026/04/07/goose-moves-to-aaif/
- https://github.com/aaif-goose/goose

Relevance:

- Serious baseline for local agent shell / desktop agent work.
- Supports the recommendation to avoid generic local agent runtime scope.
- MCP compatibility is worth considering if the project exposes a curation service to agents.

## Security Recheck

OpenClaw-style runtime security is more central than before.

Microsoft Security published a detailed analysis on 2026-02-19 warning that self-hosted agent runtimes like OpenClaw can ingest untrusted text, download/execute skills, and act using assigned credentials. Microsoft says OpenClaw should be treated as untrusted code execution with persistent credentials and should not be run on a standard workstation if evaluating it seriously.

Source:

- https://www.microsoft.com/en-us/security/blog/2026/02/19/running-openclaw-safely-identity-isolation-runtime-risk/

OpenClaw's own security docs say prompt injection is not solved and that hard enforcement comes from tool policy, approvals, sandboxing, and channel allowlists.

Source:

- https://docs.openclaw.ai/security

Implication for thesis:

- Do not include broad tool execution unless security becomes the main thesis.
- Do not add a skill marketplace.
- Do not allow silent autonomous vault rewrites.
- Treat captured content as hostile.
- Require audit logs and user approval for write/destructive actions.
- Keep the system focused on capture/curation, not arbitrary execution.

## Tiny / Local Model Recheck

### Gemma 3 270M

Google released Gemma 3 270M on 2025-08-14 as a compact 270M model for task-specific fine-tuning, instruction-following, and text structuring.

Sources:

- https://developers.googleblog.com/en/introducing-gemma-3-270m/
- https://ai.google.dev/gemma/docs/releases

Implication:

- Good candidate for privacy triage, routing, short structured extraction, or fine-tuned classifiers.
- Not enough evidence to trust it for final note synthesis or factual memory.

### EmbeddingGemma

Google docs describe EmbeddingGemma as a 308M multilingual text embedding model for retrieval, semantic similarity, classification, and clustering, with 2K input context and flexible output dimensions.

Sources:

- https://ai.google.dev/gemma/docs/embeddinggemma
- https://ai.google.dev/gemma/docs/embeddinggemma/model_card

Implication:

- Strong candidate for local semantic search and duplicate detection.
- More relevant than tiny generative models for retrieval.

### Qwen3 0.6B

Qwen3-0.6B model card lists Apache 2.0 license, 0.6B parameters, 32,768 context length, and thinking/non-thinking modes.

Source:

- https://huggingface.co/Qwen/Qwen3-0.6B

Implication:

- Good candidate for local classification/extraction experiments.
- Needs empirical evaluation on project-specific data.

### SmolLM2

SmolLM2 model cards confirm 135M, 360M, and 1.7B compact models intended for on-device use.

Sources:

- https://huggingface.co/HuggingFaceTB/SmolLM2-135M
- https://huggingface.co/HuggingFaceTB/SmolLM2-360M

Implication:

- Good experimental lower bound.
- Do not assume useful production quality without testing.

## Agent Memory Research Recheck

The memory landscape has moved beyond the original MemGPT/Mem0/Zep/LongMemEval set.

New or newly surfaced 2026 items include:

- HiMem: hierarchical long-term memory for long-horizon agents.
- APEX-MEM: semi-structured memory with temporal reasoning.
- StructMemEval: benchmark for whether agents can organize memory into useful structures.
- Surveys on autonomous LLM agent memory through early 2026.
- Multiple commercial/benchmark claims around LongMemEval/LoCoMo/BEAM.

Sources found:

- https://papers.cool/arxiv/2601.06377
- https://papers.cool/arxiv/2604.14362
- https://app.argminai.com/arxiv-dashboard/papers/2602.11243v1
- https://www.researchgate.net/publication/401719157_Memory_for_Autonomous_LLM_AgentsMechanisms_Evaluation_and_Emerging_Frontiers
- https://graphonomous.com/benchmarks/beam.html

Caution:

- Many 2026 memory claims are preprints, vendor posts, or benchmark leaderboards.
- Treat as "new related work to inspect", not settled SOTA.
- The thesis should define its own smaller evaluation around Markdown curation, not chase memory leaderboard claims.

## Updated Competitor Threat Level

| Direction | Threat after recheck | Why |
| --- | --- | --- |
| Generic Obsidian AI vault agent | Very high | Cortex/Vault Agent/Claude Sidebar/Agent Client already exist. |
| Self-hosted AI second brain over Markdown/docs | High | Khoj directly covers this territory. |
| Ambient screen/audio memory | High | Screenpipe is strong and local. |
| Generic local/self-hosted personal AI assistant | Very high | OpenClaw, ZeroClaw, Goose, Open Interpreter occupy this space. |
| Tiny-model local triage for privacy | Medium | Models exist, but productized PKM use still needs evaluation. |
| Privacy-audited Markdown curation layer | Lower | Still differentiated if narrow and measured. |
| Stale/contradiction/forgetting review for Markdown vaults | Lower | Still relatively under-served and thesis-friendly. |

## Revised Best Project Formulation

Best current formulation:

> **A privacy-audited personal knowledge curation layer that transforms explicit captures into reviewable Markdown artifacts, using local/tiny models for first-pass routing and extraction, and exposing a narrow interface usable by humans or agent runtimes.**

This avoids:

- competing with Khoj as a full second brain
- competing with OpenClaw/ZeroClaw/Goose as an agent runtime
- competing with Screenpipe as ambient capture
- competing with Obsidian plugins as vault agents

And keeps a defensible contribution:

- privacy/data-flow audit
- explicit capture
- reviewable Markdown writes
- model routing across local/self-hosted/cloud modes
- evaluation of tiny vs larger models for curation tasks

## Actionable Thesis Scope After Recheck

Must include:

- explicit capture envelope
- local privacy classifier/router
- local embeddings for duplicate/related-note candidates
- review queue before Markdown writes
- audit log of model/provider/data-flow decisions
- evaluation dataset

Should include:

- local tiny model benchmark
- comparison against manual notes, raw capture, and maybe Khoj/Screenpipe as baselines
- MCP or CLI interface so OpenClaw/ZeroClaw/Goose could call it without becoming the runtime

Should not include:

- general autonomous assistant
- skill marketplace
- arbitrary shell/browser execution
- always-on screen/audio recording
- full Obsidian replacement

## Sources To Add To Final Bibliography

- Goose AAIF migration: https://goose-docs.ai/blog/2026/04/07/goose-moves-to-aaif/
- Goose docs: https://goose-docs.ai/
- Goose GitHub: https://github.com/aaif-goose/goose
- Khoj docs: https://docs.khoj.dev/
- Khoj self-host docs: https://docs.khoj.dev/get-started/setup/
- ZeroClaw docs: https://docs.zeroclawlabs.ai/en/
- ZeroClaw GitHub: https://github.com/zeroclaw-labs/zeroclaw
- OpenClaw GitHub: https://github.com/openclaw/openclaw
- OpenClaw getting started: https://docs.openclaw.ai/start/getting-started
- OpenClaw architecture: https://docs.openclaw.ai/concepts/architecture
- OpenClaw security: https://docs.openclaw.ai/security
- Microsoft OpenClaw security analysis: https://www.microsoft.com/en-us/security/blog/2026/02/19/running-openclaw-safely-identity-isolation-runtime-risk/
- Cortex Obsidian forum: https://forum.obsidian.md/t/plugin-cortex-an-ai-obsidian-vault-agent-powered-by-claude-code/112430
- Obsidian Vault Agent forum: https://forum.obsidian.md/t/new-plugin-obsidian-vault-agent-terminal-with-claude-code-codex/110024
- Screenpipe docs: https://docs.screenpi.pe/
- Anytype local-only docs: https://doc.anytype.io/anytype-docs/advanced/data-and-security/self-hosting/local-only
- Anytype self-hosted docs: https://doc.anytype.io/anytype-docs/advanced/data-and-security/self-hosting/self-hosted
- Gemma 3 270M: https://developers.googleblog.com/en/introducing-gemma-3-270m/
- EmbeddingGemma: https://ai.google.dev/gemma/docs/embeddinggemma
- Qwen3 0.6B: https://huggingface.co/Qwen/Qwen3-0.6B

