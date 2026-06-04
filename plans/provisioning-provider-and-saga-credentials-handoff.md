# Provisioning provider strategy + saga-scoped credential grant — Handoff

**Two related refactors of the provisioning saga's provider/credential layer**, both surfaced by the 2026-06-04 incident below. They are independent but touch the same five saga handlers, so this doc scopes them together and recommends a sequencing.

- **Part 1 — Saga-scoped credential grant.** A timeout-triggered rollback cannot authenticate to the provider because the credential it needs (the DEK) expires before the saga finishes. Estimate **~2–2.5 person-days** with AI-agent assistance.
- **Part 2 — `IProvisioningProvider` strategy.** Provider behaviour is string-dispatch on a bare `Cloud.Provider` string across ~19 sites; the `"stub"` test double leaks into production branches. Estimate **~1.5–2 person-days**.

Neither is started. All file:line references verified 2026-06-04 against the working tree.

---

## Background — the 2026-06-04 incident that motivated this

A DO provision came up to `tf_applying` (droplet created) but the cloud-init callback never fired (a `set -o pipefail`-under-dash regression in the cloud-init template — fixed separately this session). The create saga waited out the full 90-minute `awaiting_cloud_callback` timeout (`SagaTimeouts.AwaitingCloudCallback`), then entered `rolling_back_tf` to destroy the droplet. By then the user's step-up unlock (60-minute sliding TTL) had expired, so the rollback could not decrypt the DO credentials — `terraform destroy` never ran, the droplet was orphaned, and (pre-fix) the handler looped forever on `AuthenticationTagMismatchException`.

Fixes landed this session (loop-stopping, not root-design):
- `infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl` — `fetch_docker_gpg` rewritten dash-safe (the actual "callback never fires" root cause).
- `DnsCreatingHandler.cs:82` — enters `awaiting_cloud_callback` with `Duration.Zero` so the 15s cancel-poll cadence actually runs.
- `RollingBackTfHandler.cs` — bails cleanly on missing unlock for real providers + a `catch` routes exceptions through the capped retry instead of an infinite loop.
- `KnownProviders.RequiresCredentials(...)` — removed the one `"stub"` magic string in `RollingBackTfHandler` (the seed of Part 2).

Two **latent design gaps** remain, which are Parts 1 and 2.

**Operator runbook note (until Part 1 lands):** a manual rollback re-drive must reset **both** the `clouds.provisioning_status` and the `provisioning_jobs.status` out of their terminal state — `SagaPhaseDispatcher` abandons any job whose *cloud* is terminal (logs `Abandoning job … for terminal cloud`). Resetting only the job is silently ignored.

---

## Part 1 — Saga-scoped credential grant

### Problem (precise)

The DEK-envelope model (ADR-0030): a per-user DEK is unwrapped from the passphrase at `POST /api/auth/unlock` (`PassphraseEndpoints.cs:34`) and cached in the `step_up_unlocks` table. The saga "inherits the unwrapped DEK transiently" through that cache.

- **Cache:** `PostgresInfraOpUnlockCache`, `SlidingTtl = Duration.FromMinutes(60)` (`PostgresInfraOpUnlockCache.cs:14`). Table `step_up_unlocks(user_id, encrypted_dek, expires_at, last_used_at, created_at)` — **per-user, not per-saga**. Registered `AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>()` in both `Program.cs` (api:120, worker:66).
- **Saga-side DEK consumers (the scope of this work — 5 handlers):**
  - `TfPlanningHandler.cs:50`
  - `TfApplyingHandler.cs:59`
  - `MintingSpacesHandler.cs:55`
  - `RollingBackTfHandler.cs:54`
  - `CancelHandler.cs:167`
  Each does `unlockCache.TryGetAsync(cloud.UserId, dek, ct)` and degrades/fails if the unlock is absent.
- **What the DEK decrypts for DO:** the OAuth access token (`DigitalOceanOAuthConnections.GetAccessTokenAsync(userId, dek)`, AES-GCM) **and** the per-cloud Spaces secret bundle (`ICloudSecretBundle`). Both are needed to build the `terraform` env (`DigitalOceanTfEnv.TryAddDoEnvVarsAsync`).

**The mismatch:** saga lifetime (`awaiting_cloud_callback` alone is 90 min, plus rollback retries) **exceeds** the 60-min unlock TTL. So any rollback triggered by the callback timeout is *guaranteed* to run after the unlock has expired — unless the user happens to re-unlock. The post-fix behaviour is a clean terminal `failed_*` with the resource orphaned, requiring a manual re-unlock + cloud/job reset to clean up. That manual dance is the symptom this part removes.

