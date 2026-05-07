# Super Tiny Models In This Project Context

Date: 2026-05-03

Scope: "super tiny" here means roughly **20M-1.5B parameters**, with special attention to sub-1B models that can plausibly run on weak laptops, phones, Raspberry Pi-class devices, or cheap CPU VPS instances.

## Executive Summary

Super tiny models should not be treated as the full "brain" of the second-brain system. They are too unreliable for deep synthesis, subtle contradiction detection, and high-quality writing.

They are useful as **local privacy-preserving microservices**:

- route content by privacy class
- classify source type and intent
- detect language
- extract candidate entities
- detect obvious PII
- generate rough tags
- decide whether a stronger model is needed
- produce simple structured JSON for short captures
- embed notes for local semantic search

Best framing:

> The system uses super tiny local models as a first-pass privacy and curation layer, escalating only difficult or user-approved tasks to larger self-hosted or cloud models.

This is more defensible than claiming a 270M-600M model can maintain an intelligent second brain alone.

## Why Tiny Models Are Interesting Here

### Privacy

Tiny models can run before any data leaves the device. That makes them useful for:

- sensitive-content classification
- PII detection
- local summarization before cloud calls
- redaction suggestions
- policy routing

### Latency

For small inputs, tiny models can respond quickly on CPU or mobile hardware. This matters for capture UX:

- user presses hotkey
- app immediately labels the capture
- user sees "private/work/project X/possible duplicate"

### Cost

No API cost for every capture. The system can process many small snippets locally and reserve expensive models for hard operations.

### Deployment

Tiny models make private mode plausible on:

- weak laptop
- phone
- cheap VPS
- single-board computer
- browser/WebGPU future path

## Verified Tiny / Small Model Options

### Gemma 3 270M

Google released Gemma 3 270M as a compact model for task-specific fine-tuning and efficient local use. Google AI docs list Gemma 3 sizes including 270M, 1B, 4B, 12B, and 27B. The 270M and 1B variants are text-only and support 32K context.

Sources:

- https://developers.googleblog.com/en/introducing-gemma-3-270m/
- https://ai.google.dev/gemma/docs/core
- https://huggingface.co/google/gemma-3-270m

Best use in this project:

- domain-specific fine-tuned classifier
- short structured extraction
- tag suggestion
- capture intent classification
- privacy routing

Risks:

- hallucination risk is high for factual answering
- not suitable for unsupported source-grounded claims
- license terms must be checked for project use
- may need task-specific fine-tuning to be worth using

### Qwen3 0.6B

Qwen3 has dense models from 0.6B upward. The Qwen3-0.6B model card lists 0.6B parameters, 32,768 context length, Apache 2.0 license, multilingual support, and thinking/non-thinking modes.

Sources:

- https://huggingface.co/docs/transformers/model_doc/qwen3
- https://huggingface.co/Qwen/Qwen3-0.6B

Best use:

- local router/classifier
- short extraction
- multilingual capture triage
- "no_think" fast mode for routine tasks

Risks:

- thinking mode may increase latency and output verbosity
- even with 32K context, useful reasoning quality is not guaranteed
- should be benchmarked against Qwen2.5 0.5B and Gemma 270M rather than assumed better

### Qwen2.5 0.5B / 1.5B

Qwen2.5 has 0.5B and 1.5B small variants. The official Qwen model card lists 32K context for both and Apache 2.0 license for these sizes.

Sources:

- https://qwen.ai/blog?id=qwen2.5-llm
- https://qwen2.org/qwen2-5/
- https://huggingface.co/Qwen/Qwen2.5-0.5B

Best use:

- weak-device local curation
- multilingual classification
- structured extraction if prompted carefully
- cheap VPS CPU experiments

Risks:

- 0.5B is likely too weak for nuanced summarization
- base models should not be used as chat assistants without post-training/instruct variants

### SmolLM / SmolLM2 135M / 360M / 1.7B

