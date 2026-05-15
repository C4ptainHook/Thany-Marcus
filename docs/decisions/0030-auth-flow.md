# ADR-0030: Auth flow — Google SSO + cookie + TOTP + step-up

Status: Accepted
Date: 2026-05-15

## Context

The portal needs end-to-end auth for its admin surface. The original tickets (`tickets-2026-05-13.md` PORTAL-003 through PORTAL-006) named the components: Google OAuth via `Microsoft.AspNetCore.Authentication.Google`, optional TOTP via `Otp.NET`, passphrase-encrypted provider tokens, 8 dual-use recovery codes. The *flow* — how these compose into sign-in, step-up, session, and revocation — was not specified.

PORTAL-002 (Postgres schema) needs the auth-flow shape locked because several table columns depend on it (`sessions_invalidated_at`, `profile_picture_url`, `passphrase_*` columns on `users`; the `auth_lockouts` table — see [[0031-rate-limiting-and-lockout]]).

Five sub-decisions needed:

1. Session storage model.
2. TOTP enforcement timing and partial-auth state representation.
3. Cookie lifetime.
4. Step-up auth shape for sensitive operations (passphrase prompt).
5. Google token retention.

## Options considered

### 1. Session storage

- **A. Stateless signed cookie.** Cookie carries claims (`sub_us`, `email`, `totp` state); signed with server-side data-protection key. No per-request DB lookup.
- **B. Server-side via `ITicketStore`.** Cookie carries an opaque session ID; server reads a `sessions` table per request. Trivial revocation, per-device UI, per-session step-up state.
- **C. Distributed cache (Redis).** Standard for high-scale apps. Not in our stack; over-engineered for single-VM deploy.
- **D. Hybrid — stateless cookie + thin audit-only `sessions` table.** Out-of-band audit decouples from real auth state.

A' is A enriched with a `users.sessions_invalidated_at` column and an `OnValidatePrincipal` hook that (a) rejects cookies whose `IssuedUtc < sessions_invalidated_at`, and (b) refreshes stale claims from DB on every request.

### 2. TOTP enforcement

Sub-question 2a — when is TOTP required (for users who enabled it)?

- **A. At every sign-in.** Standard 2FA semantics.
- **B. Step-up only.** Sign in is just Google; TOTP triggers on sensitive ops.
- **C. Both.** Maximum security, maximum friction.
- **D. Trusted-device skip** (`remember this browser for 30 days`).

Sub-question 2b — partial-auth representation between Google success and TOTP success?

- **α. Two-cookie dance.** Temporary `.Portal.TotpChallenge` cookie after Google; swap to the real `.Portal.Auth` cookie after TOTP. Two schemes, two events.
- **β. Single cookie with `totp` claim** + authorization policy enforcing `totp ∈ {verified, not-enabled}`. ASP.NET Core's native pattern.

### 3. Cookie lifetime

- Short absolute (1-4h): bank-app territory.
- 14-day sliding (ASP.NET Core default): daily-driver friendly.
- Long (>30d sliding): consumer-app territory.

### 4. Step-up auth (passphrase for infra ops)

Sub-question 4a — granularity:
- **A. Per-user unlock.** One passphrase entry decrypts DEK; any infra op on any cloud allowed within the window. Matches crypto model (DEK is per-user).
- **B. Per-cloud unlock.** Each cloud needs its own unlock. Artificial friction; no security gain since DEK is already decrypted.

Sub-question 4b — window length:
- **A. Per-request only.** Each infra op re-prompts.
- **B. Short sliding window** (industry standard 5-10 min). AWS console, GCP, `sudo`.
- **C. Session-long.** Trivially insecure (shoulder-surfed laptop).
- **D. Per-flow.** Hard to define rigorously.

Sub-question 4c — where does the decrypted DEK live during the window?

- **α. In-process memory cache** (`ConcurrentDictionary<UserId, (Dek, ExpiresAt)>`). Lost on process restart (good hygiene); single-replica.
- **β. Encrypted-DEK-in-cookie.** Server is stateless; cookie carries the encrypted DEK. Cookie theft + data-protection-key compromise → DEK leak.
- **γ. Encrypted-DEK in Postgres with TTL** (new table). Multi-replica safe; new table to manage.
- **δ. Re-decrypt from passphrase on every op.** Passphrase lives in browser JS memory; XSS-exposed.

### 5. Google token retention

