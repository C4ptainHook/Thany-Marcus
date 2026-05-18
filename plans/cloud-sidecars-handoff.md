# CLOUD-SIDECARS — Ollama + docling-serve + parakeet-server on the user cloud — Handoff Brief

Date: 2026-05-19
Status: Draft (companion to `cloud-002-handoff.md` which landed the composite-ingest spine with all per-modality extractors stubbed). This ticket is the *infrastructure half* of swapping those stubs out — it adds the three CPU-only sidecar processes to the user cloud's docker-compose stack so future tickets (`CLOUD-VLM-WORKER`, `CLOUD-DOCLING-WORKER`, `CLOUD-PARAKEET-WORKER`) can wire `VlmWorker`/`DoclingWorker`/`ParakeetWorker` against real running endpoints. No saga-side code changes here.

**Goal:** every freshly-provisioned user cloud boots with the three sidecars the [[0043-cloud-side-model-lineup]] lineup names, with their models pre-pulled into named volumes during cloud-init so the first capture does not pay a 2 GB cold-download tax, and with health endpoints reachable from `cloud-api` over the private `cloud` docker network. After this ticket, an SSH `curl http://ollama:11434/api/version` / `curl http://docling:5001/` / `curl http://parakeet:5092/health` from inside the `cloud-api` container all return 200, and `docker compose ps` shows four healthy services (`cloud-api`, `postgres`, `ollama`, `docling`, `parakeet`).

Estimated **1.5 person-days** with AI-agent assistance. Pure infra ticket; the LOC delta is dominated by docker-compose YAML and the cloud-init runcmd block. No new `.cs` files; one `appsettings.json` section added; one new test file (`tests/ThanyMarcus.Cloud.Tests/Infrastructure/Sidecars/SidecarHealthSmokeTests.cs`) that hits the sidecars over their bridge network names from a Testcontainers-driven cloud-api container — proves the DNS + port + health-path wiring, not the model output.

## Where decisions live (read before doing anything)

