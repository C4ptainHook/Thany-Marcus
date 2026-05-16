# PORTAL-002 Schema — Handoff Brief

**Goal:** produce a runnable Postgres schema for the portal — `PortalDbContext` with eight entities, one initial EF Core migration, the test-fixture wiring that applies it, and one passing test per entity proving the column shape and key relationships. **No auth logic, no endpoints, no business logic.** PORTAL-003+ fills those in on top.

Estimated **0.5–0.75 person-day** with heavy AI-agent assistance (longer than original 0.5d because of soft-delete + auth_lockouts + crypto column shape additions from the grilling).

## Where decisions live (read before doing anything)

- **`docs/decisions/0028-schema-conventions.md`** — migration runner (startup `MigrateAsync`), Respawn for per-test isolation, `EFCore.NamingConventions` snake_case, audit conventions (typed columns, `SaveChangesInterceptor`, `IClock`, **no concurrency token in MVP**).
- **`docs/decisions/0029-type-mappings.md`** — UUIDv7 PKs (`Guid.CreateVersion7()`), NodaTime `Instant` for `timestamptz`, three-column AES-GCM layout (`*_ciphertext`/`*_nonce`/`*_tag`), PHC strings for Argon2id verification hashes.
- **`docs/decisions/0030-auth-flow.md`** — auth shape that determines which columns exist on `users` (`sessions_invalidated_at`, `profile_picture_url`, `passphrase_*`). Logic implemented in PORTAL-003+; PORTAL-002 only lands the columns.
- **`docs/decisions/0031-rate-limiting-and-lockout.md`** — `auth_lockouts` table shape; logic implemented in PORTAL-003d.
- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — cascade map (RESTRICT on `clouds.user_id`, soft-delete on `clouds` via `destroyed_at`, CASCADE elsewhere).
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — `provisioning_jobs` table was originally specced here; cascade rule revised in 0032.
- **`docs/decisions/0023-test-stack.md`** — Testcontainers.PostgreSql harness already exists from PORTAL-001's testing pipeline; this ticket wires `MigrateAsync` + Respawn into `PostgresFixture`.
- **`docs/decisions/0024-dbcontext-shape.md`** — single `PortalDbContext`, per-feature `IEntityTypeConfiguration<T>` co-located with entities, `FromSqlInterpolated` for the job-claim query (PORTAL-007's territory; not here).
- **Memory files under `~/.claude/projects/-Users-bboiko-Personal-Thesis/memory/`**: `portal_architecture.md`, `portal_tooling.md`, `portal_deployment.md`.

**Do not re-litigate the schema during implementation.** The 8-question schema grilling + 5-question auth mini-grilling closed every decision that PORTAL-002 needs. If something seems unclear, the ADR for that decision is the authoritative answer.

## Output of PORTAL-002 — final directory state

```
Thany-Marcus/
├── Directory.Packages.props                    # new package versions added (EF Core, Npgsql, NodaTime, Respawn, etc.)
├── src/
│   └── ThanyMarcus.Portal.Api/
│       ├── Program.cs                          # adds DbContext registration + IClock + MigrateAsync at startup
│       ├── ThanyMarcus.Portal.Api.csproj       # adds the new PackageReferences
│       ├── Features/
│       │   ├── Auth/
│       │   │   ├── User.cs                                  # entity
│       │   │   ├── UserConfiguration.cs                     # IEntityTypeConfiguration<User>
│       │   │   ├── TotpSecret.cs
│       │   │   ├── TotpSecretConfiguration.cs
│       │   │   ├── TotpBackupCode.cs
│       │   │   ├── TotpBackupCodeConfiguration.cs
│       │   │   ├── RecoveryCode.cs
│       │   │   ├── RecoveryCodeConfiguration.cs
│       │   │   ├── EncryptedProviderToken.cs
│       │   │   ├── EncryptedProviderTokenConfiguration.cs
│       │   │   ├── AuthLockout.cs
│       │   │   └── AuthLockoutConfiguration.cs
│       │   ├── CloudManagement/
│       │   │   ├── Cloud.cs
│       │   │   ├── CloudConfiguration.cs                    # includes HasQueryFilter(c => c.DestroyedAt == null)
│       │   │   ├── PluginTokenMetadata.cs                   # portal-side metadata only; see ADR-0030
│       │   │   └── PluginTokenMetadataConfiguration.cs
│       │   └── Provisioning/
│       │       ├── ProvisioningJob.cs
│       │       └── ProvisioningJobConfiguration.cs
│       └── Infrastructure/
│           └── Database/
│               ├── PortalDbContext.cs                       # ApplyConfigurationsFromAssembly + UseNpgsql + UseNodaTime
│               ├── DesignTimeDbContextFactory.cs            # for `dotnet ef` tooling
│               ├── IHasUpdatedAt.cs                         # marker interface for the interceptor
│               ├── TimestampInterceptor.cs                  # SaveChangesInterceptor; stamps UpdatedAt via IClock
│               └── Migrations/
│                   └── <timestamp>_Initial.cs               # generated; do not hand-edit unless necessary
├── tests/
│   └── ThanyMarcus.Portal.Tests/
│       ├── ThanyMarcus.Portal.Tests.csproj     # adds Respawn + NodaTime.Testing
│       ├── Infrastructure/
│       │   ├── PostgresFixture.cs              # GAINS: MigrateAsync, Respawner.CreateAsync, ResetAsync
│       │   ├── PostgresCollection.cs           # unchanged
│       │   ├── PortalApiFactory.cs             # may gain: override IClock with FakeClock
│       │   └── DbIntegrationTestBase.cs        # NEW: base class that calls ResetAsync in InitializeAsync
│       ├── Features/
│       │   ├── Auth/
│       │   │   ├── UserTests.cs                            # INSERT round-trip + google_subject unique constraint
│       │   │   ├── TotpSecretTests.cs                      # 1:1 with user; CASCADE delete
│       │   │   ├── TotpBackupCodeTests.cs                  # CASCADE; used_at semantics
│       │   │   ├── RecoveryCodeTests.cs                    # CASCADE; envelope columns shape
│       │   │   ├── EncryptedProviderTokenTests.cs          # UNIQUE (user_id, provider); CASCADE
│       │   │   └── AuthLockoutTests.cs                     # composite PK (user_id, kind); CASCADE
│       │   ├── CloudManagement/
│       │   │   ├── CloudTests.cs                           # RESTRICT on user_id; HasQueryFilter on destroyed_at
│       │   │   └── PluginTokenMetadataTests.cs             # CASCADE
│       │   └── Provisioning/
│       │       └── ProvisioningJobTests.cs                 # status indexes; lease_expires nullable
│       └── PostgresHarnessTests.cs             # already exists from PORTAL-001 testing pipeline; stays
└── (rest unchanged)
```

## Packages to add (`Directory.Packages.props`)

Add these `PackageVersion` entries; pin to current stable when adding:

```xml
<!-- EF Core + Postgres -->
<PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.*" />
<PackageVersion Include="Microsoft.EntityFrameworkCore.Design" Version="10.*" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.*" />
<PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime" Version="10.*" />
<PackageVersion Include="EFCore.NamingConventions" Version="10.*" />

<!-- Time -->
<PackageVersion Include="NodaTime" Version="3.*" />

<!-- Test isolation -->
<PackageVersion Include="Respawn" Version="6.*" />
<PackageVersion Include="NodaTime.Testing" Version="3.*" />
```

`ThanyMarcus.Portal.Api.csproj` adds (no `Version` attr — central management):

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Design" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime" />
<PackageReference Include="EFCore.NamingConventions" />
<PackageReference Include="NodaTime" />
```

`ThanyMarcus.Portal.Tests.csproj` adds:

```xml
<PackageReference Include="Respawn" />
<PackageReference Include="NodaTime.Testing" />
```

## Program.cs wiring (additions to PORTAL-001's wiring)

Add **before** `var app = builder.Build();`:

```csharp
// --- Database ---
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<TimestampInterceptor>();
builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Portal")
            ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured"),
        npg => npg.UseNodaTime())
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

