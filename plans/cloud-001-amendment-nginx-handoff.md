# CLOUD-001 Amendment — Replace Caddy + `events.handlers.exec` with nginx + certbot `--deploy-hook`

**Goal:** swap the Caddy-based reverse proxy + event firing path that CLOUD-001 originally introduced for an **nginx-on-host + certbot `--deploy-hook`** pattern, modeled on legacy commit `5af3afd7854e7f6a530d931c4f9f0e9edfb395d5` (n8n droplet provisioning). Preserve ADR-0034's callback contract and `/admin/health` payload 1:1 — only the *mechanism* that fires the callback changes.

Estimated **0.5 person-day** with AI-agent assistance. Net change: deletes more code than it adds (Caddyfile.tpl, CaddyAdminClient, CaddyHealthReader, CaddyEventsEndpoint + their tests + the `events.handlers.exec` dependency).

## Why this exists

CLOUD-001 landed `events { on cert_obtained exec curl -fsS -X POST http://cloud-api:8080/internal/caddy-events ... }` in the cloud-init Caddyfile. That directive requires the **third-party `caddy-events-exec` plugin** which is **not bundled in `caddy:2.7-alpine`**. The 4th PORTAL-011 smoke (2026-05-17, ~20:02) failed on the droplet with:

```
Error: adapting config using caddyfile: parsing caddyfile tokens for 'events':
       getting module named 'events.handlers.exec': module not registered:
       events.handlers.exec, at /etc/caddy/Caddyfile:6
```

This is a design defect in CLOUD-001, not a deployment misconfiguration. The fix is one of:
- **A.** Custom-built Caddy image via `xcaddy build --with github.com/mholt/caddy-events-exec`
- **B.** Replace Caddy with the legacy nginx + certbot pattern — **chosen here**
- **C.** Keep Caddy, drop the events directive, add Cloud.Api polling of Caddy admin API for cert appearance

**Why B over A and C:** The legacy infra commit (`5af3afd`) proves nginx + certbot is battle-tested on this exact stack (DO droplet + apt-installed nginx + certbot `--nginx --deploy-hook` + Docker app behind it). `certbot --deploy-hook` is **the** purpose-built primitive for "do something when a cert is issued/renewed" — a built-in feature of certbot, not a plugin. A imports a maintained-by-strangers Caddy plugin into our build pipeline indefinitely. C keeps Caddy + adds polling timer + still consumes Caddy admin API, more LOC than B with no real benefit.

## What stays vs goes

### Stays (ADR-0034 contract preserved)
- **Callback payload shape**: `{cloud_id, enrollment_token, cloud_admin_token}` POSTed to `${portal_callback_url}`. Unchanged.
- **`/admin/health` response shape**: `CloudAdminHealthResponse { CertReady, CloudId, RegistrationStatus, ApiVersion }`. Unchanged.
- **`PortalCallbackService`**: one-shot POST with 8-attempt exponential backoff + idempotency gate. Unchanged.
- **`BootstrapOptions` + `BootstrapState`**: unchanged contracts.
- **`AdminHealthEndpoint`**: unchanged surface (anonymous GET; reads `BootstrapState` + cert state).
- **`SagaTimeouts.AwaitingCloudCallback = 15 minutes`** in `src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/SagaTimeouts.cs`. Unchanged.

### Goes (Caddy entirely)
- **Compose `caddy` service** in the inlined compose at `infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl` lines 134–148.
- **`Caddyfile.tpl` `write_files` block** lines 60–86 of the same file.
- **`envsubst < Caddyfile.tpl > Caddyfile` runcmd step** lines 231–240.
- **`caddy-data` + `caddy-config` named volumes** lines 190–191.
- **`infra/docker/cloud/Caddyfile.tpl`** (if a non-inlined copy exists; the inlined one in cloud-init is canonical).
- **`src/ThanyMarcus.Cloud.Api/Infrastructure/Caddy/CaddyAdminClient.cs`** — typed HttpClient gone.
- **`src/ThanyMarcus.Cloud.Api/Features/Admin/Health/CaddyHealthReader.cs`** — replaced (see below).
- **`src/ThanyMarcus.Cloud.Api/Features/Bootstrap/CaddyEventsEndpoint.cs`** — renamed (see below).
- **`Caddy__AdminUrl` env var** in compose `cloud-api` environment block (line 157).
- **All Caddy admin-API DI registration** in `Program.cs` (`builder.Services.AddHttpClient<CaddyAdminClient>`, `AddScoped<CaddyHealthReader>`).
- **Tests against CaddyAdminClient / CaddyHealthReader** in `tests/ThanyMarcus.Cloud.Tests/`.

