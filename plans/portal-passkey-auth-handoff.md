# PORTAL-PASSKEY-AUTH — FIDO2/WebAuthn passkeys: additive login AND username-only signup

**Goal:** make passkeys a first-class authentication path that supports two flows: (1) a user with a Google SSO account can register additional passkeys for faster subsequent login; (2) a user with no Google account at all can sign up with a username and a passkey, no IdP involvement. Phishing-resistant, no shared secret, ~1.5s sign-in via Face ID / Touch ID / Windows Hello / YubiKey. Estimated **4–5 person-days** total (2–3d for the additive path, 1.5–2d for username-only signup on top).

## Why this exists

Today the Portal has exactly one primary login method: **Google SSO**. That's fine for the bootstrap moment ("first time here, who are you?") but produces four real downsides:

1. **Single point of failure.** If Google revokes your access, the user is locked out. Even legitimately: app-specific 2FA changes on Google's side, device-trust prompts, country-block detection during travel — any of these can interrupt the login.
2. **Third-party dependency.** The whole identity stack depends on Google. Users who don't have or don't trust a Google account can't use the product at all.
3. **Phishing surface.** A look-alike domain (`thany-marcus.click`, `dev-thany.click`, IDN-variant) can present a fake "Sign in with Google" button and harvest the OAuth code. Passkeys eliminate this by binding the credential to the exact origin in the browser.
4. **UX cost.** Google sign-in is multi-redirect: Portal → Google consent → Portal callback. Each hop is a chance to fail (cookie issues, third-party-cookie blocking, network blip). Passkey login is in-browser: a single biometric prompt, no redirect.

Passkeys (FIDO2 + WebAuthn) are the modern open standard for password-less auth. Every browser since 2020 supports them. The .NET ecosystem has a well-maintained library (`Fido2NetLib`). This ticket lands passkeys as both an **additive credential** (for Google-SSO users) and a **standalone signup mechanism** (for users who want to skip Google entirely).

Thesis angle: demonstrating phishing-resistant, password-less auth that doesn't depend on any third-party identity provider is a defensible engineering story. The "sovereign account" framing — no email, no IdP, just a username + a hardware-backed credential the user owns — is consistent with the broader thesis position (the user owns their data, their compute, their identity).

## Scope

**Phase 1 — additive passkey for SSO users (in scope, partially landed):**
- New table `passkey_credentials` (one row per registered passkey, multiple per user).
- Server-side WebAuthn ceremonies via `Fido2NetLib` NuGet package:
  - **Registration challenge** + **completion** — called from an authenticated session to add a passkey to the user.
  - **Login challenge** + **completion** — called from an unauthenticated session to log in with a previously-registered passkey.
- Four new endpoints on Portal API, gated/ungated as appropriate:
  - `POST /api/auth/passkey/register/challenge` (authenticated)
  - `POST /api/auth/passkey/register/complete` (authenticated)
  - `POST /api/auth/passkey/login/challenge` (anonymous)
  - `POST /api/auth/passkey/login/complete` (anonymous → authenticated)
- Challenge storage: short-TTL (5 min) in-memory cache, keyed on a random `challenge_id` returned to the client. No Redis dependency in v1.
- Sign-in screen: a second button alongside Google — "Sign in with passkey". Click → browser autofill prompt → biometric → signed in.
- Settings page: a "Passkeys" section listing registered passkeys (with their device-name, registration date, last-used date) and a Revoke button per row.
- Onboarding nudge: after first Google login (when the user has 0 passkeys), show a one-shot "Want faster login next time? Register a passkey →" banner.
- TOTP integration: if TOTP is enabled, it still gates session login after passkey auth (per `feedback_totp_login_only`). Passkeys do NOT replace TOTP.
- Backend tests for both ceremonies + revoke + duplicate-credential rejection.
- Frontend tests for the WebAuthn API calls (with mocked `navigator.credentials`).

