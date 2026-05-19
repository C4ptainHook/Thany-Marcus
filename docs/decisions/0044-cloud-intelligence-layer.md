# ADR-0044: Cloud-side intelligence layer — LLM routing, entity extraction, hub generation

Status: Accepted (drafted from 2026-05-19 grilling; implementation in M6 alongside [[0042-cloud-ingest-pipeline-architecture]])
Date: 2026-05-19

## Context

The composite-ingest pipeline ([[0042-cloud-ingest-pipeline-architecture]])
runs three explicit LLM phases on every note: `routing`, `extracting_entities`,
and (for entities crossing the mention threshold) `hub_regen`. Plus an
implicit fourth: entity-dedup judgment during entity extraction. Each phase
needs a prompt, a structured output schema, a confidence threshold, retry
policy on malformed responses, and an instrumented record of what was asked +
what came back (for evaluation per M9).

The pipeline already commits to MiniCPM-V 4.6 (Q4_K_M) on Ollama for vision
([[0043-cloud-side-model-lineup]]). MiniCPM-V's underlying LM is Qwen3-derived
~1.3 B params — competent for thesis-scale routing and entity tasks at the
CPU-only constraint that anchors the architecture. Provider-agnostic prompt
shape is required because the M7 "unsafe" mode dispatches the same prompts to
an external API (Anthropic / OpenAI) ([[0024-dbcontext-shape]] is unrelated;
relevant cross-reference is the M7 `CLOUD-026` ticket from
`plans/tickets-2026-05-13.md`).

Three things make this layer thesis-load-bearing:

1. **Evaluation depends on it.** EVAL-002 (routing accuracy across baselines
   B0–B4), EVAL-003 (entity dedup P/R), and EVAL-005 (wikilink precision-at-K)
   measure LLM output behavior. The eval chapter has to point at concrete
   prompts, thresholds, and run-by-run output to make comparisons.

2. **The same model also runs locally** — every LLM call hits the
   already-loaded Ollama instance. Cost and latency budgets from
   [[0042-cloud-ingest-pipeline-architecture]] §7 assume each phase is a
   single ~4–8 s call.

3. **Safe vs unsafe mode** is a documented architectural variant
   (`notes.llm_mode` ∈ `{'safe','unsafe'}`). The same prompts must be valid
   against both local and external LLMs; the choice of provider is a runtime
   dispatch decision, not a code fork.

## Decision

A single `ILlmClient` interface dispatches prompts to either local (Ollama,
"safe") or external API ("unsafe"), with prompts stored as versioned code
constants in a prompt registry. Each call requests JSON output constrained
by an explicit schema; responses are parsed and validated server-side with
bounded retry on malformed output. Per-phase confidence thresholds
(`appsettings.json`-driven) decide whether to accept or discard each output.

### 1. The `ILlmClient` interface

```csharp
public interface ILlmClient
{
    Task<T> CompleteAsync<T>(
        PromptId promptId,
        object inputContext,
        CancellationToken ct) where T : class;
}

public sealed record PromptId(string Name, string Version);
```

Two implementations:

- **`SafeLlmClient`** — HTTP to local Ollama (`POST /api/generate` with
  `format: "json"`). Used when `notes.llm_mode='safe'` (default).
- **`UnsafeLlmClient`** — HTTP to external API (Anthropic, OpenAI). Used when
  `notes.llm_mode='unsafe'`. Ships in M7 (CLOUD-026 ticket). Until then,
  `llm_mode='unsafe'` falls back to `SafeLlmClient` with a warning in
  provenance.

The saga's LLM-phase handlers (`RoutingHandler`, `ExtractingEntitiesHandler`,
`HubGenerationHandler`) hold `ILlmClient` and dispatch by `note.llm_mode`. The
client decides transport; the handler decides prompt + schema + threshold.

### 2. Structured output via `format: "json"` + post-parse validation

For every LLM call:

