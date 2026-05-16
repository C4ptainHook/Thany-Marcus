# PORTAL-005 Provider-token vault — Handoff Brief

**Goal:** finish PORTAL-005 by landing an `IProviderTokenVault` service that wraps the existing `encrypted_provider_tokens` table — encrypt-on-write with a DEK supplied by the caller (held in `IInfraOpUnlockCache`), decrypt-only-when-needed, zero plaintext at rest, zero plaintext in DbContext change tracking. Plus the two endpoints that users hit to register and rotate a provider token (DigitalOcean / Azure / Cloudflare API tokens). The passphrase envelope half of PORTAL-005 (KEK derivation + DEK wrap/unwrap) is already shipped in `PassphraseService`; this handoff is the *consumer* of that envelope.

Estimated **0.5 person-day** with heavy AI-agent assistance.

## Where decisions live (read before doing anything)

- **`docs/decisions/0030-auth-flow.md` §"Step-up sequence"** — the canonical sequence: handler `IInfraOpUnlockCache.TryGet → dek`; `AesGcm.Decrypt(provider_token_ciphertext, ...)`; use the plaintext; drop it. This handoff lands the `IProviderTokenVault` that fronts the decrypt step.
- **`docs/decisions/0029-type-mappings.md` §"three-column AES-GCM"** — `encrypted_provider_tokens` table has `ciphertext`/`nonce`/`tag` columns. Same shape per-provider per-user, keyed by `UNIQUE (user_id, provider)`.
- **`docs/decisions/0028-schema-conventions.md`** — naming + `IClock` + interceptor conventions.
- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — `encrypted_provider_tokens.user_id` is CASCADE per PORTAL-002.
- **`plans/portal-003f-handoff.md`** — landed first; `IInfraOpUnlockCache` is now async and Postgres-backed.
- **Existing code**: `Features/Auth/StepUp/PassphraseService.cs` (the *KEK + DEK envelope*; not directly called by this ticket — the vault uses the *DEK* held in cache to wrap *provider tokens*), `Features/Auth/EncryptedProviderToken.cs` (entity, already in `PortalDbContext`).

**Do not re-implement crypto primitives.** `AesGcm` from `System.Security.Cryptography` is the right primitive; same one `PassphraseService` uses for KEK→DEK wrap. The vault is ~80 LOC; resist over-engineering.

## Scope boundary (precise)

**In scope:**
- `IProviderTokenVault` interface + implementation
- `Features/CloudManagement/ProviderTokens/` directory: vault, endpoints, request/response records
- Endpoints:
  - `POST /api/clouds/provider-tokens` — register or replace a token for `(user_id, provider)`. Requires step-up.
  - `DELETE /api/clouds/provider-tokens/{provider}` — remove a token. Requires step-up.
  - `GET /api/clouds/provider-tokens` — list providers user has tokens for (just metadata: provider name, created_at, last_used_at — NEVER the plaintext or ciphertext).
- DI registration in `Program.cs`
- Unit + integration tests proving round-trip + rejection paths

**Out of scope (DO NOT touch):**
- **Provider token validation against the actual provider API** (e.g., "is this DO token still valid?"). Live validation is PORTAL-014's territory or arrives implicitly when PORTAL-007's `terraform plan` runs.
- **Provider token rotation reminders / expiry tracking**. Tokens are opaque blobs from our POV; we don't decode expiry.
- **The provider-token UI in SvelteKit** — PORTAL-013's territory (which currently lives in `tickets-2026-05-13.md` as "Plugin-token issuance + revocation UI" but the same UI shape applies to provider tokens; the actual SvelteKit work for provider tokens isn't ticketed yet — flag for follow-up if you need a UI handoff).
- **Recovery code re-key after passphrase change**: out of scope; PORTAL-006 deals with that envelope.
- **TerraformRunner usage of the vault**: PORTAL-007 reads from this vault via `IProviderTokenVault.DecryptAsync(userId, provider, dek)`. The vault is *shaped* for that consumer in this handoff but the consumer doesn't land until PORTAL-007.

## Output of PORTAL-005 — final directory state