### Comes in
- **nginx** installed via apt on the droplet host (not in Docker).
- **certbot + python3-certbot-nginx** installed via apt.
- **`/etc/nginx/sites-available/${DOMAIN}`** templated in cloud-init `write_files`, reverse-proxies `:80` → `http://127.0.0.1:8080`.
- **`certbot --nginx --deploy-hook 'curl ...'`** runcmd step. Single command does cert issuance, nginx config rewrite for `:443 ssl`, HTTP→HTTPS redirect, and fires our callback hook.
- **`/internal/cert-installed` endpoint** in Cloud.Api (rename of `CaddyEventsEndpoint`). Same behavior: triggers `PortalCallbackService.PostRegistrationAsync` exactly once.
- **`CertFileReader`** replacement for `CaddyHealthReader`: checks `File.Exists("/etc/letsencrypt/live/${DOMAIN}/fullchain.pem")`.
- **Volume mount `/etc/letsencrypt:/etc/letsencrypt:ro`** on the `cloud-api` compose service so `CertFileReader` can see host certs.
- **Cloud.Api publishes `127.0.0.1:8080:8080`** (host loopback only) so nginx-on-host can reach it.

## Architecture diagram (post-amendment)

```
  client ── :443 ──► [nginx on host] ── 127.0.0.1:8080 ──► [cloud-api docker]
                          │                                       │
                  /etc/letsencrypt/live/$DOMAIN/                   │
                          ▲     volume mount :ro                   │
                          │     ┌────────────────────────────────┐ │
                  certbot ─┘    ▼                                │ │
                          │  CertFileReader.IsCertReady()        │ │
                          │  (File.Exists check)                 │ │
                          │                                      │ │
                  certbot --deploy-hook ──► POST :8080/internal/cert-installed
                                                  │              │
                                                  ▼              │
                                          PortalCallbackService ─┘
                                                  │
                                                  ▼
                                          POST $PORTAL_CALLBACK_URL
```

Two events of interest:
1. **Cert issuance/renewal** → certbot writes `/etc/letsencrypt/live/$DOMAIN/fullchain.pem` AND runs deploy-hook.
2. **Deploy-hook** → curls `http://127.0.0.1:8080/internal/cert-installed` on the host, which triggers `PortalCallbackService`.

## Concrete diffs

### `infra/docker/saga-worker/terraform-modules/digitalocean/cloud-init.yaml.tpl`

**Packages** (line 21 block) — replace:
```yaml
packages:
  - unattended-upgrades
  - ufw
  - fail2ban
  - curl
  - gnupg
  - jq
  - gettext-base
  - ca-certificates
  - nginx
  - certbot
  - python3-certbot-nginx
```

**`write_files`** — delete the Caddyfile.tpl block (lines 60–86) entirely. Add:
```yaml
  - path: /etc/thany-cloud/nginx-site.tpl
    owner: root:root
    permissions: "0644"
    content: |
      server {
          listen 80;
          listen [::]:80;
          server_name $${DOMAIN};

          location /internal/ {
              return 404;
          }

          location / {
              proxy_pass http://127.0.0.1:8080;
              proxy_set_header Host $host;
              proxy_set_header X-Real-IP $remote_addr;
              proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
              proxy_set_header X-Forwarded-Proto $scheme;
              proxy_http_version 1.1;
              proxy_set_header Upgrade $http_upgrade;
              proxy_set_header Connection "upgrade";
          }
      }
```

**Important — terraform templatefile escape rules:** terraform's `$$` escape collapses to `$` **only when followed by `{`** (i.e., `$${VAR}` → `${VAR}`). A bare `$host` is NOT a terraform interpolation token (no `{`) and therefore survives terraform unchanged. So nginx variables MUST be written as single `$host`, `$remote_addr`, etc. (matching legacy `5af3afd`). Writing `$$host` is WRONG — terraform leaves it as `$$host` in output, which nginx rejects with `invalid variable name`. `$${DOMAIN}` IS correct because `$` is followed by `{` — terraform collapses it to `${DOMAIN}` for envsubst to consume at first boot.

