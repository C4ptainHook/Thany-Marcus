# PORTAL-007 Provisioning saga — Handoff Brief

**Goal:** land the actual saga logic in `ThanyMarcus.Portal.SagaWorker` (the project scaffolded by PORTAL-007a): the 7-phase state machine from ADR-0033, the `SemaphoreSlim(3)` claim loop, the per-phase handlers, the `ITerraformRunner` subprocess wrapper, an `ICloudflareDnsClient` stub, the workspace-on-disk layout, plugin-cache pre-warm, and crash-recovery on worker boot. After this ticket a `pending` row in `provisioning_jobs` drives a real terraform invocation against a stub module and walks the state machine to a terminal status. **Provider modules (PORTAL-008/009), the real Cloudflare client (PORTAL-010b), cloud-init (PORTAL-010), the wizard UI (PORTAL-011), and the cloud's inbound callback handler (PORTAL-016) are explicitly OUT of scope** — they're the consumers and producers around this saga, not the saga itself.

Estimated **1 person-day** with heavy AI-agent assistance. The original ticket-list estimate was 1 day for a much smaller "TerraformRunner wrapper" scope; ADR-0033 turned this into the saga's center of gravity. The estimate holds because PORTAL-007a removed the structural overhead.

## Where decisions live (read before doing anything)

- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the canonical specification. Read **all of it**. Every implementation detail in this handoff traces back to a numbered sub-decision in that ADR. If something in this handoff seems to contradict the ADR, the ADR wins; flag the discrepancy.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — the queue substrate (Postgres SKIP LOCKED + lease + mutable status, no event sourcing). ADR-0033 amends this with `next_visible_at` for delayed visibility.
- **`docs/decisions/0028-schema-conventions.md`** — naming, `IClock`, `TimestampInterceptor`. The new columns must follow these conventions (snake_case mapping done by `UseSnakeCaseNamingConvention`).
- **`docs/decisions/0029-type-mappings.md`** — `next_visible_at`, `phase_started_at`, `lease_expires_at` are NodaTime `Instant`s mapped to `timestamptz`. `events_log` and `tf_outputs` are `JsonDocument` mapped to `jsonb`.
- **`docs/decisions/0032-fk-cascades-and-soft-delete.md`** — `provisioning_jobs.cloud_id` FK is RESTRICT on cloud delete per the ADR amendment (the existing migration says `Cascade`; this handoff *changes* that to `Restrict` so terminal-job-rows survive a soft-deleted cloud).
- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — defines the `awaiting_cloud_callback` trigger payload and the `awaiting_cert` polling contract (`GET /admin/health` → `{ cert_ready: bool }`). The worker is the *consumer* of those contracts here.
- **`docs/decisions/0035-wizard-progress-transport.md`** — `events_log` jsonb shape (what each phase appends) and the `GET /api/clouds/{id}/status` endpoint that reads it. PORTAL-007 is the *writer*; the reader lives in PORTAL-011.
- **`plans/portal-007a-handoff.md`** — landed first; provides the SagaWorker project, the empty `BackgroundService`, the docker-compose stack with shared DP keys + `terraform_data` volume + terraform binary in the image. **This ticket assumes PORTAL-007a is merged.**
- **`plans/portal-005-handoff.md`** — `IProviderTokenVault.DecryptAsync(userId, provider, dek, ct)` is the contract the saga calls to get plaintext provider credentials. Hold the plaintext in a `byte[]`, pass it to terraform via `TF_VAR_*` env var, `CryptographicOperations.ZeroMemory(...)` immediately after.
- **`plans/portal-003f-handoff.md`** — `IInfraOpUnlockCache` is Postgres-backed and async; the SagaWorker calls `TryGetAsync(userId, dek32, ct)` to fetch the DEK. `SlidingTtl = 10 minutes` — relevant for the "what if the unlock expired mid-saga" handling below.
- **Memory files**: `portal_architecture.md` (Postgres job queue + SSE), `portal_deployment.md` (single VM, Docker Compose), `portal_tooling.md` (.NET 10, warnings-as-errors, Shouldly + xUnit v3, OTel console exporter in dev).

**Do not invent state machine values, polling cadences, or column names.** They are all pinned in ADR-0033. If a defensible default is missing, prefer the ADR's listed cadences (5s × first 60s then 30s for cert polling, 5min for cloud-callback timeout, 30min cert deadline per DEC-003).

## Scope boundary (precise)

**In scope:**
- Migration: add `next_visible_at`, `lease_expires_at`, `claimed_by`, `attempt_count`, `phase_started_at`, `events_log`, `tf_outputs` to `provisioning_jobs`; add the partial index on `next_visible_at` for non-terminal rows; change `cloud_id` FK from `Cascade` to `Restrict`; widen the existing `Attempts`/`WorkerId`/`LeaseExpires`/`LastError` columns to the ADR's names (rename if needed via EF migrations).
- `SagaStatus` constant strings — 13 values, matching the ADR exactly.
- `ITerraformRunner` interface + `TerraformRunner` real impl (in `Portal.SagaWorker/Infrastructure/Terraform/`) + `FakeTerraformRunner` test double (in test project).
- `ICloudflareDnsClient` interface + `StubCloudflareDnsClient` (returns canned success; real impl lands in PORTAL-010b) + `FakeCloudflareDnsClient` test double.
- The claim loop in `SagaWorker.cs` (replaces the placeholder from PORTAL-007a).
- Per-phase handlers under `Portal.SagaWorker/Features/Provisioning/Handlers/`:
  - `TfPlanningHandler`
  - `TfApplyingHandler`
  - `DnsCreatingHandler`
  - `AwaitingCloudCallbackHandler` (worker-side timeout check; callback writes happen in PORTAL-016)
  - `AwaitingCertHandler` (one poll per claim)
  - `RollingBackTfHandler`
  - `RollingBackDnsHandler`
- A `SagaPhaseDispatcher` (or similar) — the switch on `status` that picks the handler.
- A workspace-on-disk helper: `WorkspaceLayout` writes `main.tf`, `backend.tf`, `variables.auto.tfvars` for a given job into `/var/lib/portal/terraform/jobs/<job_id>/`.
- Plugin-cache pre-warm step in the SagaWorker container entrypoint (a small bash script in `infra/docker/saga-worker/`).
- Crash recovery routine on SagaWorker boot (force-unlock terraform `pg` workspaces for stale-claimed `tf_applying`/`rolling_back_tf` rows; lazy sweep terminal job dirs older than 7 days).
- A `StubTerraformModule` baked into the saga-worker image at `/app/terraform-modules/stub/` — a `null_resource` + `local-exec` module the saga can exercise end-to-end without DO/Azure credentials. PORTAL-008/009 will add `digitalocean/` and `azure/` modules alongside it.
- A small `EnqueueGuard` helper in `Portal.Api/Features/CloudManagement/` exposing `EnsureNoInFlightJobAsync(cloudId, ct)` — returns the conflicting job's id+status if one exists. The cloud-create / destroy endpoints (landed by PORTAL-011 / PORTAL-015) consume it; we land the helper now so the saga's invariants are enforced wherever rows are enqueued.
- Tests:
  - Unit tests per handler: success path + the failure path the ADR's diagram dictates (e.g., `TfApplyingHandler` on `terraform apply` failure → `rolling_back_tf`).
  - `FakeTerraformRunner` and `FakeCloudflareDnsClient` drive the unit tests.
  - End-to-end saga integration test against Testcontainers Postgres: insert a `pending` row using the stub module, run the worker for ~30 s, assert the row reaches `awaiting_cloud_callback` (the natural stop without a real cloud answering back).
  - Concurrency test: insert 5 `pending` rows, assert exactly 3 are in `tf_planning`/`tf_applying` simultaneously, the other 2 wait.
  - Crash-recovery test: pre-seed a `tf_applying` row with `lease_expires_at < now`, boot the worker, assert force-unlock ran and the row was re-claimed.

