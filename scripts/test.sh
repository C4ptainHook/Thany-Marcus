#!/usr/bin/env bash
# Canonical entrypoint for the Portal test suite.
#
# Why this exists: as of .NET 10.0.201 + xunit.v3.mtp-v2 3.x, `dotnet test`
# does not discover MTP-style tests (reports "Zero tests ran"). The reliable
# invocation is `dotnet run` on the test project. See plans/portal-001-handoff.md.

set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=./_testcontainers-env.sh
source "${repo_root}/scripts/_testcontainers-env.sh"

exec dotnet run \
  --project "${repo_root}/tests/ThanyMarcus.Portal.Tests" \
  -c "${CONFIGURATION:-Release}" \
  -- "$@"