- **A. `SaveTokens=false`**, no `offline_access` scope, no Google tokens stored. Identity-only OAuth.
- **B. `SaveTokens=true`**, no `offline_access`. Google tokens in our cookie.
- **C. `SaveTokens=true` + `offline_access`** + persist refresh token. For calling Google APIs on user's behalf.
- **D. `SaveTokens=false` + cache profile metadata** (picture, locale). Hybrid for UI purposes.

## Decision

- **Session: A'** — stateless signed cookie + `users.sessions_invalidated_at` + `OnValidatePrincipal` hook.
- **TOTP timing: 2a → A, 2b → β** — required at every sign-in for users who opted in (TOTP enabled = `totp_secrets` row exists with non-null `enabled_at` and null `disabled_at`); enforced via single auth cookie with a `totp` claim and `[Authorize(Policy = "TotpRequired")]` on protected endpoints. The `not-enabled` value passes the policy.
- **Cookie lifetime: 14-day sliding** (ASP.NET Core default). `HttpOnly + Secure + SameSite=Lax`. TOTP-verified state persists for the cookie's lifetime; no separate TOTP timeout in MVP.
- **Step-up: 4a → A, 4b → B (10 min), 4c → α** — per-user unlock; 10-minute sliding window from last passphrase entry; decrypted DEK held in singleton `IInfraOpUnlockCache`; never written to disk or cookie. Cache invalidated on logout, recovery-code redemption, passphrase change, `sessions_invalidated_at` bump, and process restart.
- **Google tokens: 5 → A + cache `profile_picture_url`** — `SaveTokens = false`, no `offline_access`. `id_token` validated at sign-in (signature via Google JWKS, iss/aud/exp), identity claims extracted, tokens dropped. `users.profile_picture_url` cached from the `picture` claim for SPA UI rendering.

### Cookie claim shape

```text
{
  "sub_us":  "<user_id, uuid>",
  "sub_g":   "<google sub claim>",
  "email":   "user@example.com",
  "name":    "User Name",
  "totp":    "not-enabled" | "not-verified" | "verified",
  "iat":     "<issued at>",
  "exp":     "<absolute expiration, 14d from iat>"
}
```

State transitions for `totp` claim:

| Sign-in state | Initial `totp` claim | Transitions |
|---|---|---|
| No TOTP setup | `not-enabled` | → `verified` after enable-and-verify (PORTAL-003a flow); → `not-enabled` after disable-and-verify |
| TOTP enabled, fresh sign-in | `not-verified` | → `verified` after successful TOTP code or backup code at `/totp-challenge` |
| TOTP enabled, mid-session | `verified` | persists for cookie lifetime |

### Sign-in sequence (Google + TOTP)

```
1.  Browser → Portal:        GET /
                             (no cookie)
2.  Portal → Browser:        302 to /signin-google
3.  Browser → Google:        OAuth consent
4.  Google → Browser:        302 to /signin-google?code=…
5.  Browser → Portal:        GET /signin-google?code=…
6.  Portal → Google (S2S):   exchange code for { id_token, access_token }
7.  Portal validates id_token (signature, iss, aud, exp)
8.  Portal:                  upsert user by google_subject; refresh email/name/picture; bump last_seen_at
9.  Portal → Browser:        Set-Cookie: .Portal.Auth=<signed>; redirect to /
                             cookie's totp claim is:
                               "not-enabled"   if no TOTP setup
                               "not-verified"  if TOTP enabled
10. (if totp=not-verified)
    Browser → Portal:        GET /
    Authorization fails:     redirect to /totp-challenge
    Browser → Portal:        POST /totp-challenge { code }
    Portal verifies:         TOTP code OR backup code (consume row in totp_backup_codes)
    Portal → Browser:        RefreshSignIn with totp=verified; redirect to /
```

### Step-up sequence (infra op)

