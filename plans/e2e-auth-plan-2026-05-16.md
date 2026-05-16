# E2E Auth Golden Path — Parked Plan

Date: 2026-05-16
Status: **Parked mid-grill.** Q1–Q5 locked, Q6 open. Pick up by resuming Q6.

## Purpose

Plan for the Portal's first cross-process e2e test suite, scoped to auth. Covers the user-visible happy path through Google sign-in (bypassed), TOTP challenge, passphrase unlock, and a step-up-gated infra-op call. Not implementation; design decisions only.

## Where this sits in the test pyramid

```
Manual smoke / demo run           ← thesis defense
E2E (Playwright + Compose)         ← THIS PLAN — deployment-shape proof
Integration (WebApplicationFactory) ← in-process correctness (003c, 003d, 003e tests)
Unit (xUnit + Shouldly)            ← algorithmic correctness
```

**E2e proves**: deployment shape works, cross-process integration holds, SvelteKit ↔ Caddy ↔ Portal.Api ↔ Postgres chain has no wiring gap.

**E2e does NOT prove**: crypto correctness (Argon2id, AES-GCM), rate-limit window math, lockout threshold rollover, `OnValidatePrincipal` boundary cases, backup-code single-use guarantee, CSRF/XSS/SQLi resistance, markdown quality, entity dedup. Those belong to lower layers of the pyramid.

## Locked decisions

### Q1 — Purpose: (b) + (e)

- **(b) SPA layer** — exercise SvelteKit's auth UI (`fetchWithStepUp`, redirect on 401, TOTP challenge page, unlock modal). Integration tests can't touch this.
- **(e) Compose-stack parity** — run against the prod-shaped Docker Compose stack so deployment-shape bugs surface.

Explicitly de-scoped:
- (a) cross-process HTTP behavior (covered as a side effect of (b)+(e))
- (c) Caddy reverse-proxy layer (covered by (e))
- (d) Google OAuth callback path (covered by existing in-process `WithMockGoogleAuth` tests; re-proving it cross-process adds no real coverage)
- (f) first-defense smoke (falls out of (b)+(e) for free)

### Q2 — OAuth bypass: Pattern 1 + Pattern 3

- **Pattern 1** — test-only auth scheme behind `Environment=E2E` env-var gate. Reuses `GoogleSignInHandler.HandleAsync` so DB writes are real. Faked: only the IdP round-trip.
- **Pattern 3** — Playwright `storageState`. Sign-in happens once per state in `globalSetup.ts`, cookies dumped to JSON files, specs replay.

Industry references for this pattern combo: Cal.com, Grafana, NextAuth/Auth.js docs, Microsoft ASP.NET integration-test docs. Standard .NET pattern.

Rejected:
- Pattern 2 (mock OIDC container) — overkill; the Google handler is already covered by in-process tests
- Pattern 4 (real Google account) — Google actively breaks UI-driven login; non-starter

### Q3 — Spec shape: (c) sign-in + step-up + infra-op

Three independent auth journeys, three (groups of) specs:

1. **Sign-in** — fresh user → SPA Sign-in button → Pattern-1 bypass → authenticated dashboard. Covers TOTP claim transitions (`not-enabled` → `not-verified` → `verified` via `/totp-challenge`).
2. **Step-up unlock** — TOTP-verified user calls infra-op endpoint → 401 `step_up_required` → unlock modal → submit passphrase → 204 → retry → 204.
3. **Infra-op gated call** — fully-unlocked user calls gated endpoint → 204 within 10-min window.

Pattern 3 amortizes via two precomputed storage states:
- `state-totp-verified-locked.json` — signed in, TOTP-verified, DEK *not* unlocked
- `state-totp-verified-unlocked.json` — signed in, TOTP-verified, DEK in `IInfraOpUnlockCache`

Note: passphrase is **only** for infra ops (per ADR-0030 §"Step-up sequence"). It is NOT part of the sign-in flow. The naive "user signs in with passphrase" mental model is wrong — TOTP gates the *session*, passphrase gates *infra ops* within an authenticated session.

### Q4 — Step-up endpoint to hit: (b) test-only endpoint with PORTAL-015 TODO

`RequireInfraOpUnlockFilter` exists in `Features/Auth/StepUp/` but is **not attached to any production endpoint** (confirmed via grep on 2026-05-16). Infra-op endpoints land in M2 (PORTAL-007 onward); first one wired to the filter is PORTAL-015 (destroy flow).

Test-only endpoint, gated by `Environment=E2E`:

