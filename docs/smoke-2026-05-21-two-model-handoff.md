# Split vision and text LLMs on the cloud (handoff, 2026-05-21)

## Problem

The cloud currently routes every Ollama call to a single model:
`openbmb/minicpm-v4.6:q4_K_M`, served by the custom fork
`ghcr.io/c4ptainhook/thany-ollama-minicpm`. That model is a vision-language
model. Routing and entity-extraction prompts demand structured JSON output,
and MiniCPM-V cannot produce parseable JSON reliably.

Observed failure: smoke #24 reached `failed_route` after 3 attempts × ~76s each
with `error="LLM output failed to parse after 3 attempts for prompt route-v1"`.
Pipeline trace was clean up to that point — URL extracted, Parakeet
transcribed, composing produced 2362-char `body_output` — then routing
unconditionally bombed because the model can't structure-format.

## Architecture target

Run **two** models in parallel on the cloud:

| Role             | Model                            | Server                                          | Approx RSS (loaded) |
| ---------------- | -------------------------------- | ----------------------------------------------- | ------------------- |
| Vision (VLM)     | `openbmb/minicpm-v4.6:q4_K_M`    | `ghcr.io/c4ptainhook/thany-ollama-minicpm`      | ~2.0 GB             |
| Text / structure | `qwen3:1.7b-instruct-q4_K_M`     | upstream `ollama/ollama:latest`                 | ~1.2 GB             |

Both expose the OpenAI-compatible `/api/generate` and `/api/chat` endpoints
on port 11434, just from separate containers on the cloud docker network
(e.g. `ollama-vision:11434` and `ollama-text:11434`).

Rationale for the size pick (Qwen3-1.7B):
- Native instruction-tuned for JSON/structured output.
- ~1.2 GB resident at Q4_K_M leaves comfortable headroom on `s-4vcpu-8gb`
  alongside MiniCPM-V (2 GB), dotnet (~2 GB working set), parakeet idle (~0.5
  GB, ~2.5 GB peak under load), postgres+docling+nginx (~1 GB).
- Qwen3 sits at v3.6 today (May 2026); 1.7B is the smallest current
  instruction-tuned variant and is already enough for routing-grade tasks.
- 4B is the upgrade path if 1.7B's routing accuracy is too low — memory budget
  still fits (~2.5 GB), but pulls headroom thin under load.

