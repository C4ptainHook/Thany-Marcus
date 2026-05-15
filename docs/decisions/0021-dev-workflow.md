# ADR-0021: Local dev workflow

Status: Accepted
Date: 2026-05-15

## Context

Portal.Api (.NET) and Portal.Web (SvelteKit) are two different toolchains that ship as one process in production (the SvelteKit static build is copied into Portal.Api's `wwwroot/`). In development, this raises a question: how does a developer iterate on UI code with hot reload *and* iterate on .NET code with hot reload, in the same project?

The decision affects PORTAL-001 scaffolding because it shapes:
- `launchSettings.json` (which port Portal.Api binds)
- `vite.config.ts` (whether Vite proxies the API and on what port)
- the MSBuild pipeline for production builds (whether Portal.Api's csproj triggers `pnpm build` or whether CI does it as a separate step)

## Options considered

- **A. Two terminals + Vite dev proxy.** `dotnet watch run` on Portal.Api at `localhost:5000`. `pnpm dev` on Portal.Web at `localhost:5173` with Vite's dev server (HMR). Vite proxies `/api/*` → `localhost:5000`. Browser hits `localhost:5173`; SvelteKit sees same-origin (cookies work). In production, Vite is not used — Portal.Api serves the built static files.
- **B. `Microsoft.AspNetCore.SpaServices.Extensions` middleware.** Portal.Api spawns Vite as a child process and proxies frontend requests to it. One terminal, but Microsoft has been walking away from this package and the lifecycle is fragile.
- **C. Always build, always serve.** `pnpm build` then `dotnet run`. The SvelteKit output is served from `wwwroot/`. Slow UI iteration (full rebuild on every change, no HMR).
- **D. `dotnet watch` invokes `pnpm build` on every change.** Slowest of all.

For the production build pipeline (orthogonal to dev workflow):

- **A1. MSBuild target inside Portal.Api.csproj** that runs `pnpm install --frozen-lockfile && pnpm build` and copies output into `wwwroot/`. Single `dotnet publish` does everything. Couples .NET build to Node tooling — `dotnet build` now needs Node installed.
- **A2. Separate CI step before `dotnet publish`.** GitHub Actions runs `pnpm build` as one job step that copies into Portal.Api's `wwwroot/`, then runs `dotnet publish`. Each step has a single responsibility; failures are easy to diagnose.

## Decision

**Dev workflow: A (two terminals + Vite dev proxy).**
**Production build pipeline: A2 (separate CI steps).**

### Dev workflow concrete shape

`src/ThanyMarcus.Portal.Web/vite.config.ts`:

```ts
import { sveltekit } from '@sveltejs/kit/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [sveltekit()],
  server: {
    port: 5173,
    proxy: {
      '/api':     'http://localhost:5000',
      '/openapi': 'http://localhost:5000',
      '/scalar':  'http://localhost:5000',
      '/metrics': 'http://localhost:5000',
      '/health':  'http://localhost:5000',
    },
  },
});
```

`src/ThanyMarcus.Portal.Api/Properties/launchSettings.json` pins `applicationUrl` to `http://localhost:5000`.

Developer flow:

```
# terminal 1:
cd src/ThanyMarcus.Portal.Api && dotnet watch run

# terminal 2:
cd src/ThanyMarcus.Portal.Web && pnpm dev

# browser:
open http://localhost:5173
```

The browser hits Vite (`:5173`) for all assets and HMR; Vite proxies `/api/*` to Portal.Api (`:5000`) transparently. SvelteKit sees same-origin from the cookie's perspective.

### Production build pipeline concrete shape

`.github/workflows/portal-ci.yml` (sketch):

```yaml
- name: Setup pnpm
  uses: pnpm/action-setup@v3
- name: Setup Node
  uses: actions/setup-node@v4
- name: Build SvelteKit
  working-directory: src/ThanyMarcus.Portal.Web
  run: |
    pnpm install --frozen-lockfile
    pnpm build

- name: Copy SvelteKit build into wwwroot
  run: |
    rm -rf src/ThanyMarcus.Portal.Api/wwwroot
    cp -r src/ThanyMarcus.Portal.Web/build src/ThanyMarcus.Portal.Api/wwwroot

- name: Setup .NET
  uses: actions/setup-dotnet@v4
- name: Publish
  run: dotnet publish src/ThanyMarcus.Portal.Api -c Release -o publish
```

Locally, the equivalent is `pnpm build` in Portal.Web then `dotnet publish` in Portal.Api — rarely needed because dev mode uses A.

## Consequences

- **Positive:**
  - Fast UI iteration via Vite HMR (subsecond on most Svelte changes).
  - Fast .NET iteration via `dotnet watch`.
  - Same-origin cookies in dev — no CORS gymnastics.
  - CI pipeline is composable: failures point to the exact step (Node install? pnpm install? SvelteKit build? .NET publish?).
  - `dotnet build` does not require Node to be installed — a developer working only on the API doesn't need pnpm.
- **Negative:**
  - Developer needs two terminals (or one terminal multiplexer). A minor ergonomics cost.
  - Releasing locally is two commands instead of one. Acceptable because release builds are rare locally.
- **Neutral:**
  - If A1 (MSBuild target) ever becomes desirable (e.g., for `dotnet publish` to be a single command), it is additive — can be wired without rebuilding the CI pipeline.

## Related

- [[0017-portal-web-stack-sveltekit]] — SvelteKit + adapter-static, served from `wwwroot/` in production
- [[0022-solution-scope-and-layout]] — where Portal.Web lives in the repo
