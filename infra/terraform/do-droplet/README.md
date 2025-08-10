# DigitalOcean Droplet (Ubuntu) - Terraform

This module provisions a secured Ubuntu Droplet on DigitalOcean with initial hardening, Docker runtime, and networking defaults suited for running n8n and related services.

## What you get
- 1 x Ubuntu LTS Droplet (default: 24.04 LTS)
- Cloud-init for secure SSH (no password login), automatic updates, minimal hardening
- Docker Engine + Buildx + Docker Compose v2 installed and enabled
- UFW firewall: allow SSH (22) and optional ports
- DigitalOcean Cloud Firewall (recommended) with the same rules
- SSH key injection
- Optional monitoring & backups
- n8n directory prepared with docker-compose.yml, .env, and a systemd service to keep it running

## Requirements
- Terraform >= 1.5
- A DigitalOcean personal access token (PAT) with write permissions
- One or more SSH public keys uploaded to DigitalOcean (or provide raw key here)
- Windows PowerShell 5.1+ (commands below use PowerShell)

## Inputs
- `do_token` (sensitive) – your DO PAT
- `name_prefix` – resource name prefix
- `region` – e.g. `nyc3`, `sfo3`, `fra1`
- `size` – e.g. `s-1vcpu-1gb`, `s-1vcpu-2gb`
- `image` – Ubuntu slug (default `ubuntu-24-04-x64`)
- `ssh_key_names` – list of DO SSH key names to attach
- `ssh_pubkeys_raw` – optional list of raw SSH public keys
- `enable_do_firewall` – create DO Cloud Firewall
- `allow_additional_tcp_ports` – list of extra TCP ports to allow (e.g. `[80,443,5678]`)
- `droplet_backups` – enable DO backups (paid)
- `droplet_monitoring` – enable DO monitoring
### n8n-related
- `n8n_root_dir` – host directory for n8n files (default `/opt/n8n`)
- `n8n_port` – host port to expose n8n (default `5678`)
- `n8n_version` – Docker image tag for n8n (default `latest`)
- `n8n_timezone` – container timezone (default `UTC`)

## Quick start
1. Create a file `terraform.tfvars` with your values:

```
do_token = "dop_v1_..."
name_prefix = "knowledge-hub"
region = "sfo3"
size = "s-1vcpu-2gb"
ssh_key_names = ["your-do-key-name"]
allow_additional_tcp_ports = [80, 443, 5678] # include 5678 for n8n UI
```

2. Initialize and plan:

```powershell
cd d:\Marcus\infra\terraform\do-droplet
terraform init ; terraform fmt ; terraform validate ; terraform plan -out plan.out
```

3. Apply:

```powershell
terraform apply plan.out
```

4. Outputs:
- `droplet_ip` – public IPv4
- `ssh_connect` – ready-to-copy SSH command (uses your configured admin user)

## Security notes
- Password authentication is disabled; SSH keys only
- Automatic security updates enabled
- UFW is configured to default deny incoming
- Keep your PAT safe; don’t commit `.tfvars` or state files

## Destroy
```powershell
terraform destroy
```
