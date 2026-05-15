# ADR-0018: Portal API style — Minimal APIs + Vertical Slice Architecture

Status: Accepted
Date: 2026-05-15

## Context

Portal.Api is ~25 endpoints across ~6 feature areas (Auth, Provisioning, CloudManagement, PluginTokens, Destroy, RecoveryCodes). Plan §27 dictates a feature-folder repo layout (`Features/<Slice>/`) but does not commit to a *dispatch mechanism* (controllers vs Minimal APIs vs FastEndpoints) or to a request-handling pattern (mediator vs direct).

Several once-mainstream .NET patterns now carry hidden costs:
- **MediatR** moved to commercial licensing in January 2025. The "ASP.NET Core + MediatR + CQRS" template that dominated 2020–2024 is no longer free-by-default.
- **Full CQRS** (separate read and write models, projections, eventual consistency) is a fit for systems with read-scale or read/write shape divergence. Portal.Api has neither — a few hundred users in the thesis lifetime, and `Cloud` writes return the same shape `Cloud` reads return.
- **Wolverine + Marten (the "Critter Stack")** is a coherent framework offering messaging + sagas + outbox + event store + projections. Portal.Api uses ≤1.5 of those patterns (one saga, one outbox-shaped problem), so the framework's value-to-leverage ratio is poor.

The portal's job is to provide a thin, transactional admin surface. Architectural sophistication spent here doesn't appear in the thesis contribution (artifact capture, LLM routing, entity hubs, graph emergence).

## Options considered

- **A. Controllers + MediatR + CQRS** (typical 2020 template). Each operation is an `ICommand` or `IQuery` dispatched through `IMediator`. Two folders of handler classes per feature. MediatR licensing trap. Heavy ceremony when the read shape equals the write shape.
- **B. Minimal APIs + Vertical Slice Architecture, no mediator.** Each feature folder owns its endpoint + handler + DTOs + DB access. Endpoints registered inline via `MapPost("/api/clouds", CreateCloud.Handle).RequireAuthorization()` where `CreateCloud.Handle` is a static method co-located with the request and response records.
- **C. FastEndpoints (REPR pattern).** A vertical-slice-friendly endpoint toolkit; each endpoint is a class. Less ceremony than MediatR, more than plain Minimal APIs. Real value at scale (50+ endpoints, complex pipelines); marginal here.
- **D. Wolverine (Critter Stack mediator + saga engine).** Full framework. Adds `WolverineFx.Http` for endpoint auto-discovery, durable sagas, outbox, scheduled jobs. Replaces ~all of the above. For one saga + transactional CRUD, the ratio of framework adoption to leverage is poor.

## Decision

**Minimal APIs + Vertical Slice Architecture, no mediator.** Each feature folder is self-contained:

```
Features/Auth/
  User.cs                       # entity
  UserConfiguration.cs          # IEntityTypeConfiguration<User>
  StartGoogleSignIn.cs          # endpoint + request/response records + handler (one file)
  HandleGoogleCallback.cs
  EnableTotp.cs
  VerifyTotp.cs
  ...
  AuthEndpoints.cs              # static MapAuthEndpoints(this WebApplication app) extension
```

`Program.cs` composes the slices:

```csharp
app.MapAuthEndpoints();
app.MapProvisioningEndpoints();
app.MapCloudManagementEndpoints();
app.MapPluginTokenEndpoints();
app.MapDestroyEndpoints();
app.MapRecoveryCodeEndpoints();
```

A single endpoint, end-to-end, looks like:

```csharp
public static class CreateCloud
{
    public record Request(string Provider, string Region, string SshKey);
    public record Response(Guid Id);

    public static async Task<Results<Created<Response>, ValidationProblem>> Handle(
        Request req,
        PortalDbContext db,
        IJobQueue jobs,
        HttpContext ctx)
    {
        var userId = ctx.User.GetUserId();
        var cloud = new Cloud(Guid.NewGuid(), userId, req.Provider, req.Region, "pending");
        db.Clouds.Add(cloud);
        await jobs.EnqueueProvisionAsync(cloud.Id, req);
        await db.SaveChangesAsync();
        return TypedResults.Created($"/api/clouds/{cloud.Id}", new Response(cloud.Id));
    }
}
```

Cross-cutting concerns covered by stock framework primitives:
- **Auth**: `[Authorize]` / `RequireAuthorization()`; cookie middleware from `AddAuthentication().AddCookie().AddGoogle()`.
- **Validation**: per-endpoint inline or via `MinimalApis.Extensions` / `FluentValidation` — decision deferred to first endpoint that needs it.
- **Transactions**: EF Core's `SaveChangesAsync` is implicitly transactional; explicit `BeginTransactionAsync` where multi-step.
- **Error mapping**: `Results.Problem(...)` + `ProblemDetails` middleware.
- **Logging**: injected `ILogger<T>`.

## Consequences

- **Positive:**
  - No third-party mediator. No 2024-licensing trap (MediatR), no framework adoption (Wolverine/Marten).
  - Each feature folder is grep-discoverable end-to-end: open `CreateCloud.cs` to see the request shape, response shape, validation, DB access, and HTTP wiring in one file.
  - Thesis writeup can describe the API surface in concrete terms ("each endpoint is a static method registered via `MapPost`") without invoking framework abstractions an evaluator might not know.
  - Smallest possible learning surface for AI agents — Minimal APIs are extensively training-covered.
- **Negative:**
  - No mediator means no out-of-the-box cross-cutting pipeline (logging, validation, retry decorators around every command). Each concern is added via ASP.NET Core middleware or per-endpoint code. For ≤30 endpoints this is not a problem.
  - Cannot trivially swap dispatch transports later (e.g., expose the same handlers as in-process messages or as gRPC calls). Acceptable — there is no such requirement.
- **Neutral:**
  - The Provisioning slice still has internal complexity (saga + worker), but that complexity lives in the slice, not in framework wiring. See [[0019-background-work-and-saga-durability]].

## Related

- [[0019-background-work-and-saga-durability]] — Postgres-backed job queue + mutable status, no event sourcing
- [[0024-dbcontext-shape]] — single DbContext + per-feature IEntityTypeConfiguration
- `plans/cloud-pivot-plan-2026-05-13.md §27` — vertical-slice folder layout
