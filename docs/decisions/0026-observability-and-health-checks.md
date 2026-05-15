# ADR-0026: Observability + health checks

Status: Accepted
Date: 2026-05-15

## Context

Portal.Api runs a small but non-trivial workload: ~25 HTTP endpoints, an `IHostedService` worker draining a Postgres-backed job queue, outbound HTTP to Terraform/Cloudflare/user-clouds. Production deployment is a single VPS via Docker Compose, fronted by Caddy ([[0027-reverse-proxy-caddy]]).

Two operational concerns need scaffolding in PORTAL-001:

1. **Observability** — request rates, durations, error rates, runtime health (GC, threadpool, exceptions), DB query metrics, outbound HTTP metrics. Without these, debugging production behavior is guesswork.
2. **Health checks** — orchestration layers (Docker Compose `healthcheck:`, Caddy upstream health, future K8s readiness probes) need a real signal beyond "the process is up."

Both have well-trodden modern patterns in .NET 9/10.

## Options considered

### Observability library

- **A. Stock `Microsoft.Extensions.Logging` only.** Logs to stdout, JSON formatter, no metrics, no traces. Cheapest. Operationally weak — no aggregation surface for request rates, durations, error rates without log-grep heroics.
- **B. Serilog + sinks.** Mature structured logging. Replaces stock logger. No metrics or traces.
- **C. OpenTelemetry SDK with Prometheus exporter** for metrics, plus tracing instrumentation (exporter deferred). Vendor-neutral, framework-blessed in .NET 9+ (Microsoft is heavily investing in OTel). Stock `Microsoft.Extensions.Logging` is kept for log records.
- **D. Application Insights (Azure).** Vendor-locked, paid, requires Azure.
- **E. Datadog / New Relic agents.** Vendor-locked, paid.

### Metrics backend

- **A. Prometheus** (self-hosted) with `OpenTelemetry.Exporter.Prometheus.AspNetCore`. Pull-based, simple, mature. Pairs with Grafana for dashboards.
- **B. OTLP push to a collector.** More flexible (collector can fan out to Prometheus + Tempo + Loki) but adds the OTel Collector as another deployment.
- **C. Direct OTLP to Grafana Cloud.** External managed service, monthly cost.

### Tracing backend

- **A. Console exporter** (dev-only) — spans go to stdout for manual inspection.
- **B. Jaeger / Tempo / SigNoz** — real trace backend, separate deployment.
- **C. None** — instrumentation disabled.

### Health checks

- **A. `MapGet("/api/health", () => Results.Ok())`** — single trivial endpoint. Liveness-only.
- **B. `AddHealthChecks()` with `/health/live` + `/health/ready` split, tag-based dispatch.** Each component (DB, downstream service, Terraform binary) registers a check; live and ready endpoints filter by tag.

## Decision

- **Observability: C** — OpenTelemetry SDK + stock `Microsoft.Extensions.Logging` with `AddJsonConsole()`.
- **Metrics backend: A** — Prometheus, scraped over the internal docker-compose network only.
- **Tracing backend: A for now** (console exporter); real backend deferred.
- **Health checks: B** — `AddHealthChecks()` with `/health/live` + `/health/ready` split.

### Concrete shape (Program.cs additions in PORTAL-001)

```csharp
// --- Logging ---
builder.Logging.AddJsonConsole(opts =>
{
    opts.IncludeScopes = true;
    opts.UseUtcTimestamp = true;
});

// --- OpenTelemetry ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService(
        serviceName: "ThanyMarcus.Portal.Api",
        serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "dev"))
    .WithMetrics(m => m
        .AddAspNetCoreInstrumentation()       // HTTP server: rates, durations, statuses
        .AddHttpClientInstrumentation()        // outbound HTTP (Cloudflare, user clouds)
        .AddRuntimeInstrumentation()           // GC, threadpool, exceptions, allocations
        .AddProcessInstrumentation()           // CPU, memory, file descriptors
        .AddPrometheusExporter())              // scrape endpoint
    .WithTracing(t => t
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddConsoleExporter());                // dev sink only; real backend deferred

// --- Health checks ---
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live", "ready"]);
    // DB check added in PORTAL-002:
    // .AddNpgSql(cfg.GetConnectionString("Portal")!, tags: ["ready"])

// ...

app.MapPrometheusScrapingEndpoint();          // GET /metrics

app.MapHealthChecks("/health/live",  new HealthCheckOptions { Predicate = c => c.Tags.Contains("live")  });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });
```

