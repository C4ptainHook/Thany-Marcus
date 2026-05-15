# PORTAL-003b Step-up auth + `IInfraOpUnlockCache` — Handoff Brief

**Goal:** land the passphrase-step-up half of the auth foundation. Three deliverables: (a) `POST /api/auth/passphrase/init` — first-time passphrase setup: generate Argon2id salt + params, derive KEK, mint a 32-byte DEK, AES-GCM-wrap the DEK with KEK, persist `passphrase_argon2_salt` / `passphrase_argon2_params` / `passphrase_wrapped_dek` / `passphrase_wrap_nonce` / `passphrase_wrap_tag` on the `users` row; (b) `POST /api/auth/unlock` — verify a passphrase by deriving KEK and unwrapping the stored DEK, then cache the DEK in the singleton `IInfraOpUnlockCache` with a 10-minute sliding TTL; (c) the cache itself plus an `IEndpointFilter` / authorization mechanism that protected endpoints can apply to require step-up state, returning `401 { error: "step_up_required" }` when the cache misses. **No rate limiting on `/unlock` (003c), no `auth_lockouts` writes on failure (003d), no Turnstile (003e), no per-provider-token encryption (PORTAL-005), no passphrase rotation / change (PORTAL-005).**

Estimated **0.75 person-day** — the crypto is straightforward (Argon2id derive → AES-GCM wrap/unwrap is ~60 LOC), but the cache lifetime hooks (logout, `OnValidatePrincipal` rejection, sliding-window bookkeeping) and the SvelteKit fetch wrapper that turns a 401 `step_up_required` into a modal-then-retry are where the time goes.

## Where decisions live (read before doing anything)

- **`docs/decisions/0030-auth-flow.md`** §"Step-up sequence (infra op)" + §"Schema impact" — the contract for this ticket. Specifically: KEK = `Argon2id(passphrase, user.passphrase_argon2_salt, user.passphrase_argon2_params)`; DEK = `AesGcm.Decrypt(passphrase_wrapped_dek, passphrase_wrap_nonce, passphrase_wrap_tag, KEK)`; `IInfraOpUnlockCache.Set(userId, dek, ttl=10min)`; each successful `TryGet` bumps expiration forward by 10 min; invalidated on logout, recovery-code redemption, passphrase change, `sessions_invalidated_at` bump, process restart. The 401 shape is `{ error: "step_up_required" }`.
- **`docs/decisions/0029-type-mappings.md`** §"AES-GCM ciphertext layout" — three columns per AES-GCM ciphertext (`*_ciphertext` / `*_nonce` / `*_tag`). On `users`, the columns are `passphrase_wrapped_dek` / `passphrase_wrap_nonce` / `passphrase_wrap_tag` — note the `wrap_` prefix variant (the DEK is *wrapped* by KEK, not "encrypted" in the plaintext sense). Nonce = 12 bytes, tag = 16 bytes, DEK = 32 bytes (AES-256-GCM). `passphrase_argon2_params` is `jsonb` so we can ship a rotation later without a column add.
- **`docs/decisions/0031-rate-limiting-and-lockout.md`** §"Ticket split" — PORTAL-003b is *only* the step-up cache + the passphrase setup + the `/unlock` endpoint. Rate limit on `/unlock` (003c), `auth_lockouts.kind = 'unlock'` writes (003d), Turnstile on repeated failures (003e). Failures here return `401 { error: "invalid_passphrase" }`; the *escalation* is added by 003c/d.
- **`docs/decisions/0023-test-stack.md`** — `WithTestAuth(...)` bakes the cookie. The step-up cache is a singleton in the test factory; tests can call `factory.Services.GetRequiredService<IInfraOpUnlockCache>()` to assert state directly, or POST to `/unlock` and assert via a protected stub endpoint. Prefer the latter — tests the contract, not the implementation.
- **`plans/portal-003-handoff.md`** §`CookiePrincipalValidator` — the `OnValidatePrincipal` pipeline. When the validator rejects a cookie (sessions-invalidated bump), this ticket adds `Cache.Invalidate(userId)` to the rejection path. `/api/auth/signout` is also already in place; this ticket extends it with one `Cache.Invalidate(userId)` call before the cookie sign-out.
- **`plans/portal-003a-handoff.md`** §"TotpBackupCodeService" — the Argon2id PHC pattern for backup codes (and recovery codes in PORTAL-006). **Do not consume that pattern here.** Argon2id is used in 003b for *key derivation* (input → 32-byte KEK), not for *password verification* (input → hash → constant-time compare). Different API call (`GetBytes(32)` not `Verify`), different params (interactive vs. one-shot), different consumer. The eventual `Features/Auth/Argon2Hashing.cs` extraction that 003a/005/006 will negotiate is for the *verification* call sites; 003b stays out of that conversation.
- **Memory files**: `portal_architecture.md` (Minimal APIs + VSA — these endpoints go under `Features/Auth/`), `portal_tooling.md` (warnings-as-errors, hand-written TS types), `feedback_no_code_comments.md` (no narrative comments in source).

