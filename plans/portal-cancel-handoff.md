# PORTAL-CANCEL — `/api/clouds/{id}/cancel`: abort an in-flight provision

**Goal:** add `POST /api/clouds/{id}/cancel` so a user (or operator) can abort a stuck `create` provisioning saga, unblock `EnqueueGuard.CheckUserCreateInFlightAsync`, and clear the way for a fresh provision attempt. Estimated **0.5 person-day** with AI-agent assistance.

## Why this exists

PORTAL-015 (destroy) was scoped to *successfully provisioned or cert-failed* clouds only:

```csharp
// src/ThanyMarcus.Portal.Api/Features/CloudManagement/Destroy/DestroyCloudEndpoints.cs:15
private static readonly HashSet<string> DestroyableProvisioningStatuses = new(StringComparer.Ordinal)
{
    SagaStatus.Succeeded,
    SagaStatus.AwaitingCert,
    SagaStatus.FailedCert,
};
```

Anything stuck mid-saga (`tf_planning`, `tf_applying`, `dns_creating`, `awaiting_cloud_callback`, `failed_tf`, `failed_dns`, `failed_callback`, `rolling_back_tf`, `rolling_back_dns`) returns **410 Gone**. Combined with `EnqueueGuard.CheckUserCreateInFlightAsync` blocking new `POST /api/clouds` when any non-terminal `create` job exists, **a stuck saga wedges the user out** with no API recovery path. The only recovery today is direct `UPDATE provisioning_jobs ...` against Postgres, which is operator-only and brittle.

Observed in smoke #5 (2026-05-17): the smoke #4 row (`019e36d4`, started 16:45 UTC, hit the Caddy plugin bug from before this amendment) stayed in `tf_planning` for 90 min after the droplet was manually deleted — wizard rejected new provisions with "you already have a cloud being provisioned". F14 rollback bug means even the 15-min `AwaitingCallback` timeout path isn't reliable for self-healing.

This ticket lands the missing API.

## Scope

Cancel = "I want this in-flight create-saga to stop and be marked terminal so I can try again." It is NOT the same as destroy (PORTAL-015), which says "the cloud reached a usable state and I now want it gone." Different verbs, different state-machine entry conditions.

**In scope:**
- New endpoint `POST /api/clouds/{id}/cancel`
- New `SagaKinds.Cancel` saga kind + handler in saga-worker that finalizes cleanup
- Defensive `status check` in existing saga handlers: bail out if cloud row is already terminal before writing a state transition
- Updated `EnqueueGuard` semantics unchanged — once cancel flips the job to `cancelled`, the guard naturally unblocks
- Tests + ADR-0034 / consolidated-plan note

