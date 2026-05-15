#!/usr/bin/env bash
# TDD loop — re-runs the suite on every save.
#
# Note: Testcontainers Postgres is collection-scoped, so the container starts
# once per `dotnet run` invocation. Each save triggers a fresh container,
# which adds ~3-5s per cycle. For tight inner loops, pass --filter to narrow
# the test set, e.g.:
#   scripts/test-watch.sh -- --filter "FullyQualifiedName~Smoke"

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=./_testcontainers-env.sh
source "${repo_root}/scripts/_testcontainers-env.sh"

exec dotnet watch \
  --project "${repo_root}/tests/ThanyMarcus.Portal.Tests" \
  -- run -c "${CONFIGURATION:-Debug}" -- "$@"
