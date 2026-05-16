# PORTAL-008 DigitalOcean terraform module — Handoff Brief

**Goal:** land the real `digitalocean/` terraform module under `infra/docker/saga-worker/terraform-modules/digitalocean/` and the small `WorkspaceLayout` + `entrypoint.sh` changes that wire it into the saga. After this ticket, inserting a `pending` row with `cloud.Provider = "digitalocean"` and valid credentials drives the PORTAL-007 saga through `tf_planning → tf_applying → dns_creating → awaiting_cloud_callback` against a real DO API — provisioning a droplet, attached volume, and firewall. Cloud-init (the bootstrap script that runs *on* the droplet) is a placeholder in this ticket; **PORTAL-010 fills in its body**. The saga's `awaiting_cloud_callback` phase will time out because no PORTAL-010 cloud-init exists yet to call back — that's expected and documented in the integration tests.

Estimated **0.75 person-day** with heavy AI-agent assistance. The original ticket-list estimate (`tickets-2026-05-13.md` line 86) was 0.75 days; that holds — PORTAL-007 did the heavy structural work, and this ticket is workmanlike: a four-resource terraform module + a contained WorkspaceLayout branch.

## Where decisions live (read before doing anything)

- **`plans/portal-007-handoff.md`** — prerequisite. Defines the contract this module must match: variables `cloud_id`, `region`, `size`, `hostname` from the rendered root `main.tf`; sensitive `provider_token`, `enrollment_token`, `ghcr_pat` from `TF_VAR_*` env vars; required output `ip`. The `WorkspaceLayout` switch on `cloud.Provider` already routes `"digitalocean"` to `/app/terraform-modules/digitalocean/`; this ticket fills in what that path resolves to.
- **`docs/decisions/0033-provisioning-saga-and-worker.md`** — the saga's canonical spec. §"Provider modules" describes the per-provider modules baked into the worker image at `/app/terraform-modules/<provider>/` and the templated root `main.tf` that calls `module "cloud" { source = "/app/terraform-modules/digitalocean" ... }`. **Don't violate the contract specified there.**
- **`plans/tickets-2026-05-13.md`** line 86 — the original ticket line:
  > PORTAL-008 | DO Terraform module: droplet, volume, firewall | 0.75 | PORTAL-011, PORTAL-015, PORTAL-010b | Inputs: API token, size, region, ssh_key, user_data. DNS handled separately via Cloudflare client (PORTAL-010b).
  
  Note: the ticket line predates ADR-0033. The actual contract is in ADR-0033 + PORTAL-007's handoff; **the ticket-line "ssh_key" input is dropped** (see "Risks & gotchas" below for why we don't manage SSH keys in MVP).
- **`plans/tickets-2026-05-13.md`** line 54 — **DEC-001**: 16 GB / 4 vCPU default; DO `s-4vcpu-16gb` (~$84/mo). The `size` variable receives this value from the saga; the module doesn't pick it.
- **`plans/cloud-pivot-plan-2026-05-13.md`** §22.5 — full provisioning flow context: portal pre-creates a Cloudflare A-record placeholder before terraform applies, terraform creates the droplet, cloud-init brings up Docker Compose + Caddy + LE, cloud POSTs back via `enrollment_token`. **PORTAL-008 only builds the droplet + volume + firewall.** Cloudflare DNS is PORTAL-010b; cloud-init is PORTAL-010; the callback endpoint is PORTAL-016.
- **`plans/portal-007a-handoff.md`** — establishes the SagaWorker image, the `terraform_data` volume mount at `/var/lib/portal/terraform`, the terraform binary baked into the image, and the docker-compose stack. PORTAL-008 modifies the **plugin-cache pre-warm** step in `entrypoint.sh` (the loop introduced by PORTAL-007) but doesn't otherwise touch the Dockerfile.
- **Memory files**: `portal_architecture.md` (Postgres job queue + SSE), `portal_deployment.md` (single VM, Docker Compose), `portal_tooling.md` (.NET 10, warnings-as-errors, Shouldly + xUnit v3). No new tooling — this ticket is mostly HCL.

**Do not invent the variable surface.** It's pinned by PORTAL-007's `WorkspaceLayout` rendering. If a variable isn't already passed by the rendered root `main.tf`, either (a) extend `WorkspaceLayout` here in PORTAL-008 — with the matching test — or (b) confirm the variable belongs to a different ticket.

## Scope boundary (precise)