**Out of scope (DO NOT touch):**
- **`digitalocean/` and `azure/` terraform modules** — PORTAL-008 / PORTAL-009. Use the stub module here.
- **Real Cloudflare API client (HttpClient hitting `api.cloudflare.com/v4`)** — PORTAL-010b. The `StubCloudflareDnsClient` returns `{ Success = true, RecordId = "stub-" + jobId }` and that's it.
- **`cloud-init.sh.tpl`** — PORTAL-010. Templating is the provider modules' problem when they land.
- **Wizard UI / cloud-create endpoint** — PORTAL-011. The `EnqueueGuard` helper is the only thing this ticket lands on the API side.
- **Destroy endpoint** — PORTAL-015. The `RollingBack*` handlers are exercised when a saga fails partway and rolls itself back; user-initiated destroy is its own enqueue path that PORTAL-015 wires up.
- **Cloud-admin-token receive endpoint** (`POST /api/clouds/{id}/callback` or similar) — PORTAL-016. The saga waits for `awaiting_cloud_callback → awaiting_cert` to be flipped by *some other writer*; the test fakes this via direct SQL.
- **SSE / wizard status endpoint** — PORTAL-011 / ADR-0035. The saga *writes to* `events_log`; PORTAL-011 reads from it.
- **Production observability** (OTel collector, scraping) — beyond console exporters from PORTAL-007a. Follow-up for whoever takes the prod-readiness pass.

## Output of PORTAL-007 — final directory state

```
Thany-Marcus/
├── src/
│   ├── ThanyMarcus.Portal.Api/
│   │   ├── Infrastructure/Database/Migrations/
│   │   │   └── 20260516NNNNNN_ProvisioningSagaColumns.cs    # NEW: column additions + FK change
│   │   └── Features/CloudManagement/
│   │       └── EnqueueGuard.cs                              # NEW: in-flight-job check helper
│   └── ThanyMarcus.Portal.SagaWorker/
│       ├── SagaWorker.cs                                    # CHANGED: replace placeholder with claim loop
│       ├── Program.cs                                       # CHANGED: register handlers + runners + crash-recovery service
│       ├── Features/Provisioning/
│       │   ├── SagaStatus.cs                                # NEW: 13 const strings + IsTerminal helper
│       │   ├── SagaPhaseDispatcher.cs                       # NEW: switch on status → handler
│       │   ├── WorkspaceLayout.cs                           # NEW: write main.tf/backend.tf/tfvars
│       │   ├── EventsLogAppender.cs                         # NEW: jsonb append helper per ADR-0035 shape
│       │   ├── CrashRecoveryService.cs                      # NEW: IHostedService run-once on boot
│       │   └── Handlers/
│       │       ├── ISagaPhaseHandler.cs                     # NEW: HandleAsync(job, ct) contract
│       │       ├── TfPlanningHandler.cs                     # NEW
│       │       ├── TfApplyingHandler.cs                     # NEW
│       │       ├── DnsCreatingHandler.cs                    # NEW
│       │       ├── AwaitingCloudCallbackHandler.cs          # NEW
│       │       ├── AwaitingCertHandler.cs                   # NEW
│       │       ├── RollingBackTfHandler.cs                  # NEW
│       │       └── RollingBackDnsHandler.cs                 # NEW
│       └── Infrastructure/
│           ├── Terraform/
│           │   ├── ITerraformRunner.cs                      # NEW
│           │   ├── TerraformRunner.cs                       # NEW: Process.Start wrapper
│           │   ├── TerraformResult.cs                       # NEW: exit code + stdout/stderr + parsed outputs
│           │   └── TerraformException.cs                    # NEW: thrown on non-zero exit
│           └── Cloudflare/
│               ├── ICloudflareDnsClient.cs                  # NEW
│               └── StubCloudflareDnsClient.cs               # NEW: canned success; real impl is PORTAL-010b
├── infra/docker/saga-worker/
│   ├── Dockerfile                                           # CHANGED: COPY entrypoint.sh + stub module
│   ├── entrypoint.sh                                        # NEW: plugin-cache prewarm then exec dotnet
│   └── terraform-modules/stub/
│       ├── main.tf                                          # NEW: null_resource + local-exec
│       └── outputs.tf                                       # NEW: a fake "ip" output
└── tests/ThanyMarcus.Portal.Tests/
    └── SagaWorker/
        ├── Handlers/
        │   ├── TfPlanningHandlerTests.cs                    # NEW
        │   ├── TfApplyingHandlerTests.cs                    # NEW
        │   ├── DnsCreatingHandlerTests.cs                   # NEW
        │   ├── AwaitingCloudCallbackHandlerTests.cs         # NEW
        │   ├── AwaitingCertHandlerTests.cs                  # NEW
        │   └── RollingBackTfHandlerTests.cs                 # NEW (DNS rollback test is a trivial mirror; combine)
        ├── SagaEndToEndTests.cs                             # NEW: stub module → awaiting_cloud_callback
        ├── SagaConcurrencyTests.cs                          # NEW: 5 rows → 3 concurrent
        ├── CrashRecoveryTests.cs                            # NEW: stale tf_applying → force-unlock + resume
        ├── Fakes/
        │   ├── FakeTerraformRunner.cs                       # NEW: scripted responses
        │   └── FakeCloudflareDnsClient.cs                   # NEW
        └── WorkspaceLayoutTests.cs                          # NEW: rendered files match expected shape
```

## Packages

- `System.Diagnostics.Process` is in the BCL; no package for terraform subprocess.
- `Polly` may help with the cert-polling retry shape, but the ADR's cadence is simple enough (`now + 5s` for first minute, `now + 30s` after) that hand-rolling it via `next_visible_at` is clearer. **Don't add Polly for this.**
- No new entries in `Directory.Packages.props`.

## Migration: 20260516NNNNNN_ProvisioningSagaColumns

Run `dotnet ef migrations add ProvisioningSagaColumns --project src/ThanyMarcus.Portal.Api` after updating `ProvisioningJob.cs` + `ProvisioningJobConfiguration.cs`. Expected `Up()`:

```csharp
public partial class ProvisioningSagaColumns : Migration
{
    protected override void Up(MigrationBuilder mb)
    {
        // Rename existing columns to match ADR-0033 naming.
        mb.RenameColumn("attempts",       "provisioning_jobs", "attempt_count");
        mb.RenameColumn("worker_id",      "provisioning_jobs", "claimed_by");
        mb.RenameColumn("lease_expires",  "provisioning_jobs", "lease_expires_at");
        mb.RenameColumn("last_error",     "provisioning_jobs", "last_error");           // unchanged name; keep
        // Widen smallint vs int decision: attempt_count is smallint per ADR.
        mb.AlterColumn<short>("attempt_count", "provisioning_jobs",
            type: "smallint", nullable: false, defaultValue: (short)0,
            oldClrType: typeof(int), oldType: "integer");

        mb.AddColumn<Instant>(
            name: "next_visible_at",
            table: "provisioning_jobs",
            type: "timestamp with time zone",
            nullable: false,
            defaultValueSql: "now()");

        mb.AddColumn<Instant>(
            name: "phase_started_at",
            table: "provisioning_jobs",
            type: "timestamp with time zone",
            nullable: true);

        mb.AddColumn<JsonDocument>(
            name: "events_log",
            table: "provisioning_jobs",
            type: "jsonb",
            nullable: false,
            defaultValueSql: "'[]'::jsonb");

        mb.AddColumn<JsonDocument>(
            name: "tf_outputs",
            table: "provisioning_jobs",
            type: "jsonb",
            nullable: true);

        // Drop the old PORTAL-002 indexes (their filters reference the renamed/obsolete status values).
        mb.DropIndex("ix_provisioning_jobs_pending_created_at", "provisioning_jobs");
        mb.DropIndex("ix_provisioning_jobs_inprogress_lease",   "provisioning_jobs");

        // New ADR-0033 index: partial on next_visible_at, excluding terminal statuses.
        mb.Sql(@"
            CREATE INDEX ix_provisioning_jobs_claimable_next_visible
            ON provisioning_jobs (next_visible_at)
            WHERE status NOT IN (
              'succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back'
            );
        ");

        // FK Cascade -> Restrict per ADR-0032 amendment in ADR-0033 §Related.
        mb.DropForeignKey("fk_provisioning_jobs_clouds_cloud_id", "provisioning_jobs");
        mb.AddForeignKey(
            name: "fk_provisioning_jobs_clouds_cloud_id",
            table: "provisioning_jobs",
            column: "cloud_id",
            principalTable: "clouds",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder mb)
    {
        // Mirror Up in reverse. Acceptable to be terse; thesis-scope downgrade.
        mb.DropForeignKey("fk_provisioning_jobs_clouds_cloud_id", "provisioning_jobs");
        mb.AddForeignKey(
            "fk_provisioning_jobs_clouds_cloud_id", "provisioning_jobs",
            "cloud_id", "clouds", principalColumn: "id",
            onDelete: ReferentialAction.Cascade);
        mb.Sql("DROP INDEX IF EXISTS ix_provisioning_jobs_claimable_next_visible;");
        mb.DropColumn("tf_outputs",        "provisioning_jobs");
        mb.DropColumn("events_log",        "provisioning_jobs");
        mb.DropColumn("phase_started_at",  "provisioning_jobs");
        mb.DropColumn("next_visible_at",   "provisioning_jobs");
        mb.RenameColumn("lease_expires_at","provisioning_jobs","lease_expires");
        mb.RenameColumn("claimed_by",      "provisioning_jobs","worker_id");
        mb.RenameColumn("attempt_count",   "provisioning_jobs","attempts");
        mb.AlterColumn<int>("attempts", "provisioning_jobs", type: "integer",
            nullable: false, oldClrType: typeof(short), oldType: "smallint");
    }
}
```

