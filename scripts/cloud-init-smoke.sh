#!/usr/bin/env bash
# Manual cloud-init smoke test (PORTAL-010). Boots a multipass VM with a
# rendered cloud-init template against Let's Encrypt staging, waits for the
# cloud to register with a netcat listener acting as the portal, asserts
# /admin/health flips to cert_ready:true.
#
# Requires: terraform, multipass, jq, netcat. Hypervisor needed; not in CI.
# Usage:    DOMAIN=smoke.thany.click LE_EMAIL=you@example.com ./scripts/cloud-init-smoke.sh
#
# Pre-req:  the DOMAIN must already have an A-record pointing at the VM's
#           public IP. Multipass VMs are usually NATed; for a real LE smoke
#           use a tunnel (cloudflared) or run this on a DO droplet via the
#           PORTAL-008 module instead.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VARS_FILE="$REPO_ROOT/tests/ThanyMarcus.Portal.Tests/CloudInit/Fixtures/sample-vars.tfvars.json"
TEMPLATE="$REPO_ROOT/infra/terraform/shared/cloud-init.yaml.tpl"

DOMAIN="${DOMAIN:-$(jq -r .hostname "$VARS_FILE")}"
LE_EMAIL="${LE_EMAIL:-$(jq -r .le_email "$VARS_FILE")}"

# Override the fixture's empty le_acme_ca with the staging endpoint so we
# don't burn prod-LE rate limit on a smoke run.
RENDERED="$(mktemp -t cloud-init-rendered.XXXXXX.yaml)"
trap 'rm -f "$RENDERED"' EXIT

jq --arg domain "$DOMAIN" --arg email "$LE_EMAIL" \
   '.hostname=$domain | .le_email=$email | .le_acme_ca="https://acme-staging-v02.api.letsencrypt.org/directory"' \
   "$VARS_FILE" > "$RENDERED.vars"

terraform -chdir="$REPO_ROOT/infra/terraform/shared" init -backend=false >/dev/null 2>&1 || true
terraform -chdir="$REPO_ROOT/infra/terraform/shared" console <<EOF > "$RENDERED.raw"
templatefile("$TEMPLATE", jsondecode(file("$RENDERED.vars")))
EOF

python3 -c "
import json, sys
with open('$RENDERED.raw') as f:
    body = f.read().strip()
print(json.loads(body), end='')
" > "$RENDERED"

if command -v cloud-init >/dev/null 2>&1; then
  cloud-init schema --config-file "$RENDERED" --annotate
fi

VM="thany-cloud-smoke-$RANDOM"
multipass launch 24.04 --name "$VM" --cpus 4 --memory 16G --disk 50G \
  --cloud-init "$RENDERED"

trap 'multipass delete "$VM" --purge; rm -f "$RENDERED" "$RENDERED.raw" "$RENDERED.vars"' EXIT

multipass exec "$VM" -- cloud-init status --wait

ADDR=$(multipass info "$VM" --format=json | jq -r ".info[\"$VM\"].ipv4[0]")
echo "VM ip: $ADDR"

# Poll until cert_ready (≤10 minutes).
for _ in $(seq 1 60); do
  if curl -fsS "http://$ADDR/admin/health" -H "Host: $DOMAIN" \
       | jq -e '.cert_ready == true' >/dev/null 2>&1; then
    echo "SMOKE OK: cert_ready=true"
    multipass exec "$VM" -- journalctl -u thany-cloud-register --no-pager | tail -30
    exit 0
  fi
  sleep 10
done

echo "SMOKE FAIL: cert not ready after 10 minutes" >&2
multipass exec "$VM" -- journalctl -u thany-cloud.service --no-pager | tail -50
multipass exec "$VM" -- journalctl -u thany-cloud-register.service --no-pager | tail -50
exit 1
