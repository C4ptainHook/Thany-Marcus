# ADR-0038: Portal Postgres on Azure Database for PostgreSQL Flexible Server

Status: Accepted
Date: 2026-05-17

## Context

Original portal deployment ran Postgres as a Docker Compose service on the same VM as `portal-api` and `saga-worker`, backed by a `pg_data` Docker volume. This shape coupled three concerns:

1. **State lifecycle tied to VM lifecycle.** VM loss meant data loss. The `pg_data` volume had no backup story — `plans/cloud-pivot-plan-2026-05-13.md §28 Q5` flagged this as unresolved.
2. **Shared failure domain.** A slow query, a runaway terraform plugin, or a memory-hungry HTTP request could starve the others across all three services on a single B2ms.
3. **Recovery story was manual.** A pg_dump cron was the previously-proposed mitigation; it patches the symptom (missing snapshots) rather than the cause (state co-resident with disposable compute).

The fix is to move state off the VM so the VM becomes stateless and disposable. The fastest path to that property on Azure is the managed Postgres offering.

## Options considered

- **A. Keep Compose Postgres + pg_dump cron + offsite blob upload.** Patches the symptom. Restore is operator-scripted, RPO at best 24h, no PITR. Backup script becomes a load-bearing piece of infra that must itself be tested.
- **B. Azure Database for PostgreSQL Flexible Server (Burstable B1ms).** Managed durability, automated PITR (default 7-day retention), SLA, in-region with the portal VM. ~$16/mo steady-state, $0 during the 12-month Azure free trial.
- **C. Neon / Supabase managed Postgres.** Generous free tiers; cross-cloud latency to an Azure VM; breaks the "everything portal in Azure" cohesion that simplifies networking and billing.
- **D. Self-hosted Postgres on a dedicated VM.** Decouples failure domain but keeps the operator on the hook for durability (same backup problem moved to a different VM).

## Decision

**Azure Database for PostgreSQL Flexible Server, Burstable B1ms, single-zone, 7-day PITR, SSL required.**

Concrete shape:

- **SKU**: Burstable B1ms (1 vCPU, 2 GB RAM), 32 GB SSD.
- **Engine**: PostgreSQL 16 (matches current Compose Postgres).
- **Availability**: single-zone. Zone-redundant HA (~2x cost) is not justified at thesis scale.
- **Backup**: default 7-day PITR (continuous WAL archiving).
- **Networking**: VNet integration preferred; public access with firewall rule allowing only the portal VM's outbound IP as fallback.
- **TLS**: enforced server-side. Connection strings carry `SslMode=Require`.

Connection string shape (production VM `.env`):

```
PORTAL_DB_CONNECTION_STRING=Host=<server>.postgres.database.azure.com;Port=5432;Database=portal_dev;Username=<admin>;Password=<pw>;SslMode=Require;Trust Server Certificate=true;Pooling=true;Maximum Pool Size=20
```

The Compose Postgres service is retained in `docker-compose.override.yml` for local development — Testcontainers also stays unchanged. Production `docker-compose.yml` no longer declares a `postgres` service or `pg_data` volume.

**Target durability metrics.**

- **RPO ~5 min** (PITR continuous WAL).
- **RTO ~5 min** (provision fresh VM, update one env var, bring stack up).

Down from the previous "RPO 24h, RTO 30 min + manual rebuild" baseline.

## Consequences

### Positive

- **RPO/RTO improve dramatically.** PITR replaces a cron, with a managed restore UI as the operator surface.
- **Portal VM becomes disposable.** No state on it. Full DR drill = `terraform apply` a fresh VM + reconnect via env var. Documented in `infra/backups/README.md`.
- **Backup surface shrinks.** The only remaining VM-resident state is the ASP.NET Core data-protection key ring (`dp_keys` volume, ~1 MB). A small dedicated backup job (`dp-keys-backup`) replaces the heavier pg-backup story.
- **Patches, minor-version upgrades, and storage growth become Azure's job.**
- **Cohesion**: all portal infrastructure stays in Azure (VM + managed DB + DNS via Cloudflare). One billing console, one network boundary.

### Negative

- **Vendor coupling to Azure managed Postgres.** Migration to another managed PG vendor or back to self-hosted requires `pg_dump`/`pg_restore` + connection-string swap. Acceptable — Postgres dialect is portable; the lock-in is on the *managed surface* (PITR UI, networking model), not the data shape.
- **Cleartext PII on Azure's storage.** User emails, Google `sub` identifiers, cloud subdomains. Azure SRE staff have storage-level access to this data (encryption at rest is by Microsoft-managed keys; we do not currently use customer-managed keys). Accepted as a trade for managed durability + automated PITR. Mitigations: provider tokens remain encrypted with user-derived DEKs (Argon2id) per [[0030-auth-flow]], which Azure cannot derive without the user's passphrase.
- **+$16/mo steady-state** (or $0 during the 12-month Azure free trial). Negligible at thesis scale.
- **Operator must manage Flexible Server admin credentials separately.** Stored in the operator's password manager, injected into the VM `.env` at deploy time. Not in git, not in Compose.
- **Connection budget.** B1ms defaults `max_connections = 50`. At rung-1 multi-replica saga-worker scale, this becomes a concrete operational concern — see [[0019-background-work-and-saga-durability]] amendment and [[0037-saga-scaling-ladder]] rung 1.

### Neutral

- **Local development is unchanged.** Compose Postgres lives in `docker-compose.override.yml`; Testcontainers spins ephemeral Postgres per test session. The production-vs-dev split is the only structural change.
- **PostgreSQL 16 on both sides** — no engine-version drift between dev and production.
- **Plain `pg_dump`/`pg_restore`** is the one-time cutover migration path; no logical replication required at thesis volume.

## Related

- [[0019-background-work-and-saga-durability]] — saga queue substrate; the connection-budget caveat in its negative-consequences list is now a concrete concern (see its 2026-05-17 amendment)
- [[0027-reverse-proxy-caddy]] — "data never leaves user's cloud" privacy framing is clarified in light of this decision (see its 2026-05-17 amendment)
- [[0037-saga-scaling-ladder]] — rung 1's connection-budget constraint sits against this decision's `max_connections` default
- `plans/cloud-pivot-plan-2026-05-13.md §23` — deployment topology this ADR amends
- `plans/cloud-pivot-plan-2026-05-13.md §28 Q5` — backup gap that this ADR resolves
