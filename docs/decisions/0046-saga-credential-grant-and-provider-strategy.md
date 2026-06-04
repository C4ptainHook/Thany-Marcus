# ADR-0046: Saga-scoped credential grant and provider strategy

Status: Accepted (implemented 2026-06-04)
Date: 2026-06-04

## Context

Two related refactors of the provisioning saga's provider/credential layer,
both surfaced by a 2026-06-04 incident: a DigitalOcean provision reached
`tf_applying` (droplet created) but the cloud-init callback never fired, so the
create saga waited out the full 90-minute `awaiting_cloud_callback` timeout and
then entered `rolling_back_tf` to destroy the droplet. By that point the user's
interactive step-up unlock (60-minute sliding TTL, [[0030-auth-flow]]) had
expired, so the rollback could not decrypt the DO credentials, `terraform
destroy` never ran, and the droplet was orphaned.

The DEK-envelope model ([[0030-auth-flow]]): a per-user data-encryption key is
unwrapped from the passphrase at `POST /api/auth/unlock` and cached, sealed, in
`step_up_unlocks` with a 60-minute sliding TTL. The saga inherits the unwrapped
DEK transiently through that cache. The DEK decrypts the DO OAuth access token
and the per-cloud Spaces secret bundle (`plans/portal-017-do-oauth-handoff.md`)
— both needed to build the `terraform` environment.

The mismatch is structural: a saga's lifetime (`awaiting_cloud_callback` alone
is 90 min, plus rollback retries) **exceeds** the 60-minute unlock TTL. Any
rollback triggered by the callback timeout is *guaranteed* to run after the
unlock has expired, unless the user happens to be present and re-unlocks. A
provision must not depend on the user staying logged in for two hours.

Separately, provider behaviour was string-dispatch on a bare `Cloud.Provider`
across ~19 sites in three spellings, and the `"stub"` test double was selected
by string comparison inside the production saga path — the mock leaked into
production control flow.

## Decision

### Part 1 — Saga-scoped credential grant (saga-bound sealed DEK)

At authorization (where the user is present and unlocked, behind
`RequireInfraOpUnlockFilter`), copy the live DEK into a **saga-scoped grant**
keyed by `cloud_id`, **sealed at rest** with DataProtection (the `dp_keys`
ring is already shared api↔worker), with a lifetime equal to the saga's hard
deadline rather than the 60-minute interactive TTL.

- **Storage.** `saga_credential_grants(cloud_id PK/FK→clouds, sealed_dek bytea,
  created_at, expires_at)`. `sealed_dek` is sealed with the purpose-scoped
  protector `saga-credential-grant.v1`, so a DB-only breach yields nothing
  usable. `expires_at = now + 6h` — a generous backstop covering the worst-case
  create+rollback (~90 min callback + capped retries) with margin for worker
  restarts.
- **Capture.** `ISagaCredentialSource.CaptureForSagaAsync` reads the live
  interactive unlock and seals it into the grant. Called from the create,
  cancel, and destroy endpoints, inside the same serializable transaction as
  the cloud/job write, so the grant is durable before the worker can pick up
  the job.
- **Read seam.** `ISagaCredentialSource.TryGetDekAsync(cloud, dek, ct)` prefers
  the live interactive unlock and **falls back to the saga grant**. The five
  DEK-consuming saga handlers (`TfPlanning`, `TfApplying`, `MintingSpaces`,
  `RollingBackTf`, `Cancel`) call it instead of the unlock cache directly.
- **Lifetime.** The grant is deleted when the saga reaches any terminal state —
  at the single chokepoint `SagaTransitions.TransitionToTerminalAsync` and in
  `CancelHandler.FinalizeCancelledAsync` (the one terminal path that bypasses
  it). A `SagaCredentialGrantSweepService` (mirroring `InfraOpUnlockSweepService`)
  GCs grants whose `expires_at` has passed or whose cloud has reached a terminal
  saga state — a backstop for crashed processes.
- The interactive `RequireInfraOpUnlockFilter` endpoints are unchanged: the user
  is present there and the unlock guard is correct.

#### Threat-model note (the bounded relaxation)

ADR-0030's property — *a portal breach yields no usable provider access at
rest* — is preserved, with one explicit, bounded relaxation. The grant is:

1. **Sealed** with DataProtection, never plaintext at rest. A DB-only breach
   (no `dp_keys`) yields nothing.
2. **Bound to a single in-flight saga** (`cloud_id` PK), not the user. It unlocks
   one cloud's provider access, not "everything" the user can do.
3. **Destroyed at terminal** and swept on expiry/terminal-cloud.

The exposure window therefore narrows from "any idle authenticated session or DB
snapshot for 60 min" to "while a provision is actively running for this one
cloud." Option C from the handoff (extending the per-*user* unlock TTL while a
saga is in flight) was rejected: it keeps *everything* the user can do unlocked
far longer than they expect — a security regression. Option B (sealing only the
specific provider creds rather than the DEK) has a narrower blast radius but is
complicated by Spaces keys being minted mid-saga (`MintingSpaces`) and by DO
OAuth-token refresh; Option A (seal the DEK once) covers both the OAuth token
and the later-minted bundle with a single write and is the pragmatic choice
given a single provider.

### Part 2 — `IProvisioningProvider` strategy

`IProvisioningProvider`, resolved by `IProvisioningProviderRegistry` keyed on
`Cloud.Provider`, captures all provider variance: `RequiresCredentials`,
`MintsObjectStorageCredentials`, `SupportsPricing`, `UserCreatable`,
`InitialStatusAfterCreate`, `AddProvisioningEnvAsync`, and
`RevokeCredentialsAsync`. `DigitalOceanProvisioningProvider` absorbs the old
`DigitalOceanTfEnv`; `StubProvisioningProvider` is a real implementation of the
same port (`RequiresCredentials => false`, no-op mint/revoke) — **no handler
names `"stub"`**. Terraform module/HCL selection lives in a static
`ProviderTerraformCatalog` (the only consumer is the singleton `WorkspaceLayout`,
which cannot take a scoped dependency). `KnownProviders` remains the canonical
key set / `IsValid` source.

Spaces-key minting itself stays in `MintingSpacesHandler` (gated by
`provider.MintsObjectStorageCredentials`) rather than moving into the provider:
the activation probe is AWS-SDK-dependent and lives in the worker, while the
providers live in `Portal.Api` (referenced by both processes) — pushing the
probe into a provider would invert that reference.

## Consequences

- A provision rolls back and `terraform destroy` runs across the saga's full
  lifetime without the user being present or re-unlocked — no orphan, no manual
  cloud/job reset. (The operator runbook note from the handoff is obsolete.)
- Azure / Cloudflare cloud providers are accommodated by the strategy but not
  implemented here; only `digitalocean` and `stub` exist at runtime.
- The grant is one more sealed secret at rest; the threat-model note above
  bounds its exposure and is the accepted trade-off.

## References

- [[0030-auth-flow]] (DEK envelope / step-up unlock), [[0033-provisioning-saga-and-worker]],
  [[0034-cloud-bootstrap-and-portal-handshake]], `plans/portal-017-do-oauth-handoff.md` (DO OAuth + Spaces minting).
- `plans/provisioning-provider-and-saga-credentials-handoff.md`.
