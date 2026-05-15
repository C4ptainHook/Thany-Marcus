# PORTAL-003c Rate limiting (in-memory) — Handoff Brief

**Goal:** land the in-memory rate-limiting layer for the auth attack-surface endpoints. Three deliverables: (a) wire `Microsoft.AspNetCore.RateLimiting` into `Program.cs` with three named partitioned policies (`auth-totp-challenge`, `auth-unlock`, `signin-google`); (b) decorate `POST /totp-challenge` and `POST /api/auth/unlock` with `.RequireRateLimiting("…")`; (c) place a tiny path-scoped middleware in front of `UseAuthentication` that throttles the unauthenticated `/signin-google` OAuth callback by IP **before** the Google handler runs the code exchange. **No `auth_lockouts` writes (003d), no Turnstile (003e), no recovery-redeem policy (PORTAL-006 lands that with its endpoint), no Redis-backed distributed limiter.**

Estimated **0.5 person-day** — the `AddRateLimiter` config is ~40 LOC and the integration tests are mechanical, but the `/signin-google` pre-auth middleware + deterministic `FakeClock`-driven test harness for the sliding window are where the time goes.

## Where decisions live (read before doing anything)

- **`docs/decisions/0031-rate-limiting-and-lockout.md`** §"In-memory layer" + §"Ticket split" — the contract for this ticket. The four-row table (endpoint → limit / window / partition) is authoritative. 003c lands the first three rows; the fourth (`/api/auth/recovery-codes/redeem`) ships with PORTAL-006. §"Ticket split" reaffirms: PORTAL-003c is **only** the in-memory limiter, PORTAL-003d is persistent `auth_lockouts`, PORTAL-003e is Turnstile.
- **`docs/decisions/0030-auth-flow.md`** §"Cookie claim shape" + §"Sign-in sequence" — the partial-auth cookie carries `sub-us` *before* TOTP verification, so the `/totp-challenge` limiter can partition on `AuthClaimTypes.SubUs` even for `totp=not-verified` requests. §"Sign-in sequence" step 7 names `/signin-google` as the Google callback path (default of `AddGoogle`); this ticket gates that path by IP.
- **`plans/portal-003a-handoff.md`** §"Output" + `TotpEndpoints.cs` — `/totp-challenge` is registered as an *outside-the-group* endpoint (`app.MapPost("/totp-challenge", …).RequireAuthorization()`), not under the `/api/auth/totp` group. Decorate it directly; do not move it under the group.
- **`plans/portal-003b-handoff.md`** §"PassphraseEndpoints" — `/api/auth/unlock` is registered with `.RequireAuthorization(AuthPolicies.TotpRequired)` and lives in `Features/Auth/StepUp/PassphraseEndpoints.cs`. Append `.RequireRateLimiting("auth-unlock")` to that registration; do not modify the handler.
- **`plans/portal-003-handoff.md`** §`GoogleSignInHandler` — the Google `OnCreatingTicket` event runs *during* the `/signin-google` callback. The pre-auth limiter middleware sits **before** `UseAuthentication`, so a rejected request never reaches `GoogleSignInHandler.HandleAsync` — saving a DB hit + Google token exchange on rejection.
- **`docs/decisions/0023-test-stack.md`** — `WithTestAuth(...)` bakes the cookie with arbitrary claims. The rate-limiter tests need `FakeClock` to be the *same* clock the partitioned limiter consults; pass a `TimeProvider`-shaped wrapper (see "Clock injection" below). For the `/signin-google` test, the test factory needs a way to bypass the real Google handler — there's already a `WithMockGoogleAuth(...)` helper from PORTAL-003 integration tests; reuse it.
- **Memory files**: `portal_architecture.md` (Minimal APIs + VSA — these limiter policies live in a `Features/Auth/RateLimiting/` slice), `portal_tooling.md` (warnings-as-errors, OpenAPI surface — `RequireRateLimiting` doesn't change OpenAPI), `feedback_no_code_comments.md` (no narrative comments in source — the `OnRejected` callback's behavior is non-obvious, but the response shape is documented in the integration test, not in a comment).

**Do not re-litigate the policy shape.** ADR-0031 §"In-memory layer" closed: `PartitionedRateLimiter`, sliding fixed-window per ASP.NET's `FixedWindowLimiter`, no token-bucket, no leaky-bucket, no Redis. If a question seems open (e.g., "should the limit numbers be configurable from `appsettings.json`?") — yes, bind a `RateLimitingOptions` record so the *numbers* can change without a rebuild, but the *shape* (3 policies, partitioned per the ADR table) is fixed.

