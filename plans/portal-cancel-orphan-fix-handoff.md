# Cancel leaves provisioned resources alive — root cause + fix (handoff, 2026-06-03)

## TL;DR

`POST /api/clouds/{id}/cancel` marks the cloud `cancelled` and the cancel job
`succeeded`, but the DO droplet/firewall/volume/Spaces bucket can stay alive.
Root cause is **not** missing teardown logic — it's that cancellation is a
*parallel compensating job* with no mutual exclusion against, and no cooperative
signal to, the still-running `create` saga. The create job and its cancel job
can run **at the same time, for the same cloud, on the same Terraform pg-backend
state**, and no forward handler ever checks whether a cancel was requested.

This supersedes the root-cause *hypothesis* in
`docs/smoke-2026-05-21-cancel-leak-handoff.md` (which blamed
`RollingBackTfHandler`'s workspace-select skip). The current cancel path does its
own `BestEffortDestroyAsync` and does not go through `RollingBackTfHandler` at
all — so that hypothesis is stale. The real cause is below.

## What already exists (baseline — don't rebuild it)

The PORTAL-CANCEL machinery from `plans/portal-cancel-handoff.md` is fully
landed and working in isolation:

- `CancelCloudEndpoints.cs` — `POST /api/clouds/{id}/cancel`, TOTP + step-up
  gated, hostname-confirm body, idempotent (returns 202 to an in-flight cancel),
  enqueues a `ProvisioningJob(Kind=cancel, Status=pending)`. Endpoint is correct;
  leave it (we'll add one line: set a cancel signal — see fix).
- `SagaKinds.Cancel`, routed in `SagaPhaseDispatcher.HandleAsync` →
  `CancelHandler`.
- `CancelHandler.cs` — looks up the sibling create job, calls
  `tf.HasResourcesAsync`, runs `BestEffortDestroyAsync` (init + destroy), marks
  sibling create job `cancelled`, marks cloud `cancelled` + `destroyed_at`, marks
  cancel job `succeeded`. All inside a serializable txn.
- `SagaPhaseDispatcher.HandleAsync` has a **dispatch-time** guard: if the cloud
  row is already terminal, it abandons the job instead of running the phase
  handler.
- Terraform: `backend "pg"` (state in Postgres), workspace name = `cloud.Id`,
  workdir = `/var/lib/portal/terraform/jobs/<jobId>` on named volume
  `terraform_data` (survives container rebuilds). Resource state is durable.

So the pieces are there. The bug is in how they interleave under concurrency.

## Root cause (precise)

**No per-cloud serialization + no cooperative cancel signal.**

1. The worker claim query (`SagaWorker.cs:128-146`) selects *any* non-terminal
   job ordered by `next_visible_at`, `LIMIT 1 FOR UPDATE SKIP LOCKED`, with
   `MaxConcurrentJobs=3`. Nothing prevents the `create` job and the `cancel` job
   for the **same cloud** from being claimed into two concurrency slots and
   running simultaneously.

2. No forward handler (`TfApplyingHandler`, `DnsCreatingHandler`,
   `AwaitingCloudCallbackHandler`, `AwaitingCertHandler`,
   `IssuingPluginTokenHandler`) checks for a cancel request. They run their
   Terraform step to completion regardless. Confirmed: `TfApplyingHandler.cs:36-189`
   has no cancel check anywhere; it goes apply → `dns_creating` unconditionally.

3. The dispatcher's terminal-cloud guard (`SagaPhaseDispatcher.HandleAsync`,
   the `SagaStatus.IsTerminal(cloud.ProvisioningStatus)` branch) only fires at
   the **start** of a handler invocation. It does nothing for a create handler
   that is *already executing* (e.g. mid-`terraform apply`) when cancel flips the
   cloud to `cancelled`.

4. `CancelHandler.cs:88` sets the sibling create job `Status=Cancelled` but does
   **not** bump `transition_version`. `transition_version` is the EF concurrency
   token (`SagaTransitions.TransitionAsync` increments it explicitly). So a
   racing create handler that loaded the job at version N still matches
   `WHERE transition_version = N` and its transition to `dns_creating` **succeeds,
   clobbering the cancellation** — no `DbUpdateConcurrencyException` is raised.

### The two concrete leak paths

- **Skip-destroy race.** Cancel during `tf_applying`: cancel job runs concurrently
  and calls `HasResourcesAsync` → `terraform state list` *before* the create
  job's `apply` has written the droplet into pg state → returns `false` →
  `CancelHandler.cs:71` logs `no_resources_to_destroy`, **skips destroy**, marks
  cloud `cancelled`. The create job's `apply` then finishes and creates the
  droplet. Orphan. UI says cancelled; DO still bills.

- **Lost state-lock fight / re-create.** If `apply` *has* written state, cancel's
  `destroy` and create's `apply` collide on the pg-backend advisory state lock →
  `destroy` fails (`tf_destroy_failed`) but the cloud is still marked `cancelled`
  (`CancelHandler.cs:166-174` records the error and finalizes anyway, by design —
  see "leak-don't-wedge" below). Or cancel destroys, then the still-live create
  job marches to `dns_creating` and re-creates.

### Secondary weaknesses (contribute to silent leaks; fix alongside)

- `HasResourcesAsync` (`TerraformRunner.cs:75-89`) runs `state list` with **no
  `init` and no `workspace select`** — it trusts a leftover `.terraform/environment`
  in the workdir. If that file is absent/stale (fresh dir, swept dir, wrong job
  dir), it returns `false` and destroy is silently skipped even though resources
  exist in pg state. A `false` here is exactly what no-ops teardown.
- `CancelHandler` swallows destroy failure (`:166`): a failed
  `terraform destroy` still finalizes as `cancel = succeeded`, `cloud = cancelled`.
  This was an **intentional** "leak rather than wedge UX" decision in the original
  handoff (`plans/portal-cancel-handoff.md`, line ~302). It needs to be revisited:
  a failed destroy should land in `failed_destroy` so the orphan is *visible*,
  with retries, not hidden behind a green "cancelled".
- Creds at job time: the endpoint requires step-up, but the cancel job runs async.
  If the 60-min sliding unlock lapses before the worker claims it,
  `BestEffortDestroyAsync` proceeds with **no provider token**
  (`CancelHandler.cs:127-130`) and `terraform destroy` can't authenticate to DO.
  Treat missing DEK as retryable / `failed_destroy`, not "proceed anyway".

### Why the tests are green while prod leaks

`CancelHandlerTests` exercises `CancelHandler` **in isolation** with a fake
runner — no concurrently running create handler is ever simulated.
`Cancel_with_sibling_create_job_marks_both_terminal` even uses
`QueueHasResources(false)` and asserts success, encoding the exact
skip-destroy path as "correct". The concurrency interleaving is untested.

## Recommended fix

Primary mechanism is **cooperative cancellation routed through the existing
rollback chain**, so the create saga compensates *itself* and Terraform never
runs twice at once. Per-cloud claim serialization is defense-in-depth.

### 1. Cancel signal on the cloud row (primary)

- Add column `Cloud.CancelRequestedAt : Instant?` (migration). Set it in
  `CancelCloudEndpoints` in the same serializable txn that enqueues the cancel
  job (one added line + the migration).

### 2. Forward handlers self-compensate (primary)

At the top of each forward handler, and after each long Terraform step, reload
the cloud and check `CancelRequestedAt`. If set, route into the existing
rollback chain instead of marching forward:

- `pending` / `tf_planning` (no resources yet) → straight to a terminal
  `cancelled`.
- `tf_applying` / `dns_creating` / `awaiting_*` (resources may exist) →
  `TransitionAsync(... RollingBackTf ...)`. `RollingBackTfHandler` already
  destroys with retry against the workspace it owns.

Handlers to touch: `TfPlanningHandler`, `TfApplyingHandler`, `DnsCreatingHandler`,
`AwaitingCloudCallbackHandler`, `AwaitingCertHandler`, `IssuingPluginTokenHandler`,
`MintingSpacesHandler`. A shared helper (e.g. `SagaTransitions.CancelRequested(cloud)`
+ a `RouteToCancelAsync`) keeps it DRY.

### 3. Cancel job defers to the create job (primary)

Rework `CancelHandler.HandleAsync`:

- If a sibling `create` job is **non-terminal**: do NOT touch Terraform. Just
  ensure `CancelRequestedAt` is set and `RescheduleAsync(+5s)` itself. The forward
  handler (step 2) will drive the rollback/destroy. The cancel job only finalizes
  once the create job is terminal.
- If there is **no non-terminal create job** (cloud in `failed_*`, or a bare
  cloud row with live resources): proceed to destroy directly — there's no
  concurrent create here, so it's safe. This is the only path where the cancel
  job itself runs Terraform.
- On finalize: set cloud → `cancelled`, `destroyed_at`, `provisioning_completed_at`
  (overriding whatever terminal the rollback chain landed on).

This eliminates concurrent Terraform without depending on perfect claim timing.

### 4. Per-cloud claim serialization (defense-in-depth)

Even with the above, harden the claim so two sibling jobs can't run at once.
Note a naive `NOT EXISTS (active lease for sibling)` has a TOCTOU gap: two
*unclaimed* sibling jobs can both be claimed in the same instant (neither holds
a lease yet). Close it with a per-cloud advisory lock during claim, e.g. wrap the
claim in `pg_try_advisory_xact_lock(hashtext(cloud_id::text))`, or only ever
claim the earliest non-terminal job per cloud. Keep it simple; this is a backstop.

### 5. Hardening (fix regardless of approach)

- `HasResourcesAsync`: `init` + `workspace select <cloud.Id>` before `state list`
  (or drop the gate and let `destroy` no-op on empty state). Don't trust leftover
  `.terraform/environment`.
- Failed `destroy` (or missing DEK / missing provider token) → `failed_destroy`
  with retry (mirror `RollingBackTfHandler`'s 5-attempt backoff), not silent
  `cancelled`. Surfaces orphans in the UI instead of hiding them.

## Tests to add (the coverage gap)

- **Concurrency:** create handler mid-`tf_applying` + cancel requested →
  after the dust settles, exactly one terminal state, droplet destroyed, no
  forward progress past the cancel point. (Drive via the saga host / two handler
  invocations interleaved, or assert the cooperative-routing transition.)
- Forward-handler cooperative routing: each forward handler, with
  `CancelRequestedAt` set, transitions into rollback (or terminal for pre-apply
  states) instead of proceeding.
- Cancel job defers: sibling create non-terminal → cancel reschedules and does
  NOT call Terraform; once create terminal → cancel finalizes.
- `HasResourcesAsync` returns true after a real apply when only `init` ran in a
  fresh process (workspace-select correctness).
- Destroy failure / missing DEK → `failed_destroy` + retry, not `cancelled`.
- Update the existing `Cancel_with_sibling_create_job_marks_both_terminal` /
  `QueueHasResources(false)` expectations to the new semantics.

## Verification

- `dotnet build` clean (warnings-as-errors).
- Tests (xUnit v3 / Microsoft.Testing.Platform — use `--filter-namespace`, NOT
  VSTest `--filter`):
  ```
  dotnet test --project tests/ThanyMarcus.Portal.Tests/ThanyMarcus.Portal.Tests.csproj \
    -- --filter-namespace "ThanyMarcus.Portal.Tests.SagaWorker"
  ```
- Manual smoke on the dev stack (Cloudflare tunnel `dev.thany.click` →
  `localhost:5050`): provision a DO cloud, cancel during `tf_applying`, confirm
  via DO dashboard that the droplet/firewall/volume/Spaces bucket are gone and the
  cloud shows `cancelled`. Repeat cancelling during `awaiting_cloud_callback`.

## Scope decision to confirm with the user

User asked for suggestions; recommendation given is **steps 1–3 + 5** (cooperative
cancel + hardening), with **4** as a backstop. Alternative minimal scope is
"serialize-only" (just step 4, done robustly) which is correct but makes
cancelling a long apply wait for that apply to finish before destroying. Confirm
scope before implementing. User dislikes scope-lecturing — present as a
design choice, not a capacity caveat.

## Manual cleanup likely needed now

The user has been testing cancel against a real DO account. There are probably
orphaned DO resources from cancelled clouds (droplet `thany-<short>`, firewall
`-fw`, volume `-data`, Spaces bucket `thany-cloud-<short>`). Reconcile the DO
dashboard against `clouds` rows with `provisioning_status='cancelled'` and destroy
any survivors (DO dashboard, or `terraform destroy` against the cloud's pg
workspace from the saga-worker container).

## Key files

| Concern | File |
|---|---|
| Claim query (serialization) | `src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs:123-153` |
| Dispatcher guard (dispatch-time only) | `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/SagaPhaseDispatcher.cs` |
| Cancel job handler | `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/CancelHandler.cs` |
| Forward handlers (need cancel check) | `.../Handlers/{TfPlanning,TfApplying,DnsCreating,AwaitingCloudCallback,AwaitingCert,IssuingPluginToken,MintingSpaces}Handler.cs` |
| Rollback (reused for compensation) | `.../Handlers/RollingBackTfHandler.cs` |
| Transition + concurrency token | `.../Provisioning/SagaTransitions.cs` |
| `HasResourcesAsync` / runner | `src/ThanyMarcus.Portal.SagaWorker/Infrastructure/Terraform/TerraformRunner.cs:75` |
| Cancel endpoint (add signal) | `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Cancel/CancelCloudEndpoints.cs` |
| Cloud model (add CancelRequestedAt) | `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Cloud.cs` |
| Existing tests to update | `tests/ThanyMarcus.Portal.Tests/SagaWorker/Handlers/CancelHandlerTests.cs` |
| Prior (stale) hypothesis | `docs/smoke-2026-05-21-cancel-leak-handoff.md` |
| Original cancel design | `plans/portal-cancel-handoff.md` |
