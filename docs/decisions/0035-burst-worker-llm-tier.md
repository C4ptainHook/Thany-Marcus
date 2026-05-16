# ADR-0035: Burst-worker LLM tier — two-tier per-user cloud with on-demand inference VM

Status: Accepted
Date: 2026-05-16
Supersedes: DEC-001 as originally closed on 2026-05-13 (always-on 16 GB / 4 vCPU single tier)

## Context

DEC-001 was closed on 2026-05-13 as: **16 GB / 4 vCPU minimum, single tier, always on.** Per-provider mapping was DO `s-4vcpu-16gb` (~$84/mo) and Azure `Standard_B4ms` (~$120/mo). The 16 GB was driven entirely by Ollama holding Gemma 4 E4B Q4 in RAM 24/7.

Three days later (2026-05-16) two separate pressures reopened the decision:

1. **Cost is a thesis-defense issue, not just a wallet issue.** Competitor reference points (Notion AI, Mem, Reflect, Tana) sit at $10/mo by multi-tenanting inference across thousands of users and/or routing to hosted APIs. A defended thesis pitch of "single-tenant sovereign LLM at $84–120/mo" invites the committee question *"who pays 8× SaaS pricing for this?"* and has no clean answer.
2. **The workload is bursty.** A personal-notes user captures intermittently. 23+ hours/day the LLM RAM is idle. Single-tenant always-on inference is the worst possible economic configuration: you pay full hardware cost, get none of the batching efficiency a hosted GPU achieves, and amortize over one user.

The economic gap between single-tenant Ollama and hosted API inference (e.g. ~$2.50/mo of Anthropic Haiku tokens for the same workload that costs $84/mo of always-on VM) is structural — it is what hosted inference providers sell. The cost cannot be closed by tuning. But it *can* be narrowed by recognizing that the always-on tier exists only because we conflated "the user's cloud" with "the user's GPU equivalent."

Splitting the two tiers — a cheap always-on control plane and an ephemeral on-demand inference worker — collapses idle cost while preserving single-tenant sovereignty.

## Decisions

### 1. Two-tier per-user deployment

**Control plane (always-on):**
- Spec: 2 vCPU / 4 GB
- Per-provider: DO `s-2vcpu-4gb` (~$24/mo), Azure `B2s` (~$30/mo), Hetzner `CPX21` (~€5/mo)
- Runs: ingest API, Postgres + pgvector, Caddy, embeddings (e5-small in-process), cheap extractors (AngleSharp, Readability, PdfPig, Tesseract), classical D-filter, saga queue, WorkerLifecycleService, sync endpoints
- Public ingress: HTTPS via Caddy + LE on the per-cloud subdomain
- Joined to a per-cloud private VPC (DO VPC / Azure VNet)

**Burst worker (ephemeral, spawned on demand):**
- Spec: 4 vCPU / 16 GB
- Per-provider: DO `s-4vcpu-16gb` ($0.125/hr), Azure `B4ms` (~$0.166/hr), Hetzner `CPX41` (~€0.05/hr)
- Runs: Ollama (Gemma 4 E4B Q4 default), Parakeet sidecar (sherpa-onnx), worker process
- No public ingress; joined to the same per-cloud private VPC as the control plane; firewall rules restrict ingress to the control plane's private IP
- No persistent volume for user data; model weights pulled from GHCR at boot
- Lifecycle owned by the control plane (see §2)

Realistic monthly cost (30 captures/day across ~3 sessions = ~22.5 worker-hours/mo):
- DO: ~$28–32
- Azure: ~$35–40
- Hetzner: ~€11

### 2. WorkerLifecycleService — state machine

A new `IHostedService` in the control plane owns the worker's lifecycle.

```text
none ──[first LLM/ASR job leases]──► spawning
spawning ──[worker POSTs /admin/worker-ready ≤7 min]──► alive
spawning ──[7 min timeout]──► destroying (retry on next job)
alive ──[10 min with no new leases]──► draining
draining ──[in-flight leases done OR lease TTL expires]──► destroying
destroying ──[terraform destroy returns]──► none
```

- `none → spawning`: shell out to `terraform apply` against the worker module (PORTAL-008b for DO, PORTAL-009b for Azure). Inject as variables: a short-lived `worker_registration_token`, the control plane's private IP, the Postgres connection string (read from a secrets file populated at control plane provisioning), and the LLM model identifier.
- Worker cloud-init pulls `docker-compose.worker.yml` from GHCR, starts Ollama (which loads the model into RAM), starts Parakeet, starts the worker process binary. The worker process POSTs `/admin/worker-ready` to the control plane with the registration token.
- `alive`: worker leases LLM/ASR jobs from the control plane's Postgres queue using `FOR UPDATE SKIP LOCKED` with a 10-min lease TTL. Coordination is via shared Postgres state only — no direct RPC from control plane to worker.
- `alive → draining`: control plane flips `workers.should_drain = true`. Worker observes on its next 30 s heartbeat and stops leasing new jobs. Existing leases finish normally.
- `destroying`: shell out to `terraform destroy` against the same workspace. Typical ~60 s.

The control plane persists worker state in a `workers` table: `(cloud_id, state, spawned_at, last_heartbeat_at, lease_count, should_drain, terraform_workspace)`. The state machine is durable across control plane restarts — if the control plane reboots mid-spawn, on wake it reconciles by querying the worker (if `alive`) or re-running the terraform op.

### 3. Sovereignty posture