NuGet references via central package management:

```
OpenTelemetry
OpenTelemetry.Extensions.Hosting
OpenTelemetry.Instrumentation.AspNetCore
OpenTelemetry.Instrumentation.Http
OpenTelemetry.Instrumentation.Runtime
OpenTelemetry.Instrumentation.Process
OpenTelemetry.Exporter.Prometheus.AspNetCore
OpenTelemetry.Exporter.Console                 # dev-only trace sink
Microsoft.Extensions.Diagnostics.HealthChecks
```

Added in later tickets:

```
OpenTelemetry.Instrumentation.EntityFrameworkCore   (PORTAL-002)
Npgsql.OpenTelemetry                                 (PORTAL-002, ships with Npgsql)
AspNetCore.HealthChecks.NpgSql                       (PORTAL-002)
```

### Prometheus deployment shape

`/metrics` is **NOT exposed externally** through Caddy. Prometheus runs as a sibling container in the portal's docker-compose and scrapes `portal:5000/metrics` over the internal network only.

`infra/docker/portal/prometheus.yml`:

```yaml
global:
  scrape_interval: 15s

scrape_configs:
  - job_name: 'portal'
    metrics_path: '/metrics'
    static_configs:
      - targets: ['portal:5000']
```

`infra/docker/portal/docker-compose.yml` (PORTAL-017 wires this):

```yaml
prometheus:
  image: prom/prometheus:latest
  volumes:
    - ./prometheus.yml:/etc/prometheus/prometheus.yml
    - prometheus-data:/prometheus
  command:
    - '--config.file=/etc/prometheus/prometheus.yml'
    - '--storage.tsdb.retention.time=30d'
  # No host port — internal-only
```

Grafana for dashboards is optional and additive; not in PORTAL-001 scope.

### Why tracing exporter is deferred

Metrics alone is the high-value, cheap piece — request rates, durations, error rates, runtime health become visible immediately. Distributed tracing needs a trace backend (Jaeger / Tempo / Grafana Tempo / SigNoz), which is non-trivial to operate. For a single .NET process + Postgres + outbound HTTP, spans-in-logs already cover ~90% of trace value. A real trace backend can be added later (swap `AddConsoleExporter` to `AddOtlpExporter` + stand up Jaeger all-in-one as a docker-compose service) — ~1 hour, not worth pre-investing.

## Consequences

- **Positive:**
  - Observability is real on day one — request rates, durations, error rates, GC, threadpool all flow into Prometheus.
  - Health check infrastructure is scaffolded with the proper liveness/readiness split — PORTAL-002 just adds `.AddNpgSql(..., tags: ["ready"])`.
  - No vendor lock-in. OTel SDK lets you swap exporters later (OTLP → any backend) without changing instrumentation.
  - `/metrics` is privacy-safe: scraped only inside the docker network; no public exposure.
  - Stock `Microsoft.Extensions.Logging` with JSON formatter feeds any future log aggregator (Loki, Elasticsearch, Datadog).
- **Negative:**
  - OTel SDK is ~5 NuGet packages on Portal.Api. Increases the project file but each is small and well-maintained by Microsoft / the OTel WG.
  - Tracing instrumentation is enabled but only console-sinked, which means traces emit to stdout in dev — a minor log-volume cost. Filterable.
- **Neutral:**
  - Grafana / dashboards / alerting are post-MVP work.
  - Trace backend can be added later trivially.

## Related

- [[0019-background-work-and-saga-durability]] — worker emits metrics + spans through this instrumentation
- [[0027-reverse-proxy-caddy]] — Caddy does NOT proxy `/metrics`; internal scrape only
- `plans/cloud-pivot-plan-2026-05-13.md §27` — `infra/docker/portal/` deployment layout
