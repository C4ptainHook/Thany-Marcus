# PORTAL-016 Cloud bootstrap callback endpoint — Handoff Brief

**Goal:** finish the portal side of the cloud→portal registration handshake so the PORTAL-011 smoke walks `dns_creating → awaiting_cloud_callback → awaiting_cert → succeeded` end-to-end on real DO infrastructure. The HTTP endpoint already exists (`POST /api/clouds/{cloudId}/callback` in `Features/CloudManagement/Callback/CloudCallbackEndpoints.cs`); this ticket lands the **three deltas** that prevent the smoke from passing: (1) bump the saga's `AwaitingCloudCallback` timeout from 5 min to 15 min per ADR-0034 §4, (2) add the missing endpoint integration tests covering all eight response paths from ADR-0034 §1, (3) decide and document the `cloud_admin_token` storage shape (current code stores SHA-256 hash; ADR-0034 §1 + §Consequences specifies plaintext; the deviation is real and has knock-on consequences for PORTAL-014). After this ticket, the bootstrap handshake is tested, the saga waits long enough for the slow-LE path, and a future PORTAL-014 reader knows what to expect.

Estimated **0.5 person-day**. The endpoint is already wired; this is a hardening + cross-ref + decision pass, not a build-from-scratch.

## Where decisions live (read before doing anything)

- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — the canonical contract. §1 pins the POST shape, §3 pins the portal poll cadence (consumed by `AwaitingCertHandler`, not changed here), §4 pins the cloud-side retry budget and explicitly raises `awaiting_cloud_callback` timeout 5→15 min as part of CLOUD-001. The bump did not land on the saga side; this ticket lands it.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the state machine that consumes the callback. `AwaitingCloudCallback` is the state that is held until the POST lands; `AwaitingCert` is the next state. No new states here.
- **`plans/cloud-001-handoff.md`** — the cloud-side counterpart. The cloud's `PortalCallbackService` POSTs to `/api/clouds/{cloudId}/callback` with `{cloud_id, enrollment_token, cloud_admin_token}`. CLOUD-001's cross-reference note (under "Saga timeout bump") explicitly defers the 5→15 min change to PORTAL-016.
- **`plans/portal-007-handoff.md`** — defines `SagaStatus`, the dispatcher, `EnqueueGuard`, the saga rescheduling primitives. The callback endpoint is the ONLY external trigger that flips `AwaitingCloudCallback → AwaitingCert` via `pg_notify('provisioning_new', ...)`.
- **`plans/portal-011-handoff.md`** — the wizard / SSE side. The wizard subscribes to `/api/clouds/{cloudId}/events` SSE and surfaces phase transitions. No wizard changes required; the existing `awaiting_cert` SSE event already renders.
- **`plans/portal-011-smoke-runbook.md`** — the runbook that drives this end-to-end. After PORTAL-016 + PORTAL-010b land, this is what proves the system.
- **Memory files**: `portal_architecture.md`, `portal_tooling.md` (Shouldly + xUnit v3; warnings-as-errors; OpenAPI + Scalar).

**Do not invent new saga statuses.** ADR-0033 is the contract; the callback ONLY participates in `AwaitingCloudCallback → AwaitingCert` (or stays put / 4xx on validation failure).

## Scope boundary (precise)

**In scope:**

- **Saga timeout bump** (the smoke-unblock). `Portal.SagaWorker/Features/Provisioning/Handlers/AwaitingCloudCallbackHandler.cs:15`:
  ```csharp
  private static readonly Duration CallbackTimeout = Duration.FromMinutes(15);  // was 5
  ```
  Also update `Portal.SagaWorker/Features/Provisioning/Handlers/DnsCreatingHandler.cs:78` if it threads through (`AwaitingCallbackTimeout` constant) — verify it's the same value source; do not duplicate the literal. The phase-started-vs-now math in the handler is unchanged; only the constant moves.

