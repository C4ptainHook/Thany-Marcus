# ADR-0031: Rate limiting + persistent lockout + CAPTCHA

Status: Accepted
Date: 2026-05-15

## Context

Three auth endpoints expose brute-force surfaces:

- **`/totp-challenge` (POST)** — TOTP codes have a 10⁶ search space. Without rate limits, an attacker who somehow has the partial-auth cookie can exhaust the space in under a minute over LAN.
- **`/api/auth/unlock` (POST)** — passphrase verification. Argon2id slows each try to ~250 ms by design, but that's still ~12 k tries/hour without limits. A 7-character common-words passphrase falls within hours.
- **`/api/auth/recovery-codes/redeem` (POST)** — recovery codes have ~40 bits of entropy (8 Crockford-alphabet characters), and a successful redemption fully unlocks the account (passphrase substitute). Higher blast radius than TOTP.

Plus general bot abuse on the `/signin-google` callback (unauthenticated).

`tickets-2026-05-13.md` did not specify a rate-limit / lockout strategy. PORTAL-002 schema needs to know whether durable-lockout state needs its own table.

CAPTCHA was also out-of-spec but came up during the auth grilling as a real-product hardening worth deciding now (Cloudflare account already in stack for DNS, per DEC-003 / [[portal_architecture]]).

## Options considered

- **A. Column-based persistent lockout on `users`.** `failed_*_count`, `*_locked_until` per kind. Survives process restart. Forever-locked-out accounts become an ops burden.
- **B. In-memory rate limiting via `Microsoft.AspNetCore.RateLimiting`.** Built-in ASP.NET Core middleware since .NET 7. `PartitionedRateLimiter` keyed by user id (when authenticated) or IP. Resets on process restart.
- **C. Hybrid — in-memory throttle + persistent lockout for sustained attacks.** E.g., 5 failed attempts in 5 min → in-memory throttle for 15 min; sustained failures across windows → write `locked_until` to DB. Industry-standard for consumer apps.
- **D. No rate limiting in MVP.** Wrong — without limits, TOTP brute-force falls in seconds; the entire 2FA feature is theatrical.

Plus an orthogonal CAPTCHA decision (Cloudflare Turnstile vs reCAPTCHA vs hCaptcha vs self-hosted vs none).

## Decision

**C — hybrid in-memory + persistent lockout — plus Cloudflare Turnstile gated on failure escalation.**

### In-memory layer (`Microsoft.AspNetCore.RateLimiting`)

`PartitionedRateLimiter` per policy, keyed by user id (when authenticated) or IP (when not):

| Endpoint | Limit | Window | Partition |
|---|---|---|---|
| `/totp-challenge` | 5 attempts | 5 min | user_id (from partial cookie) or IP |
| `/api/auth/unlock` | 5 attempts | 5 min | user_id |
| `/api/auth/recovery-codes/redeem` | 3 attempts | 1 hour | user_id or IP |
| `/signin-google` callback | 20 attempts | 1 min | IP (NAT-tolerant) |

Numbers are defensible defaults, not magic; can move without architectural impact. The shape (in-memory partitioned, per-user where authenticated, per-IP for unauthenticated callbacks) is the architectural part.

### Persistent lockout layer (`auth_lockouts` table)

```text
auth_lockouts
─────────────────────────────────────────────────────────────────
  user_id           uuid          FK → users(id) ON DELETE CASCADE
  kind              text          NOT NULL    -- 'totp' | 'unlock' | 'recovery'
  failed_count      smallint      NOT NULL DEFAULT 0
  locked_until      timestamptz   NULL
  last_attempt_at   timestamptz   NOT NULL
  PRIMARY KEY (user_id, kind)
```

Layered behavior:
- **In-memory limiter** catches bursts within a window (5 attempts in 5 min → 15-min throttle).
- **Persistent layer** catches sustained attempts that survive across windows or process restarts (e.g., 20 failed attempts across an hour → write `locked_until = now + 30 min`).
- Periodic sweep (a hosted background job, simple `DELETE FROM auth_lockouts WHERE last_attempt_at < now - interval '30 days'`) keeps the table small.

A side table (rather than columns on `users`) avoids:
- 6+ new columns on the central row,
- a migration every time a new lockout kind appears (just add a row).

### CAPTCHA via Cloudflare Turnstile

Required on:
- `/signin-google` callback after the first throttle event from that partition (in-memory state remembers "this IP just hit the limit; require CAPTCHA on next attempt").
- `/totp-challenge` and `/api/auth/unlock` after any in-memory throttle event for that user.

Frontend renders the Turnstile widget; backend validates the token via Cloudflare's `siteverify` endpoint (~40 LOC `ITurnstileValidator` service). Cloudflare account already in stack for DNS, so no new vendor.

### Ticket split

The original auth foundation work (one ticket-level entry in `tickets-2026-05-13.md` under PORTAL-003) is split into six tickets to reflect the actual scope:

- **PORTAL-003** — Google SSO + cookie auth + `sessions_invalidated_at` + `OnValidatePrincipal` (locks [[0030-auth-flow]])
- **PORTAL-003a** — TOTP enable/disable flow + backup codes (the `Otp.NET` half of original PORTAL-004)
- **PORTAL-003b** — Step-up auth + `IInfraOpUnlockCache`
- **PORTAL-003c** — Rate limiting (in-memory) via `Microsoft.AspNetCore.RateLimiting`
- **PORTAL-003d** — Persistent lockout (`auth_lockouts` reads/writes + sweep job)
- **PORTAL-003e** — Cloudflare Turnstile integration

Total estimate ~3.5 days for the auth slice; vs. original 1.25 days, the delta is the durable lockout layer + CAPTCHA — both originally YAGNI, now in scope.

## Consequences

- **Positive:**
  - In-memory limits catch the common case (single attacker, one IP) cheaply; persistent layer catches the sustained / multi-window / cross-restart case.
  - CAPTCHA gating is failure-escalated, not added as friction for legitimate users.
  - `auth_lockouts` keyed by `(user_id, kind)` — adding new lockout kinds is a row, not a migration.
  - Cleanup is trivial: one `DELETE` query, sweep on a timer.
  - Turnstile keeps a single upstream vendor (Cloudflare, already in stack for DNS).
- **Negative:**
  - More code surface than (D). The auth slice grows from one ticket to six (003–003e).
  - Cloudflare Turnstile adds one upstream dependency on the auth-critical path; outage there breaks sign-in.
  - Two layers of code (in-memory + persistent) is two failure modes to reason about.
- **Neutral:**
  - In-memory state lost on process restart is acceptable — attackers can't time bursts to deploys; persistent layer covers the cross-restart case.
  - CAPTCHA-on-failure-escalation keeps the common-case sign-in friction-free; only adversarial paths see the widget.

## Related

- [[0028-schema-conventions]] — `auth_lockouts` follows naming and audit conventions
- [[0029-type-mappings]] — `locked_until` and `last_attempt_at` are `Instant`s
- [[0030-auth-flow]] — endpoints to which rate limiting applies
- [[portal_architecture]] — Cloudflare is already in the stack for DNS (DEC-003)
