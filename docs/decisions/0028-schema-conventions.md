# ADR-0028: Schema conventions — migration, test isolation, naming, audit

Status: Accepted
Date: 2026-05-15

## Context

PORTAL-002 ships the first Postgres schema (`PortalDbContext`, ~8 tables, first migration). Before writing migrations, four foundational conventions need to be locked so they apply uniformly across every table:

1. **Migration runner pattern** — where does `dotnet ef database update` actually execute against a real DB? [[0024-dbcontext-shape]] explicitly deferred this to PORTAL-002.
2. **Per-test DB isolation** — [[0023-test-stack]] picked Testcontainers.PostgreSql but deferred the per-test cleanup strategy to PORTAL-002.
3. **SQL identifier naming** — [[0019-background-work-and-saga-durability]] sketched snake_case in its sample but did not formalize enforcement.
4. **Audit field convention** — every table has timestamp columns; the discipline around `created_at`, `updated_at`, and optimistic concurrency tokens must be uniform or it will drift table by table.

## Options considered

### 1. Migration runner

- **A. App calls `db.Database.MigrateAsync()` at startup** before `app.Run()`. Same code path in dev/test/prod. Self-contained: deploying the API container migrates its own DB. Concurrent boots in multi-replica deploys race — Postgres advisory lock mitigates if needed.
- **B. Sidecar / init container** runs `dotnet ef database update` before the API starts. Clean separation; two artifacts to build and ship.
- **C. CLI-only — humans run `dotnet ef database update` on deploy.** Most control, zero magic; easy to forget.
- **D. App auto-migrates in Development + Testing only; prod uses CLI.** Hybrid; diverges code paths by environment.

### 2. Per-test isolation

- **A. Transaction-per-test.** Begin a transaction in test setup; roll back in teardown. Fast. But the `FOR UPDATE SKIP LOCKED` job-claim query uses explicit `BeginTransactionAsync` — nesting inside a test transaction makes it a savepoint, testing nested-transaction behavior rather than real `BEGIN ... COMMIT`. And HTTP-pipeline `SaveChangesAsync` writes are invisible across the rollback.
- **B. `Respawn` NuGet.** Wipes a configured table list via `TRUNCATE … RESTART IDENTITY CASCADE` after each test. ~1-3ms per reset for small tables. Survives any commit pattern.
- **C. New Postgres schema per test.** `CREATE SCHEMA test_<guid>`; drop after. Cleanest isolation; expensive per-test setup.
- **D. New container per test.** ~3-5s per test class. Off the table for our scale.
- **E. Hand-rolled `TRUNCATE` in fixture cleanup.** DIY Respawn.

### 3. Naming convention enforcement

- **A. `EFCore.NamingConventions` package, `UseSnakeCaseNamingConvention()`.** One-line setup; PascalCase C# properties auto-map to snake_case columns. Authored by Shay Rojansky (Npgsql lead).
- **B. Hand-written `ToTable("snake_case")` + `HasColumnName("snake_case")`** in every `IEntityTypeConfiguration<T>`. Explicit; boilerplate scales with column count; drift risk on forgotten `HasColumnName`.
- **C. Default PascalCase.** EF Core out-of-box. Conflicts with [[0019-background-work-and-saga-durability]]'s SQL samples; requires quoted identifiers in psql.

### 4. Audit conventions

- **A. `created_at` everywhere; `updated_at` only on mutable rows; typed event timestamps for lifecycle (`ready_at`, `used_at`, `revoked_at`, `destroyed_at`); no generic `state_changed_at`; no soft-delete except via lifecycle timestamps.**
- **B. Uniform `created_at` + `updated_at` on every table**, even immutable rows.
- **C. Enterprise audit columns**: `created_by_user_id`, `updated_by_user_id`. Self-referential awkwardness on `users`; system-generated rows lack an actor.
- **D. Append-only event log.** Explicitly rejected by [[0019-background-work-and-saga-durability]] for the saga.
- **E. Soft delete on user-owned tables.** Doubles every query; complicates Respawn; GDPR right-to-be-forgotten still needs hard delete underneath.

Sub-axes inside A:

- **4.1 `updated_at` maintenance**: (i) EF Core `SaveChangesInterceptor` (app-side, deterministic), (ii) Postgres `BEFORE UPDATE` trigger (DB-side), (iii) manual at every mutation site (drift-prone).
- **4.2 `created_at` source**: (i) injected `IClock` (test-deterministic), (ii) DB default `now()` (centralized but harder to test).
- **4.3 optimistic concurrency token**: (a) none, (b) `xmin` (Postgres-native, free but the EF Core wiring is unusual), (c) manual `version int` with `IsRowVersion()` (familiar pattern, costs a column).

## Decision