**Compose `caddy:` service** (lines 134–148) — delete entirely. `cloud-api` service needs adjustments:
```yaml
        cloud-api:
          image: ghcr.io/c4ptainhook/thany-cloud-api:$${IMAGE_TAG:-latest}
          restart: unless-stopped
          ports:
            - "127.0.0.1:8080:8080"
          environment:
            ASPNETCORE_ENVIRONMENT: Production
            ASPNETCORE_URLS: "http://+:8080"
            ConnectionStrings__Cloud: Host=postgres;Port=5432;Username=cloud;Password=$${POSTGRES_PASSWORD};Database=cloud
            Bootstrap__CloudId: $${CLOUD_ID}
            Bootstrap__Hostname: $${DOMAIN}
            Bootstrap__EnrollmentToken: $${ENROLLMENT_TOKEN}
            Bootstrap__CloudAdminToken: $${CLOUD_ADMIN_TOKEN}
            Bootstrap__PortalCallbackUrl: $${PORTAL_CALLBACK_URL}
            Cert__LiveDir: /etc/letsencrypt/live/$${DOMAIN}
          volumes:
            - /etc/letsencrypt:/etc/letsencrypt:ro
          depends_on:
            postgres:
              condition: service_healthy
          networks: [cloud]
          healthcheck:
            test: ["CMD-SHELL", "wget -q -O /dev/null http://localhost:8080/health/live || exit 1"]
            interval: 10s
            timeout: 5s
            retries: 5
```

Changes vs current:
- Add `ports: - "127.0.0.1:8080:8080"` so host nginx can reach it. **Loopback-only**: do NOT use `8080:8080` — that exposes Cloud.Api to the internet bypassing nginx + TLS.
- Drop `Caddy__AdminUrl` env var.
- Add `Cert__LiveDir` env var pointing into the LE live directory.
- Add `volumes: - /etc/letsencrypt:/etc/letsencrypt:ro` for `CertFileReader`.
- Drop `depends_on: caddy:` (Caddy gone).

**Compose `volumes:`** (line 189) — drop `caddy-data` + `caddy-config`. Keep `pg-data`.

**`runcmd`** — the section that did `envsubst < Caddyfile.tpl > Caddyfile` (lines 231–240) becomes the nginx-site render + certbot run. Replace the Caddyfile-render block with:

```yaml
  - |
    set -a
    # shellcheck disable=SC1091
    . /opt/thany-cloud/.env
    set +a
    envsubst '$DOMAIN' < /etc/thany-cloud/nginx-site.tpl > /etc/nginx/sites-available/$DOMAIN
    ln -sf /etc/nginx/sites-available/$DOMAIN /etc/nginx/sites-enabled/$DOMAIN
    rm -f /etc/nginx/sites-enabled/default
    nginx -t
    systemctl restart nginx
```

Then **start the docker stack** (existing line 243 `systemctl enable --now thany-cloud.service` stays), and **add the certbot step after**:

```yaml
  - |
    set -a
    . /opt/thany-cloud/.env
    set +a
    # Wait until cloud-api is healthy so the deploy-hook actually reaches a live target.
    for i in $(seq 1 60); do
      if curl -fsS -o /dev/null http://127.0.0.1:8080/health/live; then break; fi
      sleep 2
    done
    DEPLOY_HOOK="curl -fsS -X POST http://127.0.0.1:8080/internal/cert-installed \
      -H 'Content-Type: application/json' \
      -d '{\"event\":\"cert_installed\",\"identifier\":\"'$DOMAIN'\"}'"
    CERTBOT_FLAGS=""
    if [ "$LE_ACME_CA" = "https://acme-staging-v02.api.letsencrypt.org/directory" ]; then
      CERTBOT_FLAGS="--staging"
    fi
    certbot --nginx \
      -d "$DOMAIN" \
      --non-interactive \
      --agree-tos \
      -m "$LE_EMAIL" \
      --redirect \
      $CERTBOT_FLAGS \
      --deploy-hook "$DEPLOY_HOOK"
```