`ProvisioningJob.cs` updates: rename `Attempts → AttemptCount` (short), `WorkerId → ClaimedBy`, `LeaseExpires → LeaseExpiresAt`; add `NextVisibleAt` (Instant), `PhaseStartedAt` (Instant?), `EventsLog` (JsonDocument), `TfOutputs` (JsonDocument?). `ProvisioningJobConfiguration.cs` registers the new columns and the renames; the EF naming convention turns `LeaseExpiresAt` into `lease_expires_at`, etc.

## SagaStatus

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/SagaStatus.cs`:

```csharp
namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class SagaStatus
{
    public const string Pending                = "pending";
    public const string TfPlanning             = "tf_planning";
    public const string TfApplying             = "tf_applying";
    public const string DnsCreating            = "dns_creating";
    public const string AwaitingCloudCallback  = "awaiting_cloud_callback";
    public const string AwaitingCert           = "awaiting_cert";
    public const string RollingBackTf          = "rolling_back_tf";
    public const string RollingBackDns         = "rolling_back_dns";

    public const string Succeeded              = "succeeded";
    public const string FailedTf               = "failed_tf";
    public const string FailedDns              = "failed_dns";
    public const string FailedCallback         = "failed_callback";
    public const string FailedCert             = "failed_cert";
    public const string Cancelled              = "cancelled";
    public const string RolledBack             = "rolled_back";

    public static readonly IReadOnlySet<string> Terminal = new HashSet<string>(StringComparer.Ordinal)
    {
        Succeeded, FailedTf, FailedDns, FailedCallback, FailedCert, Cancelled, RolledBack,
    };

    public static bool IsTerminal(string status) => Terminal.Contains(status);
}
```

Mirror the same constants in `Portal.Api` (or expose via `ProjectReference` from the worker — preferred to avoid two-source-of-truth drift). Recommended: place `SagaStatus` in `Portal.Api/Features/Provisioning/SagaStatus.cs` and have the worker reference it via `using`. The worker already has a `ProjectReference` to `Portal.Api` from PORTAL-007a.

## ITerraformRunner

`Infrastructure/Terraform/ITerraformRunner.cs`:

```csharp
namespace ThanyMarcus.Portal.SagaWorker.Infrastructure.Terraform;

public interface ITerraformRunner
{
    Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct);
    Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct);
    Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct);
    Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct);
}

public sealed record TerraformResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}
```

`Infrastructure/Terraform/TerraformRunner.cs`:

```csharp
public sealed class TerraformRunner(ILogger<TerraformRunner> log) : ITerraformRunner
{
    private const string BinaryPath = "/usr/local/bin/terraform";
    private static readonly string PluginCache = "/var/lib/portal/terraform/plugin-cache";

    public Task<TerraformResult> InitAsync(string workdir, IReadOnlyDictionary<string, string> backendConfig, CancellationToken ct)
    {
        var args = new List<string> { "init", "-input=false", "-no-color" };
        foreach (var (k, v) in backendConfig)
            args.Add($"-backend-config={k}={v}");
        return RunAsync(workdir, args, envVars: ImmutableDictionary<string, string>.Empty, ct);
    }

    public Task<TerraformResult> PlanAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
        RunAsync(workdir, ["plan", "-input=false", "-no-color", "-out=plan.tfplan", "-json"], envVars, ct);

    public Task<TerraformResult> ApplyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
        RunAsync(workdir, ["apply", "-input=false", "-no-color", "-json", "plan.tfplan"], envVars, ct);

    public Task<TerraformResult> DestroyAsync(string workdir, IReadOnlyDictionary<string, string> envVars, CancellationToken ct) =>
        RunAsync(workdir, ["destroy", "-auto-approve", "-input=false", "-no-color", "-json"], envVars, ct);

    public async Task<JsonDocument> OutputJsonAsync(string workdir, CancellationToken ct)
    {
        var r = await RunAsync(workdir, ["output", "-json"], ImmutableDictionary<string, string>.Empty, ct);
        if (!r.Success) throw new TerraformException("output", r);
        return JsonDocument.Parse(r.Stdout);
    }

    public Task ForceUnlockAsync(string workdir, string lockId, CancellationToken ct) =>
        RunAsync(workdir, ["force-unlock", "-force", lockId], ImmutableDictionary<string, string>.Empty, ct)
            .ContinueWith(t =>
            {
                if (!t.Result.Success) throw new TerraformException("force-unlock", t.Result);
            }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

    private async Task<TerraformResult> RunAsync(
        string workdir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> envVars, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = BinaryPath,
            WorkingDirectory       = workdir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["TF_PLUGIN_CACHE_DIR"] = PluginCache;
        psi.Environment["TF_INPUT"]            = "0";
        psi.Environment["TF_IN_AUTOMATION"]    = "1";
        foreach (var (k, v) in envVars) psi.Environment[k] = v;

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new TerraformResult(p.ExitCode, await stdoutTask, await stderrTask);
    }
}
```

**Notes:**
- `-json` on plan/apply emits a stream of structured events to stdout. The saga captures the *whole stream* into `events_log` so the wizard can replay it. Translation of terraform-event-format → wizard-event-format lives in `EventsLogAppender` (next ADR-0035 follow-up); for this ticket, the appender treats the JSON lines as opaque and stores them under an `"events"` array entry tagged `"source":"terraform"`.
- Sensitive `TF_VAR_*` values flow via `psi.Environment` — *never* via the args list. `ps` listings on a Linux box show args; env vars are per-process and don't leak.
- `Process.Start`'s `WaitForExitAsync(ct)` cancels with a kill if the worker's stoppingToken trips. Acceptable: the saga resumes via crash-recovery on next boot.

## ICloudflareDnsClient (stub for this ticket)

```csharp
public interface ICloudflareDnsClient
{
    Task<DnsRecord> CreateAAsync(string subdomain, IPAddress ip, string cloudflareToken, CancellationToken ct);
    Task DeleteAsync(string recordId, string cloudflareToken, CancellationToken ct);
}

public sealed record DnsRecord(string Id, string Subdomain, IPAddress Ip);

public sealed class StubCloudflareDnsClient(ILogger<StubCloudflareDnsClient> log) : ICloudflareDnsClient
{
    public Task<DnsRecord> CreateAAsync(string subdomain, IPAddress ip, string _, CancellationToken __)
    {
        log.LogInformation("StubCloudflareDnsClient.CreateA: {Subdomain} -> {Ip}", subdomain, ip);
        return Task.FromResult(new DnsRecord(Id: "stub-" + Guid.NewGuid().ToString("N"), subdomain, ip));
    }

