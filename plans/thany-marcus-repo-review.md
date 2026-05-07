# Thany-Marcus Repo Review For New Thesis Plan

Date checked: 2026-05-04

Repo: https://github.com/C4ptainHook/Thany-Marcus

Description from GitHub API:

> Smart self-hosted N8N workflow to automate knowledge structuring

## What The Repo Contains

The repository is small and infrastructure-heavy.

Top-level structure:

- `.gitignore`
- `infra/`
- `src/`

Detected languages:

- HCL
- Smarty/template files
- JavaScript
- PowerShell
- Dockerfile

Main directories:

- `infra/terraform/infrastructure`
- `infra/terraform/kubernetes-apps`
- `infra/scripts`
- `infra/backups`
- `src/.devcontainer`
- `src/Dockerfile`

## Current Technical Direction In The Old Repo

The old project explores a self-hosted n8n-based knowledge automation stack.

Important pieces:

- DigitalOcean Terraform.
- Optional DigitalOcean Kubernetes cluster.
- Cloud-init for Docker Compose n8n on a VPS.
- n8n queue mode on Kubernetes with:
  - n8n editor deployment
  - n8n worker deployment
  - Redis queue
  - Postgres database
  - worker HPA
- nginx and HAProxy ingress comparison.
- cert-manager and Let's Encrypt.
- Prometheus/Grafana monitoring stack.
- k6 load tests for nginx vs HAProxy and HPA scaling.
- Backup JSON containing a minimal n8n workflow with Telegram trigger.

## Useful Parts To Carry Forward

### 1. Hardened VPS Bootstrap

The cloud-init template already includes useful defaults:

- non-root admin user
- SSH password auth disabled
- root login disabled
- UFW default deny incoming
- UFW allow outgoing
- UFW OpenSSH limit
- fail2ban
- unattended upgrades
- Docker + Docker Compose install
- systemd service for the Compose stack

This maps well to the new thesis idea:

> trusted user-owned cloud, hardened by default.

For the new project, this can become the base for DigitalOcean and Hetzner VPS deployment.

### 2. Terraform Variable Discipline

The repo already defines variables for:

- provider token
- region
- size
- image
- SSH keys
- firewall
- backups
- monitoring
- tags
- root directory
- app version
- timezone
- optional Kubernetes

This is useful for the new Terraform design. The new project can reuse the idea of:

- simple defaults
- explicit sensitive variables
- provider-specific variables
- output backend connection details

### 3. Queue-Based Processing

The n8n Kubernetes manifests separate:

- editor/API
- workers
- Redis queue
- Postgres persistence

That maps directly to the new system's cloud queue:

- API receives capture uploads
- worker processes captures
- DB stores queue/audit state
- object storage stores raw artifacts

The new project should not necessarily use n8n, but the old repo already reflects the right mental model: queue-based background processing.

### 4. Monitoring And Load Testing Mindset

The repo includes:

- Grafana/Prometheus stack
- k6 tests
- ingress comparison
- HPA scaling test

For the thesis, this can turn into an evaluation section:

- deployment time
- processing latency
- upload/queue throughput
- worker processing time
- resource usage
- cost estimate

### 5. Self-Hosted Product Thinking

The repo already points toward n8n-style deployment. This supports the new thesis framing:

> user-owned private backend, deployable from IaC, not a third-party SaaS.

## Parts To Avoid Carrying Forward Directly

### 1. Do Not Make n8n The Core Backend

n8n is useful inspiration, but making it the core processing engine would weaken the thesis:

- It becomes "workflow automation around Obsidian" rather than a purpose-built knowledge curation system.
- Harder to define typed capture envelopes, audit policies, model-routing decisions, and reviewable Markdown patches.
- n8n workflows are harder to evaluate as a clean architecture contribution.

Recommendation:

> Use n8n as inspiration for deployment/business model, not as the core engine.

Possible exception:

- Future integration: export captures or events to n8n webhooks.

### 2. Avoid Kubernetes For MVP

