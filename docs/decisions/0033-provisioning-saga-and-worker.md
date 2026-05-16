# ADR-0033: Provisioning saga — state machine, worker model, and terraform workspace

Status: Accepted
Date: 2026-05-16

## Context

PORTAL-007 (TerraformRunner) through PORTAL-016 (cloud-admin-token receive endpoint) collectively implement "user clicks *create cloud* → portal provisions a VM → cloud is reachable on `<random>.thany.click` with TLS → portal receives the cloud's callback → wizard reports *ready*". The original `tickets-2026-05-13.md` describes these as discrete one-line tickets; the actual choreography — what state machine the work runs on, what process executes terraform, what happens on crash, how concurrent provisions share the host — was never specified.

[[0019-background-work-and-saga-durability]] locked the *queue mechanism* ("Postgres job queue, SKIP LOCKED + lease, mutable status, no event sourcing") but not the *saga shape*. PORTAL-002 landed the `provisioning_jobs` table without locking what the `status` values mean or how they transition. The handoff for PORTAL-007 cannot be written without these decisions because they fan out into every adjacent ticket: PORTAL-008/009 (provider modules) need the workspace contract; PORTAL-010 (cloud-init) needs to know what variables terraform passes through; PORTAL-011 (wizard UI) needs the event vocabulary; PORTAL-016 needs the failure modes; CLOUD-005 needs the polling contract.

Six sub-decisions are entangled here and need to be made together:

1. **Process shape** — where does the `terraform` binary actually run?
2. **State machine** — what statuses, transitions, and rollback paths model a provision?
3. **DNS ordering** — does the Cloudflare A-record creation come before or after terraform?
4. **Worker model** — synchronous worker per job, async per-phase claim, separate hosted service for polling, or something else?
5. **Workspace layout** — long-lived per-cloud directory or ephemeral per-job?
6. **Concurrency control** — how many jobs run simultaneously on one host?

This ADR pins all six together because the answers are coupled.

## Options considered

### 1. Process shape for terraform

- **A. In-process subprocess.** SagaWorker calls `Process.Start("terraform", ...)` directly. Terraform binary is baked into the SagaWorker container image (~80 MB unpacked). Child process is reaped if the worker container exits.
- **B. Fresh Docker container per job.** Worker mounts `/var/run/docker.sock` and calls `docker run --rm hashicorp/terraform:1.x apply`. Isolated per job; worker has effective root on host.
- **C. Sidecar HTTP terraform-runner.** A separate container exposes `POST /run`; worker is an HTTP client. Independently versionable; runner is stateless across requests.
- **D. Hosted job worker as its own .NET process.** Separate `ThanyMarcus.Portal.TerraformWorker` project. Variant of A but worker runs out of the API process.

### 2. Saga state machine

- **Coarse.** `pending` / `running` / `succeeded` / `failed` (4 statuses). Failure detail lives in a separate column.
- **Phased.** A status per *named phase* of work (8–12 statuses). Wizard reads status directly; failure mode is encoded in the status itself.
- **Phased plus rollback.** Phased, with explicit transient `rolling_back_*` statuses driving the cleanup work. ~10–12 statuses total.

### 3. DNS ordering

- **A. Pre-create DNS** before terraform with a placeholder IP, update after droplet IP captured. Reserves the subdomain.
- **B. Create DNS after terraform** once droplet IP is known. Single Cloudflare call, no bogus-IP window.
- **C. Embed DNS in terraform** via `cloudflare_record` resource. Couples DNS lifecycle to terraform state.

### 4. Worker model

- **A. Synchronous worker holds job for entire provision.** One worker = one job for ~10 minutes, including the 30-min `awaiting_cert` poll. Workers blocked on `Task.Delay`.
- **B. Worker advances one phase, releases, requeues.** Sync phases (`tf_*`, `dns_*`, `rolling_back_*`) hold a worker for the work duration; async-wait phases (`awaiting_cloud_callback`, `awaiting_cert`) are not worker-held — driven by inbound triggers or by a separate polling service.
- **C. Same as B, but unify the polling phases into the queue via delayed visibility** (`next_visible_at` column). Each phase, including the polling-waits, is a job-row claim with bounded work per claim.

### 5. Workspace layout on disk