// --- Health check addition (per ADR-0026) ---
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live", "ready"])
    .AddDbContextCheck<PortalDbContext>(tags: ["ready"]);   // ← new; reports DB readiness
```

Add **after** `var app = builder.Build();` and **before** the existing `app.UseStaticFiles();`:

```csharp
// --- Run migrations on startup (ADR-0028) ---
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
    await db.Database.MigrateAsync();
}
```

`appsettings.json` adds:

```json
{
  "ConnectionStrings": {
    "Portal": "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=portal_dev"
  }
}
```

`appsettings.Development.json` may override with a developer-local connection string. CI receives the connection string via `Testcontainers` at test time (not via this file).

## PortalDbContext skeleton

`src/ThanyMarcus.Portal.Api/Infrastructure/Database/PortalDbContext.cs`:

```csharp
public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<User>                   Users                    => Set<User>();
    public DbSet<TotpSecret>             TotpSecrets              => Set<TotpSecret>();
    public DbSet<TotpBackupCode>         TotpBackupCodes          => Set<TotpBackupCode>();
    public DbSet<RecoveryCode>           RecoveryCodes            => Set<RecoveryCode>();
    public DbSet<EncryptedProviderToken> EncryptedProviderTokens  => Set<EncryptedProviderToken>();
    public DbSet<AuthLockout>            AuthLockouts             => Set<AuthLockout>();
    public DbSet<Cloud>                  Clouds                   => Set<Cloud>();
    public DbSet<PluginTokenMetadata>    PluginTokenMetadata      => Set<PluginTokenMetadata>();
    public DbSet<ProvisioningJob>        ProvisioningJobs         => Set<ProvisioningJob>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortalDbContext).Assembly);
    }
}
```

`Infrastructure/Database/IHasUpdatedAt.cs`:

```csharp
public interface IHasUpdatedAt
{
    Instant UpdatedAt { get; set; }
}
```

`Infrastructure/Database/TimestampInterceptor.cs`:

```csharp
public sealed class TimestampInterceptor(IClock clock) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        if (eventData.Context is null) return base.SavingChangesAsync(eventData, result, ct);
        var now = clock.GetCurrentInstant();
        foreach (var entry in eventData.Context.ChangeTracker.Entries<IHasUpdatedAt>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
        return base.SavingChangesAsync(eventData, result, ct);
    }
}
```

`Infrastructure/Database/DesignTimeDbContextFactory.cs` (for `dotnet ef migrations add`):

```csharp
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PortalDbContext>
{
    public PortalDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(
                Environment.GetEnvironmentVariable("PORTAL_DESIGN_TIME_CONNECTION_STRING")
                    ?? "Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=portal_design",
                npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new PortalDbContext(opts);
    }
}
```

## Test fixture additions

Update `tests/ThanyMarcus.Portal.Tests/Infrastructure/PostgresFixture.cs`:

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    public string ConnectionString => Container.GetConnectionString();
    public Respawner Respawner { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await Container.StartAsync();

        // Apply migrations once per test session.
        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new PortalDbContext(opts))
        {
            await db.Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        Respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = [new Respawn.Graph.Table("__EFMigrationsHistory")],
        });
    }

    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await Respawner.ResetAsync(conn);
    }

    public async ValueTask DisposeAsync()
    {
        await Container.DisposeAsync();
    }
}
```