**Do not re-litigate the step-up model.** ADR-0030 §"4. Step-up auth (passphrase for infra ops)" and §"Step-up sequence (infra op)" closed: per-user unlock (not per-cloud), 10-minute sliding window (not fixed), in-process singleton cache (not Redis), passphrase prompt on cache miss (not signed re-auth). If a question seems open (e.g., "should the cache be backed by a distributed store?"), ADR-0030 §"Negative consequences" says no — single-VM deployment per [[portal_deployment]] makes the in-process cache the right shape, and multi-replica is explicitly a future-revisit.

## Scope boundary (precise)

**In scope:**
- `Konscious.Security.Cryptography.Argon2` (already added by PORTAL-003a; no new package) for KEK derivation. Different param profile from backup-code hashing — see "Argon2id params" below.
- `System.Security.Cryptography.AesGcm` (BCL — no package add) for wrapping / unwrapping the DEK with KEK.
- `Features/Auth/StepUp/` slice: `PassphraseService` (derive KEK, generate DEK, wrap/unwrap), `IInfraOpUnlockCache` + `InProcessInfraOpUnlockCache`, `PassphraseEndpoints` (registers the 2 endpoints), `RequireInfraOpUnlockFilter` (the `IEndpointFilter` that gates protected endpoints), `PassphraseInitRequest` / `PassphraseUnlockRequest` records, `Argon2Params` record (the shape stored in `passphrase_argon2_params` jsonb).
- `POST /api/auth/passphrase/init` — body `{ passphrase }`. 409 if already set. Generates 16-byte salt, derives KEK from passphrase + salt + default params, mints random 32-byte DEK, AES-GCM-wraps DEK with KEK (random 12-byte nonce, 16-byte tag), persists all five columns + `passphrase_set_at` timestamp (already present per ADR-0030 schema). Does **not** seed the cache — first /unlock is what populates it.
- `POST /api/auth/unlock` — body `{ passphrase }`. Re-derives KEK from passphrase + stored salt + stored params; calls `AesGcm.Decrypt`; on `AuthenticationTagMismatchException` returns `401 { error: "invalid_passphrase" }`; on success calls `Cache.Set(userId, dek, ttl=10min)` and returns 204.
- `IInfraOpUnlockCache` interface + `InProcessInfraOpUnlockCache` singleton. `ConcurrentDictionary<Guid, CacheEntry>` where `CacheEntry` is `(byte[] Dek, Instant ExpiresAt)`. `TryGet` bumps `ExpiresAt = clock.GetCurrentInstant() + Duration.FromMinutes(10)` on hit. `Set` overwrites and zeros the previous entry's DEK buffer. `Invalidate` zeros and removes. A `IHostedService` sweep job runs every 5 minutes, zeros + removes expired entries (defensive; the cache is bounded by user count anyway).
- `RequireInfraOpUnlockFilter` — `IEndpointFilter` that resolves `IInfraOpUnlockCache` + `ClaimsPrincipal`, calls `TryGet`, returns `Results.Json(new { error = "step_up_required" }, statusCode: 401)` on miss. Adds nothing to the endpoint when present (the consumer reads the DEK by re-resolving the cache themselves; the filter is a *gate*, not a *DI source*, because passing `byte[]` through endpoint filter state is awkward and the cache hit is cheap).
- Wire `Cache.Invalidate(userId)` into:
  - `/api/auth/signout` (PORTAL-003 endpoint — one-line addition before `SignOutAsync`),
  - `CookiePrincipalValidator.ValidateAsync` (PORTAL-003 — when rejecting due to `sessions_invalidated_at`, invalidate the cache before `ctx.RejectPrincipal()`).
- SvelteKit:
  - `lib/stepUpClient.ts` — `fetchWithStepUp(url, init)`: wraps `fetch`, on `401 { error: "step_up_required" }` opens the passphrase modal, posts to `/unlock`, retries the original request. The modal is a small Svelte store + a global component mounted in `+layout.svelte`.
  - `/settings/security/+page.svelte` — extends the page from PORTAL-003a with a "Set passphrase" section (gated on `me.passphraseSet === false`, which `/api/auth/me` already returns per PORTAL-003).
- Integration tests proving the ten acceptance criteria below.

**Out of scope (do not touch — each has its own ticket):**
- Per-provider-token AES-GCM encryption using the DEK that this ticket caches — PORTAL-005. The cache produces a DEK; what consumers do with it (decrypt `encrypted_provider_tokens` rows) belongs to 005 and onward.
- Passphrase **change** / **rotation** endpoint — PORTAL-005. Rotation has to re-wrap every encrypted blob keyed under the old DEK, so it's bundled with the encryption work, not the auth work.
- Recovery-code redemption (`/api/auth/recovery-codes/redeem`) — PORTAL-006. Recovery codes restore passphrase access by re-issuing a new DEK; that flow needs this ticket's `PassphraseService` (specifically the wrap-DEK-with-KEK call), but the redemption endpoint itself is 006.
- `Microsoft.AspNetCore.RateLimiting` on `/unlock` — PORTAL-003c.
- `auth_lockouts.kind = 'unlock'` writes on failed `/unlock` — PORTAL-003d.
- Cloudflare Turnstile on `/unlock` — PORTAL-003e.
- Argon2id params *rotation* (re-deriving with new params on next successful login) — out of scope; the jsonb column shape supports it but the logic lands when we ship a params bump, not now.

