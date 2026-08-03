#!/usr/bin/env bash
# Runs the unit tests (net8.0 + xunit, no BizHawk needed — the product's
# sources are linked into the test project with stubbed ApiHawk interfaces).
#
#   tests/BizHawkMcp.Tests/BizHawkMcp.Tests.csproj
#
# Keep in sync with .github/workflows/build-and-release.yml.
set -euo pipefail
cd "$(dirname "$0")/.."
source "$(dirname "$0")/load-env.sh"

dotnet test tests/BizHawkMcp.Tests/BizHawkMcp.Tests.csproj "$@"
