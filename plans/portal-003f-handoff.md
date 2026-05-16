# PORTAL-003f Multi-instance hardening — Handoff Brief

**Goal:** apply three deferred amendments to `Program.cs` (data-protection key ring persisted, forwarded headers wired, `IInfraOpUnlockCache` moved from in-process to Postgres) so the running code matches the locked architecture from [[0033-provisioning-saga-and-worker]] and the deployment story works behind Caddy. **No new features.** Pure remediation pass that brings the existing code in line with decisions reached after PORTAL-003 was originally implemented.

Estimated **0.75 person-day** with heavy AI-agent assistance.

## Where decisions live (read before doing anything)

- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the broader architecture context; this ticket lands the "multi-instance-ready code" prerequisite that the saga depends on (in-process caches must not be load-bearing across worker/API boundaries once PORTAL-007a splits them).
- **`docs/decisions/0030-auth-flow.md` §"Step-up: 4c → α"** — original IInfraOpUnlockCache decision was *in-process memory*; this ticket *amends* that decision to Postgres-backed with at-rest encryption via `IDataProtector`. The "DEK never persists outside process memory" property is softened to "**DEK never persists outside process memory in plaintext** — encrypted form persists in Postgres". Update ADR-0030 with a short amendment paragraph as part of this ticket.
- **`docs/decisions/0027-reverse-proxy-caddy.md`** — Caddy terminates TLS in prod; portal runs plain HTTP behind it. `UseForwardedHeaders` is required for `Request.IsHttps`, `Secure` cookies, and `IssuedUtc` comparisons to behave correctly.
- **`plans/portal-003-handoff.md` §"Deployment-architecture amendments"** — already lists items 1 and 2 of this ticket as required wiring; PORTAL-003 was implemented before the amendments were added to the handoff, so they must be retrofitted here.
- **Memory files**: `portal_deployment.md` (single-VM Docker Compose, multi-instance-ready code), `portal_architecture.md`.

**Do not re-litigate the design.** The grilling session 2026-05-16 closed every decision in this scope. If something seems unclear, the relevant ADR is the authoritative answer.

## Output of PORTAL-003f — final state of changed files

```
Thany-Marcus/
├── docs/decisions/
│   └── 0030-auth-flow.md                              # AMENDED: note added under "Decision §Step-up" softening the
│                                                       # "DEK never persists outside process memory" property
├── src/ThanyMarcus.Portal.Api/
│   ├── Program.cs                                     # CHANGED: AddDataProtection.PersistKeysToFileSystem, Configure<ForwardedHeadersOptions>,
│   │                                                   # UseForwardedHeaders, swap InProcessInfraOpUnlockCache → PostgresInfraOpUnlockCache
│   ├── ThanyMarcus.Portal.Api.csproj                  # adds Microsoft.AspNetCore.HttpOverrides (transitive, but verify)
│   ├── Features/Auth/StepUp/
│   │   ├── IInfraOpUnlockCache.cs                     # CHANGED: methods become async (returning Task<bool>/Task) for DB I/O
│   │   ├── InProcessInfraOpUnlockCache.cs             # DELETED
│   │   ├── InfraOpUnlockSweepService.cs               # CHANGED: now sweeps the Postgres table, not the in-process dict
│   │   ├── PostgresInfraOpUnlockCache.cs              # NEW: persists encrypted DEKs in `step_up_unlocks` table
│   │   ├── StepUpUnlock.cs                            # NEW: entity for the new table
│   │   └── StepUpUnlockConfiguration.cs               # NEW: EF Core config
│   ├── Infrastructure/Database/
│   │   ├── PortalDbContext.cs                         # CHANGED: adds DbSet<StepUpUnlock>
│   │   └── Migrations/<timestamp>_StepUpUnlocks.cs    # NEW: generated migration for the new table
│   └── .gitignore                                     # adds data-protection-keys/
├── docker-compose.yml                                 # NEW or AMENDED if it exists: data-protection-keys volume + bind mount
│                                                       # (deferred until PORTAL-007a if no compose file exists yet)
└── tests/ThanyMarcus.Portal.Tests/
    ├── Features/Auth/StepUp/
    │   ├── InProcessInfraOpUnlockCacheTests.cs        # DELETED
    │   ├── PostgresInfraOpUnlockCacheTests.cs         # NEW: round-trip with FakeClock; expired entries return false; sweep deletes
    │   └── PassphraseEndpointsTests.cs                # may need minor tweaks if it asserted on in-process cache observable state
    └── Features/Auth/
        └── ForwardedHeadersTests.cs                   # NEW: integration test proving X-Forwarded-Proto produces Request.IsHttps=true
```