If a follow-up needs to extend this (e.g., 003d needs to wire `auth_lockouts.kind = 'unlock'` into the failure path), the seam is the `UnlockResult` enum returned by `PassphraseService.TryUnwrapDekAsync` — 003d hooks `Failed` to write the lockout row. PORTAL-003b lands the enum and the failure return; nothing more.

## Output of PORTAL-003b — final directory state

```
Thany-Marcus/
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── Program.cs                                 # registers PassphraseService, InProcessInfraOpUnlockCache (singleton),
│   │   │                                              #   InfraOpUnlockSweepService (hosted), MapPassphraseEndpoints
│   │   └── Features/
│   │       └── Auth/
│   │           ├── StepUp/                            # NEW subfolder
│   │           │   ├── PassphraseService.cs           # KEK derive, DEK generate, AES-GCM wrap/unwrap
│   │           │   ├── IInfraOpUnlockCache.cs         # interface — TryGet, Set, Invalidate
│   │           │   ├── InProcessInfraOpUnlockCache.cs # ConcurrentDictionary; sliding TTL; zeroes on eviction
│   │           │   ├── InfraOpUnlockSweepService.cs   # BackgroundService; sweeps expired entries every 5 min
│   │           │   ├── RequireInfraOpUnlockFilter.cs  # IEndpointFilter → 401 step_up_required on cache miss
│   │           │   ├── PassphraseEndpoints.cs         # MapPassphraseEndpoints — /api/auth/passphrase/init, /api/auth/unlock
│   │           │   ├── PassphraseInitRequest.cs       # record { Passphrase: string }
│   │           │   ├── PassphraseUnlockRequest.cs     # record { Passphrase: string }
│   │           │   ├── Argon2Params.cs                # record { MemoryKiB: int, Iterations: int, Parallelism: int }
│   │           │   └── UnlockResult.cs                # enum { Unlocked, Failed }  (003d hook point)
│   │           ├── AuthEndpoints.cs                   # CHANGED: /api/auth/signout now calls Cache.Invalidate(userId)
│   │           └── CookiePrincipalValidator.cs        # CHANGED: on sessions-invalidated rejection, call Cache.Invalidate(userId)
│   └── ThanyMarcus.Portal.Web/
│       ├── src/routes/
│       │   ├── settings/security/+page.svelte        # CHANGED: adds "Set passphrase" section
│       │   └── +layout.svelte                        # CHANGED: mounts <StepUpModal />
│       └── src/lib/
│           ├── stepUpClient.ts                        # NEW: fetchWithStepUp(url, init) — 401 step_up_required → modal → retry
│           └── StepUpModal.svelte                     # NEW: passphrase prompt UI bound to a writable store
└── tests/
    └── ThanyMarcus.Portal.Tests/
        └── Features/
            └── Auth/
                └── StepUp/                            # NEW subfolder
                    ├── PassphraseServiceTests.cs                # unit: derive→wrap→unwrap round-trip; wrong passphrase fails decrypt
                    ├── InProcessInfraOpUnlockCacheTests.cs      # unit: Set+TryGet; sliding TTL bumps; expired returns false; Invalidate zeros buffer
                    ├── PassphraseEndpointsTests.cs              # integration: init→unlock→protected endpoint succeeds; init twice→409; unlock wrong→401
                    ├── RequireInfraOpUnlockFilterTests.cs       # integration: protected stub returns 401 step_up_required without cache; 200 after /unlock
                    └── StepUpInvalidationTests.cs               # integration: /signout invalidates; sessions_invalidated_at bump invalidates
```

## Program.cs wiring (additions only)

Add **after** the existing TOTP-services block from PORTAL-003a:

```csharp
builder.Services.AddScoped<PassphraseService>();
builder.Services.AddSingleton<IInfraOpUnlockCache, InProcessInfraOpUnlockCache>();
builder.Services.AddHostedService<InfraOpUnlockSweepService>();
```

Add **after** `app.MapTotpEndpoints();`:

```csharp
app.MapPassphraseEndpoints();
```

No new packages, no new auth schemes, no policy additions — `RequireInfraOpUnlockFilter` is attached per-endpoint by future ticket consumers (PORTAL-005, PORTAL-011, PORTAL-015), not via a global `AddAuthorization` policy.

## PassphraseService (`Features/Auth/StepUp/PassphraseService.cs`)

