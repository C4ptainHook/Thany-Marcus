# PORTAL-017 — DigitalOcean OAuth onboarding + auto-mint Spaces credentials

**Goal:** replace the "user pastes a DigitalOcean PAT" path with **OAuth 2.0 authorization-code flow** + **server-side auto-mint of Spaces credentials**. The user clicks one button; the portal mints everything Terraform needs. Azure stays paste-a-service-principal — its credential surface is untouched.

Estimated **1.5 person-days** with AI-agent assistance. Net change: removes the DO branch of `ProviderTokenSection.svelte`, narrows `IProviderTokenVault`'s DO usage to a thin compatibility shim during migration, and adds OAuth callback + secret-bundle + mint-step.

Tracks the 2026-05-19 amendment at end of §23 in `plans/cloud-pivot-plan-2026-05-13.md` and the new ADR-0039.

## Why this exists

Two real defects in the current PAT-paste flow:

1. **UX placement is wrong.** The DO token sits at user level (`/api/provider-tokens`, surfaced from `Settings`) rather than under the cloud being created. Most users have never created a DO PAT; expecting them to navigate DO's console, generate a token with "Full Access" scope, and paste a `dop_v1_…` string into a separate "Settings → Provider tokens" page before they can start the cloud-creation wizard is friction that no consumer SaaS imposes.

2. **A single PAT does not actually unblock provisioning.** Terraform's `digitalocean` provider needs `DIGITALOCEAN_TOKEN` for droplet/network/DNS resources **and** separate `SPACES_ACCESS_KEY_ID` + `SPACES_SECRET_ACCESS_KEY` for the Spaces bucket (object store for asset blobs). The current `TfApplyingHandler.cs:62` reads only the PAT — running `terraform apply` against a DO module that declares a `digitalocean_spaces_bucket` resource fails with `403 InvalidAccessKeyId`. Discovered live during a 2026-05-19 apply attempt against the user-cloud DO module.

Fix: **OAuth for the user-facing auth (one click, no paste)**, then have the server mint the Spaces key via `POST /v2/spaces/keys` using the OAuth access token before Terraform runs.

## What stays vs goes

### Stays
- **Azure provider-token flow.** Pasted service principals + Argon2id-DEK-wrapped at rest is unchanged. `ProviderTokenSection.svelte` keeps an Azure branch; `IProviderTokenVault.AddAsync(userId, "azure", …)` keeps working as today.
- **DEK envelope.** Per-user DEK unwrapped via step-up passphrase at session start; saga inherits unwrapped DEK transiently. OAuth tokens at rest are AES-GCM-wrapped with the same DEK — the "portal breach yields no usable provider access" property is preserved.
- **Saga state machine** — `Pending → TfPlanning → TfApplying → DnsCreating → AwaitingCloudCallback → AwaitingCert → IssuingPluginToken → Succeeded`. We **insert one new state** between `Pending` and `TfPlanning` (see below); we do not reorder existing states.
- **`SagaTimeouts`, `SagaPhaseDispatcher`, `LiveLogBus`, SSE provisioning events** — unchanged.
- **Portal callback contract from cloud → portal** — unchanged (ADR-0034 untouched).
- **Cloud destruction terraform path** — same; only adds two API calls before `terraform destroy` (revoke Spaces key + revoke OAuth token).

### Goes
- **`ProviderTokenSection.svelte` DO branch** — the textbox that accepts a `dop_v1_…` string. Replaced by a "Connect DigitalOcean" button that initiates OAuth.
- **DO-specific paths in `ProviderTokenEndpoints.cs`** — `POST /api/provider-tokens` accepts `{provider: "do", token: "..."}` today; that DO request body is rejected after migration with `400 use_oauth_flow`. Endpoint stays for Azure.
- **Pre-existing DO provider-token rows in `encrypted_provider_tokens`.** Migration leaves the table; runtime DO reads route through the new `cloud_secrets` bundle. A one-shot data-migration script copies any *existing* DO rows into `cloud_secrets` under kind `do_oauth_access` *only if* the row was created post-migration; pre-existing PAT-format tokens are flagged `needs_reauth` since they aren't OAuth tokens and cannot be refreshed.
- **`Cloud.ProviderTokenId` FK** — deprecated for DO; nullable, ignored on DO read paths. (Kept for Azure.) Drop after Azure is also migrated to the bundle, which is out of scope here.

