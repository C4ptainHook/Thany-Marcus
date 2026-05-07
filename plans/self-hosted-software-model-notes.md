# Self-Hosted Software Model Notes

## Two Meanings Of "Self-Hosted Software Model"

The phrase can mean two different things. The project should cover both:

1. **Technical model:** how users deploy, operate, update, secure, and back up their own instance.
2. **Business/distribution model:** how the software is licensed, monetized, supported, and sustained while still allowing self-hosting.

For a diploma, the technical model is more important. The business model is useful background because n8n-like deployment and open-core distribution affect product design.

## Technical Self-Hosting Patterns

### Pattern 1: Single Binary / Single Container

Examples:

- many small self-hosted apps
- SilverBullet
- some personal tools

Characteristics:

- easiest deployment
- one command starts the app
- simple backup story if data folder is clear

Good for:

- personal knowledge tools
- bachelor thesis prototype
- local/private mode

Limitations:

- scaling is limited
- background workers, model inference, and storage may need extra services

### Pattern 2: Docker Compose Stack

Examples:

- n8n self-host
- AppFlowy Cloud self-host
- Outline self-host
- many open-source SaaS products

Characteristics:

- app service
- database
- object storage or file volume
- reverse proxy optional
- workers optional

Good for:

- reproducible deployment
- thesis demo on VPS
- local network private cloud

Recommended thesis stack:

- app/API service
- Postgres or SQLite depending on scope
- local filesystem or S3-compatible storage
- vector index service or embedded vector DB
- Ollama service optional
- Caddy/Traefik optional for TLS

### Pattern 3: Kubernetes / Helm

Good for companies, too heavy for the thesis.

Use only as future work.

### Pattern 4: Desktop-First With Optional Server

Examples:

- Obsidian plus sync
- local-first apps
- Git/Syncthing/Nextcloud setups

Characteristics:

- desktop app owns primary UX
- server is sync/backup/model endpoint
- works offline

This is likely the best architecture for this project.

## Recommended Deployment Profiles

### Local Desktop

User runs the app on the same machine as the vault.

Data flow:

- capture client -> local curation service -> Markdown vault
- optional local model
- no content egress

Best for:

- strongest privacy proof
- simplest prototype

### Private VPS

User runs a private server that stores encrypted vault/index and optionally runs inference.

Data flow:

- desktop/mobile client -> user VPS -> vault storage/model

Best for:

- weak client devices
- multi-device usage
- "cloud service" thesis title without third-party cloud dependency

### Hybrid Gateway

Local/private service decides whether to call cloud AI.

Data flow:

- raw capture -> local/private redaction -> sanitized request -> cloud LLM

Best for:

- higher curation quality
- realistic commercial mode

Risk:

- redaction is imperfect; semantic leakage remains.

## Self-Hosted Product Examples

### n8n

n8n is a workflow automation tool with a strong self-host story and hosted cloud product. It uses a "fair-code" / source-available style rather than permissive open source. The Sustainable Use License allows broad use and modification, but restricts offering n8n commercially as a competing hosted service.

Sources:

- https://docs.n8n.io/hosting/
- https://github.com/n8n-io/n8n
- https://github.com/n8n-io/n8n/blob/master/LICENSE.md
- https://faircode.io/

Lessons:

- Self-hosting can be core to adoption while cloud remains the monetized convenience layer.
- Deployment docs, templates, and upgrade paths matter as much as code.
- The product can expose power-user infrastructure while keeping onboarding simple.

### GitLab

GitLab is a canonical open-core/self-managed plus SaaS example. It offers self-managed deployments and GitLab.com SaaS, with enterprise features in paid tiers.

Sources:

- https://about.gitlab.com/install/
- https://about.gitlab.com/pricing/
- https://handbook.gitlab.com/handbook/company/stewardship/

Lessons:

- Self-managed and hosted SaaS can coexist.
- Open-core works when the free core is useful and paid features target teams/enterprises.

### Mattermost

Mattermost offers self-hosted collaboration and cloud options, with enterprise features and support.

Sources:

- https://mattermost.com/pricing/
- https://docs.mattermost.com/deployment-guide/server/server-deployment-planning.html

Lessons:

- Strong privacy/security positioning can support self-hosted adoption.
- Enterprise deployment docs are part of the product.

### Sentry

Sentry is source-available and offers cloud plus self-hosted deployment. It is often discussed as an example of modern commercial source-available infrastructure software.

Sources:

- https://github.com/getsentry/self-hosted
- https://develop.sentry.dev/self-hosted/
- https://sentry.io/pricing/

Lessons:

- Self-hosting can be allowed while the company still monetizes hosted operations, scale, support, and convenience.
- Source-available licenses may protect against direct cloud resale.

### AppFlowy

AppFlowy is an open-source workspace/Notion alternative with self-hosting options.

Sources:

- https://github.com/AppFlowy-IO/AppFlowy
- https://docs.appflowy.io/docs/appflowy/self-host-appflowy

