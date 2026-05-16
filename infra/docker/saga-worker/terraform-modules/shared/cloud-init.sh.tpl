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

CLOUD_ID="${cloud_id}"
HOSTNAME_FQDN="${hostname}"
PORTAL_URL="${portal_url}"
ENROLLMENT_TOKEN="${enrollment_token}"
GHCR_PAT="${ghcr_pat}"

echo "[cloud-init] PORTAL-008 placeholder for cloud_id=$${CLOUD_ID} hostname=$${HOSTNAME_FQDN}"
echo "[cloud-init] portal_url=$${PORTAL_URL}"
echo "[cloud-init] enrollment_token starts with: $${ENROLLMENT_TOKEN:0:8}..."
echo "[cloud-init] ghcr_pat starts with: $${GHCR_PAT:0:8}..."
echo "[cloud-init] PORTAL-010 will replace this with the real Docker Compose bootstrap."
exit 0