```csharp
public sealed class PassphraseService(PortalDbContext db, IClock clock)
{
    public static readonly Argon2Params DefaultParams = new(MemoryKiB: 47104, Iterations: 2, Parallelism: 1);

    public async Task<bool> InitAsync(Guid userId, string passphrase, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.PassphraseWrappedDek is { Length: > 0 }) return false;

        var salt   = RandomNumberGenerator.GetBytes(16);
        var dek    = RandomNumberGenerator.GetBytes(32);
        var nonce  = RandomNumberGenerator.GetBytes(12);
        var tag    = new byte[16];
        var cipher = new byte[dek.Length];

        var kek = DeriveKek(passphrase, salt, DefaultParams);
        try
        {
            using var aes = new AesGcm(kek, tagSizeInBytes: 16);
            aes.Encrypt(nonce, dek, cipher, tag);
        }
        finally { CryptographicOperations.ZeroMemory(kek); CryptographicOperations.ZeroMemory(dek); }

        user.PassphraseArgon2Salt   = salt;
        user.PassphraseArgon2Params = JsonSerializer.SerializeToDocument(DefaultParams);
        user.PassphraseWrappedDek   = cipher;
        user.PassphraseWrapNonce    = nonce;
        user.PassphraseWrapTag      = tag;
        user.PassphraseSetAt        = clock.GetCurrentInstant();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<UnlockResult> TryUnwrapDekAsync(Guid userId, string passphrase, byte[] dekBuffer, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(u => u.Id == userId, ct);
        if (user.PassphraseWrappedDek is null or { Length: 0 }) return UnlockResult.Failed;

        var paramsObj = user.PassphraseArgon2Params!.Deserialize<Argon2Params>()!;
        var kek = DeriveKek(passphrase, user.PassphraseArgon2Salt!, paramsObj);
        try
        {
            using var aes = new AesGcm(kek, tagSizeInBytes: 16);
            aes.Decrypt(user.PassphraseWrapNonce!, user.PassphraseWrappedDek!, user.PassphraseWrapTag!, dekBuffer);
            return UnlockResult.Unlocked;
        }
        catch (AuthenticationTagMismatchException)
        {
            return UnlockResult.Failed;
        }
        finally { CryptographicOperations.ZeroMemory(kek); }
    }

    private static byte[] DeriveKek(string passphrase, byte[] salt, Argon2Params p)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt                = salt,
            MemorySize          = p.MemoryKiB,
            Iterations          = p.Iterations,
            DegreeOfParallelism = p.Parallelism,
        };
        return argon.GetBytes(32);
    }
}
```

Three judgment calls embedded above:

- **Argon2id params** — OWASP 2024 recommends, for *interactive* login, `m = 47104 KiB (~46 MiB), t = 2, p = 1` (≈500 ms per derive on a modern x86 core). This is intentionally heavier than PORTAL-003a's backup-code hashing params (`m = 19456, t = 2, p = 2`): backup codes are 40-bit random and are throttled by the iterating verify loop, so we pick params that minimize total verify time. Passphrases are arbitrary entropy and may be weak — the slow derive is the only thing keeping a weak passphrase from falling in seconds. The cost ceiling is "one /unlock per 10 min per user" + the 003c rate limit, so ~500 ms is fine. Store the params in `passphrase_argon2_params` so we can ratchet later without a migration.
- **Caller-owned `dekBuffer`** — `TryUnwrapDekAsync` writes into a buffer the caller allocated (32 bytes). This avoids `byte[]` allocations escaping into closures and lets the caller `ZeroMemory(dekBuffer)` once it's handed to the cache. Returning `byte[]` from the service would force a copy when the cache stores it; this shape passes the buffer through.
- **`AuthenticationTagMismatchException` is the only "wrong passphrase" signal.** A wrong passphrase derives a wrong KEK, and AES-GCM verification fails on the tag. Catching just this exception means a corrupted ciphertext (DB tampering, bytes flipped) also surfaces as 401 — which is correct: the caller can't tell those apart without a separate integrity column, and the failure mode is the same.

## InProcessInfraOpUnlockCache (`Features/Auth/StepUp/InProcessInfraOpUnlockCache.cs`)

```csharp
public interface IInfraOpUnlockCache
{
    bool TryGet(Guid userId, Span<byte> dekDestination);
    void Set(Guid userId, ReadOnlySpan<byte> dek);
    void Invalidate(Guid userId);
}

public sealed class InProcessInfraOpUnlockCache(IClock clock) : IInfraOpUnlockCache
{
    private static readonly Duration SlidingTtl = Duration.FromMinutes(10);
    private readonly ConcurrentDictionary<Guid, CacheEntry> _entries = new();

    public bool TryGet(Guid userId, Span<byte> dekDestination)
    {
        if (!_entries.TryGetValue(userId, out var entry)) return false;
        var now = clock.GetCurrentInstant();
        if (entry.ExpiresAt <= now)
        {
            Invalidate(userId);
            return false;
        }
        entry.Dek.AsSpan().CopyTo(dekDestination);
        entry.ExpiresAt = now + SlidingTtl;
        return true;
    }

    public void Set(Guid userId, ReadOnlySpan<byte> dek)
    {
        if (_entries.TryRemove(userId, out var prev))
            CryptographicOperations.ZeroMemory(prev.Dek);
        var copy = dek.ToArray();
        _entries[userId] = new CacheEntry { Dek = copy, ExpiresAt = clock.GetCurrentInstant() + SlidingTtl };
    }

    public void Invalidate(Guid userId)
    {
        if (_entries.TryRemove(userId, out var entry))
            CryptographicOperations.ZeroMemory(entry.Dek);
    }

    internal void SweepExpired()
    {
        var now = clock.GetCurrentInstant();
        foreach (var (id, entry) in _entries)
            if (entry.ExpiresAt <= now) Invalidate(id);
    }

    private sealed class CacheEntry
    {
        public required byte[] Dek { get; init; }
        public Instant ExpiresAt { get; set; }
    }
}
```

Three subtleties:

- **`CacheEntry.ExpiresAt` is mutated under a hashtable lookup.** Concurrent `TryGet`s can race to write `ExpiresAt`; both write a value within 1 µs of each other, and the last writer wins. That's fine — the worst case is the TTL slides forward by an extra few microseconds. No lock needed. The `_entries` dictionary itself is `ConcurrentDictionary`, so the add/remove paths are race-safe.
- **`ZeroMemory` on eviction is best-effort.** .NET's GC may have moved the array elsewhere, leaving stale copies; the JIT may have spilled values to registers/stack; the OS may have paged the array to disk. We do it because it removes the most-likely path (still-live array in heap), not because it's an absolute scrub. The alternative — pinning + `MemoryProtect` + locked-page allocations — is overkill for a thesis prototype. ADR-0030 §"Negative" accepts this.
- **Sliding TTL on `TryGet`** matches ADR-0030 step 10 ("each successful TryGet bumps expires forward by 10 min"). Compare with a fixed TTL: an active session re-prompts every 10 min regardless of activity, which is the worse UX. The sliding behavior is intentional.

## InfraOpUnlockSweepService

```csharp
public sealed class InfraOpUnlockSweepService(InProcessInfraOpUnlockCache cache) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Period);
        while (await timer.WaitForNextTickAsync(ct))
            cache.SweepExpired();
    }
}
```

Inject the concrete `InProcessInfraOpUnlockCache` (not the interface) because `SweepExpired` is `internal` and not part of the public contract. Register the concrete type as a singleton and have the interface registration resolve to it:

```csharp
builder.Services.AddSingleton<InProcessInfraOpUnlockCache>();
builder.Services.AddSingleton<IInfraOpUnlockCache>(sp => sp.GetRequiredService<InProcessInfraOpUnlockCache>());
builder.Services.AddHostedService<InfraOpUnlockSweepService>();
```

The sweep isn't strictly necessary for correctness — `TryGet` already evicts on expired lookup — but it bounds the dictionary by inactive-user count without waiting for the next request. Cheap.

## RequireInfraOpUnlockFilter (`Features/Auth/StepUp/RequireInfraOpUnlockFilter.cs`)

```csharp
public sealed class RequireInfraOpUnlockFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        var sub  = http.User.FindFirstValue(AuthClaimTypes.SubUs);
        if (sub is null) return Results.Unauthorized();
        var cache = http.RequestServices.GetRequiredService<IInfraOpUnlockCache>();
        Span<byte> probe = stackalloc byte[32];
        if (!cache.TryGet(Guid.Parse(sub), probe))
            return Results.Json(new { error = "step_up_required" }, statusCode: StatusCodes.Status401Unauthorized);
        CryptographicOperations.ZeroMemory(probe);
        return await next(ctx);
    }
}
```

Consumers attach it like:

```csharp
app.MapPost("/api/clouds/{id}/destroy", DestroyHandler)
   .RequireAuthorization()                          // cookie + totp policies as needed
   .AddEndpointFilter<RequireInfraOpUnlockFilter>();
```

The filter only probes for cache presence; it doesn't hand the DEK to the inner handler. The inner handler re-resolves `IInfraOpUnlockCache.TryGet` to read the DEK (one extra dictionary lookup; ~50 ns). Threading the DEK through `EndpointFilterInvocationContext.Arguments` is awkward (mutable `IList<object?>`) and the duplication is one line. Keep them decoupled.

## PassphraseEndpoints

```csharp
public static class PassphraseEndpoints
{
    public static void MapPassphraseEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/passphrase/init", async (
            PassphraseInitRequest body,
            ClaimsPrincipal user,
            PassphraseService svc,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var ok = await svc.InitAsync(userId, body.Passphrase, ct);
            return ok ? Results.NoContent() : Results.Conflict(new { error = "passphrase_already_set" });
        }).RequireAuthorization(AuthPolicies.TotpRequired);

        app.MapPost("/api/auth/unlock", async (
            PassphraseUnlockRequest body,
            ClaimsPrincipal user,
            PassphraseService svc,
            IInfraOpUnlockCache cache,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
            var dek = new byte[32];
            var result = await svc.TryUnwrapDekAsync(userId, body.Passphrase, dek, ct);
            if (result is UnlockResult.Failed)
            {
                CryptographicOperations.ZeroMemory(dek);
                return Results.Json(new { error = "invalid_passphrase" }, statusCode: StatusCodes.Status401Unauthorized);
            }
            cache.Set(userId, dek);
            CryptographicOperations.ZeroMemory(dek);
            return Results.NoContent();
        }).RequireAuthorization(AuthPolicies.TotpRequired);
    }
}
```

Three points:

- **`AuthPolicies.TotpRequired` on both endpoints** — you must have completed TOTP before you can set or use the passphrase. Setting a passphrase from a `totp=not-verified` cookie would let an attacker who steals a partial-auth cookie register a passphrase on the account. ADR-0030 §"Cookie claim shape" implies this; making it explicit here is the right call.
- **`cache.Set` happens *after* successful unwrap, *before* zeroing the local `dek`.** `Set` copies into its own buffer (see `InProcessInfraOpUnlockCache.Set`), so the local zero is safe.
- **No `/passphrase/status` endpoint** — `/api/auth/me` already returns `passphraseSet: bool` per PORTAL-003 (`AuthEndpoints.cs`). The SPA reads it from there. If it doesn't (verify before starting!), add the field to the existing `MeResponse` — that's a one-line change, not a new endpoint.

## Signout + invalidation hook

