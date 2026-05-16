#!/usr/bin/env bash
set -euo pipefail

CACHE_DIR=/var/lib/portal/terraform/plugin-cache
mkdir -p "$CACHE_DIR"
mkdir -p /var/lib/portal/terraform/jobs

if [ ! -f "$CACHE_DIR/.warmed" ]; then
    echo "[entrypoint] Pre-warming terraform plugin cache..."
    for provider in digitalocean azurerm cloudflare; do
        workdir=$(mktemp -d)
        cat > "$workdir/main.tf" <<EOF
terraform {
  required_providers {
    $provider = { source = "hashicorp/$provider" }
  }
}
EOF
        TF_PLUGIN_CACHE_DIR="$CACHE_DIR" \
            terraform -chdir="$workdir" init -input=false -no-color || true
        rm -rf "$workdir"
    done
    touch "$CACHE_DIR/.warmed"
    echo "[entrypoint] Plugin cache warmed"
else
    echo "[entrypoint] Plugin cache already warmed; skipping"
fi

exec dotnet ThanyMarcus.Portal.SagaWorker.dll
