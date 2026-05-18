# ADR-0041: Portal-side cloud-admin-token storage

Status: Accepted (PORTAL-014 + CLOUD-005 plugin-token-sync handoff)
Date: 2026-05-18

## Context

[[0034-cloud-bootstrap-and-portal-handshake]] §1 specifies that the cloud emits a
`cloud_admin_token` as part of its registration POST and that the portal persists
it. The original CLOUD-002 cut stored only a SHA-256 hash on
`clouds.cloud_admin_token_hash` — enough to *verify* a future presentation by the
cloud, but useless for outgoing portal→cloud calls (which need to present the
*plaintext* as `Authorization: Bearer <token>` to satisfy the cloud's
`RequireCloudAdminTokenFilter`).

The PORTAL-014 + CLOUD-005 handoff lands the first portal→cloud `/admin/*` call
(plugin-token sync, `POST /admin/plugin-tokens`). That makes the hash-only model
unusable: the saga's `IssuingPluginTokenHandler` needs the token in plaintext.
Question 28 (Q-AdminTokenStorage) in `plans/cloud-pivot-plan-2026-05-13.md` was
left open until this handoff resolved it.

## Decision

Store the cloud-admin-token as **ASP.NET Core DataProtection-encrypted plaintext**
on `clouds.encrypted_cloud_admin_token bytea`. Decrypt on demand via
`ICloudAdminTokenAccessor` when issuing portal→cloud admin calls. Drop the unused
`clouds.cloud_admin_token_hash` column.

- Encryption purpose string: `"cloud-admin-token:v1"`.
- Key ring: the same `DataProtection:KeyRingPath` already used by
  `EncryptedProviderTokens` and `StepUpUnlocks`. Both Portal.Api and
  Portal.SagaWorker mount the same path; both must register
  `ICloudAdminTokenAccessor` in DI.
- The plaintext is captured in `CloudCallbackEndpoints` at the moment the cloud
  registers, then never re-presented to the portal.

## Alternatives considered

1. **Keep the SHA-256 hash, accept no portal→cloud admin path.**
   Rejected: blocks all of PORTAL-014, including this handoff. No way to
   bootstrap the plugin-tokens table on the cloud.

2. **Store hash AND encrypted plaintext.**
   Rejected: the hash has no consumer. Verification of a token *presented to the
   portal by the cloud* is not a flow that exists — the portal does not accept
   inbound admin-token-bearing requests from the cloud. Carrying a second column
   is dead weight.

3. **Sign portal↔cloud calls with a JWT instead.**
   Rejected: requires a keypair, rotation policy, JWKS hosting on the portal,
   and verification logic on the cloud. The cloud-admin-token already works as
   a shared secret with a much smaller surface. Re-evaluate only if portal↔cloud
   calls need delegation or audience scoping.

4. **mTLS between portal and cloud.**
   Rejected: significantly more infrastructure (cert authority, rotation,
   per-cloud client certs, nginx mTLS config). The thesis-scope security
   posture treats `cloud_admin_token` + TLS as sufficient.

## Consequences

- Portal can issue `POST /admin/plugin-tokens` and any future `/admin/*` call by
  presenting `Authorization: Bearer <plaintext>` after decrypting via the
  accessor.
- Reuse pattern: `ICloudAdminTokenAccessor` + named HttpClient is the template
  for future portal→cloud admin proxies (audit-log fetch, settings updates,
  model switching).
- **Previously provisioned clouds** (smoke #5, #6) have no
  `encrypted_cloud_admin_token` populated and must be destroyed + re-provisioned
  to gain a working portal→cloud admin path. The acceptance criteria for the
  PORTAL-014 + CLOUD-005 handoff flags this explicitly.
- DataProtection key rotation (90-day default) — if a key expires while a
  cloud's ciphertext is still encrypted under it, `Unprotect` falls back to the
  archived key. Verify the key ring retains old keys; this applies equally to
  `EncryptedProviderTokens` (not new debt).
