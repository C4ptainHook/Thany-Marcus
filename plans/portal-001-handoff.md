# PORTAL-001 Scaffolding — Handoff Brief

**Goal**: produce the empty-but-runnable .NET 10 + SvelteKit skeleton described in ADR-0022 and the [[portal-tooling]] memory record. This is pure scaffolding — no feature logic. Estimated 0.25 person-day.

## Where decisions live (read before doing anything)

- `docs/decisions/README.md` — index of all ADRs.
- `docs/decisions/0017-portal-web-stack-sveltekit.md` through `0027-reverse-proxy-caddy.md` — the 11 locked architectural decisions from the 2026-05-15 grilling. Each ADR captures **Context → Options → Decision → Consequences**.
- `~/.claude/projects/-Users-bboiko-Personal-Thesis/memory/MEMORY.md` and its linked files — concise, authoritative summaries:
  - `portal_web_stack.md` — SvelteKit + adapter-static SPA
  - `portal_architecture.md` — Minimal APIs + VSA; Postgres job queue with `LISTEN/NOTIFY`; mutable status; SSE
  - `portal_tooling.md` — .NET 10, central package mgmt, OTel+Prometheus, Shouldly+xUnit v3, OpenAPI+Scalar, hand-written TS, /health/live+ready
  - `portal_deployment.md` — Azure VM B2ms with Docker Compose
- `plans/cloud-pivot-plan-2026-05-13.md §27` — target repo layout (the full picture across all tickets).
- `plans/tickets-2026-05-13.md` — full implementation backlog. PORTAL-001 is the first ticket; PORTAL-002+ depend on what PORTAL-001 produces.

**Do not re-litigate the architecture during scaffolding.** Every fork was resolved by the grilling. If something seems unclear, the ADR for that decision is the authoritative answer.

## Output of PORTAL-001 — final directory state

```
Thany-Marcus/
├── ThanyMarcus.sln                          # contains Portal.Api + Shared + Portal.Tests
├── global.json                              # pins current .NET 10.0.x SDK, rollForward: latestFeature
├── Directory.Build.props                    # nullable, lang version, warnings-as-errors, analysis level
├── Directory.Packages.props                 # central package management (ManagePackageVersionsCentrally)
├── .editorconfig                            # 4-space C#, 2-space TS/JSON/YAML, LF, file-scoped namespaces
├── README.md
├── src/
│   ├── ThanyMarcus.Portal.Api/              # .NET 10 Web SDK
│   │   ├── ThanyMarcus.Portal.Api.csproj
│   │   ├── Program.cs                       # see "Program.cs wiring" below
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── Properties/launchSettings.json   # port 5000
│   │   ├── Features/                        # empty dirs:
│   │   │   ├── Auth/
│   │   │   ├── CloudManagement/
│   │   │   ├── Destroy/
│   │   │   ├── PluginTokens/
│   │   │   ├── Provisioning/
│   │   │   └── RecoveryCodes/
│   │   ├── Infrastructure/                  # empty dirs:
│   │   │   ├── Crypto/
│   │   │   ├── Database/
│   │   │   └── TerraformRunner/
│   │   └── wwwroot/                         # empty; production-build copies SvelteKit output here
│   ├── ThanyMarcus.Shared/
│   │   ├── ThanyMarcus.Shared.csproj        # classlib, net10.0; empty
│   │   └── (no .cs files yet)
│   └── ThanyMarcus.Portal.Web/              # SvelteKit pnpm workspace; NOT in .sln
│       ├── package.json
│       ├── svelte.config.js                 # adapter-static, fallback: 'index.html'
│       ├── vite.config.ts                   # proxy /api → http://localhost:5000
│       ├── tsconfig.json
│       ├── src/
│       │   ├── app.html
│       │   ├── app.d.ts
│       │   └── routes/+page.svelte          # stub "Portal coming soon"
│       └── static/
├── tests/
│   └── ThanyMarcus.Portal.Tests/
│       ├── ThanyMarcus.Portal.Tests.csproj  # xUnit v3 + Shouldly + Testcontainers.PostgreSql refs
│       └── (no tests yet — PORTAL-002+ fill these in)
├── docs/                                    # unchanged (already has decisions/)
├── infra/                                   # unchanged
└── plans/                                   # unchanged

DELETED in this commit: src/Dockerfile, src/.devcontainer/
```