1. Build prompt from `PromptTemplates.<Name>_<Version>` constant.
2. POST to Ollama with `format: "json"` and the JSON schema (or descriptive
   instructions) embedded in the prompt.
3. Receive response. Parse with `System.Text.Json` against the expected DTO.
4. If parse failure or schema mismatch: bump retry counter, retry with a
   refined prompt suffix `"Your previous response was not valid JSON. Respond
   with ONLY valid JSON matching the schema."`
5. After `MaxAttemptsPerPhase` retries (default 3), transition the
   ingest_job phase to its `failed_<phase>` terminal state per
   [[0042-cloud-ingest-pipeline-architecture]] §3.

Grammar-constrained decoding (Ollama's `grammar:` field with GBNF) was
considered and rejected for thesis MVP — schema-in-prompt + post-parse
validation works for ~95 % of calls and avoids maintaining grammars in two
places. Could revisit if eval shows retry rates above ~5 %.

### 3. The four prompts (M5+ ship as `_V1`)

#### 3.1 Routing prompt — `PromptId("route", "v1")`

**Input context**:
```json
{
  "body_excerpt": "<first 1500 chars of body_output>",
  "projects": [
    {"id": "<uuid>", "name": "ClientA", "description": "Acme Corp engagement..."},
    ...
  ],
  "max_projects": 50
}
```

**Prompt template**:
```
You are a note-routing assistant. Given a note's content and a list of
projects, decide which project this note belongs to.

PROJECTS:
- {projects[0].id}: {projects[0].name}. {projects[0].description}
- {projects[1].id}: {projects[1].name}. {projects[1].description}
...
- null: No project matches — leave unassigned.

NOTE CONTENT:
{body_excerpt}

Respond with JSON ONLY:
{
  "project_entity_id": "<uuid or null>",
  "confidence": <0.0-1.0>,
  "rationale": "<one-sentence reasoning>"
}
```

**Output DTO**:
```csharp
public sealed record RouteDecision(
    Guid? ProjectEntityId,
    double Confidence,
    string Rationale);
```

**Threshold**: if `Confidence < LlmIntelligence:Thresholds:RouteAcceptMin`
(default `0.5`), set `notes.project_id = null` (unrouted). User can assign
manually via plugin.

**Project list source**: `entities WHERE kind='project' AND deleted_at IS NULL
ORDER BY updated_at DESC LIMIT 50` per [[0045-composite-note-schema]] §G6.
Provisional + user-created entities both eligible.

#### 3.2 Entity extraction prompt — `PromptId("extract", "v1")`

**Input context**: full `body_output` (post-compose, pre-wikilink).

**Prompt template**:
```
You are an entity-extraction assistant. Identify mentions of people,
organizations, projects, places, or concepts that deserve to be wikilinked
in this note. Skip generic references like "the meeting", "today",
"the file".

NOTE BODY:
{body_output}

Return JSON ONLY:
{
  "mentions": [
    {
      "anchor_text": "<the exact substring in the body>",
      "start_offset": <int, character offset from start of body>,
      "end_offset":   <int, exclusive>,
      "candidate_kind": "person|organization|project|place|concept",
      "candidate_canonical": "<a clean canonical form>",
      "aliases": ["<other names if known>"],
      "confidence": <0.0-1.0>
    }
  ]
}

Use character offsets from the start of the body. Confidence reflects how
clearly the mention is a real, distinct entity.
```

**Output DTO**:
```csharp
public sealed record EntityExtraction(IReadOnlyList<MentionCandidate> Mentions);
public sealed record MentionCandidate(
    string AnchorText,
    int StartOffset,
    int EndOffset,
    string CandidateKind,
    string CandidateCanonical,
    IReadOnlyList<string> Aliases,
    double Confidence);
```

**Threshold**: drop mentions with `Confidence < Thresholds:MentionMin`
(default `0.6`). Dropped mentions still recorded in provenance for eval
(EVAL-005 sensitivity analysis).

#### 3.3 Entity dedup prompt — `PromptId("dedup", "v1")`

Invoked per-candidate during entity extraction. The handler first runs
pgvector cosine top-K (default `K=5`) against `entities.embedding` filtered
by `kind = candidate_kind`. The top-K + candidate are passed to the LLM.

**Input context**:
```json
{
  "candidate": {
    "anchor_text": "John",
    "candidate_canonical": "John Smith",
    "candidate_kind": "person",
    "surrounding_text": "...had lunch with John yesterday and we discussed..."
  },
  "similar_entities": [
    {
      "id": "<uuid>",
      "kind": "person",
      "canonical_name": "John Smith",
      "aliases": ["JS", "Smith, J."],
      "description": "..."
    },
    ...
  ]
}
```

**Prompt template**:
```
You are an entity-resolution assistant. A candidate mention has been found
in a note. Decide if it refers to one of the existing entities below, or is
a new entity.

CANDIDATE:
  Anchor: "{candidate.anchor_text}"
  Kind:   {candidate.candidate_kind}
  Canonical guess: {candidate.candidate_canonical}
  Surrounding context: ...{candidate.surrounding_text}...

EXISTING SIMILAR ENTITIES (top-K by embedding cosine):
- {similar_entities[0].id}: {similar_entities[0].kind}/{similar_entities[0].canonical_name}
   aliases: [{similar_entities[0].aliases}]
   description: {similar_entities[0].description}
- ...

Respond with JSON ONLY:
{
  "decision": "alias_of" | "new_entity" | "ambiguous",
  "matched_entity_id": "<uuid or null>",
  "candidates": ["<uuid>"] (for ambiguous; otherwise []),
  "confidence": <0.0-1.0>,
  "rationale": "<short reason>"
}

Use "ambiguous" only when two or more existing entities are equally likely.
```

**Output DTO**:
```csharp
public sealed record DedupDecision(
    string Decision,                         // "alias_of" | "new_entity" | "ambiguous"
    Guid? MatchedEntityId,
    IReadOnlyList<Guid> Candidates,
    double Confidence,
    string Rationale);
```

**Threshold behavior** (per [[0045-composite-note-schema]] §F6 + F8):

| Decision | Confidence | Action |
|---|---|---|
| `alias_of` | ≥ `Thresholds:DedupAliasMin` (default `0.8`) | INSERT mention pointing to matched_entity_id; add anchor_text to entity.aliases if novel |
| `new_entity` | ≥ `Thresholds:DedupNewMin` (default `0.6`) | INSERT new entities row; INSERT mention pointing to it |
| `ambiguous` | any | drop the mention; record candidates in provenance |
| any | below threshold | drop |

#### 3.4 Hub generation prompt — `PromptId("hub-generate", "v1")`

Invoked when an entity's `mention_count` crosses the materialization
threshold (`N=3` default) OR is created by user with `source='user'` (which
auto-hubs per [[0045-composite-note-schema]] §F8). A new `ingest_jobs` row
with `kind='hub_regen'` runs through the saga's `composing` →
`extracting_entities` → `embedding` phases; the hub-generation prompt fires
in the `composing` phase (skipping the extraction_attachments phase since
there are no attachments — the input is the entity + its mentions).

**Input context**:
```json
{
  "entity": {
    "kind": "person",
    "canonical_name": "John Smith",
    "aliases": ["John", "JS"]
  },
  "mentions": [
    {
      "note_title": "Standup notes 2026-05-18",
      "note_captured_at": "2026-05-18T10:00:00Z",
      "surrounding_text": "...John presented the migration plan..."
    },
    ...
  ],
  "previous_body": "<existing hub body, or null on first creation>"
}
```

**Prompt template (`hub-generate-v1`)**:
```
You are a knowledge-hub author. Generate a dossier note for the entity
below, drawing only from the provided mentions. Do not invent facts.

ENTITY:
  Kind: {entity.kind}
  Canonical name: {entity.canonical_name}
  Aliases: {entity.aliases}

MENTIONS (most recent first, up to {N}):
1. From note "{mentions[0].note_title}" ({mentions[0].note_captured_at}):
   "...{mentions[0].surrounding_text}..."
2. ...

{IF previous_body IS NOT null:}
CURRENT HUB BODY:
{previous_body}

Update the hub body, preserving existing structure and additively
integrating new information. Mark contradictions in "Open questions."
{ELSE:}
Produce a Markdown body with these sections:
## Context
A 2-3 paragraph synthesis of what's known about this entity, citing source notes.

## Generated Views
- Recent activity
- Related themes
- Open questions or contradictions
{END}

Use Obsidian-style wikilinks [[Note Title]] when referencing source notes.
Cite only the provided mentions; do not invent new ones.
```

**Output**: free-form Markdown (no JSON schema — the output IS the body).
Stored as `notes.body_output` on the hub note. Re-embedded as usual in the
`embedding` phase.

Diff-aware regen (the B4c pattern from `cloud-pivot-plan-2026-05-13.md`):
when `previous_body` is non-null (subsequent hub-regen), prompt includes it +
the new mention(s) since last regen. Avoids losing user edits and keeps
generated views additive.

### 4. Confidence thresholds (config-locked, eval-tunable)

All thresholds live in `appsettings.json` under `LlmIntelligence:Thresholds`,
defaults shown:

```json
{
  "LlmIntelligence": {
    "Thresholds": {
      "RouteAcceptMin":   0.5,
      "MentionMin":       0.6,
      "DedupAliasMin":    0.8,
      "DedupNewMin":      0.6
    },
    "Pgvector": {
      "DedupTopK":        5
    },
    "Retry": {
      "MaxAttempts":      3,
      "BackoffSecondsBase": 2
    }
  }
}
```

Eval can sweep these without code changes. EVAL-002 routing-accuracy can
compare 0.4 vs 0.5 vs 0.6 thresholds and report curves; EVAL-003 entity-dedup
P/R can sweep `DedupAliasMin` and `DedupNewMin`.

### 5. Prompt registry — versioned constants

Prompts are static class constants in code, named `<Name>_<Version>`:

```csharp
public static class PromptTemplates
{
    public const string Route_V1 = @"You are a note-routing assistant...";
    public const string Extract_V1 = @"You are an entity-extraction assistant...";
    public const string Dedup_V1 = @"You are an entity-resolution assistant...";
    public const string HubGenerate_V1 = @"You are a knowledge-hub author...";
}
```

The saga records the `PromptId(Name, Version)` it used in `notes.provenance`
per [[0042-cloud-ingest-pipeline-architecture]] §10c:

```json
{
  "stage": "llm_route",
  "prompt_id": "route-v1",
  "model": "minicpm-v-4.6",
  "model_version": "Q4_K_M",
  "duration_ms": 4120,
  "decision": "project:<uuid>",
  "confidence": 0.87
}
```

When prompts evolve, a new constant `Route_V2` is added; config switches via
`LlmIntelligence:RoutePromptVersion`. Old notes retain `route-v1` in
provenance; reprocess applies the new version. Eval can A/B compare v1 vs v2
on the same corpus.

### 6. Retry on malformed LLM output

For each LLM call (`SafeLlmClient.CompleteAsync<T>`):

1. Build prompt.
2. POST to Ollama (or external API).
3. Parse JSON; validate against `T`.
4. **On parse / validation failure**:
   - Append `"Previous response was not valid JSON. Respond with ONLY valid
     JSON matching the schema."` to the prompt.
   - Retry with `attempts++`.
   - After `MaxAttempts` (default 3): throw `LlmStructuredOutputException`;
     saga phase transitions to `failed_<phase>`.

Per-retry attempts are recorded in provenance under the same stage with
`retry_index`.

### 7. Safe vs unsafe (`llm_mode`) — same prompts, different transports

`notes.llm_mode` is set at ingest time (plugin sends in `/init`; defaults to
`'safe'`). The saga handler:

```csharp
ILlmClient client = note.LlmMode switch
{
    "unsafe" => unsafeLlmClient,
    _        => safeLlmClient,
};
var decision = await client.CompleteAsync<RouteDecision>(
    new PromptId("route", "v1"),
    new { body_excerpt = ..., projects = ... },
    ct);
```

Both clients send identical prompts; only the HTTP transport + provider-key
differs:

- `SafeLlmClient` → `http://ollama:11434/api/generate` with `model=minicpm-v`
  + `format=json`.
- `UnsafeLlmClient` (M7, CLOUD-026) → `https://api.anthropic.com/v1/messages`
  or `https://api.openai.com/v1/chat/completions`. Same input shape, same
  output schema. Provider key stored encrypted per
  [[0041-portal-cloud-admin-token-storage]] pattern but in a new
  `cloud_settings.external_llm_keys` column.

Provenance records the difference:

```json
{ "stage": "llm_route", "model": "minicpm-v-4.6", "llm_mode": "safe" }
// vs
{ "stage": "llm_route", "model": "claude-3-5-sonnet-2026-01", "llm_mode": "unsafe" }
```

Eval can compare safe-mode vs unsafe-mode runs on the same corpus — a real
thesis-defensible metric ("how much does external LLM quality improve
routing accuracy vs local?").

### 8. Streaming — deferred

LLM calls are non-streaming (`stream: false` in Ollama; equivalent in
external APIs). The phase completes when the LLM call returns. SSE progress
events (per [[0042-cloud-ingest-pipeline-architecture]] §5) surface
phase-level transitions, not token-level streaming.

Token-streaming to plugin SSE is a future-work hook for live preview of LLM
output. Adds JSON-streaming-parse complexity. Out of scope for thesis MVP.

## Alternatives considered

1. **Grammar-constrained decoding (Ollama `grammar:` GBNF).**
   Stronger guarantee that output is valid JSON matching the schema.
   Rejected: GBNF grammars are a second source of truth alongside the C# DTOs;
   ~5 % retry rate from `format: "json"` + post-parse validation is acceptable
   and avoids the maintenance cost. Revisit if eval shows higher retry rates.

2. **Single combined LLM call for routing + entity extraction.**
   Cuts the ~10 s per-note tax to ~6 s (one merged ~6 s call instead of
   ~4 s + ~6 s separate). Rejected for MVP: eval chapter compares routing
   accuracy and entity P/R independently — easier attribution with separate
   calls. Listed as a future-work hook in
   [[0042-cloud-ingest-pipeline-architecture]] §"Deferred future-work".

3. **Free-form output + regex/string parser.**
   Rejected: brittle, eval-hostile, retry-heavy.

4. **Per-user fine-tuned model.**
   Best-in-class quality for individual user routing. Rejected: out of thesis
   scope; requires per-user training infrastructure.

5. **Multi-call chain-of-thought / ReAct.**
   Multiple LLM calls per phase with intermediate reasoning. Rejected:
   adds ~10 s per note for marginal accuracy gain at thesis scale; obscures
   eval attribution.

6. **Embedding-based retrieval over a prompt-library** to pick the best prompt
   for each note. Rejected: premature; the four prompts cover the four
   distinct decisions cleanly.

7. **Different model per phase** (e.g., routing on a cheap text model,
   entities on the VLM's LM, hub-gen on an external API).
   Rejected: complicates RAM budget; muddles eval (model is a variable).
   Single LM serves all phases; safe vs unsafe is the only legitimate
   provider switch.

8. **JSON output without schema validation.**
   Rejected: silent schema drift kills downstream code paths.

9. **Per-mention LLM call for entity dedup** (one call per mention candidate,
   N calls per note). Already the current design — kept verbatim because
   alternatives (single batched call with all candidates) make the LLM
   output too long to be reliable.

## Consequences

### Positive

- **Eval-friendly**: every LLM call records prompt-id, prompt-version,
  model, model-version, retries, output, confidence. M9 eval scripts can
  iterate provenance and produce charts.
- **Provider-agnostic**: the same prompt set works against local Ollama or
  external API. Safe/unsafe mode is a transport switch, not a code fork.
- **Reproducibility for thesis defense**: prompts are code constants;
  versioned; recorded per run; A/B comparable.
- **Bounded retry**: malformed LLM output doesn't hang the saga; transitions
  to failure terminal after 3 attempts with full provenance.
- **Hub regen and capture share the saga** via the `kind` discriminator
  ([[0042-cloud-ingest-pipeline-architecture]] §6); no parallel intelligence
  pipeline.
- **Threshold sensitivity**: 4 named thresholds, all config-driven, all
  eval-sweepable. EVAL-002 / 003 / 005 directly consume them.

### Negative / accepted costs

- ~10 s per note tax for routing + entity-extraction. Significant fraction of
  per-note latency for short captures. Mitigation: the "merge routing +
  entities" optimization is a documented future-work hook.
- Entity dedup is one LLM call per mention candidate. A note with 5 mentions
  = 5 dedup calls = ~30 s of dedup. Most notes have ≤ 3 mentions; eval
  expected to show median dedup overhead < 10 s.
- External API mode (M7) introduces external dependency; user data leaves
  the cloud. The thesis architecture defends "local by default" — unsafe
  mode is opt-in.
- Prompts as code constants vs database means a deploy is needed to change
  them. Acceptable; eval iterates via code branches, not live tuning.
- 1.3 B LM quality is below GPT-4 / Claude on hard cases (subtle
  disambiguation, multi-hop reasoning). Eval will quantify the gap.

### Operational

- All four prompts versioned together as a "prompt registry" code module.
  Changes go through PR review; provenance captures which version ran each
  note.
- Per-phase Prometheus metrics: `llm_phase_duration_seconds{phase}`,
  `llm_retry_count{phase}`, `llm_confidence{phase}`. Surfacing low confidence
  or high retry rates as alerts is straightforward.

## Deferred future-work hooks

1. **Merge routing + entity-extraction** into a single LLM call. ~3-5 s
   saved per note. Trades eval clarity for latency.
2. **Streaming LLM output to SSE.** Live preview of LLM thinking in plugin
   status bar. Adds JSON-streaming-parse complexity.
3. **Grammar-constrained decoding (GBNF).** Drops malformed-JSON retry rate
   to ~0; worth it if retry rate exceeds ~5 % in eval.
4. **Per-prompt A/B in production.** Route a percentage of traffic to
   `Route_V2`; compare metrics. Requires plumbing; useful for the thesis
   eval chapter.
5. **External API mode (M7, CLOUD-026).** Unsafe-mode dispatch to Anthropic /
   OpenAI with the same prompt set + schemas.
6. **Per-user fine-tuned model** for routing. Out of scope.
7. **Chain-of-thought / ReAct prompts** for harder entity dedup cases.

## References

- [[0042-cloud-ingest-pipeline-architecture]] — the saga that invokes these prompts
- [[0043-cloud-side-model-lineup]] — MiniCPM-V 4.6 (text mode) and Ollama as the safe-mode LLM transport
- [[0045-composite-note-schema]] — forthcoming; entities / mentions / hubs / projects schema referenced here
- [[0020-server-push-sse]] — phase progress transport (not token streaming)
- [[0041-portal-cloud-admin-token-storage]] — DataProtection pattern, applied analogously to external-LLM keys in M7
- `plans/cloud-pivot-plan-2026-05-13.md` §B4 — diff-aware hub regen pattern
- `plans/tickets-2026-05-13.md` — CLOUD-017 (routing), CLOUD-019 (entity dedup), CLOUD-020 (hub creation), CLOUD-021 (hub regen), CLOUD-026 (external API client for unsafe mode)
- `memory/composite_ingest_decision.md` — composite ingest decision (2026-05-17)
