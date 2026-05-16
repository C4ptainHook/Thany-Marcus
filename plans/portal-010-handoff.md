# PORTAL-010 Cloud-init bootstrap — Handoff Brief

**Goal:** land `infra/terraform/shared/cloud-init.sh.tpl` — the provider-agnostic cloud-init user-data template that the DO and Azure terraform modules feed into `user_data` / `custom_data`. Plus the minimal cloud-side scaffolds (`docker-compose.yml`, `Caddyfile`, `.env.tpl`) that let the cloud-init flow boot a real droplet/VM to a state where Caddy has acquired a Let's Encrypt cert and the cloud's stub `/admin/health` returns `cert_ready: true`. After this ticket a `terraform apply` against PORTAL-008/009's modules produces a running VM that satisfies the contract the saga's `AwaitingCloudCallbackHandler` + `AwaitingCertHandler` consume (see PORTAL-007). **The real `ThanyMarcus.Cloud.Api` service (CLOUD-001), the production Caddyfile (CLOUD-006), the cloud's admin endpoints (CLOUD-005), and the portal's inbound callback endpoint (PORTAL-016) are OUT of scope** — this ticket lands placeholder shapes so the bootstrap is testable end-to-end before CLOUD-* tickets fill them in.

Estimated **0.75 person-day** with heavy AI-agent assistance. The original ticket-list estimate (0.75 d) holds: the cloud-init template itself is mostly the legacy n8n template (`infra/legacy/infrastructure/templates/cloudinit.tpl`) recombined around the new envelope, and the cloud-side compose scaffold is a 30-line file that CLOUD-001 supersedes.

## Where decisions live (read before doing anything)

- **`plans/cloud-pivot-plan-2026-05-13.md` §23 "Deployment — User's cloud deployment"** — the canonical step-by-step of what cloud-init must do (install Docker, pull compose, render `.env`, `docker compose up`, cert acquisition, callback to portal). Lines 505–522.
- **`plans/cloud-pivot-plan-2026-05-13.md` §7 "Architecture diagram" (lines 127–145)** — the user-cloud service inventory (api, postgres+pgvector, ollama, parakeet, caddy). For this ticket the compose scaffold only includes Caddy + a placeholder api container; CLOUD-001 + CLOUD-006 + CLOUD-007 + CLOUD-008 add the rest.
- **`plans/tickets-2026-05-13.md` DEC-001** — VPS spec (16 GB / 4 vCPU). Cloud-init runs on Ubuntu 24.04 LTS on both DO `s-4vcpu-16gb` and Azure `Standard_B4ms`. Architecture is `amd64` on both.
- **`plans/tickets-2026-05-13.md` DEC-003** — LE retry: cloud-init's Caddy retries LE every 60s for up to 30 min. The portal polls `/admin/health → cert_ready: bool` every 5 s (first minute) / 30 s (after) for up to 30 min, then surfaces `failed_cert`. Cloud-init **must not** treat LE failure as a fatal bootstrap error — it lets Caddy keep retrying and exposes the in-progress state via `/admin/health`.
- **`plans/tickets-2026-05-13.md` DEC-005** — image distribution: **GitHub Container Registry** (`ghcr.io/<owner>/thany-marcus-cloud-api:<tag>` etc.). Public images; no GHCR auth needed for pulls in MVP. Image signing via cosign is post-thesis.
- **`docs/decisions/0033-provisioning-saga-and-worker.md` §"Awaiting cloud callback" + §"Awaiting cert"** — the two contracts cloud-init must satisfy: (1) at first boot, the cloud POSTs `{ cloud_id, enrollment_token, cloud_admin_token }` to portal's `${portal_callback_url}`; (2) Caddy + cloud API together expose `GET /admin/health → { cert_ready: bool, ... }`.
- **`infra/legacy/infrastructure/templates/cloudinit.tpl`** — the n8n-era template. Reuse the hardening blocks verbatim (UFW + fail2ban + unattended-upgrades + sshd hardening + Docker apt source). The compose-service + .env shape is replaced wholesale.
- **`plans/portal-007-handoff.md` §"AwaitingCertHandler" + §"AwaitingCloudCallbackHandler"** — the consumer side. Reading these clarifies what payloads/timing cloud-init must match.
- **Memory files**: `portal_deployment.md` (single-VM Compose at the portal side; user-cloud is symmetric), `portal_architecture.md` (mutable status, SSE only — irrelevant to cloud-init but useful background).

**Note:** ADR-0034 ("cloud bootstrap and portal handshake") and ADR-0035 ("wizard progress transport") are referenced from PORTAL-007 but **do not yet exist on disk**. PORTAL-010 is the right ticket to *write ADR-0034* — it owns the cloud-init half of that contract. ADR-0035 stays unwritten until PORTAL-011 (wizard UI). **Add ADR-0034 as part of this ticket** (template at `docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`); it should pin the payloads, the polling cadences, and the timeout values so PORTAL-016 can implement the callback endpoint without re-deriving them.

**Do not invent payload shapes, timeouts, or env-var names.** Where this brief specifies them, they are the canonical values. Where it doesn't, prefer DEC-003 cadences and the legacy template's variable names.

## Scope boundary (precise)