- The burst worker has no public DNS, no public IP-routable services (its DO/Azure public IP is firewalled to deny all inbound except SSH from the control plane during debugging windows).
- Inference inputs (artifact text, embeddings) travel control plane → worker over the per-cloud private VPC only. They never traverse the public internet.
- The worker's disk is ephemeral. Model weights are public (Gemma is a published model); no user content is written to disk on the worker. Inference inputs and outputs exist in RAM only and are sent back to the control plane's Postgres immediately.
- On `terraform destroy` the worker VM and its disk are gone. No artifact of user content remains on the worker tier.

The sovereignty story is **strictly stronger** than the original always-on 16 GB design: the always-on design had one box with user content on disk; the burst design has user content only on the control plane's encrypted volume, with inference happening on a stateless transient compute node.

### 4. UX honesty about cold-start

First capture in an idle session pays ~5 min latency (terraform apply ~60 s + cloud-init Docker install + image pulls ~90 s + Ollama model load ~30 s + inference ~5–30 s). Subsequent captures within the 10 min idle window are warm (~30 s).

The plugin status bar surfaces worker state explicitly: `● Worker warm` / `◐ Worker waking (~5 min)` / `○ Worker idle`. No attempt to hide the architecture behind a "processing..." spinner.

### 5. What changes vs. ADR-0033 (provisioning saga)

ADR-0033 covers the *control plane* provisioning saga. The worker lifecycle is a peer state machine running *inside* the control plane after it is fully provisioned and registered with the portal. The portal does not spawn workers; the control plane does. This keeps the portal stateless w.r.t. inference capacity and keeps the worker invisible to the portal admin proxy.

The terraform workspace layout becomes: `portal-state.{cloud_id}.control_plane` (one per user, lives as long as the cloud) and `portal-state.{cloud_id}.worker` (one per user, applied/destroyed by the control plane many times over the cloud's lifetime).

### 6. Failure modes

| Failure | Behavior |
|---|---|
| terraform apply for worker fails | WorkerLifecycleService retries with exponential backoff (1, 2, 4, 8, 16 min, max 5 attempts); jobs stay queued; `/admin/health.worker_state = "spawn_failing"` surfaces to plugin status bar |
| Worker boots but never calls `/admin/worker-ready` within 7 min | Control plane runs `terraform destroy`, transitions back to `none`, retries spawn on next leased job |
| Worker crashes mid-job | Postgres lease TTL (10 min) expires; next worker (or replacement spawn) re-leases the job |
| Worker can't reach control plane Postgres | Worker process self-shuts down after 5 min of failed heartbeats; control plane detects via missing heartbeats, transitions to `destroying` |
| Race: two workers spawned for same cloud | `SKIP LOCKED` prevents duplicate processing; second worker leases nothing, idles, shuts down after 10 min — ~$0.04 wasted |
| Control plane crashes mid-spawn | On wake, reconciliation reads `workers.state`. If `spawning` and >7 min old → destroy + retry. If `alive` and worker reachable → adopt. If `alive` and unreachable → destroy + retry. |

### 7. What this does NOT change

- The structured-output S4 single-call model from 2026-05-13 is unchanged. One LLM call per artifact, JSON schema, routing + entity extraction + wikilink anchors.
- Safe/unsafe mode dichotomy is unchanged. Unsafe mode bypasses the worker entirely and calls the external API from the control plane.
- The portal's admin proxy is unchanged. Settings, model swap, plugin tokens — all addressed to the control plane.
- The plugin's contract with the cloud is unchanged. Plugin posts to the control plane's URL, polls `/api/sync/pull`, never sees the worker.

## Consequences

- **+2 person-days** on the M2/M6 backlog: PORTAL-008b (DO worker module, 0.5d), PORTAL-009b (Azure worker module, 0.5d), CLOUD-016b (WorkerLifecycleService, 1d), CLOUD-016c (worker process binary, 0.5d). Total project budget moves from 25 → 27 person-days.
- **Cost story becomes defensible for the thesis.** ~$28–32/mo on DO sits within an order of magnitude of $10/mo SaaS competitors; the gap is the price of single-tenant sovereignty rather than an architectural premium.
- **Eval-window economics extend.** With $150 DO student credit, ~4.7 months of runway at the burst rate vs. ~1.8 months at the always-on rate. The credit now comfortably covers M0–M9 plus thesis revision and defense demos.
- **Cold-start latency becomes a measurable variable.** M9 evaluation gains a new metric (capture-to-Markdown distribution with cold-start histogram) that is actually thesis-publishable.
- **New failure surface: worker spawn pathology.** Terraform apply failures, cloud-init failures, model-pull failures from GHCR. All surfaced via `/admin/health.worker_state` and observable in the plugin status bar; no silent failures, but more states for the user to interpret.
- **The plugin must be honest about cold-start.** A blanket "processing..." UX is a UX lie under burst architecture. Status bar exposes worker state.
- **Hetzner remains unimplemented** but referenced as the adopter-pricing anchor (~€11/mo) in the thesis cost chapter. Adding the Hetzner Terraform module is a ~0.75d future-work item; the architecture is already provider-portable.

## Related

- [[0033-provisioning-saga-and-worker]] — the control-plane provisioning saga; the worker lifecycle is a peer state machine running inside the already-provisioned control plane.
- [[0034-cloud-bootstrap-and-portal-handshake]] — control plane bootstrap (unchanged); worker registration is a separate, smaller handshake against the control plane (not the portal).
- `plans/cloud-pivot-plan-2026-05-13.md` §7 (architecture diagram), §11 (cloud backend stack), §12 (saga + WorkerLifecycleService), §13 (LLM strategy), §14 (server-side subsystems), §25 (MVP scope).
- `plans/tickets-2026-05-13.md` DEC-001 (revised), PORTAL-008b, PORTAL-009b, CLOUD-016b, CLOUD-016c.