```
Thany-Marcus/
├── src/ThanyMarcus.Portal.Api/
│   ├── Program.cs                                                # adds DI registration + MapProviderTokenEndpoints
│   └── Features/CloudManagement/
│       └── ProviderTokens/                                       # NEW directory
│           ├── IProviderTokenVault.cs                            # interface
│           ├── ProviderTokenVault.cs                             # implementation using AesGcm
│           ├── ProviderTokenEndpoints.cs                         # MapProviderTokenEndpoints extension
│           ├── ProviderTokenSummary.cs                           # response record for GET (metadata only)
│           ├── RegisterProviderTokenRequest.cs                   # body for POST
│           ├── ProviderTokenAlreadyExistsException.cs            # thrown by Add; mapped to 409
│           └── KnownProviders.cs                                 # const strings: "digitalocean" | "azure" | "cloudflare"
└── tests/ThanyMarcus.Portal.Tests/
    └── Features/CloudManagement/
        └── ProviderTokens/                                       # NEW directory
            ├── ProviderTokenVaultTests.cs                        # round-trip + wrong-DEK rejection + replace + list + delete
            └── ProviderTokenEndpointsTests.cs                    # endpoint contract (auth, step-up gate, 409s, etc.)
```

## Packages

No new packages. `System.Security.Cryptography` is in the BCL; `Konscious.Security.Cryptography.Argon2` is already pulled in via `PassphraseService`.

## IProviderTokenVault (`Features/CloudManagement/ProviderTokens/IProviderTokenVault.cs`)

```csharp
namespace ThanyMarcus.Portal.Api.Features.CloudManagement.ProviderTokens;

public interface IProviderTokenVault
{
    /// <summary>
    /// Encrypts <paramref name="plaintextToken"/> with the user's DEK and persists.
    /// Throws <see cref="ProviderTokenAlreadyExistsException"/> if a token already exists for
    /// <paramref name="provider"/> (call <see cref="ReplaceAsync"/> instead).
    /// </summary>
    Task AddAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct);

    /// <summary>Encrypt-and-store, replacing any existing token for the user+provider.</summary>
    Task ReplaceAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct);

    /// <summary>
    /// Decrypts and returns the plaintext. Caller is responsible for zeroing the returned buffer.
    /// Returns null if no token exists for the user+provider. Throws <see cref="AuthenticationTagMismatchException"/>
    /// if the supplied DEK is wrong.
    /// </summary>
    Task<byte[]?> DecryptAsync(Guid userId, string provider, ReadOnlyMemory<byte> dek, CancellationToken ct);

    Task RemoveAsync(Guid userId, string provider, CancellationToken ct);

    Task<IReadOnlyList<ProviderTokenSummary>> ListAsync(Guid userId, CancellationToken ct);
}

public sealed record ProviderTokenSummary(string Provider, Instant CreatedAt, Instant UpdatedAt);
```

## ProviderTokenVault (implementation)

`Features/CloudManagement/ProviderTokens/ProviderTokenVault.cs`:

```csharp
public sealed class ProviderTokenVault(PortalDbContext db, IClock clock) : IProviderTokenVault
{
    private const int NonceSize = 12;
    private const int TagSize   = 16;

    public async Task AddAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var existing = await db.EncryptedProviderTokens
            .AnyAsync(t => t.UserId == userId && t.Provider == provider, ct);
        if (existing) throw new ProviderTokenAlreadyExistsException(userId, provider);

        var (ciphertext, nonce, tag) = Encrypt(plaintextToken, dek.Span);
        var now = clock.GetCurrentInstant();

        db.EncryptedProviderTokens.Add(new EncryptedProviderToken
        {
            UserId      = userId,
            Provider    = provider,
            Ciphertext  = ciphertext,
            Nonce       = nonce,
            Tag         = tag,
            CreatedAt   = now,
            UpdatedAt   = now,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task ReplaceAsync(Guid userId, string provider, string plaintextToken, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var (ciphertext, nonce, tag) = Encrypt(plaintextToken, dek.Span);
        var existing = await db.EncryptedProviderTokens
            .SingleOrDefaultAsync(t => t.UserId == userId && t.Provider == provider, ct);
        var now = clock.GetCurrentInstant();

        if (existing is null)
        {
            db.EncryptedProviderTokens.Add(new EncryptedProviderToken
            {
                UserId      = userId,
                Provider    = provider,
                Ciphertext  = ciphertext,
                Nonce       = nonce,
                Tag         = tag,
                CreatedAt   = now,
                UpdatedAt   = now,
            });
        }
        else
        {
            existing.Ciphertext = ciphertext;
            existing.Nonce      = nonce;
            existing.Tag        = tag;
            // UpdatedAt is bumped by TimestampInterceptor via IHasUpdatedAt
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task<byte[]?> DecryptAsync(Guid userId, string provider, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var row = await db.EncryptedProviderTokens
            .AsNoTracking()
            .SingleOrDefaultAsync(t => t.UserId == userId && t.Provider == provider, ct);
        if (row is null) return null;

        var plaintext = new byte[row.Ciphertext.Length];
        using var aes = new AesGcm(dek.Span, tagSizeInBytes: TagSize);
        // throws AuthenticationTagMismatchException on wrong DEK; let it propagate
        aes.Decrypt(row.Nonce, row.Ciphertext, row.Tag, plaintext);
        return plaintext;
    }

    public async Task RemoveAsync(Guid userId, string provider, CancellationToken ct)
    {
        await db.EncryptedProviderTokens
            .Where(t => t.UserId == userId && t.Provider == provider)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<ProviderTokenSummary>> ListAsync(Guid userId, CancellationToken ct)
    {
        return await db.EncryptedProviderTokens
            .AsNoTracking()
            .Where(t => t.UserId == userId)
            .OrderBy(t => t.Provider)
            .Select(t => new ProviderTokenSummary(t.Provider, t.CreatedAt, t.UpdatedAt))
            .ToListAsync(ct);
    }

    private static (byte[] ciphertext, byte[] nonce, byte[] tag) Encrypt(string plaintextToken, ReadOnlySpan<byte> dek)
    {
        var plaintext  = Encoding.UTF8.GetBytes(plaintextToken);
        var nonce      = RandomNumberGenerator.GetBytes(NonceSize);
        var tag        = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        using var aes  = new AesGcm(dek, tagSizeInBytes: TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);
        CryptographicOperations.ZeroMemory(plaintext);   // best-effort; the byte[] may still live in GC heap until collected
        return (ciphertext, nonce, tag);
    }
}
```

## Endpoints

`Features/CloudManagement/ProviderTokens/ProviderTokenEndpoints.cs`:

```csharp
public static class ProviderTokenEndpoints
{
    public static void MapProviderTokenEndpoints(this IEndpointRouteBuilder app)
    {
        var grp = app.MapGroup("/api/clouds/provider-tokens")
            .RequireAuthorization(AuthPolicies.TotpRequired);

        grp.MapGet("", async (
            ClaimsPrincipal user,
            IProviderTokenVault vault,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var list = await vault.ListAsync(userId, ct);
            return Results.Ok(list);
        });

        grp.MapPost("", async (
            RegisterProviderTokenRequest body,
            ClaimsPrincipal user,
            IProviderTokenVault vault,
            IInfraOpUnlockCache cache,
            CancellationToken ct) =>
        {
            if (!KnownProviders.IsValid(body.Provider))
                return Results.BadRequest(new { error = "unknown_provider", supported = KnownProviders.All });
            if (string.IsNullOrWhiteSpace(body.Token))
                return Results.BadRequest(new { error = "token_required" });

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var dek = new byte[32];
            try
            {
                var unlocked = await cache.TryGetAsync(userId, dek, ct);
                if (!unlocked)
                    return Results.Json(new { error = "step_up_required" }, statusCode: StatusCodes.Status401Unauthorized);

                if (body.Replace)
                    await vault.ReplaceAsync(userId, body.Provider, body.Token, dek, ct);
                else
                {
                    try { await vault.AddAsync(userId, body.Provider, body.Token, dek, ct); }
                    catch (ProviderTokenAlreadyExistsException)
                    {
                        return Results.Conflict(new { error = "provider_token_already_set", provider = body.Provider });
                    }
                }
                return Results.NoContent();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        })
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();

        grp.MapDelete("{provider}", async (
            string provider,
            ClaimsPrincipal user,
            IProviderTokenVault vault,
            CancellationToken ct) =>
        {
            if (!KnownProviders.IsValid(provider))
                return Results.BadRequest(new { error = "unknown_provider" });

            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            await vault.RemoveAsync(userId, provider, ct);
            return Results.NoContent();
        })
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}

public sealed record RegisterProviderTokenRequest(string Provider, string Token, bool Replace = false);
```

