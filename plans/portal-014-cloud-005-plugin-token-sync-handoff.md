# PORTAL-014 + CLOUD-005 (plugin-tokens slice) — Plugin-Token Sync End-to-End — Handoff Brief

Date: 2026-05-18
Status: Draft (companion to `cloud-002-handoff.md` which landed the cloud-side `plugin_tokens` table + `RequirePluginAuthFilter`, and `portal-cancel-handoff.md` whose saga-discipline patterns are inherited here).

**Goal:** make `Authorization: Bearer <plugin-token>` against a freshly-provisioned user cloud actually work. Today the cloud-side `plugin_tokens` table is never populated by any code path — every `/api/ingest/*` and `/api/sync/*` call to a fresh cloud returns 401 forever. This handoff closes that gap by adding a new provisioning-saga phase that issues a plugin token, stores it on both sides, and surfaces the raw token to the user one time via the wizard's existing SSE channel. After this lands, the path **Obsidian plugin → cloud bearer-auth → ingest pipeline** is unblocked end-to-end.

Estimated **1 person-day** with AI-agent assistance. Tightly coupled receiver (cloud) + caller (portal) + saga phase + UI surface; ships as one combined handoff because the two halves are useless in isolation (a receiver with no caller, or a caller with no receiver, can't be integration-tested or smoked).

This handoff also resolves the long-open **PORTAL-016 `// FORK:` note** on cloud-admin-token storage (see Decision 12 in the grilling — recorded below in §"Design decisions").

## Where decisions live (read before doing anything)

- **Memory `portal_architecture.md`** — Minimal APIs + VSA; Postgres job queue (SKIP LOCKED + lease); mutable status (no event sourcing); SSE only. This handoff adds one phase to the existing provisioning saga; **do not invent a new pattern**.
- **Memory `composite_ingest_decision.md`** — cloud-side ingest endpoint receives bearer-token auth via `RequirePluginAuthFilter`. This handoff is what makes the bearer side work.
- **`plans/cloud-002-handoff.md`** — landed cloud-side `plugin_tokens` table + `RequirePluginAuthFilter` + `PluginTokenAuthenticator` (SHA-256 hash + `CryptographicOperations.FixedTimeEquals` against `bytea`). Out-of-scope #10 there explicitly punts the portal-cloud sync to this handoff.
- **`plans/portal-cancel-handoff.md`** — saga-phase discipline (terminal-status check, idempotent retry, destroy-allowed transitions). Same pattern applies here.
- **`docs/decisions/0019-background-work-and-saga-durability.md`** — Postgres job queue + SKIP LOCKED + lease + mutable status. Applies as-is to the new phase.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the provisioning saga's state machine; this handoff extends it with one phase + one terminal-failure state.
- **`docs/decisions/0025-openapi-and-hand-written-ts-contracts.md`** — endpoints get `.WithName(...)` + `.Produces<>` + `.ProducesProblem(...)`; TS types for SPA in `ThanyMarcus.Shared`.
- **`docs/decisions/0023-test-stack.md`** — xUnit v3 + Shouldly + Testcontainers + `PostgresFixture` + `Respawn`.
- **`plans/cloud-pivot-plan-2026-05-13.md` §28** — Q-AdminTokenStorage. This handoff **resolves** that question by writing ADR-0041 (see §"ADRs to author").
- **`plans/tickets-2026-05-13.md` PORTAL-014 + CLOUD-005** — this handoff is the implementation. CLOUD-005 in the ticket file becomes "partially complete" — settings/health/register-with-portal landed earlier, plugin-tokens lands here, audit-log defers to its own future slice.

**Do not invent a new auth scheme, do not add JWT signing on portal↔cloud, do not introduce mTLS.** All of those came up in grilling and were rejected as overkill. The decision was: DataProtection-encrypt the cloud-admin-token at rest in portal, decrypt-on-demand for outgoing `/admin/*` calls. Same shape as the existing encrypted provider tokens.

## Design decisions resolved in the 2026-05-18 grilling

Recorded here so the next reader does not re-litigate them.

1. **Single combined handoff** — receiver (CLOUD-005 slice) + caller (PORTAL-014) + saga phase + UI ship together. ~1 person-day total. Splitting would create a no-op middle state.
2. **CLOUD-005 scope is just `/admin/plugin-tokens`** — `/admin/audit-log` defers to its own slice when operator audit-log UI is built. Other CLOUD-005 endpoints (settings, health, register-with-portal) already shipped.
3. **Hash encoding fix folded in** — portal `plugin_token_metadata.token_hash` migrates from hex string `text` to raw 32-byte `bytea`. Cloud already uses `bytea`. Tiny portal migration; rows aren't load-bearing (no working plugin yet).
4. **Token auto-issued during new saga phase `IssuingPluginToken`** — not a separate user-triggered click. Runs between `AwaitingCert` and `Succeeded`. Raw token surfaces once via the wizard SSE channel; portal never persists raw.
5. **Failure handling: `FailedPluginToken` terminal non-destructive** — joins the destroy-allowed list (`Succeeded, AwaitingCert, FailedCert, FailedPluginToken`). Recovery is user-triggered retry via the new `POST /api/clouds/{id}/plugin-tokens/retry` endpoint, which resets attempts and re-fires the phase.
6. **Wire contract**: `POST /admin/plugin-tokens` with `Authorization: Bearer <cloud-admin-token>`, body `{ tokenHash: <base64>, label: "plugin" }`. Hash-only over the wire; raw token never crosses. UPSERT-on-(token_hash) idempotency.
7. **Rotation deferred** to PORTAL-012 era. The existing portal endpoint `POST /api/cloud-management/plugin-tokens` becomes the rotation path when the cloud-detail UI is built; until then, code-comment-flagged as "rotation-only".
8. **Wizard UI scope: token reveal block only** — copy button + non-functional `obsidian://thany-marcus-connect?...` deep-link button (functional once PLUGIN-001 registers the handler). The destroy/manage UI is task #22's responsibility; sequenced before or parallel to this handoff.
   - SSE event payload: `event: plugin_token_issued` carrying `{ rawToken, deepLink }`. Emitted exactly once on phase success.
9. **Destroy button gap → task #22 handoff** — drafted before or parallel to this one (decision deferred to draft-time). This handoff does not touch destroy UX.
10. **State machine + timeouts**: `AwaitingCert → IssuingPluginToken → Succeeded`. Config-driven: 10s HTTP timeout, 60s lease, 3 retry attempts, 2^n s backoff. Two new keys in `appsettings.json` (`Saga:PluginTokenSync:HttpTimeoutSeconds`, `Saga:PluginTokenSync:MaxAttempts`). **No hardcoded timeouts in C# source** — explicit guard against repeating task #17's cert-poll bug.
11. **Retry endpoint** `POST /api/clouds/{id}/plugin-tokens/retry`. Session auth + cloud-ownership check; **no TOTP elevation** (effect is "issue a new bearer token to the cloud you already own"; doesn't warrant friction). Crash-recovery uses the same code path — a saga that crashed mid-phase has `lease_expires_at < now`; the existing sweeper claims and re-runs with a fresh token, leaving a harmless orphan hash on the cloud.
12. **Portal cloud-admin-token storage**: DP-encrypted plaintext (`clouds.encrypted_cloud_admin_token bytea`). Drop the unused `clouds.cloud_admin_token_hash` column. **Previously-provisioned clouds** (smoke #5/#6, etc.) have no plaintext stored and must be destroyed + re-provisioned to gain a working admin path; flagged in §"Acceptance criteria".
13. **Test floor: 7 named tests required**; `PluginTokenSyncEndToEndTests` is the regression anchor and must run without a live cloud (Testcontainers + WebApplicationFactory).

## Scope boundary (precise)

Single pass, ~1 day. The slice is small enough to ship in one PR.

### 1. Cloud-side: `POST /admin/plugin-tokens` receiver

- **File**: `src/ThanyMarcus.Cloud.Api/Features/Admin/PluginTokens/AdminPluginTokenEndpoints.cs` (new).
- Endpoint: `MapPost("/admin/plugin-tokens", ...)` gated by **existing** `RequireCloudAdminTokenFilter` (from CLOUD-002 Settings feature; reuse, don't fork).
- Request:
  ```csharp
  public sealed record AdminIssuePluginTokenRequest(
      [property: JsonPropertyName("token_hash")] string TokenHashBase64,
      [property: JsonPropertyName("label")] string Label);
  ```
- Response:
  ```csharp
  public sealed record AdminIssuePluginTokenResponse(
      [property: JsonPropertyName("token_id")] Guid TokenId);
  ```
- Handler:
  ```
  1. Validate: TokenHashBase64 must decode to exactly 32 bytes; Label non-empty + ≤ 128 chars.
  2. SQL: INSERT INTO plugin_tokens (id, token_hash, label, created_at)
          VALUES (@id, @hashBytes, @label, now())
          ON CONFLICT (token_hash) DO UPDATE SET label = EXCLUDED.label
          RETURNING id;
     (UPSERT semantics — duplicate hash from saga retry is a no-op with same response.)
  3. Return 200 with TokenId.
  ```
- Annotations: `.WithName("PostAdminPluginTokens")` + `.Produces<AdminIssuePluginTokenResponse>(200)` + `.ProducesProblem(400)` + `.ProducesProblem(401)`.
- Wire-up: `app.MapAdminPluginTokenEndpoints()` in `Program.cs` adjacent to `MapAdminSettingsEndpoints()`.

### 2. Portal-side: cloud-admin-token storage migration

**Migration `AddEncryptedCloudAdminTokenAndDropHash.cs`** in `src/ThanyMarcus.Portal.Api/Infrastructure/Database/Migrations/`:

```csharp
protected override void Up(MigrationBuilder mb)
{
    mb.AddColumn<byte[]>(
        name: "encrypted_cloud_admin_token",
        table: "clouds",
        type: "bytea",
        nullable: true);

    mb.DropColumn(name: "cloud_admin_token_hash", table: "clouds");
}

protected override void Down(MigrationBuilder mb)
{
    mb.AddColumn<byte[]>(
        name: "cloud_admin_token_hash",
        table: "clouds",
        type: "bytea",
        nullable: true);
    mb.DropColumn(name: "encrypted_cloud_admin_token", table: "clouds");
}
```

**Entity update**: `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Cloud.cs`:
- Remove `public byte[]? CloudAdminTokenHash { get; set; }`
- Add `public byte[]? EncryptedCloudAdminToken { get; set; }`

**Configuration update**: `CloudConfiguration.cs`:
- Drop the `CloudAdminTokenHash` `.Property(...)` line.
- Add `.Property(c => c.EncryptedCloudAdminToken).IsRequired(false)`.

**Plaintext capture update**: `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Callback/CloudCallbackEndpoints.cs:71` becomes:
```csharp
var protector = dataProtectionProvider.CreateProtector("cloud-admin-token:v1");
cloud.EncryptedCloudAdminToken = protector.Protect(
    Encoding.UTF8.GetBytes(body.CloudAdminToken));
```
Inject `IDataProtectionProvider` into the endpoint handler (it's already registered globally per `Program.cs`).

### 3. Portal-side: `ICloudAdminTokenAccessor` decryption helper

- **File**: `src/ThanyMarcus.Portal.Api/Features/CloudManagement/ProviderTokens/CloudAdminTokenAccessor.cs` (new, sibling to `ProviderTokenVault`).
- Interface:
  ```csharp
  public interface ICloudAdminTokenAccessor
  {
      Task<string?> GetPlaintextAsync(Guid cloudId, CancellationToken ct);
  }
  ```
- Implementation:
  ```csharp
  public sealed class CloudAdminTokenAccessor : ICloudAdminTokenAccessor
  {
      private readonly PortalDbContext db;
      private readonly IDataProtectionProvider dpp;

      public async Task<string?> GetPlaintextAsync(Guid cloudId, CancellationToken ct)
      {
          var bytes = await db.Clouds
              .Where(c => c.Id == cloudId)
              .Select(c => c.EncryptedCloudAdminToken)
              .SingleOrDefaultAsync(ct);
          if (bytes is null) return null;
          var protector = dpp.CreateProtector("cloud-admin-token:v1");
          return Encoding.UTF8.GetString(protector.Unprotect(bytes));
      }
  }
  ```
- DI: scoped, registered in `Program.cs` and `Portal.SagaWorker/Program.cs` (both processes need it).

### 4. Saga: new `IssuingPluginTokenHandler`

- **File**: `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/IssuingPluginTokenHandler.cs` (new).
- Implements `ISagaPhaseHandler`. Phase: `SagaStatus.IssuingPluginToken`.
- DI dependencies:
  - `PortalDbContext`
  - `ICloudAdminTokenAccessor`
  - `IHttpClientFactory` (uses a named client `PluginTokenSyncClient` with config-driven timeout)
  - `IPortalToCloudPluginTokenClient` (thin wrapper around the HTTP call, for testability — fakes target this interface)
  - `IDataProtectionProvider` (only to hash, no encryption here)
  - `IPortalEventBus` or whatever the existing SSE channel is named — to emit the `plugin_token_issued` event
  - `IClock`
- Pseudocode:
  ```
  load cloud row by jobId.cloud_id
  if cloud.encrypted_cloud_admin_token is null:
      → Throw `MissingAdminTokenException("cloud has no admin token; must re-provision")`
        (handled by saga's terminal-failure path; flips to FailedPluginToken with this message)

  raw_plugin_token = "tm_" + RandomNumberGenerator.GetHexString(64).ToLowerInvariant()
  hash_bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw_plugin_token))  // 32 bytes

  admin_token_plaintext = await accessor.GetPlaintextAsync(cloud.Id, ct)
  if admin_token_plaintext is null: throw MissingAdminTokenException(...)

  cloud_url = $"https://{cloud.Hostname}/admin/plugin-tokens"
  await portalToCloud.PostAsync(cloud_url, admin_token_plaintext,
      hash_bytes, label: "plugin", ct)
  // → throws on 5xx / timeout / 401 / 400; retry policy in dispatcher handles it

  // Cloud accepted. Persist portal-side row.
  db.PluginTokenMetadata.Add(new PluginTokenMetadata {
      CloudId   = cloud.Id,
      Name      = "plugin",
      TokenHash = hash_bytes,           // raw 32 bytes (post-migration; D3)
      CreatedAt = clock.GetCurrentInstant(),
      UpdatedAt = clock.GetCurrentInstant(),
  })
  await db.SaveChangesAsync(ct)

  // Emit SSE event ONCE with raw token; transition to Succeeded.
  deep_link = $"obsidian://thany-marcus-connect?cloudUrl=https://{cloud.Hostname}&token={raw_plugin_token}"
  await eventBus.PublishProvisioningEventAsync(cloud.Id, new ProvisioningEvent {
      Type = "plugin_token_issued",
      Payload = JsonSerializer.Serialize(new { rawToken = raw_plugin_token, deepLink = deep_link }),
  }, ct)

  // Saga dispatcher flips status to Succeeded based on handler return.
  ```
- Idempotency note: a saga retry that succeeds after a previous attempt left an orphan row on the cloud will create a NEW raw token + NEW hash. The cloud UPSERT-on-hash means this is a new row, not a duplicate. The orphan row from the failed attempt stays (harmless; no one knows its raw). MVP debt; cleanup endpoint is post-MVP.
- `IPortalToCloudPluginTokenClient` interface:
  ```csharp
  public interface IPortalToCloudPluginTokenClient
  {
      Task PostAsync(
          string cloudUrl, string cloudAdminToken,
          byte[] tokenHashBytes, string label, CancellationToken ct);
  }
  ```
  Default impl uses `IHttpClientFactory.CreateClient("PluginTokenSyncClient")` with the config-driven timeout. Throws `PluginTokenSyncException` on non-2xx; saga dispatcher's existing retry policy handles it.

### 5. Saga state machine extension

- **File**: `src/ThanyMarcus.Portal.Api/Features/Provisioning/SagaStatus.cs`. Add:
  ```csharp
  IssuingPluginToken,
  FailedPluginToken,
  ```
- **Terminal set update** (file in same area): include `FailedPluginToken` in `Terminal` set.
- **Destroyable set update**: `src/ThanyMarcus.Portal.Api/Features/CloudManagement/Destroy/DestroyCloudEndpoints.cs` — add `SagaStatus.FailedPluginToken` to `DestroyableProvisioningStatuses`.
- **Phase wiring**: `AwaitingCertHandler` currently transitions to `Succeeded` on cert verification. Change to `IssuingPluginToken` instead. The `IssuingPluginTokenHandler` then transitions to `Succeeded`.
- **Dispatcher update**: `Portal.SagaWorker/Program.cs:91` add the new handler registration alongside the existing scoped registrations. `SagaPhaseDispatcher` routes by phase enum already — adding a case is a one-line change.

### 6. Retry endpoint

- **File**: `src/ThanyMarcus.Portal.Api/Features/CloudManagement/PluginTokens/RetryPluginTokenEndpoint.cs` (new).
- `MapPost("/api/clouds/{cloudId:guid}/plugin-tokens/retry", ...)` with session auth + cloud-ownership check (same pattern as other cloud-management endpoints).
- Handler:
  ```
  load cloud row by cloudId; assert owner = current user else 403
  if cloud.ProvisioningStatus != SagaStatus.FailedPluginToken: return 409
  load active provisioning_job for cloudId; assert it's in FailedPluginToken
  UPDATE provisioning_jobs SET
      status = 'issuing_plugin_token',
      attempts = 0,
      last_error = NULL,
      lease_owner = NULL,
      lease_expires_at = NULL,
      scheduled_at = now(),
      updated_at = now()
    WHERE id = @jobId
      AND status = 'failed_plugin_token'
      AND owner_user_id = @currentUserId;  // optimistic guard

  UPDATE clouds SET
      provisioning_status = 'issuing_plugin_token',
      updated_at = now()
    WHERE id = @cloudId AND provisioning_status = 'failed_plugin_token';

  pg_notify('provisioning_jobs_new', '')

  return 202 { jobId, status: "issuing_plugin_token" }
  ```
- Annotations: `.WithName("PostCloudPluginTokensRetry")` + `.Produces<RetryPluginTokenResponse>(202)` + `.ProducesProblem(404)` + `.ProducesProblem(409)` + `.ProducesProblem(403)`.

### 7. Wizard UI: token reveal block

- **File**: SvelteKit component for the provisioning wizard success view (Portal.Web). The task #22 handoff will define the surrounding layout; this handoff lands the **reveal block** as a self-contained component.
- Component: `+wizard/PluginTokenReveal.svelte` (or equivalent).
- Behavior:
  - Listens to the existing provisioning SSE stream.
  - When `event: plugin_token_issued` arrives, captures the payload (`rawToken`, `deepLink`) into local component state.
  - Renders:
    ```
    Plugin setup
    ┌─────────────────────────────────────────────────┐
    │ Token (shown once — copy now)                   │
    │ tm_a1b2c3...                              [Copy] │
    │                                                 │
    │ [Open in Obsidian]  ← obsidian://… deep link   │
    │                                                 │
    │ Lost it later? Rotate from your cloud's         │
    │ settings page (coming soon).                    │
    └─────────────────────────────────────────────────┘
    ```
  - If status is `FailedPluginToken`, renders a "Retry plugin setup" button calling `POST /api/clouds/{id}/plugin-tokens/retry`. After click, returns to "issuing…" state and listens for the next event.
- One-time-show discipline: the raw token lives in component state only. Page reload loses it; the user must retry to re-issue. This is the explicit security contract.

### 8. Hash encoding fix (Decision 3)

- **Migration `PluginTokenMetadataHashToBytea.cs`** in `Portal.Api/Infrastructure/Database/Migrations/`:
  ```csharp
  // Up
  mb.DropColumn("token_hash", "plugin_token_metadata");
  mb.AddColumn<byte[]>("token_hash", "plugin_token_metadata", type: "bytea", nullable: false);
  ```
- **Entity update**: `PluginTokenMetadata.cs` — change `public string TokenHash { get; set; }` to `public byte[] TokenHash { get; set; } = Array.Empty<byte>()`.
- **Configuration update**: `PluginTokenMetadataConfiguration.cs` — column type updated implicitly via property type.
- **Code update**: `PluginTokenEndpoints.cs:62`:
  ```csharp
  // Was:
  // var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
  // Becomes:
  var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
  ```
- **No backfill needed** — `plugin_token_metadata` rows are not load-bearing (no working plugin yet). Drop + re-add is safe.

## State machine

```text
Pending → TfPlanning → TfApplying → DnsCreating → AwaitingCloudCallback → AwaitingCert
       → IssuingPluginToken      ← NEW
       → Succeeded

Terminal failures:
  FailedTf, FailedDns, FailedCallback, FailedCert, FailedPluginToken (NEW),
  FailedDestroy, Cancelled, RolledBack

Phase transitions out of IssuingPluginToken:
  IssuingPluginToken ──[handler ok]──►          Succeeded                (terminal)
  IssuingPluginToken ──[handler throws; attempts < MaxAttempts]──► IssuingPluginToken
                                                  (with scheduled_at = now + 2^attempts s)
  IssuingPluginToken ──[handler throws; attempts >= MaxAttempts]──► FailedPluginToken (terminal)
  IssuingPluginToken ──[lease TTL expires]──►   IssuingPluginToken     (sweeper re-claims)

FailedPluginToken transitions:
  FailedPluginToken ──[user POST /retry]──►  IssuingPluginToken         (reset attempts + scheduled_at)
  FailedPluginToken ──[user POST /destroy]──► Destroying                (FailedPluginToken in destroyable set)
```

## Wire contract reference

### `POST /admin/plugin-tokens` (cloud-side, new)

```
POST /admin/plugin-tokens HTTP/1.1
Authorization: Bearer <cloud-admin-token>
Content-Type: application/json

{
  "token_hash": "<base64-encoded 32 bytes>",
  "label": "plugin"
}

→ 200
{
  "token_id": "<uuid>"
}
```

Errors: `400 invalid_hash_length`, `400 missing_label`, `401 invalid_admin_token`, `5xx db_error`.

### `POST /api/clouds/{cloudId}/plugin-tokens/retry` (portal-side, new)

```
POST /api/clouds/<uuid>/plugin-tokens/retry HTTP/1.1
Cookie: <session>

→ 202
{
  "jobId": "<uuid>",
  "status": "issuing_plugin_token"
}
```

Errors: `403 not_owner`, `404 cloud_not_found`, `409 not_in_failed_plugin_token`.

### SSE event payload (portal-side, on existing provisioning SSE channel)

```
event: plugin_token_issued
data: {"rawToken":"tm_a1b2c3...","deepLink":"obsidian://thany-marcus-connect?cloudUrl=https://...&token=tm_..."}
```

Emitted exactly once per saga run on transition to `Succeeded`. Subsequent reconnections to the same SSE channel do **not** replay this event — one-time-show is enforced by not persisting raw.

## File map

```
Thany-Marcus/
├── plans/
│   └── portal-014-cloud-005-plugin-token-sync-handoff.md   # THIS FILE
├── docs/decisions/
│   └── 0041-portal-cloud-admin-token-storage.md            # NEW (see §"ADRs to author")
├── src/ThanyMarcus.Cloud.Api/
│   ├── Program.cs                                           # CHANGED: + MapAdminPluginTokenEndpoints
│   └── Features/Admin/PluginTokens/                         # NEW directory
│       ├── AdminPluginTokenEndpoints.cs                     # MapPost + handler
│       ├── AdminIssuePluginTokenRequest.cs
│       └── AdminIssuePluginTokenResponse.cs
├── src/ThanyMarcus.Portal.Api/
│   ├── Program.cs                                           # CHANGED: + DI for ICloudAdminTokenAccessor;
│   │                                                        #          + MapRetryPluginTokenEndpoint
│   ├── Features/CloudManagement/
│   │   ├── Cloud.cs                                         # CHANGED: drop CloudAdminTokenHash; add EncryptedCloudAdminToken
│   │   ├── CloudConfiguration.cs                            # CHANGED: drop hash property; add encrypted property
│   │   ├── Callback/CloudCallbackEndpoints.cs               # CHANGED: encrypt + store plaintext on callback
│   │   ├── Destroy/DestroyCloudEndpoints.cs                 # CHANGED: + FailedPluginToken in destroyable set
│   │   ├── PluginTokens/
│   │   │   ├── PluginTokenEndpoints.cs                      # CHANGED: hash = raw bytes (drop hex conversion);
│   │   │   │                                                #          code-comment "rotation-only after PORTAL-012"
│   │   │   ├── PluginTokenMetadata.cs                       # CHANGED: TokenHash byte[] not string
│   │   │   ├── PluginTokenMetadataConfiguration.cs          # CHANGED: column type
│   │   │   └── RetryPluginTokenEndpoint.cs                  # NEW
│   │   └── ProviderTokens/
│   │       ├── ICloudAdminTokenAccessor.cs                  # NEW
│   │       └── CloudAdminTokenAccessor.cs                   # NEW
│   ├── Features/Provisioning/
│   │   └── SagaStatus.cs                                    # CHANGED: + IssuingPluginToken, + FailedPluginToken
│   └── Infrastructure/Database/Migrations/
│       ├── XXXX_AddEncryptedCloudAdminTokenAndDropHash.cs   # NEW (cloud_admin_token shape)
│       └── XXXX_PluginTokenMetadataHashToBytea.cs           # NEW (plugin token hash encoding)
├── src/ThanyMarcus.Portal.SagaWorker/
│   ├── Program.cs                                           # CHANGED: + DI for ICloudAdminTokenAccessor,
│   │                                                        #          + IPortalToCloudPluginTokenClient (named HttpClient),
│   │                                                        #          + IssuingPluginTokenHandler (scoped)
│   ├── appsettings.json                                     # CHANGED: + Saga:PluginTokenSync section
│   └── Features/Provisioning/Handlers/
│       ├── AwaitingCertHandler.cs                           # CHANGED: success transition target = IssuingPluginToken
│       │                                                    #          (was Succeeded)
│       └── IssuingPluginTokenHandler.cs                     # NEW
├── src/ThanyMarcus.Portal.SagaWorker/Infrastructure/Cloud/  # NEW directory (peer to Cloudflare/)
│   ├── IPortalToCloudPluginTokenClient.cs
│   └── PortalToCloudPluginTokenClient.cs                    # uses named HttpClient "PluginTokenSyncClient"
├── src/ThanyMarcus.Shared/
│   └── CloudAdmin/
│       └── AdminIssuePluginTokenRequest.cs                  # NEW (shared DTO, per ADR-0025)
├── src/Portal.Web/                                          # SvelteKit SPA
│   └── (component path under +wizard/)
│       ├── PluginTokenReveal.svelte                         # NEW
│       └── (existing success-view component updated to slot in PluginTokenReveal)
└── tests/
    ├── ThanyMarcus.Portal.Tests/
    │   ├── Features/CloudManagement/
    │   │   ├── Callback/CloudCallbackEndpointsTests.cs     # UPDATED: assert encrypted column populated
    │   │   ├── PluginTokens/RetryPluginTokenEndpointTests.cs  # NEW
    │   │   └── ProviderTokens/CloudAdminTokenAccessorTests.cs # NEW (DP encrypt/decrypt round-trip)
    │   ├── Migrations/PortalEncryptedTokenMigrationTests.cs   # NEW
    │   └── PluginTokenSyncEndToEndTests.cs                  # NEW — the regression anchor
    ├── ThanyMarcus.Portal.SagaWorker.Tests/
    │   └── Features/Provisioning/Handlers/
    │       └── IssuingPluginTokenHandlerTests.cs            # NEW (against fake IPortalToCloudPluginTokenClient)
    └── ThanyMarcus.Cloud.Tests/
        └── Features/Admin/PluginTokens/
            └── AdminPluginTokenEndpointsTests.cs            # NEW
```

## Configuration changes

`src/ThanyMarcus.Portal.SagaWorker/appsettings.json` — add section:
```json
{
  "Saga": {
    "PluginTokenSync": {
      "HttpTimeoutSeconds": 10,
      "MaxAttempts": 3
    }
  }
}
```

Override via env: `Saga__PluginTokenSync__HttpTimeoutSeconds`, `Saga__PluginTokenSync__MaxAttempts`. The handler reads these via `IOptionsMonitor<PluginTokenSyncOptions>` (a small POCO) so changes propagate without redeploy.

## ADRs to author

**ADR-0041: Portal-side cloud-admin-token storage** (~0.1 d batch with this handoff). Captures:
- Decision: store DP-encrypted plaintext in `clouds.encrypted_cloud_admin_token`. Drop hash column.
- Context: cloud-pivot-plan §28 Q-AdminTokenStorage. PORTAL-016's `// FORK:` note.
- Alternatives considered (rejected): keep hash, hash + encrypted, JWT, mTLS. Reasons each rejected (per Decision 12 grilling).
- Consequence: portal can call cloud `/admin/*` endpoints. Previously-provisioned clouds (without the column populated) require re-provisioning to gain a working admin path. This is acceptable because MVP-1 has not yet relied on portal→cloud admin calls.

Author after the migration code lands; reference from this handoff.

## Test coverage (7 required, 1 optional)

1. **`IssuingPluginTokenHandlerTests`** — unit. Fake `IPortalToCloudPluginTokenClient` (returns 200 / 5xx / timeout / 401). Covers: happy path; retry on 5xx; retry on timeout; immediate dead-letter on 401 (admin token is wrong — not retryable); attempts=MaxAttempts then `FailedPluginToken`; idempotent UPSERT on retry (fake gets same hash twice); SSE event emitted exactly once on success; missing `encrypted_cloud_admin_token` flips immediately to `FailedPluginToken` with `last_error="missing_admin_token"`.
2. **`AdminPluginTokenEndpointsTests`** — endpoint. 200 happy; 200 idempotent on duplicate hash (UPSERT no-op); 400 hash wrong length; 400 missing label; 401 wrong admin token; 401 no auth header.
3. **`CloudAdminTokenAccessorTests`** — unit. DP encrypt → DB store → DB read → DP decrypt round trip; null when row has no encrypted column; throws on tampered ciphertext.
4. **`CloudCallbackEndpointsTests`** (update existing) — assertion update: `EncryptedCloudAdminToken` is non-null after callback; `cloud_admin_token_hash` column does not exist; DP-unprotect of the stored value equals the original token.
5. **`PortalEncryptedTokenMigrationTests`** — migration. Fresh DB applies cleanly; pre-existing DB with hashed rows (legacy clouds) applies, `encrypted_cloud_admin_token = NULL`, hash column gone; rollback restores hash column (sanity).
6. **`RetryPluginTokenEndpointTests`** — endpoint. 202 happy (status flips, job re-queued, pg_notify fires); 409 when status is not `FailedPluginToken`; 404 wrong cloud id; 403 wrong owner; idempotent (second retry while already `IssuingPluginToken` returns 409 or 202 noop — pick 409 for clarity).
7. **`PluginTokenSyncEndToEndTests`** — **the regression anchor**. Stands up: portal Postgres + cloud Postgres via Testcontainers; Portal.Api `WebApplicationFactory<Program>`; Cloud.Api `WebApplicationFactory<Program>`; portal SagaWorker as `IHostedService` in the same process for test purposes (or shipped as `BackgroundService` started by the factory). Mock terraform + DNS + cert phases (jump straight to `IssuingPluginToken`). Verify:
   - Phase runs to completion within 5s
   - Cloud's `plugin_tokens` row exists with matching hash
   - Portal's `plugin_token_metadata` row exists with matching hash
   - SSE event delivered to a test subscriber with raw token
   - Raw token authenticates against cloud's `RequirePluginAuthFilter` on a real gated endpoint (`GET /api/sync/pull` with empty result)

**Optional**: live trait `[Trait("Category","Live")]` exercising the same flow against a real provisioning saga running over a real Testcontainers network with two real .NET processes. Skipped on PRs; can run nightly. Not required for green.

## Out of scope (named explicitly)

1. ❌ **Rotation UI**. The cloud-detail page that hosts rotation doesn't exist yet (PORTAL-012). The existing `POST /api/cloud-management/plugin-tokens` endpoint stays in place, code-comment-flagged as "rotation-only after PORTAL-012". This handoff does **not** delete it.
2. ❌ **Orphan-token cleanup endpoint** on the cloud. After saga retries / crashes, the cloud may accumulate `plugin_tokens` rows whose raw token is unknown. Harmless (cannot authenticate); cleanup is post-MVP.
3. ❌ **`/admin/audit-log` endpoint** (CLOUD-005 row also covers this). Defers to the operator audit-log slice.
4. ❌ **`/admin/register-with-portal` endpoint** (CLOUD-005 row also covers this). The bootstrap callback already does this implicitly; no separate endpoint needed in MVP.
5. ❌ **Cloud admin proxy for arbitrary `/admin/*` calls** (the broader PORTAL-014 ticket framing). This handoff is the **first** portal→cloud `/admin/*` call; the patterns and infrastructure (`ICloudAdminTokenAccessor`, named HttpClient, DataProtection roundtrip) are reusable for future calls, but no generic proxy ships here.
6. ❌ **Wizard destroy/manage button**. Task #22 owns this; sequenced before or parallel to this handoff. The PluginTokenReveal component is self-contained and slots into whatever success view task #22 lands.
7. ❌ **Obsidian plugin `obsidian://thany-marcus-connect` handler**. PLUGIN-001 owns this. The deep-link button on the wizard reveal block emits the URL regardless; if no plugin is installed, the OS shows a "no handler" prompt or nothing. Add a tooltip "Requires Thany-Marcus Obsidian plugin (coming soon)" to set expectations.
8. ❌ **`OpenTelemetry` spans for the new saga phase**. The existing OTel wiring captures phase transitions at the dispatcher level; explicit per-phase spans + attributes (`tokens_synced`, `cloud_url`, etc.) is polish for a later instrumentation pass.

## Open contract decisions (carry forward)

1. **SSE event versioning** — `event: plugin_token_issued` lands here as v1. Future schema changes use `event: plugin_token_issued_v2` (and the wizard handles both with version detection). Not formalized as an ADR yet; document in the component's code comment when needed.
2. **Token format** — `tm_<64 hex chars>` continues from the existing `PluginTokenEndpoints.cs` issuance pattern. Not formally specified anywhere; if the plugin needs to validate format, it does so against this regex. No need to formalize until the plugin needs it.
3. **DP key rotation** — DataProtection key ring is on local disk (`DataProtection:KeyRingPath` per memory `portal_tooling.md`); keys rotate on the standard 90-day schedule. If a key expires while a cloud's `encrypted_cloud_admin_token` is still in the old key, `Unprotect` falls back to the key ring's archived keys. **Verify the key ring is configured to retain old keys**; if not, configure it. This applies to the existing encrypted provider tokens too — not new debt from this handoff.

## Acceptance criteria

1. ✅ `dotnet build` clean with warnings-as-errors across all touched projects.
2. ✅ `dotnet test` green; **all 7 named tests included and passing.** Live trait optional, skipped without explicit env var.
3. ✅ Both migrations apply cleanly on:
   - A fresh DB (clouds table empty)
   - A DB with legacy clouds (rows have `cloud_admin_token_hash`, no `encrypted_cloud_admin_token`) — after migration, hash column is gone, encrypted column is null on these rows
   - Rollback restores the prior shape
4. ✅ **Smoke #7** on real DO infrastructure:
   ```bash
   # 1. Re-provision a smoke cloud (any prior smoke cloud lacks encrypted_cloud_admin_token; destroy + re-provision)
   #    Use the existing wizard; click through provisioning.
   #    Verify in browser devtools / SSE log: event plugin_token_issued arrives.

   # 2. Copy the revealed raw token from the wizard.
   PLUGIN_TOKEN="tm_<copied>"

   # 3. Hit a gated cloud endpoint with the token
   curl -sS https://<cloud>.thany.click/api/sync/pull \
     -H "Authorization: Bearer $PLUGIN_TOKEN"
   # → 200 {"items":[],"nextSince":"..."}

   # 4. Hit the same endpoint with a wrong token
   curl -sS https://<cloud>.thany.click/api/sync/pull \
     -H "Authorization: Bearer tm_wrong"
   # → 401

   # 5. Cancel the SSE listener; reload the wizard.
   #    Verify: token NOT shown again (one-time-show enforced).

   # 6. From the cloud-management view (or DB), induce a FailedPluginToken state
   #    on a different cloud (e.g. temporarily firewall the cloud's admin endpoint),
   #    then re-provision. After timeout, click "Retry plugin setup".
   # → Wizard re-issues, new event arrives, new token works.
   ```
5. ✅ **PluginTokenSyncEndToEndTests.cs** is green without any live cloud, exercising portal Postgres + cloud Postgres + both Web factories.
6. ✅ ADR-0041 authored and committed.
7. ✅ No `// TODO` markers in shipped code paths. `// FORK:` markers explicitly named at:
   - The existing `POST /api/cloud-management/plugin-tokens` endpoint (forks to: PORTAL-012 will make this the rotation path with TOTP gate)
   - The PluginTokenReveal Svelte component (forks to: task #22's success-view layout owns the surrounding chrome)
8. ✅ The CLOUD-002 `// FORK:` marker on the dev-seeded plugin-token path is **removed** — this handoff replaces dev-seed with the real saga-issued path. Any CLOUD-002 tests that depended on the dev seed must switch to either (a) using the saga to issue, or (b) hand-rolling a hash into the cloud DB for that test's scope.

## What the next ticket inherits

After this handoff lands:
- The portal can call any cloud `/admin/*` endpoint via the DP-encrypted-plaintext pattern. **PORTAL-014's broader admin-proxy work** (when it lands as its own ticket — settings updates, audit-log fetch, model switching) reuses `ICloudAdminTokenAccessor` + the named HttpClient pattern without new infrastructure.
- The saga has a fourth model for a "user-recoverable" terminal failure (`FailedPluginToken`), alongside the existing `FailedCert`. Future similar phases (e.g. burst-worker provisioning failure) inherit the same retry-endpoint pattern.
- The SSE event channel now carries arbitrary typed events alongside phase transitions. Future "interesting events to surface to the UI without polling" (e.g. `burst_worker_started`, `model_install_complete`) follow this shape.
- The CLOUD-002 plugin-token-seeding workaround can be retired; CLOUD-002 tests that needed a seeded token are updated to either use the saga or hand-roll a hash for the test's scope.

The thesis-demo unblocked path becomes:

```
PORTAL-014+CLOUD-005 (this handoff) ✓
    │
    └─► PLUGIN-001 (Obsidian plugin scaffold + obsidian://thany-marcus-connect handler)
          │
          └─► PLUGIN-002/003 (capture commands + composite-ingest call to /api/ingest)
                │
                └─► [M5 demo: URL+text note in Obsidian → cloud → vault]
```