The old repo has DOKS/Kubernetes, ingress controllers, cert-manager, HPA, Prometheus, Grafana.

This is impressive but too heavy for the new bachelor MVP.

Problems:

- much more cloud complexity
- higher cost
- too much operational surface
- distracts from capture/curation/privacy thesis
- DigitalOcean-specific Kubernetes path does not translate cleanly to Hetzner

Recommendation:

> New MVP should use Docker Compose on a generic VPS. Kubernetes can be future work or appendix.

### 3. Do Not Keep Load Balancer Comparison As A Main Thread

nginx vs HAProxy comparison is valid infrastructure work, but unrelated to the knowledge-capture contribution.

It can be reused only as:

- proof that the author knows deployment evaluation
- optional appendix
- load-testing inspiration

### 4. Do Not Keep Telegram Bot As Primary Capture

The old n8n backup has a Telegram trigger. For the new thesis, Telegram bot capture is weaker than browser/native selective capture:

- it is integration-specific
- it repeats existing bot-style capture tools
- it does not validate the selective desktop/browser capture thesis

Telegram can be future adapter, not MVP.

## How This Repo Should Influence The New Plan

### Architecture Adjustment

The old repo suggests a useful cloud backend shape:

```text
Obsidian plugin / capture agent / browser extension
        |
        v
Cloud API
        |
        v
Queue + DB + object store
        |
        v
Worker processors
        |
        v
Generated Source/Knowledge proposals + embeddings + audit logs
```

The repo's n8n queue-mode design maps well to this, but the implementation should be purpose-built.

### Deployment Adjustment

Use the old repo's VPS cloud-init approach as the starting point:

- Terraform provisions VM.
- cloud-init installs Docker/Compose.
- Compose runs backend services.
- UFW/fail2ban/unattended upgrades are enabled.
- Provider firewall is enabled where supported.

For the new thesis:

- DigitalOcean provider can reuse concepts directly.
- Hetzner provider should mirror the same outputs and cloud-init.
- Docker Compose should be identical across providers.

### Evaluation Adjustment

Reuse the old repo's evaluation mindset, but change metrics:

Old metrics:

- ingress performance
- HPA scaling
- load balancer comparison

New metrics:

- deployment time on DigitalOcean/Hetzner
- capture upload latency
- queue processing latency
- model processing cost/latency
- audit completeness
- recovery success
- resource usage on small VPS

## Recommended New Infrastructure Scope

### Must Have

- Terraform DigitalOcean VPS.
- Terraform Hetzner VPS.
- Shared cloud-init template.
- Docker Compose backend runtime.
- UFW/fail2ban/SSH hardening.
- HTTPS path, preferably Caddy if domain exists.
- API service.
- worker service.
- Postgres or SQLite depending on prototype maturity.
- object storage path, initially local volume or MinIO.
- audit log persistence.

### Should Have

- Redis or lightweight queue.
- health checks.
- backup/restore script.
- basic metrics.

### Future Work

- Kubernetes.
- HPA.
- Prometheus/Grafana stack.
- load balancer comparison.
- n8n workflow integration.

## Relevance To Thesis Novelty

The old repo strengthens the cloud/self-host part of the thesis because it shows an existing direction:

- self-hosted automation
- infrastructure as code
- hardened VPS
- queue processing
- monitoring

But the old repo does not solve the new thesis problem:

- no selective desktop/browser capture
- no Obsidian review client
- no local tiny-model triage
- no capture policies
- no Source/Knowledge Markdown artifact model
- no audit-oriented privacy/data-flow design
- no multimodal capture pipeline

Therefore, the new thesis should be framed as an evolution:

> From self-hosted workflow automation for knowledge structuring to a purpose-built distributed capture and curation architecture for Obsidian-compatible personal knowledge.

## Grill-Me Implication

Question to resolve next:

> Should the new backend be a purpose-built API/worker service, or should it use n8n internally as the orchestrator?

Recommended answer:

> Purpose-built backend for MVP. n8n remains inspiration and optional integration, not the core engine.

