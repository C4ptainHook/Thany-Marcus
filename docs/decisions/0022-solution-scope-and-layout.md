# ADR-0022: Solution scope and project layout

Status: Accepted
Date: 2026-05-15

## Context

PORTAL-001 says: "Scaffold .NET 10 solution: `ThanyMarcus.Portal.Api`, `ThanyMarcus.Portal.Web`, `ThanyMarcus.Shared`." Three forks need resolving before `dotnet new` is run:

1. With Portal.Web now a SvelteKit workspace (see [[0017-portal-web-stack-sveltekit]]), the actual .NET-project surface is Portal.Api + Shared. Should the solution also include a test project, placeholder Cloud.Api, or be split per service?
2. Should the SvelteKit workspace be scaffolded under PORTAL-001 or deferred to a separate ticket?
3. There are stale files at `src/Dockerfile` and `src/.devcontainer/` from the pre-pivot Avalonia/Python direction. What happens to them?

The plan's repo layout in `cloud-pivot-plan-2026-05-13.md §27` shows many .NET projects (Cloud.Api, 4 test projects, EvalTools) that are owned by *other* tickets (CLOUD-001, PORTAL-002+, EVAL-*). PORTAL-001 does not own them.

## Options considered

### Solution scope

- **A. Literal ticket text.** Only `Portal.Api` + `Shared`. Test project added in PORTAL-002.
- **B. Add `Portal.Tests` now.** xUnit + Shouldly + Testcontainers conventions set once, used by PORTAL-002+ from line one.
- **C. Pre-create empty placeholder csprojs for everything in the plan.** Cloud.Api, all 4 test projects, EvalTools.
- **D. Per-service solutions.** `ThanyMarcus.Portal.sln` + `ThanyMarcus.Cloud.sln`.

### SvelteKit scaffolding

- **A. Scaffold under PORTAL-001.** Locks the build-pipeline integration once (Vite proxy config, MSBuild copy path).
- **B. Defer to its own ticket.** PORTAL-001 stays .NET-only.

### Stale files

- **A. Delete.** Remove `src/Dockerfile` and `src/.devcontainer/`; git history preserves them.
- **B. Archive.** Move to `.archive/`.
- **C. Leave in place.**

## Decision

**Solution scope: B.** `ThanyMarcus.sln` contains `ThanyMarcus.Portal.Api` + `ThanyMarcus.Shared` + `ThanyMarcus.Portal.Tests` at PORTAL-001 close. `Cloud.Api` and other test projects are added by their owning tickets via `dotnet sln add`.

**SvelteKit scaffolding: A.** Portal.Web is scaffolded under PORTAL-001 alongside the .NET projects. "Scaffold the .NET 10 solution and project structure" in spirit covers the full repo's foundational layout — and the build-pipeline integration is cheaper to lock once than to backfill.

**Stale files: A.** Delete `src/Dockerfile` and `src/.devcontainer/`. They are from the pre-pivot Avalonia/Python direction (`mcr.microsoft.com/devcontainers/python:3.12-bookworm` is unrelated to the current .NET+TS stack) and will mislead future contributors and AI agents. Git history preserves the original content if needed.

### Concrete layout at PORTAL-001 close