`Features/CloudManagement/ProviderTokens/KnownProviders.cs`:

```csharp
public static class KnownProviders
{
    public const string DigitalOcean = "digitalocean";
    public const string Azure        = "azure";
    public const string Cloudflare   = "cloudflare";

    public static readonly IReadOnlyList<string> All = [DigitalOcean, Azure, Cloudflare];

    public static bool IsValid(string provider) => All.Contains(provider, StringComparer.Ordinal);
}
```

`Features/CloudManagement/ProviderTokens/ProviderTokenAlreadyExistsException.cs`:

```csharp
public sealed class ProviderTokenAlreadyExistsException(Guid userId, string provider)
    : InvalidOperationException($"Provider token already exists for user {userId} and provider '{provider}'")
{
    public Guid   UserId   { get; } = userId;
    public string Provider { get; } = provider;
}
```

## `Program.cs` registration

Add (anywhere with the other auth/feature scoped registrations, e.g., after the `IInfraOpUnlockCache` block):

```csharp
builder.Services.AddScoped<IProviderTokenVault, ProviderTokenVault>();
```

And in the pipeline section, after `app.MapPassphraseEndpoints();`:

```csharp
app.MapProviderTokenEndpoints();
```

## Acceptance criteria

- `dotnet build` succeeds with **zero warnings**.
- `dotnet run --project tests/ThanyMarcus.Portal.Tests` passes; **all existing tests still green**.
- `ProviderTokenVaultTests` covers:
  - (a) `AddAsync` round-trip: encrypt → row in DB has non-empty ciphertext/nonce/tag → `DecryptAsync` with the same DEK returns the original plaintext.
  - (b) `AddAsync` with an existing token for the same `(user_id, provider)` throws `ProviderTokenAlreadyExistsException`.
  - (c) `ReplaceAsync` with no existing row creates one; with an existing row updates ciphertext/nonce/tag and bumps `UpdatedAt`.
  - (d) `DecryptAsync` with no row returns `null`.
  - (e) `DecryptAsync` with a *wrong DEK* throws `AuthenticationTagMismatchException`.
  - (f) `RemoveAsync` removes the row; subsequent `DecryptAsync` returns `null`.
  - (g) `ListAsync` returns `ProviderTokenSummary` entries with **no ciphertext leak** in the response (verify via type — `ProviderTokenSummary` doesn't have crypto fields, so this is type-system-enforced).
  - (h) Distinct nonces: encrypting the same token twice produces different `nonce` and `ciphertext` (proves we generate fresh nonces per encrypt).
- `ProviderTokenEndpointsTests` covers:
  - (a) `POST /api/clouds/provider-tokens` without a cookie → 401.
  - (b) `POST` with cookie but no step-up unlock → 401 with `step_up_required` error.
  - (c) `POST` with step-up unlock returns 204 and the row exists in `encrypted_provider_tokens`.
  - (d) `POST` again for the same provider returns 409 `provider_token_already_set`.
  - (e) `POST` with `replace: true` returns 204 and the row's ciphertext changes.
  - (f) `POST` with an unknown provider → 400 `unknown_provider`.
  - (g) `POST` with empty token → 400 `token_required`.
  - (h) `GET` lists providers without exposing the ciphertext.
  - (i) `DELETE /api/clouds/provider-tokens/{provider}` removes the row; subsequent `GET` doesn't list it.
- Integration test using `TestAuthHandler` (from PORTAL-003 infra) to bake an authenticated principal + a manual `IInfraOpUnlockCache.SetAsync` to simulate step-up.
- The plaintext token NEVER appears in `provisioning_jobs.events_log`, audit logs, or anywhere in the database. (Implicit from design; assert in `ProviderTokenVaultTests` by querying the raw bytea column and confirming it doesn't decode to the input plaintext under UTF-8.)
- `psql portal_dev -c "SELECT provider, length(ciphertext), length(nonce), length(tag) FROM encrypted_provider_tokens"` shows: `nonce` is 12 bytes, `tag` is 16 bytes, `ciphertext` matches the input plaintext UTF-8 byte length.

## Concrete steps in order

1. **Create the `ProviderTokens/` directory** with the small constant + record files first: `KnownProviders.cs`, `ProviderTokenSummary.cs`, `RegisterProviderTokenRequest.cs`, `ProviderTokenAlreadyExistsException.cs`. Build.

2. **Write `ProviderTokenVaultTests`** — TDD. Use `DbIntegrationTestBase` for the real Postgres + FakeClock. Each test inserts/queries via `Db.EncryptedProviderTokens`. Run — tests fail (no impl yet).

3. **Implement `IProviderTokenVault` + `ProviderTokenVault`** per the sketches. Run tests — passing.

4. **Register the vault in `Program.cs`** — single line. Build still green.

5. **Write `ProviderTokenEndpointsTests`** — extend `PortalApiFactory` usage with `WithTestAuth(userId, totp: "verified")` and pre-seed `IInfraOpUnlockCache` for the step-up scenarios. Run — tests fail (endpoints don't exist).

6. **Implement `ProviderTokenEndpoints`** + register `app.MapProviderTokenEndpoints()` in `Program.cs`. Run tests — passing.

7. **Manual smoke**: `dotnet run --project src/ThanyMarcus.Portal.Api`. Sign in via Google. `POST /api/auth/passphrase/init { passphrase: "test123!" }`. `POST /api/auth/unlock { passphrase: "test123!" }`. `POST /api/clouds/provider-tokens { provider: "digitalocean", token: "dop_v1_xxx" }`. `GET /api/clouds/provider-tokens`. Verify the row in `psql` shows ciphertext, not plaintext.

8. **Verify all acceptance criteria** — every bullet.

## Out of scope (do not touch)

- **`encrypted_provider_tokens` schema changes**: schema is already correct per PORTAL-002. No migration in this ticket.
- **Provider-token validation against real provider APIs**: PORTAL-014 territory.
- **UI for provider-token management**: not ticketed; follow-up. The endpoints work via `curl` for now.
- **Recovery-code redemption affecting provider tokens**: PORTAL-006's envelope re-keys recovery codes, not provider tokens. Provider tokens stay encrypted under the same DEK.
- **Per-cloud-scoped provider tokens** (e.g., "DO token for cloud X is different from token for cloud Y"): out of scope. Current model is per-user, not per-cloud. If a user wants two different DO accounts for two clouds, that's a future feature (`encrypted_provider_tokens` would gain a `cloud_id` column nullable).
- **Encrypting other secrets via this vault** (e.g., webhook secrets): not in scope; the vault is provider-tokens-specific.

## Risks & gotchas

- **DEK lifetime in `DecryptAsync`'s return path.** `DecryptAsync` returns a `byte[]` plaintext that the *caller* must zero. PORTAL-007 will be the consumer; document the caller responsibility clearly in the XML doc. Don't pool/cache the plaintext.

- **`ReadOnlyMemory<byte> dek` parameter.** Span can't cross await, so the interface takes `ReadOnlyMemory<byte>`. Callers pass `dek.AsMemory()` if they have a `byte[]`. **Don't** try to switch to `byte[]` to "simplify" — `ReadOnlyMemory` keeps the contract explicit that the vault doesn't take ownership.

- **`AuthenticationTagMismatchException` is the wrong-DEK signal.** Don't catch it inside `DecryptAsync`; let it propagate so the caller can decide (e.g., the wizard might map to "your DEK is corrupt; re-unlock" or similar). The endpoint layer doesn't currently catch it because in normal flow the DEK is fresh from `IInfraOpUnlockCache` and matches.

- **`AsNoTracking()` for the decrypt read.** The vault doesn't mutate on decrypt. Tracking would slow the per-request DbContext for nothing.

- **Nonce uniqueness.** AES-GCM with a reused nonce + same key is catastrophic (key recovery). `RandomNumberGenerator.GetBytes(12)` gives 96 bits of entropy — at our volume (~10s of writes per user lifetime), collision probability is ~0. **Never** reuse a nonce across writes; the impl always generates fresh per encrypt — don't "optimize" this away.

- **`Encoding.UTF8.GetBytes(plaintextToken)` zeroing.** The `plaintext` byte array in `Encrypt` is best-effort-zeroed at the end of the method. The .NET runtime *may* copy the array around the GC heap before then; treat this as defense-in-depth, not a strong guarantee. The bigger guarantee is that plaintext is never written to disk or DB.

- **`ProviderTokenAlreadyExistsException` is application-level, not framework.** Don't expose it via 500 + stack trace. The endpoint sketches catch + map to 409. Make sure global exception handling (if any is configured) doesn't unwrap and 500 it.

- **`KnownProviders` is centrally listed.** If you add Azure later by accident (it's already there) or a fourth provider, update the const list AND any places that switch on provider name (`ProviderTokenEndpoints`'s validation). Probably warrant a small Roslyn analyzer in a follow-up; for now, manual discipline.

- **Step-up gate via `RequireInfraOpUnlockFilter`.** The filter already exists per PORTAL-003b — it short-circuits with 401 `step_up_required` if `IInfraOpUnlockCache.TryGetAsync` returns false. Adding the filter to the endpoints means the explicit `cache.TryGetAsync` call inside the handler can be **removed** — but the handler still needs the DEK for the encrypt op. Two patterns:
  - (a) Filter only gates; handler re-calls `cache.TryGetAsync` to get the DEK.
  - (b) Filter populates `HttpContext.Items["dek"]` with the DEK (less clean — DEK in request items has lifetime risk).
  - The sketch above uses (a). Resist (b).

- **`ExecuteDeleteAsync` for `RemoveAsync`.** EF Core 7+ has this; we're on 10, so OK. Avoids load-then-delete. Don't replace with `db.Remove` + `SaveChanges`.

- **`Cloud.user_id` vs. `EncryptedProviderToken.user_id`.** Both reference `users`. They're independent — a user can have provider tokens before they have any clouds, and vice versa. No FK between `encrypted_provider_tokens` and `clouds`.

- **Concurrent `Add` for same `(user_id, provider)`.** Two simultaneous `POST` requests with `replace: false` would both pass the `AnyAsync` check then both `Add`, and one would fail at SaveChanges with a `DbUpdateException` due to the UNIQUE constraint. The endpoint maps `DbUpdateException` → 409 anyway (already does for other unique conflicts via standard ASP.NET Core behavior; if not, wrap explicitly). Document but don't engineer around — concurrent token registration by the same user is not a real scenario.

- **Tests using `Encoding.UTF8.GetBytes` for DEK.** Use `RandomNumberGenerator.GetBytes(32)` to produce realistic DEKs in tests. A predictable DEK from a string lets tests reproduce wrong-DEK scenarios with `wrongDek = "different-32-bytes...".ToBytes()`, but the round-trip case should use a random DEK.

- **`IInfraOpUnlockCache.SetAsync(userId, dek, ct)`** is the test-side seed for step-up unlock. The cache is Postgres-backed per PORTAL-003f, so seeding for tests means writing to `step_up_unlocks` table directly OR calling `cache.SetAsync` — the second is preferred (uses the public surface).

## Definition of done

All acceptance criteria pass + `git status` shows the new files + a green test run + a manual `curl` smoke session against the running API proving end-to-end (sign in → passphrase init → unlock → token register → list → decrypt-on-PORTAL-007's-behalf will work).

A fresh agent picking up PORTAL-007 (saga implementation) from this state knows:
- `IProviderTokenVault.DecryptAsync(userId, provider, dek, ct)` returns plaintext or null; caller zeros the buffer.
- The vault is scoped DI, resolves from `PortalDbContext`, doesn't require any other state to call.
- Provider tokens are stored encrypted with the per-user DEK; saga workers retrieve the DEK from `IInfraOpUnlockCache` (Postgres-backed per PORTAL-003f) which the user has populated via `/api/auth/unlock` before the infra op.
- The plaintext lifecycle inside the saga handler is: `TryGetAsync → vault.DecryptAsync → use → ZeroMemory → drop`.

## Cross-references

- **PORTAL-003f** — prerequisite (Postgres-backed `IInfraOpUnlockCache`). Without it, the endpoint flow works but the consumer (PORTAL-007 worker) can't reach into the cache.
- **PORTAL-007** — direct consumer. The saga's `tf_planning` and `tf_applying` phases call `vault.DecryptAsync` to materialize the provider token, pass via `TF_VAR_*` to terraform, zero the buffer.
- **PORTAL-013** — UI for provider-token management (and plugin tokens; same UX pattern). Currently ticketed as plugin-token UI; provider-token UI is implicit follow-up.
- **ADR-0029** §"three-column AES-GCM" — the schema shape this vault produces.
- **ADR-0030** §"Step-up sequence" — the integrated flow this vault participates in.