### Comes in

**Backend (Portal.Api):**
- `Features/Auth/DigitalOcean/` — `DigitalOceanOAuthEndpoints.cs` (`GET /oauth/digitalocean/start`, `GET /oauth/digitalocean/callback`), `DigitalOceanOAuthClient.cs` (typed `HttpClient` to `cloud.digitalocean.com/v1/oauth/*` and `api.digitalocean.com/v2/spaces/keys`), `DigitalOceanTokens.cs` (DTOs).
- `Features/CloudManagement/Secrets/` — `CloudSecret.cs` (entity), `CloudSecretConfiguration.cs`, `ICloudSecretBundle.cs` + `CloudSecretBundle.cs` (read/write helpers keyed on `(cloud_id, kind)`).
- `Features/Auth/Login/RefreshOnLogin.cs` — middleware/filter that, after a successful login, walks the user's clouds and refreshes any DO OAuth token within 5 days of expiry.
- New table `cloud_secrets`; migration adds it.

**Saga worker (Portal.SagaWorker):**
- `Features/Provisioning/Handlers/MintingSpacesHandler.cs` — handles the new `MintingSpaces` state; calls `POST /v2/spaces/keys` via `DigitalOceanOAuthClient`, stores `(access_key_id, secret_key)` in the bundle.
- `TfApplyingHandler.cs` updated: for DO clouds, read `(oauth_access, spaces_access_id, spaces_secret)` from the bundle and pass all three as env vars to the Terraform subprocess.
- `DestroyEntryHandler.cs` updated: for DO clouds, call `DELETE /v2/spaces/keys/{id}` + OAuth revoke before `terraform destroy`.

**Frontend (Portal.Web SvelteKit):**
- `routes/clouds/new/+page.svelte` — when user picks DO, show a single "Connect DigitalOcean" button instead of advancing to the (now-removed) provider-token paste step.
- `routes/oauth/digitalocean/callback/+page.svelte` — minimal landing page that posts the `code` + `state` to the API and redirects into the provisioning wizard.
- `lib/ProviderTokenSection.svelte` — DO branch deleted; Azure branch retained.

**Saga state:**
- Add `SagaStatus.MintingSpaces = "minting_spaces"`.
- Add `Cloud.MintingSpacesStartedAt` timestamp column.
- Add `FailedMintingSpaces` terminal.

**Schema:**

```sql
CREATE TABLE cloud_secrets (
    id           UUID PRIMARY KEY,
    cloud_id     UUID NOT NULL REFERENCES clouds(id) ON DELETE CASCADE,
    kind         TEXT NOT NULL,                  -- 'do_oauth_access' | 'do_oauth_refresh'
                                                 -- 'do_spaces_access_id' | 'do_spaces_secret'
                                                 -- 'azure_sp_client_secret' (future)
    ciphertext   BYTEA NOT NULL,
    nonce        BYTEA NOT NULL,
    tag          BYTEA NOT NULL,
    expires_at   TIMESTAMPTZ NULL,               -- populated for oauth_access only
    created_at   TIMESTAMPTZ NOT NULL,
    updated_at   TIMESTAMPTZ NOT NULL,
    UNIQUE (cloud_id, kind)
);
CREATE INDEX ix_cloud_secrets_cloud_kind ON cloud_secrets (cloud_id, kind);

ALTER TABLE clouds
    ADD COLUMN minting_spaces_started_at TIMESTAMPTZ NULL,
    ADD COLUMN connection_status TEXT NOT NULL DEFAULT 'connected';
                                                 -- 'connected' | 'needs_reauth'
```

`encrypted_provider_tokens` table untouched.

## Architecture diagram (post-implementation, DO path)

