# CLOUD-001 Scaffold `ThanyMarcus.Cloud.Api` — Handoff Brief

**Goal:** stand up the `ThanyMarcus.Cloud.Api` project (ASP.NET Core, .NET 10, Minimal APIs), replace the `nginx:alpine` placeholder in `infra/docker/cloud/docker-compose.yml` with the real Cloud.Api image, add Postgres to the cloud-side control-plane stack, and implement **only** `/admin/health` per ADR-0034 + `/health/live` + `/health/ready`. No business logic. CLOUD-002 lands the schema, CLOUD-003 the auth, CLOUD-004/005 the plugin/admin endpoints.

Estimated **0.25 person-day** with AI-agent assistance.

## What this ticket is

CLOUD-001 is the cloud-side counterpart to PORTAL-007a (worker scaffold). It introduces the third .NET project in the solution (`Portal.Api`, `Portal.SagaWorker`, `Cloud.Api`) and the second Docker Compose stack (`infra/docker/portal/`, `infra/docker/cloud/`).

It exists so CLOUD-002/003/004/005 + PLUGIN-003 + the saga's `awaiting_cloud_callback` → `awaiting_cert` → `succeeded` transition are all unblocked simultaneously, in parallel.

**Without CLOUD-001, PORTAL-011a's smoke cannot reach `succeeded`** even after F1 lands the real cloud-init template. The template's `docker compose up` does `docker pull ghcr.io/bboiko/thany-cloud-api:latest` — that image must exist. CLOUD-001 makes it exist.

## Where decisions live (read before doing anything)

- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — pins `/admin/health` payload, no-auth posture on that endpoint, registration-status semantics. **This is the contract CLOUD-001 must implement.**
- **`docs/decisions/0027-reverse-proxy-caddy.md`** — Caddy on every user cloud; Cloud.Api sits behind Caddy on `:8080`.
- **`docs/decisions/0022-solution-scope-and-layout.md`** — solution layout (`src/`, project naming).
- **`docs/decisions/0026-observability-and-health-checks.md`** — `/health/live` + `/health/ready`, OTel + Prometheus exporter.
- **`docs/decisions/0028-schema-conventions.md`** — DbContext shape (`UseNpgsql + UseNodaTime + UseSnakeCaseNamingConvention + TimestampInterceptor`); applied here for the registration but **no entities/migrations yet** — CLOUD-002 lands those.
- **`plans/cloud-pivot-plan-2026-05-13.md §27`** — directory layout for `ThanyMarcus.Cloud.Api/Features/...`. Land the *directories* in CLOUD-001 (empty placeholder `.gitkeep`s); fill them in CLOUD-003 onward.
- **`plans/tickets-2026-05-13.md`** — CLOUD-001 row: "api + postgres + caddy on the 4 GB control plane. Ollama + Parakeet live in the burst worker stack (separate compose file), not here." DEC-001 (control plane = 2 vCPU/4 GB), DEC-005 (GHCR for image distribution).
- **Memory files**: `portal_tooling.md` (.NET 10, central pkg mgmt, warnings-as-errors, OTel+Prometheus, Shouldly+xUnit v3, OpenAPI+Scalar, /health/live+ready), `portal_deployment.md` (single-VM Docker Compose), `portal_architecture.md` (Minimal APIs + VSA). Cloud.Api inherits **all** of these conventions.

**Do not implement business logic in this ticket.** No plugin endpoints with real handlers (CLOUD-004), no admin endpoints beyond `/admin/health` (CLOUD-005), no DB entities (CLOUD-002), no bearer-token auth (CLOUD-003).

## Output of CLOUD-001 — final directory state

```
Thany-Marcus/
├── ThanyMarcus.slnx                                # ADDS Cloud.Api + Cloud.Tests
├── src/
│   ├── ThanyMarcus.Portal.Api/                     # unchanged
│   ├── ThanyMarcus.Portal.SagaWorker/              # unchanged
│   ├── ThanyMarcus.Portal.Web/                     # unchanged
│   ├── ThanyMarcus.Shared/                         # unchanged
│   └── ThanyMarcus.Cloud.Api/                      # NEW
│       ├── ThanyMarcus.Cloud.Api.csproj            # Web SDK
│       ├── Program.cs                              # composition: DbContext + IClock + OTel + endpoints
│       ├── appsettings.json
│       ├── appsettings.Development.json
│       ├── Features/                               # mirrors pivot-plan §27 layout
│       │   ├── PluginAuth/.gitkeep                 # CLOUD-003 lands here
│       │   ├── Ingest/.gitkeep                     # CLOUD-004 lands here
│       │   ├── Processing/.gitkeep
│       │   ├── Knowledge/.gitkeep
│       │   ├── Sync/.gitkeep                       # CLOUD-004
│       │   ├── Sharing/.gitkeep
│       │   ├── Settings/.gitkeep
│       │   └── Admin/
│       │       └── Health/
│       │           ├── AdminHealthEndpoint.cs      # the one real endpoint per ADR-0034
│       │           └── AdminHealthState.cs         # reads cert + registration state from /etc/thany-cloud/state.json
│       └── Infrastructure/
│           └── Database/
│               └── CloudDbContext.cs               # empty DbContext (no DbSet<> yet); CLOUD-002 fills it
├── tests/
│   ├── ThanyMarcus.Portal.Tests/                   # unchanged
│   └── ThanyMarcus.Cloud.Tests/                    # NEW
│       ├── ThanyMarcus.Cloud.Tests.csproj
│       ├── Admin/
│       │   └── AdminHealthEndpointTests.cs         # asserts payload shape per ADR-0034
│       └── ScaffoldTests.cs                        # asserts host boots + /health/live returns 200
└── infra/
    └── docker/
        └── cloud/
            ├── Dockerfile                          # NEW: builds Cloud.Api image
            ├── docker-compose.yml                  # UPDATED: nginx:alpine → cloud-api real image + adds postgres
            ├── Caddyfile.tpl                       # unchanged
            ├── cloud.env.tpl                       # unchanged
            └── stub-admin-health/                  # DELETE (replaced by real Cloud.Api)
```

