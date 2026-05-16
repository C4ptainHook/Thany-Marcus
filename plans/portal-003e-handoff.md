# PORTAL-003e Cloudflare Turnstile — Handoff Brief

**Goal:** land the CAPTCHA layer on top of the rate-limiter (003c) + persistent lockout (003d). Four deliverables: (a) `ITurnstileValidator` — a thin client over Cloudflare's `siteverify` endpoint; (b) `CaptchaRequirementTracker` — an in-memory singleton that remembers "this partition recently hit the limiter; require Turnstile on the next attempt" with TTL; (c) `RequireTurnstileFilter` — an endpoint filter that validates the Turnstile token from a header and short-circuits with **428 Precondition Required** when required-but-missing; (d) a `/api/auth/captcha-state` read endpoint that tells the SPA whether to render the widget, plus the SPA wiring on the totp-challenge / step-up / sign-in surfaces. **No new database tables, no schema changes, no new rate-limit policies, no new lockout kinds.** The persistent `auth_lockouts.failed_count` from 003d is read but never written here.

Estimated **1 person-day** — the `siteverify` client is ~40 LOC, the tracker is ~30 LOC, the filter is ~50 LOC; the time is in (i) wiring two hook points into 003c's existing `OnRejected` / `SignInGoogleRateLimitMiddleware` without breaking their tests, (ii) the SPA's per-surface widget render + token threading, and (iii) the end-to-end integration tests that stub the siteverify HTTP call deterministically.

## Where decisions live (read before doing anything)

- **`docs/decisions/0031-rate-limiting-and-lockout.md`** §"CAPTCHA via Cloudflare Turnstile" + §"Ticket split" + §"Layered behavior" — the contract for this ticket. Two trigger surfaces named explicitly: (1) `/signin-google` callback "after the first throttle event from that partition" — partition is **per-IP**, signal is the in-memory tracker; (2) `/totp-challenge` and `/api/auth/unlock` "after any in-memory throttle event for that user" — partition is **per-user (`sub-us`)**, signal is the in-memory tracker. ADR-0031 §"Layered behavior" is silent on whether persistent `auth_lockouts.failed_count` also triggers Turnstile; 003e adds that as a *second* signal because 003d's 003e-handoff section says so explicitly (`auth_lockouts.failed_count >= 5 → show captcha, regardless of whether locked_until is set`). Both signals are OR-ed in `RequireTurnstileFilter.IsCaptchaRequiredAsync`.
- **`plans/portal-003c-handoff.md`** §"`AuthRateLimiterPolicies.cs`" + §"`SignInGoogleRateLimitMiddleware.cs`" + §"Risks & gotchas" — the *exact* seams 003e plugs into. The 003c brief calls them out: "the seam is the `OnRejected` delegate in `AuthRateLimiterPolicies.ConfigureRateLimiter` — 003e replaces it with one that *also* writes to the captcha tracker" and "003e wraps or replaces 003c's `OnRejected` callback to mark the partition 'captcha required on next attempt'". 003e **adds a side-effect**, it does **not** rewrite the delegate. The 429 response shape stays identical; the tracker write is an `await tracker.MarkRequiredAsync(partitionKey, ttl, ct)` line inserted before the JSON write. Same for `SignInGoogleRateLimitMiddleware`'s rejection branch.
- **`plans/portal-003d-handoff.md`** §"Cross-references" + §"Definition of done" §"PORTAL-003e (Cloudflare Turnstile)" — confirms 003e reads `auth_lockouts.failed_count` (no schema change), suggests "5 failures → render Turnstile, 20 → lockout (003d)" as a starting threshold (003e picks the exact number; defensible default = 5, configurable via `Turnstile:FailureThreshold`). The 003d brief also names `AuthLockoutService` as the read surface — but `AuthLockoutService` exposes `IsLockedAsync` (boolean) and not the raw count. **003e adds a single read method** `GetFailedCountAsync(userId, kind, ct)` to `AuthLockoutService`, or alternatively the filter queries `db.AuthLockouts` directly. Choose the latter: a one-line `AsNoTracking().Where(...).Select(a => (short?)a.FailedCount).SingleOrDefaultAsync(ct)` keeps the lockout service's surface tight. The `AsNoTracking` matters — this is on the hot path for every gated request.
- **`docs/decisions/0030-auth-flow.md`** §"Cookie claim shape" + §"Sign-in sequence" — the partial-auth cookie carries `sub-us` *before* TOTP verification, so the per-user captcha tracker for `/totp-challenge` partitions on `sub-us` (same as 003c's limiter and 003d's filter). The Google callback path `/signin-google` is pre-authentication — there is no `sub-us` claim yet; partition by `RemoteIpAddress`. The /api/auth/signin Challenge endpoint (`AuthEndpoints.cs:50`) is *also* pre-auth (it issues the Google challenge); 003e gates the *Challenge* endpoint, **not** the OAuth callback, because the callback is owned by `AddGoogle`'s middleware and the user never sees it directly (it's a 302 from Google).
- **`docs/decisions/0023-test-stack.md`** — `WithTestAuth` / `WithRemoteIpHeader` / `WithRateLimitConfig` from `tests/Infrastructure/RateLimitTestExtensions.cs` are the test seams. 003e adds one more: `WithTurnstileValidator(StubTurnstileValidator)` to bypass the real Cloudflare siteverify call. The stub returns `Pass` or `Fail` per-test deterministically; no HTTP, no network. **Do not** use `HttpClientFactory` + a `DelegatingHandler` for stubbing — that's the right pattern for production but adds wiring complexity for tests. Inject `ITurnstileValidator` directly.
- **`docs/decisions/0026-observability-and-health-checks.md`** — `siteverify` is an outbound HTTP dependency on the auth-critical path. ADR-0031 §"Consequences" §"Negative" calls it out: "Cloudflare Turnstile adds one upstream dependency on the auth-critical path; outage there breaks sign-in." 003e adds a Prometheus counter `portal_turnstile_siteverify_total{outcome="pass|fail|error"}` and OTel tracing on the HTTP call so a Cloudflare outage is visible without log-archaeology. The `error` bucket is for HTTP-level failures (timeout, 5xx); on `error` the filter **fails open** (admits the request) per the "outage breaks sign-in" mitigation — this is a deliberate availability-over-strictness tradeoff for a thesis prototype. Documented in "Risks & gotchas".
- **Memory files**: `portal_architecture.md` DEC-003 (Cloudflare already in stack for DNS — Turnstile site keys live under the same Cloudflare account), `portal_web_stack.md` (SvelteKit SPA — the widget loads via `https://challenges.cloudflare.com/turnstile/v0/api.js`, no npm package), `portal_tooling.md` (warnings-as-errors; `IHttpClientFactory` for the siteverify call so we get OTel + Polly retries for free), `portal_deployment.md` (single-VM Docker Compose — the singleton tracker is correct; no Redis), `feedback_no_code_comments.md` (no narrative comments; the dual-signal logic in `IsCaptchaRequiredAsync` is non-obvious but integration test names document it).

**Do not re-litigate the vendor choice.** ADR-0031 §"CAPTCHA via Cloudflare Turnstile" closed on Cloudflare (not reCAPTCHA, not hCaptcha, not self-hosted) because the Cloudflare account already exists for DNS. If a question seems open (e.g., "should the captcha be required on `/api/auth/passphrase/init`?") — no, same reasoning as 003c §"Out of scope" and 003d §"Scope boundary": `/init` is a one-time setup call gated by `TotpRequired`; an attacker with that access has already won.

## Scope boundary (precise)

