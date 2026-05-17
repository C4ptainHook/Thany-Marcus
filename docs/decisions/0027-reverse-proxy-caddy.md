# ADR-0027: Reverse proxy — Caddy

Status: Accepted
Date: 2026-05-15

## Context

Two distinct deployments need HTTPS termination + reverse proxying:

1. **Portal hosting** (Boiko's deployment of Portal.Api). One fixed domain (e.g., `app.thany-marcus.io` — final brand per DEC-004).
2. **User clouds** (each user's VPS running Cloud.Api). One unique subdomain per cloud (`abc123.thany.click`), provisioned dynamically via Cloudflare DNS API (DEC-003).

The plan's repo layout in `cloud-pivot-plan-2026-05-13.md §27` shows `infra/docker/portal/docker-compose.yml` and `infra/docker/cloud/docker-compose.yml`, both with Caddy. This ADR justifies the choice deliberately rather than accepting it by reflex.

User clouds have a specific privacy requirement (plan §50): "Privacy framing 'data never leaves user's cloud' is therefore accurate." Anything that puts a TLS-terminating intermediary between the plugin and the user's cloud erodes that claim.

## Options considered

### For user clouds (per-cloud HTTPS with dynamic subdomain)

- **A. Caddy + Let's Encrypt.** Auto-acquire and auto-renew certs on first request to a configured domain. Caddyfile is ~5 lines. HTTP→HTTPS, HTTP/2, modern TLS — defaults.
- **B. Nginx + Certbot.** Standard. Needs cert-renewal cron, post-renew hooks to reload Nginx, ACME challenge directory mapping. ~60 lines of config vs ~5 for the same outcome.
- **C. Traefik.** Auto-HTTPS, Docker-label-driven config. Caddyfile is cleaner for a static-ish setup; Traefik shines when many services need dynamic discovery (you don't).
- **D. Kestrel direct + `LettuceEncrypt` library.** Pure-.NET stack. Kestrel terminating production TLS is doable but less battle-tested than dedicated proxies; needs root or `cap_net_bind_service` for port 443; loses the reverse-proxy layer where WAF/rate-limiting/log-shaping naturally live.
- **E. Cloudflare proxy ("orange cloud").** Cloudflare terminates TLS at its edge, origin can be plain HTTP. **Rejected on privacy grounds for user clouds** — Cloudflare holds the cert key and can read decrypted payload. Conflicts with the thesis privacy framing.

### For portal hosting

- All the above. The privacy constraint is weaker (portal data is non-sensitive: cloud-config metadata, hashed plugin tokens, encrypted blobs). Cloudflare proxy would be tolerable but adds the dependency.

## Decision

**Caddy for both deployments.** No Cloudflare proxy at PORTAL-001 time (DNS records stay "gray-cloud" / proxy disabled).

### Why Caddy for user clouds

The asks are:
- Per-cloud unique subdomain (`abc123.thany.click`)
- Auto-acquire + auto-renew Let's Encrypt cert per subdomain
- HTTP→HTTPS redirect, HTTP/2, modern TLS — without manual config
- No third-party TLS termination (preserves the privacy story)

A Caddyfile that satisfies this:

```caddy
{$DOMAIN} {
    encode gzip zstd
    reverse_proxy api:8080
}
```

Cloud-init writes `DOMAIN=abc123.thany.click` to the `.env` file at provisioning time. On first request, Caddy resolves the ACME challenge against the same domain (Cloudflare has already created the A-record), receives the cert, and serves traffic. Renewal happens automatically before expiry.

The alternative (Nginx + Certbot) achieves the same outcome with ~12× the configuration surface for no functional gain.

### Why Caddy for portal hosting

Choosing Caddy for the portal too is the *consistent* choice — one Caddyfile syntax across both deployments, one operational model. The portal deployment is one-off and could pick differently, but matching the user-cloud config is cheaper to maintain than two patterns.

```caddy
{$DOMAIN} {
    encode gzip zstd
    reverse_proxy portal:5000
    # /metrics is NOT proxied — internal scrape only (see ADR-0026)
}
```

### Why not Cloudflare proxy on the portal

Cloudflare DNS is already in the stack (DEC-003 — per-cloud subdomain automation). Enabling proxying on the portal record (`thany-marcus.io`) is one click and gives DDoS protection + edge caching for free. **Not adopting it at PORTAL-001 time** for two reasons:

1. **No concrete need.** No DDoS attacks against a thesis admin portal are anticipated. Edge caching of static `wwwroot/` assets is marginal — the portal is behind login and admin-only.
2. **Adding it later is one toggle in Cloudflare DNS UI** — no code or config change on the .NET side. Reserving the move keeps the dependency surface tight.

The "gray cloud" stance also keeps the portal symmetric with user clouds — neither side has Cloudflare as a TLS-terminating intermediary.

### Why not Cloudflare proxy on user clouds

Plan §50 frames the thesis privacy story as "data never leaves user's cloud." Proxying user-cloud traffic through Cloudflare contradicts this — even if Cloudflare doesn't store payload by default, it has the keys to terminate the cert and can in principle observe traffic. Keeping user-cloud DNS records "gray" preserves the framing.

## Consequences

- **Positive:**
  - One Caddyfile pattern across both deployments — minimal context-switching.
  - Auto-HTTPS removes manual cert lifecycle from operations.
  - Modern protocols (HTTP/2, TLS 1.3) by default.
  - User-cloud privacy framing preserved end-to-end.
  - Cloudflare proxy is reserve capacity: enable later if DDoS, edge caching, or geographic distribution becomes desirable.
  - Caddy's admin API supports dynamic reconfiguration (not currently needed since Tier 1 / Tier 2 split was dissolved in the late-day 2026-05-13 pivot, but available if requirements re-emerge).
- **Negative:**
  - Smaller community than Nginx; less ambient "copy-paste this Nginx config" content available. Bounded by Caddyfile's small surface area for the actual asks.
  - Caddy is a single point of failure per deployment. Acceptable — so is Nginx in the equivalent deployment.
- **Neutral:**
  - The portal's `infra/docker/portal/Caddyfile` is PORTAL-017's responsibility, not PORTAL-001.
  - User-cloud Caddyfile is owned by cloud-init template (PORTAL-010).

## Related

- [[0020-server-push-sse]] — Caddy must not buffer SSE responses (`X-Accel-Buffering: no`)
- [[0026-observability-and-health-checks]] — Caddy does NOT proxy `/metrics`; Prometheus scrapes internally
- `plans/cloud-pivot-plan-2026-05-13.md §27` — `infra/docker/{portal,cloud}/` deployment layout
- `plans/cloud-pivot-plan-2026-05-13.md §50` — privacy framing that excludes Cloudflare-proxied user-cloud traffic
- `plans/tickets-2026-05-13.md` — PORTAL-010, PORTAL-017 own the actual Caddyfile content

## Amendment 2026-05-17 — Caddy retired for user-cloud control plane

User clouds no longer use Caddy. The CLOUD-001 amendment ([[0034-cloud-bootstrap-and-portal-handshake]] §2, §4) replaced the Caddy + `events.handlers.exec` reverse-proxy and cert-event chain with **nginx-on-host + certbot `--deploy-hook`**. The driver was a design defect in the original CLOUD-001 cut: the `events { on cert_obtained exec curl ... }` directive requires the third-party `caddy-events-exec` plugin which is not bundled in `caddy:2.7-alpine`, breaking every PORTAL-011 smoke. Rather than custom-build a Caddy image with the plugin, the amendment adopts the legacy nginx + certbot pattern (modeled on the n8n droplet provisioning at commit `5af3afd`), which is battle-tested on the exact stack (apt-installed nginx + `certbot --nginx` + Docker app behind it on a DO droplet), uses purpose-built primitives (`--deploy-hook` is a first-class certbot feature), and removes a plugin dependency.

The privacy framing (no third-party TLS terminator) is unchanged — nginx + certbot also terminate TLS locally on the user's VPS with their own keys. **Caddy is retained for the portal deployment** (`docker-compose.yml`); the one-Caddyfile-pattern argument from this ADR's original Decision section no longer applies on the user-cloud side.

## Amendment 2026-05-17 — privacy framing clarified

The original "data never leaves user's cloud" framing (plan §50) was about *user-cloud* data — vault content, knowledge artefacts, processed Markdown, embeddings. It is unchanged: user-cloud traffic still terminates TLS at the user's own Caddy, on the user's own VPS, with no third-party intermediary.

What was implicit, and is now explicit: **portal operational data is not user-cloud data**, and the framing does not extend to it. With the move to Azure Database for PostgreSQL Flexible Server ([[0038-managed-postgres]]), the portal's operational store (user identity, encrypted provider tokens, cloud metadata) now lives on Azure-managed storage. Azure SRE staff have storage-level access to that data.

Two specific clarifications:

- **Provider tokens remain protected at the application layer.** Per [[0030-auth-flow]], provider tokens are encrypted with user-derived DEKs (Argon2id) before any database write. Azure cannot derive the DEK without the user's passphrase.
- **Cleartext PII on the managed DB is acknowledged and accepted.** User emails, Google `sub` identifiers, and cloud subdomains are stored in cleartext. This is the trade-off for managed durability + automated PITR, and replaces a previously-proposed operator-managed `pg_dump` cron that had its own (worse) operator-trust profile.

The user-cloud side of the framing is therefore narrower than the original wording but still load-bearing: the user's vault content never traverses an intermediary the user did not provision. Portal operational data is a separate concern with a separate trust boundary, now documented in [[0038-managed-postgres]].