The directory subtree under `Features/` mirrors the pivot plan exactly. `.gitkeep` files are placeholders so the structure is visible at scaffold-time; **each subsequent CLOUD ticket fills its own subdirectory**. This is the same scaffolding discipline that's already kept `Portal.Api/Features/CloudManagement/` cleanly partitioned across PORTAL-007/008/010/011/015/016.

## Packages to add (`Directory.Packages.props`)

Most are already present from Portal.Api / Portal.SagaWorker. New `PackageVersion` entries needed only if any of these aren't already in central package management:

- `Microsoft.AspNetCore.OpenApi` — already present
- `Scalar.AspNetCore` — already present
- `Microsoft.EntityFrameworkCore` — already present
- `Npgsql.EntityFrameworkCore.PostgreSQL`, `Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime`, `EFCore.NamingConventions` — already present
- OpenTelemetry packages incl. `OpenTelemetry.Exporter.Prometheus.AspNetCore` — already present
- `Microsoft.AspNetCore.Diagnostics.HealthChecks` — already present

No new entries expected. Verify before adding.

`ThanyMarcus.Cloud.Api.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <UserSecretsId>thany-marcus-cloud-api</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.OpenApi" />
    <PackageReference Include="Scalar.AspNetCore" />
    <PackageReference Include="Microsoft.EntityFrameworkCore" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL.NodaTime" />
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="NodaTime" />
    <PackageReference Include="Microsoft.AspNetCore.Diagnostics.HealthChecks" />
    <PackageReference Include="OpenTelemetry.Extensions.Hosting" />
    <PackageReference Include="OpenTelemetry.Instrumentation.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Http" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Runtime" />
    <PackageReference Include="OpenTelemetry.Instrumentation.Process" />
    <PackageReference Include="OpenTelemetry.Exporter.Prometheus.AspNetCore" />
    <PackageReference Include="OpenTelemetry.Exporter.Console" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\ThanyMarcus.Shared\ThanyMarcus.Shared.csproj" />
  </ItemGroup>
</Project>
```

**No `ProjectReference` to Portal.Api.** Cloud.Api lives on the user's VPS; Portal.Api lives on the operator's VPS. They share **contracts only**, via `ThanyMarcus.Shared`. Any cross-process types (e.g., `CloudAdminHealthResponse` from ADR-0034) belong in `ThanyMarcus.Shared`; Portal.Api consumes the same DTO when polling `/admin/health`.

## `Program.cs`

`src/ThanyMarcus.Cloud.Api/Program.cs`:

```csharp
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NodaTime;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using ThanyMarcus.Cloud.Api.Features.Admin.Health;
using ThanyMarcus.Cloud.Api.Infrastructure.Database;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes  = true;
    o.UseUtcTimestamp = true;
});

builder.Services.AddOpenApi();

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "ThanyMarcus.Cloud.Api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddProcessInstrumentation()
        .AddPrometheusExporter())
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());

builder.Services.AddSingleton<IClock>(SystemClock.Instance);
builder.Services.AddSingleton<TimestampInterceptor>();

builder.Services.AddDbContext<CloudDbContext>((sp, opts) => opts
    .UseNpgsql(
        builder.Configuration.GetConnectionString("Cloud")
            ?? throw new InvalidOperationException("ConnectionStrings:Cloud not configured"),
        npg => npg.UseNodaTime())
    .UseSnakeCaseNamingConvention()
    .AddInterceptors(sp.GetRequiredService<TimestampInterceptor>()));

builder.Services.AddSingleton<AdminHealthState>();

builder.Services.AddHealthChecks()
    .AddDbContextCheck<CloudDbContext>(
        name: "cloud_db",
        failureStatus: HealthStatus.Unhealthy,
        tags: ["ready"]);

var app = builder.Build();

app.UseForwardedHeaders();

app.MapOpenApi();
app.MapScalarApiReference();
app.MapPrometheusScrapingEndpoint();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false,                  // liveness = process up; no dependency checks
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = r => r.Tags.Contains("ready"),
});

app.MapAdminHealthEndpoint();

app.MapFallback(() => Results.NotFound());

await app.RunAsync();

public partial class Program;
```