In `AuthEndpoints.cs`, the existing `/api/auth/signout` handler:

```csharp
app.MapPost("/api/auth/signout", async (
    ClaimsPrincipal user,
    HttpContext http,
    IInfraOpUnlockCache cache) =>          // ADD
{
    var sub = user.FindFirstValue(AuthClaimTypes.SubUs);
    if (sub is not null) cache.Invalidate(Guid.Parse(sub));  // ADD
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
});
```

In `CookiePrincipalValidator.ValidateAsync`, in the existing rejection path for `sessions_invalidated_at`:

```csharp
if (user.SessionsInvalidatedAt is { } cutoff && cookieIssuedAt < cutoff)
{
    _cache.Invalidate(userId);             // ADD — inject IInfraOpUnlockCache via ctor
    ctx.RejectPrincipal();
    await ctx.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return;
}
```

The validator's constructor gains `IInfraOpUnlockCache cache`. Tests that construct it manually (PORTAL-003 has these) gain a `Mock<IInfraOpUnlockCache>` or a real cache in the test container — easier path is to thread the real cache through, since these are integration tests already.

## SvelteKit: `lib/stepUpClient.ts`

```typescript
import { writable } from 'svelte/store';

type ResolveFn = (passphrase: string | null) => void;
export const stepUpPrompt = writable<{ resolve: ResolveFn } | null>(null);

function promptForPassphrase(): Promise<string | null> {
  return new Promise(resolve => stepUpPrompt.set({ resolve }));
}

export async function fetchWithStepUp(input: RequestInfo, init?: RequestInit): Promise<Response> {
  const res = await fetch(input, init);
  if (res.status !== 401) return res;
  const body = await res.clone().json().catch(() => null);
  if (body?.error !== 'step_up_required') return res;

  const passphrase = await promptForPassphrase();
  if (passphrase === null) return res;

  const unlockRes = await fetch('/api/auth/unlock', {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ passphrase }),
  });
  if (!unlockRes.ok) return unlockRes;
  return fetch(input, init);
}
```

`StepUpModal.svelte` subscribes to `stepUpPrompt`, renders a form on subscription, calls `resolve(passphrase)` on submit, or `resolve(null)` on cancel. The modal is mounted once in `+layout.svelte`.

Consumers of step-up endpoints just call `fetchWithStepUp` instead of `fetch`. PORTAL-011 (provisioning wizard) and PORTAL-015 (destroy flow) will be the first real consumers; this ticket lands the plumbing only.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings**.
- All existing PORTAL-003 / 003a tests still green; new step-up tests all green.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts cleanly. Two new endpoints discoverable in OpenAPI: `POST /api/auth/passphrase/init` and `POST /api/auth/unlock`.
- **`PassphraseServiceTests` (unit / DB):** `InitAsync` populates all five columns and `passphrase_set_at`. Second `InitAsync` returns `false` and does not overwrite. `TryUnwrapDekAsync` with the original passphrase returns `Unlocked` and fills the caller's 32-byte buffer with the DEK that was generated during init. With a wrong passphrase, returns `Failed` and the buffer is untouched (caller's responsibility to zero). With a passphrase that derives to a KEK matching a *different* user's wrapped DEK, returns `Failed` (AES-GCM tag verification catches this — sanity check).
- **`InProcessInfraOpUnlockCacheTests` (unit):** `Set` then `TryGet` returns the DEK. `TryGet` on an unknown user returns `false`. `TryGet` on an expired entry returns `false` and removes the entry. `TryGet` on a fresh entry bumps `ExpiresAt` forward (assert via two `FakeClock` advancements: tick +5 min, `TryGet` succeeds; tick +9 min more, `TryGet` still succeeds because the prior `TryGet` reset the window). `Invalidate` removes the entry and zeros the previously stored buffer (assert by capturing the buffer reference via a test-only accessor or by reading `CryptographicOperations.FixedTimeEquals(buffer, zeros)` — easier: write a small `InternalsVisibleTo("ThanyMarcus.Portal.Tests")` and assert directly).
- **Concurrent `TryGet` safety:** spawn 100 parallel `TryGet`s on a present entry; all return `true`, no exceptions, dictionary still has one entry.
- **`PassphraseEndpointsTests` (integration):** with `WithTestAuth(userId, totp: Verified)`, `POST /api/auth/passphrase/init { passphrase: "hunter2hunter2" }` returns 204. Database row populated. Second call returns 409. `POST /api/auth/unlock { passphrase: "hunter2hunter2" }` returns 204; cache is now populated for `userId`. `POST /api/auth/unlock { passphrase: "wrong" }` returns 401 with `{ error: "invalid_passphrase" }`. Cache unchanged.
- **TOTP-not-verified blocks both endpoints:** with `WithTestAuth(userId, totp: NotVerified)`, both `/init` and `/unlock` return 403 (the `TotpRequired` policy).
- **`RequireInfraOpUnlockFilterTests` (integration):** a stub endpoint `GET /test/step-up-only` decorated with `.AddEndpointFilter<RequireInfraOpUnlockFilter>()` returns `401 { error: "step_up_required" }` when the cache is empty for the user; returns 200 after a successful `/unlock`; returns `401 step_up_required` again after `FakeClock` advances 11 minutes without intermediate calls.
- **`StepUpInvalidationTests` (integration):** after a successful `/unlock`, calling `POST /api/auth/signout` empties the cache for that user (next `TryGet` returns false). After a successful `/unlock`, bumping the user's `sessions_invalidated_at` in the DB and issuing any request causes `CookiePrincipalValidator` to reject the principal *and* invalidate the cache (next `TryGet` returns false).
- **`pnpm build` in `src/ThanyMarcus.Portal.Web/`** still succeeds. Manual click-through (`dotnet watch` + `pnpm dev`):
  - `/settings/security` shows "Set passphrase" when `passphraseSet === false`; after submission, the section disappears.
  - With a protected stub endpoint added to `+page.server.ts` (or just `fetchWithStepUp("/test/step-up-only")` from a button), clicking the button shows the passphrase modal on first click, then completes after entering the right passphrase; second click within 10 minutes does not re-prompt.