    public Task DeleteAsync(string recordId, string _, CancellationToken __)
    {
        log.LogInformation("StubCloudflareDnsClient.Delete: {RecordId}", recordId);
        return Task.CompletedTask;
    }
}
```

Wired in `Program.cs` as `AddSingleton<ICloudflareDnsClient, StubCloudflareDnsClient>()`. PORTAL-010b swaps the registration to the real `HttpClient`-based impl.

## SagaWorker claim loop (replaces PORTAL-007a placeholder)

```csharp
public sealed class SagaWorker(
    IServiceProvider services,
    IClock clock,
    ILogger<SagaWorker> log,
    IHostEnvironment env) : BackgroundService
{
    private readonly SemaphoreSlim concurrency = new(initialCount: 3, maxCount: 3);
    private readonly string workerId = $"{env.ApplicationName}@{Environment.MachineName}/{Guid.NewGuid().ToString("N")[..8]}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("SagaWorker started; worker_id={WorkerId}", workerId);
        while (!stoppingToken.IsCancellationRequested)
        {
            await concurrency.WaitAsync(stoppingToken);
            ProvisioningJob? job;
            try
            {
                job = await ClaimNextAsync(stoppingToken);
            }
            catch
            {
                concurrency.Release();
                throw;
            }

            if (job is null)
            {
                concurrency.Release();
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await using var scope = services.CreateAsyncScope();
                    var dispatcher = scope.ServiceProvider.GetRequiredService<SagaPhaseDispatcher>();
                    await dispatcher.HandleAsync(job, stoppingToken);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Saga phase dispatch threw uncaught; job_id={JobId}", job.Id);
                }
                finally
                {
                    concurrency.Release();
                }
            }, CancellationToken.None);
        }
        log.LogInformation("SagaWorker stopping");
    }

    private async Task<ProvisioningJob?> ClaimNextAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        const string sql = """
            UPDATE provisioning_jobs
            SET claimed_by       = {0},
                lease_expires_at = now() + interval '2 minutes',
                next_visible_at  = now() + interval '2 minutes',
                attempt_count    = attempt_count + 1,
                updated_at       = now()
            WHERE id = (
              SELECT id FROM provisioning_jobs
              WHERE status NOT IN ('succeeded','failed_tf','failed_dns','failed_callback','failed_cert','cancelled','rolled_back')
                AND next_visible_at <= now()
                AND (claimed_by IS NULL OR lease_expires_at <= now())
              ORDER BY next_visible_at
              LIMIT 1
              FOR UPDATE SKIP LOCKED
            )
            RETURNING *;
            """;
        return await db.ProvisioningJobs.FromSqlRaw(sql, workerId).FirstOrDefaultAsync(ct);
    }
}
```

**Notes:**
- `Task.Run` body uses `CancellationToken.None` for the wrapper task so the `_ =` discard doesn't observe stoppingToken at the task root; the dispatcher and handlers receive `stoppingToken` and respect it internally. This avoids unobserved cancelled-task exceptions in logs on shutdown.
- The claim query bumps `attempt_count`, which is what the wizard surfaces as "retry N of …". Phase-internal retries (e.g., a single failed `terraform init` that's safe to retry) should NOT bump `attempt_count`; only the SKIP LOCKED claim does.
- One claim per loop iteration is intentional. `SemaphoreSlim(3)` gates *concurrency*; the claim loop runs serially. A small `Task.Delay(500ms)` on the empty path keeps idle CPU at <1%.
- `lease_expires_at` is 2 min. Phase handlers must complete in <2 min OR call a "renew lease" helper if they expect to exceed it. `terraform plan` against a non-pathological config takes 10–60s on B2ms; `terraform apply` for a single droplet takes 60–120s. Tight but workable. **If apply consistently runs over 2 min**, raise the lease to 5 min in a follow-up.

## Per-phase handlers — common shape

Each handler implements:

```csharp
public interface ISagaPhaseHandler
{
    string Phase { get; }                                    // SagaStatus.* the handler claims
    Task HandleAsync(ProvisioningJob job, CancellationToken ct);
}
```

`SagaPhaseDispatcher` resolves by `Phase` (DI: `services.AddSingleton<ISagaPhaseHandler, TfPlanningHandler>()` × 7, then `services.AddSingleton<SagaPhaseDispatcher>()` reads them via `IEnumerable<ISagaPhaseHandler>` and indexes them at construction).

The standard transactional shape every handler follows:

```csharp
async Task UpdateAsync(
    PortalDbContext db, ProvisioningJob job,
    string newStatus, Duration delay, JsonDocument? tfOutputs, JsonElement eventRow, IClock clock)
{
    job.Status          = newStatus;
    job.NextVisibleAt   = clock.GetCurrentInstant() + delay;
    job.PhaseStartedAt  = clock.GetCurrentInstant();
    job.ClaimedBy       = null;                    // release
    job.LeaseExpiresAt  = null;
    if (tfOutputs is not null) job.TfOutputs = tfOutputs;
    EventsLogAppender.Append(job, eventRow);       // mutates job.EventsLog in place
    await db.SaveChangesAsync();
}
```

Plus per-handler-specific mutation of the `Cloud` row's per-phase `Instant`s (e.g., `TfPlanningHandler` sets `cloud.PlanStartedAt`; `TfApplyingHandler` sets `cloud.ApplyStartedAt`; etc.) — same transaction, same `SaveChangesAsync`. The denormalized Instants on `Cloud` exist for the dashboard's "show timeline" without joining `provisioning_jobs`.

### TfPlanningHandler

1. Read job's `cloud_id`, then load the `Cloud` row + the `User` row (for `user_id`).
2. Pull DEK: `await unlockCache.TryGetAsync(cloud.UserId, dek32, ct)`. If false, transition `status = failed_tf`, `events_log += { "reason": "step_up_required_but_not_unlocked" }`. No rollback (nothing to roll back; tf hasn't run).
3. Decrypt the provider token: `await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek32, ct)`.
4. Render the workspace: `await workspaceLayout.RenderAsync(job, cloud, ct)` — writes `main.tf` (calls `module "cloud" { source = "/app/terraform-modules/<provider>/" ... }`), `backend.tf` (pg backend, workspace = cloud_id), `variables.auto.tfvars` (non-sensitive: region, size, hostname).
5. `terraform init` with backend-config (Postgres conn str). On failure: `status = failed_tf`, append `{"phase":"tf_planning","error":"init"}`, return.
6. `terraform plan` with env vars `TF_VAR_provider_token=<plaintext>`, `TF_VAR_cloud_id=<uuid>`, `TF_VAR_enrollment_token=<random32>`, `TF_VAR_ghcr_pat=<from-config>`, etc. Zero the plaintext immediately after `RunAsync` returns.
7. On plan success: `status = tf_applying`, `next_visible_at = now` (immediate re-claim), append the plan's `-json` event lines.
8. On plan failure: `status = failed_tf`.

### TfApplyingHandler

1. Re-render is NOT needed; the workspace dir persists between claims of the same job. The pg backend keeps the state.
2. `terraform apply plan.tfplan` (the plan from `tf_planning` is on disk in the per-job dir). If the dir was wiped between claims (shouldn't happen, but possible on a recovery from a different node), re-plan implicitly via a single retry — log the recovery.
3. On apply success: `terraform output -json` → `tf_outputs` jsonb. Extract the droplet IP from the parsed outputs for the next phase.
4. Transition `status = dns_creating`, `next_visible_at = now`.
5. On apply failure: `status = rolling_back_tf`, `next_visible_at = now`.

### DnsCreatingHandler

1. Read droplet IP from `job.TfOutputs` (JSONPath: `cloud.value.ip` or similar — match what the stub module exposes).
2. Pull DEK + decrypt the Cloudflare token via `providerVault.DecryptAsync(userId, "cloudflare", ...)`.
3. `await cloudflare.CreateAAsync(subdomain: cloud.Hostname.Split('.')[0], ip, cfToken, ct)`.
4. On success: `status = awaiting_cloud_callback`, `next_visible_at = now + 5 min` (per ADR-0033's queue-folded polling). Append `{"phase":"dns_creating","record_id":...}`.
5. On failure: `status = rolling_back_tf`, `next_visible_at = now`. Note: we go *via* `rolling_back_tf` (destroy the droplet), not `rolling_back_dns` (no DNS record to undo since it failed). The ADR diagram is precise about this.

### AwaitingCloudCallbackHandler

This handler is invoked by the worker every 5 min (per `next_visible_at`). The actual *transition* to `awaiting_cert` happens in **PORTAL-016**'s inbound callback endpoint, which writes the status flip in its own transaction. When the worker claims the row at the 5-min mark:

1. If `status` is no longer `awaiting_cloud_callback` (callback already arrived): no-op, release.
2. If `(now - phase_started_at) >= 5 min`: timeout. Transition `status = rolling_back_dns`, `next_visible_at = now`.
3. Else (shouldn't happen given the queue cadence; defensive): re-schedule `next_visible_at = phase_started_at + 5 min`.

### AwaitingCertHandler

1. Compute next-poll cadence: `age = now - phase_started_at`. If `age < 60s`: poll cadence is 5s. Else 30s. If `age >= 30 min`: transition `status = failed_cert` (terminal, NO rollback per ADR-0033 + DEC-003).
2. Hit `GET <cloud_admin_base>/admin/health` (the cloud's `Hostname` per `Cloud.Hostname`). Bearer auth uses the *cloud-admin-token* that the cloud will receive in PORTAL-016. **For this ticket the auth header is a known stub** — PORTAL-016 wires the real one. Tests cover the stub.
3. Parse response: `{ cert_ready: bool, ... }`. If `cert_ready == true`: `status = succeeded`, `next_visible_at = now` (immediate; the worker re-claims, sees terminal, drops). Set `cloud.ProvisioningCompletedAt = now`.
4. Else: re-schedule via `next_visible_at = now + cadence`. Status stays `awaiting_cert`.

### RollingBackTfHandler

1. Re-render the workspace if missing (handler is idempotent vs. crash mid-rollback).
2. `terraform destroy -auto-approve`. Idempotent against partially-applied state.
3. On success:
   - If we entered from `rolling_back_dns` (i.e., DNS was rolled back first) → next status is `failed_dns` if DNS originally failed, or `failed_callback` if callback timed out, or `rolled_back` if user cancelled. The handler needs to read `events_log` to know which terminal to pick. **Add a `RollbackReason` JSON field to the events entry on entry into the rolling_back path** — easier than reverse-engineering from history.
   - If entered from `tf_applying` failure → `status = failed_tf`.
4. On failure: leave `status = rolling_back_tf` and let `next_visible_at = now + 1 min` re-claim. After 5 attempts (`attempt_count > 5`), give up: `status = failed_tf`, append `{"phase":"rolling_back_tf","abandoned":true}`. Manual cleanup required; document in the wizard surface.

### RollingBackDnsHandler

1. Read the DNS record id from `events_log` (the entry written by `DnsCreatingHandler` recorded it).
2. `await cloudflare.DeleteAsync(recordId, cfToken, ct)`.
3. On success: transition to `rolling_back_tf`.
4. On failure: log and proceed to `rolling_back_tf` anyway (the DNS record is a stub-A pointing to an about-to-be-destroyed droplet; not a correctness disaster if it lingers, but worth alerting).

## WorkspaceLayout

```csharp
public sealed class WorkspaceLayout(IClock clock, IConfiguration config)
{
    private static readonly string BaseDir = "/var/lib/portal/terraform/jobs";

