# PORTAL-003 Google SSO + cookie + OnValidatePrincipal — Handoff Brief

**Goal:** land the auth foundation defined by [[0030-auth-flow]] — Google OAuth sign-in, a 14-day sliding cookie, `OnCreatingTicket` user upsert + `totp` claim seeding, and the `OnValidatePrincipal` hook that enforces `sessions_invalidated_at` and refreshes stale claims. **No TOTP code verification, no step-up, no rate limiting, no Turnstile, no persistent lockout writes** — those are PORTAL-003a through 003e per the ticket split in [[0031-rate-limiting-and-lockout]] §"Ticket split".

Estimated **0.75 person-day** with heavy AI-agent assistance (longer than original 0.5d because the auth grilling added `OnValidatePrincipal` + claims-refresh-from-DB + the `totp` claim state machine, which the original "Google SSO integration" ticket did not anticipate).

## Where decisions live (read before doing anything)

- **`docs/decisions/0030-auth-flow.md`** — the authoritative design. The "ASP.NET Core wiring (sketch)" section maps almost 1:1 to what this ticket lands; the "Cookie claim shape" table and the `totp` state machine are the contract for `OnCreatingTicket`; the "Sign-in sequence" diagram is the integration test specification.
- **`docs/decisions/0031-rate-limiting-and-lockout.md`** — defines the ticket split. PORTAL-003 is *only* the bullet "Google SSO + cookie auth + `sessions_invalidated_at` + `OnValidatePrincipal`"; rate limiting and persistent lockout are 003c/003d.
- **`docs/decisions/0023-test-stack.md`** — `WebApplicationFactory<Program>` is the integration-test host; auth-cookie tests bake the cookie via a `TestAuthHandler` override on the cookie scheme rather than going through the real Google OAuth flow.
- **`docs/decisions/0028-schema-conventions.md`** — `IClock` for current-time reads (no `DateTimeOffset.UtcNow`); `Instant` everywhere.
- **`docs/decisions/0029-type-mappings.md`** — `LastSeenAt` and `SessionsInvalidatedAt` are `Instant`; do not introduce `DateTime` anywhere.
- **`plans/portal-002-handoff.md`** — the `User` entity is already shaped for this ticket (`GoogleSubject` unique, `Email` unique, `ProfilePictureUrl`, `SessionsInvalidatedAt`, `LastSeenAt`). Do not re-migrate; just consume.
- **Memory files**: `portal_architecture.md` (Minimal APIs + VSA — auth endpoints go under `Features/Auth/`), `portal_tooling.md` (warnings-as-errors), `portal_deployment.md` (single-VM — single-process in-memory state is acceptable).

**Do not re-litigate the auth flow during implementation.** The 5-question auth grilling closed every fork. If a decision seems unclear, ADR-0030 is the answer; if you find yourself wanting to add a `trusted_devices` table or an `ITicketStore`, stop — both are explicitly deferred under "Consequences → Negative" / "Neutral".

## Scope boundary (precise)