```
                ┌─────────────── Portal.Web ───────────────┐
                │                                          │
                │  clouds/new                              │
   user click ──┤  "Connect DigitalOcean"   ──── GET /oauth/digitalocean/start
                │                                          │      │
                │                                          │      ▼
                └──────────────────────────────────────────┘  Portal.Api
                                                                  │
                          ┌──── redirect 302 ──── /v1/oauth/authorize ◄────┘
                          │                       (DO consent screen)
                          ▼
   ┌── DO ──────────────────────────────────────┐
   │ user reviews "thany wants read+write"      │
   │ user clicks "Authorize"                    │
   └─── redirect 302 ────► /oauth/digitalocean/callback?code=…&state=…
                                                                  │
                                              ┌── token exchange ─┤
                                              ▼                   │
                                       POST /v1/oauth/token       │
                                              │                   │
                                       {access, refresh}          │
                                              │                   │
                                       wrap with DEK              │
                                              │                   │
                                       cloud_secrets ◄────────────┘
                                              │
                                       seed Cloud row + enqueue
                                              ▼
                                       SAGA: Pending → MintingSpaces
                                              │
                                       POST /v2/spaces/keys (Bearer = oauth_access)
                                              │
                                       {access_key_id, secret_key} → cloud_secrets
                                              │
                                              ▼
                                       SAGA: MintingSpaces → TfPlanning
                                              │
                                       terraform apply with:
                                         DIGITALOCEAN_TOKEN={oauth_access}
                                         SPACES_ACCESS_KEY_ID={mint.access_key_id}
                                         SPACES_SECRET_ACCESS_KEY={mint.secret_key}
                                              │
                                              ▼
                                       …existing saga continues unchanged
```

## Concrete diffs by component

### 1. Portal.Api — OAuth endpoints

New folder `src/ThanyMarcus.Portal.Api/Features/Auth/DigitalOcean/`.

**`DigitalOceanOAuthEndpoints.cs`**

```csharp
// GET /oauth/digitalocean/start
//   - require authenticated user + verified TOTP claim
//   - generate CSRF state token, store in session: HttpContext.Session.SetString("do_oauth_state", state)
//   - 302 to https://cloud.digitalocean.com/v1/oauth/authorize
//        ?response_type=code
//        &client_id={DO_OAUTH_CLIENT_ID}
//        &redirect_uri={portal_base}/oauth/digitalocean/callback
//        &scope=read%20write
//        &state={state}

// GET /oauth/digitalocean/callback
//   - read `code` + `state` from query
//   - verify state matches session value, single-use (delete on read)
//   - require step-up unlock (RequireInfraOpUnlockFilter) — DEK must be warm
//   - POST cloud.digitalocean.com/v1/oauth/token with grant_type=authorization_code
//   - on success:
//       create Cloud row in 'pending' state, Provider="do"
//       wrap {access_token, refresh_token} with DEK, INSERT into cloud_secrets
//       enqueue provisioning job (MintingSpaces is the entry phase for DO clouds)
//   - 302 to /clouds/{cloud_id}/provisioning
//   - on token exchange failure: 302 to /clouds/new?error=oauth_failed
```

Use `IHttpClientFactory` for the typed `DigitalOceanOAuthClient`. Polly retry (3 attempts, exponential 200ms→1s→5s) on 5xx from DO token endpoint. No retry on 4xx (user revoked / state mismatch / etc. — surface to user).

**Env vars** (add to `appsettings.json` schema + `Program.cs` options binding):

```
DigitalOcean__OAuth__ClientId
DigitalOcean__OAuth__ClientSecret
DigitalOcean__OAuth__CallbackUrl    # e.g. https://portal.thany.click/oauth/digitalocean/callback
```

Document in `.env.example` with placeholder values + a comment pointing at `https://cloud.digitalocean.com/account/api/applications` for registering the OAuth app.

### 2. Portal.Api — Secret bundle

**`CloudSecret.cs`**

```csharp
public sealed class CloudSecret : IHasUpdatedAt {
    public Guid Id           { get; init; } = Guid.CreateVersion7();
    public Guid CloudId      { get; init; }
    public string Kind       { get; init; } = null!;
    public byte[] Ciphertext { get; set; }   = null!;
    public byte[] Nonce      { get; set; }   = null!;
    public byte[] Tag        { get; set; }   = null!;
    public Instant? ExpiresAt { get; set; }
    public Instant CreatedAt { get; init; }
    public Instant UpdatedAt { get; set; }
}

public static class CloudSecretKind {
    public const string DoOAuthAccess     = "do_oauth_access";
    public const string DoOAuthRefresh    = "do_oauth_refresh";
    public const string DoSpacesAccessId  = "do_spaces_access_id";
    public const string DoSpacesSecret    = "do_spaces_secret";
}
```