## Scope boundary (precise)

**In scope:**
- `Microsoft.AspNetCore.RateLimiting` (shared framework, no package add). `System.Threading.RateLimiting` is the underlying primitive (already transitively available).
- `Features/Auth/RateLimiting/` slice: `AuthRateLimiterPolicies` (static class with the 3 policy-name constants), `RateLimitingOptions` (record bound from `appsettings.json`), `SignInGoogleRateLimitMiddleware` (the pre-`UseAuthentication` IP-partitioned middleware), `RateLimiterServiceCollectionExtensions` (the `AddAuthRateLimiting` extension method that wires `AddRateLimiter`).
- Three named policies registered on `AddRateLimiter`:
  - `auth-totp-challenge` — `FixedWindowLimiter`, **5 permits / 5 min**, partition key = `sub-us` claim (when present) else `RemoteIpAddress`.
  - `auth-unlock` — `FixedWindowLimiter`, **5 permits / 5 min**, partition key = `sub-us` claim. Returns 401-not-429 if `sub-us` claim is missing (means cookie was stripped between `UseAuthentication` and limiter; should never happen but null-guard).
  - `signin-google` — `FixedWindowLimiter`, **20 permits / 1 min**, partition key = `RemoteIpAddress` (NAT-tolerant per ADR-0031 §"In-memory layer" footnote).
- `OnRejected` callback that writes `429 { error: "rate_limited", retry_after_seconds: N }` with `Retry-After: N` header (N = remaining seconds in the current window, computed from `RateLimitLease.TryGetMetadata(MetadataName.RetryAfter, out var retry)`).
- `app.UseRateLimiter()` placed **after** `UseAuthentication` so partition keys can read the populated `HttpContext.User`.
- `SignInGoogleRateLimitMiddleware` placed **before** `UseAuthentication` — it holds its own `PartitionedRateLimiter<HttpContext>` instance keyed by IP, doesn't read `HttpContext.User`. Returns 429 directly on reject.
- Decorate two endpoints:
  - `app.MapPost("/totp-challenge", …).RequireAuthorization().RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge)` — in `TotpEndpoints.cs`.
  - `app.MapPost("/api/auth/unlock", …).RequireAuthorization(AuthPolicies.TotpRequired).RequireRateLimiting(AuthRateLimiterPolicies.Unlock)` — in `PassphraseEndpoints.cs`.
- `appsettings.json` adds a `RateLimiting:` section so the limit numbers (permits + window seconds) can be tuned without rebuilding. The shape is **not** configurable — `RateLimitingOptions` is a closed record.
- Integration tests proving the eight acceptance criteria below.

**Out of scope (do not touch — each has its own ticket):**
- `auth_lockouts.kind = 'totp' | 'unlock'` row writes on rejected or failed requests — PORTAL-003d. The seam between 003c and 003d is **not** the limiter's `OnRejected` callback. ADR-0031 §"Layered behavior" is explicit: "in-memory limiter catches bursts within a window; persistent layer catches sustained attempts that survive across windows or process restarts" — 003d hooks the *endpoint's failure path* (the existing `TotpChallengeResult.Failed` / `UnlockResult.Failed` enums from 003a / 003b), not the limiter. Limiter rejections (429s) are bursts; sustained failures are what `auth_lockouts` cares about.
- Cloudflare Turnstile gating on the *next* attempt after a throttle event — PORTAL-003e. 003e will add a `IPostThrottleCaptchaTracker` singleton that 003c's `OnRejected` callback writes to. **Do not pre-add** that interface; let 003e shape it. If 003e needs a hook point in 003c, the seam is the `OnRejected` callback — 003e replaces or wraps it, not 003c.
- `/api/auth/recovery-codes/redeem` rate-limit policy (`auth-recovery-redeem` per ADR-0031 row 3) — PORTAL-006. That ticket lands both the endpoint and the policy together; 003c does not pre-register the policy.
- Distributed (Redis-backed) rate limiting — out of scope per [[portal_deployment]] (single-VM Docker Compose). ADR-0031 §"Negative" calls this out; the in-memory limiter is correct for the deployment shape.
- IP-allowlist bypass for localhost / health probes — not needed. `/health/live` and `/health/ready` are unauthenticated endpoints that are *not* decorated with `.RequireRateLimiting(...)`, so the limiter never sees them.