Lessons:

- For knowledge/workspace tools, self-hosting is a common differentiator against Notion.
- However, building a full workspace app is far beyond this thesis.

### Plausible Analytics

Plausible is open-source web analytics with cloud hosting and self-hosting. It is a useful example of privacy-first product positioning.

Sources:

- https://plausible.io/open-source
- https://plausible.io/docs/self-hosting

Lessons:

- Privacy-first plus simple hosted convenience can be a clear product story.
- Self-hosting docs should explicitly state tradeoffs.

## Licensing / Distribution Models

### Fully Open Source

Examples:

- permissive MIT/Apache
- copyleft GPL/AGPL

Pros:

- academic friendliness
- community trust
- easier thesis/public repo story

Cons:

- harder to prevent hosted competitors
- monetization relies on cloud/support/services

Fit for thesis:

- best for diploma work unless there is a startup plan.

### Open Core

Core is open; advanced features are paid/proprietary.

Potential paid features for this project:

- hosted sync
- managed private cloud
- team vaults
- advanced connectors
- managed model endpoints
- compliance/audit reports

Risk:

- can reduce trust if privacy features are paywalled.

Recommendation:

- If discussing business model, keep privacy-critical features in the open core.

### Source-Available / Fair-Code

Source can be viewed and self-hosted, but commercial resale/competition is restricted.

Examples:

- n8n Sustainable Use License
- Sentry Functional Source License / Business Source License style ecosystem

Pros:

- protects commercial hosted product
- users can inspect source

Cons:

- not OSI open source
- academic/community perception may be weaker

Fit:

- worth discussing as a business model, but not necessary for thesis.

### Hosted SaaS + Free Self-Host

Common model:

- self-host is free but requires user effort
- cloud SaaS charges for convenience, uptime, backups, sync, and model credits

This maps well to the user's n8n business-model interest.

## What A Good Self-Host Experience Requires

### Install

- one-command local install or Docker Compose
- clear `.env` template
- health check
- automatic database migrations
- sample vault

### Security

- HTTPS setup
- auth
- backup encryption
- secrets management
- provider allowlist
- private mode egress restrictions

### Operations

- update command
- backup/restore command
- logs
- disk usage view
- model availability check
- index rebuild command

### Data Ownership

- plain Markdown vault export
- full backup export
- no hidden dependency on remote vendor
- documented file layout

### Model Management

- local Ollama/llama.cpp adapter
- remote self-hosted model endpoint
- cloud provider adapter
- per-provider privacy classification
- cost/latency estimates

## Proposed Product Model For This Project

### Academic Prototype

License:

- open source, preferably AGPL or Apache/MIT depending on supervisor/startup goals.

Distribution:

- desktop app
- Docker Compose private server
- docs for local-only and VPS mode

No monetization needed.

### Future Product

Free:

- local-only app
- self-host server
- basic local model curation
- Markdown vault export

Paid hosted:

- managed sync/storage
- managed model routing
- backups
- multi-device setup
- better OCR/transcription
- optional team vaults

Paid enterprise/pro:

- private managed deployment
- audit reports
- custom retention policies
- SSO
- compliance controls

Principle:

> Do not monetize privacy by making private mode paid-only. Monetize convenience and operations.

## Architecture Recommendation For Thesis

Best technical model:

> Desktop-first local app with optional self-hosted private server.

Why:

- strongest privacy story
- easiest prototype path
- still compatible with "cloud service"
- avoids building a full SaaS too early
- lets weak devices use a VPS for inference/storage

Minimum self-host deliverable:

- Docker Compose stack
- one app service
- one data volume
- optional Ollama service
- documented backup/restore
- private mode provider allowlist

Better deliverable:

- local app can switch between local and private VPS backend
- network audit log proves which endpoint received what
- vault remains exportable Markdown

## Self-Hosted Model As Thesis Contribution

Possible claim:

> The project proposes a self-hostable personal knowledge service where cloud capabilities are separated from third-party trust. The user may use cloud-like sync and remote inference while preserving a privacy boundary because all raw-content processing endpoints are user-controlled in private mode.

This is stronger than simply saying "self-hosted" because it defines what self-hosting is for:

- data ownership
- model placement control
- auditability
- durability
- reduced lock-in

## Risks

- Self-hosting can become infrastructure-heavy and distract from the knowledge-system contribution.
- Running LLMs on cheap VPS CPUs may be slow.
- Users may misconfigure servers and weaken privacy.
- Backup/restore is often ignored but essential.
- Mobile clients complicate auth, sync, and offline behavior.

## Scope Boundary

Include:

- Docker Compose deploy
- local/private provider modes
- documented data flows
- backup/export story
- model adapter abstraction

Exclude:

- Kubernetes
- enterprise SSO
- real-time collaboration
- full multi-tenant SaaS backend
- billing
- marketplace/plugins

