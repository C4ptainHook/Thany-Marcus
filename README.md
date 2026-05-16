# Thany-Marcus

Knowledge-cloud portal + Obsidian plugin + per-user self-hosted cloud for personal memory management.

This is the working repo for a bachelor's thesis on a cloud service for data aggregation and organization in Markdown format.

## Architecture

- **`src/ThanyMarcus.Portal.Api`** — ASP.NET Core (.NET 10) admin portal. Provisions per-user clouds via Terraform; issues plugin bearer tokens; serves the SvelteKit SPA.
- **`src/ThanyMarcus.Portal.Web`** — SvelteKit (TypeScript) admin UI. Built static, served from Portal.Api's `wwwroot/` in production.
- **`src/ThanyMarcus.Shared`** — small .NET class library for types shared between Portal.Api and Cloud.Api (will grow when Cloud.Api lands).
- **`src/ThanyMarcus.Cloud.Api`** *(future, owned by CLOUD-001)* — the API that runs on each user's VPS. Ingest, processing pipeline, sync.
- **`src/ThanyMarcus.Plugin`** *(future, owned by PLUGIN-001)* — Obsidian plugin in TypeScript.

## Where the design lives

- **`plans/cloud-pivot-plan-2026-05-13.md`** — current source of truth for architecture.
- **`docs/decisions/`** — Architecture Decision Records (ADRs 0017+ cover the portal scaffold; 0001–0016 are reserved for the 2026-05-13 grilling backfill).
- **`plans/tickets-2026-05-13.md`** — implementation backlog.
- **`plans/portal-001-handoff.md`** — focused brief for the current scaffolding ticket.

## Development

Two terminals during development (see ADR-0021):

```bash
# terminal 1 — .NET API on :5000
dotnet watch --project src/ThanyMarcus.Portal.Api

# terminal 2 — SvelteKit dev server on :5173 with Vite proxy → :5000
cd src/ThanyMarcus.Portal.Web
pnpm dev
```

Open `http://localhost:5173`.

## Testing

The test suite uses xUnit v3 + Shouldly + Testcontainers.PostgreSql (ADR-0023). A real Postgres container is started once per test session, so a working Docker-compatible runtime is required (Docker Desktop, OrbStack, Colima, or Podman).

```bash
# one-shot
scripts/test.sh

# TDD loop (re-runs on save)
scripts/test-watch.sh

# narrow to a subset
scripts/test.sh -- --filter "FullyQualifiedName~Smoke"
```

`scripts/test.sh` auto-detects a running Podman machine and points `DOCKER_HOST` at its socket; Docker Desktop / Linux native need no setup. The wrapper exists because `dotnet test` does not currently discover MTP-style xunit.v3 tests — use these scripts (or `dotnet run --project tests/...`) until that upstream fix lands.
