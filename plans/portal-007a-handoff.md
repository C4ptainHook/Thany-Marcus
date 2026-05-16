# PORTAL-007a SagaWorker scaffold — Handoff Brief

**Goal:** extract a new `ThanyMarcus.Portal.SagaWorker` project that runs as its own container in a new `docker-compose.yml`, shares Postgres + the data-protection key ring with `Portal.Api`, and runs an empty `BackgroundService` that's claim-loop-ready but doesn't yet handle saga phases. **No saga logic.** Pure structural prerequisite for PORTAL-007 — the actual saga code lands there.

Estimated **0.5 person-day** with heavy AI-agent assistance.

## Where decisions live (read before doing anything)

- **`docs/decisions/0033-provisioning-saga-and-worker.md` §"Worker model — claim loop"** — the canonical `SagaWorker` shape with `SemaphoreSlim(3)` bounded parallelism and the SKIP LOCKED claim query. This ticket lands the *scaffold*; the claim query and phase dispatch land in PORTAL-007.
- **`docs/decisions/0022-solution-scope-and-layout.md`** — solution layout conventions (`src/`, project naming).
- **`docs/decisions/0028-schema-conventions.md`** — DbContext registration shape (`UseNpgsql + UseNodaTime + UseSnakeCaseNamingConvention + TimestampInterceptor`).
- **`docs/decisions/0026-observability-and-health-checks.md`** — OpenTelemetry resource service name should differ from Portal.Api's (`ThanyMarcus.Portal.SagaWorker` not `ThanyMarcus.Portal.Api`).
- **`plans/portal-003f-handoff.md`** — landed first; this ticket assumes its outcomes (DP key ring persistent on filesystem, Postgres-backed `IInfraOpUnlockCache`, ForwardedHeaders middleware in Portal.Api — the worker doesn't need it because no HTTP).
- **Memory files**: `portal_deployment.md` (single-VM Docker Compose), `portal_tooling.md` (.NET 10, warnings-as-errors, OTel + console exporter for dev).

**Do not implement saga logic in this ticket.** The empty `BackgroundService` is intentional. PORTAL-007 adds the claim query, phase handlers, and terraform invocation; this ticket exists so PORTAL-007 lands cleanly without restructuring the solution mid-implementation.

## Output of PORTAL-007a — final directory state

```
Thany-Marcus/
├── ThanyMarcus.slnx                                  # adds the new SagaWorker project
├── src/
│   ├── ThanyMarcus.Portal.Api/                       # unchanged
│   └── ThanyMarcus.Portal.SagaWorker/                # NEW
│       ├── ThanyMarcus.Portal.SagaWorker.csproj      # Web SDK NOT required; use Microsoft.NET.Sdk.Worker
│       ├── Program.cs                                # composition: DbContext + IClock + DataProtection + OTel + SagaWorker
│       ├── appsettings.json                          # connection string + DP key ring path config
│       ├── appsettings.Development.json              # local-dev overrides (ports, paths)
│       ├── SagaWorker.cs                             # the BackgroundService — empty ExecuteAsync for now; PORTAL-007 fills it
│       └── Properties/launchSettings.json            # ASPNETCORE_ENVIRONMENT etc.
├── docker-compose.yml                                # NEW: portal-api + saga-worker + postgres + caddy stack
├── docker-compose.override.yml                       # NEW: dev overrides (bind-mount source, expose ports)
├── infra/docker/portal-api/Dockerfile                # NEW (or check infra/ dir): builds Portal.Api image
├── infra/docker/saga-worker/Dockerfile               # NEW: builds SagaWorker image (terraform binary included)
└── tests/
    └── ThanyMarcus.Portal.Tests/
        └── SagaWorker/
            └── SagaWorkerSmokeTests.cs               # NEW: starts the worker host, asserts ExecuteAsync runs and stops cleanly
```

## Packages to add (`Directory.Packages.props`)

The Worker SDK transitively brings most of what's needed. Confirm these are present (if not, add):

- `Microsoft.Extensions.Hosting` — already present via existing Worker references / Portal.Api
- `Microsoft.EntityFrameworkCore`, `Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime`, `EFCore.NamingConventions` — already present
- OpenTelemetry packages — already present

No new `PackageVersion` entries needed.

`ThanyMarcus.Portal.SagaWorker.csproj` adds (no `Version` attr — central package management):

```xml
<Project Sdk="Microsoft.NET.Sdk.Worker">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UserSecretsId>thany-marcus-saga-worker</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime" />
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="NodaTime" />
    <PackageReference Include="OpenTelemetry" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Process" />
    <PackageReference Include="OpenTelemetry.Exporter.Console" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ThanyMarcus.Portal.Api\ThanyMarcus.Portal.Api.csproj" />
  </ItemGroup>
</Project>
```

**`ProjectReference` to `Portal.Api` is deliberate.** The SagaWorker reuses `PortalDbContext`, `TimestampInterceptor`, `IInfraOpUnlockCache`, `PassphraseService` (read-only access to user envelope columns), and the entities themselves. Until PORTAL-007 splits the shared domain into a `ThanyMarcus.Shared` library (which it may or may not need), the worker pulls them directly from Portal.Api. This is *not ideal long-term* — it creates a build dependency from worker → web — but it's the cheapest first step. **PORTAL-007 may refactor** to extract a `ThanyMarcus.Portal.Domain` project if the worker-vs-API split becomes painful; this handoff doesn't force that move.

## SagaWorker `Program.cs`

`src/ThanyMarcus.Portal.SagaWorker/Program.cs`:

```csharp
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Infrastructure.Database;
using ThanyMarcus.Portal.SagaWorker;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes  = true;
    o.UseUtcTimestamp = true;
});

// --- OpenTelemetry (per ADR-0026) ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "ThanyMarcus.Portal.SagaWorker",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
    .WithMetrics(m => m
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddConsoleExporter())
    .WithTracing(t => t
        .AddConsoleExporter());

// --- Time ---
builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
builder.Services.AddSingleton<TimestampInterceptor>();

// --- Database (same connection string as Portal.Api) ---
builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Portal")
            ?? throw new InvalidOperationException("ConnectionStrings:Portal not configured"),
        npg => npg.UseNodaTime())
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

// --- Data protection (shared key ring with Portal.Api) ---
var dpKeysDir = builder.Configuration["DataProtection:KeyRingPath"]
    ?? throw new InvalidOperationException("DataProtection:KeyRingPath not configured (required for cross-process key sharing)");
Directory.CreateDirectory(dpKeysDir);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
    .SetApplicationName("ThanyMarcus.Portal");      // MUST match Portal.Api's value so they share the key ring

// --- Shared services from Portal.Api ---
builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();

// --- The worker itself ---
builder.Services.AddHostedService<SagaWorker>();

var host = builder.Build();
await host.RunAsync();

public partial class Program;
```

**Notes:**
- `Host.CreateApplicationBuilder` not `WebApplication.CreateBuilder` — this is a worker, no HTTP.
- Console-only OTel exporters in dev; the SagaWorker doesn't expose a `/metrics` endpoint (no HTTP). Production observability is a follow-up — could push to an OTel collector in the compose stack later, or scrape via a sidecar. Out of scope here.
- `SetApplicationName("ThanyMarcus.Portal")` **must exactly match Portal.Api**. ASP.NET Core derives the key isolation from this string; a typo means the two processes have separate key rings and can't decrypt each other's blobs.
- `DataProtection:KeyRingPath` is **required**, not defaulted — the worker must be told where to find the key ring (no `ContentRoot` convention shared with Portal.Api).
- No auth/cookie/wizard concerns; the worker is process-internal.

## SagaWorker.cs (placeholder)

`src/ThanyMarcus.Portal.SagaWorker/SagaWorker.cs`:

```csharp
namespace ThanyMarcus.Portal.SagaWorker;

/// <summary>
/// Placeholder claim loop. PORTAL-007 lands the real claim query + phase dispatch +
/// SemaphoreSlim(3) parallelism per ADR-0033.
/// </summary>
public sealed class SagaWorker(ILogger<SagaWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("SagaWorker started (scaffold; no saga logic yet — see PORTAL-007)");
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                log.LogTrace("SagaWorker tick (no-op)");
            }
        }
        catch (OperationCanceledException)
        {
            log.LogInformation("SagaWorker stopping");
        }
    }
}
```

Intentionally empty. The log line on tick is removable in PORTAL-007.

## docker-compose.yml

`docker-compose.yml` at repo root:

```yaml
services:
  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: postgres
      POSTGRES_PASSWORD: postgres
      POSTGRES_DB: portal_dev
    volumes:
      - pg_data:/var/lib/postgresql/data
    networks: [portal]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U postgres -d portal_dev"]
      interval: 5s
      timeout: 5s
      retries: 10

  portal-api:
    build:
      context: .
      dockerfile: infra/docker/portal-api/Dockerfile
    restart: unless-stopped
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Portal: Host=postgres;Port=5432;Username=postgres;Password=postgres;Database=portal_dev
      DataProtection__KeyRingPath: /var/lib/portal/data-protection-keys
      Google__ClientId:     ${GOOGLE_CLIENT_ID}
      Google__ClientSecret: ${GOOGLE_CLIENT_SECRET}
    volumes:
      - dp_keys:/var/lib/portal/data-protection-keys
    networks: [portal]
    depends_on:
      postgres:
        condition: service_healthy
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/health/ready || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5

  saga-worker:
    build:
      context: .
      dockerfile: infra/docker/saga-worker/Dockerfile
    restart: unless-stopped
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ConnectionStrings__Portal: Host=postgres;Port=5432;Username=postgres;Password=postgres;Database=portal_dev
      DataProtection__KeyRingPath: /var/lib/portal/data-protection-keys
    volumes:
      - dp_keys:/var/lib/portal/data-protection-keys
      - terraform_data:/var/lib/portal/terraform           # claimed in PORTAL-007 for workspace + plugin cache; reserved here
    networks: [portal]
    depends_on:
      postgres:
        condition: service_healthy
    deploy:
      resources:
        limits:
          cpus: '1.5'
          memory: 4G

  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports: ["80:80", "443:443"]
    volumes:
      - ./infra/docker/caddy/Caddyfile:/etc/caddy/Caddyfile
      - caddy_data:/data
      - caddy_config:/config
    networks: [portal]
    depends_on:
      portal-api:
        condition: service_healthy

volumes:
  pg_data:
  dp_keys:                                # SHARED between portal-api and saga-worker; this is the DP key ring
  terraform_data:                         # reserved for PORTAL-007
  caddy_data:
  caddy_config:

networks:
  portal:
```

`docker-compose.override.yml` (dev — gitignored if you want, or committed for dev parity):

```yaml
services:
  portal-api:
    ports: ["5000:8080"]                  # expose API directly during dev
    environment:
      ASPNETCORE_ENVIRONMENT: Development

  saga-worker:
    environment:
      ASPNETCORE_ENVIRONMENT: Development

  caddy:
    profiles: [prod]                      # skip caddy in dev; access portal-api directly on :5000

  postgres:
    ports: ["5432:5432"]                  # expose for psql / pgAdmin
```

## Dockerfiles

`infra/docker/portal-api/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj", "src/ThanyMarcus.Portal.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",         "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj"
COPY src/ src/
RUN dotnet publish "src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj" -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ThanyMarcus.Portal.Api.dll"]
```

`infra/docker/saga-worker/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj", "src/ThanyMarcus.Portal.SagaWorker/"]
COPY ["src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj",               "src/ThanyMarcus.Portal.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",                       "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj"
COPY src/ src/
RUN dotnet publish "src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj" -c Release -o /app /p:UseAppHost=false

# Pull terraform binary into final image (reserved for PORTAL-007; included now so the worker is shape-correct)
FROM hashicorp/terraform:1.7 AS terraform

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
COPY --from=terraform /bin/terraform /usr/local/bin/terraform
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "ThanyMarcus.Portal.SagaWorker.dll"]
```

The terraform binary copy is reserved-for-PORTAL-007 (the worker doesn't use it yet) but landing it here means PORTAL-007's image change is just config, not a Dockerfile edit. Image is ~150 MB final (.NET 10 runtime + terraform).

`infra/docker/caddy/Caddyfile` — placeholder, just enough to route in dev:

```text
{
    auto_https off                          # dev mode; prod overrides via env or replaces this file
}

:80, :443 {
    reverse_proxy portal-api:8080
}
```

PORTAL-017 will replace this with the production Caddyfile (LE + custom domain). Acceptable stub here.

## Solution file update

`ThanyMarcus.slnx` — add the new project. Run `dotnet sln ThanyMarcus.slnx add src/ThanyMarcus.Portal.SagaWorker/`. Verify the project appears in `dotnet sln list`.

## Smoke test

`tests/ThanyMarcus.Portal.Tests/SagaWorker/SagaWorkerSmokeTests.cs`:

```csharp
[Collection(PostgresCollection.Name)]
public sealed class SagaWorkerSmokeTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Worker_starts_and_stops_cleanly()
    {
        var builder = Host.CreateApplicationBuilder();
        var dpKeysDir = Path.Combine(Path.GetTempPath(), "saga-worker-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dpKeysDir);

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Portal"]         = postgres.ConnectionString,
            ["DataProtection:KeyRingPath"]       = dpKeysDir,
        });

        builder.Services.AddSingleton<IClock>(NodaTime.SystemClock.Instance);
        builder.Services.AddSingleton<TimestampInterceptor>();
        builder.Services.AddDbContext<PortalDbContext>((sp, opts) => opts
            .UseNpgsql(postgres.ConnectionString, npg => npg.UseNodaTime())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));
        builder.Services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(dpKeysDir))
            .SetApplicationName("ThanyMarcus.Portal");
        builder.Services.AddScoped<IInfraOpUnlockCache, PostgresInfraOpUnlockCache>();
        builder.Services.AddHostedService<ThanyMarcus.Portal.SagaWorker.SagaWorker>();

        using var host = builder.Build();
        await host.StartAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(200));        // worker ticks once
        await host.StopAsync();

        Directory.Delete(dpKeysDir, recursive: true);
    }
}
```

Proves: composition compiles, DbContext resolves against the Testcontainers Postgres, DP keys land in the temp dir, the hosted service starts and stops cleanly. This is the smoke harness PORTAL-007 will expand with phase-handler tests.

## Acceptance criteria

- `dotnet build` succeeds with **zero warnings**.
- `dotnet sln list` shows the new `ThanyMarcus.Portal.SagaWorker` project alongside Portal.Api, Shared, and Portal.Tests.
- `dotnet run --project src/ThanyMarcus.Portal.SagaWorker` against a configured connection string starts the host, logs "SagaWorker started", ticks the no-op log every 5s, and exits cleanly on Ctrl+C.
- `dotnet run --project src/ThanyMarcus.Portal.Api` still starts and works exactly as before (no behavior change to the API).
- `docker compose build` (or `docker buildx bake` if you prefer) produces both images without error. `docker compose up -d` brings the full stack online: postgres → portal-api (health check passes) → saga-worker → caddy.
- `docker compose logs saga-worker` shows the "SagaWorker started" line and periodic ticks.
- `docker compose exec portal-api ls -la /var/lib/portal/data-protection-keys/` and `docker compose exec saga-worker ls -la /var/lib/portal/data-protection-keys/` show the **same files** (proves the volume is shared).
- `docker compose down -v` cleans up volumes; subsequent `docker compose up -d` from scratch works (migrations run on portal-api boot per existing PORTAL-002 wiring; worker eventually sees the schema once it boots after postgres health passes).
- `SagaWorkerSmokeTests.Worker_starts_and_stops_cleanly` passes.
- Per-instance OpenTelemetry resource service name is `ThanyMarcus.Portal.SagaWorker`, distinguishable from `ThanyMarcus.Portal.Api` in logs.
- The terraform binary is present in the saga-worker image: `docker compose exec saga-worker terraform version` prints a version (e.g., `Terraform v1.7.x`).

## Concrete steps in order

1. **Create the project** — `dotnet new worker -n ThanyMarcus.Portal.SagaWorker -o src/ThanyMarcus.Portal.SagaWorker`. Delete the generated `Worker.cs` and `appsettings.json` (we replace them).

2. **Wire `csproj`** — replace generated csproj with the version above (Worker SDK, ProjectReference to Portal.Api, package references). Run `dotnet restore`.

3. **Add to solution** — `dotnet sln ThanyMarcus.slnx add src/ThanyMarcus.Portal.SagaWorker/`.

4. **Land `Program.cs` + `SagaWorker.cs`** — the empty placeholder shape above. Build; should succeed.

5. **Local smoke** — `dotnet run --project src/ThanyMarcus.Portal.SagaWorker` against the dev Postgres (you may need a temp `appsettings.Development.json` with the connection string locally). Verify the "SagaWorker started" log, let it tick, Ctrl+C.

6. **Land `SagaWorkerSmokeTests`** — confirm it passes against Testcontainers Postgres.

7. **Land the Dockerfiles** — `infra/docker/portal-api/Dockerfile`, `infra/docker/saga-worker/Dockerfile`. Verify each builds in isolation: `docker build -f infra/docker/portal-api/Dockerfile .` then same for saga-worker.

8. **Land `docker-compose.yml` + `docker-compose.override.yml` + `Caddyfile`** — `docker compose up -d` from scratch. Verify all four services come up healthy.

9. **Volume-sharing check** — exec into both containers, confirm the same data-protection-keys directory contents.

10. **Stop / start cycle** — `docker compose down` then `docker compose up -d`. Verify Portal.Api's cookies and TOTP secrets still validate (the volume preserves the DP key ring).

11. **Verify all acceptance criteria** — every bullet. Commit only after all pass.

## Out of scope (do not touch)

- **Saga claim query + phase handlers** — PORTAL-007.
- **`ITerraformRunner` / `ICloudflareDnsClient` / `IPortalToCloudClient` abstractions** — PORTAL-007.
- **Terraform workspace setup on the volume** — PORTAL-007.
- **Plugin cache pre-warm** — PORTAL-007.
- **`provisioning_jobs.next_visible_at` migration** — PORTAL-007.
- **`events_log` jsonb column on `provisioning_jobs`** — PORTAL-007 (or PORTAL-005-adjacent if needed earlier).
- **`SemaphoreSlim(3)` parallelism logic** — PORTAL-007.
- **CI workflow to push images to GHCR** — PORTAL-017 territory.
- **Production Caddyfile with LE + custom domain** — PORTAL-017.
- **Hostname-based routing of /api/* and /admin/* externally** — PORTAL-017.

## Risks & gotchas

- **`SetApplicationName` mismatch silently breaks DP key sharing.** ASP.NET Core's data-protection key isolation derives from this string. Portal.Api: `"ThanyMarcus.Portal"`. SagaWorker: `"ThanyMarcus.Portal"`. **Identical.** A typo or accidental capitalization difference makes the two processes unable to decrypt each other's blobs — cookies will validate fine on Portal.Api but the SagaWorker can't read DEKs from `step_up_unlocks`, every infra op fails with "step-up required". If you change one, change the other; better yet, lift the string to a shared `const`.

- **`ProjectReference` to Portal.Api drags in ASP.NET Core types the worker doesn't need.** The worker references Portal.Api's csproj, which is a Web SDK project. .NET will pull `Microsoft.AspNetCore.App` framework reference into the worker's runtime image. Slightly bloats the image (~50 MB) but doesn't otherwise cause issues. If image size matters later, factor out a `ThanyMarcus.Portal.Domain` library and reference *that* from both. For now: accept the bloat.

- **OpenTelemetry resource service name uniqueness.** Both processes need distinct `serviceName` values so traces/logs are attributable. Portal.Api: `"ThanyMarcus.Portal.Api"`. SagaWorker: `"ThanyMarcus.Portal.SagaWorker"`. Don't lift these to a shared const — they're per-process by design.

- **Migrations run from Portal.Api on startup, not from SagaWorker.** Existing wiring in Portal.Api's `Program.cs` runs `await db.Database.MigrateAsync()` on boot. The SagaWorker should **not** also run migrations — race condition on shared schema, and migration source-of-truth should be the API. Worker just connects to whatever schema is current. If you find yourself adding `MigrateAsync` to the worker, stop.

- **`docker compose up` ordering.** `depends_on` with `service_healthy` means saga-worker waits for postgres but **not** for portal-api (which is the one running migrations). On first ever boot, saga-worker could connect before migrations finish and EF Core would throw on missing tables. Mitigation: the smoke worker's no-op loop doesn't touch the DB yet, so it survives. PORTAL-007's claim query will need a retry-on-startup pattern (or `depends_on portal-api: service_healthy`, accepting that the worker waits an extra ~10s on cold start).

- **`docker compose down -v` deletes the DP key ring too.** It's on a Docker volume. Acceptable in dev. In production, the volume must be backed by a host-bind-mount that doesn't get wiped — document this when PORTAL-017 lands the prod compose file.

- **Worker SDK vs. Web SDK.** Don't `dotnet new web` for the SagaWorker — that brings Kestrel + HTTP + the wrong base image. `dotnet new worker` is the right template (uses `Microsoft.NET.Sdk.Worker`).

- **`UserSecretsId` collision.** Pick a different value from Portal.Api's. Worker template defaults to a guid; keep that or set a stable name like `thany-marcus-saga-worker`.

- **`launchSettings.json` profile.** Default Worker template generates one; verify it sets `DOTNET_ENVIRONMENT` (note: not `ASPNETCORE_ENVIRONMENT`, which is web-only) for local debug runs.

- **Cleanup on test teardown.** The smoke test creates a temp DP-keys directory under `Path.GetTempPath()`. Make sure the cleanup runs even on test failure — use a `try/finally` or `IDisposable` pattern. Otherwise CI temp dirs leak.

- **Image size and CI build time.** The terraform binary copy adds ~30 MB to the saga-worker image. CI build time goes up by ~30s on cold builds. Acceptable. If it becomes a problem, switch to a smaller base (`alpine` variant of the .NET runtime) — but that complicates the terraform binary's glibc dependency. Stay on the standard `mcr.microsoft.com/dotnet/runtime:10.0` for now.

## Definition of done

All acceptance criteria pass + `git status` shows the new project + Dockerfiles + docker-compose files + slnx update + `docker compose up -d` from scratch produces a fully-up stack with the worker logging ticks. Tasks marked completed. PORTAL-007 can pick up from here and add the claim query + phase handlers without restructuring the solution.

A fresh agent picking up PORTAL-007 from this state knows:
- The worker project exists with DI for `PortalDbContext`, `IClock`, `IInfraOpUnlockCache`, `IDataProtectionProvider`, OTel.
- The worker container has the terraform binary at `/usr/local/bin/terraform`.
- A persistent `terraform_data` volume is mounted at `/var/lib/portal/terraform` and reserved for PORTAL-007 to use.
- The DP key ring volume is shared with Portal.Api.
- Docker Compose is the deployment unit; CI/CD wires this up later.

## Cross-references

- **PORTAL-003f** — prerequisite (lands `step_up_unlocks` table + `PostgresInfraOpUnlockCache` + DP key persistence). PORTAL-007a cannot ship until PORTAL-003f does.
- **PORTAL-007** — direct successor. This handoff explicitly defers all saga logic to PORTAL-007; the file shape and DI registrations here exist so PORTAL-007 is a code-only change, not a structural one.
- **PORTAL-017** — replaces the placeholder Caddyfile and adds production hardening (LE, custom domain, GHCR image pulls in CI/CD).
- **ADR-0033** — defines the saga shape and worker model the placeholder skeleton implements scaffold for.