```
1.  SPA → Portal:            POST /api/clouds/abc/destroy
2.  Handler:                 IInfraOpUnlockCache.TryGet(userId, out dek) → false
3.  Portal → SPA:            401 { error: "step_up_required" }
4.  SPA shows modal:         "Enter your passphrase"
5.  SPA → Portal:            POST /api/auth/unlock { passphrase }
6.  Handler:                 derive KEK = Argon2id(passphrase, user.passphrase_argon2_salt, user.passphrase_argon2_params)
                             unwrap DEK = AesGcm.Decrypt(user.passphrase_wrapped_dek, user.passphrase_wrap_nonce, user.passphrase_wrap_tag, KEK)
                             IInfraOpUnlockCache.Set(userId, dek, ttl=10min)
7.  Portal → SPA:            204
8.  SPA retries:             POST /api/clouds/abc/destroy
9.  Handler:                 IInfraOpUnlockCache.TryGet(userId, out dek) → true
                             decrypt provider token with dek
                             run Terraform destroy / enqueue provisioning_job
                             drop dek + decrypted provider token from local scope
10. Cache:                   each successful TryGet bumps expires forward by 10 min
```

### Schema impact (locked here)

```text
users gains:
  sessions_invalidated_at         timestamptz  NULL
  profile_picture_url             text         NULL
  passphrase_argon2_salt          bytea        NULL
  passphrase_argon2_params        jsonb        NULL
  passphrase_wrapped_dek          bytea        NULL
  passphrase_wrap_nonce           bytea        NULL
  passphrase_wrap_tag             bytea        NULL
```

Encryption column conventions per [[0029-type-mappings]] (three columns per AES-GCM ciphertext).

### ASP.NET Core wiring (sketch)

```csharp
builder.Services.AddAuthentication(opts =>
{
    opts.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    opts.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(opts =>
{
    opts.ExpireTimeSpan      = TimeSpan.FromDays(14);
    opts.SlidingExpiration   = true;
    opts.Cookie.HttpOnly     = true;
    opts.Cookie.SameSite     = SameSiteMode.Lax;
    opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opts.Events.OnValidatePrincipal = OnValidatePrincipal;
})
.AddGoogle(opts =>
{
    opts.ClientId     = config["Google:ClientId"]!;
    opts.ClientSecret = config["Google:ClientSecret"]!;
    opts.Scope.Add("email");
    opts.Scope.Add("profile");
    opts.SaveTokens = false;
    opts.Events.OnCreatingTicket = OnCreatingGoogleTicket;
});

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy("TotpRequired", p => p.RequireAssertion(ctx =>
        ctx.User.FindFirstValue("totp") is "verified" or "not-enabled"));
});

builder.Services.AddSingleton<IInfraOpUnlockCache, InProcessInfraOpUnlockCache>();
```

## Consequences

- **Positive:**
  - Honest "sign out everywhere" and "recovery-code bumps all sessions" semantics via `sessions_invalidated_at`.
  - Per-request user lookup refreshes stale claims; cookie carries identity, DB carries truth.
  - 14-day sliding cookie + `OnValidatePrincipal` is the canonical ASP.NET Core pattern — no `ITicketStore` complexity.
  - Step-up DEK never persists outside process memory; restart forces re-unlock.
  - `SaveTokens=false` means a portal breach doesn't expose Google access/refresh tokens.
  - Per-user step-up unlock matches the crypto envelope (DEK is per-user); destroy-all-my-clouds is one passphrase entry, not N.
- **Negative:**
  - "Active sessions" UI for the user is not possible without future migration to `ITicketStore`. Accepted: not in thesis scope; clean follow-up.
  - In-process DEK cache rules out future multi-replica deploys without revisiting. Accepted: [[portal_deployment]] is single-VM.
  - Per-request DB lookup on `users` for cookie validation. Indexed PK lookup; ~0.1ms.
- **Neutral:**
  - Adding refresh-token-based Google API calls later (e.g., Drive integration) is a clean additive change: one column on `users`, one config flag, one OAuth scope addition.
  - Trusted-device skip can be added later via a `trusted_devices` table.
  - TOTP-verified-state-per-cookie-lifetime is less strict than per-7d-rechallenge but a defensible thesis position; tighter can be added later.

## Related

- [[0023-test-stack]] — cookie auth tests via `WebApplicationFactory<Program>` with auth cookies pre-baked in test setup
- [[0024-dbcontext-shape]] — `users`, `totp_secrets`, `recovery_codes` live in a single `PortalDbContext`
- [[0028-schema-conventions]] — `sessions_invalidated_at` is a nullable `Instant` per audit conventions
- [[0029-type-mappings]] — passphrase crypto columns follow the AES-GCM three-column shape
- [[0031-rate-limiting-and-lockout]] — auth endpoints rate-limited and persistent-locked-out per separate ADR
- [[portal_architecture]] — Minimal APIs + VSA shape for auth endpoints under `Features/Auth/`