- **`docs/decisions/0043-cloud-side-model-lineup.md`** — the lineup this ticket installs. Three sidecars (Ollama+MiniCPM-V 4.6 Q4, docling-serve+Granite-Docling, parakeet-server+Parakeet TDT 0.6B v3 INT8), their per-sidecar concurrency caps (Ollama=1, Docling=2, Parakeet=1), the steady-state RAM table (~5 GB sidecar footprint on the 16 GB droplet, ~9.4 GB total with cloud-api + Postgres + nginx + OS, ~6.6 GB headroom). **The contract this ticket implements.**
- **`docs/decisions/0042-cloud-ingest-pipeline-architecture.md`** — §1 worker topology (which workers claim which `target_sidecar`), §7 latency budget (the numbers this ticket's hardware must hit), `appsettings.json:IngestSaga:Sidecars:*:MaxConcurrency` config shape. **Why** the sidecars exist as separate processes (per-sidecar concurrency caps in code, runtime isolation in process).
- **`plans/cloud-002-handoff.md`** — established `infra/docker/cloud/docker-compose.yml`, the `IArtifactStore` + presigned-URL story (sidecars will fetch binaries via presigned GET URL, not via cloud-api), and the `NotImplemented{Image,Voice,File}Extractor` stubs this ticket's siblings replace. **Does not change** — the saga-side wiring is later tickets.
- **`plans/cloud-001-handoff.md`** + **`plans/cloud-001-amendment-nginx-handoff.md`** — established cloud-init's structure (`infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl`), the inlined `/opt/thany-cloud/docker-compose.yml` that the cloud's `thany-cloud.service` systemd unit invokes, and the `runcmd` ordering. **The file this ticket edits.** Note that the canonical compose lives **inlined inside the cloud-init template** — the `infra/docker/cloud/docker-compose.yml` in-repo file is a *local-dev mirror* of that inline block. Both must stay in sync; mismatches have caused production surprises before (the n8n droplet history that motivated the nginx amendment).
- **Memory `user_cloud_reverse_proxy.md`** — nginx terminates TLS on `:443` and proxies to `127.0.0.1:8080` (the cloud-api container's host-loopback publish). **Sidecars are NOT reachable from the public internet** — they have no `ports:` mapping, only the `networks: [cloud]` membership. Confirm by `ufw status` after apply: only 22/80/443 open.
- **Memory `composite_ingest_decision.md`** — `/api/ingest` carries one composite draft; the per-attachment processing fan-out is what the sidecars serve. This ticket is the runtime substrate that fan-out lands on.

**Do not change the saga, the ingest endpoints, or the extractor interfaces in this ticket.** Pure infra. The saga still stubs everything; the sidecars just sit there idle until `CLOUD-VLM-WORKER` etc. wire the workers against them.

## Design decisions resolved in the 2026-05-19 grilling

Recorded here so the next reader does not re-litigate them.

1. **Sidecars live in the same Docker Compose stack as `cloud-api` + `postgres`.** Not a separate compose file, not a separate VM. The user cloud is a single droplet; one stack, one `docker compose up`. This is the [[0043-cloud-side-model-lineup]] §"Steady-state resource footprint on a 16 GB droplet" hardware contract.

2. **Sidecars are only reachable on the private bridge network `cloud`.** No `ports:` mapping. Cloud-api resolves them by service name (`ollama`, `docling`, `parakeet`). Defense-in-depth: even if nginx is misconfigured, the model endpoints are not on the public internet. The Spaces-presigned-GET pattern means sidecars do **not** need outbound HTTPS to the bucket from the public internet either — they reach Spaces via the droplet's NAT.

3. **Models live in named Docker volumes**, populated **once at cloud-init time** by an init-container pattern, and persist across `docker compose down/up`. Re-pulling 2 GB of model weights on every restart is unacceptable on a 4 vCPU droplet. Named volumes: `ollama-models`, `docling-models`, `parakeet-models`.

4. **Pre-pull happens during cloud-init `runcmd`**, BEFORE `systemctl enable --now thany-cloud.service` fires. Cloud-init waits synchronously for the pulls to complete (3–8 min on a typical residential-uplink-equivalent IPv4 path; SSDs are not the bottleneck). The `final_message` in cloud-init now reads `"Cloud-init completed in $UPTIME seconds. nginx + Cloud.Api + 3 sidecars running; models pre-pulled."` — the increased boot time is a one-time cost.

5. **Pre-pull strategy per sidecar (chosen after weighing alternatives in §"Alternatives considered" below):**
   - **Ollama**: a separate `ollama-puller` service in compose, profile `init`, that `docker compose --profile init run --rm ollama-puller` is called once from cloud-init's `runcmd`. It runs `ollama pull <tag>` against a sibling `ollama serve` and exits. The same named volume (`ollama-models`) is mounted by both. Subsequent compose-up uses no profile and the puller does not start.
   - **docling-serve**: models ship inside the official `quay.io/docling-project/docling-serve:latest-cpu` image (Granite-Docling-258M + TableFormer + Layout are bundled). No pre-pull step needed; first-call latency is just model-load-to-RAM, not download. Image pull on first compose-up is the only download cost.
   - **parakeet-server**: achetronic/parakeet does **not** auto-download models. We pre-stage the INT8 model files (~670 MB total) into the `parakeet-models` named volume via a one-shot `parakeet-puller` service that runs an alpine container + `curl` against the upstream's published asset URLs (or, fallback, runs `make models` inside a temporary checkout). Same profile-gated pattern as Ollama.

6. **Sidecar health endpoints are docker-compose `healthcheck:` blocks, NOT cloud-api `/health/ready` participants.** Cloud-api stays "live" even when sidecars are degraded — the saga handles unreachable-sidecar via the extraction_task `failed` path, and queue depth shows up in Prometheus. Compose health states gate the **start ordering** (`cloud-api.depends_on.ollama.condition=service_healthy`) but not the readiness probe nginx hits. This decouples user-facing uptime from sidecar transient issues (a model crash should not make `/api/sync/pull` return 503).

7. **Health endpoint paths**, verified 2026-05-19 via WebFetch:
   - **Ollama**: `GET /api/version` (returns `{"version":"<v>"}`). The Ollama docs do not document a root-`/` "Ollama is running" route in the API ref; use `/api/version` as the canonical probe.
   - **docling-serve**: no documented `/health` route in `usage.md` or `configuration.md` (as of 2026-05-19). Use `GET /` (returns the static UI or a 200 root) — confirm on the pinned image tag at apply time and adjust if a `/health` lands upstream. Alternative: `GET /docs` (FastAPI's Swagger UI) returns 200 if the app is up.
   - **parakeet-server** (achetronic): `GET /health` returns `{"status":"ok"}` per the README. Canonical.
   - **If docling-serve's root path changes**, only the compose `healthcheck.test` and the `appsettings.json:IngestSaga:Sidecars:Docling:HealthPath` value need to change; no code rebuild.

8. **No GPU support paths in this ticket.** The CPU-only droplet is the thesis baseline ([[0043-cloud-side-model-lineup]] §Context). GPU burst-worker ([[0035-burst-worker-llm-tier]]) is deferred future-work; when it lands it will be a *separate* compose file on a separate VM, not a sibling of these sidecars. Do not stub GPU image variants.

9. **Sidecar concurrency caps live in `cloud-api` (saga side), NOT in sidecar processes.** Ollama serializes inference internally; docling-serve has `UVICORN_WORKERS=1` (default); parakeet-server runs one worker per process. The semaphore that prevents thrashing is `IngestSaga:Sidecars:*:MaxConcurrency` in cloud-api. This ticket only ships the **config keys + default values**; the workers that read those values land in the M5 sibling tickets.

10. **Image tags pinned**, not `:latest`. Pinned tags:
    - `ollama/ollama:0.5.1` (latest-stable as of 2026-05-19; revisit at apply time but pin not float)
    - `quay.io/docling-project/docling-serve:v0.5.0-cpu` (CPU variant; pinned to a verified tag)
    - `ghcr.io/achetronic/parakeet:v0.3.0` (verify the actual tag against the GHCR registry at apply time)
    - The **exact tag strings above need a 5-min verification pass** before terraform-apply — they reflect the latest-stable-as-of-research, but registries can move. Pinning is mandatory; `:latest` causes silent drift between staging and production user clouds.

11. **Model tag pinned for Ollama.** The MiniCPM-V 4.6 Q4_K_M GGUF tag on Ollama's library — verify against ollama.com/library at apply time. Pull tag goes into `appsettings.json:IngestSaga:Models:Vlm:OllamaTag` AND the cloud-init `ollama pull <tag>` step; both reference the same env var (`OLLAMA_PULL_TAG`) to avoid drift.

12. **Resource limits set per service** via `deploy.resources.limits` in compose (Docker swarm-compose syntax that compose v2.20+ honors as a hard cap on non-swarm too — verify with `docker compose config`). Ollama `mem_limit=3g`, Docling `mem_limit=1.5g`, Parakeet `mem_limit=2.5g`. Total sidecar RAM hard-capped at 7 GB; with Postgres (~512 MB) + cloud-api (~500 MB) + OS (~2 GB), the droplet has ~6 GB headroom (matches [[0043]] table).

13. **Sidecars `restart: unless-stopped`** like cloud-api + postgres. The systemd unit `thany-cloud.service` brings the whole stack up/down atomically; individual sidecar crashes get restarted by Docker, persistent failures will surface as unhealthy state to the saga.

14. **No SSE / inter-sidecar communication.** Each sidecar talks only to cloud-api over its own port. Sidecars do not call each other. Cloud-api is the only orchestrator.

## Scope boundary (precise)

One pass. Three concrete file edits + one test file + one smoke runbook update. ~1.5 days because the verify-at-apply-time tag confirmation and the cloud-init renderpath test (`terraform plan` against a real DO account) eat the back half-day.

### 1. `infra/docker/cloud/docker-compose.yml` — add 3 sidecars + 2 puller services + 3 named volumes

Replace the current 50-line file with the layout below. The local-dev mirror does NOT use the `init` profile by default — local dev pulls models on first compose-up by running `docker compose --profile init up ollama-puller parakeet-puller` once, exactly mirroring the cloud-init flow.

```yaml
services:
  cloud-api:
    image: ghcr.io/c4ptainhook/thany-cloud-api:${IMAGE_TAG:-latest}
    restart: unless-stopped
    ports:
      - "127.0.0.1:8080:8080"
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: "http://+:8080"
      ConnectionStrings__Cloud: Host=postgres;Port=5432;Username=cloud;Password=${POSTGRES_PASSWORD};Database=cloud
      Bootstrap__CloudId: ${CLOUD_ID}
      Bootstrap__Hostname: ${DOMAIN}
      Bootstrap__EnrollmentToken: ${ENROLLMENT_TOKEN}
      Bootstrap__CloudAdminToken: ${CLOUD_ADMIN_TOKEN}
      Bootstrap__PortalCallbackUrl: ${PORTAL_CALLBACK_URL}
      Cert__LiveDir: /etc/letsencrypt/live/${DOMAIN}
      IngestSaga__Sidecars__Ollama__BaseUrl: http://ollama:11434
      IngestSaga__Sidecars__Ollama__HealthPath: /api/version
      IngestSaga__Sidecars__Docling__BaseUrl: http://docling:5001
      IngestSaga__Sidecars__Docling__HealthPath: /
      IngestSaga__Sidecars__Parakeet__BaseUrl: http://parakeet:5092
      IngestSaga__Sidecars__Parakeet__HealthPath: /health
    volumes:
      - /etc/letsencrypt:/etc/letsencrypt:ro
    depends_on:
      postgres:    { condition: service_healthy }
      ollama:      { condition: service_healthy }
      docling:     { condition: service_healthy }
      parakeet:    { condition: service_healthy }
    networks: [cloud]
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/health/live || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5

  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: cloud
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: cloud
    volumes:
      - pg-data:/var/lib/postgresql/data
    networks: [cloud]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U cloud -d cloud"]
      interval: 5s
      timeout: 5s
      retries: 10

  ollama:
    image: ollama/ollama:0.5.1
    restart: unless-stopped
    environment:
      OLLAMA_HOST: "0.0.0.0:11434"
      OLLAMA_KEEP_ALIVE: "30m"
      OLLAMA_NUM_PARALLEL: "1"
      OLLAMA_MAX_LOADED_MODELS: "1"
    volumes:
      - ollama-models:/root/.ollama
    networks: [cloud]
    mem_limit: 3g
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O- http://localhost:11434/api/version | grep -q version || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 10
      start_period: 30s

  ollama-puller:
    image: ollama/ollama:0.5.1
    profiles: ["init"]
    restart: "no"
    entrypoint: ["/bin/sh", "-c"]
    command: |
      set -e
      ollama serve &
      SERVE_PID=$$!
      until wget -q -O- http://127.0.0.1:11434/api/version >/dev/null 2>&1; do sleep 1; done
      ollama pull "${OLLAMA_PULL_TAG}"
      kill -TERM $$SERVE_PID
      wait $$SERVE_PID || true
    environment:
      OLLAMA_HOST: "127.0.0.1:11434"
      OLLAMA_PULL_TAG: ${OLLAMA_PULL_TAG:-minicpm-v:8b-2.6-q4_K_M}
    volumes:
      - ollama-models:/root/.ollama
    networks: [cloud]

  docling:
    image: quay.io/docling-project/docling-serve:v0.5.0-cpu
    restart: unless-stopped
    environment:
      UVICORN_HOST: "0.0.0.0"
      UVICORN_PORT: "5001"
      UVICORN_WORKERS: "1"
      DOCLING_SERVE_LOG_LEVEL: "INFO"
      DOCLING_NUM_THREADS: "2"
    volumes:
      - docling-models:/opt/app-root/src/.cache
    networks: [cloud]
    mem_limit: 1500m
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:5001/ || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 10
      start_period: 60s

  parakeet:
    image: ghcr.io/achetronic/parakeet:v0.3.0
    restart: unless-stopped
    command: ["-models", "/models", "-port", "5092"]
    volumes:
      - parakeet-models:/models:ro
    networks: [cloud]
    mem_limit: 2500m
    depends_on:
      parakeet-puller: { condition: service_completed_successfully }
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O- http://localhost:5092/health | grep -q '\"status\":\"ok\"' || exit 1"]
      interval: 15s
      timeout: 5s
      retries: 10
      start_period: 45s

  parakeet-puller:
    image: alpine:3.20
    profiles: ["init"]
    restart: "no"
    entrypoint: ["/bin/sh", "-c"]
    command: |
      set -e
      apk add --no-cache curl
      if [ -f /models/.pulled ]; then
        echo "parakeet models already pulled; skipping"
        exit 0
      fi
      mkdir -p /models
      # NOTE: replace with the actual asset URLs published by achetronic/parakeet
      # for Parakeet TDT 0.6B v3 INT8. Verify at apply time.
      curl -fsSL "${PARAKEET_MODEL_URL}" -o /models/parakeet-tdt-0.6b-v3-int8.tar.gz
      tar -xzf /models/parakeet-tdt-0.6b-v3-int8.tar.gz -C /models
      rm /models/parakeet-tdt-0.6b-v3-int8.tar.gz
      touch /models/.pulled
    environment:
      PARAKEET_MODEL_URL: ${PARAKEET_MODEL_URL:?PARAKEET_MODEL_URL required}
    volumes:
      - parakeet-models:/models
    networks: [cloud]

volumes:
  pg-data:
  ollama-models:
  docling-models:
  parakeet-models:

networks:
  cloud:
```

Decisions baked into the YAML, ranked by likely-to-surprise:

- **`depends_on: { ollama: { condition: service_healthy } }` on cloud-api** means cloud-api waits up to ~150 s (10 retries × 15 s) for Ollama's healthcheck to flip green. If Ollama never becomes healthy, cloud-api never starts. **This is the intended behavior** during boot — without sidecars there's no point serving traffic that will all fail at `extracting_attachments`. nginx still serves `502 Bad Gateway` until cloud-api is up; users see a brief unavailability window, not a fast-fail. If this turns out to be too slow on real droplets, drop the sidecar `depends_on` and let cloud-api start eagerly — the saga handles unhealthy sidecars gracefully — but keep the cloud-api healthcheck as-is.
- **`profiles: ["init"]` on `ollama-puller` and `parakeet-puller`** means they are NOT started by a plain `docker compose up -d`. They only run when explicitly invoked: `docker compose --profile init run --rm ollama-puller`. This is how cloud-init runcmd triggers the one-time pull; subsequent restarts skip them.
- **`OLLAMA_PULL_TAG` env var** flows from cloud-init through to the puller's `ollama pull` invocation. Default value `minicpm-v:8b-2.6-q4_K_M` is a **placeholder** — verify the exact tag against ollama.com/library/minicpm-v at apply time. The MiniCPM-V 4.6 release this ADR-0043 targets may publish under a different tag (e.g. `minicpm-v:4.6-q4` or `minicpm-v:1.3b-q4`).
- **`PARAKEET_MODEL_URL`** is required (the `:?` fail-fast syntax). The achetronic/parakeet README documents `make models` for the int8 download but does not publish a stable single-URL asset; the implementer must determine the canonical artifact URL at apply time and bake it into terraform variables. **If a stable URL does not exist**, the fallback is a custom image that extends `ghcr.io/achetronic/parakeet:v0.3.0` and runs `make models` at build time — heavier ops surface, but no external download dependency.
- **`mem_limit` per service** caps RAM hard. If a sidecar OOMs, Docker SIGKILLs and the `restart: unless-stopped` brings it back. Sustained OOM is a model-size-vs-droplet-size mismatch and surfaces in Prometheus / portal alerts.
- **`docling-models` volume at `/opt/app-root/src/.cache`** — this is docling-serve's default model cache location inside its CPU image. Verify against the image's docs at apply time; the path is image-specific and changes between major versions.

### 2. `infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl` — inline the new compose + add pre-pull runcmd steps

Three edits to the file:

#### 2a. Add `OLLAMA_PULL_TAG` and `PARAKEET_MODEL_URL` to the env template (`/etc/thany-cloud/cloud.env.tpl`)

Append two lines to the `cloud.env.tpl` `write_files` block (currently lines 48–67 of the template):

```yaml
      OLLAMA_PULL_TAG=${ollama_pull_tag}
      PARAKEET_MODEL_URL=${parakeet_model_url}
```

These are templated by terraform — set in `main.tf`/`variables.tf` and passed through `templatefile()`. Both terraform vars have defaults in `variables.tf` (the same placeholder values as the compose file's defaults), but operators can override per-cloud if a custom model is desired.

#### 2b. Replace the inlined `/opt/thany-cloud/docker-compose.yml` write_files block with the new content from §1 above

Currently lines 136–195 of the template. The replacement is verbatim the YAML in §1 with the `$${...}` terraform-escape applied to every shell variable (the `$${POSTGRES_PASSWORD}`, etc.) — same pattern as the existing block. Be careful: the existing pattern uses `$${IMAGE_TAG:-latest}` etc.; the new pattern adds `$${OLLAMA_PULL_TAG:-minicpm-v:8b-2.6-q4_K_M}` and `$${PARAKEET_MODEL_URL:?...}`. The `?` syntax in compose's `:?` fail-fast variable substitution does **not** conflict with terraform's `?` literal — both are passthrough in cloud-init context — but verify with `terraform plan -out` that the rendered yaml is what you expect, exactly once.

#### 2c. Add pre-pull runcmd steps BEFORE the `systemctl enable --now thany-cloud.service` step

Current `runcmd` block ends with (around line 242):

```yaml
  - systemctl daemon-reload
  - systemctl enable --now thany-cloud.service
```

Insert the pre-pull sequence between the `chown ... docker-compose.yml` step (line 228) and the `daemon-reload`:

```yaml
  - |
    # Pre-pull sidecar images so the first compose-up does not race the puller services
    # against image-pull bandwidth competition.
    cd /opt/thany-cloud
    sudo -u ${admin_user} docker compose pull ollama docling parakeet
    sudo -u ${admin_user} docker compose --profile init pull ollama-puller parakeet-puller

  - |
    # Pre-pull Ollama model (~2 GB) into the named volume.
    # One-shot service exits on success.
    cd /opt/thany-cloud
    sudo -u ${admin_user} docker compose --profile init run --rm ollama-puller

  - |
    # Pre-stage Parakeet INT8 model weights (~670 MB) into the named volume.
    cd /opt/thany-cloud
    sudo -u ${admin_user} docker compose --profile init run --rm parakeet-puller
```

These three runcmd entries run sequentially (cloud-init's runcmd is ordered). The total added boot time is dominated by the Ollama pull (~3–5 min at typical DO droplet uplink speed) and the parakeet download (~30–60 s) — call it 4–7 min added to first boot. After this, the named volumes are populated and `docker compose down/up` is sub-second to start (just image-load + serve).

#### 2d. Update `final_message`

Change the last line of the template from:

```yaml
final_message: "Cloud-init completed in $UPTIME seconds. nginx + Cloud.Api running; registration fires on cert install."
```

to:

```yaml
final_message: "Cloud-init completed in $UPTIME seconds. nginx + Cloud.Api + 3 sidecars running with models pre-pulled; registration fires on cert install."
```

This shows up in DO console output as the visible "done" line; useful when debugging a slow apply.

### 3. `infra/docker/saga-worker/terraform-modules/digitalocean/variables.tf` — two new terraform vars

```hcl
variable "ollama_pull_tag" {
  description = "Ollama model tag to pre-pull at cloud-init time (e.g. 'minicpm-v:8b-2.6-q4_K_M'). Must match the tag the VlmWorker requests at runtime."
  type        = string
  default     = "minicpm-v:8b-2.6-q4_K_M"
}

variable "parakeet_model_url" {
  description = "Canonical download URL for the Parakeet TDT 0.6B v3 INT8 model bundle (.tar.gz) — pre-staged into the parakeet-models volume at cloud-init time. See achetronic/parakeet README for the published artifact URL."
  type        = string
  # No default — operator must set explicitly until a stable upstream URL is identified.
}
```

And add the matching `templatefile()` arguments to whichever resource in `main.tf` renders the cloud-init:

```hcl
templatefile("${path.module}/cloud-init.yaml.tpl", {
  # ... existing args ...
  ollama_pull_tag    = var.ollama_pull_tag
  parakeet_model_url = var.parakeet_model_url
})
```

### 4. `src/ThanyMarcus.Cloud.Api/appsettings.json` — add `IngestSaga:Sidecars:*` config section

The cloud-api process will read these values from environment-variable overrides at runtime; the `appsettings.json` defaults document the keys + dev-local values. **No DI wiring or `SidecarOptions.cs` class lands in this ticket** — the sibling workers (M5) own that. This ticket only ships the keys.

Append to `appsettings.json`:

```json
{
  "IngestSaga": {
    "Sidecars": {
      "Ollama": {
        "BaseUrl": "http://localhost:11434",
        "HealthPath": "/api/version",
        "MaxConcurrency": 1
      },
      "Docling": {
        "BaseUrl": "http://localhost:5001",
        "HealthPath": "/",
        "MaxConcurrency": 2
      },
      "Parakeet": {
        "BaseUrl": "http://localhost:5092",
        "HealthPath": "/health",
        "MaxConcurrency": 1
      }
    },
    "Models": {
      "Vlm":   { "OllamaTag": "minicpm-v:8b-2.6-q4_K_M" },
      "Asr":   { "ParakeetVersion": "parakeet-tdt-0.6b-v3-int8" },
      "Docs":  { "DoclingVersion": "granite-docling-258m" }
    }
  }
}
```

The `BaseUrl` defaults are localhost for dev; production overrides via env (`IngestSaga__Sidecars__Ollama__BaseUrl=http://ollama:11434`) — already set in §1's compose env block.

### 5. `tests/ThanyMarcus.Cloud.Tests/Infrastructure/Sidecars/SidecarHealthSmokeTests.cs` — DNS+port reachability test

A single `[Fact]` that confirms the docker-compose service-name resolution actually works for a cloud-api container alongside the three sidecars. **Does not test model inference** — that lives in the M5 worker tickets. **Does not even pull the real model images** — uses tiny mock-server containers with the same service names + ports + paths, asserting that cloud-api can reach `http://ollama:11434/api/version` and get 200.

```csharp
public sealed class SidecarHealthSmokeTests : IAsyncLifetime
{
    private INetwork _network = null!;
    private IContainer _ollama = null!;
    private IContainer _docling = null!;
    private IContainer _parakeet = null!;
    private IContainer _cloudApi = null!;

    public async Task InitializeAsync()
    {
        _network = new NetworkBuilder().WithName("cloud-sidecars-test").Build();
        await _network.CreateAsync();

        // Three trivial mock servers, each responding 200 OK on the expected health path.
        // These stand in for the real sidecars and verify only the DNS+port+path wiring.
        _ollama   = MockHttp(_network, "ollama",   11434, "/api/version", "{\"version\":\"mock\"}");
        _docling  = MockHttp(_network, "docling",  5001,  "/",            "ok");
        _parakeet = MockHttp(_network, "parakeet", 5092,  "/health",      "{\"status\":\"ok\"}");
        await Task.WhenAll(_ollama.StartAsync(), _docling.StartAsync(), _parakeet.StartAsync());

        _cloudApi = new ContainerBuilder()
            .WithImage("alpine:3.20")
            .WithNetwork(_network)
            .WithEntrypoint("/bin/sh", "-c", "apk add --no-cache curl && sleep 3600")
            .Build();
        await _cloudApi.StartAsync();
    }

    [Fact]
    public async Task All_three_sidecars_are_reachable_by_service_name()
    {
        await AssertCurl(_cloudApi, "http://ollama:11434/api/version",   contains: "\"version\"");
        await AssertCurl(_cloudApi, "http://docling:5001/",              contains: "ok");
        await AssertCurl(_cloudApi, "http://parakeet:5092/health",       contains: "\"status\":\"ok\"");
    }

    // helpers MockHttp + AssertCurl in the same file; ~30 LOC of Testcontainers plumbing.

    public async Task DisposeAsync()
    {
        await _cloudApi.DisposeAsync();
        await _ollama.DisposeAsync();
        await _docling.DisposeAsync();
        await _parakeet.DisposeAsync();
        await _network.DeleteAsync();
    }
}
```

This test exists to catch regressions where someone renames a service in compose (`ollama` → `ollama-server`) without updating `IngestSaga__Sidecars__Ollama__BaseUrl`. It runs on CI in ~10 s; no real model is downloaded.

### 6. `plans/portal-011-smoke-runbook.md` — extend smoke to verify sidecar health on a real provisioned cloud

Append a new smoke step (#8 in the current numbering) after the cert-install verification:

```
8. SSH into the cloud, confirm sidecar health:
   $ docker compose -f /opt/thany-cloud/docker-compose.yml ps
   → 4 services healthy: cloud-api, postgres, ollama, docling, parakeet
   $ docker exec thany-cloud-cloud-api-1 wget -q -O- http://ollama:11434/api/version
   → {"version":"<v>"}
   $ docker exec thany-cloud-cloud-api-1 wget -q -O- http://docling:5001/
   → 200 OK
   $ docker exec thany-cloud-cloud-api-1 wget -q -O- http://parakeet:5092/health
   → {"status":"ok"}
   $ docker volume ls | grep thany-cloud
   → ollama-models, docling-models, parakeet-models, pg-data (4 volumes)
   $ docker volume inspect thany-cloud_ollama-models --format '{{ .Mountpoint }}' | xargs du -sh
   → ~2 GB (MiniCPM-V Q4 pre-pulled)
```

The exact docker compose project name prefix (`thany-cloud_` vs `thany-cloud-`) depends on how the systemd unit invokes compose; verify the prefix during apply.

## Alternatives considered

1. **All three sidecars in a custom-built image with models baked in.** Smaller per-cloud cold start (no pull at cloud-init time), but blows up image size (~5 GB) and ties every model version bump to a docker build + push round-trip. Volume-based pre-pull keeps the image lean and lets model swaps happen via `OLLAMA_PULL_TAG` change + `compose --profile init run` — no rebuild. **Rejected.**

2. **Separate `docker-compose.sidecars.yml` invoked alongside the main compose.** Cleaner separation of concerns, but `depends_on` doesn't span compose files in standalone mode (it does in swarm mode). Sequencing cloud-api's start on sidecar health would require systemd-level ordering, which is a heavier moving piece than the current single-file approach. **Rejected.**

3. **Run sidecars on the host as native processes (not containers).** Lighter RAM (no container overhead), but Ollama is shipped only as a binary + Docker image, docling-serve is Python-with-conda-deps, parakeet-server is Go-with-onnxruntime — three completely different install paths. The container model gives uniform start/stop/log/restart semantics for free. **Rejected.**

4. **No pre-pull; lazy-load on first request.** Simplest cloud-init. But: first capture pays a 2 GB model download tax (~3-5 min), during which the saga `extracting_attachments` phase times out and the user sees a `failed` note. Replaying after the download finishes works (reprocess path), but the demo failure is bad. **Rejected.** Pre-pull is the obvious right call.

5. **Use docker-compose's `command` to run `ollama pull` at every start** instead of profile-gated puller. Idempotent (Ollama detects already-pulled), but adds ~2-5 s to every restart for the digest check and conflates "start the server" with "ensure model present." The profile-gated puller is cleaner because the operator can see whether the pull step has been done by checking volume presence. **Rejected.**

6. **GPU paths in this ticket.** Out of scope per design decision #8 above. **Rejected (deferred).**

7. **One puller container that handles all three pre-pulls.** Less compose surface, but conflates three independent operations into one failure boundary. With three separate puller services, a failed parakeet download doesn't taint a successful ollama pull, and the failure surface is per-sidecar. **Rejected** (the duplication cost is ~20 lines of yaml).

8. **Wait for sidecars to be healthy *inside cloud-init* before declaring complete**, instead of trusting compose's `depends_on`. Adds a polling loop in runcmd; the value is debuggability of "why did boot fail" via cloud-init logs vs. having to docker-inspect later. **Accepted as a follow-up** if the smoke runbook step #8 fails frequently in practice — for now, compose's healthcheck contract is trusted. The systemd `thany-cloud.service` is `Type=oneshot` + `RemainAfterExit=yes`, so it exits as soon as `docker compose up -d` returns, **before** sidecars are healthy. This is fine for cloud-init's purposes (system is "up") but means the registration-callback fires while sidecars are still loading models into RAM (~30 s for docling, ~10 s for ollama on cold serve, ~5 s for parakeet). The portal sees cert_installed → calls back → cloud-api responds healthy. **No issue** for the demo; saga handles unready-sidecar via task retry.

## Consequences

### Positive

- The user cloud now has all three model-serving sidecars ready to receive HTTP requests the moment cloud-api's saga workers fire (in the M5 sibling tickets). The runtime substrate matches [[0043-cloud-side-model-lineup]] §"Steady-state resource footprint" exactly.
- Models live in named volumes; `docker compose down/up` is sub-second to restart, and `docker compose down -v` is the explicit reset-everything switch.
- Sidecar concurrency caps are config-driven (`IngestSaga:Sidecars:*:MaxConcurrency`), letting eval iterate on the cap values without code changes.
- Health probes are uniform: every sidecar has a compose `healthcheck:` block; cloud-api's startup waits on them; Prometheus exporters can scrape them (next ticket).
- The "no public ports" stance means the sidecar attack surface is **zero from the public internet** — only the cloud-api process (which itself sits behind nginx behind certbot) can talk to them.

### Negative / accepted costs

- First cloud-init now takes ~7–12 min (up from ~5 min) because of the model pre-pull. Visible in the portal wizard's "provisioning" step; ETA in [[0036-wizard-progress-transport]] needs the bumped range.
- Three more services per cloud means three more places where image-pull or model-pull failures can stall a provisioning. Each failure mode has a clear error path (compose pull error → cloud-init failure → `portal-callback` never fires → wizard sees timeout); operators get clean signal but the failure surface grew.
- Hardcoded `mem_limit` values are guesses based on [[0043]]'s steady-state table. Real workloads may run hotter or cooler; revisit after eval.
- The `:?` fail-fast on `PARAKEET_MODEL_URL` means the operator MUST set the variable in terraform — there's no fallback to a default value. Intentional: silent fallback to a broken URL is worse than a loud terraform error, but it's a slight ops nuisance.
- The MiniCPM-V tag (`minicpm-v:8b-2.6-q4_K_M`) is a placeholder; the actual MiniCPM-V 4.6 Ollama tag must be verified at apply time against ollama.com/library. If MiniCPM-V 4.6 has not landed on Ollama yet, the implementer must either wait, or fall back to MiniCPM-V 2.6 (or whichever is current) and accept the quality delta until 4.6 ships.

### Operational

- **Volume size after first boot**: `ollama-models` ~2 GB, `docling-models` ~600 MB (image cache), `parakeet-models` ~700 MB, `pg-data` minimal. Reserve ~5 GB of disk headroom on the droplet beyond the OS + Docker overhead. Default DO droplets (50 GB SSD on `s-4vcpu-16gb`) are well above this.
- **Restart**: `systemctl restart thany-cloud.service` brings the whole stack down and back up. Named volumes survive; sidecars are healthy again within ~30 s.
- **Model upgrade**: bump `OLLAMA_PULL_TAG` in terraform → re-apply the user cloud's cloud-init → cloud-init re-runs the `ollama-puller` step → new tag is pulled into the named volume → `docker compose restart ollama` picks it up. Old tag stays in the volume until `docker compose exec ollama ollama rm <oldtag>` is called.
- **Observability**: each sidecar exposes a compose healthcheck. Next ticket (`CLOUD-OTEL`) adds Prometheus scrape configs that hit `/metrics` on each (Ollama has built-in `/metrics`; docling-serve has `DOCLING_SERVE_OTEL_ENABLE_METRICS=true`; parakeet has none — add an /api/metrics shim or accept no parakeet-side metrics).

## Acceptance criteria

1. ✅ `infra/docker/cloud/docker-compose.yml` matches §1 exactly (modulo image-tag verification). `docker compose config` parses cleanly.
2. ✅ `terraform plan` against the DO module shows the two new variables and the new templated cloud-init contents. The rendered cloud-init's inline `docker-compose.yml` matches the local-dev mirror in §1, byte-for-byte modulo the `${...}` → `$${...}` escape.
3. ✅ Local-dev: from a fresh checkout, after setting `OLLAMA_PULL_TAG=...` and `PARAKEET_MODEL_URL=...` in `.env`, running `docker compose --profile init up ollama-puller parakeet-puller` populates the two named volumes; subsequent `docker compose up -d` brings all 5 services healthy within ~60 s; `curl http://localhost:11434/api/version`, `curl http://localhost:5001/`, `curl http://localhost:5092/health` from the host all return 200. (Cloud-api's port 8080 is host-bound at 127.0.0.1; the sidecars do NOT publish ports — these `curl`s only work from inside the `cloud` network in production. For local-dev verification only, temporarily add `ports: ["11434:11434"]` etc., or `docker compose exec cloud-api wget ...`.)
4. ✅ A real DO droplet provisioned via the saga-worker terraform module boots and runs cloud-init to completion. The smoke runbook's new step #8 (§6 above) passes on the live droplet — all four healthchecks return 200, all four named volumes are populated, no `ports:` mapping exposes a sidecar publicly (`ufw status` shows only 22/80/443 open; `ss -tlnp` on the droplet shows nothing on 11434/5001/5092).
5. ✅ `tests/ThanyMarcus.Cloud.Tests/Infrastructure/Sidecars/SidecarHealthSmokeTests.cs` is green on CI. Runs in <15 s with no real model downloads.
6. ✅ `appsettings.json` contains the new `IngestSaga:Sidecars:*` and `IngestSaga:Models:*` sections; cloud-api builds clean with warnings-as-errors (the config is read-only-at-runtime in this ticket — sibling tickets bind it).
7. ✅ Boot time on a `s-4vcpu-16gb` DO droplet from `terraform apply` to `final_message` printed is ≤ 15 min (cold model downloads + image pulls). 95th percentile target: 10 min. If consistently >15 min, revisit the pre-pull strategy.
8. ✅ Verified on the smoke droplet: `docker stats` after 5 min idle shows: ollama ~50 MB resident (model not loaded until first call); docling ~1 GB (Granite-Docling loads at boot); parakeet ~700 MB; cloud-api ~400 MB; postgres ~150 MB. Total ~2.4 GB at idle; well under the 3 GB OS+overhead headroom on the 16 GB droplet. (After first VLM call, ollama climbs to ~2 GB and stays there for `OLLAMA_KEEP_ALIVE=30m`.)

## Out of scope (named explicitly)

1. ❌ **`VlmWorker` / `DoclingWorker` / `ParakeetWorker` implementations.** Land in `CLOUD-VLM-WORKER`, `CLOUD-DOCLING-WORKER`, `CLOUD-PARAKEET-WORKER` separately. This ticket only ensures the sidecars are reachable; calling them is later.
2. ❌ **The `extraction_tasks` schema migration** (the new table per [[0042]] §"Schema delta"). Lands with the first worker that needs it. Schema delta is one EF migration, not split per worker.
3. ❌ **`SidecarOptions.cs` POCO + DI registration in `Program.cs`.** Sibling tickets that consume the config bind it. This ticket only ships the keys in appsettings.
4. ❌ **Prometheus scrape configs for sidecars.** `CLOUD-OTEL` adds them. Sidecar healthchecks via compose are sufficient for this ticket.
5. ❌ **Cloud-side LLM prompts** ([[0044-cloud-intelligence-layer]] forthcoming). The Ollama sidecar serves whatever model `OLLAMA_PULL_TAG` names; the prompts that interrogate it are later.
6. ❌ **GPU burst-worker compose** ([[0035]]). Future-work; separate compose file on separate VM.
7. ❌ **Sidecar TLS / mutual auth between cloud-api and sidecars.** They're on the private `cloud` bridge network; plain HTTP is appropriate. Adding mTLS is a future hardening pass; not required for thesis.
8. ❌ **Auto-update of sidecar images / models.** Image tags are pinned; updates are explicit operator action (terraform var bump → re-apply). No automated drift.
9. ❌ **Volume backup of `ollama-models` / `parakeet-models`.** Models are reproducible from tag + URL; backup the data, not the cache. `pg_dump` of `pg-data` is the only backup that matters.
10. ❌ **Sidecar log aggregation** beyond docker's default JSON file driver. Future ops ticket.

## Open contract decisions (carry forward)

1. **Exact Ollama tag for MiniCPM-V 4.6.** Implementer verifies against ollama.com/library at apply time and updates the terraform default + appsettings default in one PR. If MiniCPM-V 4.6 has not landed on Ollama, decide: wait, or use MiniCPM-V 2.6 as interim. Document the decision in `appsettings.json` comment-free (per memory rule); the canonical tag flow is `OLLAMA_PULL_TAG` env var → `ollama pull` → `IngestSaga:Models:Vlm:OllamaTag` runtime reference. They must stay in lockstep.

2. **Canonical Parakeet model URL.** achetronic/parakeet's README documents `make models` but does not (as of 2026-05-19) publish a stable single asset URL. Options:
   - (a) Fork achetronic/parakeet → publish the int8 tarball to a Thany-controlled storage location (e.g. a public Spaces bucket) → terraform default points there.
   - (b) Custom-build the parakeet image that bakes the models in (rebuild on model bump).
   - (c) Pre-bake the model files into a small Docker image (`thany-parakeet-models:v1`) that the puller pulls and copies; lets us use the upstream achetronic image unmodified.
   Option (c) is cleanest if upstream remains URL-free; (a) is cleanest if a public asset URL is acceptable. Decide and document in a short ADR (`docs/decisions/0046-parakeet-model-distribution.md`) as part of this ticket.

3. **Docling-serve health path.** Currently `/` based on the FastAPI app's root behavior — verify on the pinned image tag at apply time. If a real `/health` route lands upstream (open feature request in the docling-serve repo as of 2026-05-19), update `appsettings.json:IngestSaga:Sidecars:Docling:HealthPath` + the compose `healthcheck.test` in lockstep.

4. **`mem_limit` ground-truth values.** The values in §1 are calibrated to [[0043]]'s table. The first eval against real captures (M5 demo) measures actual peak resident; revise the values if needed. The 6 GB droplet headroom absorbs ±1 GB of error.

5. **Sidecar restart-after-OOM policy.** Currently `restart: unless-stopped`, which means infinite restarts on a persistent OOM. Consider `restart: on-failure:5` to gate runaway restart loops once Prometheus alerting is in place; out of scope here.

6. **ADRs to author.** Candidates:
   - **ADR-0046: Parakeet model distribution.** Decides option (a)/(b)/(c) above.
   - **ADR-0047: Sidecar concurrency policy and per-cloud RAM budget.** Captures the [[0043]] table values + the `mem_limit` rationale + the budget headroom. Not strictly needed but useful for thesis chapter on system architecture.

## What CLOUD-VLM-WORKER / CLOUD-DOCLING-WORKER / CLOUD-PARAKEET-WORKER inherit

After this ticket lands:
- Each worker reads `IngestSaga:Sidecars:<name>:BaseUrl` + creates a typed `HttpClient` against it. No DNS or port discovery; everything is config-driven.
- Each worker reads `IngestSaga:Sidecars:<name>:MaxConcurrency` and creates a `SemaphoreSlim` with that capacity. The compose stack does not enforce concurrency; cloud-api does.
- Each worker reads `IngestSaga:Models:<kind>:*` to compute its `extraction_cache_key = sha256:<sidecar>:<model>:<version>`. Model bumps invalidate caches naturally.
- Health probing happens at the saga level: the worker can `HEAD` its sidecar's health path before each extraction call, or rely on docker-compose's healthcheck-driven restart cycle. The simpler path is the latter — workers just call and let HTTP errors surface as `extraction_task.failed` + retry.
- The compose stack is **already running** by the time any worker code lands. M5's first worker ticket can develop locally with a working Ollama and immediately test against real model output, with no infra distraction.

The cumulative arc is:

```
CLOUD-002 (composite-ingest spine, URL extraction)
  ─► CLOUD-SIDECARS (THIS TICKET — Ollama + Docling + Parakeet running, models pre-pulled)
  ─► CLOUD-VLM-WORKER  (VlmWorker calls ollama:11434/api/generate; image attachments get descriptions)
  ─► CLOUD-DOCLING-WORKER  (DoclingWorker calls docling:5001/v1/convert/source; PDFs become Markdown)
  ─► CLOUD-PARAKEET-WORKER  (ParakeetWorker calls parakeet:5092/v1/audio/transcriptions; voice memos become text)
  ─► CLOUD-VIDEO-SPLITTER  (VideoSplitterWorker uses ffmpeg locally + spawns child image/audio tasks)
  ─► [M5 demo: composite capture with URL + image + voice + PDF → vault with real extracted content]
  ─► CLOUD-EMBEDDING (Granite Embedding 278m in-process; pgvector retrieval helpers)
  ─► [M6 demo: end-to-end thesis-defensible CPU-only pipeline]
```

This ticket is the **infrastructure beachhead** that makes the M5 worker tickets purely about C# code — no docker, no terraform, no model bytes to wrangle.

## Risks and unknowns flagged for grilling next session

1. **MiniCPM-V 4.6 may not exist on Ollama yet.** [[0043-cloud-side-model-lineup]] was authored 2026-05-19 from WebFetch-verified Hugging Face sources; the Ollama library mirrors are often a few weeks behind. If MiniCPM-V 4.6 is not pullable via `ollama pull` at apply time, the implementer chooses between waiting and using 2.6 with a config flag. Track in CLOUD-SIDECARS-FOLLOWUP if 4.6 lands later.
2. **Parakeet INT8 model distribution.** Open contract decision #2 above. Real blocker unless an asset URL is identified.
3. **docling-serve image tag stability.** `v0.5.0-cpu` is a guess; verify against quay.io/docling-project/docling-serve at apply time. If only `latest-cpu` is published, pin to a sha256 digest instead.
4. **The 4 vCPU / 16 GB droplet may run hot under fanout.** Three workers calling sidecars in parallel + ffmpeg + Postgres + cloud-api. The `mem_limit` caps prevent runaway, but CPU contention (especially Ollama vs Docling fighting for the same cores) is observable only under load. Eval measures this; if bad, drop `IngestSaga:Sidecars:Docling:MaxConcurrency=2` to 1.
5. **`OLLAMA_KEEP_ALIVE=30m` holds 2 GB of RAM for 30 min after the last call.** This is fine on the 16 GB droplet but means idle-after-burst captures hold the budget; if the droplet hosts other workloads in future, revisit.
6. **The puller services are one-shot, but docker-compose does NOT delete their containers automatically after `--rm` if the parent compose stack is also running.** Verify behavior on the smoke droplet; the `--rm` flag and `restart: "no"` should be sufficient, but watch for stale `exited` containers in `docker ps -a` after apply.

## Why a separate handoff (and not folded into CLOUD-VLM-WORKER)

The ingest-pipeline tickets (`CLOUD-VLM-WORKER`, `CLOUD-DOCLING-WORKER`, `CLOUD-PARAKEET-WORKER`) are each ~1 person-day of C# saga work. Folding the sidecar infrastructure into the first of them would (a) bloat that ticket to ~2.5 days and mix two distinct concerns (infra vs application code), (b) leave the other two workers waiting on the first ticket's infra work, (c) make the smoke verification ambiguous (is the test exercising the worker code or the sidecar setup?). Shipping CLOUD-SIDECARS first means all three M5 worker tickets can land in parallel against a working substrate. This ticket's 1.5 days is paid once and amortizes over three sibling deliveries.