**Notes:**
- `serviceName` = `ThanyMarcus.Cloud.Api`, distinct from `ThanyMarcus.Portal.Api`.
- No cookie auth, no Google SSO. Cloud.Api is bearer-token-only on `/api/*` (CLOUD-003) and `cloud_admin_token`-protected on `/admin/*` except `/admin/health` (CLOUD-005). CLOUD-001 lands no auth middleware at all — that's CLOUD-003's job.
- `MapForwardedHeaders` is required because Caddy reverse-proxies; without it, `RemoteIpAddress` is always Caddy.
- The empty `CloudDbContext` registration means `dotnet ef migrations add ...` will succeed once CLOUD-002 adds the first entity. Until then, the DB has no schema and `AddDbContextCheck` returns *healthy* as long as it can open a connection. That's fine for CLOUD-001.
- No `MigrateAsync()` call on startup. The owning ticket for migrations is CLOUD-002.

## `Features/Admin/Health/AdminHealthEndpoint.cs`

Per ADR-0034 §2 — **no auth**, returns `cert_ready`, `cloud_id`, `registration_status`, `api_version`.

```csharp
using ThanyMarcus.Shared.CloudAdmin;

namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public static class AdminHealthEndpoint
{
    public static void MapAdminHealthEndpoint(this IEndpointRouteBuilder app) =>
        app.MapGet("/admin/health", (AdminHealthState state) =>
        {
            var snapshot = state.Read();
            return Results.Ok(new CloudAdminHealthResponse(
                CertReady:          snapshot.CertReady,
                CloudId:            snapshot.CloudId,
                RegistrationStatus: snapshot.RegistrationStatus,
                ApiVersion:         CloudAdminHealthResponse.CurrentApiVersion));
        })
        .WithName("AdminHealth")
        .AllowAnonymous();
}
```

Add the contract to `ThanyMarcus.Shared`:

```csharp
// src/ThanyMarcus.Shared/CloudAdmin/CloudAdminHealthResponse.cs
namespace ThanyMarcus.Shared.CloudAdmin;

public sealed record CloudAdminHealthResponse(
    bool CertReady,
    Guid CloudId,
    string RegistrationStatus,        // "pending" | "registered" | "failed"
    string ApiVersion)
{
    public const string CurrentApiVersion = "0.1.0";
}
```

Portal.SagaWorker's `AwaitingCertHandler` already polls `/admin/health` and parses the same shape — refactor it to consume `CloudAdminHealthResponse` from `ThanyMarcus.Shared` in this ticket. (Mechanical change; the parsing is one place. Avoids two definitions of the same DTO.)

## `Features/Admin/Health/AdminHealthState.cs`

The state is read at request time from two filesystem sources:

```csharp
using System.Text.Json;

namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public sealed class AdminHealthState(IConfiguration config, ILogger<AdminHealthState> log)
{
    private readonly string statePath = config["Cloud:StatePath"] ?? "/etc/thany-cloud/state.json";
    private readonly string certPath  = config["Cloud:CertCheckPath"]
        ?? throw new InvalidOperationException("Cloud:CertCheckPath is required");

    public Snapshot Read()
    {
        var cloudId           = Guid.Empty;
        var registrationStatus = "pending";
        var certReady          = File.Exists(certPath);

        try
        {
            if (File.Exists(statePath))
            {
                using var stream = File.OpenRead(statePath);
                using var doc    = JsonDocument.Parse(stream);
                var root         = doc.RootElement;
                if (root.TryGetProperty("cloud_id", out var idEl) &&
                    Guid.TryParse(idEl.GetString(), out var parsed))
                {
                    cloudId = parsed;
                }
                if (root.TryGetProperty("registration_status", out var statusEl) &&
                    statusEl.ValueKind == JsonValueKind.String)
                {
                    registrationStatus = statusEl.GetString() ?? "pending";
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            log.LogWarning(ex, "Failed to read state.json at {Path}", statePath);
        }

        return new Snapshot(certReady, cloudId, registrationStatus);
    }

    public sealed record Snapshot(bool CertReady, Guid CloudId, string RegistrationStatus);
}
```

**Filesystem sources** (set by cloud-init, **not by CLOUD-001**):
- `/etc/thany-cloud/state.json` — written by `register-with-portal.sh` (per ADR-0034 §4). Contains `cloud_id` + `registration_status`.
- `Cloud:CertCheckPath` — bind-mounted from Caddy's cert dir into Cloud.Api at read-only. The cloud-init compose-template owns this volume mapping; CLOUD-001 just *reads* it.

