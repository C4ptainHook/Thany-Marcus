# PORTAL-003d Persistent lockout (`auth_lockouts`) — Handoff Brief

**Goal:** land the persistent lockout layer on top of the in-memory limiter from PORTAL-003c. Three deliverables: (a) an `AuthLockoutService` that reads/writes the existing `auth_lockouts` rows (kinds `'totp'` and `'unlock'`); (b) an endpoint filter that short-circuits known-locked users on `POST /totp-challenge` and `POST /api/auth/unlock` **before** the rate-limiter runs the partition lookup; (c) a hosted background sweep job that deletes stale rows. The `auth_lockouts` table itself (entity + EF configuration + PORTAL-002 migration) already exists — 003d does **not** add columns, change keys, or write a migration. **No Turnstile (003e), no `recovery` kind (PORTAL-006 lands that with `/api/auth/recovery-codes/redeem`), no changes to the in-memory limiter's `OnRejected` callback, no rewrite of the partial-auth cookie flow.**

Estimated **0.75 person-day** — the service shape is mostly a single `Upsert` query + threshold check; the time is in the endpoint filter ordering (must run after auth, before the rate-limiter's partition resolution) and the sweep job's deterministic test against `FakeTimeProvider`.

## Where decisions live (read before doing anything)

- **`docs/decisions/0031-rate-limiting-and-lockout.md`** §"Persistent lockout layer" + §"Layered behavior" + §"Ticket split" — the contract for this ticket. The schema is fixed; the *thresholds* (failure count → lockout duration) are defensible defaults, tunable from `appsettings.json`. §"Ticket split" reaffirms: PORTAL-003d is **only** persistent lockout reads/writes + sweep; the in-memory limiter (003c) and Turnstile (003e) are separate.
- **`docs/decisions/0030-auth-flow.md`** §"Cookie claim shape" — the partial-auth cookie carries `sub-us` *before* TOTP verification. The lockout filter on `/totp-challenge` partitions on the same `sub-us` claim that 003c's limiter uses; both run against `totp=not-verified` requests. §"Sign-in sequence" step 7 names `/signin-google` as the OAuth callback — 003d does **not** touch that path (Google can't be "locked out"; lockout is per-user, and `/signin-google` is per-IP).
- **`plans/portal-003a-handoff.md`** §"Output" + `TotpEndpoints.cs` lines 102–123 — `/totp-challenge` returns `Results.Unauthorized()` on `!ok` (the `TotpChallengeResult.Failed` + backup-code-redeem fallback both fail). The hook point for 003d is *this branch*: when `ok == false`, record a `'totp'` failure; when `ok == true`, clear the failure row. The handler currently doesn't distinguish between "wrong code" and "missing claim" — 003d records a failure for *any* `!ok`, which is the right behavior (an attacker forging cookies should still trip the lockout).
- **`plans/portal-003b-handoff.md`** §"PassphraseEndpoints" + `PassphraseEndpoints.cs` lines 21–42 — `/api/auth/unlock` returns `Results.Json(new { error = "invalid_passphrase" }, statusCode: 401)` on `UnlockResult.Failed`. The hook point is the same: failure → record; success (`cache.Set(userId, dek)` branch) → clear.
- **`plans/portal-003c-handoff.md`** §"Program.cs wiring" + §"Acceptance criteria" — the pipeline order is `SignInGoogleRateLimitMiddleware → UseAuthentication → UseAuthorization → UseRateLimiter`. The lockout filter is an **endpoint filter**, not a middleware, so it doesn't slot into `app.Use*` ordering — it runs as part of the endpoint's filter pipeline, which executes *after* authorization succeeds and *before* the handler delegate. The rate-limiter middleware's partition resolution also happens before the handler, but the two are coordinate (filters run inside the endpoint invocation, the rate-limiter wraps it); the explicit guarantee we need is "no DB hit on the actual TOTP / unlock verification when locked" — the filter short-circuits before reaching `totp.VerifyChallengeAsync` / `svc.TryUnwrapDekAsync`, which is what costs.
- **`src/ThanyMarcus.Portal.Api/Features/Auth/AuthLockout.cs`** + `AuthLockoutConfiguration.cs` — the entity exists. `(UserId, Kind)` is the PK; `FailedCount` is `short` (PG `smallint`); `LockedUntil` is nullable `Instant`; `LastAttemptAt` is non-null `Instant`. EF maps the table as `auth_lockouts` with snake_case. **Do not modify these files.** If the threshold check needs an index on `LockedUntil` for the sweep, that's a 003d follow-up migration — but the sweep filters by `LastAttemptAt`, which is also non-indexed, and for the table sizes we expect (≤ a few thousand rows in production for a thesis prototype) a seq scan is fine. Skip the index.
- **`docs/decisions/0023-test-stack.md`** — `WithTestAuth(...)` bakes the partial-auth cookie; `FakeClock` is the `IClock` that handlers consume. The lockout service must consume `IClock` (the existing NodaTime `IClock`, **not** `TimeProvider` — `TimeProvider` was added in 003c only because the rate-limiter middleware demanded it; the rest of the Portal codebase uses `IClock` per [[portal_tooling]]). For the sweep service's `PeriodicTimer`, see "Sweep job" below — the timer itself uses wall-clock, but the *query* uses `IClock` so tests can drive it.
- **Memory files**: `portal_architecture.md` (Minimal APIs + VSA — 003d lives in a `Features/Auth/Lockout/` slice), `portal_tooling.md` (warnings-as-errors, NodaTime `Instant` for timestamps, `IClock` for time), `feedback_no_code_comments.md` (no narrative comments — the threshold logic is non-obvious but the integration test names document the behavior).