## Packages to verify (`Directory.Packages.props`)

The required packages are already in the project. Confirm:
- `Microsoft.EntityFrameworkCore` — already present
- `Microsoft.AspNetCore.DataProtection` — part of `Microsoft.AspNetCore.App` shared framework; no separate reference needed
- `Microsoft.AspNetCore.HttpOverrides` — part of the shared framework; usings only

If `dotnet restore` warns about anything, fix before proceeding.

## Item 1 — Persist data-protection key ring

**Why it matters:** `TotpService` uses `IDataProtector` to encrypt TOTP secrets. With the default in-process key store, **every Portal.Api restart invalidates every user's TOTP secret** — they'd have to re-enable. Similarly, the new `PostgresInfraOpUnlockCache` (Item 3 below) will encrypt DEKs with `IDataProtector`; without persistence, every restart kicks every signed-in user back to the passphrase prompt.

**Current state of `Program.cs` (around line 60):**

```csharp
// TODO(PORTAL-017): PersistKeysToFileSystem so restarts don't invalidate TotpSecret ciphertexts.
builder.Services.AddDataProtection()
    .SetApplicationName("ThanyMarcus.Portal");
```

**Replace with:**

```csharp
var dpKeysDir = builder.Configuration["DataProtection:KeyRingPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data-protection-keys");
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("ThanyMarcus.Portal");
```

**Also:**

- Add `data-protection-keys/` to `.gitignore`.
- Document in the README (Configuration section) that the path defaults to `<ContentRoot>/data-protection-keys/` in dev and should be overridden to a volume-mounted path (e.g., `/var/lib/portal/data-protection-keys`) in production.

**Defer the docker-compose volume mount.** PORTAL-007a is the ticket that lands `docker-compose.yml`. If it doesn't exist yet, just document the expected volume name in this handoff's risks section so PORTAL-007a picks it up.

## Item 2 — Forwarded headers middleware

**Why it matters:** Caddy terminates TLS in production and forwards plain HTTP to Portal.Api with `X-Forwarded-Proto: https`. Without `UseForwardedHeaders`, ASP.NET Core sees the request as HTTP, `Request.IsHttps` is `false`, the cookie scheme's `SecurePolicy.Always` refuses to set the cookie, and the user can't sign in.

**Add to `Program.cs`** — services registration, anywhere in the configuration phase (suggested: right after `AddDataProtection`, before `AddAuthentication`):

```csharp
builder.Services.Configure<ForwardedHeadersOptions>(opts =>
{
    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Compose stack: Caddy is the only upstream. We trust it implicitly by clearing the known-proxy filter.
    opts.KnownNetworks.Clear();
    opts.KnownProxies.Clear();
});
```

**Add to the pipeline** — `app.UseForwardedHeaders()` must come **before** `app.UseAuthentication()` so the cookie scheme reads the corrected scheme. Insert right after `app.UseStaticFiles()`:

```csharp
app.UseStaticFiles();

// Caddy terminates TLS; trust X-Forwarded-* so SecurePolicy.Always works.
app.UseForwardedHeaders();

app.UseMiddleware<SignInGoogleRateLimitMiddleware>();
app.UseAuthentication();
// ...
```

Required usings:

```csharp
using Microsoft.AspNetCore.HttpOverrides;
```

## Item 3 — Postgres-backed `IInfraOpUnlockCache`

**Why it matters:** the in-process cache is single-process-bound. Once PORTAL-007a extracts SagaWorker into its own container, the SagaWorker won't be able to read DEKs the Portal.Api process put into its memory — every infra op will fail with "step-up required". Landing the Postgres-backed cache now makes the eventual split a deployment-topology change rather than a code rewrite.

**Tradeoff acknowledgement:** ADR-0030's original property "DEK never persists outside process memory" is softened to "**DEK never persists outside process memory in plaintext**". The encrypted form lives in Postgres, decryptable only via the App's data-protection key ring (which lives on a shared filesystem volume, mounted into both containers). Cookie-theft + portal-DB-read + data-protection-key-read = DEK leak — the same blast radius as a portal-DB compromise generally. Document this in the ADR-0030 amendment.