Notes:
- The poll loop (`for i in $(seq 1 60)`) waits up to **2 minutes** for cloud-api `/health/live` before invoking certbot. Without this, certbot succeeds → deploy-hook fires → curl returns ECONNREFUSED → callback lost. The portal-side 15-min `AwaitingCloudCallback` would catch it but slowly; better to gate on readiness here.
- `--staging` flag selection: certbot's `--nginx` plugin doesn't accept a free-form ACME server, so we use `--staging` when `LE_ACME_CA` matches LE staging URL. (Production uses default.) Pin `LE_ACME_CA` in saga config to either the staging URL or empty (= prod).
- The deploy-hook fires on **every** cert install + renewal. `PortalCallbackService` already dedupes via `BootstrapState.RegistrationStatus == "registered"` + `SemaphoreSlim`.

**`thany-cloud-web` ufw app** (lines 109–116): unchanged. nginx + Caddy both listen on `:80` + `:443` so the firewall rule is identical.

**`final_message`** — update to: `"Cloud-init completed in $UPTIME seconds. nginx + Cloud.Api running; registration fires on cert install."`

### `src/ThanyMarcus.Cloud.Api/`

**Delete:**
- `Infrastructure/Caddy/CaddyAdminClient.cs`
- `Infrastructure/Caddy/` (directory, if empty after delete)
- `Features/Admin/Health/CaddyHealthReader.cs`
- `Features/Bootstrap/CaddyEventsEndpoint.cs`

**Add `Features/Admin/Health/CertFileReader.cs`:**

```csharp
namespace ThanyMarcus.Cloud.Api.Features.Admin.Health;

public sealed class CertFileReader(IConfiguration config, ILogger<CertFileReader> log)
{
    private readonly string _liveDir = config["Cert:LiveDir"]
        ?? throw new InvalidOperationException("Cert:LiveDir required");

    public bool IsCertReady()
    {
        var fullchain = Path.Combine(_liveDir, "fullchain.pem");
        var privkey   = Path.Combine(_liveDir, "privkey.pem");
        var ready = File.Exists(fullchain) && File.Exists(privkey);
        if (!ready) log.LogDebug("Cert not present at {LiveDir}", _liveDir);
        return ready;
    }
}
```

**Rename `CaddyEventsEndpoint` → `CertInstalledEndpoint`** at `Features/Bootstrap/CertInstalledEndpoint.cs`. Body changes only to URL + payload-record name:

```csharp
namespace ThanyMarcus.Cloud.Api.Features.Bootstrap;

public static class CertInstalledEndpoint
{
    public static void MapCertInstalledEndpoint(this IEndpointRouteBuilder app) =>
        app.MapPost("/internal/cert-installed", (
            CertInstalledPayload body,
            PortalCallbackService callback,
            BootstrapOptions opts,
            ILogger<CertInstalledPayload> log,
            CancellationToken ct) =>
        {
            log.LogInformation("Received cert-installed event for {Identifier}", body.Identifier);

            if (body.Identifier != opts.Hostname)
                return Results.NoContent();

            _ = callback.PostRegistrationAsync(ct);
            return Results.NoContent();
        })
        .WithName("CertInstalled")
        .AllowAnonymous();
}

public sealed record CertInstalledPayload(string Event, string Identifier);
```