**In scope:**
- `Features/Auth/Captcha/` slice: `TurnstileOptions` (record bound from `appsettings.json`), `ITurnstileValidator` + `TurnstileValidator` (HTTP client over `siteverify`), `CaptchaRequirementTracker` (in-memory singleton, partition-key → expiry `Instant`), `RequireTurnstileFilter` (endpoint filter), `CaptchaEndpoints` (the `GET /api/auth/captcha-state` read endpoint), `CaptchaServiceCollectionExtensions` (`AddTurnstile(...)` wiring helper).
- Three gated surfaces:
  - `/totp-challenge` — partition `sub-us`, dual-signal (tracker OR `auth_lockouts.failed_count >= FailureThreshold` for kind `'totp'`).
  - `/api/auth/unlock` — partition `sub-us`, dual-signal (tracker OR `auth_lockouts.failed_count >= FailureThreshold` for kind `'unlock'`).
  - `/api/auth/signin` (the Challenge endpoint, **not** `/signin-google` callback) — partition `RemoteIpAddress`, single-signal (tracker only; there's no per-IP failure count).
- Two write sites in 003c (additive — do not rewrite existing logic):
  - `AuthRateLimiterPolicies.OnRejectedAsync` — after computing `retryAfter`, before writing the JSON response, `await tracker.MarkRequiredAsync(partitionKey, ttl, ct)`. The `partitionKey` is the same string the limiter used; expose it via `RateLimitPartition.PartitionKey` on the lease or recompute via the same `PartitionKeyForUser(ctx)` helper (the limiter doesn't surface the resolved key on `OnRejectedContext`, so recompute — the helper is pure).
  - `SignInGoogleRateLimitMiddleware.InvokeAsync` — in the `!lease.IsAcquired` branch, mark `"ip:" + RemoteIpAddress` as required before writing the 429 body.
- `RequireTurnstileFilter` returns **428 Precondition Required** with body `{ "error": "captcha_required", "site_key": "<public site key>" }` when the captcha is required and the token is missing/invalid. The `site_key` echo lets a "blind" client that didn't pre-check `/api/auth/captcha-state` still render the widget from the error response. The filter looks for the token in the `cf-turnstile-response` header first, then (for the `/api/auth/signin` GET) a `turnstile` query string parameter — the GET-vs-POST split is unavoidable because the Challenge endpoint is a 302-issuing GET that the browser navigates to directly; headers can't be set on a `<a href>`.
- `GET /api/auth/captcha-state` returns `{ "required": bool, "site_key": "<public site key>" }`. Two query modes:
  - `?kind=totp` / `?kind=unlock` — requires the partial-auth cookie (`.RequireAuthorization()`); reads tracker for `"u:" + sub` AND the matching `auth_lockouts.failed_count`.
  - `?kind=signin` — unauthenticated (the user has no cookie yet); reads tracker for `"ip:" + RemoteIpAddress` only. **Rate-limited** by 003c's `signin-google` policy? No — that policy is path-scoped to `/signin-google`. Add a separate `.RequireRateLimiting(...)` here? Out of scope; a probing attacker who hits `/api/auth/captcha-state?kind=signin` 1000x learns nothing they couldn't infer from sending a real sign-in request. Skip.
- Cloudflare Turnstile site keys (`SiteKey` public, `SecretKey` private) live in `appsettings.json` (`SiteKey` only) + user-secrets / env (`SecretKey`). The `appsettings.json` `Turnstile:SiteKey` is empty by default; if empty at startup, the filter **disables itself** (treats every request as captcha-not-required) and logs a startup warning. This keeps `dotnet run` working without Cloudflare credentials in dev — a developer who explicitly wants to exercise the captcha sets `Turnstile:SiteKey` + `Turnstile:SecretKey` to Cloudflare's published **test keys** (always-pass, always-fail, always-challenge variants documented at `https://developers.cloudflare.com/turnstile/troubleshooting/testing/`). Memory file [[portal_tooling]] §dev experience: "dev defaults must not require external accounts."
- Frontend wiring (`src/ThanyMarcus.Portal.Web/src/lib/`):
  - `turnstileClient.ts` — fetches `/api/auth/captcha-state`, lazy-loads `https://challenges.cloudflare.com/turnstile/v0/api.js`, exposes `renderTurnstile(container, siteKey): Promise<string>` that resolves with the token on success.
  - `TurnstileWidget.svelte` — wraps `renderTurnstile` for use inside other forms; bubbles up the token via a callback prop.
  - `totp-challenge/+page.svelte` — on mount, calls captcha-state with `kind=totp`; if required, renders `<TurnstileWidget />` above the submit button; appends `cf-turnstile-response` header to the `POST /totp-challenge` call.
  - `StepUpModal.svelte` (existing) — same pattern with `kind=unlock`; appends header to `POST /api/auth/unlock`.
  - `+page.svelte` (root, where sign-in button lives) — on mount, calls captcha-state with `kind=signin`; if required, renders inline widget; the "Sign in with Google" button becomes disabled until a token is acquired, then the button's href changes from `/api/auth/signin` to `/api/auth/signin?turnstile=<TOKEN>`.
- Integration tests proving the seven acceptance criteria below.

**Out of scope (do not touch — each has its own ticket or is YAGNI):**
- `auth_lockouts` schema changes — no new column for "captcha-cleared-at" or similar. The failed-count threshold is sufficient; clearing the captcha requirement happens via tracker TTL or a successful auth (which calls `AuthLockoutService.ClearAsync` and resets `failed_count` to 0 — same call site, no change).
- Recovery-codes endpoint captcha — PORTAL-006 lands `/api/auth/recovery-codes/redeem`; that ticket adds its own `RequireTurnstileFilter` decoration and a `?kind=recovery` branch in `CaptchaEndpoints`. 003e leaves the `kind` switch open for extension via the same `AuthLockoutKinds`-style constant pattern.
- Server-side `Polly` retries on `siteverify` timeout — `IHttpClientFactory` gives basic resilience; a Polly handler is overkill for a thesis prototype. The filter fails open on HTTP error (see "Risks & gotchas"), so a flaky Cloudflare endpoint degrades to "no captcha enforced" rather than "auth broken."
- Per-user captcha bypass (e.g., trusted-device cookie that skips captcha for 30 days) — not in spec. The tracker TTL (15 min) is the only "skip" mechanism.
- Distributed tracker coordination across Portal instances — out of scope per [[portal_deployment]] (single-VM Docker Compose). The tracker is `ConcurrentDictionary<string, Instant>` in memory; a multi-instance deployment would need Redis, which is a separate decision (revisit if/when the deployment shape changes).
- Cloudflare Turnstile **Enterprise** features (action / cdata, analytics dashboard, custom widget themes) — free tier is sufficient. The widget renders in the page's default theme; no customization.
- A `/api/auth/captcha-verify` endpoint that pre-validates the token without making a real auth attempt — the SPA can use the token directly on the next request; pre-verification is just a wasted round-trip.

If a follow-up needs to extend this (e.g., 006 wants captcha on recovery-redeem), the seam is `CaptchaEndpoints.cs`'s `kind` switch — add a `'recovery'` branch that reads `auth_lockouts.failed_count` for `AuthLockoutKinds.Recovery`. The filter itself is generic over kind via the same `LockoutKindMetadata` pattern 003d established — `RequireTurnstileFilter` reads metadata, looks up the appropriate `KindOptions`, runs the check.

## Output of PORTAL-003e — final directory state

```
Thany-Marcus/
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── Program.cs                                 # CHANGED: services.AddTurnstile(builder.Configuration);
│   │   │                                              #          AddHttpClient<ITurnstileValidator, TurnstileValidator>()
│   │   ├── appsettings.json                           # CHANGED: adds "Turnstile" section (empty SiteKey by default → filter disabled)
│   │   ├── appsettings.Development.json               # CHANGED: optional — sets Cloudflare test keys for local exploration
│   │   └── Features/
│   │       └── Auth/
│   │           ├── Captcha/                           # NEW subfolder
│   │           │   ├── TurnstileOptions.cs            # SiteKey, SecretKey, FailureThreshold, TrackerTtlSeconds, SiteVerifyUrl
│   │           │   ├── ITurnstileValidator.cs         # interface: Task<TurnstileVerifyResult> VerifyAsync(token, remoteIp, ct)
│   │           │   ├── TurnstileValidator.cs          # HttpClient-based impl that POSTs siteverify
│   │           │   ├── CaptchaRequirementTracker.cs   # singleton: MarkRequiredAsync / IsRequiredAsync / Clear (ConcurrentDictionary + sweep)
│   │           │   ├── RequireTurnstileFilter.cs      # IEndpointFilter — 428 on missing/invalid token
│   │           │   ├── CaptchaEndpoints.cs            # GET /api/auth/captcha-state
│   │           │   └── CaptchaServiceCollectionExtensions.cs   # AddTurnstile(...) extension method
│   │           ├── RateLimiting/
│   │           │   ├── AuthRateLimiterPolicies.cs     # CHANGED: OnRejectedAsync writes to CaptchaRequirementTracker
│   │           │   └── SignInGoogleRateLimitMiddleware.cs   # CHANGED: rejection branch writes to tracker
│   │           ├── Totp/
│   │           │   └── TotpEndpoints.cs               # CHANGED: /totp-challenge gains .AddEndpointFilter<RequireTurnstileFilter>();
│   │           │                                      #          metadata: TurnstileKindMetadata(AuthLockoutKinds.Totp)
│   │           ├── StepUp/
│   │           │   └── PassphraseEndpoints.cs         # CHANGED: /api/auth/unlock gains .AddEndpointFilter<RequireTurnstileFilter>();
│   │           │                                      #          metadata: TurnstileKindMetadata(AuthLockoutKinds.Unlock)
│   │           └── AuthEndpoints.cs                   # CHANGED: /signin gains .AddEndpointFilter<RequireTurnstileFilter>();
│   │                                                  #          metadata: TurnstileKindMetadata("signin")
│   └── ThanyMarcus.Portal.Web/
│       └── src/
│           ├── routes/
│           │   ├── +page.svelte                       # CHANGED: pre-check captcha-state?kind=signin; render widget; gate sign-in button
│           │   └── totp-challenge/
│           │       └── +page.svelte                   # CHANGED: pre-check captcha-state?kind=totp; render widget; thread header
│           └── lib/
│               ├── turnstileClient.ts                 # NEW: fetches captcha-state, loads api.js, renderTurnstile()
│               ├── TurnstileWidget.svelte             # NEW: <TurnstileWidget siteKey onToken={...} />
│               ├── StepUpModal.svelte                 # CHANGED: pre-check captcha-state?kind=unlock; render widget inside modal
│               └── stepUpClient.ts                    # CHANGED: appends cf-turnstile-response header to /api/auth/unlock when token present
└── tests/
    └── ThanyMarcus.Portal.Tests/
        └── Features/
            └── Auth/
                └── Captcha/                           # NEW subfolder
                    ├── TurnstileValidatorTests.cs            # unit: parses siteverify success/failure JSON
                    ├── CaptchaRequirementTrackerTests.cs     # unit: MarkRequired persists for TTL; expires after; Clear removes
                    ├── RequireTurnstileFilterTests.cs        # integration: 428 when required-and-missing; 200 when required-and-valid; bypass when not-required
                    ├── CaptchaEndpointsTests.cs              # integration: /api/auth/captcha-state returns required=true after a 429
                    └── TurnstileE2ETests.cs                  # integration: 5 failed totp → captcha-state=true → 6th totp without token = 428 → with valid token = 401 (wrong code, no captcha gate)
```

No new packages (Cloudflare Turnstile widget loads from CDN). No new DB migrations. No new auth schemes. The `Turnstile` endpoint surface (`/api/auth/captcha-state`) **is** new in OpenAPI; that's the only surface delta.

## `appsettings.json` shape

```json
{
  "Turnstile": {
    "SiteKey":          "",
    "SecretKey":        "",
    "SiteVerifyUrl":    "https://challenges.cloudflare.com/turnstile/v0/siteverify",
    "FailureThreshold": 5,
    "TrackerTtlSeconds": 900,
    "TimeoutSeconds":   5
  }
}
```

`TurnstileOptions`:

```csharp
public sealed record TurnstileOptions
{
    public string SiteKey { get; init; } = "";
    public string SecretKey { get; init; } = "";
    public string SiteVerifyUrl { get; init; } = "https://challenges.cloudflare.com/turnstile/v0/siteverify";
    public int FailureThreshold { get; init; } = 5;
    public int TrackerTtlSeconds { get; init; } = 900;     // 15 min — outlives the 5-min limiter window so one trip == one captcha
    public int TimeoutSeconds { get; init; } = 5;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(SiteKey) && !string.IsNullOrWhiteSpace(SecretKey);
}
```

`IsEnabled` is the kill switch — if either key is empty, the filter and tracker behave as no-ops. This keeps dev-without-Cloudflare-account working: `dotnet run` boots, all auth flows function, no captcha is ever enforced. A developer who explicitly wants to exercise the captcha path uses Cloudflare's published test keys in `appsettings.Development.json`:

```json
{
  "Turnstile": {
    "SiteKey": "1x00000000000000000000AA",
    "SecretKey": "1x0000000000000000000000000000000AA"
  }
}
```

(These are Cloudflare's "always passes" test pair; swap the trailing `AA` for `BB` to get the "always fails" pair. Don't ship real production keys to git — `SecretKey` lives in user-secrets / env-var for any non-trivial environment.)

The 15-minute tracker TTL is intentional: 003c's rate-limiter window is 5 minutes; if we set TTL = 5 min, a user who got rate-limited at second 0 of window N could be free of the captcha requirement before window N+1 begins, meaning the captcha never gets shown. 15 min = 3x the limiter window ensures one throttle → one captcha appearance.

## `ITurnstileValidator.cs` + `TurnstileValidator.cs`

```csharp
public interface ITurnstileValidator
{
    Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct);
}

public sealed record TurnstileVerifyResult(bool Success, IReadOnlyList<string> ErrorCodes)
{
    public static readonly TurnstileVerifyResult Disabled = new(true, Array.Empty<string>());
    public static readonly TurnstileVerifyResult Empty    = new(false, ["missing-input-response"]);
}

public sealed class TurnstileValidator(
    HttpClient http,
    IOptions<TurnstileOptions> options,
    ILogger<TurnstileValidator> logger)
    : ITurnstileValidator
{
    public async Task<TurnstileVerifyResult> VerifyAsync(string token, string? remoteIp, CancellationToken ct)
    {
        var cfg = options.Value;
        if (!cfg.IsEnabled) return TurnstileVerifyResult.Disabled;
        if (string.IsNullOrWhiteSpace(token)) return TurnstileVerifyResult.Empty;

        var form = new Dictionary<string, string>
        {
            ["secret"]   = cfg.SecretKey,
            ["response"] = token,
        };
        if (!string.IsNullOrEmpty(remoteIp)) form["remoteip"] = remoteIp;

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(cfg.TimeoutSeconds));

        try
        {
            using var resp = await http.PostAsync(cfg.SiteVerifyUrl, new FormUrlEncodedContent(form), cts.Token);
            resp.EnsureSuccessStatusCode();
            var payload = await resp.Content.ReadFromJsonAsync<SiteVerifyResponse>(cts.Token)
                ?? new SiteVerifyResponse(false, ["empty-response"]);
            return new TurnstileVerifyResult(payload.Success, payload.ErrorCodes ?? Array.Empty<string>());
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Turnstile siteverify call failed; failing open");
            return new TurnstileVerifyResult(true, ["transport-error"]);   // FAIL OPEN — see "Risks & gotchas"
        }
    }

    private sealed record SiteVerifyResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("error-codes")] IReadOnlyList<string>? ErrorCodes);
}
```

Five judgment calls embedded above:

- **Fail open on HTTP/JSON exceptions.** Returns `Success: true` with `error-codes: ["transport-error"]` so the auth flow proceeds when Cloudflare is unreachable. The alternative — fail closed — means a Cloudflare outage takes down sign-in for everyone, captcha-required or not. For a thesis prototype, availability trumps strict adherence; the `transport-error` token in the response is logged + Prometheus-counted (next bullet) so the operator can see when this fires. ADR-0031 §"Consequences" §"Negative" calls this out as a known risk; this mitigation makes the failure mode degrade gracefully.
- **`SiteVerifyResult.Disabled` is `Success: true`.** When `Turnstile:SiteKey` is empty (dev default), every verify call returns "pass" — combined with `RequireTurnstileFilter.ShouldRequireAsync` also short-circuiting on disabled, the captcha path is fully no-op'd. Two layers of disable check is intentional: even if a misconfigured deployment somehow enables the filter without keys, the validator won't make spurious HTTP calls to Cloudflare with an empty secret.
- **`FormUrlEncodedContent`, not JSON.** Cloudflare's `siteverify` accepts both, but the official docs lead with form-encoded; matches their examples and is one less serializer dependency.
- **`CancellationTokenSource.CreateLinkedTokenSource` with `CancelAfter`.** Honors both the caller's `ct` (request abort) and the configured per-call timeout. Without this, a Cloudflare hang would block the auth filter for the full HTTP default timeout (100s), which is a soft DoS vector.
- **Use `IHttpClientFactory` (typed-client form: `AddHttpClient<ITurnstileValidator, TurnstileValidator>`).** Gives OTel + Prometheus HTTP instrumentation for free, handles socket reuse, and lets the test seam override the `HttpMessageHandler` via `ConfigureHttpMessageHandlerBuilder` if needed. The typed-client form means the `HttpClient` parameter is DI-resolved per-scope.

Add a Prometheus metric near `TurnstileValidator`:

```csharp
// In CaptchaServiceCollectionExtensions or alongside the validator
private static readonly Meter Meter = new("ThanyMarcus.Portal.Auth.Turnstile");
private static readonly Counter<long> SiteVerifyCounter = Meter.CreateCounter<long>(
    "portal_turnstile_siteverify_total",
    description: "Cloudflare Turnstile siteverify calls, by outcome.");
```

Increment with `outcome` tag `pass | fail | error` on each call. The Prometheus exporter (already wired per [[portal_tooling]]) picks up the meter automatically; no additional registration needed.

## `CaptchaRequirementTracker.cs`

```csharp
public sealed class CaptchaRequirementTracker(IClock clock)
{
    private readonly ConcurrentDictionary<string, Instant> _expiries = new();

    public void MarkRequired(string partitionKey, Duration ttl)
    {
        var expires = clock.GetCurrentInstant() + ttl;
        _expiries.AddOrUpdate(partitionKey, expires, (_, existing) => existing > expires ? existing : expires);
    }

    public bool IsRequired(string partitionKey)
    {
        if (!_expiries.TryGetValue(partitionKey, out var expires)) return false;
        if (expires <= clock.GetCurrentInstant())
        {
            _expiries.TryRemove(partitionKey, out _);
            return false;
        }
        return true;
    }

    public void Clear(string partitionKey) => _expiries.TryRemove(partitionKey, out _);

    internal int Count => _expiries.Count;   // test-only
}
```

Four points:

- **Lazy expiry, no background sweep.** `IsRequired` removes the entry on read when expired; entries that are never read just linger until the process restarts. For a thesis prototype with single-instance deployment and TTL = 15 min, the dictionary stays small (worst case: every IP that ever hit the limiter in the last 15 min). A background sweep is overkill; add one only if memory metrics show drift. Compare to 003d's `AuthLockoutSweepService` — that exists because the data persists in Postgres across restarts; the tracker is purely in-memory and process restart is the ultimate sweep.
- **`AddOrUpdate` takes the max expiry.** Two consecutive throttle events extend the captcha requirement, not reset it. Prevents an attacker from gaming the TTL by spacing throttles.
- **No locking.** `ConcurrentDictionary` is thread-safe; the read-then-remove in `IsRequired` is racy (two threads could both see expired, both `TryRemove`, one succeeds — fine; idempotent). The `MarkRequired` add-vs-update race could pick either expiry — fine; both are valid.
- **`IClock` injection.** Uses NodaTime per [[portal_tooling]]. Tests inject `FakeClock` and advance it to validate TTL expiry without `Task.Delay`.

Singleton lifetime — register with `AddSingleton<CaptchaRequirementTracker>()`. Stateful across requests is the whole point.

## `RequireTurnstileFilter.cs`

```csharp
public sealed class RequireTurnstileFilter(
    CaptchaRequirementTracker tracker,
    ITurnstileValidator validator,
    PortalDbContext db,
    IOptions<TurnstileOptions> options) : IEndpointFilter
{
    private const string TokenHeader = "cf-turnstile-response";
    private const string TokenQueryParam = "turnstile";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var cfg = options.Value;
        if (!cfg.IsEnabled) return await next(context);

        var http = context.HttpContext;
        var metadata = http.GetEndpoint()?.Metadata.GetMetadata<TurnstileKindMetadata>();
        if (metadata is null) return await next(context);

        var (partitionKey, kind) = ResolvePartition(http, metadata.Kind);
        var required = await IsCaptchaRequiredAsync(partitionKey, kind, http.RequestAborted);
        if (!required) return await next(context);

        var token = http.Request.Headers[TokenHeader].ToString();
        if (string.IsNullOrEmpty(token))
            token = http.Request.Query[TokenQueryParam].ToString();

        if (string.IsNullOrEmpty(token))
            return CaptchaRequiredResponse(cfg.SiteKey);

        var verify = await validator.VerifyAsync(token, http.Connection.RemoteIpAddress?.ToString(), http.RequestAborted);
        if (!verify.Success)
            return CaptchaRequiredResponse(cfg.SiteKey, verify.ErrorCodes);

        tracker.Clear(partitionKey);   // one captcha clears the requirement
        return await next(context);
    }

    private (string partitionKey, string? failureKind) ResolvePartition(HttpContext http, string kind) => kind switch
    {
        "signin" => ("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), null),
        _        => ("u:" + http.User.FindFirstValue(AuthClaimTypes.SubUs), kind),   // 'totp' or 'unlock'
    };

    private async Task<bool> IsCaptchaRequiredAsync(string partitionKey, string? failureKind, CancellationToken ct)
    {
        if (tracker.IsRequired(partitionKey)) return true;
        if (failureKind is null) return false;   // signin path: tracker is the only signal

        if (!partitionKey.StartsWith("u:", StringComparison.Ordinal)) return false;
        if (!Guid.TryParse(partitionKey.AsSpan(2), out var userId)) return false;

        var count = await db.AuthLockouts
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Kind == failureKind)
            .Select(a => (short?)a.FailedCount)
            .SingleOrDefaultAsync(ct);
        return count is not null && count.Value >= options.Value.FailureThreshold;
    }

    private static IResult CaptchaRequiredResponse(string siteKey, IReadOnlyList<string>? errorCodes = null)
        => Results.Json(
            new
            {
                error        = "captcha_required",
                site_key     = siteKey,
                error_codes  = errorCodes,
            },
            statusCode: StatusCodes.Status428PreconditionRequired);
}

public sealed record TurnstileKindMetadata(string Kind);
```

Six points:

- **Filter order vs. lockout filter (003d).** Both are endpoint filters on `/totp-challenge` and `/api/auth/unlock`. Filters run in registration order. Register the lockout filter **first** (so a hard-locked user gets 423 cheaply without an extra `auth_lockouts` read for the captcha check). The decoration order in the endpoint registration: `.AddEndpointFilter<LockoutGuardFilter>().AddEndpointFilter<RequireTurnstileFilter>()` — both attach their respective metadata via `.WithMetadata(...)` adjacent to the filter line for readability.
- **Filter order vs. rate-limiter (003c).** Same as 003d's note: `.RequireRateLimiting(...)` runs *outside* the endpoint dispatch (in the rate-limiter middleware that wraps the dispatch). A 429 from the limiter beats both 423 and 428 because the dispatch never starts. Correct: a burst attacker sees 429 (cheap), a sustained-locked user sees 423 (one DB read), a captcha-required user sees 428 (tracker check + maybe one DB read).
- **Dual-signal OR semantics.** `IsCaptchaRequiredAsync` returns true if *either* the tracker says required *or* `failed_count >= threshold`. The tracker covers "user just got rate-limited but hasn't accumulated 5 persistent failures yet" (short-term signal); the failed-count covers "user has been failing slowly without ever bursting" (long-term signal). Combined coverage matches ADR-0031 §"Layered behavior" intent.
- **`tracker.Clear(partitionKey)` on successful verify.** A solved captcha clears the requirement for the *partition*, not just this request. Without this, the user would need to solve a captcha on every subsequent request until the tracker TTL expired — UX-hostile. The `failed_count` signal is unaffected; that clears on successful auth via 003d's `AuthLockoutService.ClearAsync`. So: solve captcha → tracker cleared → auth still gated by `failed_count` until next successful login; the next failed login re-arms the tracker via 003c's `OnRejected`.
- **`PortalDbContext` is scoped, filter is scoped.** Same lifetime story as 003d's `LockoutGuardFilter` (registered as scoped). The DB read is `AsNoTracking` + selects a single short — minimal hot-path cost. Index? `auth_lockouts.(user_id, kind)` is the PK, which is the lookup key — covered.
- **`428 Precondition Required` (RFC 6585).** Standard semantics: "the origin server requires the request to be conditional." The "precondition" here is the Turnstile token. Alternative status codes: 403 (too generic, conflates with authz), 401 (conflates with "step-up required" — the SPA's `fetchWithStepUp` already parses 401 bodies for `error: "step_up_required"`, and a new `error: "captcha_required"` value in a 401 body would muddle the dispatch logic). 428 is distinctive and the SPA can handle it cleanly.

## `CaptchaEndpoints.cs`

```csharp
public static class CaptchaEndpoints
{
    public static void MapCaptchaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/auth/captcha-state", async (
            string kind,
            HttpContext http,
            CaptchaRequirementTracker tracker,
            PortalDbContext db,
            IOptions<TurnstileOptions> options,
            CancellationToken ct) =>
        {
            var cfg = options.Value;
            if (!cfg.IsEnabled) return Results.Ok(new CaptchaStateResponse(false, ""));

            var (partitionKey, failureKind, requiresAuth) = kind switch
            {
                "signin" => ("ip:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown"), (string?)null, false),
                "totp"   => ("u:" + http.User.FindFirstValue(AuthClaimTypes.SubUs), AuthLockoutKinds.Totp,   true),
                "unlock" => ("u:" + http.User.FindFirstValue(AuthClaimTypes.SubUs), AuthLockoutKinds.Unlock, true),
                _        => default,
            };
            if (partitionKey is null)
                return Results.BadRequest(new { error = "invalid_kind" });

            if (requiresAuth && http.User.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            if (tracker.IsRequired(partitionKey))
                return Results.Ok(new CaptchaStateResponse(true, cfg.SiteKey));

            if (failureKind is not null
                && Guid.TryParse(partitionKey.AsSpan(2), out var userId))
            {
                var count = await db.AuthLockouts
                    .AsNoTracking()
                    .Where(a => a.UserId == userId && a.Kind == failureKind)
                    .Select(a => (short?)a.FailedCount)
                    .SingleOrDefaultAsync(ct);
                if (count is not null && count.Value >= cfg.FailureThreshold)
                    return Results.Ok(new CaptchaStateResponse(true, cfg.SiteKey));
            }

            return Results.Ok(new CaptchaStateResponse(false, cfg.SiteKey));
        });
    }
}

public sealed record CaptchaStateResponse(bool Required, string SiteKey);
```

Three notes:

- **Conditional auth via `requiresAuth` flag, not `.RequireAuthorization()`.** The endpoint serves both authenticated (`totp`, `unlock`) and unauthenticated (`signin`) callers. Decorating with `.RequireAuthorization()` would reject the `signin` case; not decorating it and conditionally checking `User.Identity.IsAuthenticated` inside the handler is the cleaner shape. (Alternative: two endpoints — `/api/auth/captcha-state/signin` unauth + `/api/auth/captcha-state/{kind}` auth. Single endpoint is fewer routes in OpenAPI.)
- **The `site_key` is returned even when not required.** Lets the SPA cache the value on app load without a second round-trip if the captcha later becomes required. The `Required: false` + non-empty `SiteKey` is a valid response.
- **No rate-limit decoration.** Argued in "Scope boundary" — the endpoint is information-only and a probing attacker learns nothing actionable.

## 003c hooks (additive — keep existing logic)

`AuthRateLimiterPolicies.OnRejectedAsync` gains a tracker write:

```csharp
private static async ValueTask OnRejectedAsync(OnRejectedContext ctx, CancellationToken ct)
{
    var retryAfter = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
        ? (int)Math.Ceiling(retry.TotalSeconds)
        : 0;
    if (retryAfter > 0)
        ctx.HttpContext.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

    // ADD: mark partition as captcha-required (no-op if Turnstile disabled — tracker is always registered)
    var tracker = ctx.HttpContext.RequestServices.GetService<CaptchaRequirementTracker>();
    var turnstile = ctx.HttpContext.RequestServices.GetService<IOptions<TurnstileOptions>>();
    if (tracker is not null && turnstile?.Value.IsEnabled == true)
    {
        var key = PartitionKeyForUser(ctx.HttpContext);
        tracker.MarkRequired(key, Duration.FromSeconds(turnstile.Value.TrackerTtlSeconds));
    }

    ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    await ctx.HttpContext.Response.WriteAsJsonAsync(
        new RateLimitErrorBody("rate_limited", retryAfter), ct);
}
```

`SignInGoogleRateLimitMiddleware.InvokeAsync`'s `!lease.IsAcquired` branch gains the same write with `"ip:" + RemoteIpAddress` as the key:

```csharp
if (!lease.IsAcquired)
{
    var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retry)
        ? (int)Math.Ceiling(retry.TotalSeconds)
        : 0;

    // ADD: mark IP as captcha-required (no-op if Turnstile disabled)
    if (_turnstileOptions.Value.IsEnabled)
    {
        var key = "ip:" + (context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
        _tracker.MarkRequired(key, Duration.FromSeconds(_turnstileOptions.Value.TrackerTtlSeconds));
    }

    if (retryAfter > 0)
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
    await context.Response.WriteAsJsonAsync(
        new RateLimitErrorBody("rate_limited", retryAfter), context.RequestAborted);
    return;
}
```

The middleware's constructor gains two parameters: `CaptchaRequirementTracker _tracker` and `IOptions<TurnstileOptions> _turnstileOptions`. Singleton lifetime is unchanged (the tracker and options are both safely-shared-singleton).

**Two judgment calls:**

- **`GetService` (nullable) inside `OnRejected`, not `GetRequiredService`.** The 003c rate-limiter delegate runs in a static context; if a test or future config omits the captcha service registration, the limiter shouldn't crash. The conditional write is the right defensive shape.
- **No tracker write on the `LockoutGuardFilter`'s 423 short-circuit (003d).** A user who is *already* locked out doesn't need a captcha — they need to wait out the lockout. The captcha layer's job is to gate the *pre-lockout* failure attempts; once locked, the filter sequence is "lockout 423" → request rejected → no captcha state change. The lockout's `ClearAsync` (called on successful auth post-lock-expiry) does **not** clear the tracker either — if you've been locked, you've also accumulated 5+ failures, so `failed_count >= FailureThreshold` will keep the captcha required even after lockout expiry. Solving one captcha drops the tracker entry; the next successful auth clears `failed_count` via 003d.

## Endpoint wiring

`TotpEndpoints.cs` `/totp-challenge`:

```csharp
app.MapPost("/totp-challenge", /* unchanged handler */)
   .RequireAuthorization()
   .RequireRateLimiting(AuthRateLimiterPolicies.TotpChallenge)
   .AddEndpointFilter<LockoutGuardFilter>()
   .WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Totp))
   .AddEndpointFilter<RequireTurnstileFilter>()                          // ADD
   .WithMetadata(new TurnstileKindMetadata(AuthLockoutKinds.Totp));      // ADD
```

`PassphraseEndpoints.cs` `/api/auth/unlock`:

```csharp
app.MapPost("/api/auth/unlock", /* unchanged handler */)
   .RequireAuthorization(AuthPolicies.TotpRequired)
   .RequireRateLimiting(AuthRateLimiterPolicies.Unlock)
   .AddEndpointFilter<LockoutGuardFilter>()
   .WithMetadata(new LockoutKindMetadata(AuthLockoutKinds.Unlock))
   .AddEndpointFilter<RequireTurnstileFilter>()                          // ADD
   .WithMetadata(new TurnstileKindMetadata(AuthLockoutKinds.Unlock));    // ADD
```

`AuthEndpoints.cs` `/api/auth/signin`:

```csharp
grp.MapGet("/signin", () =>
    Results.Challenge(
        new AuthenticationProperties { RedirectUri = "/" },
        [GoogleDefaults.AuthenticationScheme]))
   .AddEndpointFilter<RequireTurnstileFilter>()                          // ADD
   .WithMetadata(new TurnstileKindMetadata("signin"));                   // ADD
```

The `"signin"` kind string is a magic value (not in `AuthLockoutKinds` because there's no corresponding `auth_lockouts` row). Declare a sibling constant:

```csharp
// In Captcha/TurnstileKindMetadata.cs alongside the record
public static class TurnstileKinds
{
    public const string Signin = "signin";
    // Totp and Unlock reuse AuthLockoutKinds constants to keep the values aligned.
}
```

## Frontend wiring (`src/ThanyMarcus.Portal.Web`)

The widget script (`https://challenges.cloudflare.com/turnstile/v0/api.js`) is loaded **explicitly** (`?onload=onTurnstileLoad&render=explicit`) rather than the auto-render mode so we can defer until we know whether the captcha is needed. Auto-render would inject Cloudflare's loader on every page.

`src/lib/turnstileClient.ts` (new):

```ts
let scriptPromise: Promise<void> | null = null;

function loadScript(): Promise<void> {
  if (scriptPromise) return scriptPromise;
  scriptPromise = new Promise((resolve, reject) => {
    const existing = document.querySelector('script[data-turnstile]');
    if (existing) { resolve(); return; }
    const s = document.createElement('script');
    s.src = 'https://challenges.cloudflare.com/turnstile/v0/api.js?render=explicit';
    s.async = true;
    s.defer = true;
    s.setAttribute('data-turnstile', '');
    s.onload = () => resolve();
    s.onerror = () => reject(new Error('turnstile script load failed'));
    document.head.appendChild(s);
  });
  return scriptPromise;
}

export type CaptchaStateKind = 'signin' | 'totp' | 'unlock';

export async function fetchCaptchaState(kind: CaptchaStateKind): Promise<{ required: boolean; siteKey: string }> {
  const r = await fetch(`/api/auth/captcha-state?kind=${kind}`);
  if (!r.ok) return { required: false, siteKey: '' };
  const body = await r.json();
  return { required: !!body.required, siteKey: body.site_key ?? '' };
}

export async function renderTurnstile(container: HTMLElement, siteKey: string): Promise<string> {
  await loadScript();
  return new Promise((resolve, reject) => {
    // @ts-expect-error turnstile global from Cloudflare script
    window.turnstile.render(container, {
      sitekey: siteKey,
      callback: (token: string) => resolve(token),
      'error-callback': () => reject(new Error('turnstile widget error')),
    });
  });
}
```

`src/lib/TurnstileWidget.svelte` (new):

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import { renderTurnstile } from './turnstileClient';

  let { siteKey, onToken } = $props<{ siteKey: string; onToken: (t: string) => void }>();
  let container: HTMLDivElement;

  onMount(async () => {
    try {
      const token = await renderTurnstile(container, siteKey);
      onToken(token);
    } catch {
      onToken('');
    }
  });
</script>

<div bind:this={container} class="cf-turnstile"></div>
```

`src/routes/totp-challenge/+page.svelte` (changed): add a captcha pre-check on mount, conditionally render `<TurnstileWidget />`, thread the token as a header on the existing `POST /totp-challenge`. The existing `challenge(code)` in `totpClient.ts` gains an optional `token` parameter that sets `cf-turnstile-response` when present.

`src/lib/stepUpClient.ts` (changed): `fetchWithStepUp` learns to recognize `428` and (re-)prompt with captcha + passphrase together; the unlock POST appends `cf-turnstile-response` if a token was collected. The cleanest shape: extend `stepUpPrompt` to also carry `siteKey: string | null`; the `StepUpModal` renders a captcha widget when `siteKey` is non-null in addition to the passphrase input.

`src/routes/+page.svelte` (changed): pre-check `?kind=signin` on mount; if required, render the widget and disable the sign-in link until a token comes back; rewrite the link's `href` from `/api/auth/signin` to `/api/auth/signin?turnstile=<token>`.

## Program.cs wiring (additions)

```csharp
// services
builder.Services.AddTurnstile(builder.Configuration);

// endpoint mapping (after MapAuthEndpoints / MapTotpEndpoints / MapPassphraseEndpoints)
app.MapCaptchaEndpoints();
```

`AddTurnstile` extension:

```csharp
public static IServiceCollection AddTurnstile(this IServiceCollection services, IConfiguration cfg)
{
    services.AddOptions<TurnstileOptions>().Bind(cfg.GetSection("Turnstile"));
    services.AddSingleton<CaptchaRequirementTracker>();
    services.AddHttpClient<ITurnstileValidator, TurnstileValidator>();
    services.AddScoped<RequireTurnstileFilter>();
    return services;
}
```

The tracker is singleton, the validator is HttpClient-typed (transient by default, but the typed-client form handles lifetime correctly via `IHttpMessageHandlerFactory`), the filter is scoped (lifetime matches the scoped `PortalDbContext` it consumes).

No pipeline-order changes. The captcha endpoints map alongside the other auth endpoints; the filter slots into the existing endpoint pipeline; the validator is a per-request HTTP call.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings**.
- All existing PORTAL-003 / 003a / 003b / 003c / 003d tests still green; new captcha tests all green.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts cleanly with empty `Turnstile:SiteKey`. Startup logs a warning: "Turnstile is disabled (SiteKey not configured); captcha enforcement skipped." No captcha is ever enforced; all existing flows behave identically to pre-003e.
- With `Turnstile:SiteKey` and `Turnstile:SecretKey` set to Cloudflare test-pass keys, `dotnet run` boots, OpenAPI shows the new `GET /api/auth/captcha-state`, no other surface changes.
- **`TurnstileValidatorTests` (unit):** posts to a stub HTTP handler, parses `{ "success": true, "error-codes": [] }` → `TurnstileVerifyResult(true, [])`; parses `{ "success": false, "error-codes": ["invalid-input-response"] }` → `(false, ["invalid-input-response"])`; on transport exception, returns `(true, ["transport-error"])` (fail-open). Empty `SecretKey` short-circuits to `Disabled` without calling HTTP.
- **`CaptchaRequirementTrackerTests` (unit):** `MarkRequired("k", 5min)` then `IsRequired("k")` = true; advance `FakeClock` 6 min → `IsRequired("k")` = false (and entry removed). Two `MarkRequired` calls with different TTLs → the later expiry wins. `Clear("k")` removes immediately.
- **`RequireTurnstileFilterTests` (integration):** with `Turnstile:FailureThreshold = 2`, no tracker entry, no `auth_lockouts` row → filter admits (calls `next`). Same with `failed_count = 1`. With `failed_count = 2` → filter returns 428 if no token; 200 if header `cf-turnstile-response: <valid>` and the stub validator returns `Success`; 428 if the stub returns `Fail`. On a successful 200 path, `tracker.IsRequired(key)` = false afterwards (cleared).
- **`CaptchaEndpointsTests` (integration):** authenticated GET `/api/auth/captcha-state?kind=totp` with `failed_count = 2`, threshold = 2 → `{ "required": true, "site_key": "1x00..." }`. Same path with `failed_count = 1` → `{ "required": false, "site_key": "1x00..." }`. Unauthenticated GET `?kind=signin` → `{ "required": false, ... }` (no tracker entry); after a 429 from `/signin-google` for that IP → `{ "required": true, ... }`. Invalid `kind` → 400.
- **`TurnstileE2ETests` (integration):** with `Lockout:Totp:MaxFailures = 100` (lockout out of reach), `RateLimiting:TotpChallenge:PermitLimit = 100` (limiter out of reach), `Turnstile:FailureThreshold = 3`: 3 wrong-code `POST /totp-challenge` calls return 401 (lockout records 3 failures). 4th call without a token returns **428** `{ "error": "captcha_required", "site_key": "..." }`. 4th call with `cf-turnstile-response: <valid>` returns 401 (still wrong code, but captcha gate passed). Validate the `tracker.Clear` happened by checking `/api/auth/captcha-state?kind=totp` returns `false` after the successful verify path (which 4xx-but-not-428 is from the lockout filter perspective).
- **Tracker-as-trigger (`TurnstileE2ETests`):** with `Lockout:Totp:MaxFailures = 100`, `RateLimiting:TotpChallenge:PermitLimit = 2`, `Turnstile:FailureThreshold = 100` (so only the tracker can trigger): exhaust the limiter (3rd call → 429); `OnRejected` writes tracker entry. Advance `FakeTimeProvider` past the limiter window; 4th call without token → **428**. With token → 401. Confirms the tracker write in 003c's `OnRejected` is wired.
- **Signin-flow (`TurnstileE2ETests`):** with `RateLimiting:SignInGoogle:PermitLimit = 2`: 2 GETs to `/signin-google?...` succeed; 3rd returns 429 + tracker write for `"ip:<...>"`. GET `/api/auth/signin` without token → 428. With `/api/auth/signin?turnstile=<valid>` → 302 to Google (the existing Challenge result). Confirms the `signin` kind path end-to-end without invoking the real Google handler (use the existing test setup which doesn't reach the Google callback).
- **Disabled-by-default (`TurnstileE2ETests`):** with `Turnstile:SiteKey = ""`, all of the above scenarios behave as if 003e didn't exist — no 428, no captcha state queries return `required: true`, OnRejected doesn't crash trying to write to the tracker.

## Concrete steps in order (each maps to a task)

1. **Land `TurnstileOptions` + `TurnstileKindMetadata` + `TurnstileKinds`.** Trivial. `IsEnabled` defaults to false (empty `SiteKey`).

2. **Land `ITurnstileValidator` + `TurnstileValidator` + unit tests.** Stub the `HttpMessageHandler` to assert form-encoded payload shape and parse outcomes. Verify fail-open on `HttpRequestException` and `OperationCanceledException`.

3. **Land `CaptchaRequirementTracker` + unit tests with `FakeClock`.** Cover the TTL-expiry + max-of-two-marks paths.

4. **Land `RequireTurnstileFilter` + `CaptchaServiceCollectionExtensions.AddTurnstile`.** No standalone test — exercised by step 6.

5. **Land `CaptchaEndpoints` + `MapCaptchaEndpoints` registration.** Single endpoint, two-mode handler.

6. **Add `RequireTurnstileFilterTests` + `CaptchaEndpointsTests`.** Use `WithTestAuth` for the totp/unlock kinds; for `signin` use the unauthenticated path. Use a `StubTurnstileValidator` registered via `ConfigureTestServices`.

7. **Modify `AuthRateLimiterPolicies.OnRejectedAsync` + `SignInGoogleRateLimitMiddleware`.** Defensive `GetService` for the OnRejected delegate; constructor-inject the tracker + options into the middleware. Verify existing 003c tests still green (the tracker write is a no-op when the captcha service is registered but `IsEnabled = false`).

8. **Decorate `/totp-challenge`, `/api/auth/unlock`, `/api/auth/signin` with `.AddEndpointFilter<RequireTurnstileFilter>().WithMetadata(...)`.** Three single-line endpoint changes.

9. **Add `TurnstileE2ETests`.** This is the load-bearing test class — proves the end-to-end flow with all three signal sources (tracker via OnRejected, tracker via SignInGoogleMiddleware, persistent `failed_count`).

10. **Frontend (`src/lib/turnstileClient.ts` + `TurnstileWidget.svelte`).** Vanilla, no tests (the Portal.Web has no E2E test scaffold yet per [[portal_web_stack]]).

11. **Update `totp-challenge/+page.svelte` + `StepUpModal.svelte` + `+page.svelte` + `stepUpClient.ts` + `totpClient.ts`.** Pre-check captcha-state on mount; conditionally render the widget; thread the token. Test manually via `dotnet run` + `pnpm dev` with Cloudflare test-pass keys.

12. **Wire `services.AddTurnstile(...)` + `app.MapCaptchaEndpoints()` in `Program.cs`.** Two lines.

13. **Manual smoke:** with `Turnstile:SiteKey` set to Cloudflare's "always passes" test key (`1x00000000000000000000AA`), browser flow: hit `/signin-google` 21x via curl from a single IP (triggers 429 + tracker write); reload the SPA; sign-in button is disabled with widget rendered; solve the always-pass widget → button enables → click → flow continues to Google. Repeat for `/totp-challenge`: fail 5 times → reload → widget appears → solve → enter wrong code 1 more time → 401 (not 428 — captcha cleared). Verify Prometheus metric `portal_turnstile_siteverify_total{outcome="pass"}` increments.

14. **Verify all acceptance criteria.** Commit only after.

## Risks & gotchas

- **Fail-open on Cloudflare outage is a *deliberate* availability tradeoff.** ADR-0031 §"Consequences" §"Negative" calls out that Turnstile becomes a critical-path dependency. The validator's `catch (HttpRequestException ...)` returns `Success: true` so a Cloudflare 5xx or network partition doesn't break sign-in. **The risk:** during an outage, brute-force protection drops to "rate limit only" — 003c still caps bursts, 003d still locks at 20 failures. Without fail-open, the alternative is a sign-in outage that's longer than the actual Cloudflare incident. The Prometheus counter `portal_turnstile_siteverify_total{outcome="error"}` makes this visible. If a future requirement says "fail closed on Cloudflare outage" — flip the catch block to `return new TurnstileVerifyResult(false, [...])` and add an alert on the error counter; but that's a policy decision that needs explicit sign-off, not a default.

- **The `OnRejected` delegate runs in a static context.** `AuthRateLimiterPolicies.OnRejectedAsync` is `static`. To resolve `CaptchaRequirementTracker` it goes through `ctx.HttpContext.RequestServices`. Using `GetService<>` (nullable) instead of `GetRequiredService<>` is intentional — see the §"Two judgment calls" note above. The downside is a silent miss if the tracker isn't registered; mitigation is the `RateLimitConfigurationTests` smoke that boots the full host (services include the tracker by default).

- **Token-in-query-string for `/api/auth/signin` is logged in access logs.** The `?turnstile=<TOKEN>` shows up in any HTTP request log. Turnstile tokens are single-use and short-lived (~5 min validity per Cloudflare docs); leakage is low-impact (an attacker recovering a used token can't replay it), but if Caddy / OTel access logs are archived to a long-term sink, the tokens go with them. Mitigation: strip the `turnstile` query param from access logs (Caddy `format` directive can redact; out of scope for 003e but flag for the deployment ticket).

- **The widget can be solved twice for the same partition.** A user who pre-solves the captcha on `/api/auth/signin?turnstile=<T1>` (which clears the tracker for their IP) and *then* fails sign-in 5x within the limiter window will get the tracker re-armed by 003c's `OnRejected` — they then need to solve a new captcha. UX is correct (the requirement re-arms on continued attack), but a confused user might think the first captcha "didn't work." The 428 response body's `error_codes` field surfaces the validator's outcome (e.g., `["timeout-or-duplicate"]` if they tried to reuse the same token) — the SPA can show a tailored message.

- **`AddOrUpdate` race on `MarkRequired` is benign but worth knowing.** Two concurrent `OnRejected` calls for the same partition could both compute different `expires` instants; `AddOrUpdate`'s update factory picks the max, so the later TTL always wins. No data loss.

- **Filter registration order matters: lockout before captcha.** Acceptance criteria pin this. Reverse order is wrong: a hard-locked user (423) shouldn't be asked to solve a captcha first (UX waste; 428 implies "if you do this, the request proceeds" which is a lie when locked).

- **`HttpContext.RequestServices` in OnRejected — scope availability.** The rate-limiter middleware invokes `OnRejected` *inside* the request pipeline, so the request's service scope is alive. `GetService<PortalDbContext>` would also work here; we don't need it because the captcha tracker is singleton. Don't ask the OnRejected delegate to make DB calls — keep it CPU-cheap.

- **HttpClient timeout vs. CancellationToken.** The 5-second `CancelAfter` is a wall-clock timeout; the caller's `ct` (which is `HttpContext.RequestAborted`) cancels on client disconnect. Both feed the linked `cts`. If Cloudflare hangs for >5s and the user is still connected, we cancel and fail-open (the catch handles `OperationCanceledException`). Acceptable; an attacker who knows about the timeout could try to time their requests against Cloudflare latency, but the persistent layer (003d) still kicks in.

- **Cloudflare test keys are public.** `1x00000000000000000000AA` (always-pass) is documented in Cloudflare's testing docs; committing them to `appsettings.Development.json` is *expected and safe*. Production keys go to env-vars or .NET user-secrets — `dotnet user-secrets set "Turnstile:SecretKey" "0x..."` is the dev workflow; `docker compose` env section is the prod workflow.

- **The `cf-turnstile-response` header on cross-origin requests.** Same-origin SPA per [[portal_web_stack]], so no CORS preflight on the totp / unlock POSTs. If a future ticket introduces cross-origin (e.g., a CLI hitting `/totp-challenge` from outside the browser), the preflight would need to allow the `cf-turnstile-response` header. Out of scope; revisit when the cross-origin requirement lands.

- **Tracker leak on process restart is good.** Captcha requirements reset on `dotnet run` restart. Combined with the 003d persistent `failed_count` signal, a restart doesn't *remove* the captcha gate — it just shifts the sole signal to the persistent count until a fresh limiter rejection re-arms the tracker. Correct semantics; no surprise.

- **Forwarded-headers / `RemoteIpAddress` (inherited gotcha from 003c).** The `signin` partition key is `"ip:" + RemoteIpAddress`. Behind Caddy, this is the proxy's loopback unless `UseForwardedHeaders` is configured. PORTAL-003c's brief flagged this as a pre-condition; verify it's in `Program.cs` before relying on per-IP captcha partitioning (otherwise everyone shares one captcha state and one user's failed sign-in triggers a captcha for everyone). If missing, add forwarded-headers wiring as part of 003e (small follow-up; the deployment-side Caddy config sets `X-Forwarded-For` per ADR-0027).

- **OpenAPI surface change.** `GET /api/auth/captcha-state` is new. The 003c / 003d briefs both said "no OpenAPI changes"; 003e is the first auth-slice ticket that adds a surface. Consumers of the hand-written TS contracts ([[portal_tooling]]) need to add the `CaptchaStateResponse` type — small update to the contract file.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + green `dotnet test` + a manual `curl` + browser smoke producing a 428 response with `error: "captcha_required"` after 5 failed `/totp-challenge` attempts, then a 401 (not 428) after attaching a valid `cf-turnstile-response` header. The filter ordering (lockout before captcha, both inside the endpoint pipeline; rate-limiter wraps the dispatch) is documented in source ordering only; no narrative comments per [[feedback_no_code_comments]].

The dev experience is preserved: cloning the repo and running `dotnet run` works without any Cloudflare account — empty `SiteKey` disables the entire layer with a startup warning. A developer who wants to exercise the captcha path sets test keys in user-secrets and gets the full flow.

A fresh agent can pick up PORTAL-006 (recovery codes + recovery-redeem captcha) from cold by reading:

1. ADR-0031 §"Persistent lockout layer" + §"In-memory layer" (row 3: `auth-recovery-redeem` 3/1h) — PORTAL-006 lands the endpoint + rate-limit policy + lockout kind + captcha gating together.
2. PORTAL-003c's `AuthRateLimiterPolicies` — add `RecoveryRedeem` policy constant + `AddPolicy` block.
3. PORTAL-003d's `AuthLockoutKinds` — add `Recovery` constant; `RecordFailureAsync` / `ClearAsync` already generic over kind.
4. PORTAL-003e's `RequireTurnstileFilter` — already generic over kind via `TurnstileKindMetadata`. PORTAL-006 just decorates the new endpoint with `.AddEndpointFilter<RequireTurnstileFilter>().WithMetadata(new TurnstileKindMetadata(AuthLockoutKinds.Recovery))`.
5. PORTAL-003e's `CaptchaEndpoints.cs` — extend the `kind` switch with a `"recovery"` arm; SPA's `turnstileClient.fetchCaptchaState` takes a string param, no change needed.
6. Memory `portal_tooling.md` for the warnings-as-errors expectation.

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-003 monolithic auth — split into 003 / 003a–e per ADR-0031 §"Ticket split". 003e is the final ticket in the split.
- Adjacent tickets PORTAL-003e interacts with / unblocks:
  - PORTAL-003c — rate limiting (already landed; 003e hooks the `OnRejected` callback and the `SignInGoogleRateLimitMiddleware` rejection branch additively — does not modify response shape or pipeline order)
  - PORTAL-003d — persistent lockout (already landed; 003e reads `auth_lockouts.failed_count` for the dual-signal check — no schema change, no service-surface change)
  - PORTAL-006 — recovery codes (lands `/api/auth/recovery-codes/redeem` endpoint + the `'recovery'` lockout kind + the `auth-recovery-redeem` limiter policy + the captcha gating; follows the kind-metadata + endpoint-filter pattern 003d / 003e establish)
  - PORTAL-009 / PORTAL-010 — SSE endpoints (orthogonal; no captcha on long-lived connections; if a future ticket wants captcha on SSE handshake, it slots in the same filter pattern)
- Config files: `appsettings.json` (`Turnstile:SiteKey` empty default), `appsettings.Development.json` (optional test-pass keys for local exploration), user-secrets / env (`Turnstile:SecretKey` for any non-dev environment).
- Frontend: `src/ThanyMarcus.Portal.Web/src/lib/turnstileClient.ts` + `TurnstileWidget.svelte` (new); `src/lib/stepUpClient.ts` + `StepUpModal.svelte` + `routes/totp-challenge/+page.svelte` + `routes/+page.svelte` + `lib/totpClient.ts` (changed). No npm package added; Turnstile widget loads from Cloudflare's CDN at runtime.
- External docs: Cloudflare Turnstile widget reference (`https://developers.cloudflare.com/turnstile/`), siteverify endpoint (`https://challenges.cloudflare.com/turnstile/v0/siteverify`), testing keys (`https://developers.cloudflare.com/turnstile/troubleshooting/testing/`).