### Schema — new `step_up_unlocks` table

```text
step_up_unlocks
─────────────────────────────────────────────────────────────────────────
  user_id              uuid          PRIMARY KEY, FK → users(id) ON DELETE CASCADE
  encrypted_dek        bytea         NOT NULL  -- IDataProtector ciphertext of the raw DEK bytes
  expires_at           timestamptz   NOT NULL  -- absolute expiry; sliding TTL bumps this
  last_used_at         timestamptz   NOT NULL  -- bumped on TryGet
  created_at           timestamptz   NOT NULL
INDEX (expires_at)  -- for the sweep job
```

No `nonce`/`tag` columns: `IDataProtector` returns a single opaque blob (same pattern `TotpService` uses for `TotpSecret.Ciphertext`). The Argon2id+AES-GCM three-column shape per ADR-0029 applies to *user-passphrase-derived* envelopes; `IDataProtector` is a different envelope (DP-key-derived) and uses its own format.

### New entity + configuration

`Features/Auth/StepUp/StepUpUnlock.cs`:

```csharp
public sealed class StepUpUnlock
{
    public Guid Id { get; init; } = Guid.CreateVersion7();      // unused except as audit identifier; PK is user_id
    public Guid UserId { get; init; }
    public byte[] EncryptedDek { get; set; } = null!;
    public Instant ExpiresAt { get; set; }
    public Instant LastUsedAt { get; set; }
    public Instant CreatedAt { get; init; }
}
```

Note: there's no `IHasUpdatedAt` here — the unlock entry is mutable but the `UpdatedAt` audit semantics don't apply (entries are short-lived; recreated on each unlock). Mirror the `TimestampInterceptor` exemption pattern that already exists for other short-lived entities.

`Features/Auth/StepUp/StepUpUnlockConfiguration.cs`:

```csharp
public sealed class StepUpUnlockConfiguration : IEntityTypeConfiguration<StepUpUnlock>
{
    public void Configure(EntityTypeBuilder<StepUpUnlock> b)
    {
        b.ToTable("step_up_unlocks");
        b.HasKey(u => u.UserId);
        b.Property(u => u.EncryptedDek).IsRequired();
        b.Property(u => u.ExpiresAt).IsRequired();
        b.Property(u => u.LastUsedAt).IsRequired();
        b.Property(u => u.CreatedAt).IsRequired();
        b.HasIndex(u => u.ExpiresAt);
        b.HasOne<User>().WithMany().HasForeignKey(u => u.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
```

Add `public DbSet<StepUpUnlock> StepUpUnlocks => Set<StepUpUnlock>();` to `PortalDbContext`.

Generate the migration: `dotnet ef migrations add StepUpUnlocks --project src/ThanyMarcus.Portal.Api -o Infrastructure/Database/Migrations`. Review the generated SQL — `step_up_unlocks` table with the expected columns and FK.

### Interface change

`IInfraOpUnlockCache.cs` — methods become async (DB I/O):

```csharp
public interface IInfraOpUnlockCache
{
    Task<bool> TryGetAsync(Guid userId, byte[] dekDestination, CancellationToken ct);

    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Set matches the cache semantics; not consumed from VB.")]
    Task SetAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct);

    Task InvalidateAsync(Guid userId, CancellationToken ct);
}
```

Note the parameter shape change: `Span<byte>` becomes `byte[]` and `ReadOnlySpan<byte>` becomes `ReadOnlyMemory<byte>` because spans can't cross `await` boundaries. Callers (`PassphraseEndpoints`, future `ProviderTokenVault`) update accordingly — `dek` is already a `byte[]` in the calling code, just pass it as-is.

### Implementation — `PostgresInfraOpUnlockCache`

`Features/Auth/StepUp/PostgresInfraOpUnlockCache.cs`:

```csharp
public sealed class PostgresInfraOpUnlockCache(
    PortalDbContext db,
    IDataProtectionProvider dp,
    IClock clock) : IInfraOpUnlockCache
{
    private static readonly Duration SlidingTtl = Duration.FromMinutes(10);
    private readonly IDataProtector _protector = dp.CreateProtector("step-up-unlock.v1");

    public async Task<bool> TryGetAsync(Guid userId, byte[] dekDestination, CancellationToken ct)
    {
        var entry = await db.StepUpUnlocks.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (entry is null) return false;
        var now = clock.GetCurrentInstant();
        if (entry.ExpiresAt <= now)
        {
            db.StepUpUnlocks.Remove(entry);
            await db.SaveChangesAsync(ct);
            return false;
        }

        byte[] plaintext;
        try { plaintext = _protector.Unprotect(entry.EncryptedDek); }
        catch (CryptographicException) { return false; }                // DP-key rotated mid-session; treat as expired

        if (plaintext.Length != dekDestination.Length)
        {
            CryptographicOperations.ZeroMemory(plaintext);
            return false;
        }
        try
        {
            plaintext.AsSpan().CopyTo(dekDestination);
            entry.LastUsedAt = now;
            entry.ExpiresAt  = now + SlidingTtl;
            await db.SaveChangesAsync(ct);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SetAsync(Guid userId, ReadOnlyMemory<byte> dek, CancellationToken ct)
    {
        var encrypted = _protector.Protect(dek.ToArray());
        var now       = clock.GetCurrentInstant();
        var existing  = await db.StepUpUnlocks.SingleOrDefaultAsync(u => u.UserId == userId, ct);
        if (existing is null)
        {
            db.StepUpUnlocks.Add(new StepUpUnlock
            {
                UserId        = userId,
                EncryptedDek  = encrypted,
                ExpiresAt     = now + SlidingTtl,
                LastUsedAt    = now,
                CreatedAt     = now,
            });
        }
        else
        {
            existing.EncryptedDek = encrypted;
            existing.ExpiresAt    = now + SlidingTtl;
            existing.LastUsedAt   = now;
        }
        await db.SaveChangesAsync(ct);
    }

    public async Task InvalidateAsync(Guid userId, CancellationToken ct)
    {
        await db.StepUpUnlocks.Where(u => u.UserId == userId).ExecuteDeleteAsync(ct);
    }
}
```

### Sweep service — `InfraOpUnlockSweepService`

Replace the in-process sweep with a DB sweep:

```csharp
public sealed class InfraOpUnlockSweepService(IServiceProvider sp, IClock clock) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await using var scope = sp.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
                var cutoff = clock.GetCurrentInstant();
                await db.StepUpUnlocks
                    .Where(u => u.ExpiresAt <= cutoff)
                    .ExecuteDeleteAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
    }
}
```

### Service registration update in `Program.cs`

Replace the existing block (around lines 67–69):

```csharp
builder.Services.AddSingleton<InProcessInfraOpUnlockCache>();
builder.Services.AddSingleton<IInfraOpUnlockCache>(sp => sp.GetRequiredService<InProcessInfraOpUnlockCache>());
builder.Services.AddHostedService<InfraOpUnlockSweepService>();
```

with:

```csharp
builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();
builder.Services.AddHostedService<InfraOpUnlockSweepService>();
```

Note: lifetime changes from `Singleton` to `Scoped` because the impl depends on a scoped `PortalDbContext`. The sweep service creates its own scope per tick.

### Call-site updates

`PassphraseEndpoints.cs` — `cache.Set(userId, dek)` becomes `await cache.SetAsync(userId, dek, ct)`. Other callsites (`RequireInfraOpUnlockFilter`, any tests) update the same way. Compile errors will guide the rest.

## Acceptance criteria

