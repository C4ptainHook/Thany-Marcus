# ADR-0017: Portal.Web stack — SvelteKit with adapter-static SPA

Status: Accepted
Date: 2026-05-15

## Context

The 2026-05-13 cloud-pivot plan hedged the portal frontend as "Blazor or static SPA" without committing. Resolving the fork is a precondition for PORTAL-001, because it determines whether `ThanyMarcus.Portal.Web/` is a .NET project (`dotnet new blazor`) or a TypeScript workspace, and it shapes how authentication tokens flow, how the wizard streams Terraform progress, and whether a Node.js runtime is needed in production.

The portal has a small surface (≈5 admin screens: provisioning wizard with live Terraform progress, cloud-list dashboard, plugin-token issuance, account/TOTP settings, destroy). Time-on-portal is low — the user-facing surface for capture and graph viewing is Obsidian + the plugin, not the portal.

The plugin (PLUGIN-001) is committed to TypeScript regardless. Whatever portal frontend we pick, the TypeScript toolchain has to exist in the repo.

## Options considered

- **A. Blazor Server (hosted in Portal.Api).** Cookie auth + Google SSO is a one-liner. SignalR/circuits make live Terraform progress streaming free (`StateHasChanged()` after each output line). One language end-to-end except for the plugin. Downsides: ships full Blazor + SignalR runtime to the client; "circuits + render modes + interactivity locations" decision tree adds complexity unrelated to the problem; no reuse of the TS contract pipeline the plugin already needs.
- **B. Blazor WebAssembly.** Download size and slower first paint without compensating wins. Would still need Portal.Api to expose JSON for it to call. No advantage over Blazor Server other than statelessness.
- **C. Next.js (React).** Mature ecosystem, large component libraries. Larger runtime payload (~80–100 KB minimum), more framework feature surface (App Router, Server Components, Server Actions, edge runtime, ISR) the portal does not use. Static export (`output: 'export'`) works but is a degraded path with caveats (no Image optimization, no API routes, no middleware).
- **D. SvelteKit with `adapter-static` in SPA fallback mode.** Compiles to ~10–15 KB runtime payload. `adapter-static` with `fallback: 'index.html'` is a first-class output mode (not a degraded one) — emits HTML/JS/CSS that Portal.Api serves out of `wwwroot/`. Authoring model (Svelte 5 runes: `$state`, `$derived`, `$effect`) is smaller-surface than React hooks + Server Components. Reuses the TypeScript toolchain the plugin needs. Costs: smaller ecosystem than React/Next, less AI-agent training data — both bounded by the portal's small surface area.
- **E. Plain Svelte 5 + Vite (no SvelteKit).** Lighter still, but loses SvelteKit's routing, `load` conventions, and form actions for no real gain at this size.

## Decision

**SvelteKit with `adapter-static` configured for SPA fallback mode**, served by Portal.Api as static files from `wwwroot/`.

Concrete shape:

```
src/ThanyMarcus.Portal.Web/         # pnpm workspace, NOT in the .sln
  svelte.config.js                  # adapter-static with fallback: 'index.html'
  vite.config.ts                    # dev: proxies /api → http://localhost:5000
  src/routes/+page.svelte           # SPA entry, all routing client-side
  package.json                      # pnpm-managed

# pnpm build produces:
src/ThanyMarcus.Portal.Web/build/   # index.html + _app/immutable/chunks + _app/immutable/assets

# Copied at publish into Portal.Api's wwwroot/:
src/ThanyMarcus.Portal.Api/wwwroot/

# Program.cs:
app.UseStaticFiles();
app.MapControllers();                          // /api/*
app.MapFallbackToFile("index.html");           // everything else → SPA shell
```

`svelte.config.js`:

```js
import adapter from '@sveltejs/adapter-static';
export default {
  kit: {
    adapter: adapter({ fallback: 'index.html' }),
  },
};
```

Auth is cookie-session, same-origin (Portal.Api serves both the API and the SPA shell, so no CORS). Live Terraform progress for PORTAL-011 uses Server-Sent Events at `/api/provisioning/{id}/events` rather than SignalR — see [[0020-server-push-sse]].

## Consequences

- **Positive:**
  - No Node.js runtime in the deployed portal — Portal.Api is the only process.
  - The TypeScript contract pipeline that the plugin needs is reused by the portal (one toolchain).
  - Modern admin UI ergonomics (shadcn-svelte, Skeleton, Melt UI) at a small runtime payload.
  - SSR remains an option (swap adapter, stand up a Node sidecar) if it ever becomes useful — no rewrite required.
  - Same-origin cookie auth flow: no JWT/bearer indirection for the human user. Bearer tokens reserved for the plugin → cloud path.
- **Negative:**
  - Smaller ecosystem than React/Next. Fewer pre-built component libraries; fewer Stack Overflow answers. Bounded by portal's small surface.
  - Less AI-agent training data than React/Next. Bounded by the same scope.
  - SSE wizard progress costs ~half a day of code that SignalR would have given for free with Blazor (see [[0020-server-push-sse]]).
- **Neutral:**
  - `ThanyMarcus.Shared` shrinks in importance — the cross-boundary contract is HTTP/JSON, not a shared .NET DTO library (see [[0025-openapi-and-hand-written-ts-contracts]]).

## Related

- [[0020-server-push-sse]] — SSE chosen over SignalR/WebSockets for live progress streaming
- [[0021-dev-workflow]] — Vite dev server proxy and CI build steps for this stack
- [[0022-solution-scope-and-layout]] — where the SvelteKit workspace sits relative to `ThanyMarcus.sln`
- [[0025-openapi-and-hand-written-ts-contracts]] — TS types are hand-written, not generated
- `plans/cloud-pivot-plan-2026-05-13.md §27` — repo layout
