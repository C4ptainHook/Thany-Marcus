# PORTAL-003a TOTP enable/disable + backup codes — Handoff Brief

**Goal:** complete the TOTP half of the auth foundation. Land (a) the `POST /totp-challenge` handler that verifies a TOTP code **or** a one-time backup code and bumps the `totp` claim from `not-verified` → `verified` via `RefreshSignInAsync`; (b) the enable flow (`init` → return secret + QR data URI; `verify` → persist encrypted `TotpSecret` row with `enabled_at`, generate 8 single-use backup codes, refresh sign-in); (c) the disable flow (verify code, set `disabled_at`, purge unused backup codes). **No rate limiting, no Turnstile, no `auth_lockouts` writes, no step-up unlock** — those remain PORTAL-003b through 003e per [[0031-rate-limiting-and-lockout]] §"Ticket split".

Estimated **0.75 person-day** — TOTP arithmetic via `Otp.NET` is trivial, but the enable-and-verify round trip + backup-code generation + `RefreshSignInAsync` semantics + the SvelteKit account-settings page are where the time goes.

## Where decisions live (read before doing anything)

- **`docs/decisions/0030-auth-flow.md`** — the `totp` claim state machine (table under "Cookie claim shape") is the contract for this ticket. Specifically: enable-and-verify transitions `not-enabled` → `verified` (skipping `not-verified`); disable transitions `verified` → `not-enabled`; fresh sign-in with TOTP enabled lands `not-verified` (set by `GoogleSignInHandler`, already shipped); `/totp-challenge` POST is the only transition `not-verified` → `verified`.
- **`docs/decisions/0029-type-mappings.md`** — the three-column AES-GCM layout (`Ciphertext` / `Nonce` / `Tag`) on `TotpSecret` is already shaped by PORTAL-002. The encryption key is **not** the per-user passphrase DEK — TOTP must verify on every sign-in, before any passphrase prompt. Use `IDataProtector` (Microsoft.AspNetCore.DataProtection, in the shared framework) with a purpose string of `"totp-secrets.v1"`; the data-protection key ring is server-side and survives restarts when `PersistKeysToFileSystem(...)` is configured (PORTAL-017 territory — for now the default in-memory ring is acceptable, but call out in the README that rotating it invalidates all stored TOTP secrets).
- **`docs/decisions/0031-rate-limiting-and-lockout.md`** — the ticket split is authoritative. PORTAL-003a is **only** the bullet "TOTP enable/disable flow + backup codes" — no rate limits on `/totp-challenge` (003c), no `auth_lockouts` writes on failure (003d), no Turnstile (003e). Failures here return 400/401; the *escalation* is added by 003c/d.
- **`docs/decisions/0023-test-stack.md`** — `WithTestAuth(...)` already exists from PORTAL-003 and bakes the cookie with arbitrary claims, including `totp=not-verified` for testing the challenge path. `RefreshSignInAsync` only re-emits the cookie when the *real* cookie scheme is the default; tests that need to assert the post-verification claim either (a) bake the post-state claim and assert on `/api/auth/me`, or (b) use the real cookie scheme via a sign-in helper. Prefer (a) for unit-level coverage and (b) for one end-to-end test.
- **`plans/portal-003-handoff.md`** — the cookie scheme + `OnValidatePrincipal` + `GoogleSignInHandler.ResolveTotpClaimAsync` are already in place. `CookiePrincipalValidator.ResolveTotpClaimAsync` already preserves a `verified` claim across requests once present — so once `RefreshSignInAsync` is called with `totp=verified`, subsequent requests keep it (until a sign-out, sessions bump, or TOTP disable). Don't re-implement that logic; consume it.
- **Memory files**: `portal_architecture.md` (Minimal APIs + VSA — these endpoints go under `Features/Auth/`), `portal_tooling.md` (warnings-as-errors), `feedback_no_code_comments.md` (no narrative comments in source).

**Do not re-litigate the TOTP flow.** ADR-0030 §"State transitions for `totp` claim" + §"Sign-in sequence" step 10 closed the design. If a question seems open (e.g., "should enable-and-verify go through `not-verified` first?"), ADR-0030 says no — atomic transition `not-enabled` → `verified` because the verify step *is* the proof.

## Scope boundary (precise)