**Do not re-litigate the shape.** ADR-0031 §"Persistent lockout layer" closed: side table, `(user_id, kind)` PK, three kinds, periodic sweep. If a question seems open (e.g., "should locked users get a different status code than rate-limited users?") — yes, return **`423 Locked`** for persistent lockout (distinguishes from 429 rate-limit) with body `{ "error": "account_locked", "locked_until": "<iso8601>", "remaining_seconds": N }`. The status code distinction matters for the SPA: 429 means "wait a few seconds and try again"; 423 means "wait minutes and possibly re-authenticate". The SPA's existing `fetchWithStepUp` already handles 401/403; 423 will be added by 003e's UI work or in a small 003d-companion SPA tweak — keep the server-side response shape locked here so 003e can wire the client.

## Scope boundary (precise)

**In scope:**
- `Features/Auth/Lockout/` slice: `AuthLockoutKinds` (string constants), `LockoutOptions` (record bound from `appsettings.json`), `AuthLockoutService` (the scoped service with `IsLockedAsync` / `RecordFailureAsync` / `ClearAsync`), `LockoutGuardFilter` (endpoint filter that short-circuits with 423), `LockoutServiceCollectionExtensions` (the `AddAuthLockout` extension method).
- Two kinds wired:
  - `'totp'` — threshold **20 failures / 1 hour** → `locked_until = now + 30 min`. Partition: `user_id` (from `sub-us` claim).
  - `'unlock'` — threshold **20 failures / 1 hour** → `locked_until = now + 30 min`. Partition: `user_id`.
- Endpoint filter `LockoutGuardFilter` attached to `/totp-challenge` (kind `'totp'`) and `/api/auth/unlock` (kind `'unlock'`). The filter reads the `sub-us` claim, calls `AuthLockoutService.IsLockedAsync(userId, kind, ct)`; on locked, short-circuits with `Results.Json(new { error = "account_locked", locked_until, remaining_seconds }, statusCode: 423)`.
- Hooks in handlers:
  - `TotpEndpoints.cs` `/totp-challenge` — after computing `ok`: if `!ok`, `await lockout.RecordFailureAsync(userId, AuthLockoutKinds.Totp, ct)`; if `ok`, `await lockout.ClearAsync(userId, AuthLockoutKinds.Totp, ct)`. Both before returning the existing result.
  - `PassphraseEndpoints.cs` `/api/auth/unlock` — same shape: on `UnlockResult.Failed`, record; on success (the `cache.Set(...)` branch), clear.
- `AuthLockoutSweepService` — hosted background service. Runs every **6 hours** (real-world cadence; can be tighter in tests). One SQL: `DELETE FROM auth_lockouts WHERE last_attempt_at < now() - interval '30 days' AND (locked_until IS NULL OR locked_until < now())`. Uses raw SQL via `db.Database.ExecuteSqlInterpolatedAsync` because EF's `ExecuteDeleteAsync` is fine too — prefer `ExecuteDeleteAsync` to keep `IClock`-substitutable time semantics (see "Sweep job" below).
- `appsettings.json` adds a `Lockout:` section so the threshold + window + duration can be tuned without rebuilding.
- Integration tests proving the seven acceptance criteria below.