Hugging Face's SmolLM and SmolLM2 families include 135M, 360M, and 1.7B models. SmolLM2 model cards describe compact models intended for on-device use, with improvements over the first generation in instruction following, knowledge, and reasoning. SmolLM2 135M was trained on 2T tokens; 360M on 4T tokens.

Sources:

- https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct
- https://huggingface.co/HuggingFaceTB/SmolLM2-135M
- https://huggingface.co/HuggingFaceTB/SmolLM2-360M
- https://huggingface.co/HuggingFaceTB/SmolLM-135M
- https://huggingface.co/HuggingFaceTB/SmolLM-360M

Best use:

- very small on-device experiments
- simple classification/extraction after fine-tuning
- proof that the system can support extremely constrained models

Risks:

- likely too weak for robust general-purpose note curation
- English-centric variants may be bad for multilingual user data
- best treated as experimental baseline, not default

### Llama 3.2 1B / 3B

Meta's Llama 3.2 release includes lightweight text-only 1B and 3B models for edge/mobile use. Meta announcement coverage states the 1B and 3B models support 128K context and are aimed at on-device tasks such as summarization, instruction following, and rewriting.

Source:

- https://about.fb.com/br/news/2024/09/conheca-o-llama-3-2-da-nuvem-para-a-borda-e-agora-com-visao/

Best use:

- stronger local baseline than 270M-600M models
- edge summarization
- local note rewriting
- local curation on devices that can handle 1B/3B models

Risks:

- larger than the "super tiny" tier
- license is not as simple as Apache/MIT
- 128K context does not mean high-quality long-context reasoning

## Tiny Non-Generative Models May Be More Important

For many tasks in this system, a tiny chat model is not the best tool.

### Embeddings For Search / Deduplication

#### all-MiniLM-L6-v2

Sentence Transformers' `all-MiniLM-L6-v2` maps sentences and paragraphs to 384-dimensional vectors and is intended for semantic search, clustering, and similarity. Its model card lists 22.7M parameters and Apache 2.0 license.

Source:

- https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2

Use:

- duplicate candidate search
- related-note suggestion
- clustering inbox captures
- cheap local semantic search

Limitations:

- input longer than 256 word pieces is truncated by default
- English-focused
- no reasoning or generation

#### EmbeddingGemma 308M

Google's EmbeddingGemma is a 308M multilingual text embedding model based on Gemma 3. Google docs say it is optimized for phones/laptops/tablets, trained in 100+ languages, supports 2K token context, and can run with less than 200MB RAM when quantized.

Sources:

- https://ai.google.dev/gemma/docs/embeddinggemma
- https://ai.google.dev/gemma/docs/embeddinggemma/model_card

Use:

- multilingual semantic search
- duplicate detection
- retrieval for local RAG
- clustering

Limitations:

- embedding only
- 2K context, not long-document reasoning
- Gemma terms apply

### Entity Extraction / PII

#### spaCy small pipelines

spaCy's `en_core_web_sm` is a CPU-optimized English pipeline with tokenization, tagging, parsing, lemmatization, and NER.

Sources:

- https://spacy.io/models
- https://huggingface.co/spacy/en_core_web_sm

Use:

- fast local NER
- baseline entity extraction
- lightweight source metadata parsing

Limitations:

- fixed entity labels
- weaker than LLMs for custom entities
- language-specific

#### GLiNER small

GLiNER is a generalist NER model that can identify arbitrary entity types using a bidirectional transformer encoder. Model cards list small variants around 166M parameters, with Apache 2.0 variants available in v2+.

Sources:

- https://huggingface.co/urchade/gliner_small-v2
- https://huggingface.co/urchade/gliner_small-v2.1
- https://arxiv.org/abs/2311.08526

Use:

- custom entity labels such as `project`, `person`, `deadline`, `company`, `credential`, `meeting`, `decision`
- better fit than tiny LLM generation for entity extraction
- useful privacy gate before any cloud model