## Concrete steps in order (each maps to a task)

1. **Land `Argon2Params`, `UnlockResult`, request records.** Trivial records; no logic.

2. **Land `PassphraseService` + `PassphraseServiceTests`** (TDD). Use the existing `DbIntegrationTestBase`. Use `FakeClock`. The `Init` → `TryUnwrap` round-trip is the load-bearing test; everything else falls out.

3. **Land `IInfraOpUnlockCache` + `InProcessInfraOpUnlockCache` + tests** (TDD). Pure unit tests; no DB. `InternalsVisibleTo` to the test assembly to expose `SweepExpired`. `FakeClock` for the TTL tests.

4. **Land `InfraOpUnlockSweepService`.** Smoke test only — assert that registering it doesn't crash startup. The behavior is covered by the cache tests via direct `SweepExpired` calls.

5. **Land `RequireInfraOpUnlockFilter` + tests.** Define the stub endpoint inline in the test project (not in production code) so the filter has something to gate. The test factory exposes the `IInfraOpUnlockCache` singleton; tests `Set` directly to seed the "unlocked" state.

6. **Land `PassphraseEndpoints` + tests.** Now end-to-end: `/init` → `/unlock` → protected stub returns 200. The `cache.Set` call inside `/unlock` is the integration test's load-bearing assertion.

7. **Wire `Cache.Invalidate(userId)` into `/api/auth/signout`.** One-line change to `AuthEndpoints.cs`; add a test asserting that signout-then-protected-endpoint returns `step_up_required`.

8. **Wire `Cache.Invalidate(userId)` into `CookiePrincipalValidator`.** The validator's ctor gains `IInfraOpUnlockCache`. Update the PORTAL-003 tests to pass through the new dependency. Add a test asserting that a `sessions_invalidated_at` bump invalidates the cache as a side effect of the next request.

9. **SvelteKit: `stepUpClient.ts` + `StepUpModal.svelte` + `+layout.svelte` mount.** No backend tests for this; manual click-through is the validation.

10. **SvelteKit: passphrase setup section on `/settings/security`.** Reads `me.passphraseSet`; if false, shows form; on submit, POSTs to `/api/auth/passphrase/init`; on 204, reloads `/api/auth/me`.

11. **Manual end-to-end:** sign in, complete TOTP if enabled, hit `/settings/security`, set a passphrase, navigate to a stub or test page that hits a step-up-gated endpoint, observe the modal, enter the passphrase, observe success, retry within 10 minutes, observe no modal, advance 11 minutes (wall clock), observe the modal again.

12. **Verify all acceptance criteria.** Commit only after.

## Risks & gotchas

- **Argon2id `m=47104` allocates 46 MiB per derive.** Concurrent `/unlock` calls multiply this. The 003c rate limit (5/5 min per user) bounds the per-user concurrency, but across 1000 active users in a burst this is 46 GiB if every user happens to unlock at the same moment — implausible in single-VM thesis scope, but worth noting. If memory pressure shows up in load testing (PORTAL-018 territory), drop `m` to 19456 or move to `p=2 / m=19456` (OWASP also-recommended profile).

- **`AesGcm` requires .NET 8+ with `AesGcm(byte[], int)` constructor for explicit tag size.** .NET 10 has this. The two-arg constructor `new AesGcm(kek, 16)` pins the tag size to 16 bytes; the older `new AesGcm(kek)` uses a default that varies. Be explicit.

- **`PassphraseService.TryUnwrapDekAsync` returns the DEK via an out-parameter buffer.** Forgetting to `ZeroMemory(dek)` after copying into the cache leaves a copy on the caller's stack frame. The endpoint code does this; helpers added later (e.g., recovery-code redemption in PORTAL-006) must too. Code review for ZeroMemory pairings is the only enforcement.

- **`InternalsVisibleTo` on the API project.** The cache tests need access to `SweepExpired` and possibly the `CacheEntry` buffer to assert zeroing. Add `<InternalsVisibleTo Include="ThanyMarcus.Portal.Tests" />` to `ThanyMarcus.Portal.Api.csproj`. If it's already there from PORTAL-003, no change.

- **`ClaimsPrincipal.FindFirstValue(AuthClaimTypes.SubUs)` can be null** when the filter runs before authentication completes. The filter null-checks and returns 401 — but the proper ordering is `RequireAuthorization()` *before* `AddEndpointFilter<RequireInfraOpUnlockFilter>()` on the route registration. Authorization middleware runs first; the filter only runs after auth populates `HttpContext.User`. Still, defensive null-check stays.

