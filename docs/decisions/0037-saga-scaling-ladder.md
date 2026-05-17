# ADR-0037: Saga scaling ladder

Status: Accepted
Date: 2026-05-17

## Context

[[0019-background-work-and-saga-durability]] chose a Postgres-backed saga queue (`SELECT ... FOR UPDATE SKIP LOCKED` + lease/heartbeat). [[0033-provisioning-saga-and-worker]] sized in-process concurrency to a B2ms via `SemaphoreSlim(3)`. Neither documents the path beyond the single-VM, single-worker shape.

The viva question "what if 500 users showed up at once?" needs a defensible answer that is not "rewrite the saga." This ADR records the four rungs of escalation as a ladder so the chosen rung (1) is justified against the rungs we explicitly defer (2, 3, 4).

The ladder also serves as the operator runbook: each rung names a concrete trigger (provider API rate limits, Postgres `max_connections`, VM CPU saturation) at which the next rung becomes worthwhile.

## Options considered

The four rungs below are not alternatives — they are an ordered escalation path. The decision is *where to stop pre-building*. Rejected non-ladder alternatives:

- **External queue (RabbitMQ, Redis Streams, SQS).** Already rejected in [[0019-background-work-and-saga-durability]]; SKIP LOCKED is far from saturation at thesis scale.
- **Workflow engine adoption upfront (Temporal, Durable Functions).** Considered as a starting point and rejected — framework adoption with one saga type is poor weight-to-leverage.
- **Per-user fair-share queueing.** Out of scope; documented in [[0033-provisioning-saga-and-worker]] consequences.

## Decision

Build **rung 1 capability** into the codebase (already present: `Provisioning:MaxConcurrentJobs` config knob, compose-scalable saga-worker container, SKIP LOCKED claim query that is correct across N replicas). Do NOT pre-build rungs 2/3/4. Escalate only when the named trigger fires.

### Rung 1 — multi-replica saga-worker on the existing VM

`docker compose up -d --scale saga-worker=N`. Each replica retains its per-process `SemaphoreSlim(MaxConcurrentJobs)`. Cross-replica contention is handled by `SELECT ... FOR UPDATE SKIP LOCKED` (already approved in [[0019-background-work-and-saga-durability]]:205).

**When to use.** Cohort grows beyond ~3 simultaneous provisions and the single worker is the bottleneck (not provider APIs, not Postgres).

**What changes.** A compose flag. Zero code changes. Lease/heartbeat semantics from [[0019-background-work-and-saga-durability]] guarantee that two replicas cannot run the same job.

**Operational concern.** Postgres connection budget. With managed Flexible Server B1ms ([[0038-managed-postgres]]) defaulting to `max_connections = 50`, at ≥4 replicas the LISTEN connection + Npgsql pool per replica + portal-api pool starts to bite. Two levers:

```
ALTER SYSTEM SET max_connections = 200;   -- requires Flexible Server restart
```

Or introduce PgBouncer on the VM as a transaction-pooling front for the saga-worker replicas.

**Ceiling.** ~10 concurrent provisions on the existing B2ms before VM CPU saturates during simultaneous `terraform apply` runs.

**Cost delta.** $0/mo.

### Rung 2 — bigger VM or dedicated worker VM

Resize portal VM B2ms → B4ms (4 vCPU, 16 GB), or peel saga-worker off onto a second B2ms.

**When to use.** Rung 1 plateaus at ~10 concurrent and the trigger is VM CPU during `terraform apply`, not provider rate limits.

**What changes.** Terraform variable for VM size; one redeploy. No code changes. With a dedicated worker VM, portal-api and saga-worker no longer share a failure domain.

**Cost delta.** B2ms → B4ms is approximately +$60/mo. Dedicated second B2ms is approximately +$60/mo at the same shape.

**Ceiling.** ~20 concurrent provisions. Past that, Postgres write contention and Cloudflare DNS API rate limits dominate.