**Phase 2 — username-only signup (in scope, additive on top of Phase 1):**
- `users.google_sub` column becomes nullable (today it's NOT NULL for every user).
- `users.email` column becomes nullable (today populated from Google `email` claim).
- New `users.username` column (TEXT, case-insensitively unique, 3–32 chars, `[a-zA-Z0-9_-]`). For Google-SSO users we backfill from `email`-local-part on first migration. For passkey-only users, they pick it at signup.
- Reserved-username denylist: `admin`, `root`, `support`, `system`, `api`, `health`, `thany`, `marcus`, `oauth`, anything starting with `_` or `-`. Block at signup.
- New endpoint `POST /api/auth/passkey/signup/challenge` (anonymous) — accepts `{ username }`, validates uniqueness + format + denylist, returns WebAuthn `PublicKeyCredentialCreationOptions`.
- New endpoint `POST /api/auth/passkey/signup/complete` (anonymous → authenticated) — accepts the attestation response; on success, atomically creates a `users` row (with `username`, no `email`, no `google_sub`) **and** a `passkey_credentials` row, in a single transaction, then issues the session cookie.
- Sign-in screen restructure: two columns at the top — "Sign in / Sign up with Google" and "Sign in / Sign up with passkey". The passkey button does conditional login (existing passkey) → falls through to signup if no credential exists.
- New `/signup/passkey` page for the explicit "I want to sign up with passkey" path — a single field (username) + a "Create account with passkey" button.
- CAPTCHA (Turnstile) is REQUIRED on the signup/challenge endpoint — passkey-only signup is an anonymous-write endpoint, so we need bot defence. Reuse the existing Turnstile flow with the `signup` action key.
- Onboarding for passkey-only users skips the "link your Google" step (which doesn't apply) and skips the `email` collection (since there is none). Goes straight to passphrase → Emergency Kit → optional TOTP → done.
- **Explicit "no recovery" copy** in the signup-passkey flow: "Thany-Marcus has no email on file for you. If you lose your passkey and your Emergency Kit, your account cannot be recovered." A required acknowledgement checkbox before account creation.
- Backend tests for: signup happy path; username conflict (existing); username invalid (format/denylist); CAPTCHA missing/invalid; rate-limit on signup endpoint.
- Frontend tests for the signup flow + the conditional sign-in/sign-up unification.

**Out of scope:**
- **Passkey as step-up authentication replacement.** The existing passphrase-based DEK unlock (per `feedback_totp_login_only`) stays as the per-action friction for sensitive operations. Replacing passphrase with passkey is a separate, valuable but distinct ticket.
- **Cross-device CTAP2 QR flow** as a custom UI. The browser provides this natively when the user picks "Add new device" during a passkey prompt — we get it for free.
- **Multi-tenant / enterprise SSO** (SAML, OIDC against other IdPs). Out of scope; Google stays the sole federated IdP.
- **Conditional UI (autofill suggestions)** beyond the default the browser provides. Browsers since 2023 surface passkeys in the username field on focus; we don't need to opt into anything beyond the standard `mediation: "conditional"` flag.
- **Per-device naming UX.** v1 stores the AAGUID-derived authenticator name ("iCloud Keychain", "Windows Hello", "YubiKey 5") and the user can't rename. Rename support is additive later.
- **Adding an email later to a passkey-only account.** Possibly a future enhancement (for support, etc.); for v1, a username-only account stays username-only forever. If a user wants email-backed support, they should sign up via Google SSO.
- **Reverse direction: removing Google SSO from a Google-bootstrapped account.** Once `google_sub` is set, it stays set. Users can register passkeys to skip Google at login, but can't "delete" the Google linkage. Avoids a class of identity-confusion bugs.
- **Account recovery via support / customer-service path.** There is no support email for the project — by design. Users carry their own recovery (passphrase / Emergency Kit / passkeys). Out-of-band identity verification is not a flow we implement.

## The user flow

**Registration (after first Google login):**
1. Onboarding checklist shows "Add a passkey" item.
2. User clicks → frontend calls `POST /api/auth/passkey/register/challenge`.
3. Server returns a `PublicKeyCredentialCreationOptions` payload (challenge bytes, RP ID, allowed authenticator types, etc.) + a `challenge_id` to correlate.
4. Frontend passes the options to `navigator.credentials.create({ publicKey: options })`.
5. Browser prompts for biometric/PIN; user confirms.
6. Browser returns an `AuthenticatorAttestationResponse`. Frontend POSTs it to `/api/auth/passkey/register/complete` along with the `challenge_id`.
7. Server verifies attestation via `Fido2NetLib`, extracts credential_id + public_key + AAGUID, inserts into `passkey_credentials`.
8. UI shows "Passkey registered — try signing out and back in!"

**Login:**
1. Sign-in screen shows "Sign in with Google" and "Sign in with passkey" buttons.
2. User clicks passkey button → frontend calls `POST /api/auth/passkey/login/challenge`.
3. Server returns a `PublicKeyCredentialRequestOptions` (challenge, allowed credential IDs) + `challenge_id`. With "discoverable credentials", `allowCredentials` can be empty — the device picks from its stored passkeys.
4. Frontend calls `navigator.credentials.get({ publicKey: options })`.
5. Browser shows passkey picker (if multiple) and prompts for biometric.
6. Browser returns `AuthenticatorAssertionResponse`. Frontend POSTs to `/api/auth/passkey/login/complete`.
7. Server resolves the credential_id to a user, verifies the signature against the stored public_key, increments `sign_count`, issues a session cookie (same shape Google login produces today).
8. If TOTP is enabled for this user, the session is in `pending_totp` state — same path as post-Google-SSO. Otherwise fully authenticated.

**Revoke (from Settings):**
1. User clicks the trash icon on a passkey row.
2. Frontend POSTs `DELETE /api/auth/passkey/{id}` (authenticated).
3. Server soft-deletes (`revoked_at = NOW()`) the row.
4. Future login attempts with that credential ID are rejected.

**Sign up with passkey (Phase 2 — no Google account involved):**
1. User clicks "Sign up with passkey" from the sign-in screen.
2. New page asks for a single field: **username** (3–32 chars, alphanumeric + `_-`, case-insensitively unique, not in denylist).
3. Page shows a required acknowledgement: ☐ *"I understand that Thany-Marcus has no email on file for me. If I lose my passkey AND my Emergency Kit, my account cannot be recovered."* Must be ticked to proceed.
4. Turnstile CAPTCHA must be solved.
5. User clicks "Create account" → frontend calls `POST /api/auth/passkey/signup/challenge` with `{ username, turnstileToken, acknowledged: true }`.
6. Server validates: username format, denylist, uniqueness, CAPTCHA. On any failure returns 400 with a specific error code. On success generates `PublicKeyCredentialCreationOptions` and a `challenge_id`, returns both.
7. Frontend calls `navigator.credentials.create({ publicKey: options })`. Browser prompts for biometric/PIN.
8. On success, frontend POSTs the attestation response to `POST /api/auth/passkey/signup/complete` with the `challenge_id`.
9. Server verifies attestation via `Fido2NetLib`. In a single transaction: inserts a `users` row with the username (no `email`, no `google_sub`), inserts a `passkey_credentials` row, issues the session cookie.
10. User lands on onboarding: passphrase setup → Emergency Kit modal → optional TOTP → done.

## Concrete files

### Database

#### Migration `0NNN_passkey_credentials.sql`

```sql
CREATE TABLE passkey_credentials (
    id              UUID PRIMARY KEY,
    user_id         UUID NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    credential_id   BYTEA NOT NULL,
    public_key      BYTEA NOT NULL,
    sign_count      INTEGER NOT NULL DEFAULT 0,
    aaguid          UUID NOT NULL,
    authenticator_name TEXT,
    transports      TEXT[] NOT NULL DEFAULT '{}',
    backed_up       BOOLEAN NOT NULL DEFAULT FALSE,
    created_at      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_used_at    TIMESTAMPTZ,
    revoked_at      TIMESTAMPTZ
);

CREATE UNIQUE INDEX idx_passkey_credentials_credential_id
    ON passkey_credentials (credential_id)
    WHERE revoked_at IS NULL;

CREATE INDEX idx_passkey_credentials_user_id
    ON passkey_credentials (user_id)
    WHERE revoked_at IS NULL;
```

- `credential_id` is the opaque WebAuthn credential identifier — unique per registration, used as the lookup key during login. Stored as bytea (WebAuthn opaque blob).
- `public_key` stored as bytea (CBOR-encoded COSE_Key per WebAuthn spec — Fido2NetLib handles encode/decode).
- `aaguid` identifies the authenticator model; used to derive a friendly name ("iCloud Keychain", "YubiKey 5", "Windows Hello") via a lookup table or the MDS (Metadata Service).
- `transports` records `["internal"]` for platform authenticators, `["usb", "nfc"]` for roaming keys, etc. Used to hint to the browser during login.
- `backed_up` distinguishes synced passkeys (cloud-backed, recoverable if device lost) from device-bound ones — useful UX hint in the Settings list.

### Backend

#### `src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj` — ADD

```xml
<PackageReference Include="Fido2NetLib" />
```

#### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeyConfiguration.cs` — NEW

```csharp
public sealed record PasskeyConfiguration
{
    public required string ServerDomain { get; init; }       // "thany.click" or "dev.thany.click" — the RP ID
    public required string ServerName { get; init; }         // "Thany-Marcus" — displayed on user's device
    public required string[] Origins { get; init; }          // ["https://dev.thany.click"] — allowed origins
    public required TimeSpan ChallengeTtl { get; init; }     // 5 min
}
```

Read from `appsettings.Development.json`:

```json
"Auth": {
  "Passkey": {
    "ServerDomain": "dev.thany.click",
    "ServerName": "Thany-Marcus Portal",
    "Origins": ["https://dev.thany.click"],
    "ChallengeTtlSeconds": 300
  }
}
```

Production overrides `ServerDomain` and `Origins`.

**RP ID strategy decision needed:** use `thany.click` (passkeys work on both `dev.thany.click` and `thany.click`) or `dev.thany.click` (scoped to the dev portal). Recommendation: `thany.click` — passkey works across the parent domain and any subdomain, useful if you ever add `app.thany.click` etc. Setting RP ID requires the page's effective domain to be a registrable suffix of the RP ID, which `dev.thany.click ⊂ thany.click` satisfies.

#### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeyChallengeStore.cs` — NEW

In-memory `ConcurrentDictionary<Guid, ChallengeRecord>` with a background sweep:

```csharp
public sealed record ChallengeRecord(byte[] Challenge, Guid? UserId, Instant ExpiresAt, ChallengeKind Kind);
public enum ChallengeKind { Register, Login }

public interface IPasskeyChallengeStore
{
    Guid Stash(byte[] challenge, Guid? userId, ChallengeKind kind);
    ChallengeRecord? Take(Guid challengeId);  // single-use; removes on read
}
```

Single-use semantics: a challenge is removed when consumed, preventing replay. Stale entries swept every minute via a `BackgroundService`.

#### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeyRegisterEndpoints.cs` — NEW

```csharp
group.MapPost("/challenge", RegisterChallengeAsync).RequireAuthorization();
group.MapPost("/complete",  RegisterCompleteAsync ).RequireAuthorization();

private static async Task<IResult> RegisterChallengeAsync(
    IFido2 fido2,
    ClaimsPrincipal user,
    PortalDbContext db,
    IPasskeyChallengeStore store,
    CancellationToken ct)
{
    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
    var u = await db.Users.SingleAsync(x => x.Id == userId, ct);
    var existing = await db.PasskeyCredentials
        .Where(p => p.UserId == userId && p.RevokedAt == null)
        .Select(p => new PublicKeyCredentialDescriptor(p.CredentialId))
        .ToListAsync(ct);

    var options = fido2.RequestNewCredential(
        new RequestNewCredentialParams {
            User = new Fido2User { Id = userId.ToByteArray(), Name = u.Email, DisplayName = u.Name },
            ExcludeCredentials = existing,
            AuthenticatorSelection = new AuthenticatorSelection {
                ResidentKey = ResidentKeyRequirement.Preferred,
                UserVerification = UserVerificationRequirement.Required,
            },
            AttestationPreference = AttestationConveyancePreference.None,
        });

    var challengeId = store.Stash(options.Challenge, userId, ChallengeKind.Register);
    return Results.Ok(new { challengeId, options });
}
```

`RegisterCompleteAsync` verifies attestation via `fido2.MakeNewCredentialAsync`, then inserts the row.

#### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeyLoginEndpoints.cs` — NEW

Mirror shape, but anonymous-authorized. After successful `MakeAssertionAsync`, build the session cookie via the same path Google SSO uses (look for `SignInUserAsync` or equivalent in the existing auth feature).

#### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeyRevokeEndpoint.cs` — NEW

```csharp
app.MapDelete("/api/auth/passkeys/{id:guid}", async (Guid id, ClaimsPrincipal u, PortalDbContext db, CancellationToken ct) =>
{
    var userId = Guid.Parse(u.FindFirstValue(AuthClaimTypes.SubUs)!);
    var cred = await db.PasskeyCredentials.SingleOrDefaultAsync(p => p.Id == id && p.UserId == userId, ct);
    if (cred is null) return Results.NotFound();
    cred.RevokedAt = clock.GetCurrentInstant();
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();
```

#### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeyListEndpoint.cs` — NEW

```csharp
app.MapGet("/api/auth/passkeys", async (ClaimsPrincipal u, PortalDbContext db, CancellationToken ct) =>
{
    var userId = Guid.Parse(u.FindFirstValue(AuthClaimTypes.SubUs)!);
    var creds = await db.PasskeyCredentials
        .Where(p => p.UserId == userId && p.RevokedAt == null)
        .OrderByDescending(p => p.CreatedAt)
        .Select(p => new PasskeyDto(
            p.Id,
            AaguidToName(p.Aaguid),   // "iCloud Keychain", "YubiKey 5", etc.
            p.CreatedAt.ToDateTimeOffset(),
            p.LastUsedAt.HasValue ? p.LastUsedAt.Value.ToDateTimeOffset() : (DateTimeOffset?)null,
            p.BackedUp))
        .ToListAsync(ct);
    return Results.Ok(creds);
}).RequireAuthorization();
```

#### `src/ThanyMarcus.Portal.Api/Program.cs` — EDIT

```csharp
builder.Services.Configure<PasskeyConfiguration>(builder.Configuration.GetSection("Auth:Passkey"));
builder.Services.AddSingleton<IFido2>(sp => {
    var cfg = sp.GetRequiredService<IOptions<PasskeyConfiguration>>().Value;
    return new Fido2(new Fido2Configuration {
        ServerDomain = cfg.ServerDomain,
        ServerName   = cfg.ServerName,
        Origins      = new HashSet<string>(cfg.Origins),
    });
});
builder.Services.AddSingleton<IPasskeyChallengeStore, PasskeyChallengeStore>();
builder.Services.AddHostedService<PasskeyChallengeStoreSweeper>();
```

And wire the new endpoint groups in the auth section.

#### Phase 2 additions

##### Migration `0NNN_users_username_nullable_sso.sql`

```sql
ALTER TABLE users ALTER COLUMN google_sub DROP NOT NULL;
ALTER TABLE users ALTER COLUMN email      DROP NOT NULL;
ALTER TABLE users ADD COLUMN username TEXT;
CREATE UNIQUE INDEX idx_users_username_lower
    ON users (LOWER(username))
    WHERE username IS NOT NULL;

-- Backfill: derive username from email-local-part for existing Google-SSO users.
-- Collisions (very unlikely with small user count) get a numeric suffix.
UPDATE users
SET username = split_part(email, '@', 1)
WHERE username IS NULL AND email IS NOT NULL;
```

##### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/UsernameValidator.cs` — NEW

```csharp
public static class UsernameValidator
{
    private static readonly HashSet<string> Denylist = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin","root","support","system","api","health","oauth",
        "thany","marcus","help","contact","postmaster","webmaster",
    };

    public static ValidationResult Validate(string raw)
    {
        if (raw is null) return ValidationResult.Invalid("required");
        var trimmed = raw.Trim();
        if (trimmed.Length is < 3 or > 32) return ValidationResult.Invalid("length");
        if (!Regex.IsMatch(trimmed, "^[a-zA-Z0-9_-]+$")) return ValidationResult.Invalid("chars");
        if (trimmed.StartsWith('_') || trimmed.StartsWith('-')) return ValidationResult.Invalid("prefix");
        if (Denylist.Contains(trimmed)) return ValidationResult.Invalid("reserved");
        return ValidationResult.Ok(trimmed);
    }
}
```

##### `src/ThanyMarcus.Portal.Api/Features/Auth/Passkey/PasskeySignupEndpoints.cs` — NEW

```csharp
group.MapPost("/signup/challenge", SignupChallengeAsync).AllowAnonymous();
group.MapPost("/signup/complete",  SignupCompleteAsync ).AllowAnonymous();

private static async Task<IResult> SignupChallengeAsync(
    PasskeySignupChallengeRequest req,
    IFido2 fido2,
    PortalDbContext db,
    IPasskeyChallengeStore store,
    ITurnstileVerifier turnstile,
    HttpContext httpContext,
    CancellationToken ct)
{
    // 1. CAPTCHA
    if (!await turnstile.VerifyAsync(req.TurnstileToken, "signup", httpContext.RemoteIpAddress(), ct))
        return Results.BadRequest(new { error = "captcha_invalid" });

    // 2. Acknowledgement
    if (!req.AcknowledgedNoRecovery)
        return Results.BadRequest(new { error = "acknowledgement_required" });

    // 3. Username validity
    var validated = UsernameValidator.Validate(req.Username);
    if (!validated.IsOk) return Results.BadRequest(new { error = $"username_{validated.Reason}" });

    // 4. Uniqueness
    if (await db.Users.AnyAsync(u => EF.Functions.ILike(u.Username!, validated.Username), ct))
        return Results.Conflict(new { error = "username_taken" });

    // 5. Issue challenge
    var newUserId = Guid.CreateVersion7();
    var options = fido2.RequestNewCredential(new RequestNewCredentialParams {
        User = new Fido2User { Id = newUserId.ToByteArray(), Name = validated.Username, DisplayName = validated.Username },
        AuthenticatorSelection = new AuthenticatorSelection {
            ResidentKey = ResidentKeyRequirement.Required,    // discoverable creds — needed since no username at login
            UserVerification = UserVerificationRequirement.Required,
        },
        AttestationPreference = AttestationConveyancePreference.None,
    });

    var challengeId = store.Stash(options.Challenge, userId: newUserId, ChallengeKind.Signup,
        extraData: new { Username = validated.Username });
    return Results.Ok(new { challengeId, options });
}

// SignupCompleteAsync: verifies attestation, atomically inserts (users, passkey_credentials), issues session.
```

##### `src/ThanyMarcus.Portal.Web/src/routes/signup/passkey/+page.svelte` — NEW

Single-page form: username input + acknowledgement checkbox + Turnstile widget + "Create account" button. On submit calls the two-step signup ceremony. Success redirects to onboarding (`/`).

### Frontend

#### `src/ThanyMarcus.Portal.Web/src/lib/auth/passkey.ts` — NEW

Thin wrapper around `navigator.credentials.create` and `navigator.credentials.get`:

```ts
export async function registerPasskey(api: ApiClient): Promise<void> {
    const { challengeId, options } = await api.passkey.registerChallenge();
    const credential = await navigator.credentials.create({
        publicKey: deserializeCreationOptions(options),
    }) as PublicKeyCredential | null;
    if (!credential) throw new Error('user cancelled');
    await api.passkey.registerComplete({ challengeId, credential: serializeCredential(credential) });
}

export async function loginWithPasskey(api: ApiClient): Promise<void> {
    const { challengeId, options } = await api.passkey.loginChallenge();
    const assertion = await navigator.credentials.get({
        publicKey: deserializeRequestOptions(options),
    }) as PublicKeyCredential | null;
    if (!assertion) throw new Error('user cancelled');
    await api.passkey.loginComplete({ challengeId, assertion: serializeAssertion(assertion) });
}
```

Serialize/deserialize helpers convert base64url ↔ ArrayBuffer (WebAuthn JSON uses base64url-encoded bytes; the API needs ArrayBuffers).

#### `src/ThanyMarcus.Portal.Web/src/routes/+page.svelte` — EDIT (signed-out view)

After Phase 2 lands, the signed-out hero presents two side-by-side flows ("Sign in / Sign up"):

```svelte
<div class="auth-options">
  <section>
    <h2>With Google</h2>
    <a class="btn btn-primary" href={signInHref()}>Continue with Google</a>
    <p class="muted">Uses your Google account for identity.</p>
  </section>
  <section>
    <h2>With passkey</h2>
    <button class="btn btn-secondary" onclick={() => void handlePasskeyLogin()}>Sign in with passkey</button>
    <a class="link" href="/signup/passkey">No account? Sign up with passkey →</a>
    <p class="muted">No email needed. You manage your own recovery.</p>
  </section>
</div>
```

`handlePasskeyLogin` calls `loginWithPasskey` then `invalidateAll()`. If `loginWithPasskey` fails with "no credentials found" (browser shows no passkey for this origin), the link to `/signup/passkey` is the natural next step.

#### `src/ThanyMarcus.Portal.Web/src/routes/settings/+page.svelte` — EDIT

Add a "Passkeys" section:

```svelte
<section class="card">
  <h2>Passkeys</h2>
  <ul class="passkey-list">
    {#each passkeys as p (p.id)}
      <li>
        <span>{p.authenticatorName} {p.backedUp ? '(synced)' : '(device-bound)'}</span>
        <span class="muted">added {formatDate(p.createdAt)} · last used {formatDate(p.lastUsedAt) ?? 'never'}</span>
        <button onclick={() => void revoke(p.id)}>Revoke</button>
      </li>
    {/each}
  </ul>
  <button class="btn btn-secondary" onclick={() => void register()}>Add a passkey</button>
</section>
```

#### `src/ThanyMarcus.Portal.Web/src/lib/OnboardingChecklist.svelte` — EDIT

Add a "Register a passkey" item that's checked when `passkeys.length > 0`. Click → calls `registerPasskey`.

## Tests

### Backend (Phase 1)
- `PasskeyChallengeStoreTests`: Stash returns a unique GUID; Take consumes (single-use); expired entries are removed by the sweeper.
- `PasskeyRegisterEndpointsTests`: challenge endpoint returns valid options; complete endpoint verifies attestation (using a mocked Fido2NetLib attestation for a test authenticator); duplicate credential_id rejected; missing challenge_id rejected.
- `PasskeyLoginEndpointsTests`: login challenge issued; complete verifies assertion; unknown credential_id rejected; `sign_count` regression rejected (signing-count must monotonically increase per the spec — replay-attack guard).
- `PasskeyRevokeEndpointTests`: own-credential revoked successfully; other-user's credential 404'd (no cross-user leakage); revoked credential rejected on next login attempt.
- `PasskeyListEndpointTests`: returns only own credentials, excludes revoked, sorts by recency.

### Backend (Phase 2)
- `UsernameValidatorTests`: accepts `alice`, `bob_smith`, `x_1`; rejects `ad`, `admin`, `_alice`, `alice@bob`, `verylonglonglonglonglonglongusername` etc.
- `PasskeySignupEndpointsTests`: happy path creates user + passkey in one transaction; username collision returns 409 `username_taken`; reserved name returns 400 `username_reserved`; CAPTCHA invalid returns 400 `captcha_invalid`; acknowledgement missing returns 400 `acknowledgement_required`; concurrent same-username signups → exactly one succeeds, the other gets 409.
- `UsersMigrationTests`: `google_sub` nullable; `email` nullable; backfill populates `username` from `email`-local-part for existing rows; collisions resolved with numeric suffix.

### Frontend
- `passkey.test.ts`: register flow calls API in correct order; login flow handles user-cancelled (no assertion) gracefully.
- Component test for the settings section: shows existing passkeys, revoke triggers API call + refetch.
- `signup/passkey/+page.svelte` test: form validation (username + ack + CAPTCHA all required); successful signup calls API in order; conflict shows inline error without losing the username.

### Manual smoke
- Mac with Touch ID: register on first Google login; sign out; sign back in with passkey → unlocks via Touch ID in ~1.5s.
- Phone (cross-device): on the desktop login screen, pick "Try another way" → scan QR with phone → authenticate via phone Face ID. Browser handles the QR; we get the user signed in on desktop.
- YubiKey: register a roaming key; tap to sign in.
- **Phase 2:** new browser, no existing session → "Sign up with passkey" → pick username `demo_user` → tick ack + solve CAPTCHA → biometric prompt → onboarding (passphrase + Emergency Kit + optional TOTP) → fully signed in. Verify no `email` or `google_sub` on the user row.

## Migration / deployment notes

- **Phase 1** migration is purely additive (new `passkey_credentials` table). No data loss on rollback.
- **Phase 2** migration relaxes `users.google_sub` and `users.email` to nullable and adds `username`. Existing rows get `username` backfilled from `email`-local-part. On rollback, you can re-add NOT NULL constraints only after deleting any rows with null `email`/`google_sub` (i.e., username-only users created in the interim). Document this as a one-way migration in practice.
- `Fido2NetLib` is a single NuGet add, no native dependencies.
- The RP ID is set in config, not committed — `appsettings.Development.json` has `dev.thany.click`, production deployment env var overrides for `thany.click`.
- HTTPS is required (WebAuthn rejects non-secure contexts except `localhost`). Dev portal is already HTTPS via Caddy + Let's Encrypt.
- After Phase 1 deploy: log in via Google as a test user, register a passkey, sign out, sign back in with passkey. Verify TOTP flow still gates if TOTP is enabled.
- After Phase 2 deploy: open a fresh browser profile, navigate to the Portal, choose "Sign up with passkey" → flow to completion. Verify a username-only `users` row was created, no email/google_sub.

## Risks / open questions

- **RP ID choice (`thany.click` vs `dev.thany.click`).** Once chosen, all existing passkeys are bound to that RP. Switching later would force users to re-register. Recommend `thany.click` (the parent domain) — passkeys work across all subdomains. **Decide before first deploy.**
- **Lost device + no other passkey + Google account hijacked.** The user is locked out. Mitigation: ensure Google SSO recovery flows are intact + recommend (but don't enforce) registering ≥2 passkeys (e.g. phone + Mac).
- **Phase 2: username-only user loses everything.** No Google account to recover. No email on file. If they lose all passkeys and the Emergency Kit, the account is permanently dead and we cannot help. This is **by design** and called out in the signup flow's required acknowledgement copy. Trade-off accepted for the "no third-party identity dependency" thesis story.
- **AAGUID-to-name mapping.** A few hundred well-known authenticator GUIDs have human-readable names ("iCloud Keychain": `dd4ec289-e01d-41c9-bb89-70fa845d4bf2`, "Windows Hello": ...). FIDO MDS3 is the canonical source. v1 ships with a small static map of the top 20; fall back to "Unknown authenticator" for the rest. Periodic refresh from MDS is a future improvement.
- **Fido2NetLib version.** As of late 2025, `Fido2NetLib` is the de-facto .NET FIDO2 library; well-maintained but small community. If concerns about maintenance arise, the WebAuthn protocol itself is stable; vendor lock-in is minimal — switching libraries is mostly a different `Fido2.MakeAssertionAsync` API shape.
- **TOTP + passkey UX.** With both enabled, a passkey login still prompts for TOTP. Confusing because passkeys are themselves a strong second factor. Decision: keep TOTP path for v1 to honour the existing security policy; consider a "passkey counts as second factor" config flag in a follow-up after demo.
- **Discoverable credentials (resident keys) vs server-side credential discovery.** Phase 2 requires `ResidentKey = Required` (we don't have a username at sign-in time and rely on the browser's passkey picker). Phase 1 uses `Preferred`. Both can coexist in the same database — different `userId` values, same authenticator can hold both.
- **Browser support degradation.** Old browsers without WebAuthn (IE, ancient Firefox) get only the Google SSO button. Detect `!window.PublicKeyCredential` and hide the passkey button + the Phase 2 "Sign up with passkey" link. No-feature graceful degradation.
- **Username squatting.** Anonymous signup means anyone can grab popular usernames. Reserved-name denylist mitigates the worst cases (`admin`, etc.). Beyond that, first-come first-served. If you ever want to add identity verification later (link an email), squatted usernames can be reclaimed via the "abandoned account" cleanup path — but that's out of scope here.
- **Spam / bot signups.** Phase 2 endpoint is anonymous-write. CAPTCHA (Turnstile) is the primary defence. Add per-IP rate-limit (10/hour) as a secondary measure. Failure mode if abused: storage cost is negligible (≤a few thousand spam rows per attack); cleanup is `DELETE FROM users WHERE google_sub IS NULL AND created_at < NOW() - 7 days AND last_session_at IS NULL`.

## Done = ?

**Phase 1 (additive passkey for SSO users):**

1. Log in via Google as a test user, navigate to Settings → Passkeys, click Add. Touch ID / Windows Hello prompts. Confirm. A new row appears: "iCloud Keychain (synced) · added today · last used never".
2. Sign out. Sign-in screen shows two buttons.
3. Click "Sign in with passkey". Browser prompts for biometric. Pass. Session restored — `pending_totp` flow if TOTP enabled, otherwise fully authenticated.
4. Settings → Passkeys row now shows `last used` = today.
5. Register a second passkey (e.g. a YubiKey or a phone via cross-device QR). Both appear in the list.
6. Revoke the first passkey. Sign out. Try to sign in with it → browser shows the prompt, user authenticates, but the server rejects with 401 ("credential not found"). User picks the second passkey → success.
7. Onboarding checklist correctly shows the "Add passkey" item as completed once ≥1 passkey is registered.
8. Visit the sign-in screen with an old browser (or `chrome://flags` disable WebAuthn). The passkey button is hidden. Google sign-in still works.

**Phase 2 (username-only signup):**

9. Open a fresh browser profile, navigate to the Portal. Sign-in page shows two columns: "With Google" and "With passkey". The passkey column has "No account? Sign up with passkey →".
10. Click the link → `/signup/passkey` form: username + acknowledgement + Turnstile + "Create account."
11. Try `admin` → 400 `username_reserved`. Try `ab` → 400 `username_length`. Try `alice@bob` → 400 `username_chars`.
12. Pick `demo_user`, tick the acknowledgement, solve CAPTCHA, click Create. Biometric prompt. Confirm. Lands on onboarding step "Set a passphrase".
13. Complete onboarding (passphrase + Emergency Kit, skip optional TOTP). Dashboard shows the user as `demo_user`, no email displayed.
14. Sign out. Sign back in via "Sign in with passkey" → browser picker shows the demo_user credential → biometric → signed in.
15. Database check: `SELECT username, email, google_sub FROM users WHERE username = 'demo_user'` returns `('demo_user', NULL, NULL)`.
16. In a second browser profile, try to sign up with username `demo_user` again → 409 `username_taken`.
17. Concurrent signups with the same username (load-test) → exactly one succeeds; others get 409.