1. **Migration runner: A** — startup `MigrateAsync`. Same path in dev/test/prod. Advisory lock omitted as YAGNI — [[portal_deployment]] is single-VM Docker Compose, no multi-replica race.
2. **Per-test isolation: B** — Respawn. The job-claim test from [[0019-background-work-and-saga-durability]] is load-bearing for the thesis claim about Postgres-native saga durability; it must exercise real `BEGIN ... COMMIT`, which (A) breaks.
3. **Naming: A** — `EFCore.NamingConventions` with `UseSnakeCaseNamingConvention()`. Removes the discipline problem of (B).
4. **Audit: A** — typed columns where they earn their keep. Sub-decisions:
   - **4.1 → (i)** EF Core `SaveChangesInterceptor` keyed off an `IHasUpdatedAt` marker interface.
   - **4.2 → (i)** injected `IClock` (NodaTime `SystemClock` in prod, `FakeClock` in tests; see [[0029-type-mappings]]).
   - **4.3 → (a) no concurrency token in MVP.** Single-writer-per-row in the saga model; HTTP handlers read-then-write within one `SaveChangesAsync`. The xmin convention pattern is correct but unusual enough to add cognitive load; the `version int` alternative adds a column for no realistic gain. Adding either later is additive.

### Concrete shape

```csharp
// Program.cs
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddDbContext<PortalDbContext>(opts => opts
    .UseNpgsql(cs, npg => npg.UseNodaTime())
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(new TimestampInterceptor(/* IClock */)));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
    await db.Database.MigrateAsync();
}

// Infrastructure/Database/TimestampInterceptor.cs
public sealed class TimestampInterceptor(IClock clock) : SaveChangesInterceptor
{
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        foreach (var entry in eventData.Context!.ChangeTracker.Entries<IHasUpdatedAt>())
        {
            if (entry.State == EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }
        return base.SavingChangesAsync(eventData, result, ct);
    }
}

public interface IHasUpdatedAt
{
    Instant UpdatedAt { get; set; }
}
```

Test fixture additions ([[0023-test-stack]]):

```csharp
// PostgresFixture.cs gains:
public Respawner Respawner { get; private set; } = null!;

public async ValueTask InitializeAsync()
{
    await Container.StartAsync();
    // Migrate schema once per test session
    var opts = new DbContextOptionsBuilder<PortalDbContext>().UseNpgsql(ConnectionString).Options;
    await using var db = new PortalDbContext(opts);
    await db.Database.MigrateAsync();

    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    Respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
    {
        DbAdapter = DbAdapter.Postgres,
        SchemasToInclude = ["public"],
        TablesToIgnore = [new Table("__EFMigrationsHistory")],
    });
}

public async Task ResetAsync()
{
    await using var conn = new NpgsqlConnection(ConnectionString);
    await conn.OpenAsync();
    await Respawner.ResetAsync(conn);
}
```

## Consequences

- **Positive:**
  - One code path runs migrations everywhere; `PostgresFixture` and prod boot diverge only on connection string.
  - Test isolation honors real `BEGIN ... COMMIT` semantics; the saga job-claim test is verifiable, not aspirational.
  - Naming convention is automatic, not a discipline problem; new entities get snake_case for free.
  - Time can be controlled in tests via `FakeClock`, enabling deterministic tests of lease expiration, recovery code expiry, step-up window timing.
- **Negative:**
  - Startup migration crashes the app on a bad migration. For a single-VM deploy this is loud-not-silent (which is good); for a multi-replica future, revisit and add an advisory lock or move to (B).
  - Respawn requires the Docker daemon (already required by [[0023-test-stack]]).
  - `SaveChangesInterceptor` for `updated_at` means manual `psql` UPDATEs do not refresh the timestamp. Accepted: we have no ops workflow that touches data outside the app.
- **Neutral:**
  - Re-adding optimistic concurrency later is additive (xmin convention or `version int` column) — not painted into a corner.
  - Argon2id work-factor migration is handled inside PHC strings (see [[0029-type-mappings]]) — no separate schema-version column needed.

## Related

- [[0019-background-work-and-saga-durability]] — Postgres job-queue tests require honest transaction semantics
- [[0023-test-stack]] — Testcontainers.PostgreSql + xUnit v3 + Shouldly already locked; this ADR finishes the per-test cleanup story
- [[0024-dbcontext-shape]] — single `PortalDbContext` + per-feature `IEntityTypeConfiguration<T>`; the migration runner decision was explicitly deferred here
- [[0029-type-mappings]] — PK strategy, timestamp type, crypto column shapes
- [[0030-auth-flow]] — auth-related tables and `sessions_invalidated_at` column follow these conventions
- [[0032-fk-cascades-and-soft-delete]] — `destroyed_at` on `clouds` is a lifecycle timestamp under convention 4