    public async Task<string> RenderAsync(ProvisioningJob job, Cloud cloud, CancellationToken ct)
    {
        var dir = Path.Combine(BaseDir, job.Id.ToString());
        Directory.CreateDirectory(dir);

        var module = cloud.Provider switch
        {
            "digitalocean" => "/app/terraform-modules/digitalocean",
            "azure"        => "/app/terraform-modules/azure",
            "stub"         => "/app/terraform-modules/stub",
            _ => throw new InvalidOperationException($"Unknown provider: {cloud.Provider}"),
        };

        await File.WriteAllTextAsync(Path.Combine(dir, "main.tf"), $$"""
            terraform {
              required_version = ">= 1.6"
            }
            module "cloud" {
              source         = "{{module}}"
              cloud_id       = var.cloud_id
              region         = var.region
              size           = var.size
              hostname       = var.hostname
            }
            variable "cloud_id"          { type = string }
            variable "region"            { type = string }
            variable "size"              { type = string }
            variable "hostname"          { type = string }
            variable "provider_token"    { type = string, sensitive = true }
            variable "enrollment_token"  { type = string, sensitive = true }
            variable "ghcr_pat"          { type = string, sensitive = true }
            output "ip"                  { value = module.cloud.ip }
            """, ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "backend.tf"),
            "terraform { backend \"pg\" {} }\n", ct);

        await File.WriteAllTextAsync(Path.Combine(dir, "variables.auto.tfvars"), $$"""
            cloud_id = "{{cloud.Id}}"
            region   = "{{cloud.Region}}"
            size     = "{{config["Provisioning:DefaultSize"]}}"
            hostname = "{{cloud.Hostname}}"
            """, ct);

        return dir;
    }

    public void SweepTerminal(Instant now, Duration retention)
    {
        // Called from CrashRecoveryService on boot.
        if (!Directory.Exists(BaseDir)) return;
        var cutoff = (now - retention).ToDateTimeOffset();
        foreach (var d in Directory.EnumerateDirectories(BaseDir))
        {
            var info = new DirectoryInfo(d);
            if (info.LastWriteTimeUtc < cutoff.UtcDateTime)
                try { Directory.Delete(d, recursive: true); } catch { /* swallow; next sweep retries */ }
        }
    }
}
```

The `variables.auto.tfvars` is **non-sensitive only**. Provider tokens and enrollment tokens are passed via `TF_VAR_*` env vars at `Process.Start` time, never written to disk.

## CrashRecoveryService

```csharp
public sealed class CrashRecoveryService(
    PortalDbContext db,
    ITerraformRunner tf,
    WorkspaceLayout layout,
    IClock clock,
    ILogger<CrashRecoveryService> log) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        // 1. Force-unlock stale terraform workspaces.
        var now = clock.GetCurrentInstant();
        var stale = await db.ProvisioningJobs
            .Where(j => (j.Status == SagaStatus.TfApplying || j.Status == SagaStatus.RollingBackTf)
                     && j.LeaseExpiresAt != null && j.LeaseExpiresAt < now)
            .ToListAsync(ct);

        foreach (var job in stale)
        {
            var workdir = $"/var/lib/portal/terraform/jobs/{job.Id}";
            if (Directory.Exists(workdir))
            {
                try
                {
                    // Best-effort: terraform itself records the lock id in the pg backend; force-unlock with no id is a no-op.
                    // Easier: nuke the local .terraform/terraform.tfstate.lock.info and let the pg backend resolve on re-claim.
                    // Real pg-backend unlock: read the lock id from postgres and call ForceUnlock.
                    log.LogWarning("Stale lease detected on job {JobId} ({Status}); will rely on terraform's pg-backend lease expiry on re-claim", job.Id, job.Status);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Force-unlock failed on job {JobId}; will retry on next worker boot", job.Id);
                }
            }
            job.ClaimedBy = null;
            job.LeaseExpiresAt = null;
            job.NextVisibleAt = now;            // make claimable immediately
        }
        await db.SaveChangesAsync(ct);

        // 2. Sweep terminal job dirs older than 7 days.
        layout.SweepTerminal(now, Duration.FromDays(7));
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
```

Wire via `builder.Services.AddHostedService<CrashRecoveryService>()` registered **before** the `SagaWorker` hosted service so it runs to completion before the claim loop starts. (.NET hosted services run their `StartAsync` in registration order.)

**On the force-unlock TODO:** the proper terraform `force-unlock` requires the lock id, which lives in the `pg` backend's lock table. Two options:
1. Query the `terraform_locks` table directly and call `tf.ForceUnlockAsync(workdir, lockId, ct)`.
2. Rely on the pg backend's built-in lease behavior (the `terraform_locks` row has a TTL; expired locks are reclaimable).

Option 2 is sufficient for thesis scope (the locks expire within minutes); document the limitation in the ADR-0033 follow-up that we accepted "best-effort" recovery. If you choose option 1, add a small helper that queries `terraform_locks WHERE id = $cloud_id` (workspace) and feeds the lock id into `force-unlock`.

## Plugin-cache pre-warm

`infra/docker/saga-worker/entrypoint.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