- **Endpoint integration tests** under `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Callback/` (new directory) — file `CloudCallbackEndpointTests.cs`. xUnit v3 + Shouldly + `PostgresFixture` + `WebApplicationFactory<Program>` per the existing pattern in `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Create/CreateCloudEndpointTests.cs`. Cover the **eight response paths** from ADR-0034 §1, each as a discrete test:
  1. `BadRequest` — `body.CloudId` ≠ `{cloudId}` in route → 400 `{ error: "cloud_id_mismatch" }`.
  2. `Unauthorized` (token length) — `EnrollmentToken` length ≠ 64 → 401.
  3. `BadRequest` (missing admin token) — empty `CloudAdminToken` → 400 `{ error: "missing_cloud_admin_token" }`.
  4. `NotFound` (no cloud) — `cloudId` doesn't exist → 404 `{ error: "cloud_not_found" }`.
  5. `NotFound` (no job) — cloud exists, no create job → 404 `{ error: "no_create_job" }`.
  6. `Unauthorized` (token mismatch) — same length, wrong bytes → 401. **Must verify constant-time compare** by checking the response is identical to path #2 (no early-exit timing oracle).
  7. `Conflict` — job is in `TfPlanning` or any non-`AwaitingCloudCallback` non-terminal state → 409 `{ error: "wrong_state", current: <status> }`.
  8. `Ok` (success) — job in `AwaitingCloudCallback`, token matches → 200 `{ ok: true }`. Assert: `job.Status == AwaitingCert`, `cloud.CloudAdminTokenHash` populated (≠ null), `cloud.AdminStartedAt` set, `job.EventsLog` contains a `cloud_registered` entry, `pg_notify` fires (assert via `LISTEN provisioning_job_changed` in a side connection).
  9. (bonus) `Ok` (idempotent) — same call replayed when job is `AwaitingCert` → 200 `{ idempotent: true }`. Asserts no double-write of `AdminStartedAt`.

  Use the `WebApplicationFactory<Program>` pattern from `CreateCloudEndpointTests.cs`; do **not** spin up the saga worker — the endpoint must work in isolation. Rate limiting is enabled by default in the factory; use distinct cloud IDs per test to avoid cross-test interference on `CloudCallbackPolicies.PerCloudId` (12 req / 5 min limit).

