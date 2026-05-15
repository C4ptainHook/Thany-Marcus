# ADR-0025: OpenAPI + Scalar + hand-written TS contracts

Status: Accepted
Date: 2026-05-15

## Context

Portal.Api is consumed by SvelteKit (Portal.Web) in the same repo. Cloud.Api (per the plan, owned by CLOUD-001) will be consumed by the Obsidian plugin (PLUGIN-001). Both consumers are TypeScript.

The standard 2020-era pattern is to generate TypeScript types from the .NET API surface — emit OpenAPI from .NET, run a TS codegen step, commit generated `.d.ts` files. This sells "contract drift impossible" as the headline benefit.

But the math depends on the contract surface. Portal.Web ↔ Portal.Api is ~15–20 endpoints; Plugin ↔ Cloud.Api is another ~10–15. Total ≈ 30–40 contracts, most with 3–10 simple fields. Hand-writing them is ~2 hours of typing; drift over the thesis lifetime is bounded by integration tests (see [[0023-test-stack]]).

A separate but adjacent decision: even without TS codegen, an OpenAPI spec has *documentation* value (interactive UI, thesis appendix, future external consumers).

## Options considered

### OpenAPI doc generation

- **A. `Microsoft.AspNetCore.OpenApi`** — built into the framework since .NET 9. Emits OpenAPI 3.x at `/openapi/v1.json`. No UI included.
- **B. Swashbuckle.AspNetCore** — long-standing third party. Generates spec + bundles SwaggerUI. Was the default in `dotnet new` templates pre-.NET 9.
- **C. NSwag** — spec + UI + can emit C#/TS client code via MSBuild.

### OpenAPI UI

- **A. Scalar (`Scalar.AspNetCore`)** — modern, fast, the de facto SwaggerUI replacement in 2025–2026.
- **B. SwaggerUI** (via Swashbuckle).
- **C. ReDoc.**
- **D. None.**

### TS type strategy

- **A. `openapi-typescript`** (npm package) — consumes OpenAPI JSON, emits a single `.d.ts` file with all schemas. Pure-TS tool. Types only.
- **B. NSwag CLI** — emits TS clients (request methods + types). More opinionated.
- **C. Hand-write types in plugin and Portal.Web.** Drift bounded by tests; no codegen step.

## Decision

- **OpenAPI generation: A (`Microsoft.AspNetCore.OpenApi`).**
- **UI: A (Scalar).**
- **TS types: C (hand-written).**

### Why hand-written TS types

Honest cost-benefit at this scale:

| | Hand-written | Generated |
|---|---|---|
| Setup | 0 min | ~30 min wiring + committed artifact + CI step |
| Per-contract cost | ~5 min typing | ~0 (auto) |
| Lifetime drift cost | Caught by integration tests | Caught by generation step |
| Type shape | Hand-tuned for domain | Nested unwrapping (`paths['/api/clouds']['post']['requestBody']['content']['application/json']`) |
| Discriminated unions | Direct TS unions | Round-trip awkwardness from C# |
| DateTime / nullable | Hand-decide per field | Whatever the codegen tool picks |
| AI-agent friction | None | Agents sometimes edit generated files |

For ~40 trivial contracts, hand-writing is ~2 hours total. Generation pipeline maintenance over 3 weeks is comparable. Hand-written types are better-shaped for consumption.

Generation pays off when contracts are ≥100, change weekly, or cross team boundaries — none of which apply.

### Why keep OpenAPI doc generation anyway

Even without TS codegen, `AddOpenApi()` in .NET 10 is one line and emits the spec at `/openapi/v1.json`. Costs nothing. Earns:

- **Scalar at `/scalar`** as a developer tool for poking at endpoints during PORTAL-002+ work.
- **Thesis appendix material** — a clean API reference screenshot or PDF export.
- **Future external-consumer story** — if anyone ever wants to integrate, they have a spec.

### Concrete shape

`Program.cs` additions in PORTAL-001:

```csharp
builder.Services.AddOpenApi();        // built-in, emits /openapi/v1.json
// ...
app.MapOpenApi();
app.MapScalarApiReference();          // /scalar
```

NuGet references via central package management:

```
Microsoft.AspNetCore.OpenApi          (transitive via framework reference)
Scalar.AspNetCore
```

TS consumers hand-write types alongside their fetch wrappers:

```ts
// src/lib/api.ts in Portal.Web

export interface Cloud {
  id: string;
  provider: 'digitalocean' | 'azure';
  status: 'pending' | 'planning' | 'applying' | 'dns_registering'
        | 'cert_provisioning' | 'admin_registering' | 'ready' | 'failed';
  domain: string | null;
  createdAt: string;
}

export interface CreateCloudRequest {
  provider: 'digitalocean' | 'azure';
  region: string;
  sshKey: string;
}

export class ApiError extends Error { /* ... */ }

export async function createCloud(req: CreateCloudRequest): Promise<{ id: string }> {
  const res = await fetch('/api/clouds', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(req),
    credentials: 'include',
  });
  if (!res.ok) throw new ApiError(`POST /api/clouds failed: ${res.status}`);
  return res.json();
}
```

### What this means for `ThanyMarcus.Shared`

It stays near-empty. The cross-language contract is HTTP/JSON, not a shared .NET DTO library. Cross-.NET shared types (constants, common result types) trickle in when CLOUD-001 starts needing them.

## Consequences

- **Positive:**
  - No generated files in git. PRs show contract changes as legible diffs.
  - .NET build does not need Node; TS build does not need .NET.
  - Spec endpoint and Scalar UI are real developer affordances at zero ongoing cost.
  - TS types are hand-tuned for consumers (e.g., `'ready' | 'failed'` discriminated union directly, not nested under generated paths).
- **Negative:**
  - Possible drift between .NET DTO and TS interface. Caught by `WebApplicationFactory<Program>` integration tests that exercise real HTTP serialization (see [[0023-test-stack]]). For ~40 contracts, the drift window is small.
- **Neutral:**
  - If contract volume grows 5× later, reintroducing `openapi-typescript` is a one-time additive change — the spec already exists.

## Related

- [[0017-portal-web-stack-sveltekit]] — Portal.Web consumes the hand-written types
- [[0022-solution-scope-and-layout]] — `Shared` stays nearly empty
- [[0023-test-stack]] — drift caught by integration tests