CACHE_DIR=/var/lib/portal/terraform/plugin-cache
mkdir -p "$CACHE_DIR"

if [ ! -f "$CACHE_DIR/.warmed" ]; then
    echo "[entrypoint] Pre-warming terraform plugin cache..."
    for provider in digitalocean azurerm cloudflare; do
        workdir=$(mktemp -d)
        cat > "$workdir/main.tf" <<EOF
terraform {
  required_providers {
    $provider = { source = "hashicorp/$provider" }
  }
}
EOF
        TF_PLUGIN_CACHE_DIR="$CACHE_DIR" terraform -chdir="$workdir" init -input=false -no-color || true
        rm -rf "$workdir"
    done
    touch "$CACHE_DIR/.warmed"
    echo "[entrypoint] Plugin cache warmed"
else
    echo "[entrypoint] Plugin cache already warmed; skipping"
fi

exec dotnet ThanyMarcus.Portal.SagaWorker.dll
```

Update `infra/docker/saga-worker/Dockerfile`:

```dockerfile
# ... existing PORTAL-007a content ...
COPY infra/docker/saga-worker/entrypoint.sh /entrypoint.sh
COPY infra/docker/saga-worker/terraform-modules /app/terraform-modules
RUN chmod +x /entrypoint.sh
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["/entrypoint.sh"]
```

The `.warmed` marker on the volume means subsequent restarts skip the ~30s warm-up.

## Stub terraform module

`infra/docker/saga-worker/terraform-modules/stub/main.tf`:

```hcl
variable "cloud_id"         { type = string }
variable "region"           { type = string }
variable "size"             { type = string }
variable "hostname"         { type = string }

resource "null_resource" "ping" {
  triggers = { cloud_id = var.cloud_id, hostname = var.hostname }
  provisioner "local-exec" { command = "echo provisioned cloud_id=${var.cloud_id} hostname=${var.hostname}" }
}

output "ip" { value = "203.0.113.1" }      # TEST-NET-3 per RFC 5737
```

`outputs.tf`:

```hcl
# placeholder so the WorkspaceLayout's `output "ip"` references resolve
```

Provider in tests: `cloud.Provider = "stub"`. The handlers treat it identically to `digitalocean`/`azure` once PORTAL-008/009 land; the module switch in `WorkspaceLayout` already covers all three.

## EnqueueGuard

`src/ThanyMarcus.Portal.Api/Features/CloudManagement/EnqueueGuard.cs`:

```csharp
public sealed class EnqueueGuard(PortalDbContext db)
{
    public async Task<InFlightConflict?> CheckAsync(Guid cloudId, CancellationToken ct)
    {
        var inflight = await db.ProvisioningJobs
            .Where(j => j.CloudId == cloudId && !SagaStatus.Terminal.Contains(j.Status))
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => new InFlightConflict(j.Id, j.Status))
            .FirstOrDefaultAsync(ct);
        return inflight;
    }
}

public sealed record InFlightConflict(Guid JobId, string CurrentPhase);
```

Used by future PORTAL-011/PORTAL-015 endpoints as:

```csharp
var conflict = await guard.CheckAsync(cloudId, ct);
if (conflict is not null)
    return Results.Conflict(new { reason = "cloud_busy", in_flight_job_id = conflict.JobId, current_phase = conflict.CurrentPhase });
```

Register: `builder.Services.AddScoped<EnqueueGuard>()`.

## Program.cs changes (SagaWorker)

Building on the PORTAL-007a Program.cs:

```csharp
// (existing PORTAL-007a registrations: OTel, IClock, DbContext, DataProtection, IInfraOpUnlockCache)

builder.Services.AddSingleton<ITerraformRunner, TerraformRunner>();
builder.Services.AddSingleton<ICloudflareDnsClient, StubCloudflareDnsClient>();
builder.Services.AddSingleton<WorkspaceLayout>();
builder.Services.AddSingleton<EventsLogAppender>();

builder.Services.AddScoped<ISagaPhaseHandler, TfPlanningHandler>();
builder.Services.AddScoped<ISagaPhaseHandler, TfApplyingHandler>();
builder.Services.AddScoped<ISagaPhaseHandler, DnsCreatingHandler>();
builder.Services.AddScoped<ISagaPhaseHandler, AwaitingCloudCallbackHandler>();
builder.Services.AddScoped<ISagaPhaseHandler, AwaitingCertHandler>();
builder.Services.AddScoped<ISagaPhaseHandler, RollingBackTfHandler>();
builder.Services.AddScoped<ISagaPhaseHandler, RollingBackDnsHandler>();
builder.Services.AddScoped<SagaPhaseDispatcher>();

// CrashRecoveryService BEFORE SagaWorker (start order matters).
builder.Services.AddHostedService<CrashRecoveryService>();
builder.Services.AddHostedService<SagaWorker>();

// Provider-token vault is in Portal.Api; the worker reuses it via the project reference.
builder.Services.AddScoped<IProviderTokenVault, ProviderTokenVault>();