**Out of scope (do not touch — each has its own ticket):**
- Cloudflare Turnstile gating after a lockout event — PORTAL-003e. 003e will read `auth_lockouts.failed_count` (or the lockout filter's "is locked?" signal) to decide whether to render the Turnstile widget on the next attempt. **Do not pre-add** a "captcha required" boolean to the schema; the existing `failed_count` is sufficient (003e can threshold on it: "if `failed_count >= 5` show captcha, regardless of whether `locked_until` is set"). If 003e needs a hook in 003d, the seam is `LockoutGuardFilter.InvokeAsync` — 003e wraps or replaces it.
- `recovery` kind for `/api/auth/recovery-codes/redeem` — PORTAL-006. That ticket lands both the endpoint and the lockout wiring; 003d does **not** add `AuthLockoutKinds.Recovery` (the constant) nor pre-attach a filter to an endpoint that doesn't exist. The pattern 003d establishes (kind constant + filter + record/clear in handler) is what PORTAL-006 follows.
- Rate-limit (in-memory) policy changes — PORTAL-003c is sealed. 003d does **not** modify `AuthRateLimiterPolicies` nor the `OnRejected` delegate. The seam between 003c and 003d is: 003c handles bursts (5 attempts in 5 min → 429); 003d handles sustained patterns (20 attempts in 1 hour → 423). The two are independent — a request can hit 429 without ever incrementing `failed_count` (the limiter short-circuits before the handler runs, so the handler's `RecordFailureAsync` call never fires). **This is intentional** per ADR-0031 §"Layered behavior" — the limiter is a noise filter for the persistent layer. A future tweak could have the limiter's `OnRejected` *also* record a persistent failure, but that conflates two threat models and is out of scope.
- Locking columns on `users` (e.g., a "manually locked" flag for admin lockouts) — not in spec. The thesis prototype has no admin tooling; manual lockout is `UPDATE auth_lockouts SET locked_until = '<far future>' WHERE user_id = ... AND kind = ...` via raw SQL.
- Distributed lockout coordination across Portal instances — out of scope per [[portal_deployment]] (single-VM Docker Compose). The `auth_lockouts` table is the coordination point already (Postgres is the source of truth); concurrent writes from multiple instances would be safe via the upsert pattern below, but the deployment is single-instance for the foreseeable future.
- A separate "manual unlock" endpoint for ops — not in spec. Direct SQL is the unlock mechanism.

If a follow-up needs to extend this (e.g., 003e needs to know "did this user just unlock from a lockout?" to skip the captcha), the seam is `AuthLockoutService.ClearAsync` — 003e wraps it or subscribes via a `MediatR`-style notification (out of scope here; the codebase doesn't use MediatR per [[portal_architecture]] VSA conventions). The simplest hook for 003e is to read `auth_lockouts.failed_count` directly, since the row persists post-clear (`ClearAsync` resets the count to 0 but leaves the row for sweep — see "ClearAsync semantics" below).

## Output of PORTAL-003d — final directory state

```
Thany-Marcus/
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── Program.cs                                 # CHANGED: services.AddAuthLockout(builder.Configuration);
│   │   │                                              #          services.AddHostedService<AuthLockoutSweepService>()
│   │   ├── appsettings.json                           # CHANGED: adds "Lockout" section (defaults match ADR-0031 §"Persistent lockout layer")
│   │   └── Features/
│   │       └── Auth/
│   │           ├── Lockout/                           # NEW subfolder
│   │           │   ├── AuthLockoutKinds.cs            # public const string Totp = "totp"; Unlock = "unlock";
│   │           │   ├── LockoutOptions.cs              # record bound from "Lockout" config section
│   │           │   ├── AuthLockoutService.cs          # IsLockedAsync / RecordFailureAsync / ClearAsync
│   │           │   ├── LockoutGuardFilter.cs          # IEndpointFilter — short-circuits with 423
│   │           │   ├── AuthLockoutSweepService.cs     # BackgroundService — periodic DELETE
│   │           │   └── LockoutServiceCollectionExtensions.cs  # AddAuthLockout(...) extension method
│   │           ├── Totp/
│   │           │   └── TotpEndpoints.cs               # CHANGED: /totp-challenge gains .AddEndpointFilter<LockoutGuardFilter>();
│   │           │                                      #          handler records/clears via injected AuthLockoutService
│   │           └── StepUp/
│   │               └── PassphraseEndpoints.cs         # CHANGED: /api/auth/unlock gains .AddEndpointFilter<LockoutGuardFilter>();
│   │                                                  #          handler records/clears via injected AuthLockoutService
└── tests/
    └── ThanyMarcus.Portal.Tests/
        └── Features/
            └── Auth/
                └── Lockout/                           # NEW subfolder
                    ├── TotpLockoutTests.cs            # integration: 20 failures → 21st returns 423; successful verify clears
                    ├── UnlockLockoutTests.cs          # integration: 20 failures → 21st returns 423; successful unlock clears
                    ├── LockoutSweepTests.cs           # integration: rows older than 30d with no active lock → deleted
                    └── AuthLockoutServiceTests.cs     # unit: upsert semantics, threshold rollover, ClearAsync resets count
```

No new packages. No new DB migrations. No new auth schemes. No new endpoints exposed to OpenAPI (filters don't change surface). The `AuthLockout` entity + EF configuration + `auth_lockouts` table all exist from PORTAL-002.

## `appsettings.json` shape

```json
{
  "Lockout": {
    "Totp":   { "MaxFailures": 20, "WindowSeconds": 3600, "LockoutSeconds": 1800 },
    "Unlock": { "MaxFailures": 20, "WindowSeconds": 3600, "LockoutSeconds": 1800 },
    "Sweep":  { "IntervalSeconds": 21600, "RetentionDays": 30 }
  }
}
```

`LockoutOptions`:

```csharp
public sealed record LockoutOptions
{
    public KindOptions Totp   { get; init; } = new(MaxFailures: 20, WindowSeconds: 3600, LockoutSeconds: 1800);
    public KindOptions Unlock { get; init; } = new(MaxFailures: 20, WindowSeconds: 3600, LockoutSeconds: 1800);
    public SweepOptions Sweep { get; init; } = new(IntervalSeconds: 21600, RetentionDays: 30);

    public sealed record KindOptions(int MaxFailures, int WindowSeconds, int LockoutSeconds);
    public sealed record SweepOptions(int IntervalSeconds, int RetentionDays);
}
```

Defaults baked into the record match ADR-0031's "20 failures across an hour → lock for 30 min" exactly, so a missing config section still produces the right behavior. The sweep interval default (6 hours) is conservative — the table will stay tiny in practice, so sweeping more aggressively just burns cycles.

The `WindowSeconds` semantics: a "failure" is counted toward the threshold only if `LastAttemptAt` was within `WindowSeconds` of the current attempt. See "RecordFailureAsync" below for the rollover rule.

## `AuthLockoutKinds.cs`

```csharp
public static class AuthLockoutKinds
{
    public const string Totp   = "totp";
    public const string Unlock = "unlock";
    // Recovery is intentionally absent — added by PORTAL-006 with /api/auth/recovery-codes/redeem.
}
```

The strings are the exact `kind` column values in `auth_lockouts`. ADR-0031 §"Persistent lockout layer" names them; the table has no `CHECK` constraint on the kind column (per `AuthLockoutConfiguration.cs`), so a typo silently writes a row that no filter or sweep cleans up. **Use the constants everywhere** — no inline strings.

## `AuthLockoutService.cs`

```csharp
public sealed class AuthLockoutService(PortalDbContext db, IClock clock, IOptions<LockoutOptions> options)
{
    public async Task<LockoutState> IsLockedAsync(Guid userId, string kind, CancellationToken ct)
    {
        var row = await db.AuthLockouts
            .AsNoTracking()
            .SingleOrDefaultAsync(a => a.UserId == userId && a.Kind == kind, ct);
        if (row is null || row.LockedUntil is null) return LockoutState.NotLocked;
        var now = clock.GetCurrentInstant();
        if (row.LockedUntil <= now) return LockoutState.NotLocked;
        var remaining = (row.LockedUntil.Value - now).TotalSeconds;
        return new LockoutState(IsLocked: true, LockedUntil: row.LockedUntil.Value, RemainingSeconds: (int)Math.Ceiling(remaining));
    }

    public async Task RecordFailureAsync(Guid userId, string kind, CancellationToken ct)
    {
        var cfg = ConfigFor(kind);
        var now = clock.GetCurrentInstant();
        var row = await db.AuthLockouts.SingleOrDefaultAsync(a => a.UserId == userId && a.Kind == kind, ct);
        if (row is null)
        {
            db.AuthLockouts.Add(new AuthLockout
            {
                UserId        = userId,
                Kind          = kind,
                FailedCount   = 1,
                LastAttemptAt = now,
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        var windowStart = now - Duration.FromSeconds(cfg.WindowSeconds);
        if (row.LastAttemptAt < windowStart)
            row.FailedCount = 1;
        else
            row.FailedCount = (short)(row.FailedCount + 1);

        row.LastAttemptAt = now;
        if (row.FailedCount >= cfg.MaxFailures)
            row.LockedUntil = now + Duration.FromSeconds(cfg.LockoutSeconds);

        await db.SaveChangesAsync(ct);
    }

    public async Task ClearAsync(Guid userId, string kind, CancellationToken ct)
    {
        var row = await db.AuthLockouts.SingleOrDefaultAsync(a => a.UserId == userId && a.Kind == kind, ct);
        if (row is null) return;
        row.FailedCount = 0;
        row.LockedUntil = null;
        row.LastAttemptAt = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
    }

    private LockoutOptions.KindOptions ConfigFor(string kind) => kind switch
    {
        AuthLockoutKinds.Totp   => options.Value.Totp,
        AuthLockoutKinds.Unlock => options.Value.Unlock,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown lockout kind"),
    };
}

public readonly record struct LockoutState(bool IsLocked, Instant? LockedUntil, int RemainingSeconds)
{
    public static readonly LockoutState NotLocked = new(false, null, 0);
}
```

Five judgment calls embedded above:

- **Single `SaveChangesAsync` per call, not raw SQL upsert.** Postgres has `INSERT … ON CONFLICT` which is one round-trip; EF's read-then-write is two. For a thesis prototype with single-instance deployment, the contention window is small and the readability win is large. If this ever bottlenecks, swap to `ExecuteSqlInterpolatedAsync` with `ON CONFLICT (user_id, kind) DO UPDATE SET failed_count = …, last_attempt_at = …` — the entity shape doesn't need to change.
- **Window rollover resets `FailedCount` to 1, not 0.** When the previous failure was outside the window, the current attempt counts as the first of a *new* window. Setting to 0 and incrementing would be equivalent but visually misleading in the DB. The semantics: `FailedCount` is the count within the window ending at `LastAttemptAt`.
- **`ClearAsync` resets count + clears `LockedUntil` but leaves the row.** The row is needed for the next failure (so the threshold check can compare against the window) and for 003e's "did this user recently fail?" check. The sweep handles cleanup. Alternative: `db.AuthLockouts.Remove(row)` and let `RecordFailureAsync` re-insert; this is a wash on row count but loses the `LastAttemptAt` history that 003e might want. Keep the row.
- **`LockoutState` as a readonly record struct.** Allocation-free, three small fields; idiomatic for filter return values. The `NotLocked` static avoids re-allocating the common case in tight loops (the filter calls this per request).
- **No locking / advisory locks.** Concurrent failures from the same user are rare (the rate-limiter caps bursts at 5/5min before we even get here), and EF's default isolation (read committed) is sufficient. Two concurrent failures could race and produce `FailedCount = 1` when it should be 2 — acceptable; the *next* failure catches up. If this ever matters, wrap the read+write in a `SERIALIZABLE` transaction or use `INSERT … ON CONFLICT DO UPDATE SET failed_count = auth_lockouts.failed_count + 1, …` — the latter is atomic.

## `LockoutGuardFilter.cs`

```csharp
public sealed class LockoutGuardFilter(AuthLockoutService lockouts) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var subClaim = http.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (string.IsNullOrEmpty(subClaim))
            return Results.Unauthorized();
        if (!Guid.TryParse(subClaim, out var userId))
            return Results.Unauthorized();

        var kind = http.GetEndpoint() switch
        {
            { } ep when ep.Metadata.GetMetadata<LockoutKindMetadata>() is { } m => m.Kind,
            _ => null,
        };
        if (kind is null) return await next(ctx);   // not a lockout-gated endpoint

        var state = await lockouts.IsLockedAsync(userId, kind, http.RequestAborted);
        if (!state.IsLocked) return await next(ctx);

        http.Response.Headers.RetryAfter = state.RemainingSeconds.ToString();
        return Results.Json(
            new
            {
                error             = "account_locked",
                locked_until      = state.LockedUntil!.Value.ToString("g", CultureInfo.InvariantCulture),
                remaining_seconds = state.RemainingSeconds,
            },
            statusCode: StatusCodes.Status423Locked);
    }
}

public sealed record LockoutKindMetadata(string Kind);
```

Three points:

- **`LockoutKindMetadata` carries the kind from endpoint registration to filter** — the filter is generic over kind, but the endpoint declaration is specific. The metadata pattern keeps the filter type single and the endpoint declarations terse: `.AddEndpointFilter<LockoutGuardFilter>().WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Totp))`. Reading the metadata from `GetEndpoint()` (which is non-null inside an endpoint filter) is the standard escape hatch.
- **Filter returns `Results.Unauthorized()` on missing claim** — both endpoints are `.RequireAuthorization()` (totp-challenge) or `.RequireAuthorization(AuthPolicies.TotpRequired)` (unlock), so the filter sees an authenticated principal. The null-guard is defense in depth; in practice the auth pipeline rejects pre-filter. Same posture as 003c's limiter null-guard.
- **`Retry-After` header on 423.** RFC 7231 §7.1.3 specifies `Retry-After` for 503 and 3xx, but RFC 4918 §11.3 (which defines 423 Locked) permits it for "indicate when the resource is expected to become available." Standard practice; the SPA can read it interchangeably with the JSON `remaining_seconds`.

## Endpoint wiring

`TotpEndpoints.cs` `/totp-challenge`:

```csharp
app.MapPost("/totp-challenge", async (
    TotpChallengeRequest body,
    ClaimsPrincipal user,
    HttpContext http,
    PortalDbContext db,
    TotpService totp,
    TotpBackupCodeService backups,
    AuthLockoutService lockouts,                        // ADD
    CancellationToken ct) =>
{
    if (user.Identity?.IsAuthenticated != true)
        return Results.Unauthorized();

    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

    var totpOk = await totp.VerifyChallengeAsync(db, userId, body.Code, ct);
    var ok = totpOk is TotpChallengeResult.Verified
          || await backups.RedeemAsync(userId, body.Code, ct);
    if (!ok)
    {
        await lockouts.RecordFailureAsync(userId, AuthLockoutKinds.Totp, ct);   // ADD
        return Results.Unauthorized();
    }

    await lockouts.ClearAsync(userId, AuthLockoutKinds.Totp, ct);               // ADD
    await RefreshTotpClaim(http, user, TotpClaimValues.Verified);
    return Results.NoContent();
})
   .RequireAuthorization()
   .RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge)
   .AddEndpointFilter<LockoutGuardFilter>()                                     // ADD
   .WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Totp));               // ADD
```

`PassphraseEndpoints.cs` `/api/auth/unlock`:

```csharp
app.MapPost("/api/auth/unlock", async (
    PassphraseUnlockRequest body,
    ClaimsPrincipal user,
    PassphraseService svc,
    IInfraOpUnlockCache cache,
    AuthLockoutService lockouts,                                                // ADD
    CancellationToken ct) =>
{
    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
    var dek = new byte[32];
    try
    {
        var result = await svc.TryUnwrapDekAsync(userId, body.Passphrase, dek, ct);
        if (result is UnlockResult.Failed)
        {
            await lockouts.RecordFailureAsync(userId, AuthLockoutKinds.Unlock, ct);   // ADD
            return Results.Json(new { error = "invalid_passphrase" }, statusCode: StatusCodes.Status401Unauthorized);
        }
        cache.Set(userId, dek);
        await lockouts.ClearAsync(userId, AuthLockoutKinds.Unlock, ct);               // ADD
        return Results.NoContent();
    }
    finally
    {
        CryptographicOperations.ZeroMemory(dek);
    }
})
   .RequireAuthorization(AuthPolicies.TotpRequired)
   .RequireRateLimiting(AuthRateLimiterPolicies.Unlock)
   .AddEndpointFilter<LockoutGuardFilter>()                                           // ADD
   .WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Unlock));                   // ADD
```

Three observations:

- **`/api/auth/passphrase/init` is NOT lockout-gated.** Same reasoning as 003c: the threat model differs. `/init` is a one-time setup call; gating it doesn't help (an attacker with `TotpRequired` access has already won), and a legitimate user fat-fingering during setup would self-lock pointlessly.
- **Filter order vs. rate-limiter.** `.RequireRateLimiting(...)` registers the policy via `IRateLimiterMetadata`; the rate-limiter middleware (`UseRateLimiter`) reads that metadata and acquires a lease *before* dispatching to the endpoint. Endpoint filters run *inside* the endpoint dispatch. So the actual execution order is: rate-limiter acquires lease (or rejects 429) → endpoint filter runs (or short-circuits 423) → handler runs. **A 429 from the limiter can therefore beat a 423 from the lockout** for the same request. This is correct: a known-burst attacker should see 429 (cheap rejection) rather than 423 (DB query). The two layers don't conflict — they catch different patterns.
- **Order of `.AddEndpointFilter` and `.WithMetadata` matters for readability, not execution.** `.WithMetadata` can be called at any point in the chain; the endpoint builder accumulates metadata regardless of order. Placing it adjacent to `.AddEndpointFilter<LockoutGuardFilter>()` keeps the visual coupling.

## `AuthLockoutSweepService.cs`

```csharp
public sealed class AuthLockoutSweepService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<LockoutOptions> options,
    ILogger<AuthLockoutSweepService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Value.Sweep.IntervalSeconds);
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                var cutoff = clock.GetCurrentInstant() - Duration.FromDays(options.Value.Sweep.RetentionDays);
                var deleted = await db.AuthLockouts
                    .Where(a => a.LastAttemptAt < cutoff
                             && (a.LockedUntil == null || a.LockedUntil < clock.GetCurrentInstant()))
                    .ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0)
                    logger.LogInformation("AuthLockoutSweep deleted {Count} stale lockout rows", deleted);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
```

Four points:

- **`IServiceScopeFactory` because the sweep is a singleton but `PortalDbContext` is scoped.** Same pattern as `InfraOpUnlockSweepService` (which sweeps in-memory state and doesn't need a DB scope) — but with a DB scope per tick. The scope is short (one query) so contention is fine.
- **`ExecuteDeleteAsync` (EF 7+) over raw SQL.** Keeps the query inspectable in code, generates a single `DELETE … WHERE …` round-trip (not load + delete), and lets EF translate `IClock`-sourced `Instant` to PG `timestamptz` consistently with the rest of the codebase. No raw SQL strings to keep in sync with schema changes.
- **`PeriodicTimer` over `Task.Delay` loop.** `PeriodicTimer` doesn't drift across long-running ticks (a `Task.Delay(interval)` loop drifts by however long the work takes). For a 6-hour interval the drift is irrelevant, but `PeriodicTimer` is the idiomatic .NET 6+ choice and matches `InfraOpUnlockSweepService`.
- **Sweep does NOT delete rows that are currently locked.** The `(a.LockedUntil == null || a.LockedUntil < clock.GetCurrentInstant())` guard ensures an active lockout isn't swept (a far-future `LockedUntil` from a manual-ops lockout would survive). The retention condition + active-lockout guard together mean: rows are kept while interesting, deleted when stale and inert.

## Program.cs wiring (additions)

```csharp
// services
builder.Services.AddAuthLockout(builder.Configuration);

// hosted services already include InfraOpUnlockSweepService; add AuthLockoutSweepService
builder.Services.AddHostedService<AuthLockoutSweepService>();
```

`AddAuthLockout` extension method:

```csharp
public static IServiceCollection AddAuthLockout(this IServiceCollection services, IConfiguration cfg)
{
    services.AddOptions<LockoutOptions>()
            .Bind(cfg.GetSection("Lockout"));
    services.AddScoped<AuthLockoutService>();
    services.AddScoped<LockoutGuardFilter>();
    return services;
}
```

**`LockoutGuardFilter` is scoped, not singleton.** It depends on `AuthLockoutService`, which depends on `PortalDbContext` (scoped). Registering the filter as scoped means each request gets a fresh filter instance with a fresh service — matches the scoped lifetime of the underlying DB context.

No changes to pipeline order. The filter slots into the endpoint's existing filter pipeline; the sweep is a `BackgroundService` and runs independently of the request pipeline.

## ClearAsync semantics

`ClearAsync` is called on **successful** verification (`/totp-challenge` returns 204, `/api/auth/unlock` returns 204). Behavior:

- If no row exists: no-op (the user has never failed; nothing to clear).
- If row exists with `FailedCount > 0`: reset count to 0, clear `LockedUntil`, set `LastAttemptAt = now`. The row persists for sweep.
- If row exists with `LockedUntil > now`: this should never happen — the lockout filter short-circuited before the handler ran. **But** if it does (race condition: lock expired between filter check and handler completion), the clear is correct: the successful auth proves the user is legitimate, so any stale lock state should drop.

The reset-`LastAttemptAt`-to-now on clear is deliberate: it marks the row as "fresh activity" so sweep doesn't delete it during a long-active session. Alternative semantics (clear by deleting the row) would simplify but lose the audit trail; keep the row.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings**.
- All existing PORTAL-003 / 003a / 003b / 003c tests still green; new lockout tests all green.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts cleanly. No new endpoints in OpenAPI. The `AuthLockoutSweepService` logs at startup: "AuthLockoutSweep deleted 0 stale lockout rows" only when it actually deletes (the `if (deleted > 0)` guard); a quiet startup is normal.
- **`TotpLockoutTests` (integration):** with `WithTestAuth(userId, totp: NotVerified)` and `Lockout:Totp:MaxFailures = 3` (test-override), 3 sequential `POST /totp-challenge { code: "000000" }` calls return 401 (`!ok`). The 4th returns **423 Locked** with body `{ "error": "account_locked", "locked_until": "<iso8601>", "remaining_seconds": N }` (N > 0) and a `Retry-After: N` header. After advancing `FakeClock` by `LockoutSeconds + 1`, the 5th call returns 401 (lockout expired; `IsLockedAsync` returns `NotLocked`).
- **Per-user partition isolation (`TotpLockoutTests`):** user A is locked (3 failures); user B's first call still returns 401, not 423. `(user_id, kind)` PK isolates.
- **Successful verification clears (`TotpLockoutTests`):** user A fails 2x (count = 2), then succeeds once (count = 0, locked_until = null). Subsequent 3 failures → 4th returns 423 (not "user A only has 1 attempt left from before"). Confirms `ClearAsync` resets the window.
- **`UnlockLockoutTests` (integration):** same shape — 3 failures → 4th returns 423; success clears the count.
- **`LockoutSweepTests` (integration):** seed two `auth_lockouts` rows: one with `LastAttemptAt = now - 31d, LockedUntil = null`; one with `LastAttemptAt = now - 31d, LockedUntil = now + 1d` (manually-active far-future lock). Advance `FakeClock` to trigger sweep (or call `AuthLockoutSweepService.ExecuteAsync` directly via a test hook). Assert: row 1 deleted; row 2 retained. Validates the `LockedUntil` guard.
- **`AuthLockoutServiceTests` (unit, in-memory or sqlite EF):** `RecordFailureAsync` increments within window, resets to 1 outside window, sets `LockedUntil` exactly when count crosses threshold, sets `LastAttemptAt = clock.GetCurrentInstant()` on every call. `ClearAsync` is a no-op when row absent; resets count and clears lock when present. `IsLockedAsync` returns `NotLocked` when `LockedUntil` is past, `IsLocked` when future.
- **`RateLimitConfigurationTests`-style smoke for lockout:** boot the host with `appsettings.Test.json` overriding `Lockout:Totp:MaxFailures = 2`; assert the 3rd `POST /totp-challenge` returns 423. Confirms config binding.
- **No 429-vs-423 ambiguity:** with `Lockout:Totp:MaxFailures = 100` (lockout out of reach) and `RateLimiting:TotpChallenge:PermitLimit = 3` (default 5, test-override), 3 calls return 401, 4th returns **429** (limiter wins). With `Lockout:Totp:MaxFailures = 3` and `RateLimiting:TotpChallenge:PermitLimit = 100`, 3 calls return 401, 4th returns **423** (lockout wins). Confirms the two layers don't shadow each other in their respective regimes.

## Concrete steps in order (each maps to a task)

1. **Land `AuthLockoutKinds` + `LockoutOptions` + `LockoutKindMetadata`.** Trivial. Defaults baked into `LockoutOptions` match ADR-0031 §"Persistent lockout layer".

2. **Land `AuthLockoutService` + `AuthLockoutServiceTests` (unit).** Test the threshold rollover, window expiry, and `ClearAsync` no-op cases against a sqlite or in-memory EF context. Unit tests here are cheap and prove the trickiest logic (window math) without needing the full Portal host.

3. **Land `LockoutGuardFilter` + `LockoutServiceCollectionExtensions.AddAuthLockout`.** No standalone test — the filter is exercised by the endpoint integration tests in step 6.

4. **Wire `AddAuthLockout(builder.Configuration)` in `Program.cs`.** No pipeline changes; the filter attaches at endpoint registration in step 5.

5. **Decorate `/totp-challenge` and `/api/auth/unlock`:** inject `AuthLockoutService`, call `RecordFailureAsync` on failure path, `ClearAsync` on success path; chain `.AddEndpointFilter<LockoutGuardFilter>().WithMetadata(new LockoutKindMetadata(...))` after `.RequireRateLimiting(...)`. Two endpoints, two diffs.

6. **Add `TotpLockoutTests` + `UnlockLockoutTests`.** Use the same `FakeClock` + test factory pattern as 003a / 003b. Drive failure → lockout boundary; assert 423 body + header shape; assert success clears.

7. **Land `AuthLockoutSweepService` + `LockoutSweepTests`.** The sweep test needs a way to trigger one tick without waiting 6 hours — extract the body of `ExecuteAsync`'s while-loop into an `internal Task SweepOnceAsync(CancellationToken)` method, call it directly in the test. Keep the `PeriodicTimer` shell, but make the work unit testable.

8. **Wire `AddHostedService<AuthLockoutSweepService>()` in `Program.cs`.** One-liner.

9. **Manual smoke:** `dotnet run` + `curl` 21x against `/totp-challenge` from the same authenticated cookie (set `MaxFailures = 5` in `appsettings.Development.json` for the smoke). Observe 5x 429 (rate-limiter, 003c) — to test 423, lower the limiter to `PermitLimit = 100` temporarily and re-run. Observe 20x 401 + 1x 423 with `Retry-After`. Confirm the row in `auth_lockouts` via `psql`.

10. **Verify all acceptance criteria.** Commit only after.

## Risks & gotchas

- **Race between lockout filter and rate-limiter on same endpoint.** As noted above, the rate-limiter acquires a lease before the endpoint filter runs, so a 429 beats a 423 for the same request. In acceptance criteria, the "no 429-vs-423 ambiguity" test pins this. The pathological case — limiter says "go" but lockout says "no" — is fine: 423 returns, no failure is recorded (the filter short-circuits before the handler's `RecordFailureAsync`), and the lockout state is unchanged. The pathological reverse — limiter says "no" so the handler never runs — is also fine: no failure recorded, lockout state unchanged. **Neither path corrupts state.**

- **`auth_lockouts.kind` has no `CHECK` constraint.** A typo (e.g., `"Totp"` vs `"totp"`) writes a row that no filter or sweep cleans up. Use `AuthLockoutKinds` constants everywhere. Consider a follow-up migration to add `CHECK (kind IN ('totp', 'unlock', 'recovery'))` — but PORTAL-006 also adds `'recovery'`, so coordinating the check constraint across two tickets is fiddly. Skip for now; the constants are enough discipline.

- **`FailedCount` is `short` (PG `smallint`, max 32 767).** If a user genuinely accumulates more than 32 767 failures (impossible in practice — they'd be locked out after 20), the increment overflows. The cast `(short)(row.FailedCount + 1)` will overflow silently to negative in unchecked context. Add a guard: `row.FailedCount = (short)Math.Min(row.FailedCount + 1, short.MaxValue)`. Cheap defense; never fires in real use. Mentioned in the implementation code above implicitly; make it explicit.

- **`PortalDbContext` lifetime in the sweep.** `BackgroundService` is registered as singleton; `PortalDbContext` is scoped. The pattern in `AuthLockoutSweepService` uses `IServiceScopeFactory.CreateAsyncScope()` per tick — this is correct but easy to forget. **Do not** inject `PortalDbContext` directly into the sweep constructor; it'll resolve to a captive instance that gets disposed after the first tick. The compiler doesn't catch this; the runtime fails loudly on the second tick.

- **`PeriodicTimer` doesn't fire at startup.** The first tick is at `t = IntervalSeconds`. For a 6-hour interval, the first sweep happens 6 hours after startup. If you want a startup sweep (probably not — the table is consistent across restarts), prepend a manual call: `await SweepOnceAsync(stoppingToken)` before entering the `while` loop. Not in scope; mentioning so it's not surprising in tests.

- **`ExecuteDeleteAsync` and EF query interception.** The codebase uses `TimestampInterceptor` (registered in `Program.cs` line 45) to auto-stamp `created_at` / `updated_at`. `ExecuteDeleteAsync` skips the change tracker entirely — interceptors that hook `SavingChangesAsync` won't fire. **This is fine** for a DELETE; there's nothing to stamp. But if a future refactor changes the sweep to UPDATE (e.g., soft-delete via a `swept_at` column), the interceptor won't fire and the stamps will be wrong. Not relevant today; documenting the gotcha.

- **Test isolation across xUnit collection.** Lockout state persists in the DB; tests that exhaust a partition leak into subsequent tests. Two options: (a) `WithWebHostBuilder` to spin up a fresh database per test class (slow), (b) vary `userId` per test so each test has a fresh partition (fast, no DB reset). Follow PORTAL-003a / 003b conventions — they use option (b). For tests that *need* a known prior state (the "successful verify clears" test), explicitly seed and assert.

- **Sweep + active session.** A user with a 14-day cookie expiration (`/Program.cs:76`) who fails once at day 1, succeeds, then never fails again, gets `ClearAsync` called once which sets `LastAttemptAt = now`. The sweep retention is 30 days from `LastAttemptAt`. So a steady-state successful user has *one* `auth_lockouts` row per kind, refreshed on every login that touches the failure path. **Most users will never have a row** (no failures). The table stays small.

- **`Results.Json` content type.** Returns `application/json` with status 423. ASP.NET's default JSON serializer doesn't natively know how to serialize `Instant` — verify that `Microsoft.AspNetCore.Mvc.NewtonsoftJson` or `System.Text.Json` with the NodaTime converter is wired (see `Program.cs` JSON options). If not, the `locked_until` field will be `{}` (empty object). **Workaround:** `LockedUntil.Value.ToString("g", CultureInfo.InvariantCulture)` — explicit ISO-style string, no serializer dependency. The example code above does this. Don't trust serialization to "just work" for NodaTime types in this codebase; verify at implementation time and switch to explicit string if needed.

- **CORS preflight for the filter-decorated endpoints.** Same posture as 003c: same-origin SPA per [[portal_web_stack]], no preflight, no preflight counts against lockout. If a future tool introduces a different origin, revisit.

- **`AuthClaimTypes.SubUs` parsing.** `Guid.Parse` throws on malformed; the existing `/totp-challenge` handler does this directly (line 114 of `TotpEndpoints.cs`). The filter uses `Guid.TryParse` (returns 401 on parse failure) for defense in depth. Inconsistent but acceptable — the filter runs *before* the handler, so a malformed claim is the filter's problem; the handler can assume it's parseable by the time it runs (because the filter would have rejected). If the filter is disabled in a test, the handler still throws — accept that test setup must include the filter.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + green `dotnet test` + a manual `curl` smoke producing a 423 response with `Retry-After` after threshold failures. The pipeline order (filter slots inside endpoint pipeline; rate-limiter wraps endpoint dispatch; lockout filter short-circuits before handler runs) is documented in source ordering and acceptance tests only; no narrative comment in code per [[feedback_no_code_comments]].

A fresh agent can pick up PORTAL-003e (Cloudflare Turnstile) from cold by reading:

1. ADR-0031 §"CAPTCHA via Cloudflare Turnstile" + §"Ticket split".
2. PORTAL-003c's `OnRejected` callback in `AuthRateLimiterPolicies` — 003e wraps or replaces it to mark "captcha required on next attempt" for the rejected partition.
3. PORTAL-003d's `AuthLockoutService.IsLockedAsync` and `auth_lockouts.failed_count` — 003e reads the count (no schema change) to decide whether the next `/totp-challenge` / `/api/auth/unlock` attempt needs a Turnstile token. Threshold suggestion (not binding): 5 failures → render Turnstile, 20 → lockout (003d). 003e picks the exact number.
4. Memory `portal_architecture.md` DEC-003 (Cloudflare already in stack for DNS) — Turnstile site keys live under the same Cloudflare account.
5. Memory `portal_tooling.md` for the warnings-as-errors expectation.

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-003 monolithic auth — split into 003 / 003a–e per ADR-0031 §"Ticket split".
- Adjacent tickets PORTAL-003d unblocks / interacts with:
  - PORTAL-003c — in-memory rate limiting (already landed; 003d reads no state from it, but the two run in coordinate on the same endpoints — see "Risks & gotchas" for the 429-vs-423 interaction)
  - PORTAL-003e — Cloudflare Turnstile (reads `auth_lockouts.failed_count` to decide widget render; wraps 003c's `OnRejected` to mark "captcha required")
  - PORTAL-006 — recovery codes (lands `/api/auth/recovery-codes/redeem` endpoint + the `'recovery'` kind in `AuthLockoutKinds` + filter wiring; follows the pattern 003d establishes)
  - PORTAL-009 / PORTAL-010 — SSE endpoints (orthogonal; no lockout semantics for long-lived connections)
- Schema reference: `src/ThanyMarcus.Portal.Api/Features/Auth/AuthLockout.cs` + `AuthLockoutConfiguration.cs` (entity + EF map, both from PORTAL-002). Migration creating `auth_lockouts` already ran; 003d does **not** add a migration.
