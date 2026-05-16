# stub-admin-health

Placeholder for the cloud's `/admin/health` endpoint while CLOUD-001 / CLOUD-005 are not yet implemented. Serves a JSON document from a file that a sidecar (`cert-watcher` in `docker-compose.yml`) flips from `cert_ready:false` to `cert_ready:true` once Caddy writes the LE cert to `caddy-data` volume.

Path watched by `cert-watcher`:

- Prod LE:    `/data/caddy/certificates/acme-v02.api.letsencrypt.org-directory/<DOMAIN>/<DOMAIN>.crt`
- Staging LE: `/data/caddy/certificates/acme-staging-v02.api.letsencrypt.org-directory/<DOMAIN>/<DOMAIN>.crt`

Pinned Caddy version is `caddy:2.7-alpine` so the directory layout stays stable; bumping Caddy means re-verifying the path. CLOUD-006 replaces this with a real Caddy admin-API probe.