// HttpClient for the cert poll. Configure timeout + retry-on-DNS-failure.
builder.Services.AddHttpClient<AwaitingCertHandler>(c => c.Timeout = TimeSpan.FromSeconds(5));
```

## Acceptance criteria

- `dotnet build` succeeds with **zero warnings**.
- `dotnet ef database update` applies the migration cleanly against a fresh Postgres; rolls back cleanly via `dotnet ef migrations remove`.
- The `ix_provisioning_jobs_claimable_next_visible` partial index exists and is `EXPLAIN`-ed when the claim query runs (verify in test via `EXPLAIN UPDATE …`).
- Inserting a `pending` row with `cloud.Provider = "stub"` and a valid step-up unlock drives the saga to `awaiting_cloud_callback` within ~10 seconds (the stub module's `null_resource` returns instantly; `terraform init` is the slow part on first run, ~3–5 s with warmed cache).
- Flipping the row to `awaiting_cert` via direct SQL (simulating PORTAL-016's callback) drives the saga to `succeeded` once the cert-poll fake returns `cert_ready: true`.
- Forcing `terraform apply` to fail (via `FakeTerraformRunner`) drives the saga to `rolling_back_tf → failed_tf`.
- Forcing the cloudflare stub to throw drives the saga to `rolling_back_tf → failed_dns`.
- 30-min cert timeout: pre-set `phase_started_at = now - 31min`, advance the worker once, assert `status = failed_cert` (no rollback).
- Five `pending` rows enqueued at once: assert exactly 3 are in `tf_planning`/`tf_applying` simultaneously via a SQL probe during the test; the other 2 wait.
- Crash recovery: pre-seed a `tf_applying` row with `lease_expires_at = now - 5min`, boot the worker, assert the row's `claimed_by` is reset to null, `next_visible_at <= now`, and the worker re-claims it.
- `EnqueueGuard.CheckAsync(cloudId)` returns the conflict when a non-terminal job exists; returns null otherwise.
- `docker compose up -d` from scratch → saga-worker logs the plugin-cache warm-up on first boot (~30s), `.warmed` marker created; subsequent restarts skip warm-up.
- `docker compose exec saga-worker terraform -chdir=/app/terraform-modules/stub init` succeeds (validates the module + cache are wired correctly).
- All test files under `tests/SagaWorker/` pass; coverage on the seven handlers ≥ 80% lines.
- The wizard surface (read-only for this ticket) can query `provisioning_jobs.events_log` and see the terraform `-json` event lines under `events`.

## Concrete steps in order

1. **Schema** — update `ProvisioningJob.cs` + `ProvisioningJobConfiguration.cs` (rename + add columns). `dotnet ef migrations add ProvisioningSagaColumns`. Inspect the generated migration; tweak to match the explicit shape above. Run `dotnet ef database update`. Verify column names + partial index in psql.

2. **SagaStatus constants** — land in `Portal.Api/Features/Provisioning/SagaStatus.cs`. Reference from the worker via the existing `ProjectReference`.

3. **`ITerraformRunner` + `TerraformRunner`** — land the real impl. Smoke-test locally: `dotnet run --project src/ThanyMarcus.Portal.SagaWorker` after copying a `stub/main.tf` to `/var/lib/portal/terraform/jobs/test/`, then invoke the runner via a one-off test hook. (Or skip the manual smoke — the integration tests cover it.)

4. **`ICloudflareDnsClient` + `StubCloudflareDnsClient`** — land + register.

5. **`WorkspaceLayout`** — land + unit test for the rendered file shape.

6. **`SagaStatus.IsTerminal` + `EventsLogAppender`** — land + unit test.

7. **`SagaPhaseDispatcher` + `ISagaPhaseHandler`** — land the contract + the switch.

8. **Handlers** — land one at a time, each with unit tests using `FakeTerraformRunner` + `FakeCloudflareDnsClient`. Order:
   - `TfPlanningHandler`
   - `TfApplyingHandler`
   - `DnsCreatingHandler`
   - `AwaitingCloudCallbackHandler`
   - `AwaitingCertHandler`
   - `RollingBackTfHandler`
   - `RollingBackDnsHandler`

9. **`CrashRecoveryService`** — land + integration test.

10. **`SagaWorker` claim loop** — replace the PORTAL-007a placeholder. `dotnet run` locally with one `pending` row, watch it walk to `awaiting_cloud_callback`.

11. **`EnqueueGuard`** in Portal.Api — land + scoped registration.

12. **Stub module + entrypoint.sh + Dockerfile updates** — `docker compose build saga-worker`. `docker compose up -d`. Verify entrypoint logs the warm-up.

13. **End-to-end test** (`SagaEndToEndTests`) — fixtures: Testcontainers Postgres, in-process worker host, seeded user with passphrase + step-up unlock + `digitalocean` provider token, seeded `cloud.Provider = "stub"`. Assert the row walks `pending → tf_planning → tf_applying → dns_creating → awaiting_cloud_callback`. Flip to `awaiting_cert` via direct SQL, fake the cert poll to return `cert_ready: true`, assert `succeeded`.

14. **Concurrency test** + **crash-recovery test**.

15. **Verify all acceptance criteria**. Run the full `docker compose down -v && docker compose up -d` cycle. Eyeball the events_log shape in psql.

## Out of scope (do not touch)

- **Real DO / Azure terraform modules** — PORTAL-008 / PORTAL-009. The stub module is the only one the saga exercises in this ticket.
- **Real Cloudflare HTTP client** — PORTAL-010b. `StubCloudflareDnsClient` only.
- **Cloud-init script** — PORTAL-010.
- **Wizard UI / cloud-create endpoint / cloud-destroy endpoint** — PORTAL-011 / PORTAL-015. `EnqueueGuard` is the only API-side artifact.
- **Inbound callback endpoint (`POST /api/clouds/{id}/callback`)** — PORTAL-016. Tests flip `awaiting_cloud_callback → awaiting_cert` via direct SQL.
- **SSE / wizard status endpoint** — PORTAL-011 reads from `events_log`; the saga only writes.
- **OTel collector / Prometheus push** — beyond the console exporter from PORTAL-007a.
- **Per-user fair queuing** (the ADR mentions this as a thesis-scope deferral). 3-job concurrency is global.
- **Migration rollback story for cloud-deletes against in-flight jobs** — the FK is RESTRICT; users will get a 409 from any future "destroy cloud" endpoint while a job is in flight. PORTAL-015 handles the UX.

## Risks & gotchas

- **DEK expiry mid-saga.** `IInfraOpUnlockCache.SlidingTtl = 10 min`. If the user submits the wizard form, the worker claims the row immediately, decrypts the provider token, runs `terraform plan + apply` (~3–5 min total on stub; longer on real DO/Azure), then never needs the DEK again. **The DEK is only needed up to and including `tf_planning`** (which decrypts the provider token to pass via `TF_VAR_provider_token`). Subsequent claims for the same job — `tf_applying`, `dns_creating`, etc. — re-decrypt the provider token because the plaintext was zeroed after `tf_planning`. **This means every phase that calls a provider API (`tf_applying`, `dns_creating`, `rolling_back_tf`) needs the DEK.** If the unlock has expired by the time `dns_creating` runs (~3 min after step-up at worst), the saga transitions to `failed_dns` with reason `step_up_expired`. **Mitigation:** the user re-unlocks via the wizard's existing step-up flow, then the row is enqueued for retry (PORTAL-015's destroy-then-create cycle, or a future "resume" button). For PORTAL-007 scope, document the limitation and surface it in `events_log`; the user-facing UX lives in PORTAL-011/-015.

- **DEK passed via `TF_VAR_*` env var is visible to terraform's plugin processes** (subprocess of the terraform binary). Those processes are children of the saga-worker container; they can't be seen from another container or another user's process. The plugins are official HashiCorp providers + we control the saga-worker container image — acceptable trust boundary.

- **`terraform_locks` table grows.** The pg backend writes a row per `terraform_lock` operation. Locks are released cleanly on success but stale locks from crashes accumulate. Lazy cleanup: `CrashRecoveryService.StartAsync` should also `DELETE FROM terraform_locks WHERE expires_at < now()` (a single SQL on boot). Negligible if the saga is healthy; matters at the year-of-operation timescale.

- **`Process.Start` timeouts.** `RunAsync` doesn't impose a per-call timeout — it relies on the cancellation token. The 2-min lease + `WaitForExitAsync(ct)` means a stuck terraform process gets SIGKILLed when the worker shuts down OR when the next claim notices `lease_expires_at < now`. Acceptable. If a real DO outage makes plan/apply hang for 10 min, the saga's `attempt_count` climbs but the user sees progress in `events_log`.

- **`terraform -json` stream interleaving.** Both stdout and stderr can contain `-json` lines. Stderr typically carries warnings and unstructured logs; stdout carries the event stream. The handler reads them concurrently with `Task.WhenAll(stdoutTask, stderrTask)` — order-of-emission relative to each other is non-deterministic, but order *within* stdout is preserved. **Don't merge stdout + stderr in `events_log`.** Tag each with `"source":"tf_stdout"` or `"source":"tf_stderr"`.

- **`null_resource` + `local-exec` in the stub module requires nothing more than the terraform binary** — no provider plugin, no credentials. Good for tests; means the stub module's `terraform init` is fast (<1s) and doesn't hit the network. **Make sure the WorkspaceLayout's `main.tf` doesn't `required_providers` anything when provider == "stub"**, or `terraform init` will try to download null/cloudflare/etc and slow tests down. Branch in the rendering logic.

- **Per-job dir on Docker volume costs disk** at ~5–50 MB per job. 7-day retention × 100 jobs/week = ~35 GB max. On the B2ms's 128 GB SSD: fine. On a smaller VM (B2s with 32 GB): not fine. Document in `portal_deployment` memory if you change the default VM size.

- **`SemaphoreSlim(3)` is configurable** via `Environment["PORTAL_MAX_CONCURRENT_JOBS"]`. Don't hardcode the 3 in `SagaWorker.cs` — read from `IConfiguration` with a default of 3. Add to `Program.cs`: `var maxConcurrent = builder.Configuration.GetValue("Provisioning:MaxConcurrentJobs", 3);`.

- **OpenTelemetry traces don't propagate across the claim boundary.** Each phase handler runs in its own `Activity`; there's no trace-id linking phase N to phase N+1 of the same job. Workable: tag every span with `attr.job_id`. The wizard's "show me the full trace for job X" view aggregates by attribute, not by trace-id. Out of scope to fix here; document in observability follow-up.

- **`-no-color` flag matters.** Without it, terraform's `-json` output sometimes injects ANSI codes that break JSON parsing in the events appender. Always pass `-no-color` alongside `-json`.

- **The `Cloud` entity's per-phase Instants are denormalized.** They're updated in the same `SaveChangesAsync` as the `provisioning_jobs` row, in the *same* DbContext, single transaction (`DbContext` opens a transaction per `SaveChangesAsync` by default). If a phase handler crashes between writing `provisioning_jobs.status` and `cloud.plan_started_at`, the transaction rolls back and the row stays claimable for the next worker. No torn-state risk.

- **`ConfigureAwait`.** This is a worker, not ASP.NET — no synchronization context. `ConfigureAwait(false)` is unnecessary noise; don't add it.

- **`JsonDocument` lifetime.** `events_log` is `JsonDocument` — implements `IDisposable`. EF Core takes ownership when assigned to the entity. When you mutate via `EventsLogAppender.Append`, the appender should construct a fresh `JsonDocument` from the merged array and dispose the old one. Easier: hold the events as `List<JsonElement>` in memory while the handler is processing, then `JsonDocument.Parse(JsonSerializer.Serialize(list))` at the end. ~one allocation per phase transition.

- **`force-unlock` requires the lock id.** Per the implementation note in `CrashRecoveryService`, real `force-unlock` queries the pg backend's `terraform_locks` table for the lock id keyed by workspace. For thesis scope, accept the pg-backend's TTL-based lock expiry (locks are released when the lease expires within ~5 min of crash). Document.

- **`docker compose down -v` deletes the plugin cache** along with `terraform_data`. Next `up` triggers another 30 s warm-up. Fine in dev; in prod (PORTAL-017), the volume is host-bind-mounted and survives compose-down.

- **Test isolation: per-test plugin cache.** Don't share the prod plugin-cache dir with tests; tests run against Testcontainers and don't have `/var/lib/portal` mounted. The `WorkspaceLayout` should accept a base-dir from config so tests can point it at `Path.GetTempPath()`. Add `Provisioning:WorkspaceBase` with default `/var/lib/portal/terraform`.

- **Don't share the worker DbContext across `await` boundaries inside a phase handler.** EF Core's `DbContext` is not thread-safe; reentrant `await` while a `SaveChangesAsync` is in flight will throw. The current design opens a scope per claim — fine. **Do not** introduce a long-lived DbContext on the handler.

- **`EnqueueGuard` is a *check*, not a *lock*.** Concurrent enqueues for the same `cloud_id` could both pass the check before either commits. The unique-row invariant is enforced by the API endpoint's transaction — *not* by the guard. To make the guard authoritative, the caller wraps the check + insert in `SERIALIZABLE` isolation or uses `INSERT ... ON CONFLICT (cloud_id) WHERE status NOT IN (terminal) DO NOTHING` (Postgres doesn't support partial-index-based ON CONFLICT for non-unique indexes; falls back to advisory locks or SERIALIZABLE). PORTAL-011's endpoint must do this correctly when it lands; the helper here is a *building block*, not the full guarantee. **Document this in the helper's xmldoc.**

## Definition of done

All acceptance criteria pass. `git status` shows the migration, the new files under `Portal.SagaWorker/Features/Provisioning/` and `Infrastructure/`, the updated SagaWorker.cs + Program.cs, the Dockerfile + entrypoint.sh + stub module, the `EnqueueGuard` in Portal.Api, and the test files. `docker compose up -d` from scratch produces a fully-up stack; inserting a `pending` row drives the saga to `awaiting_cloud_callback` without manual intervention. The events_log has structured entries the wizard can render.

A fresh agent picking up **PORTAL-008** (DO terraform module) from this state knows:
- The saga consumes any module that matches `WorkspaceLayout`'s contract: takes `cloud_id`, `region`, `size`, `hostname` as variables; exposes an `ip` output.
- Sensitive inputs (provider token, enrollment token, ghcr pat) arrive via `TF_VAR_*` env vars.
- The pg backend is configured at `init` time; the module declares no backend.
- The module's `terraform init` must succeed against the pre-warmed plugin cache (uses `hashicorp/digitalocean` from the warmed providers).

A fresh agent picking up **PORTAL-010b** (Cloudflare DNS client) from this state knows:
- The interface is `ICloudflareDnsClient`; swap the registration in `Program.cs` from `StubCloudflareDnsClient` to the real `HttpClient`-based impl.
- The interface returns a `DnsRecord` with an `Id` the saga stores in `events_log` for use during rollback.

A fresh agent picking up **PORTAL-011** (wizard UI + cloud-create endpoint) from this state knows:
- The endpoint inserts one `clouds` row + one `provisioning_jobs` row with `status = pending`, `next_visible_at = now`, all in one transaction.
- Before insert, the endpoint calls `EnqueueGuard.CheckAsync(cloudId)` and returns 409 on conflict.
- The wizard reads progress from `provisioning_jobs.events_log` via a new `GET /api/clouds/{id}/status` endpoint (per ADR-0035).

A fresh agent picking up **PORTAL-016** (inbound callback endpoint) from this state knows:
- The endpoint updates `provisioning_jobs` for `cloud_id`: `SET status = 'awaiting_cert', next_visible_at = now()` in a single transaction.
- The worker's next claim observes the new status and dispatches `AwaitingCertHandler` instead of `AwaitingCloudCallbackHandler` — natural transition with no orchestration code on the API side.

## Cross-references

- **PORTAL-007a** — prerequisite (lands the SagaWorker project, docker-compose stack, shared DP keys, terraform binary in image). Must be merged before this ticket starts.
- **PORTAL-005** — prerequisite (provider-token vault). The saga reads from it on every phase that calls a provider API.
- **PORTAL-003f** — prerequisite (Postgres-backed `IInfraOpUnlockCache`). The saga reads from it for DEK retrieval.
- **PORTAL-008 / PORTAL-009** — direct successors (provider modules). They land under `infra/docker/saga-worker/terraform-modules/{digitalocean,azure}/` alongside the stub module; `WorkspaceLayout` already routes by `cloud.Provider`.
- **PORTAL-010** — cloud-init template. Consumed by the provider modules, not directly by the saga.
- **PORTAL-010b** — real Cloudflare DNS client. Swaps the `StubCloudflareDnsClient` registration.
- **PORTAL-011** — wizard UI + cloud-create endpoint. Calls `EnqueueGuard.CheckAsync` and writes the `provisioning_jobs` row.
- **PORTAL-015** — destroy endpoint. Re-uses the saga: enqueues a `provisioning_jobs` row with `kind = "destroy"` (the existing `Kind` column is currently unused; PORTAL-015 starts using it to differentiate create-vs-destroy in the dispatcher).
- **PORTAL-016** — inbound callback endpoint. Flips `awaiting_cloud_callback → awaiting_cert`.
- **ADR-0033** — the canonical spec for everything in this handoff.
- **ADR-0019** — queue substrate (this ticket implements it).
- **ADR-0034** — cloud handshake (defines the contract `AwaitingCloudCallbackHandler` and `AwaitingCertHandler` consume).
- **ADR-0035** — wizard transport (defines `events_log` shape this ticket writes).
