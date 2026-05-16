# PORTAL-011a Bundle hardening — Handoff Brief

**Goal:** fix the bugs surfaced during the **2026-05-16 manual smoke** of PORTAL-011 + 010b + 016, so a fresh end-to-end run of `plans/portal-011-smoke-runbook.md` reaches `saga.status = succeeded` against real DigitalOcean + real Cloudflare. No new features.

Estimated **0.5–1 person-day** with AI-agent assistance.

## What this ticket is

`PORTAL-011a` is the immediate follow-up to the bundled PORTAL-011 handoff. The bundle landed structurally — the wizard renders, `POST /api/clouds` returns 202, the saga worker claims the job, the SSE channel pushes phase transitions, a real droplet appears on DO. But **the saga does not reach `succeeded`** end-to-end, due to a list of small bugs and one architectural drift.

The smoke session caught these because tests don't `dotnet publish` and don't run real `terraform` against the real DO+CF APIs. PORTAL-011a closes that gap: it lands each fix and **one new integration test** that would have caught the entire terraform class of bugs in pre-flight.

This is not a redesign. The 14 locked decisions from the PORTAL-011 grill still stand. The bundle architecture is correct.

## Where decisions live (read before doing anything)

- **`plans/portal-011-handoff.md`** — the parent bundle. Decisions and architecture are unchanged; this ticket only fixes implementation drift from it.
- **`plans/portal-011-smoke-runbook.md`** — the 13-step manual walk that surfaced these bugs. Re-running it (after this ticket) is the acceptance test.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — saga shape, phase contract, dispatcher semantics.
- **`docs/decisions/0034-cloud-bootstrap-handshake.md`** — what cloud-init must do (acquire LE cert, POST `/api/clouds/{id}/callback`).
- **`docs/decisions/0036-wizard-progress-transport.md`** — SSE terminal-close on `succeeded`/`failed_*`/`rolled_back`.
- **DEC-003 (in 011 grill)** — Cloudflare token is **platform-owned**, not per-user.

## What's actually broken — punch list

Each item below ties a smoke-session observation to a code location. Numbers match the order they bit during the 2026-05-16 walk.

### Critical — block re-smoke

#### F1. cloud-init template is the PORTAL-008 placeholder, not PORTAL-010's real bootstrap

The DO module loads `infra/docker/saga-worker/terraform-modules/shared/cloud-init.sh.tpl` — a 31-line PORTAL-008 placeholder that just `echo`s and exits. PORTAL-010's real cloud-init (Docker install + GHCR pull + Caddy + LE + callback POST) lives at `infra/terraform/shared/cloud-init.yaml.tpl` and **never gets copied into the saga-worker image**.

Result: droplet boots, runs nothing, never calls back, saga eventually times out in `awaiting_cloud_callback`.

**Fix:**
- Decide single source of truth for the template. **Recommendation:** keep it at `infra/terraform/shared/cloud-init.yaml.tpl` (already the PORTAL-010 location) and **copy it into the image at build time**. Edit `infra/docker/saga-worker/Dockerfile`:
  ```dockerfile
  COPY infra/terraform/shared/cloud-init.yaml.tpl /app/terraform-modules/shared/cloud-init.yaml.tpl
  ```
- Update `infra/docker/saga-worker/terraform-modules/digitalocean/main.tf` line 24 to reference `cloud-init.yaml.tpl` not `cloud-init.sh.tpl`.
- Delete `infra/docker/saga-worker/terraform-modules/shared/cloud-init.sh.tpl` (the placeholder) — single source of truth.
- Verify cloud-init template variables match: `cloud_id`, `hostname`, `portal_url`, `enrollment_token`, `ghcr_pat`. Adjust either side if they don't.

#### F2. templatefile path is hardcoded absolute — workdir-relative is the right shape

`infra/docker/saga-worker/terraform-modules/digitalocean/main.tf:24` currently hardcodes `/app/terraform-modules/shared/cloud-init.yaml.tpl`. That works inside the saga-worker container but **breaks every test** that wants to run `terraform plan` against the module from any other workdir. The hack exists because `${path.module}/../shared/...` failed in our rendered workspace (terraform resolved `path.module` to the local copy of the module, which has no `../shared/`).

**Fix:** restructure so `${path.module}/../shared/...` works. The clean shape is:
- `WorkspaceLayout.RenderAsync` symlinks (or copies) `{modulesDir}/shared` into `{workdir}/shared` alongside `{workdir}/main.tf`, so the rendered workspace mirrors the source layout.
- Or simpler: change the DO module to use `templatefile("${path.module}/cloud-init.yaml.tpl", …)` and **copy the file into the digitalocean module dir directly** instead of keeping it in `shared/`. (Only DO uses it until PORTAL-009; promote to `shared/` then.)