## Program.cs wiring (PORTAL-001 close)

Minimum-viable composition. **No** auth wiring (PORTAL-003), **no** DbContext registration (PORTAL-002), **no** feature endpoints. Just the cross-cutting plumbing locked in [[portal-tooling]] and [[portal-architecture]]:

```csharp
var builder = WebApplication.CreateBuilder(args);

// --- Logging ---
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.UseUtcTimestamp = true;
});

// --- OpenAPI ---
builder.Services.AddOpenApi();

// --- OpenTelemetry (per ADR-0026) ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "ThanyMarcus.Portal.Api",
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

// --- Health checks (per ADR-0026) ---
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live", "ready"]);
    // DB check added in PORTAL-002

var app = builder.Build();

app.UseStaticFiles();

app.MapOpenApi();
app.MapScalarApiReference();          // /scalar
app.MapPrometheusScrapingEndpoint();  // /metrics

app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = c => c.Tags.Contains("live")  });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;   // for WebApplicationFactory<Program> in tests
```

## Acceptance criteria

After running the scaffold tasks, all of the following must be true:

- `dotnet build` succeeds with **zero warnings** (warnings-as-errors is on; any package warning must be resolved at scaffold time).
- `dotnet run --project tests/ThanyMarcus.Portal.Tests` runs the smoke tests cleanly with exit 0. **Note**: `dotnet test` currently has a discovery quirk with `xunit.v3.mtp-v2` 3.x and does not find MTP-style tests. The canonical MTP invocation is `dotnet run --project <test-project>` (or running the built binary directly). CI scripts should use that.
- `dotnet run --project src/ThanyMarcus.Portal.Api` starts on `:5000`.
- `curl http://localhost:5000/health/live` → 200 OK with JSON body.
- `curl http://localhost:5000/health/ready` → 200 OK.
- `curl http://localhost:5000/openapi/v1.json` → JSON spec.
- `curl http://localhost:5000/metrics` → Prometheus exposition format (text with `# HELP` / `# TYPE` lines).
- Browser: `http://localhost:5000/scalar` → Scalar docs UI renders.
- `pnpm install && pnpm build` inside `src/ThanyMarcus.Portal.Web/` succeeds and produces `build/index.html` + chunks.
- Manual production-build smoke test: copy `Portal.Web/build/*` → `Portal.Api/wwwroot/`, restart `dotnet run`, hit `http://localhost:5000/` in a browser → SvelteKit "Portal coming soon" page renders.
- Dev-loop smoke test: `dotnet watch` (terminal 1) + `pnpm dev` (terminal 2). Browser to `http://localhost:5173`. SvelteKit's stub page renders. `http://localhost:5173/health/live` returns 200 via Vite proxy.
- `src/Dockerfile` and `src/.devcontainer/` are deleted.

## Concrete steps in order (each maps to a task)

