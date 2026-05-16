# PORTAL-015 Destroy flow — Handoff Brief

**Goal:** land the user-initiated destroy path end-to-end on the API + saga side: the step-up-gated `POST /api/clouds/{id}/destroy` endpoint, a `kind = "destroy"` enqueue path through `provisioning_jobs`, two small additions to the saga state machine (a `destroying` entry status and a `failed_destroy` terminal), `Kind`-aware branches in the existing `RollingBackDnsHandler` / `RollingBackTfHandler` so they route success to `rolled_back` + soft-delete the cloud instead of one of the `failed_*` create-side terminals, and the post-destroy DB cleanup (soft-delete the `clouds` row, revoke `plugin_token_metadata`, drop the terraform `pg` workspace, clear runtime fields). After this ticket, a user who has unlocked their passphrase can hit `POST /api/clouds/{id}/destroy { "confirmHostname": "<host>" }` and watch the saga walk `destroying → rolling_back_dns → rolling_back_tf → rolled_back`, ending with the droplet destroyed in DO/Azure, the Cloudflare A-record deleted, the cloud soft-deleted, and all plugin tokens revoked.

Estimated **0.75 person-day** with heavy AI-agent assistance. The original ticket-list estimate (`tickets-2026-05-13.md` line 53) was 0.5 days under the assumption "DB cleanup + manual DNS A-record cleanup notice". ADR-0033's saga model + PORTAL-010b's real Cloudflare client mean we now automate DNS deletion and route through the saga's existing rollback chain — slightly more wiring (the `Kind` branch + the cleanup transaction), still well under one day.

## Where decisions live (read before doing anything)

- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the canonical state machine. PORTAL-015 adds *two* states (`destroying` entry + `failed_destroy` terminal) and reuses the two existing rollback transients (`rolling_back_dns`, `rolling_back_tf`). The rationale for re-use is captured below in "State-machine extension" — read both before changing anything in `SagaStatus`.
- **`plans/portal-007-handoff.md`** — prerequisite. Defines `SagaStatus`, the dispatcher, the rollback handlers, `EnqueueGuard`, `IInfraOpUnlockCache` usage, and — line 969 — the explicit forward reference: "PORTAL-015 — destroy endpoint. Re-uses the saga: enqueues a `provisioning_jobs` row with `kind = "destroy"` (the existing `Kind` column is currently unused; PORTAL-015 starts using it to differentiate create-vs-destroy in the dispatcher)." This ticket cashes that note.
- **`plans/portal-008-handoff.md`** — prerequisite. The DO terraform module's `destroy` works automatically via terraform-state lifecycle; no module changes here. PORTAL-009 (Azure) follows the same shape; this ticket is provider-agnostic on the saga side.
- **`plans/portal-005-handoff.md`** — `IProviderTokenVault.DecryptAsync(userId, provider, dek, ct)` is called by the destroy saga's `RollingBackTfHandler` to get the provider API token for `terraform destroy`. Same pattern as the create path.
- **`plans/portal-003f-handoff.md`** — `IInfraOpUnlockCache` is Postgres-backed and async; sliding TTL 10 minutes. The endpoint validates the unlock is held (via `RequireInfraOpUnlockFilter`); the saga later re-fetches the DEK per phase, same as the create flow.
- **`docs/decisions/0030-auth-flow.md` §"Step-up sequence"** — the canonical flow. The SPA already implements `fetchWithStepUp` (401 step_up_required → unlock modal → retry). The destroy endpoint inherits that behavior by adding `RequireInfraOpUnlockFilter` like `ProviderTokenEndpoints` does.
- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — `clouds.DestroyedAt` is the soft-delete column; `CloudConfiguration` already has `HasQueryFilter(c => c.DestroyedAt == null)`. The `provisioning_jobs.cloud_id` FK is RESTRICT, so the destroy job's row survives the cloud soft-delete; sweep handles the cleanup.
- **`plans/tickets-2026-05-13.md`** line 53 — the original one-liner:
  > PORTAL-015 | Destroy flow (passphrase prompt → terraform destroy → DB cleanup) | 0.5 | — | Manual DNS A-record cleanup notice |

  The "manual DNS A-record cleanup notice" is **superseded**: PORTAL-010b's Cloudflare client + the saga's existing `rolling_back_dns` handler automate the A-record deletion. The note in `tickets-2026-05-13.md` predates that work.
- **Memory files**: `portal_architecture.md` (Postgres job queue, mutable status, SSE), `portal_deployment.md` (single VM, Docker Compose), `portal_tooling.md` (.NET 10, warnings-as-errors, Shouldly + xUnit v3, OpenAPI + hand-written TS types).

**Do not invent new saga statuses beyond the two named here.** ADR-0033 is the contract. If a defensible shape calls for a third state, raise it as an ADR amendment, do not silently extend `SagaStatus`.

## Scope boundary (precise)