Defaults are in `appsettings.json`:
```json
{
  "ConnectionStrings": {
    "Cloud": ""
  },
  "Cloud": {
    "StatePath": "/etc/thany-cloud/state.json",
    "CertCheckPath": "/etc/thany-cloud/caddy-certs/acme-v02.api.letsencrypt.org-directory/example.com/example.com.crt"
  }
}
```

`CertCheckPath` is templated at cloud-init time (it embeds `${DOMAIN}`); the compose template injects the real value via `${CERT_CHECK_PATH}`. That coupling is *not* CLOUD-001's problem; the compose template will be updated in **F1 of PORTAL-011a** (real cloud-init landing). For CLOUD-001, document the contract in `appsettings.json` and move on.

## `Infrastructure/Database/CloudDbContext.cs`

```csharp
using Microsoft.EntityFrameworkCore;

namespace ThanyMarcus.Cloud.Api.Infrastructure.Database;

public sealed class CloudDbContext(DbContextOptions<CloudDbContext> options) : DbContext(options)
{
    // CLOUD-002 fills this with DbSet<Artifact>, DbSet<Note>, etc. + pgvector mappings.
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Intentionally empty in CLOUD-001.
    }
}
```

`TimestampInterceptor` lives in Portal.Api today. To avoid project-cross-reference, **copy the interceptor source** into `src/ThanyMarcus.Cloud.Api/Infrastructure/Database/TimestampInterceptor.cs` and adjust the namespace. Yes, this duplicates ~30 lines. The alternative is extracting it into `ThanyMarcus.Shared`, which works fine — pick whichever feels less awful:

- **Option A (copy):** simpler, cloud + portal evolve independently. Drift risk.
- **Option B (extract to Shared):** no drift, but `ThanyMarcus.Shared` starts taking on infrastructure code, not just contracts.

**Recommendation:** Option B. Move `TimestampInterceptor` to `ThanyMarcus.Shared/Database/` and update both Portal.Api and Cloud.Api to reference it. The interceptor is generic over `IClock`; it's behavior, not just contract, but it's the kind of behavior that *should* be identical across the two .NET processes.

## Test project — `tests/ThanyMarcus.Cloud.Tests/`

`ThanyMarcus.Cloud.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="xunit.v3" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Testcontainers.PostgreSql" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\ThanyMarcus.Cloud.Api\ThanyMarcus.Cloud.Api.csproj" />
    <ProjectReference Include="..\..\src\ThanyMarcus.Shared\ThanyMarcus.Shared.csproj" />
  </ItemGroup>
</Project>
```

Reuse the `PostgresCollection` / `PostgresFixture` pattern from `ThanyMarcus.Portal.Tests`. If splitting fixtures across two test projects gets annoying, lift the fixture into a `ThanyMarcus.TestSupport` library — but only when it actually causes pain. For CLOUD-001, a copy is fine.

### `ScaffoldTests.cs`

```csharp
[Collection(PostgresCollection.Name)]
public sealed class ScaffoldTests(PostgresFixture postgres) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Host_boots_and_health_live_returns_ok()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cloud", postgres.ConnectionString)
                                      .UseSetting("Cloud:CertCheckPath", "/tmp/does-not-exist.crt"));
        using var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        live.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_ready_returns_ok_against_postgres()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Cloud", postgres.ConnectionString)
                                      .UseSetting("Cloud:CertCheckPath", "/tmp/does-not-exist.crt"));
        using var client = factory.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

### `Admin/AdminHealthEndpointTests.cs`

```csharp
public sealed class AdminHealthEndpointTests
{
    [Fact]
    public async Task Returns_pending_when_state_file_missing_and_cert_missing()
    {
        await using var factory = WithTempFiles(stateExists: false, certExists: false);
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>("/admin/health");

        resp.ShouldNotBeNull();
        resp.CertReady.ShouldBeFalse();
        resp.RegistrationStatus.ShouldBe("pending");
        resp.ApiVersion.ShouldBe(CloudAdminHealthResponse.CurrentApiVersion);
    }

    [Fact]
    public async Task Returns_cert_ready_true_when_cert_path_exists()
    {
        await using var factory = WithTempFiles(stateExists: false, certExists: true);
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>("/admin/health");
        resp!.CertReady.ShouldBeTrue();
    }

    [Fact]
    public async Task Returns_registered_when_state_json_says_registered()
    {
        await using var factory = WithTempFiles(stateExists: true, certExists: true,
            stateJson: """{"cloud_id":"019e3230-407b-78b0-90ae-d028d29e416f","registration_status":"registered"}""");
        using var client = factory.CreateClient();

        var resp = await client.GetFromJsonAsync<CloudAdminHealthResponse>("/admin/health");
        resp!.CloudId.ShouldBe(Guid.Parse("019e3230-407b-78b0-90ae-d028d29e416f"));
        resp.RegistrationStatus.ShouldBe("registered");
    }