Rationale for two containers instead of two model tags on the same server:
the custom `thany-ollama-minicpm` fork is pinned to a tc-mb/ollama base that
predates upstream's qwen3 architecture support. Rather than rebase that fork
(which we'd have to keep in sync forever), keep it scoped to MiniCPM-V and
run a vanilla `ollama/ollama:latest` container alongside for text.

`OLLAMA_KEEP_ALIVE=30s` stays on both, so models unload between calls and the
two never sit in memory at the same idle moment.

## Concrete changes

### 1. `infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl`

Inside `write_files: /opt/thany-cloud/docker-compose.yml`, change the single
`ollama` service into two services:

```yaml
ollama-vision:
  image: ghcr.io/c4ptainhook/thany-ollama-minicpm:$${OLLAMA_IMAGE_TAG:-latest}
  restart: unless-stopped
  environment:
    OLLAMA_HOST: "0.0.0.0:11434"
    OLLAMA_KEEP_ALIVE: "30s"
    OLLAMA_NUM_PARALLEL: "1"
    OLLAMA_MAX_LOADED_MODELS: "1"
  volumes:
    - ollama-vision-models:/root/.ollama
  networks: [cloud]
  mem_limit: 3g
  healthcheck:
    test: ["CMD", "/bin/ollama", "list"]
    interval: 15s
    timeout: 5s
    retries: 10
    start_period: 30s

ollama-text:
  image: ollama/ollama:0.24.0   # latest stable upstream (May 2026); qwen3 text variants supported
  restart: unless-stopped
  environment:
    OLLAMA_HOST: "0.0.0.0:11434"
    OLLAMA_KEEP_ALIVE: "30s"
    OLLAMA_NUM_PARALLEL: "1"
    OLLAMA_MAX_LOADED_MODELS: "1"
  volumes:
    - ollama-text-models:/root/.ollama
  networks: [cloud]
  mem_limit: 2g
  healthcheck:
    test: ["CMD", "/bin/ollama", "list"]
    interval: 15s
    timeout: 5s
    retries: 10
    start_period: 30s
```

Replace the single `ollama-puller` with two pullers (or one puller that loops
over both models). Easiest: extend the existing `puller/run.sh` `write_files`
script so it pulls both models in sequence — passing `OLLAMA_VISION_PULL_TAG`
and `OLLAMA_TEXT_PULL_TAG` envs and calling each `ollama-*:11434` host. Two
sentinel files (`.ollama-vision-puller-ok`, `.ollama-text-puller-ok`); the
certbot gate requires both.

Add to volumes block: `ollama-vision-models:` and `ollama-text-models:`.

Update the `cloud-api` service's `depends_on` to list both.

### 2. `infra/docker/saga-worker/terraform-modules/digitalocean/variables.tf`

Replace `ollama_pull_tag` (single) with:
- `ollama_vision_pull_tag` — default `openbmb/minicpm-v4.6:q4_K_M`
- `ollama_text_pull_tag` — default `qwen3:1.7b-instruct-q4_K_M`

Keep `ollama_image_tag` for the vision fork tag; add `ollama_text_image_tag`
(default `0.24.0` — latest stable on Docker Hub as of May 2026) for the
upstream image tag.

### 3. `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/WorkspaceLayout.cs`

Pass both `ollama_vision_pull_tag` and `ollama_text_pull_tag` into tfvars (it
currently doesn't pass `ollama_pull_tag` at all — defaults from variables.tf
are used, which is fine, but the two new ones should at least exist as
optional `Provisioning:CloudInit:Ollama*` config keys if you want to override
per-environment).

### 4. `src/ThanyMarcus.Cloud.Api/appsettings.json`

Today:
```json
"IngestSaga": {
  "Sidecars": {
    "Ollama": { "BaseUrl": "http://ollama:11434", ... }
  },
  "Models": {
    "Vlm": { "OllamaTag": "openbmb/minicpm-v4.6:q4_K_M", ... }
  }
}
```

Target:
```json
"IngestSaga": {
  "Sidecars": {
    "OllamaVision": { "BaseUrl": "http://ollama-vision:11434", "RequestTimeoutSeconds": 180 },
    "OllamaText":   { "BaseUrl": "http://ollama-text:11434",   "RequestTimeoutSeconds": 60 }
  },
  "Models": {
    "Vlm":   { "OllamaTag": "openbmb/minicpm-v4.6:q4_K_M", "NumCtx": 4096, "Temperature": 0.2 },
    "Route": { "OllamaTag": "qwen3:1.7b-instruct-q4_K_M",  "NumCtx": 4096, "Temperature": 0.1 },
    "Entity":{ "OllamaTag": "qwen3:1.7b-instruct-q4_K_M",  "NumCtx": 4096, "Temperature": 0.1 }
  }
}
```

### 5. `src/ThanyMarcus.Cloud.Api/Program.cs`

Two named HttpClients instead of one:
- `OllamaClientNames.Vlm` → `OllamaVision.BaseUrl`, 180s timeout (vision is slow)
- `OllamaClientNames.Text` → `OllamaText.BaseUrl`, 60s timeout (text is fast enough)

### 6. `src/ThanyMarcus.Cloud.Api/Infrastructure/Llm/*` + `Infrastructure/Sidecars/OllamaVlmClient.cs`

- `OllamaVlmClient` keeps using `OllamaClientNames.Vlm` and `Models:Vlm:OllamaTag`.
- Routing and entity-extraction code paths (which today probably go through
  `SafeLlmClient` / `LlmIntelligenceOptions`) need to be repointed to the
  text HttpClient and pick their model tag from `Models:Route` /
  `Models:Entity`.
- `SafeLlmClient.ModelName` returning a hard-coded `"minicpm-v"` will need to
  flip to whichever model the current call site uses — most cleanly, drop the
  string and let each handler construct its `OllamaRequest` with its own tag.

### 7. Routing-prompt parsing forgiveness (small, optional, defense-in-depth)

Even with qwen3-1.7b, JSON parse failures will happen occasionally. The
`route-v1` parser should accept either:
- strict JSON, or
- a regex-extracted `category`/`project` token if JSON parse fails, before
  giving up.

This makes the saga resilient to a model that occasionally drops a trailing
brace, without lowering the bar to "anything goes".

## Memory budget on `s-4vcpu-8gb` (worst case under load)

| Component                  | Approx peak RSS |
| -------------------------- | --------------- |
| ollama-vision (MiniCPM-V)  | 2.0 GB          |
| ollama-text (Qwen3-1.7B)   | 1.2 GB          |
| cloud-api (dotnet)         | 2.0 GB          |
| parakeet (during ASR)      | 2.0 GB          |
| postgres                   | 0.3 GB          |
| docling                    | 0.5 GB          |
| nginx + sshd + os          | 0.3 GB          |
| **Total worst case**       | **8.3 GB**      |

This exceeds 8 GB. **It only works because of two-phase sequencing**:
- Extraction phase uses ollama-vision + parakeet, NOT ollama-text.
- Routing/entities phase uses ollama-text, NOT vision and NOT parakeet.

The existing `ExtractingAttachmentsHandler` already serializes sidecars
inside extraction. The `IngestPhaseDispatcher` already serializes phases.
With `OLLAMA_KEEP_ALIVE=30s` on both, the inactive model unloads within 30s
of being idle. So peak resident at any instant should stay around 5-6 GB.

If we ever see OOMs again, the answer is `s-4vcpu-8gb` → `s-4vcpu-16gb` or
similar. The architecture stays the same.

## Testing plan

1. Apply all 7 changes locally; tests should pass.
2. Destroy current cloud, reprovision on `s-4vcpu-8gb`.
3. SSH into droplet; confirm both `ollama list` outputs show their respective
   model after cloud-init completes (two sentinel files present).
4. Run `tmp/smoke11-ingest.sh` (full compound: body + URL + image + audio).
5. Expect pipeline to reach `succeeded`. Routing should now parse on first or
   second attempt; entities should populate `notes.tags`; embedding should
   populate `notes.embedding`.

## Out of scope (but still open from prior smokes)

- `attempts smallint(2)` overflow in `ingest_jobs` — claim spin can still
  burn through 32k in minutes if extraction-still-pending. Either widen to
  `int` or backoff between waiting claims.
- Cancel-leak: `RollingBackTfHandler` marks user-cancel destroy succeeded
  even when terraform never ran destroy (see
  `docs/smoke-2026-05-21-cancel-leak-handoff.md`).
- VLM extraction never cleanly tested: every previous attempt was killed
  mid-flight by the cloud-api crash loop, not by an ollama failure. After
  this split, VLM should finally run uninterrupted; whether it produces
  useful image descriptions is a separate validation.