**In scope:**
- `infra/docker/saga-worker/terraform-modules/digitalocean/` — the new module:
  - `versions.tf` — `required_providers { digitalocean = { source = "digitalocean/digitalocean", version = "~> 2.0" } }`. No `provider` block — the root passes the configured provider down (Terraform's implicit provider inheritance).
  - `variables.tf` — module inputs.
  - `main.tf` — `digitalocean_droplet` + `digitalocean_volume` + `digitalocean_volume_attachment` + `digitalocean_firewall`.
  - `outputs.tf` — `output "ip"` (the droplet's public IPv4).
- `infra/docker/saga-worker/terraform-modules/shared/cloud-init.sh.tpl` — **placeholder** for PORTAL-010. Inert script (`#!/bin/bash\necho "PORTAL-010 will fill this in"; exit 0`) but with the *full template-variable contract* declared so the `templatefile()` call in `main.tf` validates today. PORTAL-010 replaces the body; the variable set is frozen here.
- `WorkspaceLayout.cs` updates:
  - Branch by provider on the `terraform { required_providers { ... } }` block.
  - Add `provider "digitalocean" { token = var.provider_token }` to the rendered root `main.tf` when `cloud.Provider == "digitalocean"`.
  - Pass `enrollment_token`, `ghcr_pat`, `portal_url` through to the child module call.
  - Emit `portal_url` to `variables.auto.tfvars` (non-sensitive; read from `IConfiguration["Provisioning:PortalUrl"]`).
  - The `"stub"` rendering path stays identical to PORTAL-007's — no `required_providers`, no extra vars passed.
- `infra/docker/saga-worker/entrypoint.sh` plugin-cache fix:
  - Change `source = "hashicorp/digitalocean"` to `source = "digitalocean/digitalocean"` (and `hashicorp/cloudflare` to `cloudflare/cloudflare` — Azure stays `hashicorp/azurerm`). PORTAL-007's entrypoint mis-specifies these; the warmer silently failed for DO/CF (the placeholder `terraform init` swallowed the error with `|| true`). **This is the one PORTAL-007 carryover this ticket fixes.**
  - Bump the `.warmed` marker filename to `.warmed-v2` so existing volumes re-warm with the correct registry sources.
- `Provisioning:PortalUrl` added to `appsettings.json` / `appsettings.Development.json` in `Portal.SagaWorker`. Default: `https://api.thany.click`. Dev: `http://host.docker.internal:5000` (the worker calls *out* to the portal? No — the *cloud* calls portal; this is the URL the cloud will hit. Dev value should be the *publicly-reachable portal URL*, which for thesis-scope dev is usually a tunnel like ngrok or the prod URL itself).
- Tests:
  - `WorkspaceLayoutTests` — extend existing test from PORTAL-007 to cover the `digitalocean` rendering path (expected `main.tf` shape including the `required_providers` block, the `provider "digitalocean"` block, and the module call with the three sensitive args + `portal_url`).
  - `DigitalOceanModuleValidationTests` — new test class. Runs `terraform init -backend=false` + `terraform validate` against the module in a temp workdir, using `Testcontainers.PostgreSql` only if a backend is configured (here we skip the backend with `-backend=false`). Asserts `Success`. Provides a one-time guard against syntax/type errors in HCL.
  - `DigitalOceanModulePlanTests` — runs `terraform plan` with `DO_FAKE_TOKEN=dop_v1_fake` against the module. The plan will *fail* at the API call stage (token is invalid), but it will succeed in validating variable types, computing the resource graph, and producing a structured error. Assert the error message mentions "401" or "Unauthorized" — proves the module reached DO's API with our variables wired correctly. **Skip in CI by default** via `[Trait("Category", "ManualDigitalOcean")]`; intended for manual smoke before merge.
  - Optional (gated behind `DO_TF_LIVE_TOKEN` env var): a `DigitalOceanLiveApplyTest` that runs a real `apply → destroy` cycle against the developer's DO account. Document the env var and the ~$0.10 cost (a 16GB droplet for 5 minutes). Default-skipped.

**Out of scope (DO NOT touch):**
- **Cloud-init script body** — PORTAL-010. This ticket lands `cloud-init.sh.tpl` with placeholder body + frozen variable contract; PORTAL-010 fills the body.
- **Cloudflare DNS API** — PORTAL-010b. The saga's `DnsCreatingHandler` calls `StubCloudflareDnsClient`; PORTAL-010b swaps it for the real impl. PORTAL-008 doesn't touch DNS.
- **Azure module** — PORTAL-009. Mirrors this ticket's shape (mostly): `azurerm_resource_group + virtual_network + network_security_group + public_ip + linux_virtual_machine`. Out of scope here.
- **Wizard / cloud-create endpoint** — PORTAL-011. The endpoint validates `Cloud.Region` against DO's allowed list (we publish the list in `Cloud.Region` validation logic for PORTAL-011 to consume — see "Region validation contract" below).
- **Destroy endpoint** — PORTAL-015. The DO module's `destroy` works automatically via terraform-state lifecycle; no module changes needed.
- **Real `DO_TF_LIVE_TOKEN` provisioning in CI** — leave that as a manual pre-merge check. The thesis project doesn't have a CI budget for DO credentials.
- **DO droplet image versioning** — pin to `ubuntu-24-04-x64` for now. Re-pinning when a new Ubuntu LTS lands is a follow-up, not this ticket.
- **`provisioning_jobs.tf_outputs` schema changes** — already `jsonb` per PORTAL-007. The DO module's outputs flow into it unchanged.

## Output of PORTAL-008 — final directory state

```
Thany-Marcus/
├── src/
│   └── ThanyMarcus.Portal.SagaWorker/
│       ├── Features/Provisioning/
│       │   └── WorkspaceLayout.cs                              # CHANGED: branch by provider; add DO required_providers + provider block; pass portal_url + sensitive vars
│       └── appsettings.json                                    # CHANGED: Provisioning:PortalUrl
├── infra/docker/saga-worker/
│   ├── entrypoint.sh                                           # CHANGED: fix registry source addresses; bump .warmed marker
│   └── terraform-modules/
│       ├── digitalocean/                                       # NEW
│       │   ├── versions.tf                                     # NEW
│       │   ├── variables.tf                                    # NEW
│       │   ├── main.tf                                         # NEW
│       │   └── outputs.tf                                      # NEW
│       └── shared/                                             # NEW
│           └── cloud-init.sh.tpl                               # NEW: placeholder + frozen variable contract
└── tests/ThanyMarcus.Portal.Tests/
    └── SagaWorker/
        ├── WorkspaceLayoutTests.cs                             # CHANGED: add digitalocean rendering case
        ├── Terraform/
        │   ├── DigitalOceanModuleValidationTests.cs            # NEW: terraform init -backend=false + validate
        │   ├── DigitalOceanModulePlanTests.cs                  # NEW: terraform plan against fake token; expects 401
        │   └── DigitalOceanLiveApplyTest.cs                    # NEW: gated by DO_TF_LIVE_TOKEN; manual smoke
        └── Provisioning/
            └── CloudInitTemplateVariablesTests.cs              # NEW: asserts the template variables passed to cloud-init.sh.tpl match the frozen contract
```

## Packages

- No new NuGet packages. The terraform module is HCL.
- `Testcontainers.PostgreSql` already referenced from PORTAL-007's saga end-to-end tests; the module tests reuse the same fixture only if a backend is needed (the validate path uses `-backend=false`).
- The HCL module pins `digitalocean/digitalocean ~> 2.0`. Current major version is 2.x; `~> 2.0` allows 2.x patch + minor updates, blocks 3.0.

## The DO module — `versions.tf`

`infra/docker/saga-worker/terraform-modules/digitalocean/versions.tf`:

```hcl
terraform {
  required_version = ">= 1.6"
  required_providers {
    digitalocean = {
      source  = "digitalocean/digitalocean"
      version = "~> 2.0"
    }
  }
}
```

**Notes:**
- No `provider "digitalocean"` block in the child module. The root `main.tf` (rendered by `WorkspaceLayout`) declares the configured provider; child modules inherit it implicitly. This is the standard Terraform pattern and lets the saga's stub-vs-real provider distinction live entirely at the root.
- Pinning `version = "~> 2.0"` matches the plugin-cache pre-warm contract (the warmer requests `digitalocean/digitalocean` with no version constraint — the latest 2.x lands in the cache).

## The DO module — `variables.tf`

```hcl
variable "cloud_id" {
  description = "Portal-side UUID for this cloud. Stamped as a tag on every resource for cross-system correlation."
  type        = string
}

variable "region" {
  description = "DO region slug (e.g. fra1, nyc3). Validated portal-side; see Region validation contract."
  type        = string
}

variable "size" {
  description = "DO droplet size slug. Default per DEC-001: s-4vcpu-16gb."
  type        = string
}

variable "hostname" {
  description = "Fully-qualified hostname (e.g. abc12345.thany.click). Used for droplet name + cloud-init bootstrap + Caddy/LE config."
  type        = string
}

variable "enrollment_token" {
  description = "One-shot token the cloud uses to register itself with the portal on first boot. Passed into cloud-init via TF_VAR_enrollment_token; never written to tfvars."
  type        = string
  sensitive   = true
}

variable "ghcr_pat" {
  description = "GitHub Container Registry PAT for image pulls. Public images don't require auth, but a PAT lifts the unauthenticated rate-limit. Passed via TF_VAR_ghcr_pat."
  type        = string
  sensitive   = true
}

variable "portal_url" {
  description = "Base URL of the portal API (e.g. https://api.thany.click). Cloud-init POSTs the cloud_admin_token here on first boot."
  type        = string
}

variable "volume_size_gb" {
  description = "Attached volume size in GB. Default 50 — fits ~150-200 artifacts including audio/images for thesis scope."
  type        = number
  default     = 50
}

variable "image_slug" {
  description = "DO image slug. Pinned to ubuntu-24-04-x64; a follow-up re-pins when 26.04 LTS ships."
  type        = string
  default     = "ubuntu-24-04-x64"
}
```

## The DO module — `main.tf`

```hcl
locals {
  resource_name = "thany-${substr(var.cloud_id, 0, 8)}"
  tags          = ["thany-marcus", "cloud-id-${var.cloud_id}", "managed-by-portal"]
}

resource "digitalocean_volume" "data" {
  name                    = "${local.resource_name}-data"
  region                  = var.region
  size                    = var.volume_size_gb
  initial_filesystem_type = "ext4"
  description             = "Artifact + Postgres storage for thany-marcus cloud ${var.cloud_id}"
  tags                    = local.tags
}

resource "digitalocean_droplet" "cloud" {
  name        = local.resource_name
  region      = var.region
  size        = var.size
  image       = var.image_slug
  ipv6        = false
  monitoring  = true
  tags        = local.tags

  user_data = templatefile("${path.module}/../shared/cloud-init.sh.tpl", {
    cloud_id          = var.cloud_id
    hostname          = var.hostname
    portal_url        = var.portal_url
    enrollment_token  = var.enrollment_token
    ghcr_pat          = var.ghcr_pat
  })

  lifecycle {
    ignore_changes = [user_data]
  }
}

resource "digitalocean_volume_attachment" "data" {
  droplet_id = digitalocean_droplet.cloud.id
  volume_id  = digitalocean_volume.data.id
}

resource "digitalocean_firewall" "cloud" {
  name        = "${local.resource_name}-fw"
  droplet_ids = [digitalocean_droplet.cloud.id]
  tags        = local.tags

  inbound_rule {
    protocol         = "tcp"
    port_range       = "80"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  inbound_rule {
    protocol         = "tcp"
    port_range       = "443"
    source_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "tcp"
    port_range            = "1-65535"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "udp"
    port_range            = "1-65535"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }

  outbound_rule {
    protocol              = "icmp"
    destination_addresses = ["0.0.0.0/0", "::/0"]
  }
}
```

**Notes:**
- **No SSH key, no SSH port.** The droplet is intentionally inaccessible via SSH. Cloud-init runs via DO's user-data mechanism (not SSH). If a user genuinely needs root console access, DO emails a root password to the account holder when no SSH key is supplied; that's the emergency path. Documented as a thesis-scope tradeoff in "Risks & gotchas".
- **`ipv6 = false`**: simplifies Cloudflare DNS (single A-record, no AAAA). Future work if dual-stack matters.
- **`monitoring = true`**: enables DO's free droplet metrics. No additional cost; useful for the thesis dashboard.
- **`lifecycle { ignore_changes = [user_data] }`**: cloud-init runs *once* at first boot. After that, any drift in the rendered user_data (e.g., portal_url changes) shouldn't trigger droplet recreation — it would wipe the cloud. Update path for portal_url changes lives in PORTAL-015's destroy-then-recreate flow.
- **`local.tags`**: every resource carries the `cloud_id` tag so the portal can correlate across DO's UI/API if it ever needs to outside the terraform state.
- **The `templatefile()` path** is `${path.module}/../shared/cloud-init.sh.tpl` — sibling-of-module location. Means PORTAL-009 (Azure) reuses the same template for `custom_data`, ensuring the bootstrap script is provider-agnostic per cloud-pivot-plan §22.5 "shared/ # cloud-init script template (provider-agnostic)".

## The DO module — `outputs.tf`

```hcl
output "ip" {
  description = "Droplet public IPv4 address. PORTAL-007's WorkspaceLayout rendering forwards this as the root module's ip output, which DnsCreatingHandler reads to set up the Cloudflare A-record."
  value       = digitalocean_droplet.cloud.ipv4_address
}

output "droplet_id" {
  description = "DO droplet numeric id. Surfaced for debugging; not consumed by the saga."
  value       = digitalocean_droplet.cloud.id
}

output "volume_id" {
  description = "DO volume id. Surfaced for debugging; not consumed by the saga."
  value       = digitalocean_volume.data.id
}
```

**The root module must forward `ip` as its top-level output.** PORTAL-007's `WorkspaceLayout.RenderAsync` already emits `output "ip" { value = module.cloud.ip }` — no change needed there; just verify it's still in the rendered root `main.tf` after the WorkspaceLayout changes below.

## Cloud-init placeholder — frozen variable contract

`infra/docker/saga-worker/terraform-modules/shared/cloud-init.sh.tpl`:

```bash
#!/bin/bash
#
# PORTAL-008 placeholder. PORTAL-010 replaces the body of this file
# with the real bootstrap script. The template-variable contract below
# is frozen by PORTAL-008 — PORTAL-010 may consume these variables but
# may NOT add new ones without updating WorkspaceLayout + this template
# in lockstep.
#
# Variables (interpolated by Terraform's templatefile() before delivery
# to the droplet's user-data — these are NOT shell vars; they're replaced
# at terraform-plan time):
#   $${cloud_id}          — portal-side UUID for this cloud
#   $${hostname}          — fully-qualified hostname (e.g. abc12345.thany.click)
#   $${portal_url}        — portal API base URL (e.g. https://api.thany.click)
#   $${enrollment_token}  — one-shot token for cloud→portal first-boot registration
#   $${ghcr_pat}          — GHCR PAT for image pulls (rate-limit bypass)

set -euo pipefail

echo "[cloud-init] PORTAL-008 placeholder for cloud_id=${cloud_id} hostname=${hostname}"
echo "[cloud-init] portal_url=${portal_url}"
echo "[cloud-init] enrollment_token starts with: $${enrollment_token:0:8}..."
echo "[cloud-init] ghcr_pat starts with: $${ghcr_pat:0:8}..."
echo "[cloud-init] PORTAL-010 will replace this with the real Docker Compose bootstrap."
exit 0
```

**HCL escaping reminder:** in `templatefile()`, `${name}` is a Terraform interpolation, and `$${name}` is a literal `${name}` (a shell var, untouched by terraform). The placeholder above uses `${name}` for terraform-interpolated values and `$${...}` for shell substring expressions to demonstrate both escaping rules. **PORTAL-010's real script must follow the same convention.**

**Why a placeholder and not skip the file:** the `templatefile()` call in `main.tf` is *evaluated at plan time*. If the file doesn't exist, `terraform plan` fails with `Could not read file`. Landing the placeholder lets PORTAL-008's `terraform validate` + `terraform plan` tests pass without depending on PORTAL-010.

## WorkspaceLayout updates

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/WorkspaceLayout.cs`:

Replace the `RenderAsync` body's main.tf generation block. Old shape (PORTAL-007) hardcoded the variable list and module call regardless of provider. New shape branches on `cloud.Provider`:

```csharp
public async Task<string> RenderAsync(ProvisioningJob job, Cloud cloud, CancellationToken ct)
{
    var dir = Path.Combine(BaseDir, job.Id.ToString());
    Directory.CreateDirectory(dir);

    var (modulePath, requiredProviders, providerBlock, extraModuleArgs) = cloud.Provider switch
    {
        "digitalocean" => (
            "/app/terraform-modules/digitalocean",
            """
            required_providers {
              digitalocean = { source = "digitalocean/digitalocean", version = "~> 2.0" }
            }
            """,
            """
            provider "digitalocean" {
              token = var.provider_token
            }
            """,
            """
              enrollment_token = var.enrollment_token
              ghcr_pat         = var.ghcr_pat
              portal_url       = var.portal_url
            """
        ),
        "azure" => (
            "/app/terraform-modules/azure",
            // PORTAL-009 fills these in. For now, throw — the only provider this ticket validates is digitalocean.
            throw new NotImplementedException("Azure provider lands in PORTAL-009"),
            "", ""
        ),
        "stub" => (
            "/app/terraform-modules/stub",
            // No required_providers, no provider block — keeps stub init fast and offline.
            "",
            "",
            ""
        ),
        _ => throw new InvalidOperationException($"Unknown provider: {cloud.Provider}"),
    };

    var sensitiveVarBlock = cloud.Provider == "stub" ? "" : """
        variable "provider_token"    { type = string, sensitive = true }
        variable "enrollment_token"  { type = string, sensitive = true }
        variable "ghcr_pat"          { type = string, sensitive = true }
        variable "portal_url"        { type = string }
        """;

    var rootMainTf = $$"""
        terraform {
          required_version = ">= 1.6"
          {{requiredProviders}}
        }
        {{providerBlock}}
        module "cloud" {
          source         = "{{modulePath}}"
          cloud_id       = var.cloud_id
          region         = var.region
          size           = var.size
          hostname       = var.hostname
        {{extraModuleArgs}}
        }
        variable "cloud_id" { type = string }
        variable "region"   { type = string }
        variable "size"     { type = string }
        variable "hostname" { type = string }
        {{sensitiveVarBlock}}
        output "ip" { value = module.cloud.ip }
        """;

    await File.WriteAllTextAsync(Path.Combine(dir, "main.tf"), rootMainTf, ct);
    await File.WriteAllTextAsync(Path.Combine(dir, "backend.tf"),
        "terraform { backend \"pg\" {} }\n", ct);

    var portalUrl = config["Provisioning:PortalUrl"]
        ?? throw new InvalidOperationException("Provisioning:PortalUrl is not configured");

    var tfvars = cloud.Provider == "stub"
        ? $$"""
            cloud_id = "{{cloud.Id}}"
            region   = "{{cloud.Region}}"
            size     = "{{config["Provisioning:DefaultSize"]}}"
            hostname = "{{cloud.Hostname}}"
            """
        : $$"""
            cloud_id   = "{{cloud.Id}}"
            region     = "{{cloud.Region}}"
            size       = "{{config["Provisioning:DefaultSize"]}}"
            hostname   = "{{cloud.Hostname}}"
            portal_url = "{{portalUrl}}"
            """;

    await File.WriteAllTextAsync(Path.Combine(dir, "variables.auto.tfvars"), tfvars, ct);

    return dir;
}
```

**Notes:**
- `portal_url` is in `variables.auto.tfvars` — non-sensitive, fine on disk. The three sensitive vars stay in `TF_VAR_*` env vars per PORTAL-007's `TfPlanningHandler`. **Verify `TfPlanningHandler` is not modified by this ticket** — it already wires the three sensitive vars from the unlocked DEK + ProviderTokenVault.
- The `azure` branch throws `NotImplementedException`. PORTAL-009 replaces the throw with the real Azure path. Tests in this ticket don't exercise the Azure path; PORTAL-009's tests will.
- Triple-quoted raw strings (C# 11+) avoid HCL's `{{` escaping pain. `$$"""..."""` doubles the brace count for interpolation; literal `{`/`}` stay single. Verify your editor's syntax highlighting tolerates this; the code compiles either way.
- `WorkspaceLayoutTests` from PORTAL-007 must be extended to cover the `digitalocean` rendering case: assert the rendered `main.tf` contains the `required_providers { digitalocean = ... }` block, the `provider "digitalocean"` block, and the three extra module arguments. Keep the existing `stub` case green.

## entrypoint.sh fix

`infra/docker/saga-worker/entrypoint.sh` — change the plugin-cache pre-warm loop:

```bash
#!/usr/bin/env bash
set -euo pipefail

CACHE_DIR=/var/lib/portal/terraform/plugin-cache
MARKER="$CACHE_DIR/.warmed-v2"
mkdir -p "$CACHE_DIR"

if [ ! -f "$MARKER" ]; then
    echo "[entrypoint] Pre-warming terraform plugin cache (v2: correct registry sources)..."
    for tuple in "digitalocean/digitalocean" "hashicorp/azurerm" "cloudflare/cloudflare"; do
        provider_name="$(echo "$tuple" | cut -d/ -f2)"
        workdir=$(mktemp -d)
        cat > "$workdir/main.tf" <<EOF
terraform {
  required_providers {
    $provider_name = { source = "$tuple" }
  }
}
EOF
        TF_PLUGIN_CACHE_DIR="$CACHE_DIR" terraform -chdir="$workdir" init -input=false -no-color
        rm -rf "$workdir"
    done
    touch "$MARKER"
    echo "[entrypoint] Plugin cache warmed"
else
    echo "[entrypoint] Plugin cache already warmed (v2); skipping"
fi

exec dotnet ThanyMarcus.Portal.SagaWorker.dll
```

**Changes vs. PORTAL-007's entrypoint:**
- Loop variable becomes `"namespace/provider"` tuples; the `source =` attribute uses the full tuple instead of `hashicorp/$provider`. Fixes the silent download failures for DO + Cloudflare.
- Marker becomes `.warmed-v2` so existing dev volumes re-warm without manual cleanup. **Don't delete the old `.warmed` marker** — it's harmless; the v2 marker is independent.
- Drop the `|| true` on `terraform init`. PORTAL-007's `|| true` masked the bug being fixed here; we want failures to be loud now that the registry sources are correct.

## Region validation contract (for PORTAL-011 to consume)

The `cloud.Region` value must be a DO-supported region slug. PORTAL-011's cloud-create endpoint validates input against an allowlist. Land the allowlist here so PORTAL-011 has a clean import:

`src/ThanyMarcus.Portal.SagaWorker/Features/Provisioning/DigitalOceanRegions.cs` (or in `Portal.Api/Features/Provisioning/` — same project-reference rationale as `SagaStatus`):

```csharp
namespace ThanyMarcus.Portal.SagaWorker.Features.Provisioning;

public static class DigitalOceanRegions
{
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "nyc1", "nyc3",
        "sfo3",
        "ams3",
        "sgp1",
        "lon1",
        "fra1",
        "tor1",
        "blr1",
        "syd1",
    };

    public static bool IsAllowed(string region) => Allowed.Contains(region);
}
```

PORTAL-011's endpoint imports `DigitalOceanRegions.IsAllowed(region)` for input validation. **The current set tracks DO's GA-as-of-2026-05 region list.** If DO adds a region, update this set; the terraform module doesn't need a code change.

## appsettings.json — `Provisioning:PortalUrl`

`src/ThanyMarcus.Portal.SagaWorker/appsettings.json`:

```json
{
  "Provisioning": {
    "PortalUrl":     "https://api.thany.click",
    "DefaultSize":   "s-4vcpu-16gb",
    "WorkspaceBase": "/var/lib/portal/terraform"
  }
}
```

`appsettings.Development.json` (or environment override): `PortalUrl` points to whatever the developer's externally-reachable portal endpoint is. For local-only end-to-end testing with no real DO calls, the `stub` provider path doesn't read `PortalUrl` (the stub's tfvars block omits it) — so dev workflow stays the same as PORTAL-007.

## Tests

### `WorkspaceLayoutTests` — extend

Add a new test case to the existing class:

```csharp
[Fact]
public async Task RenderAsync_DigitalOcean_EmitsRequiredProvidersAndProviderBlock()
{
    var cloud = new Cloud { Provider = "digitalocean", Region = "fra1", Hostname = "abc12345.thany.click", Id = Guid.NewGuid() };
    var job   = new ProvisioningJob { Id = Guid.NewGuid(), CloudId = cloud.Id };
    var dir   = await sut.RenderAsync(job, cloud, default);

    var mainTf = await File.ReadAllTextAsync(Path.Combine(dir, "main.tf"));
    mainTf.ShouldContain("required_providers");
    mainTf.ShouldContain("source  = \"digitalocean/digitalocean\"");
    mainTf.ShouldContain("provider \"digitalocean\"");
    mainTf.ShouldContain("token = var.provider_token");
    mainTf.ShouldContain("enrollment_token = var.enrollment_token");
    mainTf.ShouldContain("ghcr_pat         = var.ghcr_pat");
    mainTf.ShouldContain("portal_url       = var.portal_url");
    mainTf.ShouldContain("output \"ip\" { value = module.cloud.ip }");

    var tfvars = await File.ReadAllTextAsync(Path.Combine(dir, "variables.auto.tfvars"));
    tfvars.ShouldContain("portal_url = \"https://test.example/\"");
}

[Fact]
public async Task RenderAsync_Stub_DoesNotEmitRequiredProviders()
{
    // existing PORTAL-007 test stays green
}
```

### `DigitalOceanModuleValidationTests` — new

```csharp
public sealed class DigitalOceanModuleValidationTests
{
    [Fact]
    public async Task ModuleValidatesCleanly()
    {
        var modulePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../infra/docker/saga-worker/terraform-modules/digitalocean"));
        var workdir = Directory.CreateTempSubdirectory("do-tf-validate-").FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workdir, "main.tf"), $$"""
                module "do" {
                  source           = "{{modulePath}}"
                  cloud_id         = "00000000-0000-0000-0000-000000000000"
                  region           = "fra1"
                  size             = "s-4vcpu-16gb"
                  hostname         = "test.thany.click"
                  enrollment_token = "fake"
                  ghcr_pat         = "fake"
                  portal_url       = "https://example"
                }
                """);
            var init = await Process("terraform", new[] { "init", "-backend=false", "-input=false", "-no-color" }, workdir);
            init.ExitCode.ShouldBe(0);
            var validate = await Process("terraform", new[] { "validate", "-no-color" }, workdir);
            validate.ExitCode.ShouldBe(0);
        }
        finally
        {
            Directory.Delete(workdir, recursive: true);
        }
    }
}
```

**Why `-backend=false`:** validates HCL syntax + variable wiring + provider compatibility without touching the pg backend. Fast (~3-5 s with warmed cache), no external dependencies beyond the terraform binary.

### `DigitalOceanModulePlanTests` — new, gated

```csharp
[Trait("Category", "ManualDigitalOcean")]
public sealed class DigitalOceanModulePlanTests
{
    [Fact]
    public async Task PlanReachesDOApi_FailsWith401_OnFakeToken()
    {
        // ... same setup as ValidationTests, but run `terraform plan` with TF_VAR_provider_token=dop_v1_fake_xxxxxxxxxx
        // ... assert ExitCode != 0 and stderr contains "401" or "Unauthorized"
    }
}
```

xUnit v3 filters out `[Trait("Category", "ManualDigitalOcean")]` from the default test run via the project's `xunit.runner.json` or a per-target filter in `Directory.Build.props`. **Document the manual run command in the ticket-complete checklist.**

### `DigitalOceanLiveApplyTest` — opt-in via env var

```csharp
[Trait("Category", "LiveDigitalOcean")]
public sealed class DigitalOceanLiveApplyTest
{
    [SkippableFact]
    public async Task FullProvisionAndDestroyCycle()
    {
        var liveToken = Environment.GetEnvironmentVariable("DO_TF_LIVE_TOKEN");
        Skip.If(string.IsNullOrWhiteSpace(liveToken), "DO_TF_LIVE_TOKEN not set; skipping live test (cost ~$0.10)");

        // ... apply, assert ip is non-null and reachable on port 80 (well, will be later — for now just non-null)
        // ... destroy in a finally block
    }
}
```

Use `Xunit.SkippableFact` or the xUnit v3 conditional-skip equivalent. **Always destroy in `finally`**, even on test failure — abandoned droplets cost real money.

### `CloudInitTemplateVariablesTests` — new

```csharp
[Fact]
public void CloudInitTemplate_DeclaresExactlyTheFrozenVariableSet()
{
    var template = File.ReadAllText("infra/docker/saga-worker/terraform-modules/shared/cloud-init.sh.tpl");
    var expectedVars = new[] { "cloud_id", "hostname", "portal_url", "enrollment_token", "ghcr_pat" };
    var pattern = new Regex(@"\$\{(\w+)\}");
    var found = pattern.Matches(template).Select(m => m.Groups[1].Value).Distinct().ToHashSet();
    foreach (var v in expectedVars) found.ShouldContain(v);
    found.Count.ShouldBe(expectedVars.Length, $"Unexpected template var(s); contract is frozen by PORTAL-008. Found: {string.Join(", ", found)}");
}
```

**Why this test matters:** PORTAL-010 will edit `cloud-init.sh.tpl`'s body but **must not add variables without coordinating a WorkspaceLayout change**. This test fails loudly if someone adds `$${new_var}` without also updating the C# rendering + this test's `expectedVars` array. Keeps the saga ↔ cloud-init contract from drifting.

## Acceptance criteria

- `dotnet build` succeeds with **zero warnings**.
- `terraform fmt -check -recursive infra/docker/saga-worker/terraform-modules/digitalocean` passes (HCL formatting).
- `WorkspaceLayoutTests` pass for both `stub` and `digitalocean` providers.
- `DigitalOceanModuleValidationTests.ModuleValidatesCleanly` passes — proves HCL + variable wiring + provider plugin compatibility.
- `CloudInitTemplateVariablesTests` passes — frozen template-variable contract.
- `docker compose build saga-worker && docker compose up -d saga-worker` succeeds; the entrypoint logs `Plugin cache warmed` on first boot (v2 marker), and on second boot logs `Plugin cache already warmed (v2); skipping`.
- `docker compose exec saga-worker terraform -chdir=/app/terraform-modules/digitalocean init -backend=false` succeeds — proves the module's `terraform init` finds the digitalocean provider in the warmed cache without hitting the network.
- Manual smoke (not in CI): set `DO_TF_LIVE_TOKEN`, run `dotnet test --filter "Category=LiveDigitalOcean"`. The test provisions a droplet, captures its IP, destroys cleanly. Cost ~$0.10.
- End-to-end saga path (manual): insert a `pending` row with `cloud.Provider = "digitalocean"`, valid step-up unlock, and a *real* DO token in the provider vault. Watch the saga walk `pending → tf_planning → tf_applying → dns_creating → awaiting_cloud_callback`. The saga will time out at `awaiting_cloud_callback` (5 min) because PORTAL-010's cloud-init hasn't been written yet — assert the row reaches `rolling_back_dns → rolling_back_tf → failed_callback`, and verify the droplet is actually destroyed in DO's UI.

## Concrete steps in order

1. **`versions.tf` + `variables.tf` + `outputs.tf`** — land the structural shells. Tiny, no logic. Verify with `terraform fmt`.

2. **`shared/cloud-init.sh.tpl`** — placeholder with frozen variable contract. Drop in the bash content above. Land `CloudInitTemplateVariablesTests` and watch it pass.

3. **`main.tf`** — drop in the four-resource module. `terraform fmt`. `terraform init -backend=false` + `terraform validate` locally (outside any container) — confirm zero errors.

4. **`WorkspaceLayout.cs`** — extend the branching. Run `WorkspaceLayoutTests` (both cases). Assert the rendered `main.tf` matches the shape inline above.

5. **`entrypoint.sh`** — fix the registry sources, bump marker. `docker compose build saga-worker --no-cache` (don't accept cached layers; the entrypoint change should land). `docker compose up -d saga-worker` and `docker compose logs saga-worker` — confirm the v2 warm-up runs.

6. **`DigitalOceanRegions.cs`** — land the allowlist. Single class, no tests needed beyond a `Allowed.ShouldNotBeEmpty()`.

7. **`Provisioning:PortalUrl`** — add to `appsettings.json` + `appsettings.Development.json`. Update the `WorkspaceLayout` constructor signature if needed (it already takes `IConfiguration`).

8. **`DigitalOceanModuleValidationTests`** — land + run. Should pass with the module from step 3.

9. **`DigitalOceanModulePlanTests` + `DigitalOceanLiveApplyTest`** — land but trait-gate them. Run manually if you have a DO token. Do NOT run live test in CI.

10. **Manual end-to-end smoke**: insert a `pending` row with a real DO token, watch the saga drive to `awaiting_cloud_callback`, then to `failed_callback` after the 5-min timeout. Verify the droplet is created + destroyed in DO's web UI within ~10 min.

11. **Verify all acceptance criteria**. `git status` shows only files in `Output of PORTAL-008 — final directory state`. Commit; open PR.

## Risks & gotchas

- **DO API token in terraform state.** The `digitalocean/digitalocean` provider's `token` argument is consumed by the provider block, not stored on resource attributes. Terraform DOES NOT serialize provider-block arguments to state. **Verify:** after a successful apply against the live test, `terraform show -json` the state and grep for the token value — it should not appear. If it does, switch to setting `DIGITALOCEAN_TOKEN` env var instead of the provider arg. (As of `digitalocean/digitalocean` v2.x, the env-var path bypasses state entirely.)

- **No SSH key, no SSH access.** Intentional. The cloud is configured exclusively via cloud-init's user-data. If a user needs root console access:
  1. DO emails a randomly-generated root password to the DO account holder when no SSH key is provided at droplet creation.
  2. DO's web console (web-based terminal) accepts that password.
  
  This is documented in the wizard's "Provisioning details" expander (PORTAL-011 surface). **If thesis-scope debugging makes SSH valuable**, add `var.admin_ssh_public_key` (non-sensitive — public keys are not secrets) and a `digitalocean_ssh_key` resource + `ssh_keys = [digitalocean_ssh_key.admin.fingerprint]` on the droplet. Keep the variable optional with `default = null`; gate the `ssh_keys` arg with a conditional. Future work.

- **Volume detach blocks droplet destroy.** `digitalocean_volume_attachment` is its own resource; terraform's destroy order will detach before destroying the volume. Confirmed in DO provider v2 — should not be an issue. If destroy hangs on a volume detach, manually detach via DO's UI and re-run terraform destroy.

- **Plugin cache races.** The `entrypoint.sh` warm-up writes to `/var/lib/portal/terraform/plugin-cache`. If multiple SagaWorker replicas boot simultaneously and the marker file doesn't yet exist, they race to download providers. PORTAL-017 (production scale) may add replicas; for thesis scope (single replica), this is fine. **Add a file-lock to the warm-up** (`flock $CACHE_DIR/.warming`) if it becomes painful.

- **`templatefile()` evaluates at plan time.** If `enrollment_token` or `ghcr_pat` is `null` or empty (which they should never be — the saga checks before invoking), `terraform plan` will succeed but the rendered cloud-init will contain literal empty strings, which silently breaks first-boot registration. **The saga's `TfPlanningHandler` must validate these are non-empty before invoking terraform plan** — verify this in PORTAL-007's tests; if missing, file a follow-up.

- **`-no-color` on plan output.** PORTAL-007 already passes `-no-color` to plan/apply. Verify: if terraform's JSON event stream emits ANSI codes (it shouldn't with `-no-color`), `EventsLogAppender` will choke on JSON parse. If you see broken events_log entries, re-verify the flag.

- **`monitoring = true` adds a `digitalocean_monitor_alert` capability.** The droplet metrics are free; if a future ticket wants to set alert thresholds, that's a separate resource. Out of scope.

- **`size` and `region` are passed in by the saga, not the module.** The module trusts the inputs. If PORTAL-011 sends an invalid `region`, terraform plan fails with a structured error. The saga transitions to `failed_tf` and surfaces the error in `events_log`. The error message will be DO's ("region not found" or similar) — acceptable for thesis, but PORTAL-011 should pre-validate via `DigitalOceanRegions.IsAllowed` to give a better UX.

- **Cloud-init runs as root.** The placeholder's `set -euo pipefail` ensures errors fail loudly. PORTAL-010 must preserve this — silent cloud-init failures are the #1 source of "cloud is up but cert never appears" debug pain. PORTAL-010 should log to `/var/log/portal-bootstrap.log` and tail it on completion.

- **`hostname` is the FQDN, not the subdomain.** The droplet's name is `thany-<short_uuid>` (per `locals.resource_name`); the `hostname` variable is the *DNS-facing* FQDN. Don't conflate them. PORTAL-011 generates the FQDN as `<random8>.thany.click` and stores it in `Cloud.Hostname`.

- **`Cloud.Region` validation lives in PORTAL-011.** This ticket lands the allowlist (`DigitalOceanRegions`) but doesn't wire it. **Confirm with the PORTAL-011 ticket author** that the wizard endpoint imports it. If PORTAL-011 ships without the validation, the saga still surfaces the DO error — degraded UX, not a correctness bug.

- **Plugin cache disk usage.** Each provider plugin is ~50-100 MB. Three providers ≈ 200-300 MB on the `terraform_data` volume. Negligible on B2ms's 128 GB SSD.

- **DO rate limits.** The DO API rate-limits to ~5000 req/h per token. A single saga run does maybe 20 API calls during apply. At thesis scale (a handful of clouds), this is fine. PORTAL-015's destroy + recreate cycles will be the heaviest user — still within limits.

- **Cost-of-test discipline.** `DigitalOceanLiveApplyTest` provisions a 16 GB droplet (~$0.10/hour). **Always destroy in finally.** If the test panics in a way that skips finally (e.g., process kill mid-test), check DO's UI for orphans. Tag `managed-by-portal` on every resource lets you bulk-clean via `doctl compute droplet delete --tag managed-by-portal` if needed.

- **`Microsoft.Extensions.Configuration` binding for `Provisioning:PortalUrl`.** If the section is missing, `WorkspaceLayout` throws `InvalidOperationException` at render time. The throw is intentional — silent fallback to `null` would generate a bad cloud-init. **Verify the config is set in every environment** (dev, prod) before merging.

- **Re-pinning the Ubuntu image is a controlled change.** `image_slug = "ubuntu-24-04-x64"` is hardcoded. When 26.04 LTS ships, a follow-up ticket re-pins + tests cloud-init compatibility. Don't auto-bump.

- **`ignore_changes = [user_data]` on the droplet** means changing `portal_url` post-provisioning doesn't trigger a recreate. If the portal moves to a new URL, existing clouds keep calling the old one. Acceptable for thesis (URL is stable on `thany.click`); document in deployment runbook.

## Definition of done

All acceptance criteria pass. `git status` shows the four `.tf` files under `terraform-modules/digitalocean/`, the `shared/cloud-init.sh.tpl` placeholder, the updated `WorkspaceLayout.cs`, the new `DigitalOceanRegions.cs`, the updated `entrypoint.sh`, the updated `appsettings.json` files, and the test files. `docker compose up -d` from scratch produces a fully-up stack with the v2 plugin cache warmed correctly. Inserting a `pending` row with `cloud.Provider = "digitalocean"` and a real DO token drives the saga to `awaiting_cloud_callback` against a real droplet — proves the module is wired end-to-end.

A fresh agent picking up **PORTAL-009 (Azure module)** from this state knows:
- The pattern: a directory under `infra/docker/saga-worker/terraform-modules/azure/` with `versions.tf` + `variables.tf` + `main.tf` + `outputs.tf`.
- The contract is identical: same variable surface, same `output "ip"`.
- `WorkspaceLayout` has a stubbed `"azure"` branch that throws `NotImplementedException` — replace the throw with the real `required_providers` + `provider "azurerm"` block + extra module args.
- Azure's provider auth is *not* a single token; it's `(tenant_id, client_id, client_secret, subscription_id)`. `WorkspaceLayout` needs to pass four `TF_VAR_*` env vars, and `TfPlanningHandler` (in `Portal.SagaWorker`) needs to decrypt four credential parts from the provider vault. PORTAL-005's `IProviderTokenVault` returns a single bytes blob — coordinate with PORTAL-005's owner on a multi-part shape (probably JSON in the blob).
- Resource set: `azurerm_resource_group` + `azurerm_virtual_network` + `azurerm_subnet` + `azurerm_network_security_group` (allow 80/443 inbound, deny SSH) + `azurerm_public_ip` + `azurerm_network_interface` + `azurerm_linux_virtual_machine` (size `Standard_B4ms` per DEC-001) + `azurerm_managed_disk` attached.

A fresh agent picking up **PORTAL-010 (cloud-init script body)** from this state knows:
- The template file is `infra/docker/saga-worker/terraform-modules/shared/cloud-init.sh.tpl`.
- The variable contract is frozen: `cloud_id`, `hostname`, `portal_url`, `enrollment_token`, `ghcr_pat`. Don't add new ones without updating `WorkspaceLayout` + the rendering tests in lockstep.
- The script runs as root on first boot via DO's user-data + cloud-init's `runcmd` mechanism (DO auto-detects bash shebangs and runs them).
- Expected work: Docker install, `docker login ghcr.io -u USERNAME -p ${ghcr_pat}` (if not anonymous), pull `docker-compose.yml` from the project's release URL, render `.env` (with cloud-generated admin token + portal_url + enrollment_token + JWT signing key), `docker compose up -d`, wait for Caddy to acquire LE cert, POST `{cloud_id, admin_token}` to `${portal_url}/api/clouds/{cloud_id}/callback` (PORTAL-016's endpoint).
- `CloudInitTemplateVariablesTests` enforces the variable contract — keep it green.

A fresh agent picking up **PORTAL-010b (real Cloudflare DNS client)** from this state knows:
- The DO module's `output "ip"` is consumed by the saga's `DnsCreatingHandler`, which calls `ICloudflareDnsClient.CreateAAsync(subdomain, ip, cfToken, ct)`. PORTAL-008 doesn't touch DNS — Cloudflare is a separate concern handled at the saga level, not in the terraform module.

A fresh agent picking up **PORTAL-011 (wizard + cloud-create endpoint)** from this state knows:
- The endpoint validates `region` against `DigitalOceanRegions.Allowed` (or the future `AzureRegions.Allowed` for Azure clouds).
- The endpoint sets `cloud.Provider = "digitalocean"` (or `"azure"`); the saga routes to the right module.
- `size` defaults to `s-4vcpu-16gb` for DO clouds via `Provisioning:DefaultSize`; user can override in advanced settings.

## Cross-references

- **PORTAL-007** — prerequisite. Defines the `WorkspaceLayout` rendering and the `ITerraformRunner` abstraction this module is invoked through.
- **PORTAL-007a** — prerequisite. Defines the SagaWorker container image and the terraform binary at `/usr/local/bin/terraform`.
- **PORTAL-005** — prerequisite. `IProviderTokenVault.DecryptAsync(userId, "digitalocean", dek, ct)` returns the DO API token in plaintext, which the saga passes via `TF_VAR_provider_token`. **The `"digitalocean"` provider key must exist as a valid `ProviderKind` enum value** — verify before this ticket starts; if missing, a one-line fix to `ProviderKind.cs` is part of this ticket.
- **PORTAL-009** — successor. Mirrors this ticket's shape for Azure.
- **PORTAL-010** — successor. Replaces the cloud-init.sh.tpl placeholder body.
- **PORTAL-010b** — successor. Real Cloudflare client; separate from terraform.
- **PORTAL-011** — successor. Wizard UI imports `DigitalOceanRegions`.
- **PORTAL-015** — successor. Destroy endpoint reuses `terraform destroy` through the module.
- **PORTAL-016** — successor. Cloud's callback endpoint; the enrollment_token wired here is what authenticates that callback.
- **ADR-0033** — the canonical saga spec; this module is a consumer.
- **DEC-001** — VPS minimum spec; pins `s-4vcpu-16gb`.
- **DEC-005** — GHCR for image distribution; pins the `ghcr_pat` variable.
- **`cloud-pivot-plan-2026-05-13.md` §22.5** — the full provisioning flow context.