**Out of scope:**
- UI wiring (separate wizard ticket; this just lands the API + saga)
- Recovering DO infrastructure cost (cancel triggers terraform destroy via the existing `rolling_back_tf` path; doesn't introduce a new TF flow)
- F14 rollback retry-backoff fix (separate ticket; orthogonal — cancel works whether or not F14 is fixed because it's a one-shot user-initiated action, not a timeout-retry loop)
- Fixing the `provisioning_jobs.attempts` column drift (the destroy endpoint references it via EF Core but raw SQL queries differ — separate cleanup)

## State machine semantics

```
Eligible cancel sources (non-terminal create-saga statuses):
   pending → cancelled
   tf_planning → cancelled
   tf_applying → rolling_back_tf → cancelled    (if terraform state has resources)
                                              OR cancelled  (if state is empty)
   dns_creating → rolling_back_dns → rolling_back_tf → cancelled
   awaiting_cloud_callback → rolling_back_tf → cancelled
   awaiting_cert → rolling_back_tf → cancelled
   rolling_back_tf → cancelled    (already rolling back; we just mark cancel-requested)
   rolling_back_dns → cancelled
   failed_tf → cancelled    (terminal failure that left orphans; same cleanup path)
   failed_dns → cancelled
   failed_callback → cancelled
   failed_cert → cancelled

Ineligible (returns 409 Conflict with reason):
   succeeded → use /destroy instead
   destroying → already on its way out (Destroy saga)
   cancelled → already cancelled (409 with idempotent message OR 410 Gone)
   rolled_back → already rolled back (410 Gone)
   failed_destroy → operator intervention required
```

**Rule of thumb:** `cancel` is valid for any create-saga that has not reached `succeeded` AND is not already destroying. The endpoint enqueues a `cancel` saga job (Kind = `SagaKinds.Cancel`) which the saga-worker processes by:
1. Marking the original `create` job's status = `cancelled` (terminal, idempotent)
2. If the cloud has terraform state with resources → enqueue a follow-on rollback via the existing `rolling_back_tf` path (which calls `terraform destroy`)
3. Flipping the cloud row to `provisioning_status = 'cancelled'`, `destroyed_at = now()`

The reason for a *saga job* rather than synchronous DB writes in the HTTP handler: the cancel may need to invoke `terraform destroy` (long-running, retryable, lease-bound) which is the saga-worker's job, not the API process's. Sync DB writes leak DO infrastructure if terraform succeeded.

## Concrete files

### `src/ThanyMarcus.Portal.Api/Features/Provisioning/SagaKinds.cs` — add Cancel

```csharp
namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public static class SagaKinds
{
    public const string Create  = "create";
    public const string Destroy = "destroy";
    public const string Cancel  = "cancel";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Create, Destroy, Cancel };
}
```

### `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Cancel/CancelCloudEndpoints.cs` — new

Mirrors the `Destroy` endpoint shape: TOTP + step-up unlock required, serializable txn, hostname-confirm body to guard against accidents.

```csharp
using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Cancel;

public static class CancelCloudEndpoints
{
    // Anything that's NOT terminal-success AND NOT already in a destroy/cancel path.
    private static readonly HashSet<string> CancellableProvisioningStatuses =
        new(StringComparer.Ordinal)
        {
            SagaStatus.Pending,
            SagaStatus.TfPlanning,
            SagaStatus.TfApplying,
            SagaStatus.DnsCreating,
            SagaStatus.AwaitingCloudCallback,
            SagaStatus.AwaitingCert,
            SagaStatus.RollingBackTf,
            SagaStatus.RollingBackDns,
            SagaStatus.FailedTf,
            SagaStatus.FailedDns,
            SagaStatus.FailedCallback,
            SagaStatus.FailedCert,
        };

    public static void MapCancelCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{id:guid}/cancel", async (
            Guid id,
            CancelCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cloud = await db.Clouds
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);

            if (cloud is null)
                return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId)
                return Results.Forbid();
            if (cloud.DestroyedAt is not null)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (cloud.ProvisioningStatus == SagaStatus.Succeeded)
                return Results.Conflict(new { error = "use_destroy_instead" });
            if (cloud.ProvisioningStatus == SagaStatus.Cancelled
             || cloud.ProvisioningStatus == SagaStatus.RolledBack)
                return Results.StatusCode(StatusCodes.Status410Gone);
            if (!CancellableProvisioningStatuses.Contains(cloud.ProvisioningStatus))
                return Results.Conflict(new { error = "not_cancellable", currentStatus = cloud.ProvisioningStatus });
            if (!string.Equals(cloud.Hostname, body.ConfirmHostname, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "hostname_mismatch", expected = cloud.Hostname });

            // Idempotency: if a cancel job already exists for this cloud, return Accepted to its status.
            var existingCancel = await db.ProvisioningJobs
                .Where(j => j.CloudId == cloud.Id
                         && j.Kind == SagaKinds.Cancel
                         && !SagaStatus.Terminal.Contains(j.Status))
                .OrderByDescending(j => j.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (existingCancel is not null)
                return Results.Accepted(
                    $"/api/clouds/{cloud.Id}/status",
                    new { jobId = existingCancel.Id, cloudId = cloud.Id, status = "cancel_in_flight" });

            var now = clock.GetCurrentInstant();
            var job = new ProvisioningJob
            {
                CloudId       = cloud.Id,
                UserId        = userId,
                Kind          = SagaKinds.Cancel,
                Payload       = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
                Status        = SagaStatus.Pending,
                NextVisibleAt = now,
                EventsLog     = JsonDocument.Parse("[]"),
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            db.ProvisioningJobs.Add(job);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Accepted(
                $"/api/clouds/{cloud.Id}/status",
                new { jobId = job.Id, cloudId = cloud.Id });
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }

    public sealed record CancelCloudRequest(string ConfirmHostname);
}
```

**Auth:** same as destroy — TOTP + step-up unlock. Cancel is destructive (kills work in flight, may invoke `terraform destroy`). Same friction level is correct.

**Hostname confirmation:** body must include `confirmHostname` matching `cloud.Hostname`. Lifted directly from destroy; same anti-misclick guard.

**Idempotency:** the txn checks for an existing in-flight cancel job and returns 202 pointing at its status URL. Re-posts don't enqueue duplicates.

### `src/ThanyMarcus.Portal.Api/Program.cs` — wire up

```csharp
app.MapCancelCloudEndpoints();
```

### `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/SagaPhaseDispatcher.cs` — route Cancel kind

Add a branch for `SagaKinds.Cancel` that routes to a new `CancelHandler`. Existing Create and Destroy branches unchanged.

### `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/CancelHandler.cs` — new

```csharp
using System.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed class CancelHandler(
    PortalDbContext db,
    IClock clock,
    WorkspaceLayout workspaces,
    TerraformRunner terraform,
    ILogger<CancelHandler> log)
{
    public async Task HandleAsync(ProvisioningJob cancelJob, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

        // 1) Find sibling create job for this cloud (latest non-terminal create).
        var createJob = await db.ProvisioningJobs
            .Where(j => j.CloudId == cancelJob.CloudId
                     && j.Kind == SagaKinds.Create
                     && !SagaStatus.Terminal.Contains(j.Status))
            .OrderByDescending(j => j.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var cloud = await db.Clouds
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == cancelJob.CloudId, ct);

        var hadTerraformState = await terraform.HasResourcesAsync(workspaces.PathFor(cancelJob.CloudId), ct);

        // 2) Best-effort terraform destroy if state has resources.
        //    Failure here is logged but does NOT block marking cancelled — operator can clean up DO manually.
        if (hadTerraformState)
        {
            try
            {
                await terraform.DestroyAsync(workspaces.PathFor(cancelJob.CloudId), ct);
                log.LogInformation("terraform destroy completed for cancelled cloud {CloudId}", cloud.Id);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "terraform destroy failed for cancelled cloud {CloudId}; orphans may remain", cloud.Id);
                cloud.ProvisioningError = $"cancel: terraform destroy failed: {ex.Message}";
            }
        }

        var now = clock.GetCurrentInstant();

        // 3) Mark sibling create job cancelled (terminal).
        if (createJob is not null)
        {
            createJob.Status    = SagaStatus.Cancelled;
            createJob.UpdatedAt = now;
        }

        // 4) Mark cloud row terminal.
        cloud.ProvisioningStatus      = SagaStatus.Cancelled;
        cloud.DestroyedAt             = now;
        cloud.ProvisioningCompletedAt = now;
        cloud.UpdatedAt               = now;

        // 5) Mark our cancel job succeeded (terminal — different sense; the *cancel itself* succeeded).
        cancelJob.Status    = SagaStatus.Succeeded;
        cancelJob.UpdatedAt = now;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
```

**Terraform destroy idempotency:** `TerraformRunner.DestroyAsync` already handles empty-state (no-op). The `HasResourcesAsync` check is a fast-path skip — if there's no state file or it's empty, don't even invoke terraform.

**Failure mode:** if `terraform destroy` fails (network, DO API down, rate limit), we LOG and CONTINUE — the cancel still completes from the user's perspective. The cloud row carries `provisioning_error` describing the leak so operator can address. **This is intentional:** the alternative (cancel itself can fail) means a user with a stuck saga + flaky DO API has no recovery at all. Better to leak infra than to wedge UX permanently.

### `src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs` — defensive status check

Add an early-out at the top of each Create handler: if the cloud row's `provisioning_status` is terminal (e.g., `cancelled`), abandon the in-flight transition and mark the job terminal too. This handles the race where cancel is requested while a create handler is mid-flight.

```csharp
// In SagaPhaseDispatcher.DispatchAsync before invoking the per-phase handler:
var cloud = await db.Clouds.IgnoreQueryFilters()
    .SingleAsync(c => c.Id == job.CloudId, ct);
if (SagaStatus.IsTerminal(cloud.ProvisioningStatus))
{
    log.LogInformation(
        "abandoning job {JobId} for terminal cloud {CloudId} (status={Status})",
        job.Id, cloud.Id, cloud.ProvisioningStatus);
    job.Status    = cloud.ProvisioningStatus;
    job.UpdatedAt = clock.GetCurrentInstant();
    await db.SaveChangesAsync(ct);
    return;
}
```

**Why this matters:** `SagaWorker.ClaimNextAsync` already filters out terminal *job* status (`WHERE status NOT IN (...)`), but the create-job stays at e.g. `tf_planning` until the next handler runs. If cancel writes `clouds.provisioning_status = 'cancelled'` while a Create job is mid-handler, the handler could write `clouds.provisioning_status = 'tf_applying'` after our cancel, overwriting it. The dispatcher-level check stops that.

### `src/ThanyMarcus.Portal.Api/Features/CloudManagement/EnqueueGuard.cs` — no changes

`CheckUserCreateInFlightAsync` already filters out terminal job statuses. Once `CancelHandler` writes the create job's `status = cancelled`, the guard returns null and `POST /api/clouds` is unblocked. No code change here.

### Tests

`tests/ThanyMarcus.Portal.Tests/CloudManagement/Cancel/CancelCloudEndpointTests.cs` (new):

- 401 when no auth
- 403 when wrong user
- 404 when cloud not found
- 410 when cloud already destroyed
- 409 with `use_destroy_instead` when status = succeeded
- 410 when status = cancelled or rolled_back
- 409 with `not_cancellable` when status = destroying or failed_destroy
- 400 when hostname mismatch
- 202 + new cancel job in db when all gates pass
- 202 idempotent — second POST returns same cancel-job id

`tests/ThanyMarcus.Portal.Tests/CloudManagement/Cancel/CancelHandlerTests.cs` (new, against Postgres fixture):

- Cancel job → create job moves to `cancelled`, cloud row to `cancelled` + destroyed_at set
- Cancel with no sibling create job (orphan cloud row) still succeeds, marks cloud terminal
- Cancel with terraform state present invokes `terraform destroy` (mock TerraformRunner)
- Cancel where `terraform destroy` throws → cancel still completes, cloud has `provisioning_error` set
- Race: cancel job + create handler concurrent → defensive status check prevents create handler from overwriting cancel

## Acceptance criteria

- `dotnet build` clean.
- New endpoint accessible at `POST /api/clouds/{id}/cancel`; OpenAPI lists it; Scalar renders it.
- All new tests green.
- Existing destroy + create test suites unchanged + green (no regressions in EnqueueGuard, DestroyCloudEndpoints, AwaitingCloudCallbackHandler, etc.).
- Manual smoke: provision a cloud → kill the saga-worker mid-tf_planning → confirm wizard shows "in flight" → POST cancel via `curl` → wizard immediately unblocks → POST a fresh /api/clouds succeeds.
- **Cross-ticket validation:** smoke #5 scenario reproducibility — provision, deliberately break (e.g. wrong nginx config baked in), confirm cancel works, re-provision with corrected template, reaches `succeeded`.

## Concrete steps in order

1. **Add `SagaKinds.Cancel` constant** + update `SagaKinds.All` set. Build clean.
2. **Add `CancelCloudEndpoints.cs`** + `CancelCloudRequest` record. Wire `app.MapCancelCloudEndpoints()` in `Program.cs`. Build clean; OpenAPI shows it.
3. **Add `CancelHandler.cs`** + route in `SagaPhaseDispatcher`. Build clean.
4. **Add defensive terminal-status check** in `SagaPhaseDispatcher.DispatchAsync` (or wherever the create-side handlers are invoked). Build clean.
5. **Add `TerraformRunner.HasResourcesAsync`** if not present. Implementation: `terraform state list` (exit 0 + non-empty stdout = has resources).
6. **Write the 10 endpoint + 5 handler tests.** Green.
7. **Smoke locally** — provision, kill mid-saga, POST cancel via the running stack. Confirm DB state + wizard unblock.
8. **Smoke #6 dry-run** — full happy-path re-provision after the post-cancel reset. Reaches `succeeded`.

## Risks & gotchas

- **Terraform state path on disk** — `TerraformRunner.DestroyAsync` needs the same workspace dir the create job rendered. `WorkspaceLayout.PathFor(cloudId)` should return the canonical path. If the saga-worker pod's volume is ephemeral (it was inside a docker container that got recreated), terraform state may be **lost** — destroy then runs against empty state and silently no-ops. **Net effect:** if you cancel a saga that succeeded at `terraform apply` *after* the saga-worker pod recycled, you'll orphan the DO droplet. Mitigation in PORTAL-011a: terraform state is on a bind-mounted host volume (`/var/lib/portal/terraform/`). Verify before shipping cancel; if state is not persistent, cancel becomes "mark terminal + log an orphan" rather than actually cleaning DO.

- **F14 rollback bug is orthogonal.** This ticket does NOT replace the auto-rollback machinery; it adds a user-initiated escape hatch. The F14 bug (retries 6× in 1s, abandons) still exists for *automatic* timeout-triggered rollbacks. PORTAL-CANCEL is one-shot, single-attempt, so it's not subject to the same bug.

- **`destroying` status excluded from cancellable** — once a destroy saga is running, cancelling it is a "cancel a cancel" which is destructive infrastructure. Out of scope; if it gets wedged, operator intervention. (In practice destroying is fast; if it wedges, operator runs `terraform destroy` manually + DB update.)

- **`failed_destroy` excluded from cancellable** — the cloud has real DO infra in a bad state. Operator intervention only; auto-cancel would leak. This row stays wedged until manual fixup. Acceptable for MVP — `failed_destroy` is rare; if it becomes common, file a follow-up "self-heal failed_destroy" ticket.

- **Auth: step-up unlock** — same as destroy. Means user clicked through TOTP+step-up flow recently. Cancel-during-stuck-saga UX requires user to have step-up unlocked, which they probably did during the initial provision (still within the 10-min unlock window typically). If unlock expired, they re-do TOTP. Friction is appropriate for destructive action.

- **Idempotency window** — re-POSTing cancel within the saga's processing window returns 202 with the existing cancel-job-id. If the user POSTs cancel a second time AFTER the cancel job completed (cloud is now `cancelled`/`destroyed_at` set), we return 410 Gone (from the destroyed_at check). Both paths are safe.

- **No payload schema versioning.** `CancelCloudRequest { ConfirmHostname }` is minimal. If we later add a reason code or "force destroy DO even if state empty" flag, the record evolves. Don't bake in `Reason: string` now — YAGNI for MVP.

- **Status SSE / `GET /api/clouds/{id}/status`** already returns the current cloud state — clients polling that endpoint will see `cancelled`/`destroyed_at` flip when cancel completes. No SSE changes needed unless wizard-UI wants a richer "cancel in flight" intermediate state (out of scope here).

- **EnqueueGuard semantics for Kind=Cancel** — `CheckAsync(cloudId)` returns any non-terminal job for that cloud. If a Create job is already non-terminal AND we add a Cancel job alongside it, both are in-flight. That's fine — cancel's handler explicitly looks up sibling create jobs and marks them. But it does mean `CheckAsync` may briefly return a "busy" state while cancel is processing. Document as expected.

- **Concurrent cancels by two different sessions** — second POST sees the first cancel job is non-terminal, returns 202 with the existing job's status URL. Idempotent. Good.

- **What if Kind=Cancel job fails partway?** (e.g., DB connection drop mid-terraform-destroy.) The saga-worker's lease expires, another worker picks it up, replays. `CancelHandler.HandleAsync` is idempotent: marks sibling create job + cloud as terminal (idempotent), runs terraform destroy (idempotent if state already empty). Safe on retry.

## Definition of done

All acceptance criteria pass. The smoke-recovery loop works: provision → break → cancel → re-provision → succeed. No regressions in destroy / create flows.

A fresh agent picking up this state knows:
- `POST /api/clouds/{id}/cancel` exists with TOTP + step-up gating; hostname-confirm body.
- Saga-worker has a `CancelHandler` that does best-effort terraform destroy + flips create job + cloud to terminal.
- Defensive terminal-status check in dispatcher prevents create-handler-after-cancel races.

## Cross-references

- **`src/ThanyMarcus.Portal.Api/Features/CloudManagement/Destroy/DestroyCloudEndpoints.cs`** — sibling endpoint. Cancel mirrors its auth + transaction shape; deliberately not a refactor-to-share-base-class (different state-machine semantics, different saga kind).
- **`src/ThanyMarcus.Portal.Api/Features/CloudManagement/EnqueueGuard.cs`** — what cancel unblocks. No code change here.
- **`src/ThanyMarcus.Portal.Api/Features/Provisioning/SagaStatus.cs`** — `Cancelled` already exists as a terminal status (lines 21, 26-27). Just used here.
- **`src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs`** — claim query already excludes `cancelled`; no change.
- **`plans/cloud-001-amendment-nginx-handoff.md`** — surfaced this gap during smoke #5 cleanup; PORTAL-CANCEL is the proper recovery path so future failed amendments don't require DB hand-editing.
- **F14 rollback bug** — separate ticket (TBD). Cancel is **NOT** a workaround for F14; they solve different problems (user-initiated escape vs automatic rollback robustness). Both should land.