    [Fact]
    public async Task Endpoint_requires_no_auth()
    {
        await using var factory = WithTempFiles(stateExists: false, certExists: false);
        using var client = factory.CreateClient();

        // No Authorization header.
        var resp = await client.GetAsync("/admin/health");
        resp.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> WithTempFiles(
        bool stateExists, bool certExists, string? stateJson = null) { /* helper */ }
}
```

These four cases lock down the contract from ADR-0034. Future regressions in this endpoint break the saga's `awaiting_cert` polling; the test suite catches them.

## `infra/docker/cloud/Dockerfile`

`infra/docker/cloud/Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["Directory.Build.props", "Directory.Packages.props", "global.json", "./"]
COPY ["src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj", "src/ThanyMarcus.Cloud.Api/"]
COPY ["src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj",       "src/ThanyMarcus.Shared/"]
RUN dotnet restore "src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj"
COPY src/ src/
RUN dotnet publish "src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj" \
    -c Release -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "ThanyMarcus.Cloud.Api.dll"]
```

`libgssapi-krb5-2` is the same fix as PORTAL-011a F6 — Npgsql throws `TypeInitializationException` without it on the .NET 10 base image.

## `infra/docker/cloud/docker-compose.yml` — update

```yaml
services:
  caddy:
    image: caddy:2.7-alpine
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    depends_on:
      cloud-api:
        condition: service_healthy
    networks: [cloud]

  cloud-api:
    image: ghcr.io/bboiko/thany-cloud-api:${IMAGE_TAG:-latest}
    restart: unless-stopped
    environment:
      ASPNETCORE_ENVIRONMENT: Production
      ASPNETCORE_URLS: "http://+:8080"
      ConnectionStrings__Cloud: Host=postgres;Port=5432;Username=cloud;Password=${POSTGRES_PASSWORD};Database=cloud
      Cloud__StatePath: /etc/thany-cloud/state.json
      Cloud__CertCheckPath: /caddy-certs/certificates/acme-v02.api.letsencrypt.org-directory/${DOMAIN}/${DOMAIN}.crt
    volumes:
      - /etc/thany-cloud:/etc/thany-cloud:ro       # state.json written by cloud-init scripts
      - caddy-data:/caddy-certs:ro                 # read cert presence from Caddy's data volume
    depends_on:
      postgres:
        condition: service_healthy
    networks: [cloud]
    healthcheck:
      test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/health/live || exit 1"]
      interval: 10s
      timeout: 5s
      retries: 5

  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_USER: cloud
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: cloud
    volumes:
      - pg-data:/var/lib/postgresql/data
    networks: [cloud]
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U cloud -d cloud"]
      interval: 5s
      timeout: 5s
      retries: 10

volumes:
  caddy-data:
  caddy-config:
  pg-data:

networks:
  cloud:
```

**Removed:** the `stub-admin-health` nginx placeholder service and the `cert-watcher` alpine sidecar. Cloud.Api does the cert-presence check inline (one File.Exists call per `/admin/health` request).

**Postgres password:** templated via `${POSTGRES_PASSWORD}` from `cloud.env.tpl`. Add `POSTGRES_PASSWORD=${POSTGRES_PASSWORD}` to the env template; cloud-init generates it via `openssl rand -hex 32` at first boot (same shape as `CLOUD_ADMIN_TOKEN`).

**`Caddyfile.tpl`:** unchanged — already proxies `/admin/*` and `/api/*` to `cloud-api:8080`. The placeholder nginx was listening on `:8080`; the real Cloud.Api also listens on `:8080` (via `ASPNETCORE_URLS`). Zero Caddyfile changes.

## Solution file update

`ThanyMarcus.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/ThanyMarcus.Portal.Api/ThanyMarcus.Portal.Api.csproj" />
    <Project Path="src/ThanyMarcus.Portal.SagaWorker/ThanyMarcus.Portal.SagaWorker.csproj" />
    <Project Path="src/ThanyMarcus.Cloud.Api/ThanyMarcus.Cloud.Api.csproj" />
    <Project Path="src/ThanyMarcus.Shared/ThanyMarcus.Shared.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/ThanyMarcus.Portal.Tests/ThanyMarcus.Portal.Tests.csproj" />
    <Project Path="tests/ThanyMarcus.Cloud.Tests/ThanyMarcus.Cloud.Tests.csproj" />
  </Folder>
</Solution>
```

`Portal.Web` is not a .NET project — it stays out of `slnx`.

## GHCR push — minimal CI workflow

DEC-005 pinned GHCR. Without a CI job pushing the image, F1 of PORTAL-011a (real cloud-init) cannot pull `ghcr.io/bboiko/thany-cloud-api:latest` and the saga will fail at `docker compose up` on the droplet. **Land a minimal workflow in CLOUD-001:**

`.github/workflows/cloud-ci.yml`:

```yaml
name: Cloud CI
on:
  push:
    branches: [main]
    paths:
      - 'src/ThanyMarcus.Cloud.Api/**'
      - 'src/ThanyMarcus.Shared/**'
      - 'infra/docker/cloud/Dockerfile'
      - '.github/workflows/cloud-ci.yml'
  workflow_dispatch:

jobs:
  build-and-push:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
    steps:
      - uses: actions/checkout@v4
      - uses: docker/setup-buildx-action@v3
      - uses: docker/login-action@v3
        with:
          registry: ghcr.io
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}
      - uses: docker/build-push-action@v5
        with:
          context: .
          file: infra/docker/cloud/Dockerfile
          push: true
          tags: |
            ghcr.io/${{ github.repository_owner }}/thany-cloud-api:latest
            ghcr.io/${{ github.repository_owner }}/thany-cloud-api:${{ github.sha }}
          cache-from: type=gha
          cache-to: type=gha,mode=max
```

This is the minimum workflow; CLOUD-006 / PORTAL-017 may add tests-before-push, multi-arch, signing, etc. **Do not gate on tests** in CLOUD-001 — there's no `dotnet test` step here, just `docker build + push`. Tests run in a separate workflow (or a follow-up). Reason: this ticket needs a `:latest` tag in GHCR ASAP so PORTAL-011a's smoke can complete.

## Acceptance criteria

- `dotnet build` — zero warnings.
- `dotnet sln list` (or equivalent for `slnx`) shows `ThanyMarcus.Cloud.Api` and `ThanyMarcus.Cloud.Tests` alongside the existing projects.
- `dotnet run --project src/ThanyMarcus.Cloud.Api` against a local Postgres (`docker run --rm -p 5432:5432 -e POSTGRES_PASSWORD=test ...`) starts the host, logs "Application started", responds 200 on `/health/live` and `/health/ready`, returns the ADR-0034 payload on `/admin/health`.
- `dotnet test --filter "FullyQualifiedName~ThanyMarcus.Cloud.Tests"` — all tests green.
- `docker build -f infra/docker/cloud/Dockerfile .` — clean build.
- The minimal CI workflow lands an image at `ghcr.io/bboiko/thany-cloud-api:latest` accessible from a fresh droplet (`docker pull ghcr.io/bboiko/thany-cloud-api:latest` with no auth, since the image is public).
- `docker compose -f infra/docker/cloud/docker-compose.yml --env-file <test-env> up -d` on a local machine brings caddy + cloud-api + postgres online; `curl http://localhost/admin/health` returns the expected JSON.
- The `stub-admin-health/` directory is deleted; no references to `nginx:alpine` remain in `infra/docker/cloud/`.
- After CLOUD-001 lands: PORTAL-011a F1's real cloud-init template can successfully pull and start the Cloud.Api image on a fresh DO droplet, the cloud calls back to portal `/api/clouds/{id}/callback`, the saga reaches `awaiting_cert` and then `succeeded` once Caddy acquires the LE cert.

The last bullet is the **integration acceptance** — it crosses ticket boundaries (CLOUD-001 + PORTAL-011a F1) but is the actual user-visible behavior that motivates CLOUD-001. Land this ticket, then re-run the smoke runbook; that's the proof.

## Concrete steps in order

1. **Create the project** — `dotnet new web -n ThanyMarcus.Cloud.Api -o src/ThanyMarcus.Cloud.Api`. Delete the generated boilerplate (`WeatherForecast`, etc.). Replace csproj with the version above. Run `dotnet restore`.

2. **Add `Features/` + `Infrastructure/` directory tree** with `.gitkeep` placeholders for the per-ticket subdirs (per pivot plan §27).

3. **Move `TimestampInterceptor` to `ThanyMarcus.Shared/Database/`.** Update Portal.Api + Portal.SagaWorker to reference it from `ThanyMarcus.Shared` instead of their local copy. Verify Portal tests still pass.

4. **Add `ThanyMarcus.Shared/CloudAdmin/CloudAdminHealthResponse.cs`** with the ADR-0034 contract. Update Portal.SagaWorker's `AwaitingCertHandler` to consume it.

5. **Land `Program.cs`, `CloudDbContext.cs`, `AdminHealthState.cs`, `AdminHealthEndpoint.cs`.** Build; zero warnings.

6. **Local smoke** — start postgres locally, `dotnet run --project src/ThanyMarcus.Cloud.Api`. Hit `/health/live`, `/health/ready`, `/admin/health`. Confirm each returns expected payload.

7. **Create test project** `tests/ThanyMarcus.Cloud.Tests/`. Lift `PostgresFixture` / `PostgresCollection` from `ThanyMarcus.Portal.Tests` (copy initially; consider extracting to a `TestSupport` library if drift becomes painful).

8. **Land the four ADR-0034 test cases** in `AdminHealthEndpointTests` + the two scaffold tests in `ScaffoldTests`. All green.

9. **Add `Cloud.Api` + `Cloud.Tests` to `ThanyMarcus.slnx`.**

10. **Land `infra/docker/cloud/Dockerfile`** + update `docker-compose.yml` per the spec above. Local smoke: `docker compose -f infra/docker/cloud/docker-compose.yml up -d` with a test env file. Hit `http://localhost/admin/health`; confirm 200 + payload.

11. **Delete `infra/docker/cloud/stub-admin-health/`** entirely. Verify no other file references it.

12. **Land `.github/workflows/cloud-ci.yml`.** Push to main; verify the image appears at `https://github.com/users/bboiko/packages/container/package/thany-cloud-api`. Make the package visibility public.

13. **Cross-ticket validation** — re-run `plans/portal-011-smoke-runbook.md` end-to-end (after PORTAL-011a F1 lands, or as a coupled ticket with it). Saga must reach `succeeded`. This is the *real* DOD.

14. **Verify all acceptance criteria.** Commit only after all pass.

## Out of scope (do not touch)

- **Postgres schema** — CLOUD-002 lands DbSets + pgvector + the first migration.
- **Bearer-token auth middleware** on `/api/*` — CLOUD-003.
- **Plugin endpoints** (`/api/ingest`, `/api/sync/pull`, etc.) — CLOUD-004 stubs them; CLOUD-011+ fills the bodies.
- **Admin endpoints other than `/admin/health`** — CLOUD-005 (`/admin/settings`, `/admin/plugin-tokens`, `/admin/register-with-portal`, `/admin/audit-log`).
- **`cloud_admin_token` validation middleware** — CLOUD-005.
- **Ollama + Parakeet + burst-worker compose** — CLOUD-007 + CLOUD-016b. They live in a *separate* compose file (`docker-compose.worker.yml`) on a *separate* ephemeral droplet, not the control-plane stack.
- **Cloud-init template authoring** — that's `infra/terraform/shared/cloud-init.yaml.tpl` (PORTAL-010 + PORTAL-011a F1). CLOUD-001 documents the *contract* on `state.json` + cert-path; it doesn't author the script that writes them.
- **Caddy admin-API integration** for cert detection — File.Exists on the bind-mounted cert path is the MVP. CLOUD-005 may refactor to hit Caddy's admin API at `:2019/config/` if `File.Exists` proves flaky.
- **Image signing / cosign** — DEC-005 says "future-work". Skip.
- **Multi-arch builds** — single-arch (amd64) until someone runs Thany on Apple Silicon servers. Not soon.
- **Cloud-side TOTP / step-up / cookie auth** — explicitly rejected per `Q-2FA-scope` (cloud security is bearer-token + provider's Google SSO at the VM level).

## Risks & gotchas

- **`TimestampInterceptor` move (step 3) breaks Portal.Api tests if you miss a namespace.** It's a search-and-replace across three projects (Portal.Api, Portal.SagaWorker, the new Cloud.Api). Run the full test suite after the move; warnings-as-errors will catch most cases but ambiguous references between `ThanyMarcus.Portal.Api.Infrastructure.Database.TimestampInterceptor` and `ThanyMarcus.Shared.Database.TimestampInterceptor` could compile if both still exist. Delete the Portal.Api copy.

- **`CloudAdminHealthResponse` shape coupling.** Three places consume this DTO now: Cloud.Api emits it, Portal.SagaWorker's `AwaitingCertHandler` polls and parses it, Portal.Api's wizard surfaces it. If any of them drifts from the ADR-0034 schema, the saga silently hangs in `awaiting_cert`. Locking it in `ThanyMarcus.Shared` is the mitigation; do not let either consumer redefine it locally.

- **`/admin/health` cert-path is templated at cloud-init time, not at image-build time.** The `${DOMAIN}` segment of the path is per-cloud — `acme-v02.api.letsencrypt.org-directory/abc123.thany.click/abc123.thany.click.crt`. Cloud.Api reads `Cloud:CertCheckPath` from env, which the compose file's cloud-init wrapper renders with the actual domain. If the env var is wrong, every poll returns `cert_ready: false` and the saga eventually times out in `failed_cert`. Lock this with a runtime sanity-check log line: on startup, log the resolved cert path. Saves hours of debugging.

- **Postgres password generation at first boot.** The compose env file expects `POSTGRES_PASSWORD`. Cloud-init must generate it (`openssl rand -hex 32`) and write it to the env file before `docker compose up`. If it's empty, postgres comes up with auth disabled / random — both are wrong. PORTAL-011a F1's cloud-init template owns this; CLOUD-001 just consumes the env var.

- **GHCR `:latest` tag mutability.** Every push to `main` retags `:latest`. Deployed clouds pulling `:latest` will get the next version on next reboot — fine for MVP but a *minor* surprise. Cloud-init pins by SHA tag (`${IMAGE_TAG}`) in `cloud.env.tpl` so deployed clouds stick to the version they provisioned with. Verify: `cloud.env.tpl` sets `IMAGE_TAG` to the SHA, not to `latest`. If it doesn't, fix in F1 of PORTAL-011a (it's adjacent).

- **`docker compose` healthcheck on `cloud-api` requires `wget`.** The `aspnet:10.0` base image is Debian-based and has wget. If you switch to alpine base image later, swap to `curl` or `nc`.

- **OpenAPI / Scalar exposure on `/openapi/v1.json` and `/scalar/v1`** — these are exposed by default on the cloud-side too. **Is that fine?** Public API docs leak schema. **Recommendation:** in `Production` env, gate `MapOpenApi()` + `MapScalarApiReference()` behind `app.Environment.IsDevelopment()`. Same change in Portal.Api is unrelated; do not bundle it.

- **Liveness vs readiness semantics.** `/health/live` returns OK as soon as the process is up — *no DB check*. `/health/ready` returns OK only when the Postgres connection check passes. PORTAL-016 + ADR-0034 say the *saga* polls `/admin/health` (not `/health/ready`) — so this distinction matters mainly for the cloud's own Docker healthcheck. Don't accidentally use `/health/ready` for the saga poll.

- **`AdminHealthState.Read()` re-reads the files on every request.** No caching. Fine — the file is tiny and the saga polls every 5 s, then every 30 s. A more elegant fileystem-watcher approach lives in CLOUD-005; not necessary here.

- **Network port leak.** Caddy listens on `:80` and `:443`; Cloud.Api on `:8080` (internal). Do not expose `:8080` to the host or it bypasses Caddy + TLS. In the compose snippet above, `cloud-api` has no `ports:` mapping — only `networks: [cloud]`. Keep it that way.

- **First-time `dotnet ef` will fail** because there are no entities / no `OnModelCreating` content. That's fine — CLOUD-002 lands the first entity and the first migration. Until then, the DB stays empty.

- **The `TimestampInterceptor` registration in Cloud.Api's `Program.cs`** assumes the same interceptor signature. If the interceptor reads `IClock` from DI, ensure `IClock` is registered before the DbContext — currently it is, but if step 3 reshuffles dependencies, double-check.

- **Test-collection name collision.** `ThanyMarcus.Portal.Tests` defines `PostgresCollection` with `[CollectionDefinition("postgres")]`. If `ThanyMarcus.Cloud.Tests` copies it verbatim, they're separate collections at compile time (different assemblies), but the *test runner* may parallelize containers wastefully. Use distinct collection names (`postgres_portal` vs `postgres_cloud`) or share the fixture through a `TestSupport` library. Fine to defer to first painful moment.

## Definition of done

All acceptance criteria pass + `git status` shows the new project + Dockerfile + compose update + slnx update + CI workflow + image at `ghcr.io/bboiko/thany-cloud-api:latest` (public) + `stub-admin-health/` deleted. **Cross-ticket validation:** PORTAL-011a's smoke runbook end-to-end reaches `saga.status = succeeded` (which requires CLOUD-001's image to exist in GHCR and Cloud.Api to respond on `/admin/health`).

A fresh agent picking up CLOUD-002 from this state knows:
- The `CloudDbContext` exists, is empty, and is wired into DI with the standard interceptor + naming conventions.
- The image is published to GHCR on every main-branch push.
- Postgres is in the compose stack and reachable at `postgres:5432`.
- Cloud.Api responds 200 on `/health/live` + `/admin/health` against an empty DB; CLOUD-002's migrations land and the schema starts taking shape from there.

A fresh agent picking up CLOUD-003 knows:
- No auth middleware is wired in CLOUD-001. `/api/*` and `/admin/*` (except `/admin/health`) are currently *unrouted* — CLOUD-003 + CLOUD-005 land them with their own auth.
- The `cloud_admin_token` is in `cloud.env.tpl` (via cloud-init) under `CLOUD_ADMIN_TOKEN`. CLOUD-005's middleware reads it from config.

## Cross-references

- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — the contract this ticket implements (`/admin/health` payload, no-auth posture).
- **`docs/decisions/0027-reverse-proxy-caddy.md`** — Caddy in front of Cloud.Api.
- **`plans/portal-011a-handoff.md`** — F1 (real cloud-init template) depends on this image existing in GHCR. CLOUD-001 unblocks it.
- **`plans/cloud-pivot-plan-2026-05-13.md §27`** — directory layout this ticket scaffolds.
- **`plans/tickets-2026-05-13.md`** — CLOUD-002, CLOUD-003, CLOUD-004, CLOUD-005, CLOUD-006 are the direct successors.
- **DEC-001** (control plane = 2 vCPU / 4 GB), **DEC-005** (GHCR image distribution) — both consumed here.
- **PLUGIN-003** — first external consumer of Cloud.Api endpoints (via CLOUD-003 + CLOUD-004 auth + scaffolded routes).