**`ICloudSecretBundle.cs`**

```csharp
public interface ICloudSecretBundle {
    Task PutAsync(Guid cloudId, string kind, string plaintext, ReadOnlyMemory<byte> dek,
                  Instant? expiresAt, CancellationToken ct);
    Task<string> GetAsync(Guid cloudId, string kind, ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task<string?> TryGetAsync(Guid cloudId, string kind, ReadOnlyMemory<byte> dek, CancellationToken ct);
    Task<Instant?> GetExpiresAtAsync(Guid cloudId, string kind, CancellationToken ct);
    Task DeleteAsync(Guid cloudId, string kind, CancellationToken ct);
    Task DeleteAllForCloudAsync(Guid cloudId, CancellationToken ct);
}
```

`CloudSecretBundle.cs` mirrors `ProviderTokenVault.cs:1-40` — same AES-GCM(plaintext, dek) shape, same `NonceSize=12 / TagSize=16`. Reuse the encryption helper if practical or inline it.

Register in DI in both `Portal.Api/Program.cs` and `Portal.SagaWorker/Program.cs`. Shared `PortalDbContext` already has `Database` registered — add `DbSet<CloudSecret> CloudSecrets`.

### 3. Portal.SagaWorker — `MintingSpacesHandler`

New file `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/MintingSpacesHandler.cs`.

```csharp
public sealed class MintingSpacesHandler(
    PortalDbContext db,
    ICloudSecretBundle secrets,
    IDekUnwrapAccessor dek,
    IDigitalOceanOAuthClient doClient,
    IClock clock,
    ILogger<MintingSpacesHandler> log) : ISagaPhaseHandler {

    public string Phase => SagaStatus.MintingSpaces;

    public async Task HandleAsync(ProvisioningJob job, CancellationToken ct) {
        var cloud = await db.Clouds.SingleAsync(c => c.Id == job.CloudId, ct);
        if (cloud.Provider != "do") {
            // Azure path skips this step; saga transitions straight to TfPlanning.
            await Transitions.SetStatusAsync(db, cloud, SagaStatus.TfPlanning, clock, ct);
            return;
        }
        var dekBytes = dek.RequireUnwrappedFor(cloud.UserId);
        var accessToken = await secrets.GetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, dekBytes, ct);
        var name = $"thany-cloud-{cloud.Id:N}";
        var mint = await doClient.MintSpacesFullAccessKeyAsync(accessToken, name, ct);
        // mint = { AccessKeyId, SecretKey }
        await secrets.PutAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, mint.AccessKeyId, dekBytes, null, ct);
        await secrets.PutAsync(cloud.Id, CloudSecretKind.DoSpacesSecret,   mint.SecretKey,   dekBytes, null, ct);
        await Transitions.SetStatusAsync(db, cloud, SagaStatus.TfPlanning, clock, ct);
    }
}
```

Register in `SagaPhaseDispatcher` next to the other handlers. On DO failure, transition to terminal `SagaStatus.FailedMintingSpaces` (add to `SagaStatus.cs` + `Terminal` list).

### 4. Portal.SagaWorker — `TfApplyingHandler` env vars

Edit `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/Handlers/TfApplyingHandler.cs:62` block:

```csharp
// before:
providerToken = await providerVault.DecryptAsync(cloud.UserId, cloud.Provider, dek, ct);

// after:
if (cloud.Provider == "do") {
    var accessToken    = await secrets.GetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,    dek, ct);
    var spacesId       = await secrets.GetAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, dek, ct);
    var spacesSecret   = await secrets.GetAsync(cloud.Id, CloudSecretKind.DoSpacesSecret,   dek, ct);
    tfEnv["DIGITALOCEAN_TOKEN"]         = accessToken;
    tfEnv["SPACES_ACCESS_KEY_ID"]       = spacesId;
    tfEnv["SPACES_SECRET_ACCESS_KEY"]   = spacesSecret;
} else if (cloud.Provider == "azure") {
    // existing Azure path: still reads from providerVault
}
```

Same pattern in `RollingBackTfHandler.cs:52` and `CancelHandler.cs:120` — they decrypt the provider token to run `terraform destroy`; mirror the DO branch.