- **`fetchWithStepUp` retry can loop if `/unlock` returns 401 step_up_required** (which it never should, since `/unlock` doesn't have the filter — but if a future refactor adds it, infinite loop). Add a `__stepUpRetry` flag on the second call to short-circuit, or just trust the architectural invariant. Document in `stepUpClient.ts` with a one-line comment (rare "non-obvious why" exception per the no-comments rule).

- **CSRF on `/unlock`.** Same-site cookies + `SameSite=Lax` give the baseline. Add an `Origin` / `Referer` check on `/unlock` *only if* the cookie scheme's `SameSite=Strict` is not in place. PORTAL-003 set `SameSite=Lax`, which is the right tradeoff (Strict breaks OAuth callbacks). Lax is fine for state-changing endpoints since the modern browser implementation blocks CSRF POSTs without explicit origin opt-in. No extra defense in this ticket.

- **Modal UX: focus trap + ESC handling + multiple concurrent requests.** If two protected requests race, both get 401 and both try to open the modal. `stepUpPrompt` is a single-slot writable store — the second `set(...)` clobbers the first promise's resolver. Fix: queue resolvers in an array, drain on submit (every queued request resolves with the same passphrase, retries in parallel). Alternatively: single-flight the unlock; multiple 401s funnel into one prompt + one /unlock + N retries. Simpler: single-flight. Land single-flight if you have time; otherwise document the race as a known limitation. The realistic concurrent-step-up case (user clicks "destroy cloud A" and "destroy cloud B" simultaneously) is rare.

- **Test cache pollution across xUnit collection.** The cache is a singleton in the WebApplicationFactory; tests that `Set` an entry leak into subsequent tests in the same collection. Either reset by calling `factory.Services.GetRequiredService<IInfraOpUnlockCache>().Invalidate(userId)` in fixture teardown, or use `WithWebHostBuilder` to create a fresh factory per test class. The PORTAL-003 test setup already does the latter; follow that pattern.

- **`PassphraseSetAt` column existence.** ADR-0030 §"Schema impact" doesn't explicitly list `passphrase_set_at` (only the five crypto columns) — verify in `User.cs` from PORTAL-002 that this column exists; if not, add it via a migration in this ticket (small additive change, no risk). If the column is named differently (`passphrase_initialized_at`, etc.), use that name.

- **Argon2Params jsonb shape stability.** `JsonSerializer.SerializeToDocument(Argon2Params)` produces `{ "memoryKiB": ..., "iterations": ..., "parallelism": ... }` (System.Text.Json camelCase per project default — check `JsonSerializerOptions` in `Program.cs`). On read, `Deserialize<Argon2Params>()` round-trips. If a future param is added (e.g., `version`), the record gets an optional property and the deserialize is forward-compatible. Don't snake-case the JSON; it's stored on bytes already in the DB.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + green `dotnet test` + a manual end-to-end: sign in, complete TOTP, set a passphrase from `/settings/security`, hit a step-up-gated stub endpoint, see the modal, enter the passphrase, see success; within 10 minutes retry the same endpoint and see no modal; advance past 10 minutes idle, retry and see the modal again; sign out, sign back in, hit the endpoint, see the modal (cache cleared by signout).

A fresh agent can pick up PORTAL-003c (rate limiting) from cold by reading:

1. ADR-0031 §"In-memory layer" + §"Ticket split".
2. PORTAL-003b's `PassphraseEndpoints.cs` + `TotpEndpoints.cs` (from 003a) as the endpoints to decorate.
3. The `UnlockResult` / `TotpChallengeResult` enums as the seam: 003c rate-limits the *endpoints*, 003d hooks the *enum result* to write `auth_lockouts` rows.
4. Memory `portal_tooling.md` for the warnings-as-errors expectation.

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-005 first half ("Passphrase-encrypted provider-token vault ... decrypt-only-in-memory abstraction") — the *vault* half (per-token encrypt/decrypt) stays in PORTAL-005; the *step-up + unlock cache* half lands here, per ADR-0031 §"Ticket split".
- Adjacent tickets that PORTAL-003b unblocks:
  - PORTAL-003c — in-memory rate limiting (decorates `/api/auth/unlock` with `RequireRateLimiting`)
  - PORTAL-003d — persistent lockout (hooks `UnlockResult.Failed` to write `auth_lockouts.kind = 'unlock'` rows)
  - PORTAL-003e — Cloudflare Turnstile (gates `/unlock` post-throttle)
  - PORTAL-005 — passphrase-encrypted provider tokens (consumes `IInfraOpUnlockCache.TryGet` to obtain the DEK on the encrypt/decrypt path; lands `/passphrase/change` which re-wraps wrapped DEK + every encrypted token + invalidates the cache)
  - PORTAL-006 — recovery codes (consumes `PassphraseService.InitAsync` shape to re-issue a new DEK after redemption; lands the recovery-code-redemption endpoint that mints a new wrapped-DEK envelope)
  - PORTAL-011 — provisioning wizard (the first feature behind `.AddEndpointFilter<RequireInfraOpUnlockFilter>()`)
  - PORTAL-015 — destroy flow (second consumer of `RequireInfraOpUnlockFilter`)