The user-interactive endpoints that also consume the unlock (`Create`, `Cancel`, `Destroy`, `ProviderToken`, DO OAuth/connection, `PricingBackfill`, `Passphrase`, `Totp`, `EmergencyKit` — all behind `RequireInfraOpUnlockFilter`) are **out of scope**: the user is present and the unlock guard is correct there.

### Goal

Decouple the saga's ability to authenticate to the provider from the interactive unlock TTL, so apply **and** compensation (rollback/cancel) complete across the saga's full lifetime without requiring the user to be present or re-unlocked.

### Design options

| | Approach | Pro | Con |
|---|---|---|---|
| **A** | **Saga-bound sealed DEK.** At authorization, copy the DEK into a saga-scoped store keyed by cloud/job id, sealed at rest (DataProtection — `dp_keys` volume is already shared api↔worker), lifetime = saga lifetime. | Covers all secret kinds, incl. Spaces keys minted *later* by `MintingSpacesHandler`. Minimal handler logic. | Holds the master DEK longer → broadest blast radius if the portal is breached mid-saga. |
| **B** | **Saga-bound sealed provider creds.** Decrypt only the specific creds the saga needs and seal *those* (not the DEK) per-cloud, aligned with the existing per-cloud `cloud_secrets` bundle. | Narrowest blast radius (one cloud's provider creds, not the key that unlocks everything). | Spaces keys are minted *after* create (`MintingSpacesHandler`), so the grant must be appended to mid-saga; DO OAuth-token refresh handling is more complex. |
| **C** | **Extend unlock TTL while a saga is in-flight.** Worker bumps `step_up_unlocks.expires_at` while a non-terminal saga exists for the user. | Smallest change. | Per-*user*, not per-saga — keeps **everything** unlocked far longer than the user expects. Security regression. **Reject.** |

**Recommendation:** **A** is the pragmatic choice given that DO needs *multiple* secrets and one of them (Spaces keys) is minted mid-saga — sealing the DEK once covers both the OAuth token and the later-minted bundle without a second write. Bound it tightly (see threat-model note) to keep the exposure acceptable. B is preferable on blast-radius grounds *if* the minting step is reworked to append minted keys to the grant; capture that decision in the ADR.

### Scope / steps (option A)

1. **Storage.** New table `saga_credential_grants(cloud_id PK/FK, sealed_dek bytea, created_at, expires_at)` (or a new `cloud_secrets` kind). `expires_at` = the saga's hard deadline, not 60 min. Seal `sealed_dek` with `IDataProtectionProvider` (purpose-scoped), so a DB-only breach yields nothing.
2. **Write the grant at authorization.** In `CreateCloudEndpoints` (the user is unlocked there via `RequireInfraOpUnlockFilter`), after the cloud row is created, take the live DEK from the unlock cache and seal it into the grant bound to the cloud.
3. **Credential-source seam.** Introduce `ISagaCredentialSource.TryGetDekAsync(cloud, dek, ct)` that prefers the live interactive unlock and **falls back to the saga grant**. Refactor the 5 saga handlers to call it instead of `unlockCache.TryGetAsync` directly. (This seam also makes the handlers testable without an unlock.)
4. **Lifetime.** Delete the grant when the saga reaches any terminal state (`succeeded`, `rolled_back`, `cancelled`, `failed_*`). Add a sweep mirroring `InfraOpUnlockSweepService` to GC grants whose saga is terminal or whose `expires_at` passed.
5. **Leave the interactive `RequireInfraOpUnlockFilter` endpoints untouched.**

### Threat-model note (must be in the ADR)

The "portal breach yields no usable provider access at rest" property (ADR-0030 / `[[auth_recovery_model]]`) must be preserved. The grant must be **sealed** (DataProtection, not plaintext), **bound to a single in-flight saga**, and **destroyed at terminal** — so the exposure window narrows from "any idle session/DB" to "while a provision is actively running." Document this as an explicit, bounded relaxation.

### Acceptance

- Provision a DO cloud, let the callback time out (90 min) **without** the user re-unlocking → the saga rolls back, `terraform destroy` runs, the droplet is gone, the job lands a clean terminal, **no orphan, no manual reset**.
- User-initiated cancel mid-apply still tears down.
- Breach simulation: with no in-flight saga, the grant store holds nothing usable.

---

## Part 2 — `IProvisioningProvider` strategy

### Problem

`Cloud.Provider` is a bare `string` (`Cloud.cs:11`). Provider behaviour is dispatched by comparing that string across ~19 sites, in three inconsistent spellings (literal `"digitalocean"`, `DigitalOceanTfEnv.DigitalOceanProvider`, `KnownProviders.DigitalOcean`). The `"stub"` test/local module (a `null_resource` in `terraform-modules/stub/main.tf`) is selected by string comparisons inside the production saga path, rather than being an implementation behind a port — i.e. the mock leaks into prod control flow. One site was cleaned this session (`RollingBackTfHandler.cs:57` now uses `KnownProviders.RequiresCredentials`).

### The dispatch sites (verified 2026-06-04), grouped by concern

- **Capability "needs credentials"** (`!= DO && != "stub"`): `RollingBackTfHandler.cs:68`, `CancelHandler.cs:172`, `TfApplyingHandler.cs:71`, `TfPlanningHandler.cs:63`.
- **"is DO" behaviour branches:** `CancelHandler.cs:301`, `RollingBackTfHandler.cs:206,312`, `TfApplyingHandler.cs:172,219`, `TfPlanningHandler.cs:173`, `MintingSpacesHandler.cs:40`, plus the const `DigitalOceanTfEnv.cs:8`.
- **Terraform module/provider-block selection:** `WorkspaceLayout.cs:39,148,153,164`.
- **API surface:** `CreateCloudEndpoints.cs:16` (`SupportedProviders`), `:33` (validation), `:40` (connect-required), `:64` (initial status); `PricingBackfillEndpoint.cs:34`.

### Design

`IProvisioningProvider` — one implementation per provider, resolved by a registry keyed on `Cloud.Provider`:

```csharp
interface IProvisioningProvider {
    string Key { get; }                                   // "digitalocean", "stub"
    bool RequiresCredentials { get; }                     // stub => false
    SagaStatus InitialStatusAfterCreate { get; }          // CreateCloudEndpoints:64
    bool SupportsPricing { get; }                         // PricingBackfillEndpoint:34
    string TerraformModuleSource { get; }                 // WorkspaceLayout selection
    Task BuildApplyEnvAsync(IDictionary<string,string> env, Cloud c, ...creds, CancellationToken ct);
    Task BuildDestroyEnvAsync(IDictionary<string,string> env, Cloud c, ...creds, CancellationToken ct);
    Task MintCredentialsAsync(Cloud c, ...creds, CancellationToken ct);   // DO mints Spaces; stub no-op
    Task RevokeCredentialsAsync(Cloud c, ...creds, CancellationToken ct); // DO revokes Spaces key + OAuth
}
```

- Implementations: `DigitalOceanProvisioningProvider` (absorbs `DigitalOceanTfEnv`), `StubProvisioningProvider` (`RequiresCredentials => false`, no-op mint/revoke, stub module). **The test double becomes a real implementation of the same port** — no handler ever names `"stub"`.
- `IProvisioningProviderRegistry.Get(string key)`; DI-register both in api + worker `Program.cs`. Keep `KnownProviders` as the key registry / `IsValid` source.

### Migration (incremental — keep the test suite green at each step; do NOT big-bang)

1. Define interface + registry + the two impls; register in DI. No call-site changes.
2. Migrate **one concern at a time**, each behind green tests: capability → apply env → destroy env → module selection → minting → initial status → pricing → revoke.
3. Delete `DigitalOceanTfEnv` and the remaining `"stub"`/`"digitalocean"` literals as each concern moves.

### Risk

This touches the working apply/plan/minting path. Use the Shouldly + xUnit v3 suite as a guardrail and migrate incrementally. Do **not** combine with Part 1 in a single change.

### Out of scope

Azure / Cloudflare providers — only `digitalocean` and `stub` exist at runtime today. The interface should *accommodate* them, but don't implement them here.

---

## Sequencing

Do **Part 2 first**: it produces clean `BuildDestroyEnvAsync` / credential seams that Part 1's `ISagaCredentialSource` plugs into. At minimum, define the credential-source seam before starting Part 1. Both land with the saga test suite green.

## References

- This session's fixes (2026-06-04): cloud-init `pipefail`, `DnsCreatingHandler` entry delay, `RollingBackTfHandler` A/B, `KnownProviders.RequiresCredentials`.
- ADR-0030 (auth flow / DEK envelope), ADR-0034 (cloud bootstrap + portal callback), ADR-0039 / `plans/portal-017-do-oauth-handoff.md` (DO OAuth + Spaces minting + `cloud_secrets` bundle).
- Memory: `[[auth_recovery_model]]`, `[[do_oauth_decision]]`, `[[portal_architecture]]`.