### 5. Portal.SagaWorker — `DestroyEntryHandler` revocation

Before the existing `terraform destroy` call, for DO clouds:

```csharp
if (cloud.Provider == "do") {
    var spacesId    = await secrets.TryGetAsync(cloud.Id, CloudSecretKind.DoSpacesAccessId, dek, ct);
    var accessToken = await secrets.TryGetAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,    dek, ct);
    if (spacesId is not null && accessToken is not null)
        await doClient.DeleteSpacesKeyAsync(accessToken, spacesId, ct);   // 204 or 404; both fine
    if (accessToken is not null)
        await doClient.RevokeOAuthTokenAsync(accessToken, ct);             // best-effort; log+continue on failure
}
```

Both calls are best-effort — destroy must not block on a revoke failure (e.g., user already revoked from DO console). Log + continue.

### 6. Portal.Api — Refresh-on-login

New file `src/ThanyMarcus.Portal.Api/Features/Auth/Login/DigitalOceanTokenRefresher.cs`. Hooked into the post-login callback (where the session is established and DEK becomes warm):

```csharp
// pseudocode
foreach (var cloud in user.Clouds.Where(c => c.Provider == "do" && !c.IsDestroyed)) {
    var expiresAt = await secrets.GetExpiresAtAsync(cloud.Id, CloudSecretKind.DoOAuthAccess, ct);
    if (expiresAt is null || expiresAt > now.Plus(Duration.FromDays(5))) continue;
    try {
        var refreshToken = await secrets.GetAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh, dek, ct);
        var fresh = await doClient.RefreshAsync(refreshToken, ct);
        await secrets.PutAsync(cloud.Id, CloudSecretKind.DoOAuthAccess,  fresh.AccessToken,  dek,
                               now.Plus(Duration.FromSeconds(fresh.ExpiresIn)), ct);
        await secrets.PutAsync(cloud.Id, CloudSecretKind.DoOAuthRefresh, fresh.RefreshToken, dek, null, ct);
        cloud.ConnectionStatus = "connected";
    } catch (DigitalOceanOAuthRefreshFailedException) {
        cloud.ConnectionStatus = "needs_reauth";
    }
}
await db.SaveChangesAsync(ct);
```

No background `IHostedService`. Per the 2026-05-19 amendment, refresh only happens with a warm DEK.

### 7. Portal.Web — `clouds/new` flow

`src/ThanyMarcus.Portal.Web/src/routes/clouds/new/+page.svelte`:

- Provider step unchanged (`DO` / `Azure` cards).
- After picking DO: show a single full-width button **"Connect DigitalOcean"**. On click, `window.location.href = '/oauth/digitalocean/start'`. No region/size selection yet — those move into a "Configure" step after callback (or default to `nyc3` + `s-2vcpu-4gb` and offer change in dashboard, depending on whether onboarding wants to stay 1-click — recommendation: default + dashboard-edit).
- After picking Azure: existing service-principal paste flow unchanged.

`src/ThanyMarcus.Portal.Web/src/routes/oauth/digitalocean/callback/+page.svelte`:

- Receives the user back after DO redirect. Just shows a "Connecting your DigitalOcean account…" spinner; the server-side `/oauth/digitalocean/callback` handler is what actually does the token exchange and creates the Cloud row. This page can be a thin SvelteKit page that on mount fetches `/api/clouds/me/latest` and redirects to its provisioning view.

`src/ThanyMarcus.Portal.Web/src/lib/ProviderTokenSection.svelte`:

- Delete the DO option from the provider dropdown.
- Retain Azure path entirely.
- If a user had a pre-existing DO row in `encrypted_provider_tokens`, show a one-time banner: "Reconnect DigitalOcean — switched to OAuth. [Connect button]".

## Schema migration

Add migration `20260520000000_AddCloudSecretsAndOAuthColumns`. Up:

```csharp
migrationBuilder.CreateTable(name: "cloud_secrets", columns: …);  // matches DDL above
migrationBuilder.AddColumn<Instant?>("minting_spaces_started_at", "clouds");
migrationBuilder.AddColumn<string>("connection_status", "clouds",
    defaultValue: "connected", nullable: false);
```

Down: reverse.