Add `tests/ThanyMarcus.Portal.Tests/Infrastructure/DbIntegrationTestBase.cs`:

```csharp
[Collection(PostgresCollection.Name)]
public abstract class DbIntegrationTestBase : IAsyncLifetime
{
    protected PostgresFixture Postgres { get; }
    protected FakeClock Clock { get; } = new(Instant.FromUtc(2026, 5, 15, 12, 0));
    protected PortalDbContext Db { get; private set; } = null!;

    protected DbIntegrationTestBase(PostgresFixture postgres) => Postgres = postgres;

    public async ValueTask InitializeAsync()
    {
        await Postgres.ResetAsync();
        var opts = new DbContextOptionsBuilder<PortalDbContext>()
            .UseNpgsql(Postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new TimestampInterceptor(Clock))
            .Options;
        Db = new PortalDbContext(opts);
    }

    public async ValueTask DisposeAsync() => await Db.DisposeAsync();
}
```

Tests inherit this base and get `Db` (a fresh `PortalDbContext`) and `Clock` (a `FakeClock`) automatically. Respawn cleans the DB before each test.

## Acceptance criteria

After running the implementation steps below, all of the following must be true:

- `dotnet build` succeeds with **zero warnings** (warnings-as-errors is on per [[portal_tooling]]).
- `scripts/test.sh` runs through clean (smoke + harness + per-entity tests all green).
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts on `:5000`. On first start, applies the initial migration against an empty `portal_dev` database (developer must `createdb portal_dev` first or use the Docker Compose Postgres).
- `dotnet ef migrations list` shows exactly **one** migration (`Initial`).
- `curl http://localhost:5000/health/ready` → 200 OK with `"db": "Healthy"` (or similar) in the body.
- `psql portal_dev -c "\dt"` lists **9 tables**: `users`, `totp_secrets`, `totp_backup_codes`, `recovery_codes`, `encrypted_provider_tokens`, `auth_lockouts`, `clouds`, `plugin_token_metadata`, `provisioning_jobs` — all in snake_case.
- `psql portal_dev -c "\d clouds"` shows the cascade rules from ADR-0032: `user_id` foreign key is `ON DELETE RESTRICT`, `destroyed_at` is nullable `timestamptz`.
- Each entity has at least one test covering: (a) INSERT round-trips, (b) any UNIQUE constraint rejects duplicates, (c) FK cascade behavior (CASCADE deletes children, RESTRICT throws).
- `Cloud` has a test proving `HasQueryFilter` excludes rows with `destroyed_at IS NOT NULL` from default queries and `IgnoreQueryFilters()` includes them.
- `provisioning_jobs` has a test proving the partial indexes work: a `pending` row + 1000 `completed` rows still resolves the "next pending" query via the index, not a scan.
- `TimestampInterceptor` test: updating an entity bumps `UpdatedAt` to the `FakeClock`'s current instant.
- No tests use `DateTime` (only `Instant`).
- No entity uses `DateTime` (only `Instant`).

