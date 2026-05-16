#!/usr/bin/env bash
set -euo pipefail

CACHE_DIR=/var/lib/portal/terraform/plugin-cache
MARKER="$CACHE_DIR/.warmed-v2"
mkdir -p "$CACHE_DIR"
mkdir -p /var/lib/portal/terraform/jobs

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
        TF_PLUGIN_CACHE_DIR="$CACHE_DIR" \
            terraform -chdir="$workdir" init -input=false -no-color
        rm -rf "$workdir"
    done
    touch "$MARKER"
    echo "[entrypoint] Plugin cache warmed"
else
    echo "[entrypoint] Plugin cache already warmed (v2); skipping"
fi

exec dotnet ThanyMarcus.Portal.SagaWorker.dll