If a follow-up needs to extend this (e.g., 003e needs to track "captcha required" state on rejection), the seam is the `OnRejected` delegate in `AuthRateLimiterPolicies.ConfigureRateLimiter` — 003e replaces it with one that *also* writes to the captcha tracker. PORTAL-003c lands the delegate; nothing more.

## Output of PORTAL-003c — final directory state

```
Thany-Marcus/
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── Program.cs                                 # CHANGED: AddAuthRateLimiting(builder.Services, builder.Configuration);
│   │   │                                              #          app.UseMiddleware<SignInGoogleRateLimitMiddleware>() before UseAuthentication;
│   │   │                                              #          app.UseRateLimiter() after UseAuthentication
│   │   ├── appsettings.json                           # CHANGED: adds "RateLimiting" section (defaults match ADR-0031 table)
│   │   ├── appsettings.Development.json               # CHANGED: same shape (optional override; identical to defaults today)
│   │   └── Features/
│   │       └── Auth/
│   │           ├── RateLimiting/                      # NEW subfolder
│   │           │   ├── AuthRateLimiterPolicies.cs     # policy-name constants + ConfigureRateLimiter delegate
│   │           │   ├── RateLimitingOptions.cs         # record bound from "RateLimiting" config section
│   │           │   ├── SignInGoogleRateLimitMiddleware.cs  # pre-auth IP-partitioned limiter for /signin-google
│   │           │   └── RateLimiterServiceCollectionExtensions.cs  # AddAuthRateLimiting(...) extension method
│   │           ├── Totp/
│   │           │   └── TotpEndpoints.cs               # CHANGED: /totp-challenge gains .RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge)
│   │           └── StepUp/
│   │               └── PassphraseEndpoints.cs         # CHANGED: /api/auth/unlock gains .RequireRateLimiting(AuthRateLimiterPolicies.Unlock)
└── tests/
    └── ThanyMarcus.Portal.Tests/
        └── Features/
            └── Auth/
                └── RateLimiting/                      # NEW subfolder
                    ├── TotpChallengeRateLimitTests.cs           # integration: 5 OK + 1 429 with Retry-After; window advance resets
                    ├── UnlockRateLimitTests.cs                  # integration: 5 OK + 1 429; per-user partition (user A's bucket independent of user B)
                    ├── SignInGoogleRateLimitTests.cs            # integration: 20 OK + 1 429; per-IP partition (X-Forwarded-For respected when ForwardedHeaders is on)
                    └── RateLimitConfigurationTests.cs           # smoke: options bind from config; defaults match ADR-0031 table
```

No new packages. No new auth schemes. No policy additions to `AddAuthorization`. No DB migration.

## `appsettings.json` shape

```json
{
  "RateLimiting": {
    "TotpChallenge": { "PermitLimit": 5, "WindowSeconds": 300 },
    "Unlock":         { "PermitLimit": 5, "WindowSeconds": 300 },
    "SignInGoogle":   { "PermitLimit": 20, "WindowSeconds": 60 }
  }
}
```

`RateLimitingOptions`:

```csharp
public sealed record RateLimitingOptions
{
    public WindowOptions TotpChallenge { get; init; } = new(PermitLimit: 5,  WindowSeconds: 300);
    public WindowOptions Unlock        { get; init; } = new(PermitLimit: 5,  WindowSeconds: 300);
    public WindowOptions SignInGoogle  { get; init; } = new(PermitLimit: 20, WindowSeconds: 60);

    public sealed record WindowOptions(int PermitLimit, int WindowSeconds);
}
```

Defaults baked into the record match ADR-0031 exactly, so a missing config section still produces the right behavior — `appsettings.json` is for *tuning*, not *correctness*.

## `AuthRateLimiterPolicies.cs`

