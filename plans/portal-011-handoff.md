# PORTAL-011 + 010b + 016 + ADR-0036 bundle — Handoff Brief

**Goal:** land the user-visible cloud-creation path end-to-end. After this bundle, a signed-in TOTP-verified user with a passphrase set and a DigitalOcean API token stored can navigate to `/clouds/new` in the SPA, walk a two-step wizard (Target → Review), click Provision, and watch a real DigitalOcean droplet appear in their DO console while the wizard live-streams saga phase transitions over SSE to a `/clouds/{id}` status page. The cloud reaches `succeeded` (Caddy acquires a real LE cert against `<random>.thany.click`, cloud POSTs back to portal, saga flips to `awaiting_cert`, cert poll sees `cert_ready: true`). The user can then destroy via PORTAL-015.

Bundled because the user-perceptible demo requires all four pieces in lockstep:
- **PORTAL-011** — wizard UI + `POST /api/clouds` + `/clouds/{id}` SSE + frontend foundation
- **PORTAL-010b** — real Cloudflare DNS client (replaces `StubCloudflareDnsClient`)
- **PORTAL-016** — `POST /api/clouds/{id}/callback` (the cloud's inbound handshake)
- **ADR-0036** — wizard progress transport (the SSE event vocabulary + REST snapshot contract); also unblocks the dead `[[0035-wizard-progress-transport]]` reference in ADR-0033 (the number was taken by ADR-0035 burst-worker on 2026-05-16)

Estimated **2.0 person-days** with heavy AI-agent assistance. Original line items in `tickets-2026-05-13.md`: PORTAL-011 1d, PORTAL-010b 0.5d, PORTAL-016 0.25d. The bundle adds the frontend foundation (~0.25d) that PORTAL-011 needs and the ADR write-up (~0.05d). Total holds at ~2d.

## Where decisions live (read before doing anything)

- **`plans/portal-011-handoff.md`** — this document. The decisions resolved in the 2026-05-16 grill are recorded inline below ("Locked decisions" subsection at the top of each surface).
- **`docs/decisions/0020-server-push-sse.md`** — canonical: SSE chosen for user-facing transport over polling / WebSocket / SignalR. The new ADR-0036 sits under this one.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the saga state machine. PORTAL-011's create endpoint enqueues a `provisioning_jobs(kind="create", status="pending")` row and lets the existing handler chain (PORTAL-007) drive it. **Do not invent new saga states.**
- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — pinned callback payload + cadences for PORTAL-016. Read §1 (payload) + §3 (no auth on `/admin/health`) + §4 (retry budget) before touching the callback endpoint.
- **`docs/decisions/0035-burst-worker-llm-tier.md`** — the revised DEC-001 (two-tier; control plane is 2 vCPU / 4 GB). Wizard's cost display references this directly.
- **`plans/tickets-2026-05-13.md`** — `DEC-003` (platform-owned `thany.click`, per-cloud random subdomain via Cloudflare DNS API), `DEC-005` (GHCR for image distribution; passed through as `ghcr_pat` from portal config).
- **`plans/portal-005-handoff.md`** — `IProviderTokenVault` endpoints already exist: `POST /api/clouds/provider-tokens`, `GET /api/clouds/provider-tokens`, `DELETE /api/clouds/provider-tokens/{provider}`. PORTAL-011 surfaces the UI for them on `settings/security`. **Do not modify the endpoints; only add the SvelteKit consumer.**
- **`plans/portal-007-handoff.md`** — the saga skeleton + handler chain. PORTAL-011 touches `TfPlanningHandler` minimally (read `EnrollmentToken` from the new column instead of generating it on the fly, if PORTAL-007 currently does so) and otherwise leaves the saga unchanged.
- **`plans/portal-007a-handoff.md`** — the SagaWorker docker-compose stack. No changes here.
- **`plans/portal-008-handoff.md`** — the DO terraform module. PORTAL-011 honors its variable surface (cloud_id, region, size, hostname + sensitive enrollment_token / ghcr_pat / portal_url via TF_VAR). The size source is `Provisioning:DefaultSize` (PORTAL-008's appsetting); PORTAL-011 does **not** override it. **Sub-edit:** update PORTAL-008's `Provisioning:DefaultSize` from `s-4vcpu-16gb` to `s-2vcpu-4gb` per DEC-001 revised — same `appsettings.json`, three characters to change.
- **`plans/portal-010-handoff.md`** — cloud-init template + frozen variable contract (`cloud_id, hostname, portal_url, enrollment_token, ghcr_pat`). PORTAL-016 consumes the callback POST that PORTAL-010's `register-with-portal.sh` emits.
- **`plans/portal-015-handoff.md`** — destroy endpoint. **Sub-edit:** lines 285, 790, 800 carry stale "polls `/api/clouds/{id}/status`" language from before SSE was canonical. Replace with references to `/api/clouds/{id}/events` (SSE) + `GET /api/clouds/{id}` (REST snapshot). Three-line cleanup; do it as part of this bundle.
- **`plans/portal-003-handoff.md` + `portal-003f-handoff.md`** — Google SSO, TOTP policies, `IInfraOpUnlockCache` sliding 10-min TTL. PORTAL-011 inherits these contracts unchanged.
- **`plans/cloud-pivot-plan-2026-05-13.md`** — §22.5 has the old "pre-create A-record placeholder" phrasing for DNS. **Superseded by ADR-0033 + this bundle's ADR-0036:** DNS is created *after* terraform applies (single phase). One-line fix-up edit in §22.5 to reference the saga's actual `dns_creating` phase shape.
- **Memory files**: `portal_architecture.md` (Postgres queue + LISTEN/NOTIFY + SSE only), `portal_deployment.md` (single VM Compose), `portal_web_stack.md` (SvelteKit static SPA served from Portal.Api wwwroot), `portal_tooling.md` (.NET 10, warnings-as-errors, OTel, Shouldly + xUnit v3, hand-written TS types).

**Do not invent SSE event names, request/response shapes, or status codes beyond those pinned below.** The grill that produced this handoff resolved 14 decisions; if a defensible alternative is missing, file a follow-up rather than diverging.

## Scope boundary (precise)

**In scope:**

### Backend — Portal.Api

- `Features/CloudManagement/Create/` (new directory):
  - `CreateCloudRequest.cs` — `{ Provider: string, Region: string }`
  - `CreateCloudResponse.cs` — `{ CloudId: Guid, JobId: Guid, Hostname: string }`
  - `CreateCloudEndpoints.cs` — `POST /api/clouds`. Group requires `AuthPolicies.TotpRequired`; endpoint adds `RequireInfraOpUnlockFilter`. In one SERIALIZABLE tx: validate provider+region, check user-in-flight-create guard, generate hostname (retry on collision), insert `clouds` row, insert `provisioning_jobs(kind=create, status=pending, enrollment_token=<random32hex>)` row, commit. Return 202.
  - `EnrollmentTokenGenerator.cs` — 32-byte hex via `RandomNumberGenerator.GetHexString(64)` (note: 32 bytes = 64 hex chars; the variable name "32 byte" in ADR-0034 is the byte count not the char count).
- `Features/CloudManagement/Status/` (new):
  - `GetCloudStatusEndpoints.cs` — `GET /api/clouds/{id}` returns the REST snapshot: cloud row + current provisioning job + curated `events_log` tail (last 10 entries, server-filtered to UI-safe content). Requires cookie auth + ownership check.
  - `CloudStatusResponse.cs` — `{ cloudId, hostname, provider, region, provisioningStatus, succeededAt?, destroyedAt?, currentJob?: JobSummary, recentEvents: EventSummary[] }`
- `Features/CloudManagement/Events/` (new):
  - `CloudEventsEndpoints.cs` — `GET /api/clouds/{id}/events` (SSE). Requires cookie auth + ownership check. Holds a long-lived response with `Content-Type: text/event-stream`; subscribes to Postgres `LISTEN provisioning_job_changed` on a dedicated `NpgsqlConnection`; on each notification, re-queries the job row, computes deltas vs. last-emitted, emits one or more curated events. Sends `: heartbeat\n\n` every 30s. Closes the response with `: closed\n\n` on terminal state.
  - `SagaEventTranslator.cs` — pure logic: given (previousJobState, currentJobState), emits 0..N curated `WizardSseEvent` instances. Encapsulates the events_log → SSE-vocabulary translation.
  - `WizardSseEvent.cs` — discriminated record for the 6 vocabulary events (`phase_started`, `phase_completed`, `phase_failed`, `cloud_ready`, `cloud_failed`, `cloud_rolled_back`).
- `Features/CloudManagement/ProviderMeta/` (new):
  - `ProviderMetaEndpoints.cs` — `GET /api/clouds/provider-meta/digitalocean` returns `{ regions: RegionInfo[] }`. Read directly from the new `DigitalOceanRegions.Catalog` (see below). No auth required (public info); rate-limited under the existing default policy.
- `Features/CloudManagement/Callback/` (new — PORTAL-016):
  - `CloudCallbackEndpoints.cs` — `POST /api/clouds/{cloud_id}/callback`. Body `{ cloudId, enrollmentToken, cloudAdminToken }`. No auth scheme; `enrollmentToken` is the secret. Implements the Q14e status table verbatim (200 / 401 / 404 / 409 / 429 / 500). On match-and-state-valid: store `cloudAdminToken` plaintext on `clouds.CloudAdminToken`, flip `provisioning_jobs.status` from `awaiting_cloud_callback` to `awaiting_cert`, append `{phase:"cloud_registered", at:<now>}` to events_log in one tx. Idempotent (Q14c (ii)).
  - Rate limiter policies: `cloud_callback_per_id` (per route param, 12/5 min), `cloud_callback_per_ip` (per remote IP, 60/5 min) — both defined in `Features/Auth/RateLimiting/` alongside PORTAL-003c's policies.
- `Features/Provisioning/EnqueueGuard.cs` — **extend** the existing helper from PORTAL-007. Today it checks per-cloud in-flight jobs (used by PORTAL-015). PORTAL-011 needs a per-user in-flight-create variant: `CheckUserCreateInFlightAsync(userId)`. Same SERIALIZABLE-tx contract.
- `Features/Provisioning/DigitalOceanRegions.cs` — **extend** the existing class from PORTAL-008. Today it has `Allowed` (slug HashSet) only. Add:
  ```csharp
  public sealed record RegionInfo(string Slug, string Label, string Continent);
  public static readonly IReadOnlyList<RegionInfo> Catalog = [
      new("nyc1", "New York 1",    "North America"),
      new("nyc3", "New York 3",    "North America"),
      new("sfo3", "San Francisco", "North America"),
      new("tor1", "Toronto",       "North America"),
      new("ams3", "Amsterdam",     "Europe"),
      new("fra1", "Frankfurt",     "Europe"),
      new("lon1", "London",        "Europe"),
      new("blr1", "Bangalore",     "Asia-Pacific"),
      new("sgp1", "Singapore",     "Asia-Pacific"),
      new("syd1", "Sydney",        "Asia-Pacific"),
  ];
  ```
  Refactor `Allowed` to derive from `Catalog.Select(r => r.Slug).ToHashSet()`. Existing `IsAllowed(string region)` API unchanged.
- `Features/Provisioning/HostnameGenerator.cs` — new (or in-line in the create endpoint; preference: separate class for testability):
  ```csharp
  public sealed class HostnameGenerator(PortalDbContext db) {
      public async Task<string> GenerateAsync(CancellationToken ct) {
          for (var attempt = 1; attempt <= 5; attempt++) {
              var candidate = $"{RandomNumberGenerator.GetHexString(8).ToLower()}.thany.click";
              var taken = await db.Set<Cloud>().IgnoreQueryFilters()
                  .AnyAsync(c => c.Hostname == candidate, ct);
              if (!taken) return candidate;
          }
          throw new InvalidOperationException("hostname_generation_exhausted");
      }
  }
  ```
  `IgnoreQueryFilters` so soft-deleted clouds' hostnames are still "taken" — soft-delete doesn't free the unique index slot per ADR-0032.

### Backend — Portal.SagaWorker

- `Features/Provisioning/Handlers/TfPlanningHandler.cs` — **minor change**: read `EnrollmentToken` from the new `provisioning_jobs.EnrollmentToken` column instead of generating it. If PORTAL-007's current implementation generates the token on the fly inside the handler, replace that with a column read. (If it already reads from a stash column under a different name, this is a renaming pass — verify before editing.)
- `Infrastructure/Cloudflare/` (new — PORTAL-010b):
  - `ICloudflareDnsClient.cs` — interface with `CreateAAsync(string subdomain, string ip, CancellationToken ct) → Task<string recordId>` and `DeleteAsync(string recordId, CancellationToken ct) → Task`.
  - `CloudflareDnsClient.cs` — real impl using `IHttpClientFactory`'s named `"cloudflare"` client, Polly retry policy (3 attempts, exponential 1s/2s/4s, on 429 + 5xx + transient HTTP exceptions), optimistic-POST + 81057 catch idempotency (Q13c).
  - `StubCloudflareDnsClient.cs` — already exists from PORTAL-007; keep registered for tests. Toggle via `Cloudflare:UseStub` (default false).
  - `CloudflareApiResponse.cs` + nested DTOs — the small set of records needed to deserialize CF responses (success, errors[81057], result.id).
- `Program.cs` (Portal.SagaWorker) — DI registration: swap `StubCloudflareDnsClient` for `CloudflareDnsClient` when `Cloudflare:UseStub == false`; configure the named HttpClient with BaseAddress + bearer token from config; register the Polly handler.

### Database

- Migration `20260516XXXXXX_AddEnrollmentTokenAndPerUserCreateGuard.cs`:
  - Adds `provisioning_jobs.enrollment_token` (nullable text — only set for `kind="create"` rows; destroy jobs leave it null).
  - Adds a partial index `ix_provisioning_jobs_user_create_active` on `(user_id, status)` filtered to `kind='create' AND status NOT IN (<terminal>)` for the per-user in-flight-create guard's lookup. **Note:** this assumes `provisioning_jobs.user_id` exists. If today the user-id is only on the `clouds` table (FK chain `provisioning_jobs.cloud_id → clouds.user_id`), either (a) add a denormalized `user_id` column on `provisioning_jobs` for the partial-index path, or (b) materialize the guard query as `EXISTS (...)` with a join — the partial index becomes harder. **Pick (a) if the column doesn't exist; the denormalization is consistent with how the `Cloud.UserId` lives on the `clouds` row and gets read frequently by ownership checks.**
- No schema changes to `clouds` (`Hostname` unique index already exists from PORTAL-002; `CloudAdminToken` column already exists from PORTAL-002).

### Frontend — Portal.Web

- `src/lib/types/` (new directory, one file per feature; replaces inline anonymous typings):
  - `auth.ts` — `User`, `MeResponse` (the `/api/auth/me` shape; currently inlined in `+page.svelte`)
  - `cloud.ts` — `Cloud`, `CloudSummary`, `ProvisioningStatus` (string union of saga statuses), `JobKind`
  - `provisioning.ts` — `WizardSseEvent` (discriminated union: `phase_started | phase_completed | phase_failed | cloud_ready | cloud_failed | cloud_rolled_back`), `PhaseName` (string union: `tf_planning | tf_applying | dns_creating | awaiting_cloud_callback | awaiting_cert | cloud_registered`), `CloudStatusResponse`
  - `providerMeta.ts` — `RegionInfo`
  - `errors.ts` — `ApiProblem` (`{ error: string, [k: string]: unknown }`)
- `src/lib/http.ts` (new):
  - `parseProblem(r: Response): Promise<ApiProblem | null>` — parses `{ error, ... }` bodies; returns null on non-JSON or successful responses.
  - `apiFetch(input, init?)` — wraps `fetchWithStepUp`; on non-2xx, attaches a `.problem` property to the response (or returns `{ response, problem }` — pick one shape; recommend returning the raw Response and providing `parseProblem` as a separate awaiter to keep call sites explicit about error handling).
- `src/lib/auth.ts` (new):
  - `requireAuth(me: MeResponse | null): MeResponse` — throws a SvelteKit `redirect(302, '/api/auth/signin')` if null. Used in `/clouds/new` and `/clouds/{id}` page `load` functions. Removes the duplicated `onMount` auth-fetches from `totp-challenge/+page.svelte` and `settings/security/+page.svelte` (do this as a small concurrent cleanup; if it grows the diff, defer to a follow-up).
- `src/lib/sse.ts` (new):
  - `subscribeToEvents<T>(url: string, listeners: Partial<Record<string, (data: T) => void>>): () => void` — wraps `EventSource`. Calls listeners by `event.type`. Returns an unsubscribe function that calls `eventSource.close()`. Auto-reconnect is left to the native `EventSource` behavior; we don't manually retry.
- `src/app.css` (new) — design tokens applied to `:root`:
  ```css
  :root {
    --color-bg: #fff;
    --color-text: #1a1a1a;
    --color-muted: #555;
    --color-disabled: #999;
    --color-error: #b00020;
    --color-success: #1b7a3a;
    --color-chip-bg: #f5f5f5;
    --color-backdrop: rgba(0, 0, 0, 0.4);
    --color-border: #ddd;
    --space-1: 0.25rem;
    --space-2: 0.5rem;
    --space-3: 1rem;
    --space-4: 1.5rem;
    --space-5: 2rem;
    --radius: 8px;
    --font-sans: system-ui, -apple-system, sans-serif;
    --font-mono: ui-monospace, monospace;
  }
  ```
  Imported once via `+layout.svelte`. Migrate the existing components' hardcoded colors to the variables as a single sweep (touch `+page.svelte`, `StepUpModal.svelte`, `settings/security/+page.svelte`, `totp-challenge/+page.svelte`). New components use the tokens from day one.
- `src/routes/+page.svelte` — change from auth-only landing to include a "Create cloud" button. Layout:
  - If `data.me` is null → existing sign-in flow (unchanged).
  - If `data.me` is present → "Signed in as {name}" + a primary button `<a href="/clouds/new">Create cloud</a>`. Skip the cloud list (PORTAL-012's territory).
- `src/routes/clouds/new/+page.svelte` (new):
  - State: `let step = $state<'target' | 'review'>('target')`; `let region = $state('')`; `let regions = $state<RegionInfo[]>([])`; `let submitting = $state(false)`; `let submitError = $state<string | null>(null)`.
  - On mount: `regions = await fetch('/api/clouds/provider-meta/digitalocean').then(r => r.json()).then(d => d.regions)`.
  - Step 'target': region `<select>` grouped by `<optgroup label="<continent>">`; "Next" button disabled until region selected; "Next" → `step = 'review'`.
  - Step 'review': read-only summary (region label, cost breakdown with tokens, cold-start callout, "no commitment" line); "Edit" → `step = 'target'`; "Provision" → submit via `fetchWithStepUp`.
  - Submit: `POST /api/clouds` with `{ provider: 'digitalocean', region }`. On 202 → `goto(`/clouds/${response.cloudId}`)`. On 401 step_up_required → `fetchWithStepUp` handles modal automatically. On 409 user-in-flight → render inline error. On 400 invalid region → revert to step 'target' with inline error. On 5xx → inline error "Service unavailable, try again."
  - **Component decomposition:** keep as a single file for thesis scope (~200 LOC). If it grows past 300 LOC, extract `TargetStep.svelte` and `ReviewStep.svelte` siblings.
- `src/routes/clouds/[id]/+page.svelte` (new):
  - `load` function fetches REST snapshot via `GET /api/clouds/{id}`.
  - `onMount`: subscribe to `/api/clouds/{id}/events` via `subscribeToEvents`.
  - State per Q6 + Q10. Renders:
    - Header: hostname, provider, region.
    - Phase tracker: ordered list of phases (`tf_planning → tf_applying → dns_creating → awaiting_cloud_callback → cloud_registered → awaiting_cert → succeeded`). Each phase has a status dot: pending / in-progress / done / failed.
    - Cost summary (re-rendered from `lib/costs.ts` constants — same component as the wizard's review step).
    - Cold-start callout once `succeeded` (Q12b).
    - On `cloud_ready`: success card with hostname link.
    - On `cloud_failed`: failure card per Q10 (headline + last events tail + "Try again" → `/clouds/new` + "Try destroy again" only if `terminal_status === 'failed_destroy'`).
    - On `cloud_rolled_back`: terminal "destroy complete" card.
- `src/routes/settings/security/+page.svelte` — **extend** the existing page with a new section under "Set passphrase":
  - "Provider credentials" header.
  - One `<ProviderTokenSection provider="digitalocean" />` component (Azure omitted per Q11d (ii); no Cloudflare info row per Q11d (iv)).
- `src/lib/ProviderTokenSection.svelte` (new component, used on settings/security):
  - Props: `provider: 'digitalocean'` (string union, ready for `| 'azure'` later).
  - State: `status: 'loading' | 'not-set' | 'set'`; `summary: ProviderTokenSummary | null`; `tokenInput: string`; `error: string | null`.
  - On mount: fetch `GET /api/clouds/provider-tokens` (filter to this provider). Set status accordingly.
  - "Not set" UI: password input + "Save" button. Loose client validation (Q11a (i)): reject if `tokenInput.length < 16` or contains whitespace, otherwise accept. Save via `fetchWithStepUp(POST /api/clouds/provider-tokens, { provider, token })`. On 204: refresh status.
  - "Set" UI: timestamp display (Q11b: `Saved · created <ISO date>`, or `Last rotated <ISO date>` if `UpdatedAt > CreatedAt`); "Rotate" button (re-shows the password input); "Remove" button (Q11c: confirm modal "Removing this token will prevent any further infrastructure operations against your DigitalOcean clouds. Continue?" → `fetchWithStepUp(DELETE /api/clouds/provider-tokens/digitalocean)`).
- `src/lib/costs.ts` (new, hardcoded per Q12a (i)):
  ```ts
  export const DigitalOceanCosts = {
    controlPlaneMonthly: 24,        // USD, s-2vcpu-4gb
    workerHourly: 0.125,            // USD, s-4vcpu-16gb on demand
    typicalMonthlyMin: 28,
    typicalMonthlyMax: 32,
    coldStartMinutes: 5,
    warmWindowMinutes: 10,
  };
  ```
- `src/lib/CostSummary.svelte` (new) — renders the cost block (used in wizard Review + `/clouds/{id}`).

### ADR-0036 — wizard progress transport

New file: `docs/decisions/0036-wizard-progress-transport.md`. Full text below in the "ADR-0036" section. ~120 lines.

**Out of scope (DO NOT touch):**

- **PORTAL-012 (cloud-list dashboard)** — out of scope. Don't add a list to `/` beyond the single "Create cloud" button.
- **PORTAL-013 (plugin-token issuance UI)** — out of scope. The bundle does NOT add UI for issuing plugin tokens against a `succeeded` cloud. PORTAL-013 owns that.
- **PORTAL-014 (cloud admin proxy)** — out of scope.
- **PORTAL-009 (Azure terraform module)** — out of scope. Wizard hardcodes `provider: "digitalocean"`. When 009 lands, the provider becomes a wizard field; mark a `TODO(PORTAL-009)` in the wizard's form state where the hardcode lives.
- **CLOUD-001..006 (cloud-side services)** — out of scope. PORTAL-010's nginx-placeholder cloud is sufficient for the saga to reach `succeeded` (the placeholder serves `cert_ready: true` once Caddy actually has a cert).
- **Frontend Playwright e2e harness** — out of scope. The bundle is testable via manual smoke + integration tests (`WebApplicationFactory` + Testcontainers). The Playwright harness is the `e2e-auth-plan-2026-05-16.md` parked plan.
- **Real DO credentials in CI** — out of scope. Manual smoke against `DO_TF_LIVE_TOKEN` per PORTAL-008's pattern.
- **BYO-domain (user-supplied custom hostname)** — out of scope per DEC-003. Hostname is always server-generated `<random8>.thany.click`.
- **Size override** — out of scope. Always `Provisioning:DefaultSize`.
- **Cloud-list nav / cloud-list dashboard** — see PORTAL-012.
- **Refactoring `fetchWithStepUp`** — keep it as-is. The new `apiFetch` helper wraps it; don't rewrite the inner.
- **Splitting `settings/security/+page.svelte` into sub-routes** — out of scope. Add the new section in-place.
- **Hard-deleting failed_* cloud rows** — out of scope. Stale rows linger; PORTAL-012 adds a cleanup affordance later.

## Output of the bundle — final directory state

```
Thany-Marcus/
├── docs/decisions/
│   └── 0036-wizard-progress-transport.md           # NEW
├── src/ThanyMarcus.Portal.Api/
│   ├── Features/
│   │   ├── CloudManagement/
│   │   │   ├── Create/                             # NEW
│   │   │   │   ├── CreateCloudRequest.cs
│   │   │   │   ├── CreateCloudResponse.cs
│   │   │   │   ├── CreateCloudEndpoints.cs
│   │   │   │   └── EnrollmentTokenGenerator.cs
│   │   │   ├── Status/                             # NEW
│   │   │   │   ├── GetCloudStatusEndpoints.cs
│   │   │   │   └── CloudStatusResponse.cs
│   │   │   ├── Events/                             # NEW
│   │   │   │   ├── CloudEventsEndpoints.cs
│   │   │   │   ├── SagaEventTranslator.cs
│   │   │   │   └── WizardSseEvent.cs
│   │   │   ├── ProviderMeta/                       # NEW
│   │   │   │   └── ProviderMetaEndpoints.cs
│   │   │   └── Callback/                           # NEW
│   │   │       └── CloudCallbackEndpoints.cs
│   │   ├── Provisioning/
│   │   │   ├── DigitalOceanRegions.cs              # CHANGED: add Catalog
│   │   │   ├── EnqueueGuard.cs                     # CHANGED: + CheckUserCreateInFlightAsync
│   │   │   └── HostnameGenerator.cs                # NEW
│   │   └── Auth/RateLimiting/
│   │       └── CloudCallbackPolicies.cs            # NEW: two policies for PORTAL-016
│   ├── Infrastructure/Database/Migrations/
│   │   └── 20260516XXXXXX_AddEnrollmentTokenAndPerUserCreateGuard.cs  # NEW
│   └── Program.cs                                  # CHANGED: register the new endpoints + policies
├── src/ThanyMarcus.Portal.SagaWorker/
│   ├── Features/Provisioning/Handlers/
│   │   └── TfPlanningHandler.cs                    # CHANGED: read EnrollmentToken from job row
│   ├── Infrastructure/Cloudflare/                  # NEW
│   │   ├── ICloudflareDnsClient.cs
│   │   ├── CloudflareDnsClient.cs                  # the real impl (PORTAL-010b)
│   │   ├── StubCloudflareDnsClient.cs              # existing; keep
│   │   └── CloudflareApiResponse.cs
│   ├── Program.cs                                  # CHANGED: swap stub for real when !Cloudflare:UseStub
│   └── appsettings.json                            # CHANGED: Cloudflare section
├── src/ThanyMarcus.Portal.Web/
│   ├── src/
│   │   ├── app.css                                 # NEW: design tokens
│   │   ├── lib/
│   │   │   ├── types/                              # NEW directory
│   │   │   │   ├── auth.ts
│   │   │   │   ├── cloud.ts
│   │   │   │   ├── provisioning.ts
│   │   │   │   ├── providerMeta.ts
│   │   │   │   └── errors.ts
│   │   │   ├── http.ts                             # NEW
│   │   │   ├── auth.ts                             # NEW
│   │   │   ├── sse.ts                              # NEW
│   │   │   ├── costs.ts                            # NEW
│   │   │   ├── CostSummary.svelte                  # NEW
│   │   │   └── ProviderTokenSection.svelte         # NEW
│   │   └── routes/
│   │       ├── +layout.svelte                      # CHANGED: import app.css
│   │       ├── +page.svelte                        # CHANGED: add Create cloud button
│   │       ├── clouds/
│   │       │   ├── new/+page.svelte                # NEW
│   │       │   └── [id]/+page.svelte               # NEW
│   │       └── settings/security/+page.svelte      # CHANGED: + Provider credentials section
└── tests/ThanyMarcus.Portal.Tests/
    ├── Features/CloudManagement/
    │   ├── Create/
    │   │   ├── CreateCloudEndpointTests.cs         # NEW
    │   │   └── HostnameGeneratorTests.cs           # NEW
    │   ├── Status/
    │   │   └── GetCloudStatusEndpointTests.cs      # NEW
    │   ├── Events/
    │   │   ├── CloudEventsEndpointTests.cs         # NEW
    │   │   └── SagaEventTranslatorTests.cs         # NEW
    │   ├── ProviderMeta/
    │   │   └── ProviderMetaEndpointTests.cs        # NEW
    │   └── Callback/
    │       ├── CloudCallbackEndpointTests.cs       # NEW
    │       └── CloudCallbackRateLimitTests.cs      # NEW
    ├── Provisioning/
    │   └── EnqueueGuardUserCreateTests.cs          # NEW
    └── SagaWorker/Infrastructure/Cloudflare/
        ├── CloudflareDnsClientTests.cs             # NEW (WireMock)
        └── CloudflareDnsClientRetryTests.cs        # NEW
```

**Sub-edits in other files (small surgical edits, not full rewrites):**

- `src/ThanyMarcus.Portal.SagaWorker/appsettings.json` — `Provisioning:DefaultSize` from `s-4vcpu-16gb` to `s-2vcpu-4gb` per DEC-001 revised.
- `plans/portal-015-handoff.md` — lines 285, 790, 800: replace "polls `/api/clouds/{id}/status`" with "subscribes to `/api/clouds/{id}/events` (SSE) + `GET /api/clouds/{id}` (REST snapshot)."
- `plans/cloud-pivot-plan-2026-05-13.md` §22.5 — strike "portal pre-creates a Cloudflare A-record placeholder before terraform applies" and replace with "portal creates the Cloudflare A-record at `dns_creating` after terraform applies and outputs the droplet IP, per ADR-0033's saga shape."
- `docs/decisions/0033-provisioning-saga-and-worker.md` "Related" section — repoint `[[0035-wizard-progress-transport]]` to `[[0036-wizard-progress-transport]]`.
- `plans/portal-007-handoff.md` — if it references the dead `[[0035-wizard-progress-transport]]`, repoint to `[[0036-wizard-progress-transport]]`. (Greppable: `grep -n "0035-wizard" plans/portal-007*`)

## Packages

- **Backend (Portal.Api):** no new NuGet. Reuses Npgsql (for the SSE handler's `LISTEN`), EF Core, RateLimiter middleware, NodaTime.
- **Backend (Portal.SagaWorker):** no new NuGet for PORTAL-010b's HttpClient pattern — `Microsoft.Extensions.Http.Polly` is the standard Polly+IHttpClientFactory bridge; **add it to `Directory.Packages.props` if not present**. Inspect first.
- **Frontend:** no new pnpm dependencies. Native `EventSource` API, native `fetch`. SvelteKit `goto` already available.
- **Tests:** `WireMock.Net` for the Cloudflare client tests — **add to `Directory.Packages.props` if not present**.

## ADR-0036 — wizard progress transport

File: `docs/decisions/0036-wizard-progress-transport.md`. Verbatim content to land:

```markdown
# ADR-0036: Wizard progress transport — SSE + REST snapshot

Status: Accepted
Date: 2026-05-16
Supersedes: the planned-but-never-written `[[0035-wizard-progress-transport]]` referenced from ADR-0033 (the number 0035 was claimed by burst-worker tier on 2026-05-16; this is its replacement).

## Context

ADR-0033 locked the saga's mutable status + events_log shape. ADR-0020 chose SSE as the
canonical user-facing server-push transport. The wizard (PORTAL-011) is the first consumer
of both; PORTAL-015's destroy modal and PORTAL-012's dashboard will reuse the same channel.

What was not pinned by ADR-0020 / ADR-0033:
1. The exact SSE event vocabulary the wizard subscribes to.
2. How initial state arrives on page load (vs. SSE which is live-only).
3. Reconnect semantics (replay-from-N vs. re-snapshot).
4. How the server multiplexes saga state changes onto SSE subscribers.
5. Auth posture on the channel.

This ADR pins those.

## Decisions

### 1. Curated event vocabulary, not raw events_log

The SSE channel emits a fixed set of 6 named events. The server-side `SagaEventTranslator`
maps internal `events_log` entries to this vocabulary. The frontend does not see raw events_log.

| event              | data                                                       |
|--------------------|------------------------------------------------------------|
| phase_started      | { phase: PhaseName, at: ISO8601 }                          |
| phase_completed    | { phase: PhaseName, at: ISO8601 }                          |
| phase_failed       | { phase: PhaseName, reason: string, message: string }      |
| cloud_ready        | { cloudId: UUID, hostname: string, ip: string }            |
| cloud_failed       | { terminalStatus: string, reason: string, message: string }|
| cloud_rolled_back  | { reason: string }                                         |

PhaseName ∈ { tf_planning | tf_applying | dns_creating | awaiting_cloud_callback | cloud_registered | awaiting_cert }.

Plus an SSE-comment heartbeat (`: heartbeat\n\n`) every 30s.

Rationale: events_log carries internal cardinality (record ids, stderr, sub-phase entries)
the wizard doesn't need. Coupling the wire to events_log makes every internal schema
tweak a frontend-breaking change. Curated vocabulary is small, stable, debuggable.

### 2. Initial state via REST snapshot, not SSE replay

`GET /api/clouds/{id}` returns the full cloud + current-job state synchronously on page
load and on every reconnect. The SSE channel only carries events from the moment of
subscription onward — no replay-from-N logic, no `Last-Event-ID` handling.

Rationale: the REST snapshot is needed anyway for refresh / bookmark / share cases.
Reusing it for SSE reconnection collapses two protocols into one snapshot path.

### 3. Reconnection: re-snapshot + resubscribe

Native `EventSource` auto-reconnects. The client's handler on every (re)connect:
1. Re-fetches `GET /api/clouds/{id}`.
2. Re-renders state.
3. Listeners attached to the EventSource catch subsequent live events.

Belt-and-suspenders against any event loss during the disconnect window.

### 4. Server-side multiplexing: LISTEN/NOTIFY

The saga worker emits `pg_notify('provisioning_job_changed', <jobId>)` inside the same
transaction as every events_log append (and every status transition). The SSE handler
holds a dedicated `NpgsqlConnection` doing `LISTEN provisioning_job_changed`. On each
notification matching a subscribed jobId, the handler re-queries the job row and asks
`SagaEventTranslator` to compute deltas vs. the last-emitted state, then emits curated events.

Rationale: matches ADR-0019's choice for worker dispatch. Zero polling. Sub-second
fan-out latency. One Postgres primitive across two consumers.

### 5. Auth: cookie + ownership check

The endpoint is `[Authorize]` (cookie scheme) + requires `cloud.UserId == claims.SubUs`.
Returns 403 if the caller doesn't own the cloud, 404 if it doesn't exist.

No bearer-token auth on this channel (EventSource doesn't support custom headers).
Same-origin cookie auth is sufficient because the SPA is served from the same origin
as the API (per `portal_web_stack` memory).

### 6. Terminal-state close

On `cloud_ready` / `cloud_failed` / `cloud_rolled_back`, the server emits the event,
flushes a `: closed\n\n` SSE comment, and closes the response. Client detects the
terminal event type and calls `eventSource.close()` to prevent auto-reconnect.

### 7. Heartbeat

`: heartbeat\n\n` every 30 seconds. SSE comments are not delivered to the EventSource
listener, so this is invisible to the frontend code path; it exists to keep proxies
(Caddy, Cloudflare if added later) from idle-killing the connection.

## Consequences

- Frontend has 6 listener types to wire, not "render whatever the wire says." Adding a
  new phase requires a coordinated change to the translator + the listener set + the
  vocabulary in this ADR. That coordination is the desired property — silent schema
  drift is what motivated the curation.
- LISTEN/NOTIFY scales to ~1000s of concurrent SSE subscribers on a 2 vCPU control
  plane; well past thesis budget. If we ever hit it, the bottleneck is Postgres'
  connection count, not the notification fan-out.
- REST snapshot endpoint duplicates information available via the saga's events_log,
  but in a stable shape. The frontend never reads events_log directly; the snapshot
  is the source of truth for "what state is the cloud in right now."

## Related

- [[0019-background-work-and-saga-durability]] — LISTEN/NOTIFY + safety-net poll
- [[0020-server-push-sse]] — the canonical transport choice
- [[0033-provisioning-saga-and-worker]] — saga + events_log shape
- [[0034-cloud-bootstrap-and-portal-handshake]] — what the saga's `awaiting_cert` polls
- [[0035-burst-worker-llm-tier]] — unrelated; just notes the 0035 number was rebound
- `plans/portal-011-handoff.md` — the consumer.
```

## The `POST /api/clouds` endpoint

`Features/CloudManagement/Create/CreateCloudEndpoints.cs`:

```csharp
using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using ThanyMarcus.Portal.Api.Features.Auth;
using ThanyMarcus.Portal.Api.Features.Auth.StepUp;
using ThanyMarcus.Portal.Api.Features.CloudManagement;
using ThanyMarcus.Portal.Api.Features.Provisioning;
using ThanyMarcus.Portal.Api.Infrastructure.Database;

namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;

public static class CreateCloudEndpoints
{
    public static void MapCreateCloudEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/clouds", async (
            CreateCloudRequest body,
            ClaimsPrincipal user,
            PortalDbContext db,
            EnqueueGuard guard,
            HostnameGenerator hostnameGen,
            EnrollmentTokenGenerator tokenGen,
            IClock clock,
            CancellationToken ct) =>
        {
            var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);

            if (body.Provider != "digitalocean")
                return Results.BadRequest(new { error = "unsupported_provider", supported = new[] { "digitalocean" } });
            if (!DigitalOceanRegions.IsAllowed(body.Region))
                return Results.BadRequest(new { error = "invalid_region", region = body.Region });

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var inFlight = await guard.CheckUserCreateInFlightAsync(userId, ct);
            if (inFlight is { } existing)
                return Results.Conflict(new { error = "user_create_in_flight", in_flight_job_id = existing.JobId });

            var hostname = await hostnameGen.GenerateAsync(ct);
            var enrollmentToken = tokenGen.Generate();
            var now = clock.GetCurrentInstant();

            var cloud = new Cloud
            {
                UserId             = userId,
                Provider           = body.Provider,
                Region             = body.Region,
                Hostname           = hostname,
                ProvisioningStatus = "pending",
                CreatedAt          = now,
            };
            db.Set<Cloud>().Add(cloud);
            await db.SaveChangesAsync(ct); // get cloud.Id

            var job = new ProvisioningJob
            {
                CloudId          = cloud.Id,
                UserId           = userId,    // denormalized for the partial-index guard
                Kind             = SagaKinds.Create,
                Status           = "pending",
                EnrollmentToken  = enrollmentToken,
                NextVisibleAt    = now,
                Payload          = JsonDocument.Parse("""{"reason":"user_initiated"}"""),
                EventsLog        = JsonDocument.Parse("[]"),
            };
            db.Set<ProvisioningJob>().Add(job);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            // Wake the worker promptly (LISTEN/NOTIFY); the safety-net poll catches misses.
            await db.Database.ExecuteSqlRawAsync("SELECT pg_notify('provisioning_new', @p0)", new[] { job.Id.ToString() }, ct);

            return Results.AcceptedAtRoute(
                "GetCloudStatus",
                new { id = cloud.Id },
                new CreateCloudResponse(cloud.Id, job.Id, cloud.Hostname));
        })
        .RequireAuthorization(AuthPolicies.TotpRequired)
        .AddEndpointFilter<RequireInfraOpUnlockFilter>();
    }
}
```

**Notes:**

- **`UserId` denormalized on `provisioning_jobs`** to support the partial-index guard. If PORTAL-002's `provisioning_jobs` schema lacks `user_id`, add it via the migration described above.
- **`pg_notify('provisioning_new', <jobId>)`** matches PORTAL-007's safety-net-poll channel (per ADR-0019). The `provisioning_job_changed` channel is a *separate* notification fired by the saga handlers as they update the job's status — used by the SSE handler's `LISTEN`. **Don't conflate the two channels:** `provisioning_new` is producer→worker (job available), `provisioning_job_changed` is worker→SSE-subscriber (state advanced).
- **Hostname generation inside the SERIALIZABLE tx** so a concurrent create (vanishingly unlikely) can't grab the same string between generate-and-check vs. insert.
- **Step-up filter triggers a 401 with `step_up_required`** when the user's DEK is not in `IInfraOpUnlockCache`. `fetchWithStepUp` on the SPA side catches and prompts. Otherwise the endpoint never sees the DEK; the saga reads it during `TfPlanningHandler`.

`Features/CloudManagement/Create/CreateCloudRequest.cs`:

```csharp
namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;
public sealed record CreateCloudRequest(string Provider, string Region);
```

`Features/CloudManagement/Create/CreateCloudResponse.cs`:

```csharp
namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;
public sealed record CreateCloudResponse(Guid CloudId, Guid JobId, string Hostname);
```

`Features/CloudManagement/Create/EnrollmentTokenGenerator.cs`:

```csharp
using System.Security.Cryptography;
namespace ThanyMarcus.Portal.Api.Features.CloudManagement.Create;
public sealed class EnrollmentTokenGenerator
{
    public string Generate() => RandomNumberGenerator.GetHexString(64).ToLower(); // 32 bytes = 64 hex chars
}
```

Registered in `Program.cs`: `app.MapCreateCloudEndpoints(); builder.Services.AddSingleton<EnrollmentTokenGenerator>(); builder.Services.AddScoped<HostnameGenerator>();`.

## The `GET /api/clouds/{id}` REST snapshot

```csharp
app.MapGet("/api/clouds/{id:guid}", async (Guid id, ClaimsPrincipal user, PortalDbContext db, CancellationToken ct) =>
{
    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
    var cloud = await db.Set<Cloud>().IgnoreQueryFilters()
        .SingleOrDefaultAsync(c => c.Id == id, ct);
    if (cloud is null) return Results.NotFound(new { error = "cloud_not_found" });
    if (cloud.UserId != userId) return Results.Forbid();

    var job = await db.Set<ProvisioningJob>()
        .Where(j => j.CloudId == id)
        .OrderByDescending(j => j.CreatedAt)
        .FirstOrDefaultAsync(ct);

    var recentEvents = job is null
        ? Array.Empty<EventSummary>()
        : ParseEventsTail(job.EventsLog, take: 10);  // server-filtered curation

    return Results.Ok(new CloudStatusResponse(
        CloudId: cloud.Id,
        Hostname: cloud.Hostname,
        Provider: cloud.Provider,
        Region: cloud.Region,
        ProvisioningStatus: cloud.ProvisioningStatus,
        SucceededAt: cloud.SucceededAt,
        DestroyedAt: cloud.DestroyedAt,
        CurrentJob: job is null ? null : new JobSummary(job.Id, job.Kind, job.Status),
        RecentEvents: recentEvents
    ));
})
.WithName("GetCloudStatus")
.RequireAuthorization();
```

`ParseEventsTail` strips stderr / internal record-ids and emits `{ phase, status, reason, at }` shapes the SPA can render directly.

## The `GET /api/clouds/{id}/events` SSE endpoint

Skeleton (the full handler is ~150 LOC; documenting the shape):

```csharp
app.MapGet("/api/clouds/{id:guid}/events", async (
    Guid id,
    HttpContext http,
    ClaimsPrincipal user,
    PortalDbContext db,
    NpgsqlConnectionFactory connFactory,    // a small wrapper around Npgsql connection string config
    SagaEventTranslator translator,
    CancellationToken ct) =>
{
    var userId = Guid.Parse(user.FindFirstValue(AuthClaimTypes.SubUs)!);
    var cloud = await db.Set<Cloud>().IgnoreQueryFilters().SingleOrDefaultAsync(c => c.Id == id, ct);
    if (cloud is null) { http.Response.StatusCode = 404; return; }
    if (cloud.UserId != userId) { http.Response.StatusCode = 403; return; }

    http.Response.Headers.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";
    http.Response.Headers.Connection = "keep-alive";

    await using var conn = await connFactory.OpenAsync(ct);
    await using (var cmd = new NpgsqlCommand("LISTEN provisioning_job_changed;", conn))
        await cmd.ExecuteNonQueryAsync(ct);

    // Initial: peek state and emit current phase as `phase_started` if non-terminal.
    var lastJobState = await LoadJobStateAsync(db, id, ct);
    foreach (var evt in translator.InitialEvents(lastJobState))
        await WriteSseAsync(http.Response, evt, ct);

    var heartbeat = StartHeartbeat(http.Response, ct);
    conn.Notification += async (_, args) =>
    {
        if (!Guid.TryParse(args.Payload, out var changedJobId)) return;
        if (lastJobState?.JobId != changedJobId) return;  // not our job
        var current = await LoadJobStateAsync(db, id, ct);
        var deltas = translator.Translate(lastJobState, current);
        foreach (var evt in deltas) await WriteSseAsync(http.Response, evt, ct);
        lastJobState = current;
        if (current is { IsTerminal: true })
        {
            await WriteSseCommentAsync(http.Response, "closed", ct);
            http.RequestAborted.ThrowIfCancellationRequested();
            heartbeat.Stop();
        }
    };

    while (!ct.IsCancellationRequested)
        await conn.WaitAsync(ct);   // unblocks on each NOTIFY
});
```

**Implementation notes:**

- `NpgsqlConnectionFactory` is a thin DI-friendly wrapper around `new NpgsqlConnection(connectionString)` so tests can inject a fake. Add `Infrastructure/Database/NpgsqlConnectionFactory.cs` if it doesn't already exist (PORTAL-002 may have one for the EF Core data source).
- `WriteSseAsync` formats as `event: <name>\ndata: <json>\n\n`. Use `System.Text.Json` source-gen for the data DTO to avoid reflection on the hot path.
- `WriteSseCommentAsync` writes `: <comment>\n\n` (the leading colon = SSE comment, not delivered to the client listener).
- The `conn.Notification` event handler runs on Npgsql's connection thread; capture `http.Response` carefully (it's safe across awaits as long as the request isn't aborted).
- On `http.RequestAborted` (client disconnects), the `while (await conn.WaitAsync(ct))` loop throws `OperationCanceledException` and unwinds. The `using` blocks release the LISTEN connection back to the pool.

`SagaEventTranslator.cs` (pure logic, easily unit-tested):

```csharp
public sealed class SagaEventTranslator
{
    public IEnumerable<WizardSseEvent> InitialEvents(JobState? state) {
        if (state is null) yield break;
        if (state.IsTerminal) {
            yield return state.Status switch {
                "succeeded"      => WizardSseEvent.CloudReady(state.CloudId, state.Hostname, state.Ip),
                "rolled_back"    => WizardSseEvent.CloudRolledBack(state.RollbackReason),
                _                => WizardSseEvent.CloudFailed(state.Status, state.Reason, state.Message),
            };
        } else {
            yield return WizardSseEvent.PhaseStarted(PhaseFor(state.Status));
        }
    }

    public IEnumerable<WizardSseEvent> Translate(JobState? prev, JobState curr) {
        if (prev?.Status == curr.Status) yield break;
        if (prev != null) yield return WizardSseEvent.PhaseCompleted(PhaseFor(prev.Status));
        if (curr.IsTerminal) yield return TerminalEvent(curr);
        else yield return WizardSseEvent.PhaseStarted(PhaseFor(curr.Status));
    }

    private static string PhaseFor(string status) => status switch {
        "tf_planning" or "tf_applying" or "dns_creating" or "awaiting_cloud_callback" or "awaiting_cert" => status,
        _ => throw new InvalidOperationException($"Unmapped status {status}"),
    };

    private static WizardSseEvent TerminalEvent(JobState state) => state.Status switch {
        "succeeded"   => WizardSseEvent.CloudReady(state.CloudId, state.Hostname, state.Ip),
        "rolled_back" => WizardSseEvent.CloudRolledBack(state.RollbackReason),
        _             => WizardSseEvent.CloudFailed(state.Status, state.Reason, state.Message),
    };
}
```

`WizardSseEvent.cs`:

```csharp
public abstract record WizardSseEvent(string Type)
{
    public sealed record PhaseStarted(string Phase) : WizardSseEvent("phase_started");
    public sealed record PhaseCompleted(string Phase) : WizardSseEvent("phase_completed");
    public sealed record PhaseFailed(string Phase, string Reason, string Message) : WizardSseEvent("phase_failed");
    public sealed record CloudReady(Guid CloudId, string Hostname, string Ip) : WizardSseEvent("cloud_ready");
    public sealed record CloudFailed(string TerminalStatus, string Reason, string Message) : WizardSseEvent("cloud_failed");
    public sealed record CloudRolledBack(string Reason) : WizardSseEvent("cloud_rolled_back");
    public static PhaseStarted PhaseStarted(string phase) => new(phase);
    public static PhaseCompleted PhaseCompleted(string phase) => new(phase);
    // … other static factories
}
```

## The PORTAL-016 callback endpoint

`Features/CloudManagement/Callback/CloudCallbackEndpoints.cs`:

```csharp
app.MapPost("/api/clouds/{cloudId:guid}/callback", async (
    Guid cloudId,
    CloudCallbackRequest body,
    PortalDbContext db,
    IClock clock,
    CancellationToken ct) =>
{
    if (body.CloudId != cloudId)
        return Results.BadRequest(new { error = "cloud_id_mismatch" });

    await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

    var cloud = await db.Set<Cloud>().IgnoreQueryFilters().SingleOrDefaultAsync(c => c.Id == cloudId, ct);
    if (cloud is null) return Results.NotFound(new { error = "cloud_not_found" });

    var job = await db.Set<ProvisioningJob>()
        .Where(j => j.CloudId == cloudId && j.Kind == SagaKinds.Create)
        .OrderByDescending(j => j.CreatedAt)
        .FirstOrDefaultAsync(ct);
    if (job is null || job.EnrollmentToken is null)
        return Results.NotFound(new { error = "no_create_job" });

    if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(job.EnrollmentToken),
            Encoding.UTF8.GetBytes(body.EnrollmentToken)))
        return Results.Unauthorized();

    if (job.Status is "awaiting_cert" or "succeeded")
    {
        // idempotent replay; tokens matched; do nothing
        await tx.CommitAsync(ct);
        return Results.Ok(new { idempotent = true });
    }

    if (job.Status != "awaiting_cloud_callback")
        return Results.Conflict(new { error = "wrong_state", current = job.Status });

    var now = clock.GetCurrentInstant();
    cloud.CloudAdminToken = body.CloudAdminToken;  // plaintext per ADR-0034 + cloud-pivot §21
    job.Status = "awaiting_cert";
    job.NextVisibleAt = now;  // wake the cert poll handler immediately
    AppendEvent(job, new { phase = "cloud_registered", at = now });

    await db.SaveChangesAsync(ct);
    await tx.CommitAsync(ct);
    await db.Database.ExecuteSqlRawAsync("SELECT pg_notify('provisioning_job_changed', @p0)",
        new[] { job.Id.ToString() }, ct);

    return Results.Ok(new { ok = true });
})
.RequireRateLimiting("cloud_callback_per_id")    // per route param
.RequireRateLimiting("cloud_callback_per_ip");   // per IP, layered
```

**Notes:**

- **`FixedTimeEquals` for token comparison** even though enrollment_token is 256-bit random and timing attacks are practically infeasible. Cheap defense; matches the convention from PORTAL-003's TOTP comparison.
- **Idempotent replay (Q14c (ii)):** if state has already advanced past `awaiting_cloud_callback` and tokens match, return 200. This is the common case for cloud-init's retry-on-network-error path where the first response was lost.
- **No auth scheme.** Cookie auth would require the cloud to hold a portal session, which makes no sense. The token IS the auth.
- **Two rate-limit policies stacked.** ASP.NET's rate limiter allows multiple `RequireRateLimiting` calls; both must pass.

`CloudCallbackRequest.cs`:

```csharp
public sealed record CloudCallbackRequest(Guid CloudId, string EnrollmentToken, string CloudAdminToken);
```

`Features/Auth/RateLimiting/CloudCallbackPolicies.cs`:

```csharp
public static class CloudCallbackPolicies
{
    public const string PerCloudId = "cloud_callback_per_id";
    public const string PerIp = "cloud_callback_per_ip";

    public static void Register(RateLimiterOptions options)
    {
        options.AddPolicy(PerCloudId, ctx =>
        {
            var id = ctx.Request.RouteValues["cloudId"]?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(id, _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(5) });
        });
        options.AddPolicy(PerIp, ctx =>
        {
            var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(ip, _ =>
                new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(5) });
        });
    }
}
```

## The PORTAL-010b Cloudflare client

`Infrastructure/Cloudflare/CloudflareDnsClient.cs`:

```csharp
public sealed class CloudflareDnsClient(IHttpClientFactory http, IOptions<CloudflareOptions> opts, ILogger<CloudflareDnsClient> log)
    : ICloudflareDnsClient
{
    public async Task<string> CreateAAsync(string subdomain, string ip, CancellationToken ct)
    {
        var client = http.CreateClient("cloudflare");
        var zone = opts.Value.ZoneId;
        var payload = new
        {
            type = "A",
            name = subdomain,        // full FQDN; e.g., "abc12345.thany.click"
            content = ip,
            ttl = 1,                 // 1 = "auto" in CF parlance
            proxied = false,         // direct A record; we want LE HTTP-01 to reach the droplet directly
        };
        var response = await client.PostAsJsonAsync($"zones/{zone}/dns_records", payload, ct);

        if (response.IsSuccessStatusCode)
        {
            var ok = await response.Content.ReadFromJsonAsync<CloudflareResponse<DnsRecord>>(ct);
            return ok!.Result.Id;
        }

        var err = await response.Content.ReadFromJsonAsync<CloudflareErrorResponse>(ct);
        if (err?.Errors.Any(e => e.Code == 81057) == true)
        {
            // Record already exists; fetch the existing one.
            log.LogInformation("Cloudflare A record for {Subdomain} already exists; fetching", subdomain);
            var listed = await client.GetFromJsonAsync<CloudflareResponse<List<DnsRecord>>>(
                $"zones/{zone}/dns_records?type=A&name={Uri.EscapeDataString(subdomain)}", ct);
            var existing = listed?.Result.FirstOrDefault();
            if (existing is null)
                throw new CloudflareApiException($"81057 said record exists but list returned empty for {subdomain}");
            return existing.Id;
        }

        throw new CloudflareApiException($"CreateA failed: {response.StatusCode} {string.Join(';', err?.Errors ?? [])}");
    }

    public async Task DeleteAsync(string recordId, CancellationToken ct)
    {
        var client = http.CreateClient("cloudflare");
        var zone = opts.Value.ZoneId;
        var response = await client.DeleteAsync($"zones/{zone}/dns_records/{recordId}", ct);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
            return;  // 404 on delete = idempotent success
        throw new CloudflareApiException($"Delete failed: {response.StatusCode}");
    }
}
```

`Program.cs` (SagaWorker) — DI registration:

```csharp
builder.Services.Configure<CloudflareOptions>(builder.Configuration.GetSection("Cloudflare"));
builder.Services.AddHttpClient("cloudflare", (sp, c) =>
{
    var o = sp.GetRequiredService<IOptions<CloudflareOptions>>().Value;
    c.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
    c.DefaultRequestHeaders.Authorization = new("Bearer", o.ApiToken);
})
.AddPolicyHandler(HttpPolicyExtensions
    .HandleTransientHttpError()
    .OrResult(r => r.StatusCode == HttpStatusCode.TooManyRequests)
    .WaitAndRetryAsync(3, n => TimeSpan.FromSeconds(Math.Pow(2, n - 1))));  // 1s, 2s, 4s

if (builder.Configuration.GetValue<bool>("Cloudflare:UseStub"))
    builder.Services.AddSingleton<ICloudflareDnsClient, StubCloudflareDnsClient>();
else
    builder.Services.AddSingleton<ICloudflareDnsClient, CloudflareDnsClient>();
```

`appsettings.json` (SagaWorker) additions:

```json
{
  "Cloudflare": {
    "ZoneId": "<thany.click zone id — non-secret>",
    "ApiToken": "",
    "UseStub": false
  }
}
```

`appsettings.Development.json`: `"UseStub": true` so dev runs don't hit real Cloudflare. The real token is injected in production via `CLOUDFLARE__APITOKEN` env var.

## Frontend foundation — types, http, auth, sse, costs

### `src/lib/types/auth.ts`

```ts
export type TotpState = 'not-enabled' | 'not-verified' | 'verified';

export interface MeResponse {
  userId: string;
  email: string;
  name: string;
  profilePictureUrl: string | null;
  totp: TotpState;
  passphraseSet: boolean;
}
```

### `src/lib/types/cloud.ts`

```ts
export type Provider = 'digitalocean';   // | 'azure' once PORTAL-009 lands
export type JobKind = 'create' | 'destroy';
export type ProvisioningStatus =
  | 'pending' | 'tf_planning' | 'tf_applying' | 'dns_creating'
  | 'awaiting_cloud_callback' | 'awaiting_cert' | 'succeeded'
  | 'destroying' | 'rolling_back_dns' | 'rolling_back_tf'
  | 'rolled_back' | 'failed_tf' | 'failed_dns' | 'failed_callback' | 'failed_cert' | 'failed_destroy';

export interface Cloud {
  cloudId: string;
  hostname: string;
  provider: Provider;
  region: string;
  provisioningStatus: ProvisioningStatus;
  succeededAt: string | null;
  destroyedAt: string | null;
}

export interface JobSummary { jobId: string; kind: JobKind; status: ProvisioningStatus; }
export interface EventSummary { phase: string; status: string; reason?: string; at: string; }
export interface CloudStatusResponse extends Cloud {
  currentJob: JobSummary | null;
  recentEvents: EventSummary[];
}
```

### `src/lib/types/provisioning.ts`

```ts
export type PhaseName =
  | 'tf_planning' | 'tf_applying' | 'dns_creating'
  | 'awaiting_cloud_callback' | 'cloud_registered' | 'awaiting_cert';

export type WizardSseEvent =
  | { type: 'phase_started'; phase: PhaseName; at: string }
  | { type: 'phase_completed'; phase: PhaseName; at: string }
  | { type: 'phase_failed'; phase: PhaseName; reason: string; message: string }
  | { type: 'cloud_ready'; cloudId: string; hostname: string; ip: string }
  | { type: 'cloud_failed'; terminalStatus: string; reason: string; message: string }
  | { type: 'cloud_rolled_back'; reason: string };
```

### `src/lib/types/providerMeta.ts`

```ts
export interface RegionInfo {
  slug: string;
  label: string;
  continent: string;
}
```

### `src/lib/types/errors.ts`

```ts
export interface ApiProblem {
  error: string;
  [k: string]: unknown;
}
```

### `src/lib/http.ts`

```ts
import { fetchWithStepUp } from './stepUpClient';
import type { ApiProblem } from './types/errors';

export async function parseProblem(r: Response): Promise<ApiProblem | null> {
  if (r.ok) return null;
  const ct = r.headers.get('content-type') ?? '';
  if (!ct.includes('application/json')) return null;
  try { return (await r.clone().json()) as ApiProblem; }
  catch { return null; }
}

export const apiFetch = fetchWithStepUp;
```

`apiFetch` is currently a re-export to make call sites read consistently (`apiFetch(...)` reads as "this hits our API and may need step-up"); future refactors can wrap behavior in here without touching call sites.

### `src/lib/auth.ts`

```ts
import { redirect } from '@sveltejs/kit';
import type { MeResponse } from './types/auth';

export function requireAuth(me: MeResponse | null): MeResponse {
  if (!me) throw redirect(302, '/api/auth/signin');
  return me;
}
```

Called from `+page.ts` / `+layout.ts` `load` functions:

```ts
// src/routes/clouds/new/+page.ts
export const load = async ({ parent }) => {
  const { me } = await parent();
  requireAuth(me);
};
```

### `src/lib/sse.ts`

```ts
type EventMap<T> = Partial<Record<string, (data: T) => void>>;

export function subscribeToEvents<T>(url: string, listeners: EventMap<T>): () => void {
  const es = new EventSource(url);
  for (const [eventName, handler] of Object.entries(listeners)) {
    if (!handler) continue;
    es.addEventListener(eventName, (e) => {
      try { handler(JSON.parse((e as MessageEvent).data) as T); }
      catch (err) { console.error(`SSE handler ${eventName} failed`, err); }
    });
  }
  return () => es.close();
}
```

### `src/lib/costs.ts`

```ts
export const DigitalOceanCosts = {
  controlPlaneMonthly: 24,
  workerHourly: 0.125,
  typicalMonthlyMin: 28,
  typicalMonthlyMax: 32,
  coldStartMinutes: 5,
  warmWindowMinutes: 10,
} as const;
```

### `src/app.css`

```css
:root {
  --color-bg: #fff;
  --color-text: #1a1a1a;
  --color-muted: #555;
  --color-disabled: #999;
  --color-error: #b00020;
  --color-success: #1b7a3a;
  --color-chip-bg: #f5f5f5;
  --color-backdrop: rgba(0, 0, 0, 0.4);
  --color-border: #ddd;
  --space-1: 0.25rem;
  --space-2: 0.5rem;
  --space-3: 1rem;
  --space-4: 1.5rem;
  --space-5: 2rem;
  --radius: 8px;
  --font-sans: system-ui, -apple-system, sans-serif;
  --font-mono: ui-monospace, monospace;
}

main {
  font-family: var(--font-sans);
  color: var(--color-text);
  max-width: 40rem;
  margin: 4rem auto;
  padding: 0 var(--space-3);
}

.error { color: var(--color-error); }
.muted { color: var(--color-muted); }
```

Imported in `+layout.svelte`:

```svelte
<script lang="ts">
  import '../app.css';
  import StepUpModal from '$lib/StepUpModal.svelte';
  let { children } = $props();
</script>
{@render children()}
<StepUpModal />
```

Existing components: migrate hardcoded colors to `var(--color-*)` in a single pass. ~30 LOC of edits.

## Wizard UX spec — `/clouds/new`

State machine:

```
target ── [Next, region selected] ──► review ── [Provision] ──► submit ── [202] ──► goto /clouds/{id}
  ▲                                       │
  └─────── [Edit] ────────────────────────┘
```

**Target step:**

```svelte
<section>
  <h2>Choose a region</h2>
  <p class="muted">Pick the location where your cloud will run.</p>
  <label>
    Region
    <select bind:value={region}>
      <option value="" disabled>Select a region…</option>
      {#each continents as continent}
        <optgroup label={continent.name}>
          {#each continent.regions as r}
            <option value={r.slug}>{r.label}</option>
          {/each}
        </optgroup>
      {/each}
    </select>
  </label>
  <button disabled={!region} onclick={() => step = 'review'}>Next</button>
</section>
```

`continents` is derived from `regions` by groupBy:

```ts
let continents = $derived(
  regions.length === 0 ? [] : groupContinents(regions)
);
function groupContinents(rs: RegionInfo[]) {
  const order = ['North America', 'Europe', 'Asia-Pacific'];
  return order.map(name => ({
    name,
    regions: rs.filter(r => r.continent === name),
  })).filter(c => c.regions.length > 0);
}
```

**Review step:**

```svelte
<section>
  <h2>Review</h2>
  <dl>
    <dt>Region</dt>
    <dd>{regionLabel(region)}</dd>
    <dt>Hostname</dt>
    <dd class="muted">Will be assigned when you provision (random name on thany.click).</dd>
  </dl>
  <CostSummary />
  {#if submitError}<p class="error">{submitError}</p>{/if}
  <div class="actions">
    <button onclick={() => step = 'target'}>Edit</button>
    <button class="primary" disabled={submitting} onclick={submit}>
      {submitting ? 'Provisioning…' : 'Provision'}
    </button>
  </div>
</section>
```

`CostSummary.svelte` renders the headline + breakdown + cold-start callout + no-commitment line from `costs.ts`.

**Submit:**

```ts
async function submit() {
  submitting = true;
  submitError = null;
  try {
    const r = await apiFetch('/api/clouds', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ provider: 'digitalocean', region }),
    });
    if (r.status === 202) {
      const { cloudId } = await r.json();
      goto(`/clouds/${cloudId}`);
      return;
    }
    if (r.status === 401) {
      // step-up cancelled by user; fetchWithStepUp returned the original 401
      submitError = 'Passphrase required to provision.';
      submitting = false;
      return;
    }
    const problem = await parseProblem(r);
    submitError = mapErrorToMessage(problem?.error, r.status);
  } catch (e) {
    submitError = 'Network error. Try again.';
  } finally {
    submitting = false;
  }
}

function mapErrorToMessage(code: string | undefined, status: number): string {
  switch (code) {
    case 'user_create_in_flight': return 'You already have a cloud being provisioned. Wait for it to finish.';
    case 'invalid_region':         return 'That region is not supported.';
    case 'unsupported_provider':   return 'Provider not supported.';
    default:                       return `Provisioning failed (HTTP ${status}). Try again.`;
  }
}
```

## `/clouds/{id}` spec

Page loads via `+page.ts`:

```ts
export const load = async ({ params, fetch, parent }) => {
  const { me } = await parent();
  requireAuth(me);
  const r = await fetch(`/api/clouds/${params.id}`);
  if (r.status === 404) throw error(404, 'Cloud not found');
  if (r.status === 403) throw error(403, 'You don\'t own this cloud');
  return { cloud: await r.json() as CloudStatusResponse };
};
```

Component subscribes on mount:

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import { subscribeToEvents } from '$lib/sse';
  import type { WizardSseEvent } from '$lib/types/provisioning';
  let { data } = $props();
  let cloud = $state(data.cloud);
  let phaseStates = $state(initialPhaseStates(cloud));
  let terminal = $state<TerminalState | null>(initialTerminal(cloud));

  onMount(() => {
    const close = subscribeToEvents<WizardSseEvent>(`/api/clouds/${cloud.cloudId}/events`, {
      phase_started:     (e) => updatePhase(e.phase, 'in_progress'),
      phase_completed:   (e) => updatePhase(e.phase, 'done'),
      phase_failed:      (e) => { updatePhase(e.phase, 'failed'); terminal = { kind: 'failed', ...e }; close(); },
      cloud_ready:       (e) => { cloud = { ...cloud, ...e, succeededAt: new Date().toISOString() }; terminal = { kind: 'ready', ...e }; close(); },
      cloud_failed:      (e) => { terminal = { kind: 'failed', ...e }; close(); },
      cloud_rolled_back: (e) => { terminal = { kind: 'rolled_back', ...e }; close(); },
    });
    return close;
  });
</script>
```

Rendering: phase tracker (ordered list with status dots), cost summary, cold-start callout once ready, terminal cards per Q10.

## Provider-token section component — `ProviderTokenSection.svelte`

```svelte
<script lang="ts">
  import { onMount } from 'svelte';
  import { apiFetch, parseProblem } from '$lib/http';

  let { provider }: { provider: 'digitalocean' } = $props();
  let state = $state<'loading' | 'not-set' | 'set'>('loading');
  let summary = $state<{ createdAt: string; updatedAt: string } | null>(null);
  let tokenInput = $state('');
  let error = $state<string | null>(null);
  let confirmingRemove = $state(false);

  async function refresh() {
    state = 'loading';
    const r = await fetch('/api/clouds/provider-tokens');
    if (!r.ok) { state = 'not-set'; return; }
    const list: Array<{ provider: string; createdAt: string; updatedAt: string }> = await r.json();
    const me = list.find(p => p.provider === provider);
    if (me) { summary = me; state = 'set'; } else { state = 'not-set'; }
  }
  onMount(refresh);

  async function save() {
    error = null;
    if (tokenInput.trim().length < 16 || /\s/.test(tokenInput)) {
      error = "That doesn't look like a valid token.";
      return;
    }
    const r = await apiFetch('/api/clouds/provider-tokens', {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ provider, token: tokenInput }),
    });
    if (r.status === 204) { tokenInput = ''; await refresh(); return; }
    const problem = await parseProblem(r);
    error = problem?.error ?? `Save failed (HTTP ${r.status}).`;
  }

  async function remove() {
    confirmingRemove = false;
    const r = await apiFetch(`/api/clouds/provider-tokens/${provider}`, { method: 'DELETE' });
    if (r.ok || r.status === 204) await refresh();
    else error = `Remove failed (HTTP ${r.status}).`;
  }
</script>

<section class="provider-row">
  <h3>{providerLabel(provider)}</h3>
  {#if state === 'loading'}
    <p class="muted">Loading…</p>
  {:else if state === 'set' && summary}
    <p>Saved · {summary.createdAt === summary.updatedAt ? 'created' : 'last rotated'} {formatDate(summary.updatedAt)}</p>
    <button onclick={() => { state = 'not-set'; tokenInput = ''; }}>Rotate</button>
    <button onclick={() => confirmingRemove = true}>Remove</button>
  {:else}
    <label>
      API token
      <input type="password" autocomplete="off" bind:value={tokenInput} />
    </label>
    {#if error}<p class="error">{error}</p>{/if}
    <button disabled={!tokenInput} onclick={save}>Save</button>
  {/if}

  {#if confirmingRemove}
    <div class="backdrop" onclick={() => confirmingRemove = false}></div>
    <div class="modal">
      <h4>Remove {providerLabel(provider)} token?</h4>
      <p>Removing this token will prevent any further infrastructure operations against your {providerLabel(provider)} clouds. Continue?</p>
      <button onclick={() => confirmingRemove = false}>Cancel</button>
      <button class="danger" onclick={remove}>Remove</button>
    </div>
  {/if}
</section>
```

## Migration — `20260516XXXXXX_AddEnrollmentTokenAndPerUserCreateGuard.cs`

```csharp
public partial class AddEnrollmentTokenAndPerUserCreateGuard : Migration
{
    protected override void Up(MigrationBuilder b)
    {
        // 1. Add the enrollment_token column (nullable; only create-kind rows set it).
        b.AddColumn<string>(name: "enrollment_token", table: "provisioning_jobs", nullable: true);

        // 2. Add the denormalized user_id (if not present) for the partial-index guard.
        //    Check first; if PORTAL-002 already added it, skip.
        b.Sql("""
            DO $$
            BEGIN
              IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                             WHERE table_name='provisioning_jobs' AND column_name='user_id') THEN
                ALTER TABLE provisioning_jobs ADD COLUMN user_id uuid NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000';
                ALTER TABLE provisioning_jobs ALTER COLUMN user_id DROP DEFAULT;
                UPDATE provisioning_jobs pj SET user_id = c.user_id FROM clouds c WHERE pj.cloud_id = c.id;
                ALTER TABLE provisioning_jobs ADD CONSTRAINT fk_provisioning_jobs_user FOREIGN KEY (user_id) REFERENCES users(id);
              END IF;
            END $$;
            """);

        // 3. Partial index for per-user in-flight-create lookup.
        b.Sql("""
            CREATE INDEX ix_provisioning_jobs_user_create_active
              ON provisioning_jobs (user_id)
              WHERE kind = 'create' AND status NOT IN
                ('succeeded','rolled_back','failed_tf','failed_dns','failed_callback','failed_cert','failed_destroy');
            """);
    }

    protected override void Down(MigrationBuilder b)
    {
        b.Sql("DROP INDEX IF EXISTS ix_provisioning_jobs_user_create_active;");
        b.DropColumn(name: "enrollment_token", table: "provisioning_jobs");
        // Don't drop user_id on Down — risks data loss if it was added here.
    }
}
```

## EnqueueGuard extension

```csharp
public sealed class EnqueueGuard(PortalDbContext db)
{
    // existing: per-cloud check for PORTAL-015 ...

    public async Task<InFlightCreate?> CheckUserCreateInFlightAsync(Guid userId, CancellationToken ct)
    {
        var existing = await db.Set<ProvisioningJob>()
            .Where(j => j.UserId == userId
                     && j.Kind == SagaKinds.Create
                     && !SagaStatus.Terminal.Contains(j.Status))
            .Select(j => new { j.Id })
            .FirstOrDefaultAsync(ct);
        return existing is null ? null : new InFlightCreate(existing.Id);
    }

    public sealed record InFlightCreate(Guid JobId);
}
```

## Tests

### Backend integration tests (`WebApplicationFactory` + Testcontainers)

- `CreateCloudEndpointTests`:
  - 401 when not signed in.
  - 401 `step_up_required` when TOTP-verified but unlock cache empty.
  - 400 `unsupported_provider` for `provider: "azure"`.
  - 400 `invalid_region` for unknown slug.
  - 409 `user_create_in_flight` when user has a non-terminal create job.
  - 202 happy path: row inserted with `Kind=create`, `Status=pending`, `EnrollmentToken` populated, hostname matches `[0-9a-f]{8}\.thany\.click`, response body contains all three fields.
  - Race test: 50 parallel POSTs for same user → exactly one succeeds with 202, rest get 409 (relies on SERIALIZABLE + partial-index guard).
- `HostnameGeneratorTests`:
  - Unique on first attempt (no collision case).
  - Retries on collision (seed a `clouds` row with hostname `aaaaaaaa.thany.click`, mock RandomNumberGenerator to return `aaaaaaaa` then `bbbbbbbb`, assert second is returned).
  - Throws after 5 attempts (mock RandomNumberGenerator to always return the same value, seed a clash).
- `GetCloudStatusEndpointTests`:
  - 404, 403, happy path.
  - `recentEvents` is filtered (no stderr leaks).
- `CloudEventsEndpointTests`:
  - 404, 403 before stream opens.
  - Happy path: connect, simulate `pg_notify`, assert SSE wire format `event: phase_started\ndata: {...}\n\n`.
  - Terminal emits + close.
- `SagaEventTranslatorTests`:
  - Pure-logic unit tests for every (prev, curr) state transition pair.
- `ProviderMetaEndpointTests`:
  - Returns the catalog, all 10 entries.
- `CloudCallbackEndpointTests`:
  - 200 happy path: state flips, cloud admin token stored, events_log appended.
  - 200 idempotent replay (state already `awaiting_cert`, tokens match).
  - 401 mismatched token.
  - 404 unknown cloud.
  - 404 cloud exists but no create job.
  - 409 wrong state (saga is in `tf_planning`).
  - `cloud_id` path param vs body mismatch → 400.
- `CloudCallbackRateLimitTests`:
  - 13th request to same cloud_id within 5 min → 429.
  - 61st request from same IP within 5 min → 429.
- `EnqueueGuardUserCreateTests`:
  - Returns null when no jobs.
  - Returns existing when create job is non-terminal.
  - Returns null when create job is terminal.

### Cloudflare client tests (WireMock.Net)

- `CloudflareDnsClientTests`:
  - `CreateAAsync` success path → returns record id.
  - `CreateAAsync` 81057 → falls back to GET → returns existing id.
  - `DeleteAsync` 200 → no throw.
  - `DeleteAsync` 404 → no throw (idempotent).
  - `DeleteAsync` 500 → throws.
- `CloudflareDnsClientRetryTests`:
  - 429 once then 200 → 1 retry, returns id.
  - 500 thrice → final throw.
  - 429 thrice → final throw.

### Frontend manual smoke (no Playwright in scope)

1. `pnpm dev` against the Compose stack with `Cloudflare:UseStub=false` and a real DO token in the vault.
2. Sign in (Google OAuth bypass — already in place for dev per Pattern 1).
3. Set passphrase via `settings/security`.
4. Save real DO token via the new Provider credentials section. Step-up modal pops; enter passphrase.
5. Navigate to `/`; click "Create cloud".
6. Pick region `fra1`. Next.
7. Review screen shows cost breakdown + cold-start callout. Click Provision.
8. Either modal pops (if unlock TTL slid out) or 202 fires immediately.
9. Browser navigates to `/clouds/<uuid>`. Phase tracker shows `tf_planning → tf_applying` advancing in real time.
10. Switch to DO console; see the new droplet appear (`s-2vcpu-4gb` in `fra1`).
11. Phase tracker continues `dns_creating → awaiting_cloud_callback → cloud_registered → awaiting_cert → succeeded`. Total ~5–10 minutes.
12. Success card shows hostname `<random>.thany.click`; visit it; see Caddy serving the placeholder nginx with valid LE cert.
13. Hit `POST /api/clouds/{id}/destroy` (curl + cookies, or wait for PORTAL-012's UI). Watch the destroy saga walk and droplet disappear in DO.

## Acceptance criteria

- `dotnet build` succeeds with zero warnings.
- `dotnet test` passes (all new tests + existing tests still green).
- `pnpm check` (svelte-check) passes with zero errors and zero warnings.
- `pnpm build` produces the static SPA bundle; serving it via Portal.Api works (`MapFallbackToFile("index.html")` already in place from PORTAL-003).
- Migration applies cleanly on a fresh database and on a database with existing PORTAL-007 data; rolls back via `Down` without leaving orphan columns.
- Manual smoke (above): all 13 steps succeed against a real DO + Cloudflare environment.
- ADR-0036 lands at `docs/decisions/0036-wizard-progress-transport.md` with all 7 decisions.
- Sub-edits to PORTAL-008's `Provisioning:DefaultSize`, PORTAL-015's three stale lines, cloud-pivot-plan §22.5, ADR-0033's broken `[[0035]]` link, and PORTAL-007's broken link are all applied.
- `git grep -n "0035-wizard-progress-transport"` returns zero hits (the old link is fully replaced).
- `git grep -n "polls /api/clouds/\{id\}/status"` returns zero hits.

## Concrete steps in order

1. **Land ADR-0036.** Write `docs/decisions/0036-wizard-progress-transport.md`. Update ADR-0033's "Related" section. Fix PORTAL-007's stale link. This is pure docs; ~30 min.
2. **Land the migration.** Add `provisioning_jobs.enrollment_token` + `user_id` (if not present) + the partial index. Apply via `dotnet ef database update` on the local Compose stack; verify with `\d provisioning_jobs`.
3. **Extend `DigitalOceanRegions.cs`** with the `Catalog` + `RegionInfo` record. Land + test.
4. **Land `EnqueueGuard.CheckUserCreateInFlightAsync`** + tests.
5. **Land `HostnameGenerator`** + tests.
6. **Land `EnrollmentTokenGenerator`** (trivial).
7. **Land `POST /api/clouds`** + tests. Verify with curl + DB inspection that a `provisioning_jobs` row appears with the right shape.
8. **Land `GET /api/clouds/provider-meta/digitalocean`** + test.
9. **Land `GET /api/clouds/{id}`** + tests.
10. **Land `GET /api/clouds/{id}/events`** SSE. Test with curl: `curl -N -H "Cookie: ..." http://localhost:5000/api/clouds/{id}/events`. Use `psql` to manually `pg_notify('provisioning_job_changed', '<jobId>')` and observe an event delivered.
11. **Land PORTAL-016 callback** + tests including rate-limit tests.
12. **Land PORTAL-010b Cloudflare client** + WireMock tests. Replace `StubCloudflareDnsClient` registration in SagaWorker `Program.cs` with the toggle.
13. **Update PORTAL-008's `Provisioning:DefaultSize`** to `s-2vcpu-4gb`. Land.
14. **Update `TfPlanningHandler`** to read `EnrollmentToken` from the job row. Re-run PORTAL-007's existing tests; they should still pass with this read-from-column change.
15. **Land `app.css`** + `+layout.svelte` import. Migrate existing components' hardcoded colors. Visual diff: pages still look identical.
16. **Land `src/lib/types/`** + `http.ts` + `auth.ts` + `sse.ts` + `costs.ts`.
17. **Land `CostSummary.svelte`** + `ProviderTokenSection.svelte`.
18. **Update `+page.svelte`** with the "Create cloud" button.
19. **Land `/clouds/new/+page.svelte`** + `+page.ts`. Test manually: wizard renders, region picker populates from `/provider-meta`, Next → Review → Provision flow works (step-up fires).
20. **Land `/clouds/[id]/+page.svelte`** + `+page.ts`. Test with a real provisioning row: SSE delivers events, phase tracker updates.
21. **Update `settings/security/+page.svelte`** with the Provider credentials section.
22. **Manual end-to-end smoke** (steps 1–13 in the Tests section above).
23. **Apply remaining sub-edits**: PORTAL-015's three stale lines, cloud-pivot-plan §22.5.
24. **Final grep**: confirm `0035-wizard-progress-transport` and `polls /api/clouds/{id}/status` are both zero-hit.
25. **Verify all acceptance criteria.** `git status` should show only files in "Output of the bundle" + the small sub-edit files. Commit; open PR.

## Risks & gotchas

- **`provisioning_jobs.user_id` may not exist.** PORTAL-002 added the table; PORTAL-007 may have added `user_id` for its own queries. Grep first (`grep -n "user_id" src/ThanyMarcus.Portal.Api/Infrastructure/Database/Migrations/*.cs`). If it exists, drop the `DO $$ ... END $$` block from the migration; if not, keep it.
- **Test seeds that build `ProvisioningJob` POCOs directly must set `UserId`** once the migration lands. Production endpoints (`CreateCloudEndpoints.cs`, `DestroyCloudEndpoints.cs`) thread the value from claims, but several test helpers build the entity in-memory and previously didn't need a `UserId`. After applying the migration, the FK `fk_provisioning_jobs_users_user_id` causes `INSERT` to fail with `23503` until every seed is updated. Required sweep:
  ```
  grep -rn "new ProvisioningJob" tests/
  ```
  Confirmed sites to update (observed 2026-05-16 during pre-flight):
  - `tests/ThanyMarcus.Portal.Tests/SagaWorker/SagaTestSeed.cs` — the shared helper used by ~27 saga tests; add `UserId = user.Id`.
  - `tests/ThanyMarcus.Portal.Tests/Features/Provisioning/ProvisioningJobTests.cs` — 4 initializers across `Round_trip_*`, `Partial_index_*`, `Restricts_cloud_delete_*`; add `UserId = cloud.UserId`.
  - `tests/ThanyMarcus.Portal.Tests/Features/CloudManagement/Destroy/DestroyCloudEndpointTests.cs` — `Active_job_returns_409_cloud_busy`; add `UserId = user.Id`.
  - `tests/ThanyMarcus.Portal.Tests/SagaWorker/WorkspaceLayoutTests.cs` — in-memory POCO only, never `SaveChangesAsync`'d; safe to leave alone (verify before editing).
  Without these updates `dotnet test` shows ~32 failures all rooted in `SagaTestSeed.SeedAsync` SaveChangesAsync at the FK violation. The fix is one line per initializer; the diagnosis cost is much higher because the failure cascades across the saga handler test suite.
- **Hostname collision is astronomically unlikely** (1 in 4 billion). The 5-attempt cap throws `InvalidOperationException("hostname_generation_exhausted")` — verify the endpoint maps it to a 500 rather than leaking the exception. Add `Results.Problem` middleware path.
- **SSE through Caddy / Cloudflare:** Caddy passes SSE without buffering by default. If Cloudflare is added in front of the portal later (it isn't currently — DEC-003 says CF is for `thany.click`, not for `app.thany.click`), set `Cache-Control: no-transform` to disable CF's buffering. Not a concern today.
- **`fetchWithStepUp` collapses concurrent 401s.** If a user double-clicks Provision before disable kicks in (between click and `submitting = true`), both POSTs 401, both await the same modal, both retry after unlock. Result: two POSTs to `/api/clouds`. The per-user-in-flight guard catches the second with 409. Net effect: one cloud created, one inline error briefly shown then cleared. Acceptable.
- **Hostname uniqueness across soft-deletes.** `IgnoreQueryFilters` in the generator's `AnyAsync` is mandatory — otherwise a soft-deleted cloud's `Hostname` could be re-issued, and the unique index would 23505 on insert. Verified by `HostnameGeneratorTests`.
- **`EnrollmentToken` lookup during cloud-init.** Cloud-init's `register-with-portal.sh` POSTs ~5–10 minutes after the saga generated the token. The token lives on `provisioning_jobs.EnrollmentToken` for as long as the job exists (i.e., past `succeeded`). Old job rows are not pruned in MVP — they accumulate. **Acceptable for thesis scope; PORTAL-012 may add a sweep later.**
- **`CryptographicOperations.FixedTimeEquals`** requires same-length byte arrays. The `enrollment_token` is always 64 hex chars (32 bytes); if a malformed body arrives with a different length, `Encoding.UTF8.GetBytes` yields a different-length byte array and FixedTimeEquals throws. Wrap in `try/catch` or pre-check length.
- **Cloudflare 81057 fallback semantics.** If the existing record's `content` (IP) differs from what we want, we currently still return its id — and the saga proceeds with an A-record pointing at a stale IP. LE will fail. **For thesis scope, accept this** (the case requires a hard-deleted clouds row, which doesn't happen via normal flows). Defensive fix: DELETE the existing record and POST a new one. Add as a follow-up if observed.
- **TFC-state column rename risk.** If PORTAL-007 currently generates `enrollment_token` in `TfPlanningHandler` (rather than reading from the job row), step 14 is a behavior change, not a rename. Verify the existing implementation before editing. If the change is non-trivial, surface it; don't silently rewrite handler logic.
- **`NotifyAsync(args.Payload)` is called on Npgsql's connection thread.** Don't run long work in the handler — capture the payload, enqueue to a `Channel<T>` if needed. The skeleton above re-queries the DB inside the handler, which is fine because the work is short-lived.
- **SSE handler holds a Postgres connection.** For thesis-scale (1–3 concurrent SSE subscribers), connection pool of 20 is plenty. If we ever break 100 concurrent subscribers, multiplex the LISTEN onto a single dedicated background service that fans out to in-memory `Channel<T>`s per subscriber. Out of scope here.
- **`apiFetch` is currently a re-export of `fetchWithStepUp`.** That's intentional — the seam exists for future error-mapping changes. Don't optimize it away.
- **Design-token migration cosmetic risk.** When swapping hardcoded colors to `var(--color-*)`, easy to miss one. The pass is small (~10 files); manual visual check after migration: load `/`, `totp-challenge`, `settings/security` side-by-side pre/post and diff visually. No automated test catches this.
- **The "1 = auto" TTL in Cloudflare** is a documented magic value; verify against current CF docs at implementation time. If wrong, A records will have a 5-min TTL by default — fine for thesis but worth knowing.
- **Cloudflare ZoneId is non-secret** but is per-environment. Production and staging will use different zone ids (different test domains, e.g., `thany.click` vs `staging.thany.click`). Document the per-env `appsettings.{Environment}.json` override.
- **The `:closed` SSE comment doesn't fire if the response is already closed.** Make sure to write it *before* closing, not after. Order: write the terminal event → write `: closed` → flush → close.

## Definition of done

All acceptance criteria pass. `git status` shows only the files listed in "Output of the bundle" + the documented sub-edits. The manual smoke (13 steps) succeeds end-to-end against a real DO + Cloudflare + LE-prod environment, producing a `succeeded` cloud at `<random>.thany.click` reachable via HTTPS with a valid certificate, within 10 minutes of clicking Provision. Destroying via PORTAL-015's curl path against the same cloud drives the saga to `rolled_back` and removes the droplet.

A fresh agent picking up **PORTAL-009** (Azure terraform module) from this state knows:
- The wizard hardcodes `provider: "digitalocean"`; PORTAL-009 changes the wizard's target step to include a provider picker.
- `DigitalOceanRegions.Catalog` is the template; `AzureRegions.Catalog` mirrors it.
- The create endpoint already validates `provider`; expanding the allowlist to `["digitalocean", "azure"]` + a `AzureRegions.IsAllowed` check is the entire backend change.

A fresh agent picking up **PORTAL-012** (cloud-list dashboard) from this state knows:
- `GET /api/clouds` (list endpoint) doesn't exist yet — PORTAL-012 adds it.
- The list page replaces the "Create cloud" button on `/` with a full dashboard.
- Each row's "Destroy" button hits PORTAL-015's endpoint; "View" hits `/clouds/{id}`.
- The SSE channel from PORTAL-011 is reusable for destroy-progress modals.

A fresh agent picking up **PORTAL-013** (plugin-token issuance) from this state knows:
- The `/clouds/{id}` page is the obvious surface for "Issue plugin token" buttons.
- Tokens are issued via the cloud's `/admin/plugin-tokens` endpoint, called through PORTAL-014's admin proxy.
- The cloud_admin_token landed by PORTAL-016 is what authenticates the proxy.

## Cross-references

- **PORTAL-005** — prerequisite. `IProviderTokenVault` endpoints consumed by the SPA's provider-token section.
- **PORTAL-007** — prerequisite. Saga skeleton + handler chain.
- **PORTAL-007a** — prerequisite. SagaWorker container.
- **PORTAL-008** — prerequisite. DO terraform module + region allowlist.
- **PORTAL-010** — prerequisite. Cloud-init template + `register-with-portal.sh` (consumed by PORTAL-016).
- **PORTAL-015** — peer. Destroy flow; reuses the SSE channel; this bundle fixes its three stale `/status` references.
- **PORTAL-009** — successor. Azure module.
- **PORTAL-012** — successor. Cloud-list dashboard.
- **PORTAL-013** — successor. Plugin-token issuance.
- **PORTAL-014** — successor. Admin proxy.
- **ADR-0019** — LISTEN/NOTIFY pattern reused.
- **ADR-0020** — SSE chosen as user-facing transport.
- **ADR-0033** — saga state machine + events_log shape.
- **ADR-0034** — cloud↔portal handshake; PORTAL-016 is its consumer.
- **ADR-0035** — burst-worker tier; informs the cost-display copy.
- **ADR-0036** — this bundle's new ADR (wizard progress transport).
- **DEC-001** (revised 2026-05-16) — two-tier; control plane is `s-2vcpu-4gb`.
- **DEC-003** — platform-owned `thany.click` + Cloudflare DNS API.
- **DEC-005** — GHCR; passed via `ghcr_pat` from portal config.