Limitations:

- not a full PII solution
- needs evaluation on the user's real capture types

### Language Detection

fastText language ID models recognize 176 languages. The official docs list a 126MB model and a compressed 917KB model.

Source:

- https://fasttext.cc/docs/en/language-identification

Use:

- route Ukrainian/Russian/English content to suitable models
- avoid sending unsupported language text to a bad extractor
- choose tokenizer/pipeline

Limitations:

- language ID only
- not a semantic model

### PII Redaction Frameworks

Microsoft Presidio is an open-source framework for detecting, redacting, masking, and anonymizing sensitive data across text, images, and structured data. It supports NLP, pattern matching, and customizable recognizers.

Source:

- https://github.com/microsoft/presidio

Use:

- deterministic + NLP PII detection baseline
- privacy gateway
- audit-friendly redaction pipeline

Limitations:

- PII detection is incomplete by nature
- recognizer coverage depends on locale and configuration
- should be combined with user review for high-risk data

## Recommended Tiny-Model Architecture

Do not use one tiny model for everything. Use a cascade:

1. **Rules and metadata**
   - source app
   - URL/domain
   - user-selected privacy mode
   - file path
   - capture type

2. **Fast deterministic detectors**
   - regex for emails, phone numbers, API keys, URLs
   - checksum/hash for duplicate raw captures
   - language ID

3. **Tiny specialist models**
   - embedding model for similarity
   - spaCy/GLiNER for entities
   - tiny LLM for short classification or JSON extraction

4. **Medium local/self-hosted model**
   - 3B-7B model for summarization, note drafting, contradiction checks

5. **Cloud model only with consent**
   - complex synthesis
   - final polishing
   - hard reasoning

This cascade is better than asking a 270M model to be a universal assistant.

## Task Fit Matrix

| Task | Tiny model fit | Better tool |
| --- | --- | --- |
| Language detection | Excellent | fastText |
| Duplicate candidate retrieval | Excellent | MiniLM / EmbeddingGemma |
| Related note search | Good | MiniLM / EmbeddingGemma |
| PII regex detection | Excellent | deterministic rules / Presidio |
| Custom entity extraction | Good | GLiNER / spaCy / tiny LLM backup |
| Source type classification | Good | tiny LLM or small classifier |
| Privacy routing | Good | rules + tiny classifier |
| Tag suggestion | Medium | tiny LLM + embedding clusters |
| Atomic fact extraction | Medium | tiny LLM, but evaluate carefully |
| Short summary | Medium-low | 1B+ model preferred |
| Long summary | Poor | 3B-7B+ model |
| Contradiction detection | Poor | 7B+ or cloud, plus retrieval |
| Reliable redaction | Medium | Presidio + GLiNER + user review |
| Final note writing | Poor-medium | 3B-7B+ model |
| Autonomous vault rewrite | Bad | never without user review |

## Concrete Use Cases In The Diploma Project

### 1. Private Capture Triage

Input:

- selected text
- source app
- timestamp
- optional user comment

Tiny model output:

```json
{
  "source_type": "work_chat",
  "privacy": "private",
  "contains_pii": true,
  "candidate_projects": ["diploma", "pkm-system"],
  "suggested_action": "store_locally_and_do_not_send_to_cloud"
}
```

Why tiny model fits:

- short input
- limited label set
- low-latency
- privacy-critical

### 2. Local Duplicate Candidate Search

Use embeddings:

- embed capture
- search nearest Markdown notes
- ask tiny/medium model whether it is duplicate, elaboration, or unrelated

Tiny model role:

- not full semantic judge
- candidate reducer

### 3. Entity and Link Suggestion

Use GLiNER/spaCy:

- extract people, projects, deadlines, organizations, concepts
- map entities to existing note titles and aliases
- suggest wikilinks

Tiny LLM role:

- turn extracted entities into candidate tags
- explain link reason in one sentence