- **A. Per-cloud long-lived directory.** `/var/lib/portal/terraform/clouds/<cloud_id>/` exists for the cloud's lifetime. Each job (create, destroy) `cd`s into it.
- **B. Per-job ephemeral directory.** `/var/lib/portal/terraform/jobs/<job_id>/` created at `tf_planning`, cleaned on terminal.
- **C. Hybrid.** Per-job ephemeral working dir + shared persistent plugin cache via `TF_PLUGIN_CACHE_DIR`.

### 6. Concurrency control

- **A. Sequential.** One worker, one job at a time. Five concurrent user clicks → fifth user waits ~50 min.
- **B. Bounded in-process parallelism.** Single worker process; `SemaphoreSlim(N)` caps concurrent in-flight jobs. Each job runs in a `Task.Run` released back to the claim loop.
- **C. Multiple worker processes.** `docker compose --scale saga-worker=N`. N containers, each handles one job at a time. More crash isolation; more resource overhead per process.

## Decision

- **1 → A** — in-process subprocess via `Process.Start`. Operational simplicity wins at single-VM scope; terraform's CLI is the canonical surface and replicating it over HTTP buys no isolation we need. Test seam is `ITerraformRunner`, with real impl wrapping `Process.Start` and a fake impl in tests.
- **2 → Phased plus rollback.** 7 in-flight phases, 5 failure terminals, 2 rollback transients. Wizard reads status directly; per-phase failure semantics are explicit.
- **3 → B** — DNS created *after* terraform with the real IP. No race to protect against (random subdomain), no bogus-IP window. Single Cloudflare call.
- **4 → C** — sync phases hold a worker; async-wait phases use queue-folded delayed visibility (`next_visible_at`). No separate polling `IHostedService`. One worker model for all phases.
- **5 → C** — hybrid: per-job ephemeral working dir + shared plugin cache. Plugin cache pre-warmed at container start to avoid the documented concurrent-`init` race.
- **6 → B** — `SemaphoreSlim(3)` bounded parallelism inside a single worker process. 3 concurrent in-flight jobs is comfortable on a B2ms (2 vCPU, 8 GB) given a single terraform `apply` consumes ~200–400 MB and ~0.3–0.5 vCPU during apply.

### The 7-phase state machine

```
                    enqueue (request handler writes
                    clouds row + provisioning_jobs row
                    in one transaction)
                              │
                              ▼
                        ┌──────────┐
                        │ pending  │
                        └─────┬────┘
                              │ worker claims
                              ▼
                       ┌───────────────┐
                       │ tf_planning   │  terraform init + plan -out=plan.tfplan -json
                       └───────┬───────┘
                               │ plan ok                  ▼ plan/init fails
                               │                    failed_tf (terminal)
                               ▼
                       ┌───────────────┐
                       │ tf_applying   │  terraform apply -auto-approve -json plan.tfplan
                       └───────┬───────┘
                               │ apply ok                 ▼ apply fails
                               │                    rolling_back_tf → failed_tf
                               ▼
                       ┌───────────────┐
                       │ dns_creating  │  Cloudflare API: A record <sub>.thany.click → IP
                       └───────┬───────┘
                               │ DNS ok                   ▼ DNS fails
                               │                    rolling_back_tf → failed_dns
                               ▼
                       ┌─────────────────────────┐
                       │ awaiting_cloud_callback │  queue-released; next_visible_at = now + 5 min
                       └───────┬─────────────────┘
              cloud callback   │   timeout (worker re-claims at 5-min mark)
              received         │
                               │      ▼ timeout
                               │   rolling_back_dns → rolling_back_tf → failed_callback
                               ▼
                       ┌────────────────┐
                       │ awaiting_cert  │  poll cloud /admin/health; cadence 5s for 60s then 30s
                       └───────┬────────┘
                               │ ready                    ▼ 30-min timeout (per DEC-003)
                               │                    failed_cert (terminal; NO rollback)
                               ▼
                       ┌───────────┐
                       │ succeeded │  (terminal)
                       └───────────┘

Cancel by user at any pre-terminal phase → cancelled, with rollback chain matching the
"would-have-been" failure from the current phase.
```

State enumeration:

```text
SagaStatus
─────────────────────────────────────────────────────────────
  pending                  -- enqueued, not yet claimed
  tf_planning              -- terraform init + plan running
  tf_applying              -- terraform apply running
  dns_creating             -- Cloudflare A-record being created
  awaiting_cloud_callback  -- waiting for cloud's POST /admin/register-with-portal
  awaiting_cert            -- polling cloud's /admin/health for cert_ready

  rolling_back_tf          -- transient; terraform destroy running
  rolling_back_dns         -- transient; Cloudflare A-record being deleted

  succeeded                -- terminal
  failed_tf                -- terminal; plan or apply failed
  failed_dns               -- terminal; DNS create failed after terraform succeeded
  failed_callback          -- terminal; cloud callback timed out
  failed_cert              -- terminal; cert acquisition timed out (NO rollback per DEC-003)
  cancelled                -- terminal; user-initiated abort
  rolled_back              -- terminal; reached after rolling_back_* completes
```

`failed_cert` is the only failure terminal that does NOT roll back. Rationale: once a cloud has compute + DNS + callback, it is *functional from the portal's POV*; cert acquisition is the cloud's problem and the user can retry via a "Re-check" button on the dashboard without re-provisioning. See ADR-0035 for the wizard surface.

### Schema additions to `provisioning_jobs`

Building on the columns landed in PORTAL-002:

```text
provisioning_jobs adds:
  next_visible_at         timestamptz   NOT NULL DEFAULT now()
  lease_expires_at        timestamptz   NULL
  claimed_by              text          NULL   -- worker instance id; for debugging
  attempt_count           smallint      NOT NULL DEFAULT 0
  phase_started_at        timestamptz   NULL   -- bumped on each phase transition; for ETAs
  events_log              jsonb         NOT NULL DEFAULT '[]'::jsonb
  tf_outputs              jsonb         NULL   -- captured from `terraform output -json`
INDEX (next_visible_at) WHERE status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')
```

`events_log` is appended-to in the same transaction as each phase transition (see [[0036-wizard-progress-transport]] for shape). `tf_outputs` captures the structured outputs from terraform (droplet IP, etc.) for the saga's downstream use (DNS creation needs the IP).

### Worker model — claim loop

The SagaWorker (a `BackgroundService` in `ThanyMarcus.Portal.SagaWorker` — separate project, separate container in `docker-compose.yml`) runs a single claim loop:

```csharp
public sealed class SagaWorker(...) : BackgroundService
{
    private readonly SemaphoreSlim concurrency = new(initialCount: 3, maxCount: 3);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await concurrency.WaitAsync(ct);
            var job = await ClaimNextAsync(ct);
            if (job is null)
            {
                concurrency.Release();
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await HandleAsync(job, ct); }
                finally { concurrency.Release(); }
            }, ct);
        }
    }
}
```

`ClaimNextAsync` runs the canonical SKIP LOCKED + lease query:

```sql
UPDATE provisioning_jobs
SET claimed_by       = $worker_instance_id,
    lease_expires_at = now() + interval '2 minutes',
    next_visible_at  = now() + interval '2 minutes',
    attempt_count    = attempt_count + 1
WHERE id = (
  SELECT id FROM provisioning_jobs
  WHERE status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')
    AND next_visible_at <= now()
    AND (claimed_by IS NULL OR lease_expires_at <= now())
  ORDER BY next_visible_at
  LIMIT 1
  FOR UPDATE SKIP LOCKED
)
RETURNING *;
```

`HandleAsync` is a switch on `status` that dispatches to per-phase handlers, each of which:

1. Performs bounded work (one terraform subprocess call, one HTTP call, etc.)
2. In one transaction: updates `status`, appends to `events_log`, sets `next_visible_at` for the next phase (immediate for sync phases; `now + 5min` for `awaiting_cloud_callback`; `now + 5s`/`now + 30s` for `awaiting_cert` depending on age).
3. Releases the claim.

Per-phase handler classes live in `ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/`:
- `TfPlanningHandler`
- `TfApplyingHandler`
- `DnsCreatingHandler`
- `AwaitingCloudCallbackHandler` (worker-side: handles timeout transition; the *trigger* is the inbound HTTP callback to PORTAL-016)
- `AwaitingCertHandler` (worker-side: one poll per claim)
- `RollingBackTfHandler`
- `RollingBackDnsHandler`

The inbound callback handler in PORTAL-016 transitions `awaiting_cloud_callback → awaiting_cert` directly via SQL; no worker involvement. When the worker later claims that row (at the 5-min mark), it sees `status != awaiting_cloud_callback` and the handler is a no-op.

