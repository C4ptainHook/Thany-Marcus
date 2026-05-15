# ADR-0023: Test stack — WebApplicationFactory + Testcontainers + xUnit v3 + Shouldly

Status: Accepted
Date: 2026-05-15

## Context

Portal.Api's design depends on Postgres-specific behavior in load-bearing ways:

- The job queue ([[0019-background-work-and-saga-durability]]) uses `SELECT ... FOR UPDATE SKIP LOCKED`. This is the entire reason we chose Postgres-backed queue over Hangfire — and it has no SQLite/in-memory equivalent.
- The Terraform `pg` backend runs in the same Postgres instance.
- Argon2id key-derivation and AES-GCM token storage (PORTAL-005) are crypto-heavy; correctness without tests is malpractice.
- Recovery codes (PORTAL-006) have single-use semantics enforced via unique constraint and redeemed-at timestamp — only verifiable against real Postgres.

Tests that don't exercise real Postgres semantics ship a delta between "what tests verify" and "what runs in prod." For a thesis that *specifically argues for* Postgres-native job queue elegance, testing against SQLite would be false advertising.

Two .NET test-library decisions in 2024–2025 have downstream consequences worth surfacing:

- **MediatR** moved to commercial licensing in January 2025 — already addressed in [[0018-portal-api-minimal-apis-vsa]].
- **FluentAssertions v8** moved to commercial licensing in January 2025. v7 and below stay MIT but receive no updates.

## Options considered

### Database under test

- **A. `Microsoft.EntityFrameworkCore.InMemory`.** Fake provider, no SQL, weak constraint enforcement. Microsoft's own docs explicitly recommend against it for testing.
- **B. SQLite in-memory.** Real SQL, but it's SQLite. No `FOR UPDATE SKIP LOCKED`, no JSONB, no pgvector. Job-queue tests cannot verify locking. Net: tests pass against a fiction.
- **C. Embedded Postgres binary** (`PostgresFan.EmbeddedPostgres` and similar). Real Postgres, no Docker required. Ships a ~100 MB binary per platform; less battle-tested in CI; no trivial pgvector install.
- **D. Testcontainers.PostgreSql.** Real Postgres in a Docker container, started on test-collection setup, torn down at end. Requires Docker daemon. ~3–5s first start, ~0.5s subsequent.
- **E. Shared Postgres + Respawn.** One Postgres running locally and in CI; `Respawn` library wipes tables between tests. Fast (no per-test container start). Shared state ⇒ parallelism is tricky.

### Host wrapper

- **A. `WebApplicationFactory<TEntryPoint>`** from `Microsoft.AspNetCore.Mvc.Testing`. Hosts the API in-process for integration tests; reaches endpoints over `HttpClient` without a real socket.
- (No realistic alternative; this is the framework-provided pattern.)

### Assertion library

- **A. Shouldly.** Long-established, MIT, comparable ergonomics (`result.ShouldBe(...)`, `Should.Throw<>()`).
- **B. FluentAssertions.** Familiar to many, but v8 (Jan 2025) is commercial.
- **C. AwesomeAssertions.** Community fork of FluentAssertions v7, Apache 2.0.
- **D. Plain `xUnit.Assert.*`.** Uglier, zero dependencies, never an issue.

### Test framework

- **A. xUnit v3** (released 2025). Modern; better collection-fixture story; familiar `[Fact]`/`[Theory]`.
- **B. NUnit.** Fine, less common in modern .NET community.

## Decision

- **Database: D (Testcontainers.PostgreSql).** One container per xUnit `CollectionFixture` (shared across all tests in the collection).
- **Host wrapper: WebApplicationFactory<Program>.**
- **Assertions: Shouldly.**
- **Framework: xUnit v3.**

### Concrete shape

`tests/ThanyMarcus.Portal.Tests/Infrastructure/PostgresFixture.cs`:

```csharp
public sealed class PostgresFixture : IAsyncLifetime
{
    public PostgreSqlContainer Container { get; } = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        // Run EF Core migrations against Container.GetConnectionString().
        using var scope = BuildHost().Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<PortalDbContext>().Database.MigrateAsync();
    }

    public Task DisposeAsync() => Container.DisposeAsync().AsTask();
}

[CollectionDefinition("Postgres")]
public class PostgresCollection : ICollectionFixture<PostgresFixture> { }
```

`tests/ThanyMarcus.Portal.Tests/Infrastructure/PortalApiFactory.cs`:

```csharp
public sealed class PortalApiFactory : WebApplicationFactory<Program>
{
    public PortalApiFactory(PostgresFixture pg) => this.pg = pg;

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureAppConfiguration((_, cfg) =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Portal"] = pg.Container.GetConnectionString(),
            }));
}
```

Per-test isolation via transactions that roll back at end of test, or `Respawn` invoked in `DisposeAsync` of a per-test fixture — to be decided when first tests land in PORTAL-002 (both patterns are well-trodden).

A test:

```csharp
[Collection("Postgres")]
public sealed class CreateCloudTests : IClassFixture<PortalApiFactory>
{
    private readonly PortalApiFactory factory;
    public CreateCloudTests(PortalApiFactory factory) => this.factory = factory;

    [Fact]
    public async Task POST_clouds_with_valid_token_enqueues_provisioning_job()
    {
        var client = factory.CreateClient();
        // ... seed an authenticated session ...
        var res = await client.PostAsJsonAsync("/api/clouds",
            new { provider = "digitalocean", region = "fra1", sshKey = "..." });

        res.StatusCode.ShouldBe(HttpStatusCode.Created);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var jobs = await db.ProvisioningJobs.ToListAsync();
        jobs.ShouldHaveSingleItem();
        jobs[0].Status.ShouldBe("pending");
    }
}
```

### NuGet references in `ThanyMarcus.Portal.Tests.csproj`

```
xunit.v3
xunit.runner.visualstudio
Microsoft.AspNetCore.Mvc.Testing
Testcontainers.PostgreSql
Shouldly
Microsoft.NET.Test.Sdk
```

## Consequences

- **Positive:**
  - Job-queue and crash-resume tests run against the exact Postgres semantics they depend on. The thesis claim "we use `FOR UPDATE SKIP LOCKED` for elegant single-process saga durability" is verifiable, not aspirational.
  - The same Testcontainers pattern carries to `Cloud.Api` (which will need pgvector) — `new PostgreSqlBuilder().WithImage("pgvector/pgvector:pg16")` and the rest is identical.
  - No commercial-license footnote anywhere in the test stack.
  - xUnit v3's `CollectionFixture` model fits the "one container per test session" pattern cleanly.
- **Negative:**
  - Docker daemon required for tests. The developer machine is macOS where OrbStack/Docker Desktop/Rancher is already in the stack (the portal ships via Docker Compose), so this is not a new dependency.
  - First container start adds ~3–5s to test-session warmup. Amortized over a session by sharing across all tests in the collection.
- **Neutral:**
  - Per-test reset strategy (transaction rollback vs Respawn) deferred to PORTAL-002 — both are standard and the choice can be made per-test-class.

## Related

- [[0018-portal-api-minimal-apis-vsa]] — what gets tested (endpoint handlers, not framework abstractions)
- [[0019-background-work-and-saga-durability]] — the Postgres-specific behavior the test stack must verify
- [[0024-dbcontext-shape]] — single `PortalDbContext` makes test setup symmetric
- [[0022-solution-scope-and-layout]] — `Portal.Tests` lives at `tests/ThanyMarcus.Portal.Tests/`