### 4. Privacy Gateway Before Cloud Model

Pipeline:

- Presidio/rules detect obvious sensitive strings
- GLiNER detects custom sensitive entities
- tiny LLM classifies privacy risk
- only sanitized summary can be sent onward

Important:

- this is risk reduction, not a guarantee
- logs must record what was removed and what was sent

### 5. Weak Device Mode

On phone/weak laptop:

- language detection
- embeddings
- basic PII detection
- capture envelope creation
- optionally tiny LLM label extraction

Send to user-controlled server for heavier processing if private server exists.

## Evaluation Plan For Tiny Models

### Dataset

Create 100-200 capture snippets:

- chat messages
- meeting notes
- web excerpts
- emails
- project decisions
- personal/private snippets
- duplicates
- stale updates
- multilingual examples: English/Ukrainian/Russian if relevant

Annotate:

- source type
- privacy class
- entities
- PII spans
- duplicate links
- desired tags
- whether cloud processing is allowed

### Metrics

For classifiers:

- accuracy
- precision/recall per privacy class
- false negatives for sensitive content

For PII:

- recall is more important than precision
- count severe misses separately

For entity extraction:

- entity-level precision/recall/F1
- custom entity F1

For retrieval:

- recall@k for duplicate/related-note candidates
- MRR or nDCG for related note ranking

For generated JSON:

- parse success rate
- schema adherence
- hallucinated fields

For operations:

- latency on target hardware
- memory usage
- battery estimate if mobile
- model size on disk

### Baselines

Compare:

- deterministic rules only
- all-MiniLM-L6-v2 embeddings
- EmbeddingGemma
- spaCy
- GLiNER small
- Gemma 3 270M
- Qwen3 0.6B
- Qwen2.5 0.5B / 1.5B
- one medium local model such as 3B-7B
- cloud model as upper bound

## Implementation Implications

### Model Adapter Interface

The system should not know whether it is using Gemma 270M, Qwen3 0.6B, GLiNER, or Presidio. It should call task-specific interfaces:

- `classify_capture`
- `detect_sensitive_spans`
- `extract_entities`
- `embed_text`
- `suggest_tags`
- `route_for_processing`

This makes tiny models replaceable.

### Confidence and Abstention

Tiny models need abstention:

- "unknown"
- "needs larger model"
- "needs user review"
- "do not send"

Never force a tiny model to decide high-risk actions.

### Human Review

Tiny model outputs should be suggestions:

- proposed tags
- proposed links
- proposed privacy class
- proposed cloud routing

High-risk outputs require explicit user approval.

## Thesis Angle From Tiny Models

A strong thesis contribution could be:

> An empirical evaluation of a tiered local-first curation pipeline showing which personal-knowledge tasks can be handled by super tiny models, and when escalation to larger self-hosted or cloud models is necessary.

This is better than claiming tiny models solve the whole project.

Possible research question:

> Can sub-1B local models provide enough privacy-preserving first-pass curation for personal knowledge capture to reduce cloud model usage without unacceptable loss of note quality?

Possible hypothesis:

> Super tiny models are sufficient for routing, classification, entity extraction assistance, and retrieval candidate generation, but insufficient for final synthesis and contradiction detection.

## Recommended Position

Use super tiny models as **gatekeepers and assistants**, not as the final curator.

Best default stack to evaluate:

- language ID: fastText compressed model
- embeddings: all-MiniLM-L6-v2 and/or EmbeddingGemma
- entity extraction: GLiNER small + spaCy baseline
- tiny generative model: Gemma 3 270M, Qwen3 0.6B, Qwen2.5 0.5B
- larger local baseline: Llama 3.2 3B, SmolLM3 3B, or Qwen 3B/4B class model

The final system can then choose:

- super tiny only for strict private/weak-device mode
- medium local model for normal private desktop mode
- cloud model only for explicit public/hybrid mode