```csharp
if (builder.Environment.IsEnvironment("E2E"))
{
    // TODO(PORTAL-015): repoint e2e step-up spec at /api/clouds/{id}/destroy, delete this endpoint.
    app.MapPost("/api/test/ping-unlocked", () => Results.NoContent())
       .RequireAuthorization(AuthPolicies.TotpRequired)
       .AddEndpointFilter<RequireInfraOpUnlockFilter>();
}
```

TODO marker convention matches existing precedent in `Program.cs` (`TODO(PORTAL-017): PersistKeysToFileSystem ...`).

### Q5 — Postgres state management: (c) namespace by user

- One Postgres container per CI run (in the E2E Compose stack).
- Each spec creates its own test user with a random UUID via `/api/test/seed-user`.
- No truncation, no rollback. State accumulates within a run; Postgres yawns at the volumes.
- Storage states in `globalSetup` use their own user IDs, namespaced.

Seed endpoint shape (single endpoint, options-driven):

```csharp
if (builder.Environment.IsEnvironment("E2E"))
{
    app.MapPost("/api/test/seed-user", async (SeedUserRequest req, PortalDbContext db, /* ... */) =>
    {
        // Insert User row, optional TotpSecret row, optional passphrase columns.
        // Returns the seeded user id + (if totp enabled) the plaintext secret so the test can compute codes.
    });
}
```

Rate-limit (003c) and lockout (003d) partitions are per-`sub-us`; random UUIDs make collision astronomically unlikely. No coordination needed.

Industry references: Cal.com, Linear, Vercel dashboard, Sentry — all namespace-by-entity rather than wipe state.

## Open questions (pick up here)

### Q6 — Compose shape + TLS handling

Cookie attribute `Cookie.SecurePolicy = CookieSecurePolicy.Always` (Program.cs:80) means cookies only delivered over HTTPS. E2e must either run over HTTPS or override the policy.

**Compose shape:**
- **A.1** Same `docker-compose.yml` with env-var differences
- **A.2** Separate `docker-compose.e2e.yml` extending the base via `-f`