Do **not** drop `encrypted_provider_tokens` — Azure still uses it. Out of scope for this ticket.

## Tests

### Backend (`tests/ThanyMarcus.Portal.Tests/`)

- `Features/Auth/DigitalOcean/DigitalOceanOAuthEndpointsTests.cs`:
  - `start`: redirects to `cloud.digitalocean.com/v1/oauth/authorize` with `response_type=code&client_id=…&scope=read+write&state=<32B-base64url>`.
  - `start`: requires authenticated session (302 to login if not).
  - `callback` happy path: with mocked `DigitalOceanOAuthClient` returning `{access, refresh, expires_in=2592000}` — Cloud row created in `pending`, 2 rows in `cloud_secrets` for that cloud, provisioning job enqueued, response 302 to `/clouds/{id}/provisioning`.
  - `callback`: state mismatch → 400, no Cloud row, no secrets, no job.
  - `callback`: DO token exchange returns 401 → 302 to `/clouds/new?error=oauth_failed`, no rows created.
- `Features/CloudManagement/Secrets/CloudSecretBundleTests.cs`:
  - put/get roundtrip, decrypt with same DEK works, decrypt with wrong DEK throws.
  - unique constraint on `(cloud_id, kind)`.
  - `DeleteAllForCloudAsync` deletes all rows for a given cloud.
- `Features/Auth/Login/DigitalOceanTokenRefresherTests.cs`:
  - access expiring in 3 days → refresh called, rows updated, connection_status stays `connected`.
  - access expiring in 30 days → refresh **not** called.
  - refresh throws → connection_status flips to `needs_reauth`.

### Saga worker (`tests/ThanyMarcus.Portal.SagaWorker.Tests/`)

- `MintingSpacesHandlerTests.cs`:
  - DO cloud: invokes `MintSpacesFullAccessKeyAsync`, persists 2 secrets, transitions to `TfPlanning`.
  - Azure cloud: skips minting, transitions to `TfPlanning` directly.
  - DO API returns 401 (token revoked mid-flow): handler transitions to `FailedMintingSpaces`.
- `TfApplyingHandlerTests.cs` — update existing happy-path test to assert all three env vars are present when provider is `do`.
- `DestroyEntryHandlerTests.cs`:
  - DO destroy: both DO revoke calls fired before terraform destroy, both 404s tolerated.
  - DO destroy with missing secrets (defensive): destroy still proceeds.

### Frontend

- Playwright (or your existing front-end test harness): clicking "Connect DigitalOcean" navigates the test browser to a URL starting with `cloud.digitalocean.com/v1/oauth/authorize`. Don't try to drive DO's own page; that's not testable end-to-end without real OAuth. The unit assertion is "we redirected to the right place."

## Acceptance criteria

1. A new user signing up, picking DO, clicking "Connect DigitalOcean," authorizing on DO, and being redirected back ends up on a live provisioning page with a Cloud row in `pending` status — without typing or pasting any credential.
2. Saga progresses through `MintingSpaces → TfPlanning → TfApplying → …` and `terraform apply` succeeds against a DO module that creates a Spaces bucket.
3. `encrypted_provider_tokens` has no new DO rows post-migration; new DO clouds have rows only in `cloud_secrets`.
4. User logs out, logs back in within 25 days → no refresh occurs; logs in after the 25-day mark with token still valid → refresh runs silently, rows updated.
5. User revokes the Thany app in DO's console → next login attempt refresh fails → dashboard shows "Reconnect DigitalOcean" — clicking it re-runs OAuth and replaces the bundle entries.
6. Destroy flow: clicking destroy on a DO cloud → Spaces key `DELETE` and OAuth `revoke` both observably fire (via mocked `DigitalOceanOAuthClient` in tests; via DO audit log on a live destroy) → terraform destroy succeeds → all `cloud_secrets` rows for that cloud are gone.
7. Azure flow unchanged end-to-end: existing Azure-provisioning integration tests pass with no edits.

## Open verification (blocks the implementation of step 3 only)

**Live test required before merging `MintingSpacesHandler`:** does an OAuth-issued `read_write` token actually authorize `POST /v2/spaces/keys`? The DO docs do not state a restriction, but the historical comment "Spaces keys can only be created via Control Panel" appeared in a SlashApi mirror and contradicts current DO docs — worth a 30-second confirmation rather than a 1-day debug surprise.