### Rung 3 — orchestrator/executor split (Azure Container Apps Jobs)

Saga-worker shrinks to a lightweight orchestrator (single replica, holds saga state, dispatches phase work). Each `tf_planning`, `tf_applying`, `dns_creating`, `rolling_back_*` phase becomes an Azure Container Apps Job invocation that runs once and exits.

**When to use.** Sustained >20 concurrent provisions or burst peaks where rung 2 wastes baseline VM capacity.

**What changes.**
- New container image: `terraform-runner` job, parameterized by job id + phase.
- Orchestrator dispatches via Azure SDK; receives completion via webhook or polled job status.
- Secret-passing redesign (provider tokens currently piped via `TF_VAR_*` env into a process the worker controls; now must be injected into a job invocation via Container Apps secret refs).
- Test rewrite: the `ITerraformRunner` seam from [[0033-provisioning-saga-and-worker]] grows a second implementation; fake impl in tests stays compatible.
- Cold-start cost: ~5–10s per job invocation.

**Cost.** Container Apps Jobs is consumption-priced; per-provision compute approximately $0.005 (vCPU-seconds + memory-seconds). Add Azure Container Registry Basic at ~$5/mo. Build effort approximately 5–10 dev-days.

**Ceiling.** ~50–100 concurrent provisions. Beyond this, provider API rate limits (DigitalOcean, Azure, Cloudflare) dominate and per-user fair queuing becomes the real lever.

**Cost delta.** ~$5/mo fixed + per-provision compute, offset by retiring rung-2 VM capacity. Vendor coupling to Azure-specific service (Container Apps Jobs).

### Rung 4 — workflow engine (Temporal / Durable Functions)

Only relevant at >10 distinct saga types or sustained >1000 concurrent workflows.

**When to use.** Not on the thesis horizon. Documented for completeness so the answer to "why don't you use Temporal?" is "because the trigger conditions do not yet apply."

**What changes.** Whole-saga rewrite against the workflow engine's primitives. Lose Postgres-as-single-source-of-truth property; gain durable timer + retry primitives we currently implement by hand.

**Cost delta.** Operator burden (managed Temporal Cloud is approximately $200+/mo entry, self-hosted Temporal needs its own Postgres + frontend cluster).

## Consequences

### Positive

- The thesis defends a concrete choice (rung 1) rather than an absence of choice. "Why not Temporal?" has a documented answer.
- Each rung names its trigger (CPU saturation, `max_connections`, rate limits, saga-type count), so escalation is not a judgment call.
- Rung 1 capability is already present: scaling is a compose flag, not a refactor.
- Avoids speculative complexity. Rungs 2/3/4 stay paper until their trigger fires.

### Negative

- A >20-concurrent burst cannot be absorbed without operator intervention (resize VM or scale replicas + raise `max_connections`). Acceptable at thesis scale.
- Rung 3's secret-passing redesign and test-double rewrite are non-trivial; deferring them means they become a sprint at trigger time, not a steady-state cost.
- Lock-in escalates with each rung: rung 1 is portable, rung 3 is Azure-specific, rung 4 is workflow-engine-specific.

### Neutral

- The current `Provisioning:MaxConcurrentJobs` knob already covers per-replica intra-process concurrency; rung 1 simply multiplies that across processes.
- Cloudflare DNS API rate limits (~1200/5min on the default plan) are not the binding constraint until rung 3; documented here so they are not a surprise.

## Related

- [[0019-background-work-and-saga-durability]] — saga queue substrate; rung 1 multiplies its workers, semantics unchanged
- [[0033-provisioning-saga-and-worker]] — current `SemaphoreSlim(3)` sizing, the rung-1 starting point
- [[0038-managed-postgres]] — `max_connections` budget that constrains rung 1 at ≥4 replicas
- `plans/cloud-pivot-plan-2026-05-13.md §11` — control-plane shape that this ladder operates within