**In scope:**
- `infra/terraform/shared/cloud-init.sh.tpl` — the cloud-init user-data template (terraform `templatefile()` consumes it). Uses `cloud-config` format (YAML) with `runcmd` and `write_files` blocks. **Not a bash script** despite the `.sh.tpl` filename hint in `tickets-2026-05-13.md`; `cloud-config` is strictly more reliable than custom bash, and matches the legacy template. Rename to `cloud-init.yaml.tpl` for clarity — the tickets-file shorthand was loose.
- `infra/docker/cloud/docker-compose.yml` — minimal scaffold: `caddy` service + a placeholder `cloud-api` service (an nginx container serving a stub `/admin/health` JSON). CLOUD-001 swaps the placeholder for the real .NET service in M3.
- `infra/docker/cloud/Caddyfile.tpl` — Caddy config templated with the user's `${hostname}`. Reverse-proxies `/admin/*` and `/api/*` to `cloud-api:8080`. CLOUD-006 supersedes the template logic with the real Caddyfile once endpoints are pinned.
- `infra/docker/cloud/cloud.env.tpl` — `.env` template rendered into `/opt/thany-cloud/.env` by cloud-init. Variables: `CLOUD_ID`, `CLOUD_ADMIN_TOKEN`, `ENROLLMENT_TOKEN`, `PORTAL_CALLBACK_URL`, `DOMAIN`, `LE_EMAIL`, `JWT_SIGNING_KEY`, `IMAGE_TAG`.
- `infra/docker/cloud/stub-admin-health/` — a tiny image (or just an `nginx:alpine` + mounted JSON) that responds at `GET /admin/health` with `{ "cert_ready": <bool>, "cloud_id": "<id>" }`. The `cert_ready` value is determined by checking whether `/var/lib/caddy/.local/share/caddy/certificates/` contains a non-empty entry for the domain — a `bash` sidecar that writes a flag file Caddy + nginx both observe. Pragmatic, not pretty; CLOUD-006 cleans it up.
- A `register-with-portal.sh` script baked into the cloud-init `write_files` block. On first boot, after `docker compose up -d` and after the cert appears, it `curl`s the portal's `${PORTAL_CALLBACK_URL}` with `{ cloud_id, enrollment_token, cloud_admin_token }`. Retries on 5xx / network failure with exponential backoff up to 30 min, then writes `/var/log/thany-cloud/registration-failed` and exits 1 (systemd surfaces it via `systemctl status thany-cloud-register`).
- `docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md` — the ADR. Defines payloads, cadences, and the `cert_ready` contract.
- **Local smoke-test harness:** a `scripts/cloud-init-smoke.sh` that uses **multipass** (`multipass launch 24.04 --cloud-init <rendered-yaml> --cpus 4 --memory 16G --disk 50G`) to spin a local VM, waits for cloud-init to finish (`multipass exec <vm> -- cloud-init status --wait`), then asserts `/admin/health` returns `cert_ready: false` initially and `cert_ready: true` after Caddy obtains a staging-LE cert (use `acme_ca = https://acme-staging-v02.api.letsencrypt.org/directory` in the Caddyfile template for the smoke variant — production uses prod LE). Multipass works on macOS + Linux; Boiko's dev environment is macOS per memory. Document the Linux alternative (`lxd launch` + `lxc config set ... user.user-data=...`).
- Tests:
  - `cloud-init schema --config-file <rendered>` validates the YAML.
  - `shellcheck` against any bash fragments embedded via `write_files` and against `register-with-portal.sh`.
  - The multipass smoke test (manual; not in CI — multipass needs hypervisor).
  - A terraform `templatefile()` rendering test in `tests/ThanyMarcus.Portal.Tests/CloudInit/` that asserts a sample variable set produces a valid YAML output (Python's `yaml.safe_load` via `python3 -c '...'` from a `dotnet test` shell-out, OR `YamlDotNet.Serialization` parsing in C# directly — prefer the latter, add `YamlDotNet` to `Directory.Packages.props` if not present).

**Out of scope (DO NOT touch):**
- **The real `ThanyMarcus.Cloud.Api` service** — CLOUD-001. The placeholder `nginx:alpine` + JSON is enough to satisfy the cert + `/admin/health` contract for PORTAL-010's smoke test.
- **Cloud-side Postgres + pgvector** — CLOUD-002. Not in the compose scaffold; CLOUD-001 adds it as part of its docker-compose stack.
- **Ollama / Parakeet containers** — CLOUD-007 / CLOUD-008. They land in M6; the scaffold's compose file does not include them.
- **Production Caddyfile** — CLOUD-006. Our `Caddyfile.tpl` only needs to acquire a cert and reverse-proxy `/admin/health`; it does not need to handle real API routing.
- **Cloudflare A-record creation** — PORTAL-010b. By the time cloud-init runs, the A record already points at the droplet IP (the saga's `DnsCreatingHandler` has already called Cloudflare). Cloud-init *assumes* DNS resolves.
- **Portal's inbound callback endpoint (`POST /api/clouds/{id}/callback`)** — PORTAL-016. The cloud-init knows the URL to POST to (terraform passes it via `portal_callback_url`); the endpoint is stubbed in the saga's e2e test until PORTAL-016 lands.
- **`/admin/register-with-portal` endpoint on the cloud side** — CLOUD-005. Cloud-init does NOT call back into its own API; it directly POSTs to the portal. CLOUD-005's endpoint is for *post*-bootstrap admin ops, not the initial handshake.
- **Provider-specific terraform module wiring** — PORTAL-008 (DO) and PORTAL-009 (Azure) consume `cloud-init.yaml.tpl` via `templatefile()` and pass the rendered YAML into `digitalocean_droplet.user_data` / `azurerm_linux_virtual_machine.custom_data`. PORTAL-010 lands the template, not the consumers.
- **Stub-module integration with cloud-init** — the PORTAL-007 stub module uses `null_resource` and intentionally does not boot a VM. Cloud-init smoke testing here is multipass-only; the saga's e2e test continues to use the stub module.
- **Disk / volume encryption** — DO and Azure native flags; set in PORTAL-008/009. Cloud-init does not touch LUKS.
- **SSH key generation** — terraform generates the per-cloud SSH key (PORTAL-008/009); cloud-init only writes the public key into `~admin/.ssh/authorized_keys` via the user template variable.

## Output of PORTAL-010 — final directory state

```
Thany-Marcus/
├── infra/
│   ├── terraform/
│   │   └── shared/
│   │       └── cloud-init.yaml.tpl                       # NEW: cloud-config user-data template
│   └── docker/
│       └── cloud/
│           ├── docker-compose.yml                        # NEW: scaffold (Caddy + nginx placeholder)
│           ├── Caddyfile.tpl                             # NEW: domain templated in by cloud-init
│           ├── cloud.env.tpl                             # NEW: .env template, rendered by cloud-init
│           └── stub-admin-health/
│               ├── nginx.conf                            # NEW: serves /admin/health from a JSON file
│               └── README.md                             # NEW: explains the cert-flag sidecar trick
├── scripts/
│   └── cloud-init-smoke.sh                               # NEW: multipass smoke test
├── docs/
│   └── decisions/
│       └── 0034-cloud-bootstrap-and-portal-handshake.md  # NEW: the contract ADR
└── tests/ThanyMarcus.Portal.Tests/
    └── CloudInit/
        ├── CloudInitTemplateRenderTests.cs               # NEW: terraform templatefile renders to valid YAML
        └── Fixtures/
            └── sample-vars.tfvars.json                   # NEW: fixture for the render test
```

## Terraform template variables (the cloud-init contract)

The provider modules (PORTAL-008/009) call `templatefile("${path.module}/../shared/cloud-init.yaml.tpl", { ... })` with these variables. **Lock the names here so the modules can be written against a stable contract.**

| Variable | Type | Source | Sensitive | Purpose |
|---|---|---|---|---|
| `cloud_id` | string (UUID) | `var.cloud_id` from saga | no | Goes into `.env` + the registration POST payload |
| `hostname` | string (FQDN, e.g. `abc123def.thany.click`) | `var.hostname` | no | Templated into Caddyfile; used for LE cert; written to `.env` as `DOMAIN` |
| `enrollment_token` | string (32-byte hex) | `var.enrollment_token` (generated by saga per job) | yes | Authenticates the registration POST; portal's `AwaitingCloudCallbackHandler` validates it matches what it stashed for the job |
| `cloud_admin_token` | string (32-byte hex) | **Generated by cloud-init at boot via `openssl rand -hex 32`** — NOT passed in from terraform | yes (post-generation) | The token portal uses to call back into `/admin/*` on the cloud. Cloud-init generates it, writes to `.env`, sends to portal via the registration POST. **One source of truth:** the cloud-init script. |
| `portal_callback_url` | string (URL, e.g. `https://app.thany.click/api/clouds/<cloud_id>/callback`) | `var.portal_callback_url` (built by the saga from the cloud_id + portal's base URL from config) | no | Where the registration POST goes. Includes the cloud_id in the path so portal routes it without parsing the body |
| `le_email` | string (email) | `var.le_email` (from portal config; not user-supplied) | no | Let's Encrypt account email — defaults to `boiko+letsencrypt@<portal-domain>` |
| `image_tag` | string (e.g. `v0.1.0` or `main-abc1234`) | `var.image_tag` (portal config; pinned per release) | no | Cloud-side image versions; written to `.env` as `IMAGE_TAG` for the compose's `${IMAGE_TAG}` substitution |
| `ghcr_pat` | string | `var.ghcr_pat` (from portal config; empty string for public images) | yes | Optional. Empty for MVP since DEC-005 mandates public GHCR images. Reserve the variable so post-MVP private images don't require a template change. |
| `admin_user` | string | `var.admin_user` (default `thanyadmin`) | no | The non-root sudo user cloud-init creates |
| `ssh_public_key` | string (OpenSSH format) | `var.ssh_public_key` (terraform-generated per cloud) | no | Written to `~admin/.ssh/authorized_keys` |
| `compose_url` | string (URL to raw `docker-compose.yml`) | `var.compose_url` (portal config, e.g. `https://raw.githubusercontent.com/<owner>/thany-marcus/${var.compose_ref}/infra/docker/cloud/docker-compose.yml`) | no | The URL cloud-init `curl`s to fetch the compose file. Pinned to a git ref, not `main`. |
| `compose_aux_files` | list(object({ path = string, url = string })) | terraform-rendered | no | Aux files to download (Caddyfile.tpl, stub nginx.conf). Templated into a `write_files` loop. |
| `timezone` | string (default `Etc/UTC`) | `var.timezone` | no | System timezone |

**The provider modules are responsible for** generating `enrollment_token` (e.g., via `random_id`), generating `ssh_public_key` (via `tls_private_key`), and constructing `portal_callback_url`. PORTAL-010 owns the template variable list itself, not the variable derivations.

## `cloud-init.yaml.tpl` — the core template

Targets cloud-init v23+ (Ubuntu 24.04 ships v24.x). Uses `cloud-config` YAML format. Order of operations:

1. `bootcmd` — directory pre-create + timezone set.
2. `users` — non-root sudo user with SSH key.
3. `package_update: true` / `package_upgrade: true` / install hardening packages.
4. `write_files` — sshd hardening, ufw app definition, the `.env.tpl` placeholder, the Caddyfile.tpl placeholder, the stub nginx.conf, the `register-with-portal.sh` script, the `thany-cloud.service` systemd unit, the `thany-cloud-register.service` systemd unit.
5. `runcmd`:
   1. UFW configure + enable (allow `22/tcp` from anywhere; allow `80/tcp` + `443/tcp` for Caddy + LE HTTP-01).
   2. Install Docker (via the legacy template's apt-source block).
   3. `usermod -aG docker $${admin_user}` (cloud-init `runcmd` runs as root; this lets the admin user run compose without sudo).
   4. Generate `cloud_admin_token` and `jwt_signing_key`: `openssl rand -hex 32 > /run/cloud-secrets/cloud_admin_token && openssl rand -hex 32 > /run/cloud-secrets/jwt_signing_key` (in tmpfs so they don't hit disk before the next step).
   5. Render the `.env` from the template at `/etc/thany-cloud/cloud.env.tpl` into `/opt/thany-cloud/.env` via `envsubst` (the secrets read from `/run/cloud-secrets/`).
   6. `curl -fsSL $${compose_url} -o /opt/thany-cloud/docker-compose.yml` — pinned to `compose_ref`.
   7. `envsubst < /etc/thany-cloud/Caddyfile.tpl > /opt/thany-cloud/Caddyfile` — bake the domain in.
   8. `cd /opt/thany-cloud && docker compose up -d`.
   9. `systemctl enable --now thany-cloud-register.service` — kicks off the registration POST + cert-poll loop in the background.
   10. Wipe `/run/cloud-secrets/` (it's tmpfs but explicit clear avoids the case where compose-up failed and we'd want the next reboot to retry; for v1, accept the limitation that a failed compose-up needs manual re-render — document).
6. `final_message: "Cloud-init completed in $UPTIME seconds. Caddy + registration running async."`

**The verbatim YAML template** (paste into `infra/terraform/shared/cloud-init.yaml.tpl`):

```yaml
#cloud-config
bootcmd:
  - mkdir -p /opt/thany-cloud /etc/thany-cloud /var/log/thany-cloud /run/cloud-secrets
  - chmod 0700 /run/cloud-secrets
  - timedatectl set-timezone ${timezone}

users:
  - default
  - name: ${admin_user}
    groups: [adm, sudo]
    shell: /bin/bash
    lock_passwd: true
    sudo: "ALL=(ALL) NOPASSWD:ALL"
    ssh_authorized_keys:
      - ${ssh_public_key}

ssh_pwauth: false

package_update: true
package_upgrade: true
packages:
  - unattended-upgrades
  - ufw
  - fail2ban
  - curl
  - gnupg
  - jq
  - gettext-base   # provides envsubst
  - ca-certificates

write_files:
  - path: /etc/ssh/sshd_config.d/01-hardening.conf
    owner: root:root
    permissions: "0644"
    content: |
      PasswordAuthentication no
      PermitRootLogin no
      ChallengeResponseAuthentication no
      UsePAM yes
      X11Forwarding no
      ClientAliveInterval 300
      ClientAliveCountMax 2
      AllowUsers ${admin_user}

  - path: /etc/thany-cloud/cloud.env.tpl
    owner: root:root
    permissions: "0640"
    content: |
      CLOUD_ID=${cloud_id}
      DOMAIN=${hostname}
      LE_EMAIL=${le_email}
      IMAGE_TAG=${image_tag}
      ENROLLMENT_TOKEN=${enrollment_token}
      PORTAL_CALLBACK_URL=${portal_callback_url}
      # Generated at boot — see runcmd
      CLOUD_ADMIN_TOKEN=$${CLOUD_ADMIN_TOKEN}
      JWT_SIGNING_KEY=$${JWT_SIGNING_KEY}

  - path: /etc/thany-cloud/Caddyfile.tpl
    owner: root:root
    permissions: "0644"
    content: |
      {
        email $${LE_EMAIL}
      }
      $${DOMAIN} {
        encode gzip
        reverse_proxy /admin/* cloud-api:8080
        reverse_proxy /api/*   cloud-api:8080
        respond 404
      }

  - path: /usr/local/bin/register-with-portal.sh
    owner: root:root
    permissions: "0755"
    content: |
      #!/usr/bin/env bash
      set -euo pipefail
      ENV_FILE=/opt/thany-cloud/.env
      # shellcheck disable=SC1090
      source "$ENV_FILE"

      # Wait for cert (poll /admin/health from inside the host).
      DEADLINE=$(( $(date +%s) + 1800 ))   # 30 min per DEC-003
      while [[ $(date +%s) -lt $DEADLINE ]]; do
        if curl -fsS "http://127.0.0.1/admin/health" \
             -H "Host: $DOMAIN" \
             | jq -e '.cert_ready == true' > /dev/null 2>&1; then
          break
        fi
        sleep 5
      done

      if ! curl -fsS "http://127.0.0.1/admin/health" -H "Host: $DOMAIN" \
            | jq -e '.cert_ready == true' > /dev/null 2>&1; then
        echo "Cert not ready after 30 min; aborting registration" >&2
        exit 1
      fi

      # Cert is ready — call portal. Retry with exponential backoff up to 10 min total.
      ATTEMPT=0
      while (( ATTEMPT < 8 )); do
        if curl -fsS -X POST "$PORTAL_CALLBACK_URL" \
             -H 'Content-Type: application/json' \
             --data "$(jq -n \
               --arg cid "$CLOUD_ID" \
               --arg et "$ENROLLMENT_TOKEN" \
               --arg cat "$CLOUD_ADMIN_TOKEN" \
               '{cloud_id:$cid, enrollment_token:$et, cloud_admin_token:$cat}')"; then
          echo "Registered with portal"
          exit 0
        fi
        sleep $(( 2 ** ATTEMPT ))
        ATTEMPT=$(( ATTEMPT + 1 ))
      done

      echo "Registration failed after $ATTEMPT attempts" >&2
      exit 1

  - path: /etc/systemd/system/thany-cloud.service
    owner: root:root
    permissions: "0644"
    content: |
      [Unit]
      Description=Thany Cloud Docker Compose
      Requires=docker.service
      After=docker.service network-online.target
      Wants=network-online.target

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      WorkingDirectory=/opt/thany-cloud
      User=${admin_user}
      ExecStart=/usr/bin/docker compose up -d
      ExecStop=/usr/bin/docker compose down

      [Install]
      WantedBy=multi-user.target

  - path: /etc/systemd/system/thany-cloud-register.service
    owner: root:root
    permissions: "0644"
    content: |
      [Unit]
      Description=Register cloud with portal
      Requires=thany-cloud.service
      After=thany-cloud.service

      [Service]
      Type=oneshot
      RemainAfterExit=yes
      ExecStart=/usr/local/bin/register-with-portal.sh
      StandardOutput=append:/var/log/thany-cloud/register.log
      StandardError=append:/var/log/thany-cloud/register.log

      [Install]
      WantedBy=multi-user.target

  - path: /etc/ufw/applications.d/thany-cloud
    owner: root:root
    permissions: "0644"
    content: |
      [thany-cloud-web]
      title=Thany Cloud (HTTP/HTTPS)
      description=Caddy + LE
      ports=80,443/tcp

  - path: /etc/apt/apt.conf.d/50unattended-upgrades
    owner: root:root
    permissions: "0644"
    content: |
      Unattended-Upgrade::Allowed-Origins {
        "$${distro_id}:$${distro_codename}";
        "$${distro_id}:$${distro_codename}-security";
        "$${distro_id}:$${distro_codename}-updates";
      };
      Unattended-Upgrade::Automatic-Reboot "true";
      Unattended-Upgrade::Automatic-Reboot-Time "03:30";

runcmd:
  # Firewall
  - ufw default deny incoming
  - ufw default allow outgoing
  - ufw limit OpenSSH
  - ufw allow thany-cloud-web
  - ufw --force enable

  # Docker apt source + install
  - install -m 0755 -d /etc/apt/keyrings
  - curl -fsSL https://download.docker.com/linux/ubuntu/gpg | gpg --dearmor -o /etc/apt/keyrings/docker.gpg
  - chmod a+r /etc/apt/keyrings/docker.gpg
  - |
    echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.gpg] https://download.docker.com/linux/ubuntu $(. /etc/os-release && echo $VERSION_CODENAME) stable" \
      > /etc/apt/sources.list.d/docker.list
  - apt-get update
  - apt-get install -y docker-ce docker-ce-cli containerd.io docker-compose-plugin
  - usermod -aG docker ${admin_user}

  # Secrets in tmpfs
  - openssl rand -hex 32 > /run/cloud-secrets/cloud_admin_token
  - openssl rand -hex 32 > /run/cloud-secrets/jwt_signing_key
  - chmod 0600 /run/cloud-secrets/*

  # Render .env (reads secrets from tmpfs via env)
  - |
    set -a
    CLOUD_ADMIN_TOKEN=$(cat /run/cloud-secrets/cloud_admin_token)
    JWT_SIGNING_KEY=$(cat /run/cloud-secrets/jwt_signing_key)
    set +a
    envsubst < /etc/thany-cloud/cloud.env.tpl > /opt/thany-cloud/.env
    chown ${admin_user}:${admin_user} /opt/thany-cloud/.env
    chmod 0640 /opt/thany-cloud/.env

  # Compose file from pinned URL
  - curl -fsSL "${compose_url}" -o /opt/thany-cloud/docker-compose.yml
  - chown ${admin_user}:${admin_user} /opt/thany-cloud/docker-compose.yml

  # Caddyfile from template
  - |
    set -a
    # shellcheck disable=SC1091
    source /opt/thany-cloud/.env
    set +a
    envsubst < /etc/thany-cloud/Caddyfile.tpl > /opt/thany-cloud/Caddyfile

  # Bring everything up
  - systemctl daemon-reload
  - systemctl enable --now thany-cloud.service
  - systemctl enable --now thany-cloud-register.service

  # Cleanup
  - shred -u /run/cloud-secrets/cloud_admin_token /run/cloud-secrets/jwt_signing_key || true

  # Hardening
  - systemctl restart ssh
  - systemctl enable --now unattended-upgrades
  - systemctl restart fail2ban || true

final_message: "Cloud-init completed in $UPTIME seconds. Caddy + registration running async."
```

**Notes:**

- `${var}` is **terraform** templating. `$${var}` is the literal `${var}` (passed through to the rendered cloud-init for shell-time expansion). Watch this carefully when editing — the legacy template uses the same convention, the rendered file uses single-`$`.
- `envsubst` is intentional: it only substitutes variables that are *set in the env at substitution time*, so a leaked `${...}` doesn't pull from random env. Sensitive secrets (`CLOUD_ADMIN_TOKEN`, `JWT_SIGNING_KEY`) live in tmpfs and are exported only during the substitution block. After the `shred -u`, they exist only in `/opt/thany-cloud/.env` (0640, owned by admin user).
- The Caddyfile uses `email` for LE registration — required by Caddy for ACME. Without it Caddy uses `terms-of-service-accepted` mode which Let's Encrypt now flags.
- `reverse_proxy /admin/* cloud-api:8080` — the placeholder nginx (and later, real Cloud.Api) listens on 8080 inside the compose network.
- `respond 404` catches everything else so the placeholder doesn't accidentally expose nginx defaults. CLOUD-006's real Caddyfile reshapes this once the API routes are pinned.
- `thany-cloud-register.service` runs **after** `thany-cloud.service` because the cert-poll target is the cloud's own `/admin/health`. The script waits for cert-ready then POSTs. Ordering matters: if the compose stack isn't up, the cert can't be ready.

## `infra/docker/cloud/docker-compose.yml` — the scaffold

A 30-line file that boots Caddy + a stub `cloud-api` (nginx serving `/admin/health` from a file). **CLOUD-001 supersedes this entirely** — but ship this version now so the cloud-init flow has something real to bring up:

```yaml
services:
  caddy:
    image: caddy:2-alpine
    restart: unless-stopped
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile:ro
      - caddy-data:/data
      - caddy-config:/config
    depends_on:
      - cloud-api

  cloud-api:
    image: nginx:alpine
    restart: unless-stopped
    volumes:
      - ./nginx.conf:/etc/nginx/nginx.conf:ro
      - cert-flag:/var/lib/caddy/.local/share/caddy/certificates:ro
    environment:
      CLOUD_ID: ${CLOUD_ID}

volumes:
  caddy-data:
  caddy-config:
  cert-flag:
```

**Limitation:** the `cert-flag` volume mount tries to share Caddy's cert directory with the nginx placeholder. In practice this doesn't quite work because Caddy writes the cert *after* nginx is up and nginx doesn't watch the directory. Two ways out:

1. **Pragmatic (recommended for PORTAL-010 scope):** the nginx placeholder reads `/etc/nginx/cert-status` (a file initially containing `{"cert_ready":false}`) and serves it at `/admin/health`. A small `caddy-hook` sidecar (alpine + inotify) watches Caddy's cert dir and rewrites the file to `{"cert_ready":true}` once the cert appears. Add the sidecar to the compose file. This is gross; document that CLOUD-001/006 reshapes it.
2. **Simpler placeholder:** the nginx config returns a *hardcoded* `cert_ready: true` after a fixed 90 s delay (a `sleep 90 && touch /tmp/cert-ready` initContainer pattern, then nginx serves different content based on file existence). Faster to land; less faithful to the real contract.

**Choose option 1.** PORTAL-016's callback validation depends on the registration script firing *only* after cert acquisition, and a fake delay-based flag would defeat the smoke test's purpose. The inotify sidecar adds ~15 lines to the compose; worth the fidelity.

Add to the compose:

```yaml
  cert-watcher:
    image: alpine:3.19
    restart: unless-stopped
    command: |
      sh -c "apk add --no-cache inotify-tools jq && \
             echo '{\"cert_ready\":false,\"cloud_id\":\"${CLOUD_ID}\"}' > /shared/cert-status && \
             while ! ls /caddy-certs/acme*/${DOMAIN}/${DOMAIN}.crt 2>/dev/null; do sleep 2; done && \
             echo '{\"cert_ready\":true,\"cloud_id\":\"${CLOUD_ID}\"}' > /shared/cert-status && \
             tail -f /dev/null"
    volumes:
      - caddy-data:/caddy-certs:ro
      - cert-status:/shared
    environment:
      CLOUD_ID: ${CLOUD_ID}
      DOMAIN: ${DOMAIN}
```

And mount `cert-status` into `cloud-api` at `/etc/nginx/cert-status:ro`. The nginx config:

```nginx
events {}
http {
  server {
    listen 8080;
    location = /admin/health {
      default_type application/json;
      alias /etc/nginx/cert-status/cert-status;
    }
    location / { return 404; }
  }
}
```

(Yes, `alias` with a directory-mounted-as-file works for single-file mounts on Linux. Use a bind-mount on the volume so the path resolves.)

**Caveat for the agent:** `caddy:2-alpine` writes certs to `/data/caddy/certificates/acme-v02.api.letsencrypt.org-directory/${DOMAIN}/${DOMAIN}.crt`. Verify the exact path with `docker compose exec caddy ls /data/caddy/certificates/` on a successful smoke run, then update the watcher's `ls` pattern to match. **Don't trust the path I wrote above without verifying.** Caddy's directory layout has changed across major versions.

## ADR-0034 — what to write

`docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`. ~150 lines. Sections:

1. **Status / Date / Deciders** — same header style as ADR-0033.
2. **Context** — the saga (ADR-0033) needs two contracts with the user-cloud: (a) the cloud calling back into the portal after first boot, (b) the portal polling the cloud for cert-ready. Both must be specified before PORTAL-010 + PORTAL-016 can be implemented without churn.
3. **Decision §1 — registration POST payload.**
   ```json
   {
     "cloud_id":          "uuid",
     "enrollment_token":  "32-byte hex",
     "cloud_admin_token": "32-byte hex"
   }
   ```
   Endpoint: `POST ${portal_base}/api/clouds/{cloud_id}/callback`. Auth: none at HTTP layer; the `enrollment_token` is the secret. TLS is the channel guarantee. Portal validates `enrollment_token` matches the value the saga stashed in `provisioning_jobs.enrollment_token` (or in a side table — pick a name). On match, portal stores `cloud_admin_token` (plaintext per cloud-pivot §21 "low-impact damage") and flips the saga status from `awaiting_cloud_callback` to `awaiting_cert`. On mismatch, 401 + don't change status. Cloud-init retries on 5xx + network errors only; 4xx means the registration is permanently broken and the cloud-init logs `registration-failed`.
4. **Decision §2 — `/admin/health` payload.**
   ```json
   {
     "cert_ready":   true,
     "cloud_id":     "uuid",
     "api_version":  "0.1.0"     // optional; used by PORTAL-014 admin proxy compatibility checks
   }
   ```
   Endpoint: `GET ${cloud_base}/admin/health`. Auth in MVP: **none** for the `cert_ready` field (see §3 below). Portal polls every 5 s for the first minute after entering `awaiting_cert`, then every 30 s. Total budget: 30 min (DEC-003). On 30-min expiry: terminal `failed_cert`, no rollback.
5. **Decision §3 — auth on `/admin/health` is intentionally absent.** Two reasons: (a) the portal hits `/admin/health` *before* it has the cloud_admin_token in many scenarios (during `awaiting_cert` polling, but also during long-running ops where the token may rotate). (b) `cert_ready` is non-secret. Other `/admin/*` endpoints require the cloud_admin_token; `/admin/health` is the one exception. CLOUD-005 enforces this split.
6. **Decision §4 — retry cadences.** Cloud-init's registration script retries POST to portal 8 times with exponential backoff (1, 2, 4, 8, 16, 32, 64, 128 s — total ~4 min budget). After 8 fails, the cloud is "registered-broken" — surfaces in `/admin/health` as a new field `registration_status: "failed" | "pending" | "registered"`. Portal's wizard surfaces this so the user can manually retry from the UI. (The retry-from-UI flow is PORTAL-016's surface; PORTAL-010 just emits the status.)
7. **Decision §5 — what cloud-init does NOT do.** It does not handle DNS (PORTAL-010b). It does not configure the LLM model (CLOUD-007's first-boot pull). It does not run db migrations (CLOUD-002's compose-time init container handles that once it lands).
8. **Consequences** — pinning the payloads here unblocks PORTAL-010/016 from independent agents. The lack of auth on `/admin/health` is mildly problematic if a public attacker port-scans `:443/admin/health` on every Thany cloud — they learn nothing useful (just whether the cert is ready), but document the surface.
9. **Related.** ADR-0033, DEC-003, DEC-005, PORTAL-010, PORTAL-016.

## Cloud-init smoke test (the manual gate)

`scripts/cloud-init-smoke.sh`:

```bash
#!/usr/bin/env bash
set -euo pipefail

# Render the template with a known-good var set.
VARS_FILE=tests/ThanyMarcus.Portal.Tests/CloudInit/Fixtures/sample-vars.tfvars.json
RENDERED=$(mktemp --suffix=.yaml)
terraform -chdir=infra/terraform/shared console \
  <<<"templatefile(\"cloud-init.yaml.tpl\", jsondecode(file(\"../../../$VARS_FILE\")))" \
  > "$RENDERED"

# Validate cloud-init schema.
cloud-init schema --config-file "$RENDERED" --annotate

# Launch multipass VM with the rendered user-data.
VM=thany-cloud-smoke-$RANDOM
multipass launch 24.04 --name "$VM" --cpus 4 --memory 16G --disk 50G \
  --cloud-init "$RENDERED"

trap "multipass delete $VM --purge" EXIT

# Wait for cloud-init to finish.
multipass exec "$VM" -- cloud-init status --wait

# Assert /admin/health works (use staging-LE; assert eventually cert_ready: true).
ADDR=$(multipass info "$VM" --format=json | jq -r ".info.\"$VM\".ipv4[0]")
for i in $(seq 1 60); do
  if curl -fsS "http://$ADDR/admin/health" -H "Host: $(jq -r .hostname "$VARS_FILE")" \
       | jq -e '.cert_ready == true' > /dev/null; then
    echo "SMOKE OK"
    exit 0
  fi
  sleep 10
done
echo "SMOKE FAIL: cert not ready after 10 minutes" >&2
multipass exec "$VM" -- journalctl -u thany-cloud.service --no-pager
multipass exec "$VM" -- journalctl -u thany-cloud-register.service --no-pager
exit 1
```

The smoke test is **manual** (multipass needs a hypervisor; not in GitHub Actions). Document in the script's header that it's intended for local validation before merging.

For the LE-staging variant: the Caddyfile.tpl above uses prod LE by default. Add a `smoke` mode that injects `acme_ca https://acme-staging-v02.api.letsencrypt.org/directory` into the Caddyfile's global block. Pass via a terraform var `le_acme_ca` (default empty → prod; non-empty → injected verbatim). For multipass smoke tests, set it to staging — staging has higher rate limits and doesn't pollute the LE-prod database. Smoke `hostname` should be a Cloudflare subdomain the dev controls (e.g., `smoke.thany.click`).

## C# template-render test

`tests/ThanyMarcus.Portal.Tests/CloudInit/CloudInitTemplateRenderTests.cs`:

```csharp
public class CloudInitTemplateRenderTests
{
    [Fact]
    public async Task Renders_to_valid_yaml()
    {
        // Shell out to terraform console to render the template — the canonical renderer.
        var vars = await File.ReadAllTextAsync("CloudInit/Fixtures/sample-vars.tfvars.json");
        var psi = new ProcessStartInfo("terraform",
            "-chdir=../../../../infra/terraform/shared console")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        await p.StandardInput.WriteLineAsync(
            $$"""templatefile("cloud-init.yaml.tpl", jsondecode(file("{{Path.GetFullPath("CloudInit/Fixtures/sample-vars.tfvars.json").Replace("\\", "/")}}")))""");
        p.StandardInput.Close();
        var rendered = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        p.ExitCode.ShouldBe(0);

        // Strip the surrounding quotes terraform console adds for string outputs.
        rendered = rendered.Trim().Trim('"').Replace("\\n", "\n").Replace("\\\"", "\"");

        // Parse as YAML; must contain `#cloud-config` header.
        rendered.ShouldStartWith("#cloud-config");
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder().Build();
        var parsed = deserializer.Deserialize<Dictionary<object, object>>(rendered);
        parsed.ShouldContainKey("write_files");
        parsed.ShouldContainKey("runcmd");

        // Spot-check a few rendered values to make sure variables substituted.
        var sample = JsonSerializer.Deserialize<JsonElement>(vars);
        rendered.ShouldContain(sample.GetProperty("hostname").GetString()!);
        rendered.ShouldContain(sample.GetProperty("cloud_id").GetString()!);
        rendered.ShouldNotContain("${hostname}");  // no leaked templating
    }
}
```

Add `YamlDotNet` to `Directory.Packages.props` if not already present. The test requires `terraform` on PATH — fine for dev + CI (the saga-worker image already has it). Add a `[Trait("Category", "RequiresTerraform")]` so CI can skip if needed.

`sample-vars.tfvars.json` fixture:

```json
{
  "cloud_id": "00000000-0000-0000-0000-000000000001",
  "hostname": "smoke.thany.click",
  "enrollment_token": "0000000000000000000000000000000000000000000000000000000000000000",
  "portal_callback_url": "https://app.thany.click/api/clouds/00000000-0000-0000-0000-000000000001/callback",
  "le_email": "boiko+letsencrypt@example.com",
  "image_tag": "v0.0.0-smoke",
  "ghcr_pat": "",
  "admin_user": "thanyadmin",
  "ssh_public_key": "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAITESTKEYTESTKEYTESTKEYTESTKEYTESTKEYTESTKEY test@smoke",
  "compose_url": "https://raw.githubusercontent.com/example/thany-marcus/v0.0.0-smoke/infra/docker/cloud/docker-compose.yml",
  "timezone": "Etc/UTC"
}
```

## Acceptance criteria

- `cloud-init schema --config-file <rendered>` exits 0 with no warnings.
- `shellcheck infra/terraform/shared/cloud-init.yaml.tpl` ignores YAML lines but lints the embedded bash; zero errors. (Use `shellcheck -s bash -x` against an extracted bash-only file if direct linting of YAML-embedded bash is awkward.)
- `terraform fmt -check infra/terraform/shared/` exits 0 (the `.tpl` file is technically just a template, but keep it fmt-compatible for the test fixture).
- `CloudInitTemplateRenderTests` passes locally.
- `scripts/cloud-init-smoke.sh` (run manually) succeeds against a real multipass VM with staging LE: cloud-init finishes, Caddy obtains a cert for `smoke.thany.click`, `/admin/health` eventually returns `{ "cert_ready": true, ... }`, `journalctl -u thany-cloud-register` shows the registration POST hitting the portal (mock the portal endpoint with a netcat listener for the smoke run; assert the body contains the expected JSON).
- The compose scaffold `docker compose up -d` against a local working directory with a hand-rendered `.env` and `Caddyfile` brings up Caddy + nginx + cert-watcher; `curl localhost:8080/admin/health` (direct to nginx) returns valid JSON.
- ADR-0034 lands with all five decisions filled in and references from ADR-0033's "Related" section back to it (you may need to update ADR-0033 to add the back-link).
- `git grep -n "PORTAL-010" plans/` shows the dependency notes are intact (no broken references in PORTAL-007/-008/-009/-011 handoffs).
- The render test does not leak any unsubstituted `${var}` patterns in its output (catches missing template variables).

## Concrete steps in order

1. **Write ADR-0034.** Pin the payloads + cadences before touching code. Cross-reference from ADR-0033.
2. **Land `cloud-init.yaml.tpl`** with the verbatim content above. Run `cloud-init schema --config-file ...` against a hand-rendered version (substitute the variables manually with sample values).
3. **Land the compose scaffold** at `infra/docker/cloud/docker-compose.yml`, the `Caddyfile.tpl`, `cloud.env.tpl`, and `stub-admin-health/nginx.conf`. Verify locally: `cd infra/docker/cloud && (cp Caddyfile.tpl Caddyfile && cp cloud.env.tpl .env && envsubst < .env > .env.rendered ...)` — yeah, just do it once by hand to confirm the volume mounts work.
4. **Verify Caddy cert path.** Boot the scaffold against a local domain you control (use `/etc/hosts` + a real DNS A-record on a test subdomain). Run `docker compose exec caddy ls /data/caddy/certificates/` after Caddy obtains the cert. Update the `cert-watcher` sidecar's `ls` pattern to the exact path. **Do NOT skip this step** — Caddy's cert path is the contract between sidecar and Caddy; if it's wrong, `cert_ready` never flips to true and PORTAL-016's e2e test will hang.
5. **Land `scripts/cloud-init-smoke.sh`**. Run it once locally end-to-end against staging LE. Capture the runtime (~3–5 min on first boot for `apt update + docker install + image pulls`).
6. **Land the C# template-render test + fixtures.** `dotnet test --filter Category=CloudInit` should pass.
7. **Add `YamlDotNet` to `Directory.Packages.props`** if missing.
8. **Update ADR-0033's "Related" section** to point at ADR-0034 (and update the back-references in `portal-007-handoff.md` if they're load-bearing — they're not strictly required since the handoff is already merged-or-not, but it keeps the docs consistent).
9. **Run shellcheck on `register-with-portal.sh`** (extract it from the template into a standalone file for the lint, OR write a small lint helper that extracts `content: |` blocks). Document the approach in `scripts/cloud-init-smoke.sh` comments.
10. **Verify all acceptance criteria.**

## Out of scope (do not touch)

- **Real Cloud.Api implementation** — CLOUD-001.
- **Cloud-side Postgres + pgvector container** — CLOUD-002.
- **Real `/admin/health` endpoint with auth** — CLOUD-005. Our placeholder serves it from a static JSON file; the real one queries the saga state + LLM model load state + Postgres connectivity.
- **Production Caddyfile** — CLOUD-006. The `.tpl` here exists only to make cloud-init testable.
- **Cloudflare A-record creation** — PORTAL-010b. The smoke test pre-provisions DNS via a `/etc/hosts` entry (manual) or a real Cloudflare A-record (if smoke.thany.click is your dev domain).
- **Portal's inbound callback endpoint** — PORTAL-016. The smoke test mocks it with `nc -l`.
- **Image signing / cosign** — DEC-005 future work.
- **Cloud-side database migrations** — CLOUD-002.
- **Image-update flow** ("user clicks Update in portal") — Q-Updates is still open; out of scope for cloud-init's initial-boot focus.
- **VPS resize flow** — separate portal infra-op; doesn't touch cloud-init.
- **Backup / pg_dump cadence on the user's cloud** — Q12 / Q5; future work.
- **OS choice (Ubuntu 24.04 LTS is the assumption).** Don't add Debian / Fedora / etc. branches. If a later thesis variant needs Ubuntu 22.04 fallback, branch then; don't preemptively abstract.

## Risks & gotchas

- **`${var}` vs `$${var}` in terraform templates.** Single-dollar is terraform substitution; double-dollar escapes to a literal `$` in the rendered file. The bash + envsubst sections need `$${VAR}` so the rendered cloud-init contains `${VAR}` for shell expansion. **One typo and either terraform fails to render, or the cloud-init contains a literal `${VAR}` that never gets substituted.** Diff carefully; the render test catches the obvious cases but not all of them.

- **Caddy cert path drift.** Caddy 2.7+ uses `/data/caddy/certificates/acme-v02.api.letsencrypt.org-directory/<domain>/<domain>.crt`. Caddy 2.8+ may have changed. Pin the Caddy version in the compose (`caddy:2.7-alpine` rather than `caddy:2-alpine`) so the watcher's `ls` pattern stays stable. **The version pin is more important than picking the latest** — when CLOUD-006 lands, it can bump the pin and re-verify.

- **LE rate limits.** Production LE allows 50 certs per registered domain per week. Each smoke test against `*.thany.click` consumes one cert if you don't use staging. **Always use staging in smoke tests**; configure prod LE only when the smoke pipeline has been validated and you're ready to land. Document.

- **`envsubst` substitutes all `${VAR}` in the input.** If the Caddyfile.tpl or compose.yml.tpl contains a literal `${something_not_in_env}`, `envsubst` silently replaces it with an empty string. Use `envsubst '$DOMAIN $LE_EMAIL'` (single-quoted, space-separated, explicit) to limit which variables it touches. The verbatim template above already uses this pattern in the Caddyfile rendering — keep it that way.

- **`cloud-init` schema validation is strict but not exhaustive.** `cloud-init schema` validates structure but doesn't catch logical errors in `runcmd`. The multipass smoke test is the actual gate. Don't merge based on schema-only success.

- **Multipass is macOS / Linux only.** If a Windows agent picks this up, document that they should use `lxd launch` (Linux) or skip the manual smoke and rely on the C# render test + a real DO droplet provisioning run via PORTAL-008 later.

- **`thany-cloud-register.service` retries are bounded.** 8 attempts × 128s max ≈ 4 min total. If the portal is genuinely down for >4 min during the cloud's first boot, the registration fails permanently. Cloud-init exposes the failure via `/admin/health.registration_status`; the user manually retries from the portal wizard (PORTAL-016 owns that UX). **Document this in ADR-0034 §4.** Alternative: bump retries to 24 attempts (~17 min total). For thesis scope, accept the 4-min budget; portals don't go down for >4 min in MVP.

- **The portal's callback endpoint URL embeds the cloud_id in the path.** This means the cloud-init *knows* the cloud_id (it does — terraform passes it). It also means a leaked enrollment_token leaks the cloud_id. Acceptable risk per ADR-0034: enrollment_token rotates per provisioning attempt; even if leaked it only lets an attacker hijack the registration of one specific provisioning job, and only while that job is in `awaiting_cloud_callback` (≤5 min window).

- **`docker compose up -d` returns before services are healthy.** The `thany-cloud-register.service` depends on `thany-cloud.service` but compose-up just kicks off pulls + starts; the actual cert acquisition happens minutes later. The registration script polls `/admin/health` in a loop, so this is fine — but it means the apparent "cloud-init finished" timestamp is misleading. The user-visible "cloud ready" event is the portal seeing `cert_ready: true`, not cloud-init's `final_message`.

- **`apt-get update` + `apt-get install` consume bandwidth + time** (~30–90 s on a fresh DO droplet, ~60–120s on Azure). This is the slowest single step in cloud-init. Pre-baked images (custom DO snapshots / Azure custom images) would shave 2–3 min off provisioning. **Out of scope for this ticket** — but a follow-up. Open as `PORTAL-010c` in the tickets file if you want.

- **`/run/cloud-secrets/` is tmpfs by default on Ubuntu 24.04.** Verify with `findmnt /run`. If for any reason it's disk-backed, the `shred -u` matters more. Tmpfs means the secrets vanish on reboot regardless.

- **Cloud-init logs leak secrets if not careful.** `runcmd` echoes the executed command to `/var/log/cloud-init-output.log`. The `cat /run/cloud-secrets/cloud_admin_token` step would log the token if invoked directly. Using `set -a; source ...; set +a` keeps the values in shell env, not in the log. **Do not** add `set -x` for debugging in production templates; if you need it for smoke, gate behind a `debug` terraform var.

- **`docker-compose-plugin` vs `docker-compose` (legacy).** Modern Ubuntu installs `docker compose` (space, plugin) via `docker-compose-plugin`. The systemd unit uses `/usr/bin/docker compose up -d`. **Don't** install `docker-compose` (the Python-based v1); it's deprecated and Caddy's compose-up will fail with version-2-only syntax.

- **`ufw` denies inbound 8080 by default.** The compose's `cloud-api:8080` is internal to the docker bridge network; ufw is host-level and doesn't filter container-to-container traffic. Caddy on `:443` is what reaches the outside. **Do not** add `ufw allow 8080` — that would expose the placeholder nginx directly, bypassing Caddy. Verify in the smoke test that `curl http://<vm-ip>:8080/admin/health` *fails* (connection refused/timeout) while `curl https://<domain>/admin/health` succeeds.

- **`fail2ban` defaults are sane for SSH but not for HTTP.** Caddy logs to stdout (compose-captured), not to `/var/log/caddy/`; fail2ban's caddy filter doesn't match. **Don't bother enabling HTTP fail2ban for thesis scope.** Caddy + LE + ufw + sshd hardening is enough.

- **`shred -u` on tmpfs is theatrical.** Tmpfs unlinks free the memory immediately; `shred` is meaningful only for spinning disk. Keep the call as documentation of intent, not as a real wipe.

- **The compose scaffold uses anonymous volumes.** `caddy-data` survives `docker compose down`. `docker compose down -v` deletes it (and the cert). This is what we want for clean smoke tests — each multipass VM is fresh anyway.

## Definition of done

All acceptance criteria pass. `git status` shows: `infra/terraform/shared/cloud-init.yaml.tpl`, `infra/docker/cloud/{docker-compose.yml, Caddyfile.tpl, cloud.env.tpl, stub-admin-health/}`, `scripts/cloud-init-smoke.sh`, `docs/decisions/0034-cloud-bootstrap-and-portal-handshake.md`, the test fixtures + render test, and `Directory.Packages.props` if `YamlDotNet` was added. A multipass smoke run against staging LE produces a cloud that POSTs to a mock portal endpoint with the expected JSON within ~5 minutes of cloud-init kickoff.

A fresh agent picking up **PORTAL-008** (DO terraform module) from this state knows:
- The cloud-init template lives at `infra/terraform/shared/cloud-init.yaml.tpl`.
- It's rendered via `templatefile("${path.module}/../shared/cloud-init.yaml.tpl", { ... })` with the variable set documented above.
- The result goes into `digitalocean_droplet.user_data`.
- The module is responsible for generating `enrollment_token` (e.g., `random_id`), `ssh_public_key` (`tls_private_key`), and constructing `portal_callback_url` from a portal-base variable + `cloud_id`.

A fresh agent picking up **PORTAL-009** (Azure terraform module) knows the same, except `azurerm_linux_virtual_machine.custom_data = base64encode(templatefile(...))` (Azure requires base64-encoded user-data; DO does not).

A fresh agent picking up **PORTAL-016** (inbound callback endpoint) knows:
- Endpoint: `POST /api/clouds/{cloud_id}/callback`. Path param is the source of truth for cloud_id; body's `cloud_id` field is redundant but validated to match (defensive).
- Body: `{ cloud_id, enrollment_token, cloud_admin_token }`.
- Validates `enrollment_token` against the value stashed when the saga entered `awaiting_cloud_callback`.
- On match: store `cloud_admin_token` plaintext on the `clouds` row, flip `provisioning_jobs.status` to `awaiting_cert`, append to `events_log`, return 200.
- On mismatch: 401, do not flip status. Cloud-init does not retry on 4xx.

A fresh agent picking up **CLOUD-001** (Cloud.Api scaffold + compose stack) knows:
- The `infra/docker/cloud/docker-compose.yml` scaffold from this ticket is intentionally minimal. CLOUD-001 replaces the `nginx:alpine` placeholder with the real `ghcr.io/<owner>/thany-marcus-cloud-api:${IMAGE_TAG}` service.
- The Caddyfile's reverse-proxy targets (`/admin/*`, `/api/*` → `cloud-api:8080`) are the contract; the real service must listen on 8080 inside the compose network.
- The `cert-watcher` sidecar can go away once the real Cloud.Api implements `/admin/health` with a proper cert-status probe (e.g., reading from Caddy's admin API).

A fresh agent picking up **CLOUD-006** (Caddy + LE) knows:
- The `Caddyfile.tpl` shipped here is the minimal viable shape. Real Caddy config (timeouts, headers, gzip+brotli, request-size limits, security headers) is CLOUD-006's surface.

## Cross-references

- **PORTAL-007** — saga's `AwaitingCloudCallbackHandler` + `AwaitingCertHandler` consume this ticket's contracts. ADR-0033 cites ADR-0034 (to be written here) for payloads.
- **PORTAL-008 / PORTAL-009** — direct consumers; they call `templatefile("../shared/cloud-init.yaml.tpl", ...)`.
- **PORTAL-010b** — Cloudflare DNS client. Independent code-path; both must be done before PORTAL-011 lights up.
- **PORTAL-011** — wizard UI. Doesn't directly consume the cloud-init, but its progress phase reads `events_log` entries the saga writes after the cloud's registration POST lands.
- **PORTAL-016** — inbound callback endpoint. Contract pinned by ADR-0034.
- **CLOUD-001 / CLOUD-005 / CLOUD-006** — the cloud-side services that replace this ticket's placeholder scaffolds.
- **DEC-001 / DEC-003 / DEC-005** — VPS spec, LE retry cadence, GHCR image registry.
- **ADR-0033** — saga + worker; the upstream contract.
- **ADR-0034** — new, written as part of this ticket.
- **`infra/legacy/infrastructure/templates/cloudinit.tpl`** — reference template; the hardening blocks (sshd, ufw, fail2ban, unattended-upgrades, Docker apt-source) are lifted verbatim.
