#!/usr/bin/env bash

set -euo pipefail

if [[ -z "${DOCKER_HOST:-}" ]] && command -v podman >/dev/null 2>&1; then
  podman_socket="$(podman machine inspect 2>/dev/null \
    | awk -F'"' '/"PodmanSocket"/{flag=1;next} flag && /"Path"/{print $4; exit}')"
  if [[ -n "${podman_socket:-}" && -S "${podman_socket}" ]]; then
    export DOCKER_HOST="unix://${podman_socket}"
    # Ryuk (the Testcontainers reaper) often fails under Podman rootless;
    # rely on IAsyncLifetime DisposeAsync to clean up containers instead.
    export TESTCONTAINERS_RYUK_DISABLED="${TESTCONTAINERS_RYUK_DISABLED:-true}"
  fi
fi

if [[ "${CI:-}" == "true" ]]; then
  export TESTCONTAINERS_RYUK_DISABLED="${TESTCONTAINERS_RYUK_DISABLED:-true}"
fi