**Recommendation:** the second approach. Less moving parts, terraform-validate-friendly, no symlinks.

#### F3. DnsCreatingHandler reads CF token from per-user vault — architectural drift from DEC-003

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/DnsCreatingHandler.cs:58-69` tries `providerVault.DecryptAsync(cloud.UserId, KnownProviders.Cloudflare, dek, ct)` first, only falling back to `CloudflareOptions.ApiToken` if that's empty.

DEC-003 locked this as **platform-owned** — the operator (you) holds one CF token in app config; users never see it. The grill explicitly chose this so user wizards don't ask for a CF token they don't have.

**Fix:** delete the unlock-cache + vault lookup branch from `DnsCreatingHandler`. Pass an empty `callerToken` to `cloudflare.CreateAAsync(...)` so `CloudflareDnsClient.cs:68` falls through to `opts.Value.ApiToken` directly. Also remove the now-unused `IInfraOpUnlockCache` + `IProviderTokenVault` ctor params; the handler shouldn't even know about user step-up.

(Cross-check: `RollingBackDnsHandler.cs` — same drift if it exists there. Read it and apply the same fix.)

#### F4. SPA doesn't redirect `totp=not-verified` users to `/totp-challenge` after re-login

`src/ThanyMarcus.Portal.Web/src/routes/+layout.ts` fetches `/api/auth/me` but never branches on `data.me.totp`. After logout/login of a TOTP-enrolled user, the SPA happily renders `/settings/security` and lets the user click "Set passphrase" — which then 403s server-side because `RequireAuthorization(AuthPolicies.TotpRequired)` rejects `totp=not-verified`.

**Fix:** in `+layout.ts` (or a shared `requireAuth` helper, per the bundle's frontend foundation spec), if `me.totp === "not-verified"` and `route.id` is not `/totp-challenge` or `/signout`, redirect to `/totp-challenge`. Whitelist the auth/callback routes (`/signin`, `/signin-google`, `/totp-challenge`).

This is the bundle's `requireAuth` helper from the grill; it just wasn't wired into the root layout.

#### F5. No MSBuild target copies SvelteKit build → `Portal.Api/wwwroot`

Per `portal_web_stack.md` memory, Portal.Web's `adapter-static` output is **served from Portal.Api/wwwroot**. The bundle ships the Web project but no MSBuild target copies its build output. Manual `cp -r src/ThanyMarcus.Portal.Web/build/. src/ThanyMarcus.Portal.Api/wwwroot/` was required every smoke iteration.

**Fix:** add to `src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj`:
```xml
<Target Name="BuildSpa" BeforeTargets="Build" Condition="'$(SkipSpaBuild)' != 'true'">
  <Exec Command="pnpm install --frozen-lockfile" WorkingDirectory="..\ThanyMarcus.Portal.Web" />
  <Exec Command="pnpm build" WorkingDirectory="..\ThanyMarcus.Portal.Web" />
  <ItemGroup>
    <SpaOutput Include="..\ThanyMarcus.Portal.Web\build\**\*" />
  </ItemGroup>
  <Copy SourceFiles="@(SpaOutput)" DestinationFolder="wwwroot\%(RecursiveDir)" SkipUnchangedFiles="true" />