**In scope:**
- `Otp.NET` package + `Totp` (RFC 6238, 30 s window, SHA-1, 6 digits — the defaults match every authenticator app; do not override).
- `QRCoder` package for the QR code data URI returned by enable-init (PNG bytes → base64 → `data:image/png;base64,…`).
- `Microsoft.AspNetCore.DataProtection` (shared framework — no package add) for `IDataProtector` with purpose `"totp-secrets.v1"`.
- `Konscious.Security.Cryptography.Argon2` for hashing backup codes (PORTAL-005 lands the same package for passphrase-side use — both depend on it; central package management handles dedup).
- `Features/Auth/Totp/` slice: `TotpService` (generate secret, verify code), `TotpBackupCodeService` (generate 8 codes, hash, redeem), `TotpEndpoints` (registers the 4 endpoints), `TotpEnableInitResponse` / `TotpEnableVerifyRequest` / `TotpChallengeRequest` records.
- `POST /totp-challenge` (consumes the existing `GET /totp-challenge` stub's slot — keep the GET, add the POST).
- `POST /api/auth/totp/enable/init` — returns the new secret (base32) + a QR data URI. Stores nothing yet.
- `POST /api/auth/totp/enable/verify` — body `{ secret, code }`. Verifies, encrypts secret with `IDataProtector`, upserts `totp_secrets` row with `enabled_at = now`, generates + hashes 8 backup codes, writes `totp_backup_codes` rows, calls `RefreshSignInAsync` with `totp=verified`. Returns `{ backupCodes: [...] }` (plaintext, **shown once**).
- `POST /api/auth/totp/disable` — body `{ code }`. Verifies (TOTP code only, no backup code — disabling via backup code would be a privilege escalation). Sets `disabled_at = now` on the row; deletes unused backup codes (`UsedAt IS NULL`); calls `RefreshSignInAsync` with `totp=not-enabled`.
- SvelteKit account-settings page (`/settings/security`): shows TOTP state from `me.totp`; "Enable TOTP" button posts to `/init`, renders QR, prompts for code, posts to `/verify`, shows backup codes once; "Disable TOTP" prompt + code field; "TOTP challenge" page (`/totp-challenge` GET turns into a real Svelte route) renders a code input and posts to `/totp-challenge`.
- Integration tests proving the eleven acceptance criteria below.

**Out of scope (do not touch — each has its own ticket):**
- `Microsoft.AspNetCore.RateLimiting` on `/totp-challenge` and friends — PORTAL-003c.
- `auth_lockouts` row writes on failed `/totp-challenge` attempts — PORTAL-003d.
- Cloudflare Turnstile widget rendering + token validation — PORTAL-003e.
- Recovery-code redeem endpoint (`/api/auth/recovery-codes/redeem`) — PORTAL-006. **Recovery codes ≠ TOTP backup codes.** Backup codes restore TOTP-verified state; recovery codes restore passphrase access. ADR-0030 §"Step-up sequence" + PORTAL-006 own recovery codes; this ticket owns backup codes only.
- Step-up unlock cache (`IInfraOpUnlockCache`) — PORTAL-003b.
- Persisting the data-protection key ring to disk / KeyVault — PORTAL-017.

If a follow-up needs to extend this (e.g., 003d needs to wire `auth_lockouts.kind = 'totp'` into the failure path), the seam is the `TotpChallengeResult` enum returned by `TotpService.VerifyChallengeAsync` — 003d hooks `Failed` to write the lockout row. PORTAL-003a lands the enum and the failure return; nothing more.

## Output of PORTAL-003a — final directory state

```
Thany-Marcus/
├── Directory.Packages.props                           # adds Otp.NET, QRCoder, Konscious.Security.Cryptography.Argon2
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── ThanyMarcus.Portal.Api.csproj              # adds the three new package references
│   │   ├── Program.cs                                 # registers TotpService, TotpBackupCodeService, MapTotpEndpoints, AddDataProtection
│   │   └── Features/
│   │       └── Auth/
│   │           ├── Totp/                              # NEW subfolder
│   │           │   ├── TotpService.cs                 # generate secret, verify TOTP code, encrypt/decrypt via IDataProtector
│   │           │   ├── TotpBackupCodeService.cs       # generate 8 codes, hash with Argon2id, redeem
│   │           │   ├── TotpEndpoints.cs               # MapTotpEndpoints — /totp-challenge POST + /api/auth/totp/{init,verify,disable}
│   │           │   ├── TotpChallengeRequest.cs        # record { Code: string }   — TOTP or backup code
│   │           │   ├── TotpEnableInitResponse.cs      # record { Secret: string (base32), QrPngDataUri: string }
│   │           │   ├── TotpEnableVerifyRequest.cs     # record { Secret: string, Code: string }
│   │           │   ├── TotpEnableVerifyResponse.cs    # record { BackupCodes: IReadOnlyList<string> }
│   │           │   ├── TotpDisableRequest.cs          # record { Code: string }
│   │           │   └── TotpChallengeResult.cs         # enum { Verified, Failed }  (003d hook point)
│   │           └── AuthEndpoints.cs                   # CHANGED: remove the inline `/totp-challenge` GET stub; the GET now lives in SvelteKit (SPA fallback)
│   └── ThanyMarcus.Portal.Web/
│       ├── src/routes/
│       │   ├── totp-challenge/+page.svelte            # NEW: code input, POST /totp-challenge, redirect to /
│       │   └── settings/security/+page.svelte         # NEW: enable / disable UI; shows backup codes once on enable
│       └── src/lib/totpClient.ts                      # NEW (small): fetch wrappers for the 4 endpoints
└── tests/
    └── ThanyMarcus.Portal.Tests/
        └── Features/
            └── Auth/
                └── Totp/                              # NEW subfolder
                    ├── TotpServiceTests.cs           # unit: generate → verify round-trip; reject stale / future codes; encrypt → decrypt round-trip
                    ├── TotpBackupCodeServiceTests.cs # unit: 8 codes, all unique, Argon2id verify, single-use semantics
                    ├── TotpEndpointsTests.cs         # integration: enable init → verify → /api/auth/me shows totp=verified; disable; challenge with TOTP code; challenge with backup code; backup code can't be reused
                    └── TotpChallengeFailureTests.cs  # integration: wrong code → 401; wrong code + already verified → still 200/no-op (idempotent); challenge endpoint requires authenticated cookie
```

The `/totp-challenge` **GET** stub from PORTAL-003 (the inline HTML in `AuthEndpoints.cs`) goes away — the route is now served by SvelteKit's `MapFallbackToFile("index.html")`, which means the cookie scheme's `AccessDeniedPath = "/totp-challenge"` still works (the SPA shell renders, hydrates, and Svelte's router resolves to `totp-challenge/+page.svelte`). The **POST** is a real API endpoint registered by `MapTotpEndpoints`.

## Packages to add (`Directory.Packages.props`)

```xml
<PackageVersion Include="Otp.NET" Version="1.4.*" />
<PackageVersion Include="QRCoder" Version="1.6.*" />
<PackageVersion Include="Konscious.Security.Cryptography.Argon2" Version="1.3.*" />
```

`ThanyMarcus.Portal.Api.csproj` adds:

```xml
<PackageReference Include="Otp.NET" />
<PackageReference Include="QRCoder" />
<PackageReference Include="Konscious.Security.Cryptography.Argon2" />
```

Data Protection is part of `Microsoft.AspNetCore.App`; no package add.

## Program.cs wiring (additions only)

Add **before** `var app = builder.Build();`, after the existing auth-services block:

```csharp
builder.Services.AddDataProtection()
    .SetApplicationName("ThanyMarcus.Portal");

builder.Services.AddScoped<TotpService>();
builder.Services.AddScoped<TotpBackupCodeService>();
```

`SetApplicationName` ensures consistent key derivation across the cookie scheme and the TOTP secret protector — both default to the assembly name otherwise, which is fine, but pinning it future-proofs against an assembly rename.

Add **after** the existing `app.MapAuthEndpoints();`:

```csharp
app.MapTotpEndpoints();
```

## TotpService (`Features/Auth/Totp/TotpService.cs`)

```csharp
public sealed class TotpService(IDataProtectionProvider dp)
{
    private readonly IDataProtector _protector = dp.CreateProtector("totp-secrets.v1");

    public string GenerateSecret()
    {
        var bytes = new byte[20];
        RandomNumberGenerator.Fill(bytes);
        return Base32Encoding.ToString(bytes);
    }

    public string BuildQrPngDataUri(string secret, string email, string issuer = "Thany-Marcus")
    {
        var uri = $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(email)}"
                + $"?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits=6&period=30";
        using var qr = new QRCodeGenerator();
        using var data = qr.CreateQrCode(uri, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(6);
        return "data:image/png;base64," + Convert.ToBase64String(png);
    }

    public bool Verify(string secret, string code)
    {
        var bytes = Base32Encoding.ToBytes(secret);
        var totp  = new Totp(bytes);
        return totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay);
    }

    public (byte[] Ciphertext, byte[] Nonce, byte[] Tag) Encrypt(string secret)
    {
        var protectedBytes = _protector.Protect(Encoding.UTF8.GetBytes(secret));
        // IDataProtector returns a single opaque blob; we stuff it into Ciphertext and leave Nonce/Tag empty-but-non-null
        // (the column shape is shared with passphrase-DEK-encrypted blobs; here the envelope is data-protection, not AES-GCM directly).
        return (protectedBytes, Array.Empty<byte>(), Array.Empty<byte>());
    }

    public string Decrypt(byte[] ciphertext) =>
        Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));

    public async Task<TotpChallengeResult> VerifyChallengeAsync(
        PortalDbContext db, Guid userId, string code, CancellationToken ct)
    {
        var row = await db.TotpSecrets
            .SingleOrDefaultAsync(t => t.UserId == userId && t.EnabledAt != null && t.DisabledAt == null, ct);
        if (row is null) return TotpChallengeResult.Failed;
        var secret = Decrypt(row.Ciphertext);
        return Verify(secret, code) ? TotpChallengeResult.Verified : TotpChallengeResult.Failed;
    }
}
```

Two judgment calls embedded above:

- **`VerificationWindow.RfcSpecifiedNetworkDelay`** — `Otp.NET`'s default tolerance is ±1 step (90 s window centered on now). Tighter (±0 steps) breaks for users with clock skew; wider (±2 steps) widens the brute-force surface. RFC 6238 §5.2 explicitly allows ±1; that's the default.
- **`Ciphertext` carries the data-protection blob; `Nonce` and `Tag` are empty.** This is the one place where the three-column shape from ADR-0029 is *not* an AES-GCM tuple. The alternative — implement AES-GCM directly with a key derived from a configured master secret — is more code, more crypto surface, and harder to rotate. `IDataProtector` is the canonical ASP.NET Core answer for server-side opaque secrets. The column shape stays the same so the migration is unchanged; the consumer (`TotpService`) is the only place that knows the envelope. Document this in a one-line `//` on the column write (the rare "non-obvious why" case where a comment earns its keep — per the "no comments" rule, this is the exception about a hidden invariant).

## TotpBackupCodeService (`Features/Auth/Totp/TotpBackupCodeService.cs`)

```csharp
public sealed class TotpBackupCodeService(PortalDbContext db, IClock clock)
{
    private const int CodeCount = 8;
    // Crockford base32, omitting ambiguous chars (I, L, O, U). 8 chars = ~40 bits per code.
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public async Task<IReadOnlyList<string>> IssueAsync(Guid userId, CancellationToken ct)
    {
        // Purge any existing rows for this user — IssueAsync is called on enable and re-issue both.
        var existing = await db.TotpBackupCodes.Where(b => b.UserId == userId).ToListAsync(ct);
        db.TotpBackupCodes.RemoveRange(existing);

        var now = clock.GetCurrentInstant();
        var plaintexts = new List<string>(CodeCount);
        var rows = new List<TotpBackupCode>(CodeCount);
        for (var i = 0; i < CodeCount; i++)
        {
            var code = GenerateCode();
            plaintexts.Add(code);
            rows.Add(new TotpBackupCode
            {
                UserId     = userId,
                HashedCode = HashCode(code),
                CreatedAt  = now,
            });
        }
        db.TotpBackupCodes.AddRange(rows);
        await db.SaveChangesAsync(ct);
        return plaintexts;
    }

    public async Task<bool> RedeemAsync(Guid userId, string code, CancellationToken ct)
    {
        var rows = await db.TotpBackupCodes
            .Where(b => b.UserId == userId && b.UsedAt == null)
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (Argon2idVerify(row.HashedCode, code))
            {
                row.UsedAt = clock.GetCurrentInstant();
                await db.SaveChangesAsync(ct);
                return true;
            }
        }
        return false;
    }

    public async Task PurgeUnusedAsync(Guid userId, CancellationToken ct)
    {
        await db.TotpBackupCodes
            .Where(b => b.UserId == userId && b.UsedAt == null)
            .ExecuteDeleteAsync(ct);
    }

    private static string GenerateCode()
    {
        Span<byte> buf = stackalloc byte[8];
        Span<char> chars = stackalloc char[8];
        RandomNumberGenerator.Fill(buf);
        for (var i = 0; i < 8; i++)
            chars[i] = Alphabet[buf[i] & 0x1F];
        return new string(chars);
    }

    private static string HashCode(string code)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(code))
        {
            DegreeOfParallelism = 2,
            MemorySize          = 19456,   // 19 MiB — OWASP 2024 recommendation low end
            Iterations          = 2,
            Salt                = RandomNumberGenerator.GetBytes(16),
        };
        var hash = argon.GetBytes(32);
        return $"$argon2id$v=19$m=19456,t=2,p=2${Convert.ToBase64String(argon.Salt)}${Convert.ToBase64String(hash)}";
    }

    private static bool Argon2idVerify(string phc, string code)
    {
        // Simple PHC parser sufficient for the format we emit above.
        var parts = phc.Split('$');
        if (parts.Length != 6 || parts[1] != "argon2id") return false;
        var salt = Convert.FromBase64String(parts[4]);
        var expected = Convert.FromBase64String(parts[5]);
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(code))
        {
            DegreeOfParallelism = 2,
            MemorySize          = 19456,
            Iterations          = 2,
            Salt                = salt,
        };
        var actual = argon.GetBytes(expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }
}
```

The PHC format here is a *subset* of the spec (no version field, fixed params) — adequate for our own-emit-own-verify use. **PORTAL-005 / PORTAL-006** will land a full Argon2id PHC parser for passphrase / recovery-code verification; if the API surface there generalizes nicely, refactor this to share it. Don't pre-extract a shared helper now — both ticket authors should look at all three call sites and choose the seam.

## TotpEndpoints (`Features/Auth/Totp/TotpEndpoints.cs`)

```csharp
public static class TotpEndpoints
{
    public static void MapTotpEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/auth/totp").RequireAuthorization();

        grp.MapPost("/enable/init", (
            ClaimsPrincipal user,
            TotpService totp) =>
        {
            var email = user.FindFirstValue(ClaimTypes.Email)!;
            var secret = totp.GenerateSecret();
            var qr     = totp.BuildQrPngDataUri(secret, email);
            return Results.Ok(new TotpEnableInitResponse(secret, qr));
        });

        grp.MapPost("/enable/verify", async (
            TotpEnableVerifyRequest body,
            ClaimsPrincipal user,
            HttpContext http,
            PortalDbContext db,
            TotpService totp,
            TotpBackupCodeService backups,
            IClock clock,
            CancellationToken ct) =>
        {
            if (!totp.Verify(body.Secret, body.Code))
                return Results.BadRequest(new { error = "invalid_code" });

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var now    = clock.GetCurrentInstant();

            var row = await db.TotpSecrets.SingleOrDefaultAsync(t => t.UserId == userId, ct);
            var (ct_, nonce, tag) = totp.Encrypt(body.Secret);
            if (row is null)
            {
                row = new TotpSecret
                {
                    UserId     = userId,
                    Ciphertext = ct_, Nonce = nonce, Tag = tag,
                    EnabledAt  = now,
                    CreatedAt  = now,
                    UpdatedAt  = now,
                };
                db.TotpSecrets.Add(row);
            }
            else
            {
                row.Ciphertext = ct_; row.Nonce = nonce; row.Tag = tag;
                row.EnabledAt  = now;
                row.DisabledAt = null;
            }
            await db.SaveChangesAsync(ct);

            var backupCodes = await backups.IssueAsync(userId, ct);

            await RefreshTotpClaim(http, user, TotpClaimValues.Verified);

            return Results.Ok(new TotpEnableVerifyResponse(backupCodes));
        });

        grp.MapPost("/disable", async (
            TotpDisableRequest body,
            ClaimsPrincipal user,
            HttpContext http,
            PortalDbContext db,
            TotpService totp,
            TotpBackupCodeService backups,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var result = await totp.VerifyChallengeAsync(db, userId, body.Code, ct);
            if (result is TotpChallengeResult.Failed)
                return Results.Unauthorized();

            var row = await db.TotpSecrets.SingleAsync(t => t.UserId == userId, ct);
            row.DisabledAt = clock.GetCurrentInstant();
            await db.SaveChangesAsync(ct);
            await backups.PurgeUnusedAsync(userId, ct);

            await RefreshTotpClaim(http, user, TotpClaimValues.NotEnabled);
            return Results.NoContent();
        });

        // /totp-challenge is at the root, not under /api/auth/totp, because the cookie scheme's
        // AccessDeniedPath redirects browsers here directly.
        app.MapPost("/totp-challenge", async (
            TotpChallengeRequest body,
            ClaimsPrincipal user,
            HttpContext http,
            PortalDbContext db,
            TotpService totp,
            TotpBackupCodeService backups,
            CancellationToken ct) =>
        {
            if (user.Identity?.IsAuthenticated != true)
                return Results.Unauthorized();

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            var totpOk = await totp.VerifyChallengeAsync(db, userId, body.Code, ct);
            var ok = totpOk is TotpChallengeResult.Verified
                  || await backups.RedeemAsync(userId, body.Code, ct);
            if (!ok) return Results.Unauthorized();

            await RefreshTotpClaim(http, user, TotpClaimValues.Verified);
            return Results.NoContent();
        }).RequireAuthorization();
    }

    private static async Task RefreshTotpClaim(HttpContext http, ClaimsPrincipal user, string totpValue)
    {
        var identity = (ClaimsIdentity)user.Identity!;
        foreach (var existing in identity.FindAll(AuthClaimTypes.Totp).ToList())
            identity.RemoveClaim(existing);
        identity.AddClaim(new Claim(AuthClaimTypes.Totp, totpValue));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, user);
    }
}
```

Three subtleties:

- **`RequireAuthorization()` on the group** uses the default policy, which is "any authenticated user" (no `TotpRequired` — that would create a chicken-and-egg: you need to be TOTP-verified to verify TOTP). The cookie scheme's `AccessDeniedPath` is what redirects unverified users to `/totp-challenge` for *other* protected endpoints; `/totp-challenge` and the enable/disable endpoints intentionally bypass that policy.
- **`SignInAsync` re-emits the cookie** with the modified principal — this is the `RefreshSignInAsync` semantic. Calling `SignInAsync` on an already-authenticated cookie is the canonical ASP.NET Core way to update claims. `RefreshSignInAsync` is the same thing internally; `SignInAsync` is more obvious and doesn't require resolving the scheme handler manually.
- **Enable-while-already-enabled** (the user re-runs the enable flow without disabling first) is handled by the upsert in `/enable/verify`: existing row gets a new ciphertext, new `enabled_at`, cleared `disabled_at`, and the backup codes are re-issued (old ones are purged in `IssueAsync`). The UI should confirm before this happens — call out in the SvelteKit code.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings**.
- All existing PORTAL-003 tests still green; new TOTP tests all green.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts cleanly. Three new endpoints discoverable in OpenAPI: `POST /api/auth/totp/enable/init`, `POST /api/auth/totp/enable/verify`, `POST /api/auth/totp/disable`. Plus `POST /totp-challenge` (at root).
- **Enable round-trip (integration):** with `WithTestAuth(userId, totp: TotpClaimValues.NotEnabled)`, POST `/api/auth/totp/enable/init` returns `{ secret, qrPngDataUri }` where `qrPngDataUri` starts with `data:image/png;base64,`. Compute a TOTP code from `secret` against current `FakeClock` time. POST that code + secret to `/enable/verify`. Response includes 8 backup codes. `psql -c "SELECT count(*) FROM totp_secrets WHERE user_id = …"` → 1; `enabled_at IS NOT NULL`; `disabled_at IS NULL`. `count(*) FROM totp_backup_codes WHERE user_id = …` → 8. After the refresh-sign-in, `/api/auth/me` returns `totp: "verified"`.
- **Challenge with TOTP code:** with `WithTestAuth(userId, totp: TotpClaimValues.NotVerified)` and a `TotpSecret` row in the DB, POST `/totp-challenge` with a valid TOTP code → 204; subsequent `/api/auth/me` shows `totp: "verified"`. Wrong code → 401; claim unchanged.
- **Challenge with backup code:** with the same setup, POST `/totp-challenge` with a backup-code plaintext (test inserts an issued backup-code row first using `TotpBackupCodeService.IssueAsync` and captures the plaintext). 204. The row's `used_at` is now `IS NOT NULL`. Submitting the same backup code again → 401. Submitting a *different* unused backup code → 204.
- **Disable (TOTP code only):** with `WithTestAuth(..., totp: TotpClaimValues.Verified)` and an enabled `TotpSecret`, POST `/disable` with a valid TOTP code → 204. `disabled_at IS NOT NULL`. Unused backup codes deleted. `/api/auth/me` shows `totp: "not-enabled"`. POST `/disable` with a *backup code* → 401 (privilege escalation guard).
- **`/totp-challenge` requires authentication:** unauthenticated POST → 401 (cookie scheme's `OnRedirectToLogin` returns 401 for `/api` paths, but `/totp-challenge` is at root — verify behavior is consistent: either 401 directly or 302 to `/api/auth/signin`; either is acceptable per ADR-0030, but document the chosen behavior).
- **`TotpServiceTests` (unit):** generate-then-verify round-trip; reject codes from ±2 steps ago (outside the tolerance); reject empty / 5-digit / 7-digit codes; encrypt → decrypt round-trip; decrypt of a blob from a different protector purpose throws.
- **`TotpBackupCodeServiceTests` (unit):** `IssueAsync` produces 8 codes, all unique, all 8 characters from the Crockford alphabet; `RedeemAsync` of a known plaintext returns `true` and marks `UsedAt`; second `RedeemAsync` of the same plaintext returns `false`; `RedeemAsync` of a random string returns `false`; `IssueAsync` called twice purges the first set; `PurgeUnusedAsync` deletes `UsedAt IS NULL` rows only.
- **`TotpEndpointsTests` (integration):** the four acceptance flows above.
- **`TotpChallengeFailureTests` (integration):** wrong code → 401; cookie's `totp` claim unchanged; user not in `totp_secrets` → 401 (defensive — shouldn't reach here, but if `enabled_at IS NULL` somehow).
- **`pnpm build` in `src/ThanyMarcus.Portal.Web/`** still succeeds. Manual click-through (`dotnet watch` + `pnpm dev`):
  - `/settings/security`: shows current TOTP state from `/api/auth/me`.
  - Click "Enable TOTP" → QR rendered, scan with phone authenticator, enter code, see 8 backup codes (formatted in 4×2 grid, copyable).
  - Reload `/api/auth/me` → `totp: "verified"`.
  - Sign out + sign back in → land at `/totp-challenge`, enter code from authenticator, redirected to `/`.
  - Sign out + sign back in → land at `/totp-challenge`, enter a backup code, redirected to `/`. The backup code is now consumed (try the same code again on the next sign-in → 401).
  - On `/settings/security`, click "Disable TOTP" → prompted for code, enter it, claim flips to `not-enabled`.

## Concrete steps in order (each maps to a task)

1. **Add packages** — `Directory.Packages.props` + csproj. `dotnet restore`. Verify zero warnings.

2. **Add Data Protection registration** — one line in `Program.cs`. Existing tests still green.

3. **Land `TotpService` + `TotpServiceTests`** — TDD. The encrypt/decrypt tests need a real `DataProtectionProvider` — instantiate via `DataProtectionProvider.Create("test-app")` from `Microsoft.AspNetCore.DataProtection`, not the DI container, to keep the test unit-level.

4. **Land `TotpBackupCodeService` + `TotpBackupCodeServiceTests`** — TDD against `DbIntegrationTestBase`. Argon2id verify is slow (~250 ms per code × 8 codes = 2 s per `RedeemAsync` worst case — the service iterates because we don't know which row the user's code matches). Document the cost; PORTAL-003c rate-limits the endpoint at 5/5min so this is bounded.

5. **Land `TotpEndpoints` (skeleton — endpoints registered, returning 501)** — wires `MapTotpEndpoints` into `Program.cs`. Lets you `curl` and confirm routing without any logic.

6. **Land the request/response records** — `TotpChallengeRequest`, `TotpEnableInitResponse`, `TotpEnableVerifyRequest`, `TotpEnableVerifyResponse`, `TotpDisableRequest`. Trivial records.

7. **Implement `/enable/init`** — pure read; no DB writes. Test with `WithTestAuth(...)` that the response shape is right and the QR is a valid PNG (decode the base64, check the PNG magic bytes).

8. **Implement `/enable/verify`** — the meatiest endpoint. Write the integration test first; the assertion that `/api/auth/me` reads `verified` after the refresh-sign-in is the load-bearing one. Note: `WithTestAuth(...)` overrides the cookie scheme, so `SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, ...)` becomes a no-op in tests. Two ways: (a) test the post-state by setting `WithTestAuth(..., totp: Verified)` for a separate `/api/auth/me` call (proves the contract); (b) write *one* end-to-end test that uses the real cookie scheme via a sign-in helper (proves `SignInAsync` writes the cookie). Do (a) for the bulk of coverage and (b) once.

9. **Implement `/totp-challenge` POST** — TOTP code path first, then backup-code fallback. Two integration tests: one per path. A third test proves backup codes can't be reused.

10. **Implement `/disable`** — privilege-escalation guard (no backup-code fallback) is the load-bearing test; explicitly assert that POSTing a backup code returns 401.

11. **Remove the inline GET stub** from `AuthEndpoints.cs` — the SvelteKit route will serve it via fallback. Verify the cookie scheme's `AccessDeniedPath` redirect still works (manual: hit a `TotpRequired` endpoint as a `not-verified` user, expect the SPA shell to render and Svelte to route to `/totp-challenge`).

12. **SvelteKit routes** — `totp-challenge/+page.svelte` (form + POST), `settings/security/+page.svelte` (state + enable/disable). `lib/totpClient.ts` for fetch wrappers (small — 4 functions, ~30 lines total).

13. **Manual end-to-end** — sign in with the real Google client, enable TOTP, sign out, sign back in, complete the challenge with a TOTP code; sign out, sign back in, complete with a backup code; disable; sign out, sign back in — no challenge required.

14. **Verify all acceptance criteria** — every check above passes. Commit only after.

## Risks & gotchas

- **`SignInAsync` in tests under `WithTestAuth(...)` is a silent no-op.** The test cookie scheme doesn't actually emit `Set-Cookie`, so the post-refresh claim isn't observable via the same factory's HTTP client without re-baking. The cleanest test pattern: separate the *side effects* (DB writes — observable in `Db`) from the *cookie refresh* (cookie scheme behavior — covered by one end-to-end test against the real scheme). Don't fight the test harness.

- **TOTP clock skew vs. `FakeClock`.** `Otp.NET.Totp.VerifyTotp` reads `DateTime.UtcNow` internally — it does **not** take an `IClock`. Two options: (a) inject the timestamp via `Totp.VerifyTotp(code, out _, VerificationWindow.RfcSpecifiedNetworkDelay, timestamp: clock.GetCurrentInstant().ToDateTimeUtc())` (the overload exists), (b) accept that TOTP tests use wall-clock time. (a) is correct; verify the overload's parameter name in 1.4.x — the API has shifted across versions. The handoff sketch above does **not** thread the timestamp through; fix this in the implementation, and pass `IClock` to `TotpService.Verify` as an overload (or a per-call parameter).

- **`Otp.NET`'s `VerifyTotp` consumes a code window.** A naive implementation that calls `Verify` twice in a row with the same code can both succeed (within the 90 s window). This isn't a security hole for `/totp-challenge` (cookie's `totp` claim already flipped after first success — second call is a refresh) but *is* a problem for enable-verify if a user double-clicks. Idempotency: the second call hits `db.TotpSecrets.SingleOrDefaultAsync` → finds the just-inserted row → updates `enabled_at` to the same value → re-issues 8 *new* backup codes, invalidating the codes the user just wrote down. The UI must disable the verify button on click; the server should also debounce by checking `enabled_at IS NOT NULL && disabled_at IS NULL` and short-circuiting with a 409 if the user is already enabled. **Add this guard** in `/enable/verify` — it's not in the sketch above. (Alternatively, allow re-enable, but warn in the UI before issuing new backup codes.)

- **`Argon2id` cost in tests.** Each backup-code hash is ~250 ms with the OWASP-recommended params. 8 codes per `IssueAsync` = ~2 s. `RedeemAsync` iterates all unused rows = up to 8 × 250 ms = 2 s. Tests that exercise the full flow can hit ~10-15 s per test if naively written. Mitigation: lower the params in test config via an `IOptions<Argon2Options>` injection, **OR** scope each integration test tightly (don't issue + redeem all 8 codes per test). Prefer the latter — production params in tests catch real cost regressions.

- **Backup-code uniqueness across users isn't enforced.** Two users could theoretically generate the same 8-character code. Collision probability per pair: 8 × 32⁻⁸ ≈ 7 × 10⁻¹⁰; with 1000 users, ≈ 3 × 10⁻⁴ chance of *any* collision across the whole table. Acceptable. The `RedeemAsync` query is keyed by `user_id` first, so cross-user collision is moot anyway.

- **PHC format mismatch with PORTAL-006.** Recovery codes (PORTAL-006) also Argon2id-hashed; same package. The two PHC writers/readers should agree on format. The sketch here uses a hand-rolled format; PORTAL-006 may want to use a real PHC library (or share this). When PORTAL-006 lands, **refactor**: the second author should look at this file and decide whether to extract `Features/Auth/Argon2Hashing.cs` or accept the duplication. Don't pre-extract now — the seam isn't proven yet.

- **`MapPost("/totp-challenge", …)` route collision with the SvelteKit fallback.** `MapFallbackToFile("index.html")` runs only on unmatched routes. The explicit `MapPost("/totp-challenge", …)` matches only POST; GET `/totp-challenge` falls through to the SPA shell. Verify by `curl -X GET http://localhost:5000/totp-challenge` → returns `index.html` (200 + `Content-Type: text/html`), and `curl -X POST http://localhost:5000/totp-challenge -d '{...}'` → returns 204/401 from the API.

- **Data Protection key ring is in-memory by default.** Restarting the API rotates the keys, which means existing `TotpSecret.Ciphertext` blobs can't be decrypted → all enabled users get "TOTP broken" on next sign-in. **Acceptable for PORTAL-003a's dev/test scope; explicitly call this out in the README.** PORTAL-017 (deployment) lands `PersistKeysToFileSystem("/var/lib/portal/dp-keys")` for prod. Add a `TODO(PORTAL-017)` in `Program.cs` next to `AddDataProtection()`.

- **`/api/auth/totp/enable/verify` is unauthenticated against the `TotpRequired` policy** — it has to be, since the user can't be `verified` yet. But it *is* authenticated against the default policy (cookie present). Tests must use `WithTestAuth(userId, totp: TotpClaimValues.NotEnabled)`. Don't try to gate this endpoint behind `TotpRequired`.

- **SvelteKit `+page.svelte` for `/totp-challenge` must not require `me.totp === 'verified'`** in its load function — it's the *unverified* state's destination. The page should fetch `/api/auth/me` and:
  - if `me === null` → redirect to `/api/auth/signin`
  - if `me.totp === 'verified'` → redirect to `/`
  - if `me.totp === 'not-verified'` → render the form
  - if `me.totp === 'not-enabled'` → redirect to `/` (shouldn't happen; defensive)

- **OpenAPI schemas for the request records.** Hand-written TS types per `portal_tooling` — when this lands, run whatever sync mechanism the project uses to refresh the TS contracts. Check `scripts/` for an existing generator; if none, the SvelteKit code can hand-type the four small request/response shapes inline in `totpClient.ts`.

- **`/disable` requires the user to be `verified` already.** The default policy (just authenticated) lets a `not-verified` user hit `/disable`, which is wrong — that's a way to skip TOTP verification by disabling it. Add `.RequireAuthorization(AuthPolicies.TotpRequired)` to `/disable` only. `/enable/init`, `/enable/verify`, and `/totp-challenge` stay on the default policy.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + green `dotnet run --project tests/...` + a manual end-to-end: enable TOTP with a real authenticator app, sign out, sign back in, challenge with TOTP code; sign out, sign back in, challenge with backup code; disable; sign out, sign back in (no challenge).

A fresh agent can pick up PORTAL-003b (step-up auth + `IInfraOpUnlockCache`) from cold by reading:

1. ADR-0030 §"Step-up sequence (infra op)" + §"Cookie claim shape".
2. ADR-0031 §"Ticket split" for the boundary.
3. PORTAL-003a's `Features/Auth/Totp/` shape as the template for `Features/Auth/StepUp/`.
4. The Argon2id PHC pattern in `TotpBackupCodeService.HashCode` — passphrase verification uses the same package but a different param profile; consider extracting `Argon2Hashing` at that point.

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-004 — 0.75 d covering "TOTP 2FA enable/disable + QR + verify-on-disable". This handoff is the full PORTAL-003a as scoped in ADR-0031 §"Ticket split"; backup codes were originally PORTAL-006's territory, but the auth grilling reshuffled them under TOTP because they restore TOTP-verified state (not passphrase access — that's recovery codes).
- Adjacent tickets that PORTAL-003a unblocks:
  - PORTAL-003b — step-up auth (consumes the `TotpRequired` policy landed in 003 + the `verified` state landed here; gates infra ops on top)
  - PORTAL-003c — in-memory rate limiting (decorates `/totp-challenge` POST + `/api/auth/totp/*`)
  - PORTAL-003d — persistent lockout (hooks `TotpChallengeResult.Failed` to write `auth_lockouts` rows)
  - PORTAL-003e — Cloudflare Turnstile (gates `/totp-challenge` post-throttle)
  - PORTAL-005 — passphrase-encrypted provider tokens (reuses the Argon2id PHC pattern; consider extracting `Argon2Hashing` at that point)
  - PORTAL-006 — recovery codes (separate code set; same hash pattern; same UI archetype as backup codes — "show once, copy to clipboard")
  - PORTAL-011 — provisioning wizard (the first feature that benefits from `TotpRequired` being meaningfully enforced)