- `dotnet build` succeeds with **zero warnings** (warnings-as-errors per [[portal_tooling]]).
- `dotnet run --project tests/ThanyMarcus.Portal.Tests` exits 0; **all existing tests still green** — this is a pure remediation pass.
- `dotnet ef migrations list` shows the new `StepUpUnlocks` migration appended after `AddPassphraseSetAt`.
- `psql portal_dev -c "\dt"` lists the new `step_up_unlocks` table.
- `psql portal_dev -c "\d step_up_unlocks"` confirms FK `user_id` is `ON DELETE CASCADE`, `expires_at` has an index, `encrypted_dek` is `bytea NOT NULL`.
- New test `PostgresInfraOpUnlockCacheTests`: covers (a) TryGet on empty store → false, (b) Set then TryGet → true with the right bytes, (c) wrong-length destination buffer → false, (d) expired entry returns false and is deleted, (e) TryGet bumps `expires_at` forward (sliding), (f) Invalidate removes the row, (g) sweep service deletes expired rows on tick.
- New test `ForwardedHeadersTests`: WebApplicationFactory client sends `X-Forwarded-Proto: https` to `/api/auth/me`, the request handler observes `HttpContext.Request.IsHttps == true`, response cookies have the `Secure` flag.
- Manual test (or augmented `PassphraseEndpointsTests`): after `POST /api/auth/unlock` succeeds, restarting the Portal.Api process and then calling an infra-op-protected endpoint **still** sees the DEK in cache (because it's in Postgres now). Previously, restart would force re-unlock — verifying this works is the point of Item 3.
- The deleted `InProcessInfraOpUnlockCache.cs` and `InProcessInfraOpUnlockCacheTests.cs` are gone; no references remain in `Program.cs` or elsewhere (`grep -r InProcessInfraOpUnlockCache src/ tests/` returns nothing).
- ADR-0030 has a new amendment paragraph noting the softening of "DEK never persists outside process memory".

## Concrete steps in order (each maps to a task)

1. **Apply Item 1** — `Program.cs` DataProtection persistence. Add `data-protection-keys/` to `.gitignore`. Build + tests still pass (no behavior change yet beyond key-ring persistence; existing tests don't depend on it surviving restart).

2. **Apply Item 2** — `Program.cs` ForwardedHeaders. Add `ForwardedHeadersTests` proving X-Forwarded-Proto produces `IsHttps=true`. Tests pass.

3. **Land the new entity** — `StepUpUnlock` + `StepUpUnlockConfiguration`. Add `DbSet` to `PortalDbContext`. Generate migration `dotnet ef migrations add StepUpUnlocks`. Review generated SQL. Build passes. New migration applied automatically on next test run via the existing `PostgresFixture` MigrateAsync.

4. **Change the interface** — `IInfraOpUnlockCache` methods become async. Build fails at all call sites; that's expected.

5. **Land `PostgresInfraOpUnlockCache`** — implement per the sketch above. Replace `InProcessInfraOpUnlockCache` registration in `Program.cs`. Rewire the sweep service to scope-resolve `PortalDbContext`. Delete the in-process impl + its tests.

6. **Update call sites** — `PassphraseEndpoints.cs` and any others. Build green.

7. **Write `PostgresInfraOpUnlockCacheTests`** — covering the 7 cases listed in Acceptance.

8. **Run full test suite** — every existing test should still pass; new tests should pass. If `PassphraseEndpointsTests` had any in-process-cache observable-state asserts, update to query `db.StepUpUnlocks` instead.

9. **Add the ADR-0030 amendment** — a 3-sentence paragraph under "Decision §Step-up" noting that for multi-instance readiness the DEK now persists encrypted-at-rest in Postgres, with the security tradeoff explicit.

10. **Verify acceptance criteria** — every bullet. Commit only after all pass.

## Out of scope (do not touch)

- **`docker-compose.yml` and the bind-mount for `data-protection-keys`**: PORTAL-007a's territory; landing it now would orphan the compose file. The default `Path.Combine(ContentRoot, "data-protection-keys")` works fine for local dev without docker-compose.
- **Migration to a stronger DEK envelope** (e.g., user-passphrase-derived re-wrap of cached DEK): out of scope. The IDataProtector wrap is adequate per the amended ADR-0030.
- **`encrypted_provider_tokens` writes**: PORTAL-005's territory.
- **Cross-instance pub/sub**: not needed under single-VM with single Portal.Api process. Future-work documented in [[0033-provisioning-saga-and-worker]] §"Consequences".
- **Rate-limiter to Postgres-backed**: per the grilling, in-memory per-instance is acceptable at thesis scope, with the persistent lockout layer absorbing sustained attacks. Stays as-is.

## Risks & gotchas

- **`Span<byte>` → `byte[]` interface change ripples.** Span can't cross `await`. Every caller of `TryGet`/`Set` updates; compile errors guide the rewrite. Don't try to keep Span — DB I/O is fundamentally async.

- **`PortalDbContext` lifetime change.** `InProcessInfraOpUnlockCache` was a singleton; `PostgresInfraOpUnlockCache` is scoped (it depends on a scoped DbContext). Sweep service must `CreateAsyncScope` per tick; sketch above does this. If you keep singleton lifetime by accident, EF Core will throw at runtime about the captured DbContext.

- **Migration order.** The `AddPassphraseSetAt` migration must already be in `Migrations/`. `dotnet ef migrations add StepUpUnlocks` appends; do not edit the migration history.

- **`ExecuteDeleteAsync` requires EF Core 7+.** We're on 10, so fine. Don't replace with `db.Remove` + `SaveChanges` — that loads rows into the change tracker first and is slower.

- **`IDataProtector` purpose string must be stable.** `dp.CreateProtector("step-up-unlock.v1")` — the `v1` suffix lets us rotate the key purpose later if needed without breaking existing cipher blobs (we'd add a `v2` protector + a one-shot migration that re-protects under the new purpose). Don't change `v1` without a migration plan.

- **`Protect` / `Unprotect` semantics.** `Unprotect` throws `CryptographicException` if the key ring lost the key. The sketched impl catches and returns false, which surfaces to the user as "step-up required" — better than a 500.

- **`AsNoTracking()` not used for the cache reads.** We *update* the row (sliding TTL bump) — tracked entity is correct here. Don't optimize prematurely.

- **`step_up_unlocks` size.** ~100 bytes per row; even at 10k concurrent unlocked sessions, ~1 MB. Sweep keeps it bounded. No index beyond `expires_at` needed.

- **Cookie validation crashes after data-protection migration**: if the key ring directory doesn't exist or isn't writable at startup, ASP.NET Core falls back to ephemeral keys silently — cookies issued before next restart die at next restart. The `Directory.CreateDirectory(dpKeysDir)` line in Item 1 prevents this. Verify the directory is writable from the test fixture if running tests in CI containers; mount point permissions are the usual culprit.

- **`KnownNetworks.Clear()` / `KnownProxies.Clear()` is permissive.** It tells ASP.NET Core to accept `X-Forwarded-*` from any upstream. Safe in our compose stack where Caddy is the only thing reaching Portal.Api (Postgres talks SQL, no HTTP). If we ever expose Portal.Api directly to the internet (no Caddy), this becomes a spoofing surface — but [[portal_deployment]] makes Caddy mandatory, so it's fine.

- **`UseForwardedHeaders` order.** Must come before `UseAuthentication`. Wrong order silently breaks cookies. The handoff is explicit; double-check after merging.

- **Test fixture migrations.** The existing `PostgresFixture` calls `MigrateAsync` on InitializeAsync; the new migration applies automatically. If you find tests failing due to missing tables, you're looking at a stale Testcontainers image — restart the test container.

## Definition of done

All acceptance criteria pass + `git status` shows the new files (entity, configuration, migration, new impl, new tests) and the deletions (in-process cache + its tests) + a green `scripts/test.sh` + ADR-0030 amendment committed. Tasks marked completed. The deployment story now matches the locked architecture; PORTAL-007a can proceed without retrofitting.

A fresh agent can pick PORTAL-007a (SagaWorker scaffold) up from cold knowing that:
- `IInfraOpUnlockCache` works across multiple processes that share the same `step_up_unlocks` table and the same data-protection key ring (filesystem volume).
- ForwardedHeaders is wired so Portal.Api behind Caddy or any reverse proxy works.
- DP keys persist across restarts, so TOTP secrets and unlock cache entries survive.

## Cross-references

- **Original deferral**: `Program.cs:60` (`TODO(PORTAL-017): PersistKeysToFileSystem...`) and the absence of `Configure<ForwardedHeadersOptions>` / `UseForwardedHeaders` — both were marked as PORTAL-017 territory in the original PORTAL-003 implementation. PORTAL-003f extracts them out of PORTAL-017 because they're prerequisites for PORTAL-007a, not deployment-only concerns.
- **PORTAL-003 handoff §"Deployment-architecture amendments"** — the same wiring is now documented there too; this ticket implements it.
- **PORTAL-007a** — depends on this ticket's `step_up_unlocks` table existing and DP key ring being persistent + cross-process-readable.
- **PORTAL-005** — independent of this ticket but lands shortly after; the provider-token vault doesn't directly use `IInfraOpUnlockCache` (the calling endpoint does), so no coupling.