</Target>
```
- Gate with `Condition="'$(SkipSpaBuild)' != 'true'"` so CI test jobs that don't have pnpm can skip via `-p:SkipSpaBuild=true`.
- Add `wwwroot/` to `.gitignore` if not already.
- Verify `docker compose build portal-api` still works (the Portal.Api Dockerfile needs pnpm/node in the build stage, or alternatively do `pnpm build` in a separate Docker stage and `COPY --from=spa-build` into the runtime image — pick whichever keeps the build clean).

#### F6. Saga-worker Dockerfile missing `libgssapi-krb5-2`

Npgsql throws `TypeInitializationException` at startup on the .NET 10 `runtime:10.0` base image because `libgssapi-krb5-2` is not present.

**Fix:** confirm the line is in `infra/docker/saga-worker/Dockerfile`:
```dockerfile
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
```
(Likely already there from the smoke fix — verify it's committed.)

### Should-fix — polish

#### F7. `provisioning_jobs.user_id NOT NULL FK` not seeded in tests

The PORTAL-011 migration added `provisioning_jobs.user_id` with `NOT NULL` + FK to `users.id`. 32 tests failed in pre-flight with `fk_provisioning_jobs_users_user_id` violations. Already fixed in:
- `tests/ThanyMarcus.Portal.Tests/SagaWorker/SagaTestSeed.cs`
- `tests/ThanyMarcus.Portal.Tests/Features/Provisioning/ProvisioningJobTests.cs`
- `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Destroy/DestroyCloudEndpointTests.cs`

**Verify:** `dotnet test` is green. If anything still relies on a future seed helper, refactor `SagaTestSeed` to **always** populate `UserId` from a seeded user, never `default(Guid)`.

#### F8. SagaWorker .csproj duplicate-output collision

`appsettings.json` collides on `dotnet publish` because the ProjectReference to Portal.Api drags Portal.Api's own `appsettings.json` into the worker's output dir.

Fix already applied in `src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj`:
```xml
<ErrorOnDuplicatePublishOutputFiles>false</ErrorOnDuplicatePublishOutputFiles>
```

**Better fix (do this in 011a):** make the worker's `appsettings.json` the only one in its publish output. Add to the csproj:
```xml
<ItemGroup>
  <Content Update="..\ThanyMarcus.Portal.Api\appsettings*.json" CopyToPublishDirectory="Never" />
</ItemGroup>
```
Then remove the `ErrorOnDuplicatePublishOutputFiles` override. The override silences a class of warnings we'd rather hear about.

#### F9. Terraform pg backend smell (already patched, lock it down)

Two bugs hit in the same call:
- WorkspaceLayout emitted invalid one-line HCL `terraform { backend "pg" {} }` — fixed to multi-line in `WorkspaceLayout.cs:33`.
- TfPlanningHandler passed the raw .NET connection string to terraform; pg backend required a `postgres://` URL with `sslmode=disable`. Fixed via `ToPostgresUrl` helper in `TfPlanningHandler.cs:148-160`.

**Verify in 011a:** both fixes are committed. Add a unit test for `ToPostgresUrl` covering: SSL on/off, URL-encoding of `@`/`:`/`/` in passwords, default port, empty username.

#### F10. `CreateCloudEndpoints` inserted `Status=Pending` — dispatcher has no handler for "pending"

Already fixed in `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Create/CreateCloudEndpoints.cs:64,76` (both Cloud and ProvisioningJob now start at `TfPlanning`).

**Lock it down:** add a test in `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Create/` that asserts the inserted `ProvisioningJob.Status` is a phase the dispatcher knows about. Cheap one-liner via `SagaPhaseDispatcher` enumeration; would have caught the bug.

#### F11. Cloudflare `UseStub=true` default in `appsettings.Development.json` leaks into compose

Even with `Cloudflare__ApiToken` set in `.env`, the saga used `StubCloudflareDnsClient` because `appsettings.Development.json` had `Cloudflare:UseStub = true` and compose was running `DOTNET_ENVIRONMENT=Development`. The fix in `docker-compose.override.yml:10` adds `Cloudflare__UseStub: "false"`.

**Better fix:** flip the default. Remove `Cloudflare:UseStub` from `appsettings.Development.json` entirely (default to false in `CloudflareOptions`). Tests that need the stub set it explicitly via test config. Avoids the "env-var override silently required" footgun.

#### F12. `/settings/security` stuck on "Loading…" after TOTP setup

`src/ThanyMarcus.Portal.Web/src/routes/settings/security/+page.svelte` doesn't re-fetch state after the backup-codes modal is dismissed. The user enrolled successfully, dismissed the modal, and the page sat on "Loading…" until a hard refresh.