```csharp
public static class AuthRateLimiterPolicies
{
    public const string TotpChallenge = "auth-totp-challenge";
    public const string Unlock        = "auth-unlock";
    public const string SignInGoogle  = "signin-google";

    public static void Configure(RateLimiterOptions opts, RateLimitingOptions cfg)
    {
        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        opts.OnRejected = OnRejectedAsync;

        opts.AddPolicy(TotpChallenge, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: PartitionKeyForUser(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.TotpChallenge.PermitLimit,
                    Window      = TimeSpan.FromSeconds(cfg.TotpChallenge.WindowSeconds),
                    QueueLimit  = 0,
                }));

        opts.AddPolicy(Unlock, ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: PartitionKeyForUser(ctx),
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.Unlock.PermitLimit,
                    Window      = TimeSpan.FromSeconds(cfg.Unlock.WindowSeconds),
                    QueueLimit  = 0,
                }));
    }

    private static string PartitionKeyForUser(HttpContext ctx)
    {
        var sub = ctx.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (!string.IsNullOrEmpty(sub)) return "u:" + sub;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return "ip:" + ip;
    }

    private static async ValueTask OnRejectedAsync(OnRejectedContext ctx, CancellationToken ct)
    {
        var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
            ? (int)Math.Ceiling(retry.TotalSeconds)
            : 0;
        if (retryAfter > 0)
            ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString();
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsJsonAsync(
            new { error = "rate_limited", retry_after_seconds = retryAfter }, ct);
    }
}
```

Four judgment calls embedded above:

- **`FixedWindowLimiter` over `SlidingWindowLimiter` / `TokenBucketLimiter`.** A sliding window is more uniform but does double bookkeeping; a token bucket admits burst traffic up to the bucket size, which is the *opposite* of what we want for brute-force defense. Fixed window is the simplest shape that matches ADR-0031's "5 attempts in 5 min". The edge-of-window double-burst (attacker hits the boundary and gets 10 attempts in 10 seconds) is bounded by the persistent layer (PORTAL-003d) and is acceptable for a thesis prototype.
- **`QueueLimit = 0`** — reject immediately, don't queue. Queueing makes sense for traffic-shaping; for brute-force defense it just delays the 429 and ties up server threads. The endpoint is "fail fast or admit".
- **`PartitionKeyForUser` fallback to IP** — `/totp-challenge` is decorated with `.RequireAuthorization()`, so the cookie is guaranteed present and `sub-us` is guaranteed in the claim set by the time the limiter resolves the partition (the authorization middleware runs before `UseRateLimiter`'s endpoint resolution). The IP fallback is defensive; in practice it never fires. Logging a warning if it does is overkill — the null-guard is enough.
- **`OnRejected` writes JSON, not plaintext.** SvelteKit's `fetchWithStepUp` (from PORTAL-003b) and the TOTP challenge page parse JSON error bodies; an HTML or text response would confuse them. The `retry_after_seconds` field is for the client to schedule the next attempt; the `Retry-After` header is the standard HTTP signal. Sending both is cheap and aligns with the SPA conventions in [[portal_architecture]].

## `SignInGoogleRateLimitMiddleware.cs`

```csharp
public sealed class SignInGoogleRateLimitMiddleware : IMiddleware, IAsyncDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _limiter;

    public SignInGoogleRateLimitMiddleware(IOptions<RateLimitingOptions> opts)
    {
        var cfg = opts.Value.SignInGoogle;
        _limiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = cfg.PermitLimit,
                    Window      = TimeSpan.FromSeconds(cfg.WindowSeconds),
                    QueueLimit  = 0,
                }));
    }

    public async Task InvokeAsync(HttpContext ctx, RequestDelegate next)
    {
        if (!ctx.Request.Path.Equals("/signin-google", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        using var lease = await _limiter.AcquireAsync(ctx, permitCount: 1, ctx.RequestAborted);
        if (!lease.IsAcquired)
        {
            var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
                ? (int)Math.Ceiling(retry.TotalSeconds)
                : 0;
            if (retryAfter > 0)
                ctx.Response.Headers.RetryAfter = retryAfter.ToString();
            ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            await ctx.Response.WriteAsJsonAsync(
                new { error = "rate_limited", retry_after_seconds = retryAfter });
            return;
        }

        await next(ctx);
    }

    public async ValueTask DisposeAsync() => await _limiter.DisposeAsync();
}
```

Three points:

- **Why a separate middleware instead of decorating an endpoint** — `/signin-google` is **not** an endpoint registered via `MapGet/MapPost`; it's a *callback path* handled by `GoogleAuthenticationHandler` inside `UseAuthentication`. There is no `IEndpoint` to attach `.RequireRateLimiting(...)` to. The middleware sits **before** `UseAuthentication` so the OAuth code exchange (HTTP call to Google + DB write in `GoogleSignInHandler`) never happens on a rejected request. This is the load-bearing reason for this code.
- **Why `IMiddleware`** — singleton-friendly; the `PartitionedRateLimiter<HttpContext>` is stateful and must persist across requests. Using a per-request middleware (`Use(...)` lambda) would recreate the limiter on every request and lose all counts. Register as a singleton: `builder.Services.AddSingleton<SignInGoogleRateLimitMiddleware>();`.
- **`IAsyncDisposable`** — `PartitionedRateLimiter` owns timers and `IAsyncDisposable` is the documented cleanup path. The DI container calls `DisposeAsync` on host shutdown.

## Program.cs wiring (additions + ordering)

```csharp
// inside ConfigureServices block
builder.Services.AddAuthRateLimiting(builder.Configuration);
builder.Services.AddSingleton<SignInGoogleRateLimitMiddleware>();

// later — pipeline construction. Order is load-bearing.
app.UseStaticFiles();

app.UseMiddleware<SignInGoogleRateLimitMiddleware>();   // BEFORE UseAuthentication: rejects before Google handler
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();                                   // AFTER UseAuthentication: claims populated for partition keys
```

`AddAuthRateLimiting` extension method:

```csharp
public static IServiceCollection AddAuthRateLimiting(this IServiceCollection services, IConfiguration cfg)
{
    services.AddOptions<RateLimitingOptions>()
            .Bind(cfg.GetSection("RateLimiting"));
    services.AddRateLimiter(opts =>
    {
        var snapshot = cfg.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        AuthRateLimiterPolicies.Configure(opts, snapshot);
    });
    return services;
}
```

The `Get<RateLimitingOptions>()` snapshot inside `AddRateLimiter` is a one-time read at startup — the rate-limiter middleware doesn't support `IOptionsMonitor` reloads, so tuning a limit at runtime requires a process restart. This is fine for a thesis prototype; if it ever matters, the workaround is a custom `IRateLimiterPolicy<>` that reads `IOptionsSnapshot` per acquisition, but that's premature.

## Endpoint decorations

`TotpEndpoints.cs`:

```csharp
app.MapPost("/totp-challenge", async (
    TotpChallengeRequest body,
    ClaimsPrincipal user,
    HttpContext http,
    PortalDbContext db,
    TotpService totp,
    TotpBackupCodeService backups,
    CancellationToken ct) => { /* unchanged */ })
   .RequireAuthorization()
   .RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge);   // ADD
```

`PassphraseEndpoints.cs` (lands in PORTAL-003b; 003c is the *follow-up* that adds the limiter — if 003b is not yet merged when 003c starts, sequence accordingly):

```csharp
app.MapPost("/api/auth/unlock", async (…) => { /* unchanged */ })
   .RequireAuthorization(AuthPolicies.TotpRequired)
   .RequireRateLimiting(AuthRateLimiterPolicies.Unlock);          // ADD
```

`/api/auth/passphrase/init` is **not** rate-limited. ADR-0031 §"In-memory layer" doesn't list it, and the threat model differs — `/init` is a one-time setup call gated by `AuthPolicies.TotpRequired`. An attacker with TOTP-verified session access has already won; rate-limiting `/init` doesn't help.

## Clock injection for tests

`FixedWindowLimiter` consults `TimeProvider.System` for window expiry. To drive it from `FakeClock` in integration tests, override `TimeProvider` in the test factory:

```csharp
builder.Services.AddSingleton<TimeProvider>(_ => fakeTimeProvider);
```

ASP.NET's `RateLimiterOptions.TimeProvider` property (set inside `Configure`) wires the limiter to use the registered `TimeProvider`. Tests can then call `fakeTimeProvider.Advance(TimeSpan.FromMinutes(5))` to roll the window without a real wall-clock wait.

`AuthRateLimiterPolicies.Configure` reads `TimeProvider` from the service provider when integrating with the test harness. The simplest shape: pass `TimeProvider` as a parameter to `Configure`:

```csharp
public static void Configure(RateLimiterOptions opts, RateLimitingOptions cfg, TimeProvider clock)
{
    opts.TimeProvider = clock;
    // … rest unchanged
}
```

And in `AddAuthRateLimiting`:

```csharp
services.AddRateLimiter(opts =>
{
    var sp = services.BuildServiceProvider();
    var clock = sp.GetService<TimeProvider>() ?? TimeProvider.System;
    var snapshot = cfg.GetSection("RateLimiting").Get<RateLimitingOptions>() ?? new RateLimitingOptions();
    AuthRateLimiterPolicies.Configure(opts, snapshot, clock);
});
```

The `BuildServiceProvider` call inside a `ConfigureServices` lambda is normally an anti-pattern (it captures a snapshot), but `AddRateLimiter`'s configure delegate runs once at startup, after all registrations, so it's safe here. Alternative: thread `TimeProvider` through `IOptions<>` or a custom `RateLimiterContext` factory; the snapshot pattern is shorter.

`SignInGoogleRateLimitMiddleware` also needs `TimeProvider`:

```csharp
public SignInGoogleRateLimitMiddleware(IOptions<RateLimitingOptions> opts, TimeProvider clock)
{
    // … pass clock to PartitionedRateLimiter.Create via FixedWindowRateLimiterOptions or — actually,
    // FixedWindowRateLimiterOptions doesn't expose TimeProvider directly. Pass it via the
    // limiter's options where supported. For .NET 10, FixedWindowRateLimiterOptions exposes
    // TimeProvider as a settable property — verify in the SDK reference at implementation time.
}
```

If the SDK doesn't expose `TimeProvider` on the options record (rare in .NET 10 but verify), the fallback is a test-only `IClock`-aware custom limiter, or `Microsoft.Extensions.TimeProvider.Testing`'s `FakeTimeProvider` registered as `TimeProvider.System`'s replacement. The .NET 8+ `FakeTimeProvider` package is the standard test-time clock for limiter-aware tests.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings**.
- All existing PORTAL-003 / 003a / 003b tests still green; new rate-limit tests all green.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts cleanly. No new endpoints in OpenAPI (the policies are wiring, not surface). Hitting an existing endpoint without exhausting the limit returns the same response shape as before; OpenAPI consumers see no change.
- **`TotpChallengeRateLimitTests` (integration):** with `WithTestAuth(userId, totp: NotVerified)`, 5 sequential `POST /totp-challenge { code: "000000" }` calls all return 401 (wrong code) — the limiter admits them. The 6th call returns **429** with body `{ "error": "rate_limited", "retry_after_seconds": N }` (N > 0) and a `Retry-After: N` header. After advancing `FakeTimeProvider` by `WindowSeconds + 1`, the 7th call returns 401 (admitted again).
- **Per-user partition isolation (`TotpChallengeRateLimitTests`):** user A exhausts the limit (5 calls); user B's first call still returns 401, not 429. Cookies / `sub-us` claims partition independently.
- **`UnlockRateLimitTests` (integration):** with `WithTestAuth(userId, totp: Verified)` + a passphrase set, 5 sequential `POST /api/auth/unlock { passphrase: "wrong" }` calls return 401 (`invalid_passphrase`). The 6th returns 429 with the same body shape + `Retry-After`. **Successful** unlocks also count against the limit (the limiter doesn't distinguish — admission cost = 1 per call, success or failure). This is intentional: a user mashing the unlock button accidentally is rare; allowing unlimited *successful* unlocks would let an attacker who has the right passphrase bypass any future hardening. ADR-0031 §"In-memory layer" doesn't distinguish; 003c follows.
- **`SignInGoogleRateLimitTests` (integration):** 20 sequential `GET /signin-google?code=…` calls (with a mocked Google handler so they don't actually call Google) return whatever the auth handler returns. The 21st returns **429** *before* the Google handler runs — assertable by counting `GoogleSignInHandler.HandleAsync` invocations (mock + counter), which should be 20, not 21.
- **`SignInGoogleRateLimitTests` partition by IP:** request from `192.0.2.10` exhausts; request from `192.0.2.11` still admitted. Test factory needs to set `RemoteIpAddress` per request (override via `httpContext.Connection.RemoteIpAddress = IPAddress.Parse(...)` in a custom test handler).
- **`RateLimitConfigurationTests` (smoke):** boot the host with `appsettings.Test.json` overriding `RateLimiting:TotpChallenge:PermitLimit = 2`; assert the 3rd `POST /totp-challenge` returns 429. Confirms config binding works.

## Concrete steps in order (each maps to a task)

1. **Land `RateLimitingOptions`.** Trivial record + nested `WindowOptions`. Defaults match ADR-0031.

2. **Land `AuthRateLimiterPolicies` + `RateLimiterServiceCollectionExtensions`.** No tests yet — the policies are exercised by the endpoint-level integration tests in step 6.

3. **Land `SignInGoogleRateLimitMiddleware` + smoke test.** Smoke test: register the middleware, make 20 GETs to `/signin-google` (with auth handler bypassed via test factory), assert the 21st returns 429. No assertion on the body of the first 20 — the goal is the rejection boundary.

4. **Wire `app.UseMiddleware<SignInGoogleRateLimitMiddleware>()` before `UseAuthentication`** in `Program.cs`. Wire `app.UseRateLimiter()` after `UseAuthentication`. Verify pipeline order doesn't break any existing integration test.

5. **Decorate `/totp-challenge` and `/api/auth/unlock`** with `.RequireRateLimiting(...)`. One-line additions to `TotpEndpoints.cs` and `PassphraseEndpoints.cs`. Confirm existing TOTP / unlock integration tests still pass (none of them make more than 4 calls per partition; the limit is 5).

6. **Add `TotpChallengeRateLimitTests` + `UnlockRateLimitTests` + `SignInGoogleRateLimitTests`.** All three follow the same shape: drive `FakeTimeProvider`, assert the rejection boundary, assert per-partition isolation. The `TimeProvider` wiring (step 7) is what makes these deterministic — until then, tests would have to `Task.Delay(5 min)` which is unacceptable.

7. **Wire `TimeProvider` through `Configure` + the middleware.** Verify `FixedWindowRateLimiterOptions.TimeProvider` is settable in .NET 10 (it should be; .NET 8 added the property under preview). If not, fall back to `Microsoft.Extensions.TimeProvider.Testing.FakeTimeProvider` registered as `TimeProvider` in the test factory and rely on `TimeProvider.System` replacement.

8. **Add `RateLimitConfigurationTests`.** Override config in the test factory; assert the limit changes.

9. **Manual smoke:** `dotnet run` + `curl` 6x against `/totp-challenge` from the same authenticated cookie; observe 5x 401 + 1x 429 with `Retry-After`. Manual check via `curl -i` to see the header is present.

10. **Verify all acceptance criteria.** Commit only after.

## Risks & gotchas

- **`FixedWindowLimiter` edge-of-window double burst.** An attacker who lands attempt 5 at second 299 of window N and attempt 1 at second 0 of window N+1 gets 10 attempts in ~1 second. This is a known fixed-window pathology. ADR-0031 §"Layered behavior" assumes 003d's persistent layer catches sustained patterns that span windows. Sliding window would close this gap but doubles bookkeeping; not worth it for a thesis prototype. Document the boundary behavior in `RateLimitConfigurationTests` so future me (or 003d's author) doesn't waste time "fixing" it.

- **`UseRateLimiter` ordering vs `UseAuthorization`.** The endpoint-decorated limiters resolve `HttpContext.User` for partition keys. `UseAuthorization` populates the user's *authorization* results but not the principal — `UseAuthentication` is what assigns `HttpContext.User`. So `UseRateLimiter` after `UseAuthentication` is sufficient; placing it after `UseAuthorization` is also fine and is the more common order (auth → authz → rate-limit). Keep `UseRateLimiter` last among the three.

- **`SignInGoogleRateLimitMiddleware` ordering before `UseAuthentication` matters for a different reason.** If placed after, the Google handler's `OnCreatingTicket` event has already fired by the time the limiter rejects — that's a wasted DB write in `GoogleSignInHandler.HandleAsync` plus an HTTP roundtrip to Google's token endpoint. The middleware's value is the *cost saved on rejection*, not just the 429 response.

- **`X-Forwarded-For` and `RemoteIpAddress`.** Behind Caddy ([[portal_architecture]] DEC-005 / ADR-0027), `RemoteIpAddress` is the loopback address of the proxy unless `UseForwardedHeaders` is configured. PORTAL-003 already wires forwarded-headers middleware before `UseAuthentication` per the reverse-proxy ADR — verify before starting. If not, the `signin-google` limiter partitions everyone into the same IP partition and the limit becomes global. **This is a deal-breaker for the per-IP partition guarantee.** Read `Program.cs` for `UseForwardedHeaders` before writing the middleware; if missing, add it (small follow-up, low risk).

- **`FakeTimeProvider` package add.** Tests need `Microsoft.Extensions.TimeProvider.Testing` (NuGet) for `FakeTimeProvider`. Add it to `Directory.Packages.props` under the test scope only:
  ```xml
  <PackageVersion Include="Microsoft.Extensions.TimeProvider.Testing" Version="9.*" />
  ```
  And reference from `ThanyMarcus.Portal.Tests.csproj` only. Not needed by the API project.

- **`PartitionedRateLimiter<HttpContext>` thread-safety.** The limiter is concurrent-safe; multiple requests on the same partition acquire serially. Tests that fire 100 parallel requests against the same partition should expect exactly `PermitLimit` to succeed and the rest to fail — assert via counting, not via individual order.

- **`OnRejected` and SSE / WebSocket endpoints.** Future SSE endpoints ([[portal_architecture]] DEC-002 — server push via SSE) may also need rate limiting, but with very different parameters (long-lived connections). 003c does not pre-emptively shape those policies. When the SSE feature lands (PORTAL-009 / 010 territory), a new policy joins `AuthRateLimiterPolicies` or sits in its own slice.

- **CORS preflight (`OPTIONS`).** `RequireRateLimiting` decorates the *endpoint*, which means preflight `OPTIONS` requests also count against the limit. For same-origin SPA traffic (Portal.Web served from Portal.Api wwwroot per [[portal_web_stack]]), there are no cross-origin preflights, so this doesn't bite. If a future tool (e.g., a CLI hitting the API from a different origin) introduces preflights, revisit.

- **Test cache pollution across xUnit collection.** The rate-limiter is a singleton; tests that exhaust a partition leak into subsequent tests in the same collection. Use `WithWebHostBuilder` to create a fresh factory per test class (the PORTAL-003 / 003a / 003b test setup already does this — follow that pattern). Alternative: vary the `sub-us` user id per test so each test uses a fresh partition. Simpler.

- **`appsettings.Development.json` vs `appsettings.json`.** The defaults in `RateLimitingOptions` are correct; the `RateLimiting` section in `appsettings.json` only exists to make the values *visible* to ops (greppable, tunable). Don't duplicate the section in `appsettings.Development.json` unless dev wants different numbers (e.g., higher limits for manual exploration). Lower limits in dev would surprise developers; higher would mask real-world behavior. Keep dev = prod for the defaults.

- **Recovery-redeem policy intentionally absent.** ADR-0031 row 3 names `auth-recovery-redeem` with 3/1h. Do **not** pre-register it in 003c. The reason: a registered-but-unused policy is dead code, and `RequireRateLimiting("name-that-doesn't-exist")` throws at startup — the *coupling* between policy registration and endpoint decoration is what PORTAL-006 wants to land atomically with the new endpoint. Pre-registering creates the temptation for someone to wire it to a non-existent endpoint by mistake.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + green `dotnet test` + a manual `curl` smoke against `/totp-challenge` and `/signin-google` producing the 6th / 21st request 429 with `Retry-After`. The pipeline order (`SignInGoogleRateLimitMiddleware` → `UseAuthentication` → `UseAuthorization` → `UseRateLimiter`) is documented in `Program.cs` source ordering only; no narrative comment needed per [[feedback_no_code_comments]].

A fresh agent can pick up PORTAL-003d (persistent lockout) from cold by reading:

1. ADR-0031 §"Persistent lockout layer" + §"Ticket split" + §"Layered behavior".
2. PORTAL-003c's `AuthRateLimiterPolicies.OnRejectedAsync` — **not** as a hook point, but to confirm 003d does *not* attach there.
3. The existing `TotpChallengeResult.Failed` (003a) and `UnlockResult.Failed` (003b) enums — these are 003d's actual hook points.
4. `AuthLockout.cs` (already in PORTAL-002 schema per `Features/Auth/AuthLockout.cs`) — the table exists; 003d wires reads/writes.
5. Memory `portal_tooling.md` for the warnings-as-errors expectation.

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-003 monolithic auth — split into 003 / 003a–e per ADR-0031 §"Ticket split".
- Adjacent tickets PORTAL-003c unblocks / interacts with:
  - PORTAL-003d — persistent lockout (hooks `*Result.Failed` enums to write `auth_lockouts` rows; reads `auth_lockouts.locked_until` *before* the rate-limiter in a separate middleware to short-circuit known-locked users)
  - PORTAL-003e — Cloudflare Turnstile (wraps or replaces 003c's `OnRejected` callback to mark the partition "captcha required on next attempt"; renders the widget after a 429)
  - PORTAL-006 — recovery codes (lands `/api/auth/recovery-codes/redeem` endpoint **and** the `auth-recovery-redeem` policy together, decorating with `.RequireRateLimiting("auth-recovery-redeem")` as the endpoint registration's first decorator)
  - PORTAL-009 / PORTAL-010 — SSE endpoints (may add separate long-lived-connection policies; orthogonal shape)