Procedure (Boiko's side, ~5 minutes):
1. Register a test OAuth app at `https://cloud.digitalocean.com/account/api/applications` with callback `http://localhost:5050/oauth/digitalocean/callback`.
2. Manually walk through the OAuth flow once (any test user; can be Boiko's own account) and capture the resulting `access_token`.
3. `curl -X POST -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"name":"test-mint","grants":[{"bucket":"","permission":"fullaccess"}]}' https://api.digitalocean.com/v2/spaces/keys`
4. Expect 201 with `{access_key_id, secret_key}` in body.
5. Cleanup: `curl -X DELETE -H "Authorization: Bearer $TOKEN" https://api.digitalocean.com/v2/spaces/keys/$ACCESS_KEY_ID`.

If step 4 returns 403 or "endpoint not available via OAuth," fall back to the documented contingency: keep DO OAuth for provisioning, accept a single Spaces-keys paste step from the user, and let them know in a "Why are we asking for this?" tooltip. Adds one paste, doesn't change the rest of this handoff.

## Out of scope

- **Azure secret-bundle migration.** Azure still uses `EncryptedProviderToken` after this ticket; a future PORTAL-018 generalizes it. Keeping the migration narrow keeps the blast radius bounded.
- **Token-rotation policy at DO.** The portal's OAuth client_id/client_secret rotation is manual and infrequent; runbook in `docs/operations/` is a follow-up.
- **Bucket-scoped grants.** The mint step requests `fullaccess` for the bootstrap window. Narrowing to bucket-scoped after the first apply (so a compromised Spaces key can only reach the user's own bucket, not other buckets in their DO account) is a future refinement — file as Tier-B in §28 of the cloud-pivot plan.
- **Telemetry for OAuth funnel.** Tracking "started OAuth → completed OAuth → callback succeeded → first apply succeeded" rate is post-thesis; the saga's existing SSE event stream already gives enough signal for the thesis.
- **CLI / API-only onboarding.** OAuth flow assumes a browser. If we ever want a non-browser path (CI, scripted bring-up), that's a separate ticket — likely a service-account model with a long-lived bot OAuth grant.

## References

- `plans/cloud-pivot-plan-2026-05-13.md` — 2026-05-19 amendment at end of §23 (this ticket's authoritative design)
- `plans/portal-016-handoff.md` — predecessor that landed cloud-callback encryption
- `docs/decisions/ADR-0023.md` — original two-credential portal design (superseded for DO by ADR-0039)
- `docs/decisions/ADR-0039.md` — **to be authored** alongside this implementation
- `docs/decisions/ADR-0034.md` — portal callback contract (preserved here)
- DigitalOcean: [OAuth API](https://docs.digitalocean.com/reference/api/oauth/), [Spaces Keys API](https://docs.digitalocean.com/reference/api/reference/spaces-keys/), [Token scopes](https://docs.digitalocean.com/reference/api/scopes/)
- Memory: `~/.claude/.../do_oauth_decision.md`

## Sequencing for implementation

A natural order with check-in points:

1. **Schema migration + `CloudSecret` entity + `CloudSecretBundle`** (no behavior change yet) — green build, tests pass.
2. **`DigitalOceanOAuthClient` + auth endpoints** behind a feature flag — manually verify the OAuth round-trip lands in the callback handler, stores secrets, but does not yet enqueue a provisioning job.
3. **Run the open verification** (live `POST /v2/spaces/keys` test) using a real OAuth token from step 2.
4. **`MintingSpacesHandler` + saga state additions** — DO cloud progresses through the new step in a test environment.
5. **Wire `TfApplyingHandler` to read from the bundle** — first end-to-end DO provision succeeds with a Spaces bucket.
6. **Refresh-on-login** + **destroy-time revoke** — close the lifecycle.
7. **Frontend swap** — flip the "Connect DigitalOcean" button on; delete the DO branch from `ProviderTokenSection.svelte`.
8. **Author ADR-0039** + update `docs/onboarding.md`.

Steps 1, 2, 4–6 can each ship as separate PRs against a feature branch; step 7's UI flip is the user-visible cutover.
