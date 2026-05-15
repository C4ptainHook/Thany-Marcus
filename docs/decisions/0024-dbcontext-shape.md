# ADR-0024: DbContext shape — single PortalDbContext with per-feature configurations

Status: Accepted
Date: 2026-05-15

## Context

Portal.Api has ~7–10 tables across ~6 features:

- `users`, `totp_secrets`, `totp_backup_codes`, `recovery_codes` (Auth)
- `clouds` (CloudManagement)
- `plugin_tokens` (PluginTokens)
- `provisioning_jobs` (Provisioning — see [[0019-background-work-and-saga-durability]])
- `encrypted_provider_tokens` (Crypto vault — PORTAL-005)

These are aggressively relational with foreign keys (`users ↔ clouds`, `clouds ↔ plugin_tokens`, `users ↔ recovery_codes`) and unique constraints (one TOTP secret per user, one provider-token vault per user). Postgres-locked. Single application owner.

Two design questions:

1. **Single `DbContext` or split per bounded context?** Vertical Slice Architecture ([[0018-portal-api-minimal-apis-vsa]]) might suggest one DbContext per feature, but DbContext split is orthogonal to slice ownership in EF Core.
2. **EF Core only, or EF Core + Dapper for raw-SQL hot spots?** The job-claim query (`FOR UPDATE SKIP LOCKED`) has no LINQ form.

## Options considered

### DbContext shape

- **A. Single `PortalDbContext` with all `DbSet<T>`s.** All entity configurations apply in one place. One migrations assembly. Cross-feature queries are trivial.
- **B. Split by bounded context.** `AuthDbContext` (users, totp, recovery), `CloudDbContext` (clouds, jobs, plugin tokens). Cleaner DDD aesthetics. Cross-context transactions in EF Core require sharing a `DbConnection` and orchestrating transactions manually (`UseTransaction`) — fiddly. Two migrations assemblies, two `dotnet ef` commands.
- **C. One DbContext per feature folder.** Maximum slice purity. Maximum migration complexity. Every cross-feature query becomes an integration concern.

### Configuration co-location

- **A. Configuration in `PortalDbContext.OnModelCreating`.** Single file with all `modelBuilder.Entity<T>(...)` calls. Centralized but cuts across all features.
- **B. Per-feature `IEntityTypeConfiguration<T>` classes co-located with the entity.** `Features/Auth/UserConfiguration.cs : IEntityTypeConfiguration<User>`. `PortalDbContext.OnModelCreating` calls `ApplyConfigurationsFromAssembly(typeof(PortalDbContext).Assembly)` once — pulls all configs in automatically.

### Raw-SQL escape hatch

- **A. EF Core's `FromSqlInterpolated` for hot spots.** Parameterized (SQL-injection safe), returns tracked entities, idiomatic. One ORM mental model.
- **B. EF Core + Dapper.** Use Dapper for the few queries where EF Core LINQ is awkward (job claim with `FOR UPDATE SKIP LOCKED`). Two ORM-shaped abstractions in the codebase; double the test setup; inconsistent transaction handling.

## Decision

- **A. Single `PortalDbContext`.**
- **B. Per-feature `IEntityTypeConfiguration<T>` co-located with each entity.**
- **A. EF Core only, with `FromSqlInterpolated` for the job-claim query.**

### Concrete shape

```
src/ThanyMarcus.Portal.Api/
  Infrastructure/Database/
    PortalDbContext.cs
    DesignTimeDbContextFactory.cs           # for `dotnet ef` tooling
    Migrations/
  Features/Auth/
    User.cs                                  # entity
    UserConfiguration.cs                     # : IEntityTypeConfiguration<User>
    TotpSecret.cs
    TotpSecretConfiguration.cs
    RecoveryCode.cs
    RecoveryCodeConfiguration.cs
  Features/CloudManagement/
    Cloud.cs
    CloudConfiguration.cs
  Features/Provisioning/
    ProvisioningJob.cs
    ProvisioningJobConfiguration.cs
    ...
```

`PortalDbContext.cs`:

```csharp
public sealed class PortalDbContext(DbContextOptions<PortalDbContext> options) : DbContext(options)
{
    public DbSet<User>                   Users                  => Set<User>();
    public DbSet<TotpSecret>             TotpSecrets            => Set<TotpSecret>();
    public DbSet<RecoveryCode>           RecoveryCodes          => Set<RecoveryCode>();
    public DbSet<Cloud>                  Clouds                 => Set<Cloud>();
    public DbSet<PluginToken>            PluginTokens           => Set<PluginToken>();
    public DbSet<ProvisioningJob>        ProvisioningJobs       => Set<ProvisioningJob>();
    public DbSet<EncryptedProviderToken> EncryptedProviderTokens => Set<EncryptedProviderToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PortalDbContext).Assembly);
    }
}
```

A per-feature configuration:

```csharp
// Features/Auth/UserConfiguration.cs
public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.GoogleSubject).IsRequired();
        b.HasIndex(x => x.GoogleSubject).IsUnique();
        b.HasOne(x => x.TotpSecret).WithOne().HasForeignKey<TotpSecret>(x => x.UserId);
    }
}
```

The job-claim raw-SQL escape hatch:

```csharp
public async Task<ProvisioningJob?> ClaimNextAsync(string workerId, TimeSpan lease, CancellationToken ct)
{
    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
    var job = await db.ProvisioningJobs
        .FromSqlInterpolated($@"
            SELECT * FROM provisioning_jobs
             WHERE status = 'pending'
                OR (status = 'in_progress' AND lease_expires < now())
             ORDER BY created_at
             FOR UPDATE SKIP LOCKED
             LIMIT 1")
        .SingleOrDefaultAsync(ct);

    if (job is null) { await tx.CommitAsync(ct); return null; }

    job.Status = "in_progress";
    job.WorkerId = workerId;
    job.LeaseExpires = DateTime.UtcNow + lease;
    job.Attempts++;
    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    return job;
}
```

## Consequences

- **Positive:**
  - All migrations in one assembly; `dotnet ef migrations add` and `dotnet ef database update` work without `--project` indirection.
  - Cross-feature queries (e.g., "list user's clouds with active plugin-token counts") are one LINQ expression.
  - Per-feature configuration co-location preserves VSA's ownership model — opening `Features/Auth/` shows you the User entity, its DB shape, and its endpoints all together.
  - Single transaction scope for state transitions that span features (e.g., "create cloud + enqueue provisioning job" lands as one `SaveChangesAsync`).
  - `FromSqlInterpolated` is the idiomatic EF Core escape hatch; no second ORM to learn or coordinate.
- **Negative:**
  - `PortalDbContext` accumulates a `DbSet<T>` per entity. For ~10 entities this is fine; if it ever grows past 30 entities, splitting is a reasonable refactor.
  - The `FOR UPDATE SKIP LOCKED` SQL is provider-specific. Migration to another DB would require rewriting it.
- **Neutral:**
  - Migration runner pattern (run on startup vs sidecar container vs CLI) deferred to PORTAL-002.

## Related

- [[0018-portal-api-minimal-apis-vsa]] — Features/ folder ownership of entities and configurations
- [[0019-background-work-and-saga-durability]] — job-claim query that uses `FromSqlInterpolated`
- [[0023-test-stack]] — `PortalDbContext` is the single context migrated in test fixtures