1. **Cleanup stale src files** — `rm src/Dockerfile && rm -rf src/.devcontainer/`. Verify no other Avalonia/Python-era artifacts remain in `src/`.
2. **Root tooling files** — write `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `README.md` (stub). Conventions are in [[portal-tooling]]; final exact contents per ADR-0026 and ADR-0023.
3. **Create solution + .NET projects** — `dotnet new sln -n ThanyMarcus`, then `dotnet new web -n ThanyMarcus.Portal.Api -o src/ThanyMarcus.Portal.Api`, `dotnet new classlib -n ThanyMarcus.Shared -o src/ThanyMarcus.Shared`, `dotnet new xunit3 -n ThanyMarcus.Portal.Tests -o tests/ThanyMarcus.Portal.Tests`. Add all three to the sln. Create the empty `Features/` and `Infrastructure/` subdirs in Portal.Api with `.gitkeep` files.
4. **Wire Portal.Api packages + Program.cs** — add `Scalar.AspNetCore`, OpenTelemetry packages (per ADR-0026 package list), `Microsoft.Extensions.Diagnostics.HealthChecks` (transitive should cover this). Implement `Program.cs` per the wiring shown above. Add `public partial class Program;` line for test host.
5. **Wire Portal.Tests packages** — add `Microsoft.AspNetCore.Mvc.Testing`, `Testcontainers.PostgreSql`, `Shouldly`. No tests yet (PORTAL-002+ adds them).
6. **Scaffold Portal.Web SvelteKit workspace** — `cd src && pnpm create svelte@latest ThanyMarcus.Portal.Web`. Choose: Skeleton project, TypeScript, ESLint, Prettier, Vitest. Then `pnpm add -D @sveltejs/adapter-static`. Configure `svelte.config.js` with `fallback: 'index.html'` (per ADR-0017). Configure `vite.config.ts` with proxy for `/api`, `/openapi`, `/scalar`, `/metrics`, `/health` to `http://localhost:5000` (per ADR-0021). Replace `src/routes/+page.svelte` with a "Portal coming soon" stub.
7. **Verify acceptance criteria** — run every check in the Acceptance criteria section above. Fix any warnings. Commit only after all checks pass.

## Out of scope (do not touch)

- Authentication wiring (Google SSO, cookies, TOTP) — PORTAL-003
- DbContext, migrations, entities — PORTAL-002
- Terraform runner — PORTAL-007
- Caddyfile, docker-compose.yml for the portal deployment — PORTAL-017
- prometheus.yml — PORTAL-017
- Any feature endpoint logic
- `.github/workflows/portal-ci.yml` — separate from PORTAL-001; PORTAL-017 territory or its own ticket

## Risks & gotchas

- **xUnit v3 template name**: `dotnet new xunit3` is the v3 template; `dotnet new xunit` is v2. If `xunit3` isn't available in the installed SDK, install via `dotnet new install xunit.v3.templates` and retry.
- **`dotnet test` + xunit.v3.mtp-v2 discovery quirk**: as of .NET 10.0.201 + `xunit.v3.mtp-v2` 3.2.x, `dotnet test` reports "Zero tests ran" / exit 5 even though tests are present and pass when invoked directly. Use `dotnet run --project tests/<project>` (or run the test binary directly) to run MTP tests. Re-evaluate when xunit.v3.mtp-v2 or the .NET CLI updates.
- **Scalar.AspNetCore** versioning: check NuGet for the latest stable when adding the reference. Pre-1.0 packages may have API churn.
- **OpenTelemetry packages**: only `OpenTelemetry.Instrumentation.Process` may need a pre-release flag depending on its current channel — use the stable equivalent if available.
- **Central package management gotcha**: when `ManagePackageVersionsCentrally` is true, `PackageReference` entries in csprojs must NOT have `Version` attributes. Every package version lives in `Directory.Packages.props` only.
- **Warnings-as-errors gotcha**: scaffolded templates sometimes emit transient warnings (XML doc warnings for new files). Resolve at scaffold time — don't suppress, fix.
- **SvelteKit defaults**: the create-svelte CLI may default to installing Tailwind/etc. Skip these in PORTAL-001 — choose the minimal Skeleton template. UI styling decisions belong to PORTAL-011.

## Definition of done

All acceptance criteria pass + `git status` shows the new files. No code committed beyond the scaffold. Tasks marked completed in the task list.

A fresh agent can pick this up from cold by reading this brief + the four memory files + ADRs 0017–0027.