## Concrete steps in order (each maps to a task)

1. **Add packages** — update `Directory.Packages.props` + the two csprojs. Run `dotnet restore`. Verify zero warnings.

2. **Add `PortalDbContext` + `IHasUpdatedAt` + `TimestampInterceptor` + `DesignTimeDbContextFactory` skeleton** — no entities yet, just the empty context and the interceptor. Build should compile. Tests pass (no new tests yet).

3. **Wire `Program.cs`** — add the DbContext registration, `IClock`, `MigrateAsync` at startup, `AddDbContextCheck` health check. Build. Tests still pass (no migration exists yet, so `MigrateAsync` is a no-op against an empty DB — won't fail).

4. **Update `PostgresFixture`** — add `MigrateAsync` call to `InitializeAsync` (no-op until migrations exist) + Respawn creation. Update `Directory.Packages.props` if needed. Add `DbIntegrationTestBase`. Tests pass.

5. **Write the first failing test: `UserTests`** — INSERT a User, SELECT it, assert fields round-trip; assert duplicate `google_subject` throws. Test will fail (no entity yet).

6. **Implement `User` + `UserConfiguration`** — covers all columns from ADR-0030's schema impact + ADR-0028's audit conventions.

7. **Generate first migration** — `dotnet ef migrations add Initial --project src/ThanyMarcus.Portal.Api -o Infrastructure/Database/Migrations`. Review the generated SQL — assert it's snake_case, has the right columns and constraints.

8. **Run tests** — `UserTests` should now pass. `MigrateAsync` in the fixture applies the new migration. Respawn resets between tests.

9. **Repeat steps 5–8 for each remaining entity in this order** (chosen for FK dependency order):
   - `TotpSecret`, `TotpBackupCode`, `RecoveryCode` (1:N from `users`, CASCADE)
   - `EncryptedProviderToken` (1:N from `users`, CASCADE, UNIQUE `(user_id, provider)`)
   - `AuthLockout` (composite PK, CASCADE)
   - `Cloud` (1:N from `users`, RESTRICT, soft-delete via `destroyed_at`, `HasQueryFilter`)
   - `PluginTokenMetadata` (1:N from `clouds`, CASCADE)
   - `ProvisioningJob` (1:N from `clouds`, CASCADE, partial indexes)

   For each: write a failing test → implement entity + configuration → migration → tests pass.

   **All entities should be added in a single migration** by running `dotnet ef migrations add` only once, after step 6 (the User entity). Each subsequent entity is added with `dotnet ef migrations` again, which UPDATES the same `Initial` migration if you `remove` and `add` repeatedly, OR creates incremental migrations (e.g., `AddTotpSecret`, `AddTotpBackupCode`). **Prefer the latter** — one migration per entity makes the migration history readable and reversible. The acceptance criteria of "exactly one migration" should be revised: it's fine to have one migration per entity. Update the acceptance criteria accordingly: **9 migrations, one per entity**.

10. **Cross-entity tests** — once all entities exist, add tests that span entities:
    - Deleting a user with no clouds succeeds and cascades to totp/recovery/etc.
    - Deleting a user WITH a cloud throws `DbUpdateException` (FK RESTRICT).
    - Deleting a cloud cascades to plugin_token_metadata and provisioning_jobs.
    - Soft-deleting a cloud (set `destroyed_at`) excludes it from default `Clouds.ToListAsync()` but `IgnoreQueryFilters` includes it.
    - `TimestampInterceptor` bumps `UpdatedAt` on a Cloud update.

11. **Verify acceptance criteria** — run every check in the Acceptance criteria section. Commit only after all checks pass.

## Out of scope (do not touch)

- **Auth endpoints** — PORTAL-003 onward. PORTAL-002 lands the columns on `users`/`totp_secrets`/`recovery_codes`/`encrypted_provider_tokens` but no `OnValidatePrincipal`, no `/signin-google`, no `/totp-challenge`, no `IInfraOpUnlockCache`.
- **Rate limiting / `auth_lockouts` writes** — PORTAL-003c/d. The table exists; no code reads or writes it yet.
- **Cloudflare Turnstile** — PORTAL-003e.
- **Saga worker / job claim** — PORTAL-007. The `provisioning_jobs` table exists with the right shape; no `HostedService`, no `ClaimNextAsync`.
- **Terraform integration** — PORTAL-007 onward.
- **OpenAPI documentation of feature endpoints** — there are no feature endpoints yet.
- **SvelteKit changes** — no UI for any of this yet.
- **CI changes** — `.github/workflows/portal-ci.yml` already runs the test suite; new tests run automatically. No CI changes needed.

## Risks & gotchas

- **`Npgsql.EntityFrameworkCore.PostgreSQL` version drift.** Major Npgsql releases sometimes break `UseNpgsql` configuration. Pin to current stable when adding the package; if `UseNodaTime()` doesn't compile, check the package's release notes for renamed methods.
- **`UseSnakeCaseNamingConvention` and reserved Postgres keywords.** Default snake_case for "User" → "user" → that's a reserved Postgres keyword. EF Core will quote it on emission, but it's worth using `b.ToTable("users")` (plural) explicitly to sidestep. **Use plural snake_case table names throughout** (`users`, `clouds`, etc.) — already in the schema spec.
- **`xmin` system column showing up in `dotnet ef migrations`.** It shouldn't, because we're not configuring optimistic concurrency (ADR-0028 → no concurrency token). If the generated migration tries to add an `xmin` column, you've accidentally wired the convention loop from the grilling conversation. Remove that loop from `OnModelCreating`.
- **`HasQueryFilter` and Respawn.** Respawn's `TRUNCATE … CASCADE` doesn't care about query filters; it wipes all rows including soft-deleted ones. Tests that verify the filter behavior need to INSERT then verify within the same test, not assume previous-test state.
- **`Microsoft.EntityFrameworkCore.Design` package.** Required for `dotnet ef` tooling. Some teams put this as `<PrivateAssets>all</PrivateAssets>` to avoid shipping the design DLL to runtime — that's correct. EF Core docs cover the right `csproj` shape.
- **Connection string in tests.** `PortalApiFactory` already injects `ConnectionStrings:Portal` from the Testcontainers connection string (PORTAL-001 testing pipeline). Don't hard-code in tests.
- **EF Core `Instant` mapping requires `npg.UseNodaTime()`.** Forgetting this gives a runtime error on first query, not a compile error. Easy to forget; the build won't catch it. Use `PostgresHarnessTests` (which already verifies the container starts) as a sanity check.
- **`SaveChangesInterceptor` and async**: use `SavingChangesAsync` (not `SavingChanges`) since EF Core's async path is the one tests will exercise via `await db.SaveChangesAsync()`.
- **Migration history bloat.** One migration per entity (per step 9) generates a clean reversible history. Resist the urge to consolidate into a single `Initial` migration — it makes the diff at every step hard to review.
- **PHC string encoding.** The `recovery_codes.hashed_code` and `totp_backup_codes.hashed_code` columns are `text` (per ADR-0029). PORTAL-002 doesn't implement Argon2id encoding — just lands the column. PORTAL-003a will write the `PhcEncoder` helper.
- **`provisioning_jobs.payload` as `jsonb`.** EF Core 10 maps `JsonDocument` to `jsonb` by default for Npgsql. If you map a `string` and call it JSON, EF will store as `text` and you'll have to migrate later. Use `JsonDocument` or a typed `record` with `OwnsOne` or value converters.
- **Tests using `Instant.FromUtc(...)` rather than `Instant.MinValue`.** `MinValue` for `Instant` is a sentinel that doesn't map to a meaningful `timestamptz`; using it in `WHERE` clauses for tests will be confusing. Always pick a concrete year like 2026-05-15.

## Definition of done

All acceptance criteria pass + `git status` shows the new files + a green `scripts/test.sh`. Tasks marked completed in the task list. The DB schema in a fresh `portal_dev` matches the spec from the grilling exactly.

A fresh agent can pick PORTAL-003 up from cold by reading:

1. This handoff + the 5 new ADRs (0028–0032).
2. The four memory files referenced at the top.
3. The existing PORTAL-001 handoff for testing-pipeline conventions.
4. Running `scripts/test.sh` to confirm baseline green.

## Cross-references

- Original ticket (now superseded): `plans/tickets-2026-05-13.md` PORTAL-002 — 0.5 d estimate. Revised: 0.5–0.75 d with the soft-delete / `auth_lockouts` / crypto column additions.
- Adjacent tickets that PORTAL-002 unblocks:
  - PORTAL-003 — Google SSO + cookie auth (uses `users.sessions_invalidated_at`, `users.profile_picture_url`)
  - PORTAL-003a — TOTP enable/disable + backup codes (uses `totp_secrets`, `totp_backup_codes`)
  - PORTAL-003b — Step-up auth (uses `users.passphrase_*` columns)
  - PORTAL-003c — Rate limiting (no DB; in-memory only)
  - PORTAL-003d — Persistent lockout (uses `auth_lockouts`)
  - PORTAL-003e — Cloudflare Turnstile (no DB)
  - PORTAL-005 — Crypto vault (the actual implementation of envelope encryption that the column shapes support)
  - PORTAL-006 — Recovery codes (uses `recovery_codes` envelope columns + `hashed_code` PHC strings)
  - PORTAL-007 — Terraform runner (uses `provisioning_jobs`)