### Concurrency invariants enforced at API enqueue

The cloud-create endpoint (and destroy endpoint) check for conflicting in-flight jobs:

```sql
SELECT 1 FROM provisioning_jobs
WHERE cloud_id = $1
  AND status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')
FOR SHARE
```

If a row exists, reject with `409 Conflict { "reason": "cloud_busy", "in_flight_job_id": ..., "current_phase": ... }`. The wizard's destroy button is disabled while any non-terminal job exists; the create endpoint additionally enforces a per-user concurrent-provision limit (default 1 in-flight per user; configurable as anti-abuse alongside Q13).

This avoids `pg` advisory-lock contention at the terraform layer — only one job per `cloud_id` ever runs at a time.

### Workspace layout on disk

```
/var/lib/portal/terraform/         ← Docker named volume, mounted into SagaWorker container
├── plugin-cache/                  ← shared across all jobs; persistent
│   └── registry.terraform.io/hashicorp/{digitalocean,azurerm,cloudflare}/...
└── jobs/
    └── <job_id>/                  ← ephemeral; created at tf_planning, swept after 7 days post-terminal
        ├── main.tf                ← invokes /app/terraform-modules/<provider>/
        ├── backend.tf             ← pg backend, workspace = cloud_id
        ├── variables.auto.tfvars  ← non-sensitive per-cloud values
        └── .terraform/            ← symlinks into plugin-cache
```

Per-provider terraform modules are baked into the SagaWorker container image at `/app/terraform-modules/digitalocean/` and `/app/terraform-modules/azure/` at build time (PORTAL-008/009 land them). The job's `main.tf` is templated by the SagaWorker on `tf_planning` entry — a ~10-line file calling `module "cloud" { source = "/app/terraform-modules/digitalocean" ... }`.

Sensitive values (GHCR PAT, enrollment_token, decrypted provider credentials) are passed via `TF_VAR_<name>` environment variables at `Process.Start` time, never written to the tfvars file. The `pg` backend connection string is similarly passed via `-backend-config="conn_str=$..."` at `terraform init`.

### Plugin cache pre-warm

At SagaWorker container start (before the claim loop begins), the entrypoint runs:

```bash
for provider in digitalocean azurerm cloudflare; do
  workdir=$(mktemp -d)
  cat > "$workdir/main.tf" <<EOF
terraform { required_providers { $provider = { source = "hashicorp/$provider" } } }
EOF
  TF_PLUGIN_CACHE_DIR=/var/lib/portal/terraform/plugin-cache \
    terraform -chdir="$workdir" init -input=false -no-color
  rm -rf "$workdir"
done
```

Populates the cache for all providers we ship. Adds ~30s to cold container start (first-ever boot); ~1s on subsequent restarts (cache is persistent on the volume). Prevents the documented race where two concurrent `terraform init`s against an empty cache corrupt provider downloads.

### Resource limits

`docker-compose.yml` for the SagaWorker container:

```yaml
saga-worker:
  image: ghcr.io/bboiko/thany-marcus-portal-saga-worker:latest
  deploy:
    resources:
      limits:
        cpus: '1.5'
        memory: 4G
  volumes:
    - terraform_data:/var/lib/portal/terraform
  environment:
    PORTAL_DB_CONNECTION:        "Host=postgres;..."
    PORTAL_MAX_CONCURRENT_JOBS:  "3"
```

Leaves ~0.5 vCPU + 4 GB for portal-api + postgres + caddy on a B2ms. A runaway terraform plugin gets OOM-killed at the container level; saga-resume picks up where it left off.

### Crash recovery routine

On SagaWorker startup, before the claim loop begins:

1. For each `provisioning_jobs` row where `status = 'tf_applying'` AND `lease_expires_at < now()`: `terraform force-unlock` the corresponding `pg` workspace, then leave the row claimable (the next claim will re-run `terraform apply`, which is idempotent against existing state).
2. For each row where `status = 'rolling_back_tf'` AND `lease_expires_at < now()`: same.
3. Run the lazy sweep of terminal job directories older than 7 days.

This is the saga's "bounded outage with automatic recovery" guarantee in action. The combination of (a) Postgres-durable saga state, (b) terraform's idempotency, (c) force-unlock on resume produces the property: **no work lost on host failure; outage bounded to VM-restart time** (typically 5–15 minutes on Azure).