- **Hash-vs-plaintext token storage** — document the deviation, do **not** fix it in this ticket. The current code stores `cloud.CloudAdminTokenHash = SHA256.HashData(...)` at `CloudCallbackEndpoints.cs:65`. ADR-0034 §1 + §Consequences specifies plaintext. The two diverge because:
  - Plaintext at rest enables PORTAL-014 (portal → cloud `/admin/*` proxy) to present the token in `Authorization: Bearer <token>`. The cloud-side stores the same plaintext and verifies via constant-time compare. With hash-only storage on the portal side, PORTAL-014 cannot construct a valid request — the cloud has no way to verify a hash unless the cloud also pre-hashes (and the cloud doesn't).
  - Hash-only storage on the portal side is what the current code does and is **fine for the smoke**: the smoke only requires `AwaitingCloudCallback → AwaitingCert`, which the current implementation does correctly. PORTAL-014 is not on the smoke path.
  - **Action for this ticket:** add a `// FORK:` block-comment marker (one of the rare exceptions to "no comments" — this is a non-obvious cross-ticket invariant the next reader must see) at `CloudCallbackEndpoints.cs:65` pointing at PORTAL-014 + ADR-0034 §1 mismatch, AND open a new entry in `plans/cloud-pivot-plan-2026-05-13.md` §28 "Tier-B open questions" naming the decision: "Q-AdminTokenStorage — hash vs plaintext vs DataProtection-encrypted on the portal side." Do not change schema, code paths, or migrations.

- **OpenAPI annotation parity** — the endpoint currently has no `.Produces<>` / `.ProducesProblem` annotations, so the generated `.NET` → TS contract for the wizard cannot reason about it. Add the standard annotations (`.WithName("PostCloudCallback")`, `.Produces<CloudCallbackOkResponse>(StatusCodes.Status200OK)`, `.ProducesProblem(StatusCodes.Status400BadRequest)`, `.ProducesProblem(StatusCodes.Status401Unauthorized)`, `.ProducesProblem(StatusCodes.Status404NotFound)`, `.ProducesProblem(StatusCodes.Status409Conflict)`) and define `CloudCallbackOkResponse` (one record discriminating `ok` vs `idempotent`). The endpoint is unauthenticated and called by the cloud (not by the SPA), so **no TS contract file is generated for it** — the OpenAPI annotations exist purely for documentation/Scalar visibility, not for `Portal.Web` types.

**Out of scope (named explicitly so it doesn't sneak in):**

- ❌ Hash → plaintext migration. Captured as a Tier-B open question (above); fixed in PORTAL-014's ticket or a dedicated follow-up. Do **not** change the schema, do not write a migration.
- ❌ "Retry from portal" UI surface. ADR-0034 line 91 names this requirement; it belongs in a PORTAL-016a or in PORTAL-011 polish, not here. (Rationale: PORTAL-016 is the *endpoint*; user-facing retry is a wizard concern. Bundling them blurs which side broke when smoke fails.)
- ❌ Changes to `AwaitingCertHandler`. Even though `StubCloudAdminToken = "stub-cloud-admin-token"` (line 27, 86) is wrong per ADR-0034 §2 ("`/admin/health` requires no auth"), removing it is harmless cleanup unrelated to PORTAL-016 — schedule as a one-line follow-up. If you touch it incidentally while reviewing, fine; do not extend scope to refactor the handler.
- ❌ Changes to `PortalCallbackService` on the cloud side. CLOUD-001 owns the cloud side; this ticket is portal-only.
- ❌ Rate-limit policy tuning. `CloudCallbackPolicies.PerCloudId` = 12 req / 5 min and `PerIp` = 60 req / 5 min are inherited; CLOUD-001's exponential backoff (8 attempts over ~4 min) stays well under both. If the smoke surfaces a rate-limit hit, that's a bug in CLOUD-001's dedup gate, not a PORTAL-016 concern.
- ❌ Migrations. The `cloud_admin_token_hash bytea?` column already exists. No schema change in this ticket.

## File map

```
Thany-Marcus/
├── docs/decisions/
│   └── 0034-cloud-bootstrap-and-portal-handshake.md         # (read-only) the contract
├── plans/
│   ├── portal-016-handoff.md                                # THIS FILE
│   └── cloud-pivot-plan-2026-05-13.md                       # CHANGED: add Tier-B Q-AdminTokenStorage
├── src/ThanyMarcus.Portal.Api/
│   └── Features/CloudManagement/Callback/
│       ├── CloudCallbackEndpoints.cs                        # CHANGED: + OpenAPI annotations; + FORK comment at line 65
│       └── CloudCallbackOkResponse.cs                       # NEW: discriminated record for OpenAPI typing
├── src/ThanyMarcus.Portal.SagaWorker/
│   └── Features/Provisioning/Handlers/
│       ├── AwaitingCloudCallbackHandler.cs                  # CHANGED: CallbackTimeout 5 → 15 min
│       └── DnsCreatingHandler.cs                            # VERIFY: AwaitingCallbackTimeout constant references the same source (no duplicate literal)
└── tests/ThanyMarcus.Portal.Tests/
    └── Features/CloudManagement/Callback/                   # NEW directory
        └── CloudCallbackEndpointTests.cs                    # NEW: 9 test cases (8 response paths + idempotency)
```

## State machine — interaction surface

The callback endpoint participates in exactly one transition. ADR-0033 fragment relevant here:

```
                       ┌──────────────────────────┐
                       │  awaiting_cloud_callback │ ◄── handler reschedules every 5 s
                       └────────────┬─────────────┘     until timeout (15 min)
                                    │
            cloud POSTs ─────────►  │  CloudCallbackEndpoints flips status
            /api/clouds/{id}/       │  in SERIALIZABLE tx, pg_notify wakes
            callback                │  the saga immediately
                                    ▼
                       ┌──────────────────────────┐
                       │      awaiting_cert       │
                       └──────────────────────────┘
```

**Failure-path interactions (already implemented, do not change):**

- Timeout (15 min, post-bump) → `AwaitingCloudCallbackHandler` transitions to `RollingBackDns`. The cloud is up, has its admin token, but never reached the portal (cloud-init crashed / network partition / cert never issued so Caddy never fired the `cert_obtained` event). The user sees `failed_callback` after rollback completes.
- Late callback (cloud POSTs after the 15-min timeout) → endpoint hits the `Conflict` path (job.Status is `RollingBackDns` or later), returns 409. Cloud-side `PortalCallbackService` treats 4xx as permanent and stops retrying. Acceptable: the cloud is orphaned in DO, the portal has rolled back; user re-provisions.
- Replay attack with stale `enrollment_token` → endpoint returns 401 via constant-time compare. The token is single-use per provisioning job; once the job leaves `AwaitingCloudCallback`, all future POSTs hit the `Conflict` branch first (state check before token compare in the current code flow — verify this ordering in the test for path #7).

## Acceptance criteria

1. ✅ `AwaitingCloudCallbackHandler.CallbackTimeout` is `Duration.FromMinutes(15)`. The constant is referenced (not literal-duplicated) anywhere it appears.
2. ✅ `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Callback/CloudCallbackEndpointTests.cs` exists and contains the nine test cases listed in "Scope boundary" point 2. All pass under `dotnet test`.
3. ✅ The `cloud_registered` `events_log` entry written on success path is asserted in the success test (path #8) — both the entry's presence and that `pg_notify('provisioning_new', ...)` fires.
4. ✅ `CloudCallbackEndpoints.cs` has `.Produces<>` / `.ProducesProblem` annotations and a `CloudCallbackOkResponse` record. Scalar UI at `/scalar/v1` shows the endpoint with all four response shapes (200, 400, 401, 404, 409).
5. ✅ The `// FORK:` marker at the hash-storage site at `CloudCallbackEndpoints.cs:65` cross-references ADR-0034 §1 and PORTAL-014. The marker is the rare comment-allowed exception per CLAUDE.md memory `feedback_no_code_comments`: it captures a non-obvious cross-ticket invariant.
6. ✅ `plans/cloud-pivot-plan-2026-05-13.md` §28 contains a new entry "Q-AdminTokenStorage" describing the hash-vs-plaintext fork and naming PORTAL-014 as the consumer that forces a decision.
7. ✅ `dotnet test` is green. Existing `AwaitingCloudCallbackHandlerTests` may need its expected timeout updated; if so, do that.
8. ✅ The PORTAL-011 smoke (per `plans/portal-011-smoke-runbook.md`) walks past the `awaiting_cloud_callback` phase to `awaiting_cert` end-to-end. **The smoke also requires PORTAL-010b**; PORTAL-016 alone is not sufficient to make the smoke pass, but its absence guarantees the smoke fails. Run the smoke as the final acceptance check **after both tickets land**.

## Risks and gotchas

- **Test parallelism on rate limits.** `CloudCallbackPolicies.PerCloudId` partitions on the route value `cloudId`. xUnit v3's per-class parallelism is fine because each test seeds its own cloud (its own Guid). xUnit-level parallelism across classes could collide on `PerIp` (60 req / 5 min from the test host). If you see flaky 429s, configure `CollectionDefinition` to serialize the callback tests OR scope the test host's `RemoteIpAddress` to a synthetic per-test IP via test-server hook. Try the simpler fix first.
- **`pg_notify` assertion.** Asserting that `pg_notify('provisioning_job_changed', ...)` fires requires opening a side `NpgsqlConnection` with `LISTEN provisioning_new` *before* the POST, then awaiting the notification with a small timeout (e.g. 2 s). The pattern is in `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Create/CreateCloudEndpointTests.cs` if you want a reference.
- **`IgnoreQueryFilters`.** The endpoint uses `db.Clouds.IgnoreQueryFilters()` so a soft-deleted-then-resurrected cloud is visible (defensive). Tests should NOT seed soft-deleted clouds; if a future bug requires distinguishing live-vs-destroyed, the test set expands, but for ADR-0034 §1's contract the IgnoreQueryFilters is purely defensive against a state we don't expect.
- **Constant-time compare validation.** Test path #6 (token mismatch with correct length) cannot directly measure timing (test-infrastructure noise > the timing gap we care about). The acceptable proxy: assert the response code + body shape are byte-for-byte identical to a length-mismatch case, and rely on code review of `CryptographicOperations.FixedTimeEquals` at `CloudCallbackEndpoints.cs:52`. Do not write timing-based tests; they will flake.
- **The "FORK:" comment.** This is the *only* code comment this ticket adds. CLAUDE.md memory `feedback_no_code_comments` forbids narrative comments; this one is justified because the deviation crosses three documents (ADR-0034, current code, future PORTAL-014) and is not derivable from local reading. If a reviewer pushes back, refer them to the memory's "non-obvious why" carve-out.
- **Wizard SSE.** The wizard already renders `awaiting_cert` (per `Portal.Web/src/routes/clouds/[id]/+page.svelte:26`). No changes required, but verify visually during smoke that the wizard transitions UI state on the POST, not at the next saga tick — the `pg_notify` should propagate the SSE within ~1 s.
- **Idempotent calls and `AdminStartedAt`.** The success path sets `cloud.AdminStartedAt = now`. The idempotent path (job already `AwaitingCert`) skips this assignment correctly because it `return`s before the state-change block. The test for path #9 should assert `AdminStartedAt` is unchanged between the first and second call.

## Cross-references and follow-ups (NOT in this ticket)

- **PORTAL-014** consumes `cloud.CloudAdminTokenHash`/-plaintext directly. When it lands, the hash-vs-plaintext fork must be resolved. Whoever owns PORTAL-014 should read the `FORK:` marker and the Tier-B Q-AdminTokenStorage entry.
- **PORTAL-016a (proposed)** — "Retry from portal" UI surface. The wizard offers a `Resend bootstrap callback` button on `failed_callback` jobs that re-arms the job (resets `PhaseStartedAt`, status back to `AwaitingCloudCallback`, generates a fresh `enrollment_token`, calls a new endpoint to push the token down to the still-running cloud). This is mid-sized work (the "push token down" path doesn't exist yet); not in PORTAL-016.
- **PORTAL-010b** — Cloudflare DNS handoff. Companion smoke-unblock. The smoke requires both. Tracked separately.
- **`AwaitingCertHandler.StubCloudAdminToken` cleanup** — one-line follow-up; not blocking smoke (ADR-0034 §2 says `/admin/health` ignores the header).

## What "done" looks like

Run the smoke from `plans/portal-011-smoke-runbook.md` after PORTAL-010b is also in. Expected log sequence in the wizard SSE stream:

```
queued → tf_planning → tf_applying → dns_creating → awaiting_cloud_callback
  ↓                                                   ↓ (cloud POSTs after Caddy emits cert_obtained)
                                                   awaiting_cert
                                                      ↓ (portal polls /admin/health every 5–30 s)
                                                   succeeded
```

The droplet stays up. The Cloudflare A-record stays. The user lands on the dashboard's "ready" state. No `failed_*` terminal.