**Fix:** on modal-dismiss, re-call the `/api/auth/me` + `/api/auth/totp/state` endpoints (whichever feeds the page's reactive state). Verify with Svelte 5 runes (`$state` / `$effect`) that the reload actually triggers a re-render.

### Carry forward (do NOT fix in 011a)

These hurt the smoke but are bigger than this ticket. File as separate tickets:

#### F13. No way to destroy a `failed_tf` cloud → **PORTAL-015a**

`POST /api/clouds/{id}/destroy` requires the cloud to be in `succeeded`. A cloud that reached `tf_applying` (real DO droplet created) but then failed in `dns_creating` (or later) has no UI path to destroy. During the smoke, the droplet at 209.38.224.184 had to be killed via the DO web console.

Needs a "force-destroy" path for clouds in any state past `tf_applying`. Step-up still required. Separate ticket.

#### F14. Auto-rollback gives up after 5 retries → **PORTAL-007b** (saga hardening)

`terraform destroy` in `RollingBackTfHandler` hit `rollback_init_failed` 5 times then stopped. Root cause is the pg backend re-init in the rollback workspace; the workspace was already partially-applied so terraform tried to re-acquire state lock and couldn't.

Needs investigation in a saga-hardening ticket. Workaround for now: manual DB UPDATE to `destroyed_at` + manual DO cleanup.

#### F15. Google OAuth redirect-URI setup not in any runbook → **runbook polish**

The redirect_uri_mismatch error during signup is fixed by adding `https://<your-tunnel>/signin-google` to the Google Cloud Console authorized redirect URIs. The smoke runbook should document this. Edit `plans/portal-011-smoke-runbook.md` step 0 to include the GCC step. Trivial, do as part of 011a's docs update.

#### F16. `dotnet ef` design-time factory points at `portal_design` → **PORTAL-002 polish**

When running EF migrations from the host, the design-time factory connects to `portal_design` not `portal_dev`. The smoke workaround was `--connection "Host=localhost;Port=5432;...;Database=portal_dev"`. Either:
- Change the design-time factory to read from `appsettings.Development.json`, or
- Document in `CONTRIBUTING.md` that host-side `dotnet ef` calls always need `--connection`.

Pick one. Separate ticket.

## The new test that would have caught half of these

Add `tests/ThanyMarcus.Portal.Tests/SagaWorker/TerraformWorkspaceIntegrationTests.cs`:

```csharp
[Collection(PostgresCollection.Name)]
public sealed class TerraformWorkspaceIntegrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Rendered_workspace_passes_terraform_validate_for_digitalocean()
    {
        // 1. Seed a Cloud + ProvisioningJob in postgres.
        // 2. Resolve WorkspaceLayout against a temp dir; render the DO workspace.
        // 3. Shell out to `terraform init -backend=false` (no pg backend in test).
        // 4. Shell out to `terraform validate`.
        // 5. Assert exit 0 and no diagnostics.
    }

    [Fact]
    public async Task Rendered_workspace_terraform_plan_succeeds_with_stub_provider()
    {
        // Same as above but use a stub DO provider (or DO_TOKEN=dummy + plan-only flag) so we don't hit the real API.
        // Asserts: HCL parses, templatefile path resolves, pg URL parses, sslmode is right.
    }
}
```

**Why this matters:** every single terraform bug we hit in the smoke (F2, F9, the one-line HCL) would have been caught by `terraform validate` against a rendered workspace. The bundle has unit tests for `WorkspaceLayout` but **none of them run real terraform**. This test closes that gap.

It requires `terraform` on the test runner's PATH. Mark the test with `[Trait("requires", "terraform")]` and skip in CI if not present. Local dev always has it.

## Acceptance criteria

- `dotnet build` — zero warnings (warnings-as-errors).
- `dotnet test` — all green (the 32 fixed tests plus the two new integration tests).
- `docker compose build` — clean build, no `NETSDK1152` (after F8 better-fix), no missing libs (after F6).
- `docker compose up -d` — all services healthy. `docker compose exec saga-worker terraform version` prints a version.
- A re-run of `plans/portal-011-smoke-runbook.md` end-to-end against a real DO + real Cloudflare zone reaches **`saga.status = succeeded`**:
  - Droplet boots
  - Cloud-init installs Docker + pulls images + acquires LE cert
  - Cloud calls back to portal `/api/clouds/{id}/callback`
  - Saga transitions `awaiting_cloud_callback` → `awaiting_cert` → `succeeded`
  - SSE channel emits the terminal `phase: succeeded` and closes
  - User can navigate to `https://<hostname>.thany.click` and see the cloud's `/health/ready` return 200
- After success: `POST /api/clouds/{id}/destroy` (with step-up unlock) tears the droplet down cleanly and the row transitions to `destroyed`.
- `wwwroot/` is regenerated on build; no manual `cp` step.
- Logged-in user with `totp=not-verified` is bounced to `/totp-challenge` on any non-auth route (no more 403 surprises).
- After enrolling TOTP and dismissing the backup-codes modal, `/settings/security` shows the post-enrollment state without a hard refresh.

## Concrete steps in order

1. **F1 + F2** (cloud-init template) — single biggest behavior unlock. Drives a real droplet to actually run real bootstrap. Do this first because everything after `tf_applying` is gated on it.
2. **F3** (DnsCreatingHandler drift) — small mechanical change; do it before re-running the saga so the next smoke uses the platform CF token.
3. **F4** (totp redirect) — frontend-only; can land in parallel.
4. **F5** (SPA build target) — eliminates a daily friction point.
5. **F6, F8, F11** (Dockerfile + csproj polish) — small.
6. **F9, F10** (lock-down tests for existing patches) — small.
7. **F12** (security page reactivity) — frontend-only.
8. **The new TerraformWorkspaceIntegrationTests** — the single most valuable test to add.
9. **F15** (runbook edit) — docs only.
10. **Run the full smoke runbook** end-to-end. If it reaches `succeeded`, ship.

Items F13, F14, F16 are out of scope — file them as separate tickets when 011a lands.

## Out of scope (do not touch)

- Anything in the **carry-forward list above** (F13, F14, F16). File tickets.
- Frontend redesign — bundle UX shape is decided.
- New phase handlers — saga state machine is decided.
- Azure provider — PORTAL-009.
- Multi-cloud UI — PORTAL-012.
- Production deploy — PORTAL-017.
- Refactoring the saga-worker → Portal.Api project reference. Yes it's ugly. PORTAL-007 may extract a `ThanyMarcus.Portal.Domain` library later; not 011a's problem.

## Risks & gotchas

- **F1's "single source of truth" decision** has knock-on effects: the cloud-init template needs to be tested. Add a smoke check in F1's commit that the rendered template parses as valid YAML (or shell, depending on which form 010 uses) — a `templatefile` that produces malformed cloud-init silently makes droplets reboot-loop.
- **F3 removes ctor params from `DnsCreatingHandler`.** Update DI registrations in `Program.cs` if you removed them via constructor (the compiler will catch it). Also delete the unlock-cache + vault probes from any tests that asserted on them.
- **F5's MSBuild target slows `dotnet build`** because pnpm install + svelte build runs every time. Mitigate with `SkipUnchangedFiles="true"` (already in the snippet) and `Condition` on file timestamps if it becomes slow.
- **F8's better-fix** (`CopyToPublishDirectory="Never"`) only works if you've confirmed the worker doesn't actually need Portal.Api's `appsettings.json`. It shouldn't — the worker has its own — but verify nothing in the worker reads keys that only exist in Portal.Api's appsettings (auth provider config, OAuth secrets, etc.).
- **The new `TerraformWorkspaceIntegrationTests`** requires `terraform` on PATH. CI may or may not have it. Skip-attribute it, document the local-dev expectation in `CONTRIBUTING.md`.
- **Smoke re-run will cost real money on DO.** ~$0.03/hour for a `s-2vcpu-4gb`. Tear down the droplet immediately after smoke succeeds. Both via the portal's destroy endpoint *and* via the DO web console as belt-and-braces — orphaned droplets from this iteration's smoke ate $0.20 before manual cleanup.
- **PORTAL-011's frontend foundation gaps** (types directory, parseProblem helper, requireAuth helper, SSE client, design tokens) — the bundle said these would land. F4 builds on `requireAuth`. Verify it actually exists in the bundle's frontend foundation before referencing it; if not, land the helper as part of F4.
- **`appsettings.Development.json` flag flips** (F11) can cascade: someone reads "UseStub default is now false" and forgets to set it in a test fixture, then tests start hitting real Cloudflare. Audit all test fixtures that resolve `ICloudflareDnsClient` after F11.
- **F2's recommendation to move the template into the DO module dir** means PORTAL-009 (Azure) will have its own copy. Acceptable; promote to `shared/` properly when Azure lands.

## Definition of done

- All acceptance criteria pass.
- Full smoke runbook walked end-to-end with `saga.status = succeeded` on a real DO droplet, with a real LE certificate, reachable at `https://<hostname>.thany.click`.
- `git status` clean. Tickets F13/F14/F15/F16 filed (even as one-line stubs in `plans/tickets-2026-05-13.md`).
- PORTAL-011's smoke runbook updated to reflect the new flow (no manual `cp`, no GCC step missing, etc.).

A fresh agent picking up the next stage (M5 demo: CLOUD-001 + PLUGIN-001) from this state knows:
- The portal provisions a working cloud end-to-end.
- The cloud responds at its hostname with a valid LE cert.
- Destroy works from the UI.
- There is now a `terraform validate` integration test that catches HCL/path/conn-string regressions in pre-flight.

## Cross-references

- **`plans/portal-011-handoff.md`** — parent bundle. This is its hardening pass.
- **`plans/portal-011-smoke-runbook.md`** — acceptance walkthrough. Update during F15.
- **`plans/tickets-2026-05-13.md`** — file F13/F14/F16 here.
- **ADR-0033, ADR-0034, ADR-0036** — unchanged; this ticket aligns implementation with them.
- **DEC-003 (Cloudflare = platform-owned)** — F3 enforces.