**TLS handling:**
- **B.1** Skip Caddy in E2E; override `SecurePolicy = SameAsRequest`
- **B.2** Caddy with `tls internal` (self-signed via Caddy's built-in CA); Playwright with `ignoreHTTPSErrors: true`
- **B.3** Caddy with Cloudflare DNS-01 + real test subdomain

**Recommended: A.2 + B.2.** Separate Compose file keeps prod artifact pristine; `tls internal` preserves cookie-attribute parity with prod without external dependencies.

Sub-question: hostname for the E2E stack — `portal.local` (suggested default), `localhost`, `e2e.test`?

### Q7 — TOTP secret handling in tests (not yet asked)

How does the test compute the 6-digit TOTP code at challenge time?
- (a) Seed endpoint returns the plaintext secret; test uses `Otp.NET` (or a JS equivalent like `otplib`) to compute codes
- (b) Seed endpoint pre-computes a window of valid codes and returns them
- (c) Bypass `/totp-challenge` entirely in seed flow by signing the user in directly with `totp=verified` claim

**Recommended: (a)** — exercises the real `/totp-challenge` handler. The seed endpoint generates the secret server-side (real `TotpService.GenerateSecret()`), persists encrypted, returns plaintext to the test fixture only (E2E-gated, never in prod). Test uses `otplib` (~5kb npm package) to compute codes.

### Q8 — Compose orchestration trigger (not yet asked)

Where does `docker compose up` happen?
- Playwright `globalSetup.ts` orchestrates Compose
- CI workflow brings up Compose, then runs Playwright as a separate step
- A dev script (`pnpm e2e`) does both for local runs

**Recommended:** CI workflow brings up Compose (parallel-safe, fail-fast). Local convenience script wraps the same commands. Playwright assumes the stack is up — does not orchestrate it itself.

### Q9 — Sliding-window expiry test (not yet asked)

Does the e2e exercise the 10-minute unlock-cache idle expiry?
- (a) Yes — `FakeClock` injected via E2E config; test advances clock
- (b) No — leave to integration tests; e2e covers the within-window happy path only

**Recommended: (b).** Window math is integration-test territory (per the pyramid). E2e proves the *mechanism* works (unlock → cached → next call succeeds); the *boundary* is unit/integration test territory.

### Q10 — CI venue (not yet asked)

GitHub Actions? Local-only first? Both?

**Recommended:** Both. Local-first to validate the harness; GitHub Actions follows once the local run is stable. Same `docker compose -f` invocation in both.

### Q11 — Test data lifecycle (not yet asked)

Storage-state JSON files: committed to repo or regenerated each run?
- (a) Committed — stable artifacts, fast spec startup, but drift risk if test scheme changes
- (b) Regenerated by `globalSetup` every run — always fresh, ~5s overhead

**Recommended: (b).** Regen is cheap, eliminates drift risk, matches Playwright's intended use.

## Future-test scaling notes

The Pattern 1 + seed endpoint + Compose + namespace-by-user shape **scales** to all future Portal e2e:

| Future test | Net-new infrastructure needed |
|---|---|
| Adversarial auth (429, 423, captcha) | None — same scheme, more specs |
| Recovery codes (PORTAL-006) | Extend seed endpoint to populate `recovery_codes` rows |
| Provisioning (M2 PORTAL-007+) | Stub `ITerraformRunner` in E2E DI |
| Cloud admin proxy (PORTAL-014) | WireMock container OR fake `cloud-api` in Compose |
| Cross-service M5 demo | Stub LLM (WireMock or `IOllamaClient` stub); add `cloud-api` container to Compose |
| Cloudflare DNS (PORTAL-010b) | Stub `ICloudflareDnsClient` in E2E DI |

**Underlying principle:** every external boundary needs a C# interface; E2E swaps the impl via `Environment=E2E` registration. The Portal's VSA conventions per [[portal_architecture]] already lean this way. As long as new code follows the interface-at-boundary pattern (the 003d brief does), E2E extension cost is "add one stub."

**Pin as coding standard before M2 lands:** any new "calls something outside the process" code path must go through an interface so E2E can stub it.

## What this plan deliberately does not cover

- **Plugin e2e** — driving Obsidian itself via Playwright is fragile. Instead: e2e the cloud-api the plugin calls + unit-test the plugin's vault interactions. Not in this plan.
- **Load tests** — Postgres queue concurrency, rate-limit accuracy under burst. Different tooling (k6, NBomber); different concerns.
- **Security tests** — XSS, CSRF, SQLi enumeration. Security-review territory, not e2e.
- **Eval harness** — markdown quality, entity dedup P/R, routing accuracy. M9 thesis-evaluation chapter, not e2e.

## Concrete dependencies to land before this plan can execute

None of these block the *design*; they block the *implementation*:

1. **`Environment=E2E` gate machinery** — conditional service registration in Program.cs (5 LOC of `if`-block).
2. **Test-only auth scheme** (`Features/Auth/E2ETestScheme/`) — ~30 LOC handler + ~10 LOC registration.
3. **Test-only seed endpoint** (`Features/Auth/E2ETestScheme/SeedEndpoints.cs`) — ~50 LOC across user seeding and the `/api/test/ping-unlocked` step-up smoke endpoint.
4. **Startup assertion** — `if (env.IsProduction() && hasE2ESurfaces) throw` to prevent accidental prod leak.
5. **`docker-compose.e2e.yml`** + `Caddyfile.e2e` — ~30 lines total.
6. **Playwright project scaffold** — `tests/e2e/` with config, `globalSetup.ts`, 3 spec files. ~200 LOC.

Total implementation effort once design is closed: **~1 person-day** for harness + 3 golden-path specs.

## Cross-references

- ADR-0030 (auth flow) §"Sign-in sequence" and §"Step-up sequence" — the flows under test
- ADR-0031 (rate-limiting + lockout) — adversarial extension specs land after 003d/003e
- ADR-0023 (test stack) — `WebApplicationFactory` for integration tests; e2e is the cross-process complement
- ADR-0027 (Caddy reverse proxy) — TLS termination shape that B.2 mirrors via `tls internal`
- `plans/portal-003-handoff.md` §"Test infrastructure" — existing `TestAuthHandler.cs` is the in-process precedent for Pattern 1
- `plans/portal-003c-handoff.md` — rate limiter that adversarial e2e will exercise once 003d ships
- `plans/portal-003d-handoff.md` — lockout layer; e2e for 423 boundary lands after 003d implementation
- `tickets-2026-05-13.md` — M2 ticket sequence; PORTAL-015 is the TODO target for Q4

## How to resume

1. Decide Q6 (Compose shape + TLS) — recommend A.2 + B.2.
2. Walk Q7–Q11 with the same grill cadence; recommendations in the open-questions section above are defensible defaults.
3. Once all questions closed, write the implementation handoff brief (`plans/e2e-auth-handoff.md`) in the same shape as the PORTAL-003x briefs.
4. Sequence the work: do not implement before 003d ships (e2e for 003d's 423 boundary is in scope for the adversarial follow-on, but the golden-path suite doesn't depend on 003d).