```
Thany-Marcus/
├── ThanyMarcus.sln                          # Portal.Api + Shared + Portal.Tests
├── global.json                              # pins .NET 10.0.x SDK, rollForward: latestFeature
├── Directory.Build.props                    # nullable, lang version, warnings-as-errors
├── Directory.Packages.props                 # central package management
├── .editorconfig                            # 4-space C#, 2-space TS/JSON/YAML
├── README.md
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── ThanyMarcus.Portal.Api.csproj    # Web SDK, net10.0
│   │   ├── Program.cs                       # OpenAPI+Scalar, OTel+Prometheus,
│   │   │                                    # /health/live+ready, static files + fallback
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   ├── Properties/launchSettings.json   # port 5000
│   │   ├── Features/                        # empty subdirs:
│   │   │   ├── Auth/
│   │   │   ├── CloudManagement/
│   │   │   ├── Destroy/
│   │   │   ├── PluginTokens/
│   │   │   ├── Provisioning/
│   │   │   └── RecoveryCodes/
│   │   ├── Infrastructure/                  # empty subdirs:
│   │   │   ├── Crypto/
│   │   │   ├── Database/
│   │   │   └── TerraformRunner/
│   │   └── wwwroot/                         # empty; populated from Portal.Web/build at publish
│   ├── ThanyMarcus.Shared/
│   │   ├── ThanyMarcus.Shared.csproj        # classlib, net10.0
│   │   └── (empty — populated only when a real cross-.NET need arises)
│   └── ThanyMarcus.Portal.Web/              # NOT in .sln; SvelteKit pnpm workspace
│       ├── package.json
│       ├── svelte.config.js                 # adapter-static, fallback: 'index.html'
│       ├── vite.config.ts                   # proxy /api → :5000
│       ├── tsconfig.json
│       ├── src/
│       │   ├── app.html
│       │   ├── app.d.ts
│       │   └── routes/+page.svelte          # stub "Portal coming soon"
│       └── static/
├── tests/
│   └── ThanyMarcus.Portal.Tests/
│       ├── ThanyMarcus.Portal.Tests.csproj  # xUnit v3 + Shouldly + Testcontainers refs
│       └── (empty — first tests land with PORTAL-002+)
├── docs/                                    # unchanged; .Decisions/ already populated
├── infra/                                   # unchanged
└── plans/                                   # unchanged

DELETED in this commit: src/Dockerfile, src/.devcontainer/
```

### Why each rejected option was rejected

- **Sol-scope A (literal):** the first PORTAL-002 task is to add the test project anyway — bundling it into PORTAL-001 keeps the scaffold cohesive and means crypto/auth tests are possible from line one rather than after a separate scaffolding ticket.
- **Sol-scope C (all placeholders):** empty `Cloud.Api` and four extra test csprojs are dead weight that AI agents and humans must mentally filter on every `dotnet build` and IDE load. CLOUD-001 cleanly adds Cloud.Api when it's needed.
- **Sol-scope D (per-service slns):** breaks "open repo, hit run" for a single-developer monorepo. Adds IDE-load ceremony with no compensating benefit.
- **SvelteKit B (defer):** PORTAL-011's wizard UI is the first ticket that uses Portal.Web, but the build-pipeline integration (Vite proxy, MSBuild copy path, CI step) is cheaper to lock during initial scaffolding than to retrofit.
- **Stale-files B (archive):** `.archive/` accumulates indefinitely. Git history is the right archive.
- **Stale-files C (leave):** a fresh contributor or AI agent reading `src/Dockerfile` and `src/.devcontainer/` would spend non-zero time figuring out why Python 3.12 is in a .NET portal. Net cognitive tax for no gain.

## Consequences

- **Positive:**
  - `dotnet test` works from the first commit; PORTAL-002's crypto and EF Core migrations have a test home.
  - Single `dotnet build` covers all .NET code; no IDE ceremony.
  - Portal.Web's existence as a real-but-empty workspace lets PORTAL-011's UI ticket focus on UI rather than scaffolding.
  - Repo `src/` is unambiguous — only files relevant to the current architecture.
- **Negative:**
  - `Shared` exists as a near-empty class library, which can read as YAGNI. Justified because (a) the plan calls for it, (b) cross-.NET shared types will accumulate as Cloud.Api lands, and (c) it costs nothing to leave empty.
- **Neutral:**
  - A new `.devcontainer/` for the .NET+TS stack is *not* added under PORTAL-001 (single-developer thesis, no Codespaces use). Can be added later if onboarding need arises.

## Related

- [[0017-portal-web-stack-sveltekit]] — what Portal.Web is and why it's not a .NET project
- [[0018-portal-api-minimal-apis-vsa]] — what the Features/ subdirs will hold
- [[0021-dev-workflow]] — how the two workspaces interoperate during development
- [[0023-test-stack]] — what `ThanyMarcus.Portal.Tests.csproj` contains
- `plans/cloud-pivot-plan-2026-05-13.md §27` — full target repo layout