**In scope:**
- `Features/CloudManagement/Destroy/` directory (the existing `.gitkeep` becomes the real feature folder):
  - `DestroyCloudRequest.cs` — request DTO with `ConfirmHostname` (the user must type the cloud's hostname; mismatch ⇒ 400).
  - `DestroyCloudEndpoints.cs` — `POST /api/clouds/{id}/destroy`. Group requires `AuthPolicies.TotpRequired`; endpoint adds `RequireInfraOpUnlockFilter` so step-up is enforced.
  - The endpoint's body is in one SERIALIZABLE transaction (`db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)`): re-load cloud with `IgnoreQueryFilters` (must see already-destroyed clouds to return 410), validate ownership + state, run `EnqueueGuard.CheckAsync`, insert the `provisioning_jobs` row, commit. Return `202 Accepted { jobId, cloudId }`.
- `SagaStatus` additions (in `Portal.Api/Features/Provisioning/SagaStatus.cs`):
  - `Destroying` (= `"destroying"`) — the entry state for `kind = "destroy"` rows. The dispatcher routes it to a new `DestroyEntryHandler` whose only job is to flip the row to `rolling_back_dns` (or skip straight to `rolling_back_tf` if the cloud never finished `dns_creating` — see "Idempotent destroy across cloud lifecycle" below). Justifies its existence rather than starting directly in `rolling_back_dns` because it makes the destroy entry explicit in logs/events_log and avoids a sweep-time ambiguity where the saga can't tell "rollback after failed create" from "user-initiated destroy".
  - `FailedDestroy` (= `"failed_destroy"`) — added to the `Terminal` set. Reached when the destroy abandons after 5 attempts (mirrors the `failed_tf` abandonment terminal but is `kind`-specific so dashboards can show "destroy failed; manual cleanup required" vs. "create failed").
- `SagaKinds` constants in `Portal.Api/Features/Provisioning/SagaKinds.cs`: `Create = "create"`, `Destroy = "destroy"`. PORTAL-011's create-endpoint should write `SagaKinds.Create`; PORTAL-007's tests currently insert rows with `Kind = "create"` hard-coded — change those call sites to the constant in passing. **This is the smallest possible refactor; do not expand it.**
- `Portal.SagaWorker/Features/Provisioning/Handlers/DestroyEntryHandler.cs` — new. ~30 LOC:
  - Reads `cloud` (with `IgnoreQueryFilters` so it sees a soft-deleted-then-revived row, though this should not happen).
  - If `cloud.ProvisioningStatus` indicates DNS was ever created (`cloud.Subdomain` is non-null AND there's a `dns.record_id` entry in any prior job's `events_log`) → transition to `rolling_back_dns`.
  - Else → transition to `rolling_back_tf` (no DNS to delete).
  - Sets `RollbackReason = "user_destroy"` in the events_log entry so the rollback handlers know to terminate in `rolled_back` (success) or `failed_destroy` (abandoned) rather than `failed_dns` / `failed_tf`.
- `RollingBackDnsHandler` change (in `Portal.SagaWorker/Features/Provisioning/Handlers/RollingBackDnsHandler.cs`):
  - Branch on `job.Kind`. When `Kind == "destroy"`:
    - Same DNS-delete work (read record-id from events_log, call `ICloudflareDnsClient.DeleteAsync`).
    - On success: transition to `rolling_back_tf` (unchanged).
    - On failure: same logic as today — log + proceed to `rolling_back_tf` anyway (DNS-record orphan is non-fatal; user can clean up via Cloudflare UI). Append a structured warning to `events_log` flagged `kind: "destroy"` so the dashboard surfaces "DNS record may need manual cleanup".
  - When `Kind == "create"`: behavior unchanged. Existing tests must remain green.
- `RollingBackTfHandler` change (in `Portal.SagaWorker/Features/Provisioning/Handlers/RollingBackTfHandler.cs`):
  - Branch on `job.Kind`. When `Kind == "destroy"`:
    - Run `terraform destroy -auto-approve` exactly as today (idempotent against partial state; the runner already exposes `DestroyAsync` per `ITerraformRunner.cs:10`).
    - On success: in one transaction, set `job.Status = rolled_back`, set `cloud.DestroyedAt = clock.GetCurrentInstant()`, set `cloud.ProvisioningStatus = rolled_back`, null out `cloud.VmIp` + `cloud.CloudAdminTokenHash` + `cloud.TerraformWorkspace`, revoke all `plugin_token_metadata` rows for this `CloudId` (set `RevokedAt = now` where `RevokedAt IS NULL`), append a structured `events_log` entry `{phase:"destroy_cleanup", revoked_token_count: N}`. **Do not delete the `clouds` row hard** — soft-delete preserves audit history and the `provisioning_jobs` rows that FK-RESTRICT-reference it.
    - On terraform-destroy failure: same backoff as today (`next_visible_at = now + 1 min`, up to 5 attempts), then transition to `failed_destroy` (NOT `failed_tf`) with a structured `events_log` warning. The cloud is **not** soft-deleted in this path — it's still up in DO/Azure, just marked as "destroy_failed" so the user knows to retry or manual-clean.
  - When `Kind == "create"`: behavior unchanged. Existing rollback-on-create-failure tests stay green.
- Terraform `pg` workspace deletion: after a successful `terraform destroy` for `kind = "destroy"`, call `ITerraformRunner.DeleteWorkspaceAsync(cloudId, ct)` (a new method — see "ITerraformRunner extension" below). The `pg` backend keeps per-workspace state forever otherwise; for destroyed clouds we want the workspace gone to keep the state schema small and to prevent accidental re-apply if someone re-creates a cloud with the same id. Failure to delete the workspace is logged but does NOT fail the saga — the cloud is already destroyed; the workspace row is housekeeping.
- `Portal.SagaWorker/Infrastructure/Terraform/ITerraformRunner.cs` — add one method:
  ```csharp
  Task<TerraformResult> DeleteWorkspaceAsync(string workspaceName, CancellationToken ct);
  ```
  Implementation in `TerraformRunner` shells out to `terraform workspace select default && terraform workspace delete <name>` against the saga's plugin-cache + pg backend dir. **Tested only at the contract level** (the test fake records the call); real-impl integration is exercised by the end-to-end destroy smoke (see "Acceptance criteria").
- `EnqueueGuard` consumer: the destroy endpoint calls `EnqueueGuard.CheckAsync(cloudId, ct)`. If a non-terminal job exists, return `409 Conflict { reason: "cloud_busy", in_flight_job_id, current_phase }`. The guard is a *check, not a lock* — the SERIALIZABLE-transaction wrapper around the insert is what makes the invariant authoritative; the helper's xmldoc already says this.
- Migration `20260516XXXXXX_DestroySagaStatuses.cs`:
  - Updates the partial-index filter on `ix_provisioning_jobs_active_next_visible_at` to include `'failed_destroy'` in the terminal-status list (otherwise abandoned destroy rows stay forever in the active index).
  - Updates the matching `TerminalStatusFilter` const in `ProvisioningJobConfiguration.cs:9` — both need to change together; CI will fail with index-mismatch if you forget.
  - No schema-shape changes (the new statuses are string values; no new columns).
- TypeScript contract regen: `pnpm typegen` in `src/ThanyMarcus.Portal.Web/` after the endpoint lands. The hand-written-TS-types convention (per [[portal_tooling]]) applies — verify the generated request/response types compile against the existing SPA's `fetchWithStepUp`.
- Tests in `tests/ThanyMarcus.Portal.Tests/`:
  - `Features/CloudManagement/Destroy/DestroyCloudEndpointTests.cs` — `WebApplicationFactory`-backed integration:
    - 401 when not signed in.
    - 401 `step_up_required` when signed in + TOTP-verified but DEK not in unlock cache.
    - 400 `hostname_mismatch` when `confirmHostname` doesn't match `cloud.Hostname`.
    - 403 when the cloud belongs to a different user (ownership check).
    - 404 when the cloud id doesn't exist.
    - 410 `cloud_already_destroyed` when `cloud.DestroyedAt IS NOT NULL`.
    - 409 `cloud_busy` when a non-terminal `provisioning_jobs` row already exists for the cloud.
    - 202 happy path: assert the row is inserted with `Kind = "destroy"`, `Status = "destroying"`, `Payload` contains `reason = "user_initiated"`, `next_visible_at <= now`.
  - `SagaWorker/Handlers/DestroyEntryHandlerTests.cs` — unit:
    - Cloud has `Subdomain` set AND prior events_log has a `dns.record_id` → transitions to `rolling_back_dns`.
    - Cloud has no `Subdomain` (destroy requested before DNS phase ever ran — unusual but defensible: user destroys a cloud that failed at `tf_planning`) → transitions to `rolling_back_tf`.
    - The events_log entry written carries `rollback_reason = "user_destroy"`.
  - `SagaWorker/Handlers/RollingBackDnsHandlerDestroyTests.cs` — unit (extend the existing `RollingBackDnsHandlerTests`):
    - `Kind = "destroy"` + successful DNS delete → transitions to `rolling_back_tf`. Same as the create path.
    - `Kind = "destroy"` + DNS-delete failure → still transitions to `rolling_back_tf`, events_log entry has `dns_orphan = true`.
    - `Kind = "create"` tests stay green (regression guard).
  - `SagaWorker/Handlers/RollingBackTfHandlerDestroyTests.cs` — unit:
    - `Kind = "destroy"` + successful destroy → `status = rolled_back`, `cloud.DestroyedAt` set, `cloud.VmIp` nulled, `plugin_token_metadata` rows have `RevokedAt` set.
    - `Kind = "destroy"` + repeated failures → after 5 attempts, `status = failed_destroy`, cloud is NOT soft-deleted, events_log has `{phase:"rolling_back_tf", abandoned:true, kind:"destroy"}`.
    - Workspace deletion: `ITerraformRunner.DeleteWorkspaceAsync` called exactly once on success; not called on failure-abandonment.
  - `SagaWorker/Handlers/DestroyEndToEndTests.cs` — Testcontainers Postgres integration:
    - Seed: a `succeeded` cloud with `Subdomain`, `VmIp`, two `plugin_token_metadata` rows with `RevokedAt = NULL`, and a prior `succeeded` create job whose events_log contains the DNS record id.
    - Insert a `pending` destroy row via the endpoint (hit it via `HttpClient`).
    - Run the worker for ~10s with `FakeTerraformRunner` + `FakeCloudflareDnsClient`.
    - Assert: row reaches `rolled_back`; cloud `DestroyedAt` is set; both plugin tokens have `RevokedAt` set; `FakeTerraformRunner.DestroyCalls == 1`; `FakeCloudflareDnsClient.DeleteCalls == 1`; `FakeTerraformRunner.DeleteWorkspaceCalls == 1`.
  - `Features/Provisioning/SagaStatusTests.cs` — extend the existing test (if one exists; otherwise add) asserting `Terminal` contains `FailedDestroy` and excludes `Destroying`.
- `appsettings.json` additions: none. The destroy abandonment threshold (5 attempts) is hard-coded in `RollingBackTfHandler` per PORTAL-007's existing pattern; if you want it configurable, do it in a follow-up and apply uniformly to create + destroy abandonment.

**Out of scope (DO NOT touch):**
- **Destroy button in the SPA dashboard** — PORTAL-012 (cloud-list dashboard UI, blocked by 013/014/015 per `tickets-2026-05-13.md` line 71). The endpoint surface this ticket lands is what PORTAL-012 will call. **Don't add a one-off destroy button to an existing page** — the dashboard is its own ticket and the SPA's destroy modal belongs there.
- **`pg_advisory_lock` for serializing concurrent destroys** — the `EnqueueGuard` + SERIALIZABLE transaction is sufficient at thesis scale. If a future incident shows a race, raise an ADR.
- **Force-destroy / cancel-mid-saga** — out of scope. If the saga is mid-`rolling_back_tf` and the user clicks destroy again, they get a 409. Manually cancelling a stuck rollback is a console-tool problem, not a UI problem.
- **Hard delete** of `clouds` rows — soft-delete is the contract per ADR-0032. A separate "purge after N days" sweeper is a future-work item; not in MVP.
- **Provider token cleanup** — the `encrypted_provider_tokens` row belongs to the user, not the cloud (`(user_id, provider)` unique). Other clouds may share the same provider token. **Don't touch the token vault from the destroy path.**
- **Re-creating a destroyed cloud with the same hostname** — `clouds.Hostname` is unique; the query filter hides destroyed rows but the unique index doesn't. Hostname conflicts at re-create are a PORTAL-011 concern; this ticket leaves the soft-deleted row's hostname column populated. (A `_destroyed_<ts>` suffix at soft-delete time is a defensible future enhancement; deliberately deferred to keep the audit log readable.)
- **Plugin-side cloud-discovery** — when a cloud is destroyed, plugins pointing at it should fail their next bearer-token auth (the tokens are revoked) but the plugin itself doesn't know. Plugin UX for "cloud gone" is out of scope.
- **Resource sweeper** — if `terraform destroy` succeeds but the API never gets the success transition (worker crash mid-cleanup), the droplet is gone but the cloud row is not soft-deleted. Crash-recovery already re-claims `rolling_back_tf` rows; the second attempt will see `terraform destroy` succeed idempotently and complete the cleanup. No new sweeper needed.

## Output of PORTAL-015 — final directory state

```
Thany-Marcus/
├── src/ThanyMarcus.Portal.Api/
│   ├── Features/
│   │   ├── CloudManagement/Destroy/
│   │   │   ├── DestroyCloudRequest.cs                            # NEW
│   │   │   └── DestroyCloudEndpoints.cs                          # NEW
│   │   ├── Provisioning/
│   │   │   ├── SagaStatus.cs                                     # CHANGED: add Destroying + FailedDestroy; expand Terminal set
│   │   │   ├── SagaKinds.cs                                      # NEW: Create / Destroy constants
│   │   │   └── ProvisioningJobConfiguration.cs                   # CHANGED: TerminalStatusFilter includes failed_destroy
│   │   └── Destroy/.gitkeep                                      # DELETED (folder replaced by CloudManagement/Destroy)
│   ├── Infrastructure/Database/Migrations/
│   │   └── 20260516XXXXXX_DestroySagaStatuses.cs                 # NEW: updates partial index filter
│   └── Program.cs                                                # CHANGED: app.MapDestroyCloudEndpoints();
├── src/ThanyMarcus.Portal.SagaWorker/
│   ├── Features/Provisioning/Handlers/
│   │   ├── DestroyEntryHandler.cs                                # NEW
│   │   ├── RollingBackDnsHandler.cs                              # CHANGED: branch on Kind
│   │   └── RollingBackTfHandler.cs                               # CHANGED: branch on Kind; cleanup on destroy success
│   └── Infrastructure/Terraform/
│       ├── ITerraformRunner.cs                                   # CHANGED: + DeleteWorkspaceAsync
│       └── TerraformRunner.cs                                    # CHANGED: implement DeleteWorkspaceAsync
└── tests/ThanyMarcus.Portal.Tests/
    ├── Features/CloudManagement/Destroy/
    │   └── DestroyCloudEndpointTests.cs                          # NEW
    └── SagaWorker/Handlers/
        ├── DestroyEntryHandlerTests.cs                           # NEW
        ├── RollingBackDnsHandlerDestroyTests.cs                  # NEW (or extend RollingBackDnsHandlerTests)
        ├── RollingBackTfHandlerDestroyTests.cs                   # NEW
        └── DestroyEndToEndTests.cs                               # NEW
```

The pre-existing `src/ThanyMarcus.Portal.Api/Features/Destroy/.gitkeep` placeholder is **deleted**. The destroy feature lives under `Features/CloudManagement/Destroy/` to keep cloud-lifecycle concerns colocated — same VSA convention as `Features/CloudManagement/ProviderTokens/`.

## Packages

No new NuGet packages. Everything reuses existing infrastructure (EF Core, Npgsql, NodaTime, xUnit v3, Shouldly).

## State-machine extension

ADR-0033's 13-state machine grows by 2:

```text
SagaStatus additions
─────────────────────────────────────────────────────────────
  destroying            -- entry; only valid for Kind == "destroy"
  failed_destroy        -- terminal; destroy abandoned after 5 TF-destroy attempts
```

`destroying` is **not** in the `Terminal` set (it's an entry transient). `failed_destroy` **is** in the `Terminal` set.

```
                    user POSTs /api/clouds/{id}/destroy
                    (handler writes provisioning_jobs row
                     kind=destroy status=destroying in
                     SERIALIZABLE tx with EnqueueGuard check)
                              │
                              ▼
                        ┌──────────────┐
                        │  destroying  │
                        └──────┬───────┘
                               │ DestroyEntryHandler:
                               │   if cloud has DNS record → rolling_back_dns
                               │   else                    → rolling_back_tf
                               │
                       ┌───────┴──────────────┐
                       ▼                      ▼
              ┌──────────────────┐   ┌─────────────────┐
              │ rolling_back_dns │   │ rolling_back_tf │
              └────────┬─────────┘   └────────┬────────┘
                       │                      │
                       ▼                      │
              ┌─────────────────┐             │
              │ rolling_back_tf │             │
              └────────┬────────┘             │
                       │                      │
                       ▼                      ▼
                   destroy ok                destroy abandoned
                       │                      (5 attempts)
                       ▼                      ▼
                  ┌───────────┐          ┌─────────────────┐
                  │ rolled_back│          │ failed_destroy │
                  │ + cloud   │          │ (cloud still   │
                  │ soft-del  │          │  alive in DO)  │
                  │ + tokens  │          └─────────────────┘
                  │ revoked   │
                  └───────────┘
```

**Why a separate `destroying` entry rather than starting in `rolling_back_dns` directly:** the entry handler picks the next status based on the cloud's lifecycle state, which the SQL-only enqueue path can't compute in the same transaction without a round-trip. Pushing the decision into the saga (a single phase row with `Status = "destroying"`) keeps the endpoint dumb. It also makes log searches (`status = "destroying"`) cleanly distinguish "user-initiated destroy in flight" from "create-failed rollback in flight".

**Why a separate `failed_destroy` terminal rather than reusing `failed_tf`:** they have different operational meanings. A `failed_tf` cloud was never up; the user can retry create. A `failed_destroy` cloud is up *and the user wanted it gone* — operator action is "ssh in via the provider UI and manually destroy, then mark the row terminal" (or "retry destroy with a working provider token"). Dashboards and operator runbooks branch on this; conflation costs UX.

## The `POST /api/clouds/{id}/destroy` endpoint

`src/ThanyMarcus.Portal.Api/Features/CloudManagement/Destroy/DestroyCloudEndpoints.cs`:

```csharp
using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Destroy;

public static class DestroyCloudEndpoints
{
    public static void MapDestroyCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds/{id:guid}/destroy", async (
            Guid id,
            DestroyCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            EnqueueGuard guard,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var cloud = await db.Set<Cloud>()
                .IgnoreQueryFilters()
                .SingleOrDefaultAsync(c => c.Id == id, ct);

            if (cloud is null)                   return Results.NotFound(new { error = "cloud_not_found" });
            if (cloud.UserId != userId)          return Results.Forbid();
            if (cloud.DestroyedAt is not null)   return Results.StatusCode(StatusCodes.Status410Gone);
            if (!string.Equals(cloud.Hostname, body.ConfirmHostname, StringComparison.Ordinal))
                return Results.BadRequest(new { error = "hostname_mismatch", expected = cloud.Hostname });

            var conflict = await guard.CheckAsync(cloud.Id, ct);
            if (conflict is { } c)
                return Results.Conflict(new { reason = "cloud_busy", in_flight_job_id = c.JobId, current_phase = c.Status });

            var now = clock.GetCurrentInstant();
            var job = new ProvisioningJob
            {
                CloudId       = cloud.Id,
                Kind          = SagaKinds.Destroy,
                Payload       = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
                Status        = SagaStatus.Destroying,
                NextVisibleAt = now,
                EventsLog     = JsonDocument.Parse("[]"),
            };
            db.Set<ProvisioningJob>().Add(job);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            return Results.Accepted($"/api/clouds/{cloud.Id}/status", new { jobId = job.Id, cloudId = cloud.Id });
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
```

`DestroyCloudRequest.cs`:

```csharp
namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Destroy;

public sealed record DestroyCloudRequest(string ConfirmHostname);
```

**Notes:**
- **`IgnoreQueryFilters` for the cloud load** is mandatory — the default filter hides `DestroyedAt != null` rows, and we want a soft-deleted cloud to return 410, not 404. Tests cover the distinction.
- **Hostname-confirm** is the only client-supplied gate; the unlock filter handles step-up. The hostname-confirm is UX, not security — a passphrase-unlocked attacker doesn't have to type the hostname, but the SPA modal will require it. **Treat the check as input validation, not auth.**
- **SERIALIZABLE isolation** matches PORTAL-011's eventual create endpoint and is the only level Postgres needs to actually serialize the `EnqueueGuard.CheckAsync` → insert sequence (`READ COMMITTED` lets two requests both see "no conflict" before either commits). The retry budget of `serialization_failure` errors is implicit — `EF Core` surfaces them as `PostgresException` SQLSTATE 40001; let them propagate to a 503 (the SPA's `fetchWithStepUp` retries on 503 with backoff per PORTAL-003 conventions). **Do not catch and retry inside the endpoint** — that hides operator-visible signal.
- **`RequireInfraOpUnlockFilter` placement** — `AddEndpointFilter` is per-endpoint, not per-group, so a future read-only GET on `/api/clouds/{id}` doesn't accidentally inherit the step-up requirement. Mirrors `ProviderTokenEndpoints` line 66 / 81.
- **No SSE here.** PORTAL-011 / ADR-0036 ship the wizard-progress SSE channel; the destroy SPA modal (PORTAL-012) reuses the same `/api/clouds/{id}/events` stream — out of scope for this ticket.
- **Why `POST .../destroy` not `DELETE /api/clouds/{id}`** — the request has a body (`confirmHostname`), and the action enqueues a job rather than deleting a resource synchronously. `POST .../destroy` matches the eventual `POST .../resize` etc. (post-thesis future-work) better than `DELETE`. RESTafarian objection acknowledged and overridden.

`Program.cs` line ~207 — add the registration alongside the others:

```csharp
app.MapDestroyCloudEndpoints();
```

## The `DestroyEntryHandler`

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/DestroyEntryHandler.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning.Handlers;

public sealed class DestroyEntryHandler(PortalDbContext db, EventsLogAppender events, IClock clock) : ISagaPhaseHandler
{
    public string Phase => SagaStatus.Destroying;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
    {
        var cloud = await db.Set<Cloud>()
            .IgnoreQueryFilters()
            .SingleAsync(c => c.Id == job.CloudId, ct);

        // DNS exists iff cloud.Subdomain is non-null AND some prior job's events_log recorded a record_id.
        var hasDns = cloud.Subdomain is not null && await HasDnsRecordAsync(db, cloud.Id, ct);

        job.Status = hasDns ? SagaStatus.RollingBackDns : SagaStatus.RollingBackTf;
        job.PhaseStartedAt = clock.GetCurrentInstant();
        job.NextVisibleAt  = clock.GetCurrentInstant();
        events.Append(job, new { phase = "destroy_entry", next = job.Status, rollback_reason = "user_destroy" });

        await db.SaveChangesAsync(ct);
    }

    private static async Task<bool> HasDnsRecordAsync(PortalDbContext db, Guid cloudId, CancellationToken ct)
    {
        // Cheap existence probe; events_log is jsonb, prior create job has a structured entry.
        // Using FromSqlInterpolated for the jsonb path query — keep this scoped to the handler.
        var hits = await db.Database.SqlQueryRaw<int>(
            "SELECT 1 FROM provisioning_jobs WHERE cloud_id = {0} AND events_log @> '[{\"phase\":\"dns_created\"}]'::jsonb LIMIT 1",
            cloudId).ToListAsync(ct);
        return hits.Count > 0;
    }
}
```

**Register** the handler in `Portal.SagaWorker/Program.cs` alongside the create-side handlers from PORTAL-007:

```csharp
builder.Services.AddScoped<ISagaPhaseHandler, DestroyEntryHandler>();
```

## `RollingBackDnsHandler` — Kind branch

The existing handler from PORTAL-007 does DNS delete + `→ rolling_back_tf` regardless of context. Branch:

```csharp
public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
{
    var recordId = ReadRecordIdFromEventsLog(job.EventsLog);
    if (recordId is not null)
    {
        try
        {
            var cfToken = await cfTokenLoader.LoadAsync(job.CloudId, ct);
            await cloudflare.DeleteAsync(recordId, cfToken, ct);
            events.Append(job, new { phase = "rolling_back_dns", deleted = recordId });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Per ADR-0033: orphan-A records are non-fatal. Proceed.
            events.Append(job, new {
                phase = "rolling_back_dns",
                error = ex.GetType().Name,
                dns_orphan = true,
                kind = job.Kind,
            });
        }
    }

    job.Status         = SagaStatus.RollingBackTf;
    job.PhaseStartedAt = clock.GetCurrentInstant();
    job.NextVisibleAt  = clock.GetCurrentInstant();
    await db.SaveChangesAsync(ct);
}
```

**The only PORTAL-015-specific change** is the `kind = job.Kind` field in the events_log entry on failure, so the dashboard can surface "manual DNS cleanup may be needed" with the right severity. Behavior is identical between `create` and `destroy` rollback paths for DNS; the divergence is downstream in `RollingBackTfHandler`.

## `RollingBackTfHandler` — Kind branch + cleanup transaction

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/RollingBackTfHandler.cs` — the destroy branch is the meaty change:

```csharp
public async Task HandleAsync(ProvisioningJob job, CancellationToken ct)
{
    var workspaceDir = await EnsureWorkspaceRenderedAsync(job, ct);
    var envVars      = await BuildEnvVarsAsync(job, ct);   // DEK-decrypted provider_token, etc.

    var result = await terraform.DestroyAsync(workspaceDir, envVars, ct);
    if (!result.IsSuccess)
    {
        await HandleDestroyFailureAsync(job, result, ct);
        return;
    }

    if (job.Kind == SagaKinds.Destroy)
    {
        await CompleteUserDestroyAsync(job, ct);
    }
    else
    {
        // Create-side rollback (unchanged): terminal is failed_tf / failed_dns / failed_callback / cancelled
        // based on RollbackReason stored in events_log.
        await CompleteCreateRollbackAsync(job, ct);
    }
}

private async Task CompleteUserDestroyAsync(ProvisioningJob job, CancellationToken ct)
{
    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

    var cloud = await db.Set<Cloud>().IgnoreQueryFilters().SingleAsync(c => c.Id == job.CloudId, ct);
    var now   = clock.GetCurrentInstant();

    cloud.DestroyedAt           = now;
    cloud.ProvisioningStatus    = SagaStatus.RolledBack;
    cloud.VmIp                  = null;
    cloud.CloudAdminTokenHash   = null;
    cloud.TerraformWorkspace    = null;

    var revoked = await db.Set<PluginTokenMetadata>()
        .Where(p => p.CloudId == cloud.Id && p.RevokedAt == null)
        .ExecuteUpdateAsync(s => s.SetProperty(p => p.RevokedAt, now), ct);

    job.Status         = SagaStatus.RolledBack;
    job.PhaseStartedAt = now;
    events.Append(job, new {
        phase = "destroy_cleanup",
        revoked_token_count = revoked,
        cloud_id = cloud.Id,
    });

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);

    // Best-effort terraform workspace deletion (outside the tx — it's idempotent housekeeping).
    try
    {
        await terraform.DeleteWorkspaceAsync(cloud.Id.ToString(), ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        LogWorkspaceDeleteFailed(log, ex, cloud.Id);
    }
}

private async Task HandleDestroyFailureAsync(ProvisioningJob job, TerraformResult result, CancellationToken ct)
{
    if (job.AttemptCount > 5)
    {
        job.Status = job.Kind == SagaKinds.Destroy ? SagaStatus.FailedDestroy : SagaStatus.FailedTf;
        events.Append(job, new {
            phase = "rolling_back_tf",
            abandoned = true,
            kind = job.Kind,
            last_error = result.Stderr.Truncate(2000),
        });
    }
    else
    {
        job.NextVisibleAt = clock.GetCurrentInstant() + Duration.FromMinutes(1);
        events.Append(job, new {
            phase = "rolling_back_tf",
            retry = job.AttemptCount,
            error = result.ExitCode,
        });
    }
    await db.SaveChangesAsync(ct);
}
```

**Notes:**
- **`ExecuteUpdateAsync` for plugin-token revocation** — single SQL UPDATE, no per-row materialization. The number of revoked rows is captured for the audit log.
- **`IgnoreQueryFilters` on the cloud load** — the cloud is about to be soft-deleted; subsequent claims of the same row must see the unfiltered entity. The PORTAL-007 handler probably uses the filter; **swap to `IgnoreQueryFilters` here** so the destroy path can re-load a soft-deleted cloud on retry without the filter hiding it.
- **The workspace delete is outside the cleanup transaction** by design — terraform-side housekeeping shouldn't gate the DB transition, and a transient `terraform workspace delete` failure shouldn't roll back the soft-delete. The next sweep run could pick it up; not a correctness issue.
- **`AttemptCount > 5` for abandonment** matches PORTAL-007's `RollingBackTfHandler` create-side abandonment behavior. The threshold is intentionally identical so the operator runbook has one number to remember.

## `ITerraformRunner.DeleteWorkspaceAsync`

`src/ThanyMarcus.Portal.SagaWorker/Infrastructure/Terraform/ITerraformRunner.cs`:

```csharp
public interface ITerraformRunner
{
    Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct);
    Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct);
    Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct);

    /// <summary>
    /// Deletes the `pg` backend workspace for the given name. Safe to call on a non-existent workspace
    /// (logs and returns success). Run *after* a successful `terraform destroy` to keep the state schema small.
    /// </summary>
    Task<TerraformResult> DeleteWorkspaceAsync(string workspaceName, CancellationToken ct);
}
```

The real impl in `TerraformRunner` shells `terraform workspace select default` followed by `terraform workspace delete <name>` against a workdir that's pre-initialized against the `pg` backend (`terraform init -backend-config="conn_str=$..."`). Reusing the same workdir as the destroy run is fine — the workspace switch happens before the delete. Capture stdout/stderr per existing pattern.

The fake (in tests) records the call list; assertions check `DeleteWorkspaceCalls.Count`.

## `SagaKinds` + `SagaStatus` additions

`src/ThanyMarcus.Portal.Api/Features/Provisioning/SagaKinds.cs`:

```csharp
namespace ThanyMarcus.Portal.Api.Features.Provisioning;

public static class SagaKinds
{
    public const string Create  = "create";
    public const string Destroy = "destroy";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Create, Destroy };
}
```

`src/ThanyMarcus.Portal.Api/Features/Provisioning/SagaStatus.cs` — extend:

```csharp
public const string Destroying     = "destroying";
public const string FailedDestroy  = "failed_destroy";

public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
{
    Succeeded, FailedTf, FailedDns, FailedCallback, FailedCert, FailedDestroy, Cancelled, RolledBack,
};
```

`src/ThanyMarcus.Portal.Api/Features/Provisioning/ProvisioningJobConfiguration.cs` — the `TerminalStatusFilter` const must mirror the new terminal set exactly. If they drift, the partial index gives correct rows but the saga's `IsTerminal` lies; CI will catch the mismatch via a new test (`SagaStatusTerminalFilterTests`).

## Migration: `20260516XXXXXX_DestroySagaStatuses`

Single migration; updates the partial index filter:

```csharp
protected override void Up(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropIndex(
        name: "ix_provisioning_jobs_active_next_visible_at",
        table: "provisioning_jobs");

    migrationBuilder.CreateIndex(
        name: "ix_provisioning_jobs_active_next_visible_at",
        table: "provisioning_jobs",
        column: "next_visible_at",
        filter: "status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy','cancelled','rolled_back')");
}

protected override void Down(MigrationBuilder migrationBuilder)
{
    migrationBuilder.DropIndex(
        name: "ix_provisioning_jobs_active_next_visible_at",
        table: "provisioning_jobs");

    migrationBuilder.CreateIndex(
        name: "ix_provisioning_jobs_active_next_visible_at",
        table: "provisioning_jobs",
        column: "next_visible_at",
        filter: "status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')");
}
```

No schema-shape changes — the new statuses are string values; Npgsql's `text` column accepts them without alteration. Snapshot file regenerates via `dotnet ef migrations add`.

## Idempotent destroy across cloud lifecycle

A user can request destroy at any point in a cloud's life. The entry handler routes based on what was created:

| Cloud state when destroy requested | DestroyEntryHandler transitions to | Reason |
|---|---|---|
| `succeeded` (cloud fully up) | `rolling_back_dns` | DNS exists, droplet exists, callback completed |
| `awaiting_cert` (cert never landed) | `rolling_back_dns` | DNS exists, droplet exists |
| `failed_cert` (terminal; cloud functional but no cert) | `rolling_back_dns` | DNS + droplet still alive |
| `failed_callback` (terminal; rollback already ran) | endpoint rejects with `410` after the rollback completed | already soft-deleted-equivalent |
| `failed_dns` / `failed_tf` (rolled-back terminals) | endpoint should reject with `410` or no-op | nothing to destroy |
| `cancelled` (rolled-back terminal) | endpoint rejects with `410` | nothing to destroy |

**The endpoint enforces this** via the `DestroyedAt is not null → 410` check plus a new `ProvisioningStatus` check: if `ProvisioningStatus` is in `{FailedTf, FailedDns, FailedCallback, Cancelled, RolledBack}`, return 410 (the cloud has nothing in DO/Azure to destroy). The entry handler then doesn't need to handle these — it only sees `destroying` rows where the cloud is *up*.

**Exception:** `failed_cert` clouds *are* up — the saga's diagram explicitly says `failed_cert` does NOT roll back. Destroy from this state goes through `rolling_back_dns → rolling_back_tf → rolled_back` normally.

The endpoint logic for state checks:

```csharp
var destroyable = cloud.ProvisioningStatus switch
{
    SagaStatus.Succeeded    => true,
    SagaStatus.AwaitingCert => true,
    SagaStatus.FailedCert   => true,
    _                        => false,
};
if (!destroyable) return Results.StatusCode(StatusCodes.Status410Gone);
```

Test coverage: one parameterized test enumerates all 13 statuses and asserts the right outcome (202 for the three destroyables, 410 for the rest, 409 for active rows blocked by `EnqueueGuard`).

## Tests

### `DestroyCloudEndpointTests.cs`

`WebApplicationFactory`-backed with the standard test-auth scheme. Reuses the existing test fixtures (Testcontainers Postgres + ProviderTokenVault seed helpers per PORTAL-005/007's test infra).

```csharp
[Fact]
public async Task DestroyHappyPath_Returns202AndEnqueuesDestroyingRow()
{
    var (cloud, userId) = await SeedSucceededCloud();
    using var client = factory.CreateClientWithTotpVerifiedAndUnlocked(userId);

    var resp = await client.PostAsJsonAsync($"/api/clouds/{cloud.Id}/destroy",
        new DestroyCloudRequest(cloud.Hostname));

    resp.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    var body = await resp.Content.ReadFromJsonAsync<EnqueueResponse>();
    body!.JobId.ShouldNotBe(Guid.Empty);

    using var db = factory.Services.CreateScope().ServiceProvider.GetRequiredService<PortalDbContext>();
    var job = await db.Set<ProvisioningJob>().SingleAsync(j => j.Id == body.JobId);
    job.Kind.ShouldBe(SagaKinds.Destroy);
    job.Status.ShouldBe(SagaStatus.Destroying);
}

[Theory]
[InlineData(SagaStatus.Succeeded,    HttpStatusCode.Accepted)]
[InlineData(SagaStatus.AwaitingCert, HttpStatusCode.Accepted)]
[InlineData(SagaStatus.FailedCert,   HttpStatusCode.Accepted)]
[InlineData(SagaStatus.FailedTf,     HttpStatusCode.Gone)]
[InlineData(SagaStatus.FailedDns,    HttpStatusCode.Gone)]
[InlineData(SagaStatus.RolledBack,   HttpStatusCode.Gone)]
public async Task DestroyChecksProvisioningStatus(string status, HttpStatusCode expected) { /* ... */ }

[Fact]
public async Task DestroyWithoutUnlock_Returns401StepUpRequired() { /* ... */ }

[Fact]
public async Task DestroyHostnameMismatch_Returns400() { /* ... */ }

[Fact]
public async Task DestroyOtherUsersCloud_Returns403() { /* ... */ }

[Fact]
public async Task DestroySoftDeletedCloud_Returns410() { /* ... */ }

[Fact]
public async Task DestroyDuringActiveJob_Returns409() { /* ... */ }
```

### `DestroyEntryHandlerTests.cs`

```csharp
[Fact]
public async Task EntryHandler_CloudWithDns_TransitionsToRollingBackDns()
{
    var cloud = await SeedSucceededCloudWithDnsRecord();
    var job   = await SeedDestroyJob(cloud.Id);
    var sut   = new DestroyEntryHandler(db, events, clock);

    await sut.HandleAsync(job, default);

    job.Status.ShouldBe(SagaStatus.RollingBackDns);
}

[Fact]
public async Task EntryHandler_CloudWithoutDns_TransitionsToRollingBackTf()
{
    var cloud = await SeedCloudThatNeverReachedDns();
    var job   = await SeedDestroyJob(cloud.Id);
    var sut   = new DestroyEntryHandler(db, events, clock);

    await sut.HandleAsync(job, default);

    job.Status.ShouldBe(SagaStatus.RollingBackTf);
}
```

### `RollingBackTfHandlerDestroyTests.cs`

```csharp
[Fact]
public async Task Destroy_Success_SoftDeletesCloudAndRevokesTokens()
{
    var cloud = await SeedSucceededCloud();
    var token1 = await SeedPluginToken(cloud.Id);
    var token2 = await SeedPluginToken(cloud.Id);
    var job = await SeedJobInStatus(cloud.Id, SagaStatus.RollingBackTf, SagaKinds.Destroy);

    fakeTerraform.DestroyResult = TerraformResult.Success;
    var sut = new RollingBackTfHandler(db, events, clock, fakeTerraform, vault, ...);

    await sut.HandleAsync(job, default);

    job.Status.ShouldBe(SagaStatus.RolledBack);
    var reloaded = await db.Set<Cloud>().IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id);
    reloaded.DestroyedAt.ShouldNotBeNull();
    reloaded.VmIp.ShouldBeNull();
    (await db.Set<PluginTokenMetadata>().Where(p => p.CloudId == cloud.Id).ToListAsync())
        .ShouldAllBe(p => p.RevokedAt != null);
    fakeTerraform.DeleteWorkspaceCalls.ShouldHaveSingleItem();
}

[Fact]
public async Task Destroy_AbandonedAfter5Attempts_FailedDestroyTerminal_CloudNotSoftDeleted()
{
    /* ... */
    job.AttemptCount = 6;
    fakeTerraform.DestroyResult = TerraformResult.Failure("auth error");
    await sut.HandleAsync(job, default);

    job.Status.ShouldBe(SagaStatus.FailedDestroy);
    var reloaded = await db.Set<Cloud>().IgnoreQueryFilters().SingleAsync(c => c.Id == cloud.Id);
    reloaded.DestroyedAt.ShouldBeNull();
    fakeTerraform.DeleteWorkspaceCalls.ShouldBeEmpty();
}
```

### `DestroyEndToEndTests.cs`

Testcontainers Postgres + the real worker (single replica, `SemaphoreSlim(1)` for determinism) + fake terraform + fake Cloudflare. Drives the row from `destroying` to `rolled_back` in a single test, asserts the final cleanup state. ~80 LOC; pattern matches the existing saga end-to-end test from PORTAL-007.

## Acceptance criteria

- `dotnet build` succeeds with **zero warnings**.
- `dotnet test` passes including all new tests.
- `dotnet ef migrations script -i` produces the new `DestroySagaStatuses` migration without diff against the model snapshot.
- `WorkspaceLayoutTests` + all PORTAL-007 + PORTAL-008 tests stay green (regression guard on the existing rollback paths).
- `pnpm typegen` in `src/ThanyMarcus.Portal.Web/` regenerates the TS contracts cleanly; the new `DestroyCloudRequest` type is exported.
- Manual smoke (against the stub provider from PORTAL-007):
  1. Boot the full stack via `docker compose up`.
  2. Sign in → enable TOTP → init passphrase → unlock.
  3. Create a stub-provider cloud via direct SQL (or via PORTAL-011's wizard if landed). Drive it to `succeeded` (the stub auto-callback works).
  4. `curl -X POST -H "Cookie: ..." -H "Content-Type: application/json" \
         -d '{"confirmHostname":"<host>"}' \
         https://localhost/api/clouds/<id>/destroy`
  5. Watch `provisioning_jobs` row walk `destroying → rolling_back_dns → rolling_back_tf → rolled_back` over ~10s.
  6. Assert: `clouds.destroyed_at IS NOT NULL`, `plugin_token_metadata.revoked_at IS NOT NULL` for all rows where `cloud_id = <id>`, the terraform `pg` workspace for the cloud no longer exists (`SELECT name FROM terraform_workspaces WHERE name = '<cloud_id>'` returns no rows).
- Manual smoke against a real DO droplet (after PORTAL-008 + PORTAL-010 land): same flow but the droplet, volume, firewall, and Cloudflare A-record actually go away. Cost: ~$0.01 if you destroy within 1 minute of provisioning.

## Concrete steps in order

1. **`SagaKinds.cs`** — single constant class. Land + a 3-line test.
2. **`SagaStatus.cs`** — add `Destroying` + `FailedDestroy`; expand `Terminal`. Add a small test asserting the set membership.
3. **`ProvisioningJobConfiguration.cs`** — update `TerminalStatusFilter` to include `failed_destroy`. Land migration `20260516XXXXXX_DestroySagaStatuses`.
4. **`Features/Destroy/.gitkeep` → `Features/CloudManagement/Destroy/`** — move the placeholder; land `DestroyCloudRequest.cs` + `DestroyCloudEndpoints.cs`. Register in `Program.cs`.
5. **`DestroyCloudEndpointTests.cs`** — write the suite first (TDD). Watch the 202 happy path fail because the endpoint exists but no saga handler routes `destroying` yet (the row sits at `destroying` forever; that's fine for endpoint-level tests).
6. **`ITerraformRunner.DeleteWorkspaceAsync`** + impl + fake. Tiny.
7. **`DestroyEntryHandler.cs`** + registration in `SagaWorker/Program.cs`. Land `DestroyEntryHandlerTests.cs`.
8. **`RollingBackDnsHandler.cs`** branch — minimal change, mostly events_log enrichment. Extend existing tests.
9. **`RollingBackTfHandler.cs`** branch — the meaty change. Land `RollingBackTfHandlerDestroyTests.cs`.
10. **`DestroyEndToEndTests.cs`** — Testcontainers integration. Drives the full chain.
11. **Manual smoke** — stub provider end-to-end. Real DO is a follow-up after PORTAL-008/010 land.
12. **TS contracts** — `pnpm typegen`; verify `DestroyCloudRequest` shape.
13. **Verify all acceptance criteria**. `git status` shows only files in "Output of PORTAL-015 — final directory state". Commit; open PR.

## Risks & gotchas

- **DEK expiry mid-saga.** Same risk as PORTAL-007's create path. The SagaWorker re-fetches the DEK via `IInfraOpUnlockCache.TryGetAsync` on every phase that needs the provider token. For destroy, `RollingBackTfHandler` is the only DEK consumer (terraform destroy needs the provider API token). The 10-minute sliding-TTL window covers `destroying → rolling_back_dns → rolling_back_tf` comfortably (~30 seconds of wall-clock work at most under stub; ~3–5 minutes under real DO). If the user walked away and the unlock expired before `rolling_back_tf` claims, the handler returns and re-queues at `now + 1 min`. **After 5 attempts → `failed_destroy`.** UX: dashboard surfaces "destroy stalled, re-unlock and retry." Acceptable for thesis scope.

- **The `EnqueueGuard` race.** Per PORTAL-007's gotcha, `EnqueueGuard.CheckAsync` is a check, not a lock. **The endpoint wraps check + insert in SERIALIZABLE**; that's what makes the invariant authoritative. If you skip SERIALIZABLE, two concurrent destroy requests can both pass the check and insert two `destroying` rows for the same cloud. The second's `Status = destroying` insert succeeds (no unique constraint on cloud_id for non-terminal jobs — adding one would conflict with successful prior create jobs); the saga then has two destroy rows racing each other. Result: terraform destroys correctly (idempotent) but the cleanup transaction runs twice, plugin tokens get revoked twice (no-op the second time because `RevokedAt != null`), `DestroyedAt` gets set twice (last writer wins). **Not a correctness disaster, but ugly logs.** SERIALIZABLE prevents it cheaply; don't skip.

- **Hostname uniqueness on re-create.** The `clouds.Hostname` unique index doesn't have a partial filter on `DestroyedAt IS NULL`. A user destroying `abc12345.thany.click` and then trying to create another cloud with the same hostname will get a unique-constraint violation. **Mitigation:** PORTAL-011's create endpoint generates a fresh random hostname per cloud (per cloud-pivot-plan-2026-05-13.md §22), so re-use is not a normal user flow. If you want to free the hostname for re-use, a follow-up adds `WHERE destroyed_at IS NULL` to the index — non-trivial because Postgres unique partial indexes don't auto-resolve; tracked as future-work, not in MVP.

- **`provisioning_jobs.cloud_id` FK is RESTRICT** per ADR-0032. Destroy soft-deletes the cloud; the destroy job row survives. **Good**: audit trail. **Watch out**: a hard-delete sweeper for soft-deleted clouds (future work) must `CASCADE` provisioning_jobs too, or the FK will block it.

- **Plugin tokens are revoked, not deleted.** Per the design, `RevokedAt` is set on `plugin_token_metadata`. Plugins polling the cloud get bearer-auth failures from the cloud-side (because the cloud is gone — Caddy returns 502 / connection-refused). The portal still has the revoked-token record for audit. **The plugin SPA** (out of scope here) should surface "this cloud was destroyed; reconfigure" — but the plugin discovers this through its own UX, not from the portal.

- **Terraform workspace deletion can stall.** If the `pg` backend has rows for the workspace that aren't owned by terraform (e.g., locks), `terraform workspace delete` fails. **The handler logs and proceeds** — the cloud is already soft-deleted from the user's POV. A cron sweeper (post-thesis) can clean orphan workspaces.

- **The `events_log` jsonb query in `DestroyEntryHandler`** uses `events_log @> '[{"phase":"dns_created"}]'::jsonb`. This works correctly only if the create-side `DnsCreatingHandler` appends an entry with `phase: "dns_created"` on success. **Verify PORTAL-007's actual events shape** — if it uses a different phase name, update the query string. Add a TODO marker + a test that creates a job through both handlers end-to-end to catch any drift.

- **`failed_cert` clouds going through destroy.** They're functional (compute + DNS + callback all up) but never got a cert. The destroy path works fine — the DNS record exists, the droplet exists. Test coverage includes `failed_cert` in the destroyable-status set.

- **DO API rate limits during destroy.** ~5000 req/h per token. A typical destroy is <10 API calls. At thesis scale this is irrelevant.

- **Concurrent destroys across different clouds.** `SemaphoreSlim(3)` in SagaWorker per ADR-0033 means up to 3 destroys in-flight on the same VM. Each terraform destroy takes ~30s–2min; 3 concurrent is comfortable on a B2ms.

- **What if `terraform destroy` succeeds but the cleanup transaction fails?** The droplet is gone, but the cloud row is not soft-deleted. The job's `Status` is still `rolling_back_tf`. On next claim, the handler re-runs `terraform destroy` (idempotent, returns success immediately because state is empty), then re-runs the cleanup transaction. The cleanup is also idempotent (`DestroyedAt = now` overwrites; `RevokedAt` set via `WHERE RevokedAt IS NULL` no-ops on the second pass). Saga eventually converges.

- **Cancellation mid-destroy.** Out of scope. Once the destroy is enqueued, it runs to terminal. UI shows "destroy in progress; please wait." If the user really needs to abort, they wait for the saga to terminal then re-create.

- **Operator-side manual cleanup.** If the saga reaches `failed_destroy`, the cloud is still up in DO/Azure. Operator runbook: SSH/web-console into the droplet (or use `doctl compute droplet delete --tag managed-by-portal cloud-id-<uuid>`), then update the row's status manually to `rolled_back` and soft-delete the cloud. Document in the SagaWorker README; not in code.

- **No bespoke destroy SSE channel.** The endpoint returns 202; the SPA (PORTAL-012) subscribes to `/api/clouds/{id}/events` (SSE) + reads `GET /api/clouds/{id}` (REST snapshot) per PORTAL-011 / ADR-0036. If you really want a distinct destroy topic, add a `kind = "destroy"` partition to the existing SSE channel — but that's PORTAL-012's call. Out of scope.

- **Two-step confirm UX (`confirmHostname`) is anti-fat-fingers, not anti-malicious.** A passphrase-unlocked attacker can read the hostname from any cloud-list response and type it. The real security gate is the step-up unlock. **Document that in the endpoint xmldoc** so a future reviewer doesn't accidentally promote the hostname check into something it isn't.

## Definition of done

All acceptance criteria pass. `git status` shows only the files in "Output of PORTAL-015 — final directory state". `docker compose up -d` from scratch produces a fully-up stack. Inserting (via the new endpoint) a destroy request for a `succeeded` cloud drives the saga end-to-end: row reaches `rolled_back`, cloud is soft-deleted, plugin tokens are revoked, terraform workspace is gone. Regression: all PORTAL-007 / PORTAL-008 tests stay green.

A fresh agent picking up **PORTAL-012 (cloud-list dashboard UI)** from this state knows:
- The destroy endpoint is `POST /api/clouds/{id}/destroy` with body `{"confirmHostname": "<hostname>"}`. Step-up-gated via `fetchWithStepUp`.
- Returns `202 Accepted { jobId, cloudId }` on success; subscribe to `/api/clouds/{id}/events` (SSE) + read `GET /api/clouds/{id}` (REST snapshot) for progress (PORTAL-011 / ADR-0036 channel).
- Common error responses: `401 step_up_required`, `400 hostname_mismatch`, `409 cloud_busy`, `410 Gone` (cloud already destroyed or never destroyable). The dashboard's destroy modal handles these per the standard auth/conflict UI patterns.
- The terminal-status surface to render: `rolled_back` (success ✓), `failed_destroy` (manual-cleanup-needed ⚠ with a "retry destroy" CTA).

A fresh agent picking up **PORTAL-013 (plugin-token issuance + revocation UI)** from this state knows:
- Destroy automatically revokes all plugin tokens for the cloud (`plugin_token_metadata.RevokedAt` set). The token-issuance UI should hide tokens belonging to destroyed clouds; the token-revocation UI need not call the destroy endpoint to revoke (it's already done).

A fresh agent picking up **PORTAL-016 (cloud-admin-token receive endpoint)** from this state knows:
- The destroy saga sets `cloud.CloudAdminTokenHash = null` on cleanup. PORTAL-016's callback handler must check `cloud.DestroyedAt IS NULL` before accepting a callback — a destroyed cloud's enrollment_token must not be redeemable. This is a separate check the receive endpoint owns; not on the destroy path.

## Cross-references

- **PORTAL-007** — prerequisite. Defines the saga, the dispatcher, `EnqueueGuard`, the rollback handlers, the `ITerraformRunner` abstraction this ticket extends.
- **PORTAL-008** — prerequisite. The DO terraform module's `destroy` is what `RollingBackTfHandler` shells out to. No module changes here.
- **PORTAL-005** — prerequisite. `IProviderTokenVault.DecryptAsync` is called by `RollingBackTfHandler` for the DO/Azure API token.
- **PORTAL-003f** — prerequisite. `IInfraOpUnlockCache` is the step-up cache the endpoint and the saga both consume.
- **PORTAL-010b** — sibling. The real Cloudflare client; destroy reuses it via `ICloudflareDnsClient.DeleteAsync`. If PORTAL-010b ships first, destroy benefits automatically; if PORTAL-010b hasn't shipped, the stub returns success and the A-record orphans (acceptable; logged in `events_log`).
- **PORTAL-011** — sibling. The create endpoint. PORTAL-015 lands the destroy endpoint following the same patterns (SERIALIZABLE + `EnqueueGuard` + step-up filter).
- **PORTAL-012** — consumer. Dashboard surfaces the destroy button + progress.
- **PORTAL-013** — consumer. Token UI hides tokens for destroyed clouds.
- **PORTAL-016** — consumer. Callback handler checks for soft-deleted clouds.
- **ADR-0030** — auth flow; step-up sequence the endpoint inherits.
- **ADR-0032** — FK cascades + soft-delete; the `clouds.DestroyedAt` contract.
- **ADR-0033** — saga state machine; this ticket adds 2 states.
- **ADR-0035** — wizard progress transport (referenced for the future SSE channel; not consumed in this ticket).
- **`cloud-pivot-plan-2026-05-13.md` §22.5** — full provisioning flow context.
- **`tickets-2026-05-13.md`** line 53 — original ticket-line; "manual DNS cleanup notice" is superseded by PORTAL-010b's automation.