## Consequences

### Positive

- **One worker model for all phases.** Sync work and polling-waits both flow through the same SKIP LOCKED claim loop; no parallel `IHostedService` to reason about. The saga is "a row with `status` that workers advance" — a single mental model.
- **Bounded in-process parallelism via `SemaphoreSlim(3)` matches a B2ms's resource envelope** without paying for per-container overhead. Scales by changing the constant or by raising compose `replicas` on a beefier host.
- **Per-job ephemeral workspace eliminates `.terraform/` corruption** — a known failure mode in long-running terraform workflows. The shared plugin cache recovers the init-cost saving.
- **DNS-after-terraform is one Cloudflare call and zero bogus-IP window**, vs. the pre-create-then-update pattern that the original ticket suggested.
- **`failed_cert` non-rollback preserves user work.** A cloud whose DNS hasn't propagated yet is still a functional cloud; making the user re-provision over a transient TLS issue would be hostile UX.
- **Crash recovery is the saga's job, not a separate concern.** The same mechanism handles VM crashes, container restarts, worker OOM-kills, and `lease_expires_at` timeouts. One code path; one mental model.

### Negative

- **In-process terraform subprocess couples the worker container's resource budget to terraform's appetite.** A misbehaving provider plugin could OOM the worker. Mitigated by Docker resource limits; mitigated further by saga-resume picking up after OOM-kill.
- **Same-cloud concurrent jobs are rejected at the API layer with `409 Conflict`**, not queued. UX choice: simpler error; user retries after current job terminates. Could alternatively queue, but adds complexity (per-cloud serialization queue) for marginal UX gain.
- **3-job concurrency cap is a per-VM constant**, not a per-user one. Five users clicking at once means the last two wait for the first three to clear `tf_applying` (~3–5 min). Acceptable at thesis scale; production would want per-user fair queuing.
- **`rolled_back` is a separate terminal status from the `failed_*` ones**, requiring callers/UI to handle both. Alternative would be a `status` + `failure_reason` column shape; we picked separate statuses for queryability and SSE-replay simplicity.
- **Per-job dir on Docker volume costs disk** (~50 MB per terminal job * 7-day retention). At even high thesis usage (~100 provisions/week), <500 MB. Negligible against a 128 GB SSD.

### Neutral

- **One worker process means horizontal scale requires either raising `SemaphoreSlim` count or scaling compose `replicas`.** Either is a config change; the code makes no single-instance assumption beyond the choice of how many SagaWorker containers run. Multi-replica works correctly because SKIP LOCKED ensures one job is claimed by exactly one worker.
- **Polling cadence (5s/30s) and timeouts (5 min callback, 30 min cert) are defensible defaults**, not magic. Configurable via app settings. The 30-min cert timeout matches DEC-003.
- **Pre-warming the plugin cache adds ~30s to first-ever container boot.** On the order of zero impact for steady-state operations.

## Related

- [[0019-background-work-and-saga-durability]] — locks the queue substrate (Postgres SKIP LOCKED + lease + mutable status, no event sourcing). This ADR adds `next_visible_at` for delayed visibility and the worker-as-separate-container model. See [[0019-background-work-and-saga-durability]] amendment in this ADR's commit.
- [[0029-type-mappings]] — `next_visible_at`, `phase_started_at`, `lease_expires_at` are `Instant`s mapped to `timestamptz`.
- [[0032-fk-cascades-and-soft-delete]] — `provisioning_jobs.cloud_id` FK behavior on cloud destroy: RESTRICT (provision jobs survive a cloud's soft-delete; cleanup sweep handles them).
- [[0034-cloud-bootstrap-and-portal-handshake]] — defines `awaiting_cloud_callback` trigger (cloud's POST to `/admin/register-with-portal`) and `awaiting_cert` polling contract (`/admin/health` shape).
- [[0036-wizard-progress-transport]] — defines the curated SSE event vocabulary, the `events_log` → SSE translation layer, and the `GET /api/clouds/{id}` REST snapshot + `GET /api/clouds/{id}/events` SSE channel that the wizard consumes.
- [[portal_deployment]] — single VM, single SagaWorker container, multi-instance-ready code; multi-replica is a deployment-topology change.
- DEC-003 — 30-min cert timeout per the cloud-pivot plan.