Still local-only — no auth — same reasoning as before (callable only from host loopback via nginx's `location /internal/ { return 404; }` block + the fact that the only path to reach `127.0.0.1:8080` from outside requires shell access).

**`Program.cs` diff:**

```csharp
// Delete:
- builder.Services.AddHttpClient<CaddyAdminClient>(c => { ... });
- builder.Services.AddScoped<CaddyHealthReader>();

// Add:
+ builder.Services.AddSingleton<CertFileReader>();

// Endpoint mapping:
- app.MapCaddyEventsEndpoint();
+ app.MapCertInstalledEndpoint();
```

**`AdminHealthEndpoint.cs` diff:** change the dependency from `CaddyHealthReader` to `CertFileReader`, and the method call from `IsCertReadyAsync(opts.Hostname, ct)` to `IsCertReady()`:

```csharp
app.MapGet("/admin/health", (
    CertFileReader cert,
    BootstrapState bootstrap,
    BootstrapOptions opts) =>
{
    var certReady = cert.IsCertReady();
    return Results.Ok(new CloudAdminHealthResponse(
        CertReady:          certReady,
        CloudId:            opts.CloudId,
        RegistrationStatus: bootstrap.RegistrationStatus,
        ApiVersion:         CloudAdminHealthResponse.CurrentApiVersion));
})
.WithName("AdminHealth")
.AllowAnonymous();
```

(Note: handler is now synchronous; the async-via-Caddy-HTTP-call is gone.)

### `src/ThanyMarcus.Cloud.Api/appsettings.json` / `appsettings.Development.json`

Remove any `Caddy` section. Add nothing — `Cert:LiveDir` is supplied by compose env (`Cert__LiveDir`) at runtime; for local dev set it via `dotnet user-secrets` or an environment variable.

### Tests — `tests/ThanyMarcus.Cloud.Tests/`

**Delete:**
- Any test that constructs `CaddyAdminClient` / `CaddyHealthReader` / `CaddyEventsEndpoint`.
- The two cases that asserted Caddy admin API responses.

**Add `Admin/CertFileReaderTests.cs`:**

```csharp
public sealed class CertFileReaderTests
{
    [Fact]
    public void Returns_false_when_live_dir_missing()
    {
        var reader = new CertFileReader(
            ConfigWith("/tmp/does-not-exist"),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeFalse();
    }

    [Fact]
    public void Returns_true_when_fullchain_and_privkey_both_present()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "fullchain.pem"), "x");
        File.WriteAllText(Path.Combine(tmp.Path, "privkey.pem"),   "y");
        var reader = new CertFileReader(
            ConfigWith(tmp.Path),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeTrue();
    }

    [Fact]
    public void Returns_false_when_only_fullchain_present()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "fullchain.pem"), "x");
        var reader = new CertFileReader(
            ConfigWith(tmp.Path),
            NullLogger<CertFileReader>.Instance);
        reader.IsCertReady().ShouldBeFalse();
    }

    private static IConfiguration ConfigWith(string liveDir) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cert:LiveDir"] = liveDir })
            .Build();
}
```

**Update `AdminHealthEndpointTests`** — point `Cert__LiveDir` at a temp dir per test, create/skip the `fullchain.pem` + `privkey.pem` pair to drive `cert_ready: true|false`. Drop any Caddy admin API mocking infrastructure.

**Update `CertInstalledEndpointTests`** (renamed from `CaddyEventsEndpointTests`): identifier matching + dedup behavior unchanged.

**`CloudInitTemplateRenderTests.cs`** at `tests/ThanyMarcus.Portal.Tests/CloudInit/`:
- Drop assertions about Caddyfile content / Caddy service / `events.handlers.exec`.
- Add assertions about: `packages:` contains `nginx`, `certbot`, `python3-certbot-nginx`; `/etc/thany-cloud/nginx-site.tpl` write_files block exists with `proxy_pass http://127.0.0.1:8080`; runcmd contains `certbot --nginx ... --deploy-hook`; `cloud-api` compose service has `ports: 127.0.0.1:8080:8080` and `volumes: /etc/letsencrypt:/etc/letsencrypt:ro`.

### ADR-0034 update

Section to amend (cite §2 source-of-cert-state, §4 retry semantics):

> **§2 `/admin/health` cert source (amended):** `cert_ready` is sourced by `CertFileReader` checking `File.Exists` on `${Cert:LiveDir}/fullchain.pem` and `privkey.pem` (default `/etc/letsencrypt/live/{hostname}`). The directory is bind-mounted read-only into the `cloud-api` container from the host. **This supersedes the Caddy admin API source described in the original CLOUD-001.**
>
> **§4 retry semantics (amended):** registration retries are unchanged (8 attempts, exponential backoff). The **trigger** changes from Caddy `events { on cert_obtained exec ... }` to `certbot --deploy-hook 'curl http://127.0.0.1:8080/internal/cert-installed ...'`. Deploy-hook fires on initial issuance AND on every renewal; idempotency is preserved by `BootstrapState.RegistrationStatus == "registered"` short-circuit + `SemaphoreSlim` gate in `PortalCallbackService`.

Section §1 (callback payload) and §3 (portal poll cadence) are **unchanged**.

Add a one-line note in §5 superseded-decisions: "Caddy reverse-proxy (ADR-0027) retired for user-cloud control plane; nginx + certbot adopted. Caddy may still be used in other deployment contexts but is not the reverse proxy on the user cloud."

## Acceptance criteria

- `dotnet build` — zero warnings; Caddy* types/files gone; CertFileReader + CertInstalledEndpoint compile.
- `dotnet test --filter "FullyQualifiedName~ThanyMarcus.Cloud.Tests"` — all green; new CertFileReader cases pass; renamed CertInstalledEndpoint cases pass; AdminHealthEndpoint cases pass against tempdir-based cert state.
- `dotnet test --filter "FullyQualifiedName~CloudInitTemplateRender"` — green against the new cloud-init shape.
- `docker compose -f infra/docker/cloud/docker-compose.yml --env-file <test-env> up -d` (if a non-inlined copy is maintained) — only `cloud-api` + `postgres`; no `caddy`. `cloud-api` healthcheck reaches green.
- `grep -r "Caddy\|caddy" src/ThanyMarcus.Cloud.Api/` — zero hits (other than possibly historical ADR refs in xml-doc comments, which should also be removed per "no narrative comments" convention).
- `grep -r "events.handlers.exec\|cert_obtained" infra/` — zero hits.
- **Integration acceptance** — re-run `plans/portal-011-smoke-runbook.md`. Saga walks `tf_planning → tf_applying → dns_creating → awaiting_cloud_callback → awaiting_cert → succeeded` on a fresh DO droplet, with the callback firing **within seconds** of LE staging issuing the cert (deploy-hook is synchronous to cert install). End-to-end timing should drop noticeably vs the 4 failed Caddy-era smokes.

## Concrete steps in order

1. **Land the source-side delete + rename** — drop `Caddy*.cs`, add `CertFileReader.cs`, rename `CaddyEventsEndpoint` → `CertInstalledEndpoint`. Update `Program.cs` + `AdminHealthEndpoint.cs` DI/wiring. Build clean.
2. **Update Cloud.Tests** — drop Caddy-related tests, add CertFileReader tests, fix AdminHealthEndpoint test fixtures to use tempdir cert state. `dotnet test` green.
3. **Update `cloud-init.yaml.tpl`** — delete Caddyfile.tpl write_files block + envsubst runcmd; delete Caddy service from inlined compose; add nginx-site.tpl write_files block + envsubst runcmd; add certbot --nginx --deploy-hook runcmd after `systemctl enable --now thany-cloud.service`. Add `ports: 127.0.0.1:8080:8080` + `volumes: /etc/letsencrypt:/etc/letsencrypt:ro` to cloud-api compose service. Add `nginx + certbot + python3-certbot-nginx` to apt packages.
4. **Update `CloudInitTemplateRenderTests.cs`** — assertions for the new shape. `dotnet test` green.
5. **Amend ADR-0034 §2, §4, §5** per the diff in this handoff.
6. **Local smoke** — `dotnet run --project src/ThanyMarcus.Cloud.Api` with `Cert__LiveDir=/tmp/fake-cert` (empty dir → `cert_ready: false`); `touch /tmp/fake-cert/fullchain.pem /tmp/fake-cert/privkey.pem` → next `/admin/health` returns `cert_ready: true`. `curl -X POST localhost:8080/internal/cert-installed -d '{"event":"cert_installed","identifier":"local.test"}'` (configured Hostname mismatch) → 204 no-op; correct identifier → callback POST hits mock portal once.
7. **Cross-ticket validation** — push to main; wait for `cloud-ci.yml` to publish `:latest`. Run end-to-end via Portal wizard. Saga reaches `succeeded` on a real droplet with a real LE staging cert. **This is the real DOD.**

## Risks & gotchas

- **`location /internal/ { return 404; }` block in nginx config** — defensive. Cloud.Api's `/internal/*` is bound to `0.0.0.0:8080` inside the container but only `127.0.0.1:8080` is published on the host. The 404 block makes external bypass via the nginx side path impossible even if someone changes the publish to `0.0.0.0`. Keep it.
- **`Cert__LiveDir` for local dev** — must be set (or `Program.cs` throws on startup). Document in `infra/docker/cloud/README.md`: "for local dev set `Cert__LiveDir=$(pwd)/fake-cert` and `mkdir -p $(pwd)/fake-cert` to start. `touch fake-cert/fullchain.pem fake-cert/privkey.pem` to flip `cert_ready: true`."
- **Read-only bind mount of `/etc/letsencrypt`** — UID inside `aspnet:10.0` container (typically `app`/65532) must be able to read the live dir. LE creates `0755 root:root` perms on `live/` and `0755 root:root` on the per-domain subdir; the actual PEM symlinks are `0644`/`0640`. Mount is `:ro`, so writes can't happen anyway, but read perms should be fine in default LE setup. Verify in smoke (`docker exec cloud-api ls -la /etc/letsencrypt/live/$DOMAIN`).
- **certbot --nginx and the `:443 ssl` block** — certbot rewrites the existing `/etc/nginx/sites-available/$DOMAIN` in-place to add the `listen 443 ssl;` block, `ssl_certificate`/`ssl_certificate_key` directives, and the `:80` → `:443` redirect. If the nginx-site template already contains a `:443` block, certbot may bail. **Keep the template `:80`-only**; let certbot own the SSL portion.
- **`--staging` flag detection** — the runcmd compares `LE_ACME_CA` against the literal staging URL. If the saga changes the URL convention, this comparison silently breaks (defaults to prod, hits LE rate limit fast). Lock with a test: render the cloud-init with `le_acme_ca = "https://acme-staging-v02.api.letsencrypt.org/directory"` and assert the rendered runcmd contains `--staging`.
- **Deploy-hook timing race** — the runcmd order matters: `systemctl enable --now thany-cloud.service` (starts Docker stack) **must** run before `certbot --nginx ... --deploy-hook ...`. The `for i in $(seq 1 60)` health-poll loop guards against the docker pull + cloud-api warmup taking >2 min, but if GHCR is slow or pull fails, certbot still issues the cert and deploy-hook curls fail. **Worst case**: cert exists on disk, callback never fires, portal hits the 15-min `AwaitingCloudCallback` timeout, saga fails. Mitigation already in place (15-min budget); document this failure mode.
- **`certbot renew` cron** — packaged certbot installs a renewal timer (`certbot.timer` on systemd-based Ubuntu). When LE renews, the deploy-hook re-fires. Cloud.Api's `BootstrapState` will short-circuit (already "registered"), so portal won't get a re-registration call. Portal-side, the renew callback is irrelevant — registration is one-shot for the cloud's lifetime. **Document but don't act**: if we ever want renew-time signaling to portal, that's a separate feature.
- **`ports: 127.0.0.1:8080:8080`** — Docker desktop on macOS handles `127.0.0.1:` prefix differently than Linux. Spec is Linux droplet, so this is fine in prod; local-dev with Docker Desktop may need a different incantation if you smoke locally. Out of scope for prod cloud-init.
- **`Caddy:AdminUrl` env in compose templates** — make sure to grep the whole repo, not just the inlined cloud-init. If there's a standalone `infra/docker/cloud/docker-compose.yml`, it has the same env block. Both must be updated.
- **Saga-side dead code** — if any `Portal.SagaWorker` handler imports `CaddyAdminClient` from `Shared` or references `caddy-events`, it needs cleanup. Grep `src/ThanyMarcus.Portal.SagaWorker/` for the same strings.
- **F14 rollback bug is orthogonal** — same retry-without-backoff + unlock-expired pattern that abandoned rollback on all 4 prior smokes. This handoff does not fix it; a separate ticket should land `rollback_tf` exponential backoff + step-up unlock survival for long-running sagas. Without that fix, any *future* failure during this amendment's smoke will still leave an orphan droplet.

## Definition of done

All acceptance criteria pass. ADR-0034 §2/§4/§5 amended. `grep -r "Caddy\|caddy" src/ infra/` clean. The 5th smoke run end-to-end reaches `saga.status = succeeded` on a real droplet with a real LE staging cert + a real callback firing from `certbot --deploy-hook`. The orphan droplets from smokes 1–4 are cleaned up (manual `doctl compute droplet delete` until F14 rollback ticket lands).

## Cross-references

- **Legacy commit `5af3afd7854e7f6a530d931c4f9f0e9edfb395d5`** — the working nginx + certbot pattern this amendment is modeled on. Read the diff before implementing.
- **`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`** — amended in §2/§4/§5 as part of this ticket.
- **`docs/decisions/0027-reverse-proxy-caddy.md`** — note in §5 that Caddy is retired for the user-cloud control plane; ADR-0027 still stands wherever Caddy is used elsewhere.
- **`plans/cloud-001-handoff.md`** — the original ticket this amendment supersedes for the Caddy bits. Everything in CLOUD-001 OTHER than Caddy stays (CloudDbContext, BootstrapOptions/State, PortalCallbackService, AdminHealthEndpoint payload, `ThanyMarcus.Shared/CloudAdmin/CloudAdminHealthResponse`, the test infrastructure, the CI workflow, GHCR image distribution).
- **F14 rollback retry-backoff ticket** — separate, not blocked by this. File it.