**In scope:**
- `Microsoft.AspNetCore.Authentication.Google` registration + Google ClientId/ClientSecret from config.
- `Microsoft.AspNetCore.Authentication.Cookies` registration with 14d sliding, HttpOnly, Secure, SameSite=Lax.
- `OnCreatingTicket` on the Google scheme: upsert `users` row by `google_subject`, refresh `email` / `name` / `profile_picture_url`, bump `last_seen_at`, set the `sub_us` and `totp` claims on the outgoing identity.
- `OnValidatePrincipal` on the cookie scheme: reject cookies whose `IssuedUtc < users.sessions_invalidated_at`; refresh `email` / `name` / `profile_picture_url` / `totp` claims from DB on every request.
- `TotpRequired` authorization policy (`totp ∈ {verified, not-enabled}`).
- A `Features/Auth/` slice: `/api/auth/me`, `POST /api/auth/signout`, `GET /api/auth/signin` (returns 302 to Google), `GET /totp-challenge` (placeholder page that renders the "enter TOTP code" stub — the POST handler is PORTAL-003a's).
- SvelteKit landing-page wiring that calls `/api/auth/me` and renders sign-in / signed-in state.
- Integration tests proving the seven acceptance criteria below.

**Out of scope (do not touch — each has its own ticket):**
- `POST /totp-challenge` body handling — PORTAL-003a (consumes a TOTP code or backup code via `Otp.NET`).
- TOTP enable / disable flow + QR generation + `totp_backup_codes` writes — PORTAL-003a.
- `/api/auth/unlock` + `IInfraOpUnlockCache` + Argon2id passphrase verification — PORTAL-003b.
- `Microsoft.AspNetCore.RateLimiting` registration + per-endpoint policies — PORTAL-003c.
- `auth_lockouts` reads/writes + sweep `IHostedService` — PORTAL-003d.
- Cloudflare Turnstile widget + `ITurnstileValidator` — PORTAL-003e.
- Recovery-code redeem endpoint — PORTAL-006.
- `EncryptedProviderToken` reads — PORTAL-005.

If a follow-up ticket needs a hook (e.g., 003a needs to call `RefreshSignInAsync` after TOTP verification), PORTAL-003 lands the cookie scheme and the `totp` claim shape that 003a hooks onto — nothing more.

## Output of PORTAL-003 — final directory state

```
Thany-Marcus/
├── Directory.Packages.props                           # adds Microsoft.AspNetCore.Authentication.Google
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── ThanyMarcus.Portal.Api.csproj              # adds the Google package reference
│   │   ├── Program.cs                                 # adds AddAuthentication / AddCookie / AddGoogle + AddAuthorization + UseAuthentication + UseAuthorization
│   │   ├── appsettings.json                           # adds "Google": { "ClientId": "", "ClientSecret": "" } stubs
│   │   ├── appsettings.Development.json               # local dev Google credentials (gitignored variant)
│   │   └── Features/
│   │       └── Auth/                                  # existing entity files unchanged
│   │           ├── AuthClaimTypes.cs                  # NEW: const string SubUs = "sub_us"; SubGoogle = "sub_g"; Totp = "totp";
│   │           ├── AuthPolicies.cs                    # NEW: const string TotpRequired = "TotpRequired";
│   │           ├── TotpClaimValues.cs                 # NEW: const NotEnabled / NotVerified / Verified
│   │           ├── GoogleSignInHandler.cs             # NEW: service invoked from OnCreatingTicket — upsert + claims
│   │           ├── CookiePrincipalValidator.cs        # NEW: service invoked from OnValidatePrincipal — sessions_invalidated_at + claim refresh
│   │           ├── AuthEndpoints.cs                   # NEW: MapAuthEndpoints — /api/auth/me, /api/auth/signin, /api/auth/signout, /totp-challenge GET
│   │           └── MeResponse.cs                      # NEW: record returned by /api/auth/me
│   └── ThanyMarcus.Portal.Web/
│       └── src/routes/
│           ├── +layout.ts                             # CHANGED: fetch /api/auth/me on load; expose `me` to pages
│           └── +page.svelte                           # CHANGED: render signed-in user (name + picture) or "Sign in with Google" link to /api/auth/signin
└── tests/
    └── ThanyMarcus.Portal.Tests/
        ├── ThanyMarcus.Portal.Tests.csproj            # unchanged
        ├── Features/
        │   └── Auth/
        │       ├── GoogleSignInHandlerTests.cs        # NEW: unit-level — upsert creates / refreshes; bumps last_seen_at; emits right totp claim
        │       ├── CookiePrincipalValidatorTests.cs   # NEW: unit-level — rejects when IssuedUtc < sessions_invalidated_at; refreshes claims; rejects when user missing
        │       ├── AuthEndpointsTests.cs              # NEW: integration — /api/auth/me 401 unauth; 200 with claims body when cookie is baked; /api/auth/signout clears cookie
        │       └── TotpRequiredPolicyTests.cs         # NEW: integration — verified + not-enabled pass; not-verified is 403 (or redirect — see Wiring §"TotpRequired challenge")
        └── Infrastructure/
            ├── TestAuthHandler.cs                     # NEW: minimal AuthenticationHandler<TestAuthOptions> that succeeds with whatever claims the test put in scope
            └── AuthenticatedClientExtensions.cs       # NEW: WebApplicationFactory ext to override the cookie scheme with TestAuthHandler in tests
```

The `GoogleSignInHandler` and `CookiePrincipalValidator` services exist for one reason: they hold the logic that `OnCreatingTicket` / `OnValidatePrincipal` lambdas would otherwise inline, and they are independently unit-testable with a real `PortalDbContext` (via the existing `DbIntegrationTestBase`). The `OnCreatingTicket` / `OnValidatePrincipal` lambdas in `Program.cs` resolve these from the request's service scope and delegate — that's all.

## Packages to add (`Directory.Packages.props`)

```xml
<PackageVersion Include="Microsoft.AspNetCore.Authentication.Google" Version="10.*" />
```

`ThanyMarcus.Portal.Api.csproj` adds (no `Version` attr per central package management):

```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.Google" />
```

Cookie authentication is in `Microsoft.AspNetCore.App` (shared framework) — no separate package needed.

## Program.cs wiring (additions only)

Add **before** `var app = builder.Build();`, after the DbContext registration block:

```csharp
// --- Auth services (PORTAL-003) ---
builder.Services.AddScoped<GoogleSignInHandler>();
builder.Services.AddScoped<CookiePrincipalValidator>();

builder.Services.AddAuthentication(opts =>
{
    opts.DefaultScheme          = CookieAuthenticationDefaults.AuthenticationScheme;
    opts.DefaultChallengeScheme = GoogleDefaults.AuthenticationScheme;
})
.AddCookie(opts =>
{
    opts.Cookie.Name         = ".Portal.Auth";
    opts.ExpireTimeSpan      = TimeSpan.FromDays(14);
    opts.SlidingExpiration   = true;
    opts.Cookie.HttpOnly     = true;
    opts.Cookie.SameSite     = SameSiteMode.Lax;
    opts.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    opts.LoginPath           = "/api/auth/signin";
    opts.LogoutPath          = "/api/auth/signout";
    opts.AccessDeniedPath    = "/totp-challenge";
    opts.Events.OnValidatePrincipal = async ctx =>
    {
        var validator = ctx.HttpContext.RequestServices.GetRequiredService<CookiePrincipalValidator>();
        await validator.ValidateAsync(ctx);
    };
})
.AddGoogle(opts =>
{
    opts.ClientId     = builder.Configuration["Google:ClientId"]
        ?? throw new InvalidOperationException("Google:ClientId not configured");
    opts.ClientSecret = builder.Configuration["Google:ClientSecret"]
        ?? throw new InvalidOperationException("Google:ClientSecret not configured");
    opts.Scope.Add("email");
    opts.Scope.Add("profile");
    opts.SaveTokens = false;
    opts.Events.OnCreatingTicket = async ctx =>
    {
        var handler = ctx.HttpContext.RequestServices.GetRequiredService<GoogleSignInHandler>();
        await handler.OnCreatingTicketAsync(ctx);
    };
});

builder.Services.AddAuthorization(opts =>
{
    opts.AddPolicy(AuthPolicies.TotpRequired, p => p.RequireAssertion(c =>
        c.User.FindFirstValue(AuthClaimTypes.Totp)
            is TotpClaimValues.Verified or TotpClaimValues.NotEnabled));
});
```

Add **after** `var app = builder.Build();` and **after** the existing `app.UseStaticFiles();`, **before** `app.MapFallbackToFile`:

```csharp
app.UseAuthentication();
app.UseAuthorization();

app.MapAuthEndpoints();   // extension method in Features/Auth/AuthEndpoints.cs
```

`appsettings.json` adds:

```json
{
  "Google": {
    "ClientId": "",
    "ClientSecret": ""
  }
}
```

Empty strings — real credentials come from `appsettings.Development.json` (gitignored in dev) and from env vars / Azure KeyVault in prod (PORTAL-017 territory).

`appsettings.Development.json` adds the developer's Google OAuth client credentials. **Do not commit real credentials.** Add `appsettings.Development.json` to `.gitignore` if not already there, and document the setup in the README (Google Cloud Console → OAuth client → authorized redirect URI `http://localhost:5000/signin-google` for dev, `https://<your-portal-host>/signin-google` for prod).

### TotpRequired challenge (cookie scheme behavior)

When an authenticated user with `totp = not-verified` hits a protected endpoint, ASP.NET Core's authorization middleware returns 403 by default (the principal *is* authenticated, just unauthorized). The cookie scheme's `AccessDeniedPath` redirects browser navigations (Accept: text/html) to `/totp-challenge`. SPA fetch calls (Accept: application/json) get the 403 verbatim and the SPA handles it.

`/totp-challenge` GET in PORTAL-003 is a stub that returns a tiny HTML page with the text "TOTP challenge endpoint — POST coming in PORTAL-003a". The real form rendering happens when SvelteKit's `+page.svelte` for that route lands in PORTAL-003a. The point of landing the GET stub now is so that the cookie scheme's `AccessDeniedPath` doesn't 404.

## GoogleSignInHandler (`Features/Auth/GoogleSignInHandler.cs`)

```csharp
public sealed class GoogleSignInHandler(PortalDbContext db, IClock clock)
{
    public async Task OnCreatingTicketAsync(OAuthCreatingTicketContext ctx)
    {
        var googleSub = ctx.Principal?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Google ticket missing sub claim");
        var email     = ctx.Principal?.FindFirstValue(ClaimTypes.Email)
            ?? throw new InvalidOperationException("Google ticket missing email claim");
        var name      = ctx.Principal?.FindFirstValue(ClaimTypes.Name) ?? email;
        var picture   = ctx.User.RootElement.TryGetProperty("picture", out var p) ? p.GetString() : null;

        var now = clock.GetCurrentInstant();
        var user = await db.Users.SingleOrDefaultAsync(u => u.GoogleSubject == googleSub);
        if (user is null)
        {
            user = new User
            {
                GoogleSubject     = googleSub,
                Email             = email,
                Name              = name,
                ProfilePictureUrl = picture,
                LastSeenAt        = now,
                CreatedAt         = now,
                UpdatedAt         = now,
            };
            db.Users.Add(user);
        }
        else
        {
            user.Email             = email;
            user.Name              = name;
            user.ProfilePictureUrl = picture;
            user.LastSeenAt        = now;
        }
        await db.SaveChangesAsync();

        var totp = await ResolveTotpClaimAsync(user.Id);

        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        identity.AddClaim(new Claim(AuthClaimTypes.SubUs, user.Id.ToString()));
        identity.AddClaim(new Claim(AuthClaimTypes.SubGoogle, googleSub));
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, totp));
    }

    private async Task<string> ResolveTotpClaimAsync(Guid userId)
    {
        // TOTP "enabled" = totp_secrets row exists with enabled_at NOT NULL AND disabled_at IS NULL.
        var enabled = await db.TotpSecrets
            .AnyAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null);
        return enabled ? TotpClaimValues.NotVerified : TotpClaimValues.NotEnabled;
    }
}
```

`ctx.User` here is the parsed JSON object from Google's `/userinfo` endpoint (`JsonElement`), not the ClaimsPrincipal — that's the OAuth-handler quirk. The `picture` claim is extracted from there, not from `ctx.Principal`.

## CookiePrincipalValidator (`Features/Auth/CookiePrincipalValidator.cs`)

```csharp
public sealed class CookiePrincipalValidator(PortalDbContext db)
{
    public async Task ValidateAsync(CookieValidatePrincipalContext ctx)
    {
        var subUs = ctx.Principal?.FindFirstValue(AuthClaimTypes.SubUs);
        if (subUs is null || !Guid.TryParse(subUs, out var userId))
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var user = await db.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId);
        if (user is null)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // sessions_invalidated_at gate: a bump after the cookie was issued = reject.
        if (user.SessionsInvalidatedAt is { } invalidatedAt
            && ctx.Properties.IssuedUtc is { } issuedUtc
            && Instant.FromDateTimeOffset(issuedUtc) < invalidatedAt)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Refresh stale claims from DB on every request — cookie carries identity, DB carries truth.
        var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
        ReplaceClaim(identity, ClaimTypes.Email, user.Email);
        ReplaceClaim(identity, ClaimTypes.Name, user.Name);
        if (user.ProfilePictureUrl is { } pic)
            ReplaceClaim(identity, "picture", pic);

        var totp = await ResolveTotpClaimAsync(userId, identity.FindFirst(AuthClaimTypes.Totp)?.Value);
        ReplaceClaim(identity, AuthClaimTypes.Totp, totp);

        ctx.ShouldRenew = true;
    }

    private async Task<string> ResolveTotpClaimAsync(Guid userId, string? current)
    {
        var enabled = await db.TotpSecrets
            .AnyAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null);
        if (!enabled) return TotpClaimValues.NotEnabled;
        // If user has already verified this session, keep it; otherwise demand verification.
        return current == TotpClaimValues.Verified ? TotpClaimValues.Verified : TotpClaimValues.NotVerified;
    }

    private static void ReplaceClaim(ClaimsIdentity identity, string type, string value)
    {
        foreach (var existing in identity.FindAll(type).ToList()) identity.RemoveClaim(existing);
        identity.AddClaim(new Claim(type, value));
    }
}
```

Two subtle invariants in `ResolveTotpClaimAsync`:
- If the user **disabled** TOTP mid-session, `enabled` is false → claim becomes `not-enabled` → user passes `TotpRequired` immediately (correct: they no longer have 2FA, so there's nothing to verify).
- If the user **enabled** TOTP mid-session and is currently `verified`, the claim stays `verified` (PORTAL-003a's enable-and-verify flow calls `RefreshSignInAsync` with the right claim — we don't want to clobber that here on the next request).

## AuthEndpoints (`Features/Auth/AuthEndpoints.cs`)

```csharp
public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth");

        grp.MapGet("/me", (ClaimsPrincipal user) =>
        {
            if (user.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            return Results.Ok(new MeResponse(
                UserId:            Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!),
                Email:             user.FindFirstValue(ClaimTypes.Email)!,
                Name:              user.FindFirstValue(ClaimTypes.Name)!,
                ProfilePictureUrl: user.FindFirstValue("picture"),
                Totp:              user.FindFirstValue(AuthClaimTypes.Totp)!));
        });

        grp.MapPost("/signout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // GET /api/auth/signin → 302 to Google. The cookie scheme would do this on demand for any
        // unauthenticated request to a protected resource, but the SPA needs an explicit entry point.
        app.MapGet("/api/auth/signin", (HttpContext http) =>
            Results.Challenge(
                new AuthenticationProperties { RedirectUri = "/" },
                [GoogleDefaults.AuthenticationScheme]));

        // GET /totp-challenge → placeholder; POST is PORTAL-003a's territory.
        app.MapGet("/totp-challenge", () => Results.Content(
            """
            <!DOCTYPE html>
            <html><body><h1>TOTP challenge</h1>
            <p>POST handler ships in PORTAL-003a.</p>
            </body></html>
            """,
            "text/html"));
    }
}

public sealed record MeResponse(
    Guid     UserId,
    string   Email,
    string   Name,
    string?  ProfilePictureUrl,
    string   Totp);
```

## SvelteKit changes

`src/ThanyMarcus.Portal.Web/src/routes/+layout.ts` (load `me` once per navigation):

```ts
export const ssr = false;

export const load = async ({ fetch }) => {
  const r = await fetch('/api/auth/me');
  return { me: r.ok ? await r.json() : null };
};
```

`src/ThanyMarcus.Portal.Web/src/routes/+page.svelte` (render either sign-in CTA or signed-in state):

```svelte
<script lang="ts">
  export let data;
</script>

{#if data.me}
  <p>Signed in as <strong>{data.me.name}</strong> ({data.me.email})</p>
  {#if data.me.profilePictureUrl}
    <img src={data.me.profilePictureUrl} alt="" width="64" height="64" />
  {/if}
  <p>TOTP state: {data.me.totp}</p>
  <form method="post" action="/api/auth/signout">
    <button type="submit">Sign out</button>
  </form>
{:else}
  <a href="/api/auth/signin">Sign in with Google</a>
{/if}
```

The SPA does not call Google directly. It hits `/api/auth/signin`, the portal responds with a 302 to Google, the browser follows, Google redirects back to `/signin-google?code=…`, the portal completes the exchange, sets the `.Portal.Auth` cookie, and 302s back to `/`.

## Test infrastructure (`Infrastructure/TestAuthHandler.cs` + `AuthenticatedClientExtensions.cs`)

For tests that need to exercise a signed-in cookie, we override the cookie scheme with a test handler in a derived `WebApplicationFactory`. This is the canonical ASP.NET Core integration-test pattern.

```csharp
public sealed class TestAuthOptions : AuthenticationSchemeOptions
{
    public required Claim[] Claims { get; init; }
    public Instant? IssuedAt { get; init; }
}

public sealed class TestAuthHandler(
    IOptionsMonitor<TestAuthOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<TestAuthOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity  = new ClaimsIdentity(Options.Claims, "Test");
        var principal = new ClaimsPrincipal(identity);
        var props     = new AuthenticationProperties();
        if (Options.IssuedAt is { } issued)
            props.IssuedUtc = issued.ToDateTimeOffset();
        var ticket = new AuthenticationTicket(principal, props, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public static class AuthenticatedClientExtensions
{
    public static WebApplicationFactory<Program> WithTestAuth(
        this WebApplicationFactory<Program> factory,
        Guid userId,
        string email     = "alice@example.com",
        string name      = "Alice",
        string? picture  = null,
        string totp      = TotpClaimValues.NotEnabled,
        Instant? issuedAt = null)
        => factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Configure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme          = "Test";
                o.DefaultAuthenticateScheme = "Test";
                o.DefaultChallengeScheme    = "Test";
            });
            s.PostConfigure<TestAuthOptions>("Test", o =>
            {
                Claim[] claims =
                [
                    new(AuthClaimTypes.SubUs, userId.ToString()),
                    new(ClaimTypes.Email, email),
                    new(ClaimTypes.Name,  name),
                    new(AuthClaimTypes.Totp, totp),
                    .. (picture is null ? Array.Empty<Claim>() : new[] { new Claim("picture", picture) }),
                ];
                o.Claims   = claims;
                o.IssuedAt = issuedAt;
            });
            s.AddAuthentication("Test").AddScheme<TestAuthOptions, TestAuthHandler>("Test", _ => { });
        }));
}
```

**Why this shape, not a real Google ticket round-trip:** mocking Google's OAuth2 server end-to-end is high-effort and tests Google's correctness more than our cookie/claims plumbing. The `OnCreatingTicket` logic is unit-tested directly in `GoogleSignInHandlerTests` against a real `PortalDbContext`; the `OnValidatePrincipal` logic is similarly unit-tested in `CookiePrincipalValidatorTests`; integration tests that need an authenticated client bake the claims with `TestAuthHandler` and prove the endpoints respect them. Tests that *specifically* want to prove `OnValidatePrincipal` runs end-to-end through the real cookie scheme can issue a real cookie via a sign-in helper endpoint — but that's overkill for this ticket.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings** (warnings-as-errors per [[portal_tooling]]).
- `dotnet run --project tests/ThanyMarcus.Portal.Tests` exits 0; existing PORTAL-002 tests still green; new auth tests all green.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts on `:5000` when `Google:ClientId` and `Google:ClientSecret` are configured (in `appsettings.Development.json` or env vars). Without them, startup fails fast with a clear `InvalidOperationException`.
- `curl -i http://localhost:5000/api/auth/me` → **401 Unauthorized** when no cookie is present.
- `curl -i http://localhost:5000/api/auth/signin` → **302** with `Location:` pointing at `https://accounts.google.com/o/oauth2/v2/auth?...` (or whatever Google's current authorize endpoint is).
- A manual browser sign-in (real Google client configured in dev) lands at `/` with a `.Portal.Auth` cookie set (`HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`, `Expires` ≈ 14 days out), and `/api/auth/me` returns the `MeResponse` JSON with the right user id, email, and `totp: "not-enabled"`. A second sign-in with the same Google account does **not** create a duplicate `users` row.
- A manual browser POST to `/api/auth/signout` clears the cookie (`Set-Cookie: .Portal.Auth=; expires=Thu, 01 Jan 1970 …`) and the next `/api/auth/me` is 401.
- `GoogleSignInHandlerTests`: covers (a) new-user-creates-row, (b) existing-user-by-google-sub-refreshes-email/name/picture/last_seen_at and does *not* duplicate, (c) `totp` claim is `not-enabled` when no `totp_secrets` row, (d) `totp` claim is `not-verified` when a `totp_secrets` row exists with `enabled_at != null AND disabled_at == null`, (e) `totp` claim is `not-enabled` when the only `totp_secrets` row has `disabled_at != null`.
- `CookiePrincipalValidatorTests`: covers (a) missing `sub_us` claim → reject, (b) user not in DB → reject, (c) cookie `IssuedUtc < sessions_invalidated_at` → reject, (d) cookie `IssuedUtc >= sessions_invalidated_at` → pass + claims refreshed from DB, (e) email/name/picture changes in DB propagate to the principal on next validate, (f) TOTP disabled mid-session → claim becomes `not-enabled`, (g) `verified` claim is preserved across requests once present.
- `AuthEndpointsTests`: `/api/auth/me` 401 without a baked cookie; 200 with the expected JSON body when `WithTestAuth(...)` bakes claims; `/api/auth/signout` returns 204 and signs out (next `/api/auth/me` is 401 — verifiable only with the real cookie scheme, so test against `WithTestAuth(...)` for the 204 plus a separate test that posts to signout against the real cookie scheme via an in-test sign-in helper, or accept that the test covers the endpoint contract only).
- `TotpRequiredPolicyTests`: a stub endpoint protected by `[Authorize(Policy = AuthPolicies.TotpRequired)]` returns 200 when the baked principal has `totp=verified`, 200 when `totp=not-enabled`, and 403 when `totp=not-verified`. (The browser redirect to `/totp-challenge` is a cookie-scheme behavior that fires only when the cookie scheme is the default — that's covered by the AccessDeniedPath setting, not asserted by this unit-level test.)
- `pnpm build` in `src/ThanyMarcus.Portal.Web/` still succeeds. The new `+layout.ts` + `+page.svelte` render in dev (`pnpm dev` + `dotnet watch` two-terminal flow): unauthenticated landing shows "Sign in with Google" link.

## Concrete steps in order (each maps to a task)

1. **Add the Google auth package** — update `Directory.Packages.props` + the Portal.Api csproj. Run `dotnet restore`. Verify zero warnings.

2. **Land the claim-type and policy constants** — `AuthClaimTypes.cs`, `AuthPolicies.cs`, `TotpClaimValues.cs`. These are tiny `public static class` files holding `const` strings; landing them first lets the rest of the code reference them without copy-paste.

3. **Wire `Program.cs`** — add the AddAuthentication / AddCookie / AddGoogle / AddAuthorization block. Add `UseAuthentication` + `UseAuthorization`. Skip `MapAuthEndpoints` for now (the endpoints file doesn't exist yet). Configure `Google:ClientId` / `Google:ClientSecret` placeholders in `appsettings.json`. The API should still start (with the InvalidOperationException if Google config is missing). Run the existing PORTAL-002 tests — still green.

4. **Land `GoogleSignInHandler` + its tests** — write `GoogleSignInHandlerTests` first (failing), then implement the handler. Each test inserts the relevant `User` / `TotpSecret` rows via `DbIntegrationTestBase.Db`, constructs an `OAuthCreatingTicketContext` manually (it's awkward — see Risks below for the workaround), invokes `OnCreatingTicketAsync`, and asserts on the resulting `ClaimsIdentity` and on the DB rows.

5. **Land `CookiePrincipalValidator` + its tests** — same TDD loop. Construct a `CookieValidatePrincipalContext` (also awkward — see Risks), call `ValidateAsync`, assert on `ctx.Principal` and on `ctx.IsRejected` / signed-out state.

6. **Land `AuthEndpoints.cs` + `MeResponse.cs`** — wire `MapAuthEndpoints` in `Program.cs`. Run the API, hit `/api/auth/me` with `curl` (should be 401), hit `/api/auth/signin` with `curl -i` (should be 302 to Google).

7. **Land `TestAuthHandler` + `AuthenticatedClientExtensions`** — these are pure test infrastructure; no failing test to drive them yet, but write a smoke test that proves `factory.WithTestAuth(userId)` produces a client whose `/api/auth/me` returns the expected JSON. This validates the test harness before you build on top of it.

8. **Land `AuthEndpointsTests`** — covers the contract of `/api/auth/me` and `/api/auth/signout`.

9. **Land `TotpRequiredPolicyTests`** — register a tiny test-only endpoint in `Program.cs` *or* add it dynamically in the factory (preferred — keeps prod code clean). Assert the three claim-value × policy outcomes.

10. **SvelteKit changes** — `+layout.ts` and `+page.svelte`. Run `pnpm build`. Run `pnpm dev` + `dotnet watch` and click through manually.

11. **Manual end-to-end sign-in** — register a Google OAuth client in Google Cloud Console with redirect URI `http://localhost:5000/signin-google`. Put the credentials in `appsettings.Development.json` (gitignored). Run the app. Click "Sign in with Google". Verify the seven manual acceptance-criteria bullets (cookie attributes, `/api/auth/me` body, signout clears cookie, second sign-in does not duplicate the user row — query `psql portal_dev -c "SELECT count(*) FROM users WHERE google_subject = '<your sub>'"`).

12. **Verify all acceptance criteria** — run every check above. Commit only after all pass.

## Risks & gotchas

- **Constructing `OAuthCreatingTicketContext` in unit tests.** This class is part of `Microsoft.AspNetCore.Authentication.OAuth` and has a non-trivial constructor: `(ClaimsPrincipal principal, AuthenticationProperties properties, HttpContext context, AuthenticationScheme scheme, OAuthOptions options, HttpClient backchannel, OAuthTokenResponse tokens, JsonElement user)`. The simplest workable approach is to refactor `GoogleSignInHandler` so the public method takes the four things it actually needs — a `JsonElement` representing Google's userinfo payload, a `ClaimsIdentity` to mutate, and the existing DB / clock — and the lambda in `Program.cs` is a 3-line adapter: extract the four bits from the real `OAuthCreatingTicketContext` and call the handler. This keeps the handler trivially unit-testable and the production lambda thin. Apply the same shape to `CookiePrincipalValidator` (take an `AuthenticationProperties`, a `ClaimsPrincipal`, and a `RejectCallback` rather than the full `CookieValidatePrincipalContext`). The exact split is a judgment call — choose the seam that makes the test-construction code shortest.

- **`AccessDeniedPath` redirect vs. SPA fetch.** The cookie scheme's `AccessDeniedPath` redirect kicks in for any 403 response from the cookie handler. For a SPA that calls `/api/...` via `fetch`, the browser silently follows the redirect, and `fetch` resolves with the HTML body of `/totp-challenge` — not what the SPA wants. The standard ASP.NET Core fix is to set `opts.Events.OnRedirectToAccessDenied = ctx => { if (ctx.Request.Path.StartsWithSegments("/api")) { ctx.Response.StatusCode = 403; return Task.CompletedTask; } return base.OnRedirectToAccessDenied(ctx); }` — return 403 for API paths, redirect for everything else. Apply the same to `OnRedirectToLogin` (401 for `/api`, 302 to Google for browser nav). Land both overrides as part of the `AddCookie(...)` block.

- **`SecurePolicy = Always` breaks local dev over plain HTTP.** ASP.NET Core's dev launch profile uses HTTPS by default on `:5001` and HTTP on `:5000`. With `SecurePolicy.Always`, the cookie won't set on `http://localhost:5000`. Two ways to handle: (a) use the HTTPS launch URL in dev, (b) override `SecurePolicy` in the `Development` environment to `SameAsRequest`. (a) is closer to production and avoids a divergent code path; choose (a) unless there's a reason not to. Document the chosen URL in the README.

- **Google `picture` claim location.** Google's OAuth2 handler exposes the parsed `/userinfo` JSON as `OAuthCreatingTicketContext.User` (a `JsonElement`), not as a claim on `ctx.Principal`. The `picture` URL lives in `ctx.User.GetProperty("picture").GetString()`. ASP.NET Core's Google handler maps some fields (sub, email, name) into `ctx.Principal` claims automatically, but not `picture`. The `GoogleSignInHandler` sketch above handles this correctly — don't try to read `picture` off the principal.

- **`ClaimsIdentity.Name` vs. `ClaimTypes.Name`.** Google's handler maps `name` to `ClaimTypes.Name` (`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name`), but `ClaimsPrincipal.Identity.Name` reads from whatever `NameClaimType` the identity was constructed with — which is `ClaimTypes.Name` by default, but the `TestAuthHandler` constructs identities without a `NameClaimType` override, so `Identity.Name` will be null unless you pass it explicitly. Just use `FindFirstValue(ClaimTypes.Name)` and `FindFirstValue(ClaimTypes.Email)` consistently — don't lean on `Identity.Name`.

- **`Instant.FromDateTimeOffset` vs. `Instant.FromUtc`.** `AuthenticationProperties.IssuedUtc` is `DateTimeOffset?`. Converting to NodaTime: `Instant.FromDateTimeOffset(issuedUtc)` — note `Offset`, not `Instant.FromDateTimeUtc` (which only accepts a `DateTime` with `Kind == Utc`). Easy to mix up.

- **`SignOutAsync` on the cookie scheme during `OnValidatePrincipal`.** Calling `await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme)` after `RejectPrincipal()` is correct — it emits the `Set-Cookie: .Portal.Auth=` header that clears the browser cookie. Without it, the principal is rejected for *this* request but the browser keeps sending the now-invalid cookie until expiry. Verify this in `CookiePrincipalValidatorTests` by checking response headers.

- **`AsNoTracking()` on the validator's user read.** The validator reads the user on every request — using a tracked entity here would slow change-tracking on the per-request DbContext for no benefit. The validator never writes; the `last_seen_at` bump happens in `OnCreatingTicket` (sign-in time), not on every request. If you want a per-request `last_seen_at` bump as a follow-up, that's a separate ticket — out of scope here.

- **Cookie `Path = /` and SvelteKit's static fallback.** SvelteKit's `adapter-static` produces a single SPA shell. The portal serves the shell from `/` via `MapFallbackToFile("index.html")`. The cookie's default `Path = /` means it's sent on every request including static assets — fine, since the static-file middleware doesn't care about the cookie. No action needed; flagging in case a future change moves static assets under a subpath.

- **`MapFallbackToFile("index.html")` and `/totp-challenge`.** The fallback fires only for unmatched routes, and `app.MapGet("/totp-challenge", ...)` matches first. Order is correct in the sketched `Program.cs` (`MapAuthEndpoints` before `MapFallbackToFile`). Don't reorder.

- **Tests using `DateTimeOffset.UtcNow` for `IssuedAt`.** All time references in tests must come from `FakeClock`, not the wall clock. The `WithTestAuth(..., issuedAt:)` parameter takes an `Instant?`; pass `Clock.GetCurrentInstant() - Duration.FromMinutes(5)` to simulate "cookie issued 5 minutes ago", not `DateTimeOffset.UtcNow.AddMinutes(-5)`.

- **The default Google OAuth scope.** The handler already includes `openid` implicitly when `email` and `profile` are added — don't add it explicitly (some older docs do; it's redundant in 10.x). Do not add `offline_access`: ADR-0030 §5 is explicit that we don't store refresh tokens.

- **`SignInScheme` on AddGoogle.** The cookie scheme is the default — `AddGoogle(...)` infers `SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme` from the default. If a future change overrides `DefaultScheme`, this implicit wiring breaks silently (Google's callback would try to issue a Google-scheme cookie, which doesn't exist). If in doubt, set `opts.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;` explicitly inside `AddGoogle`.

- **Warnings from analyzer rules on `null!` claim values.** The Google ticket should always have `sub`; the `throw` is a safety net for malformed Google responses (extremely rare). Analyzers may complain about `??` on a value the type system says can't be null — suppress at the call site with a comment if needed, but don't loosen the throw.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + a green `dotnet run --project tests/...` + a manual end-to-end sign-in through Google with a real OAuth client. Tasks marked completed.

A fresh agent can pick PORTAL-003a (TOTP enable/disable + backup codes) up from cold by reading:

1. This handoff + ADR-0030 §"Cookie claim shape" / §"State transitions for `totp` claim".
2. ADR-0031 §"Ticket split" for the boundary between 003 and 003a.
3. The existing `Features/Auth/` source — `GoogleSignInHandler.ResolveTotpClaimAsync` and `CookiePrincipalValidator.ResolveTotpClaimAsync` are the two places that read `totp_secrets`; 003a is where they get *written* to.
4. The TOTP-flow sketch from the auth grilling (in the grilling transcript or ADR-0030 §"Sign-in sequence" step 10).

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-003 — 0.5 d estimate covering "Google SSO + session cookie". Revised: 0.75 d once `OnValidatePrincipal` + claims-refresh + the `totp` claim state machine landed in ADR-0030.
- Adjacent tickets that PORTAL-003 unblocks (or unblocks once their sibling 003x tickets land):
  - PORTAL-003a — TOTP enable/disable + backup codes (consumes the `totp` claim shape; lands the POST `/totp-challenge` handler and TOTP enable/disable endpoints; calls `RefreshSignInAsync` with the right claim transition)
  - PORTAL-003b — Step-up auth + `IInfraOpUnlockCache` (independent of the cookie, but invalidates cache on `sessions_invalidated_at` bump landed here)
  - PORTAL-003c — In-memory rate limiting (decorates the auth endpoints landed here)
  - PORTAL-003d — Persistent lockout writes (depends on the auth endpoints landed here; reads/writes `auth_lockouts`)
  - PORTAL-003e — Cloudflare Turnstile (gates the same endpoints landed here, post-failure)
  - PORTAL-011 — Provisioning wizard (the first feature behind `[Authorize(Policy = AuthPolicies.TotpRequired)]`)
  - PORTAL-015 — Destroy flow (the first feature behind step-up; depends on 003b, which depends on this)
