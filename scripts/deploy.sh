#!/usr/bin/env bash
# Builds the external tool (Release) and drops it into the install's
# ExternalTools folder. EmuHawk's ExternalToolManager watches that folder,
# so the menu entry appears automatically (Tools > External Tools).
#
#   BIZHAWK_INSTALL can override the install dir (default: auto-detected
#   in Directory.Build.props).
set -euo pipefail
cd "$(dirname "$0")/.."
source "$(dirname "$0")/load-env.sh"

BIZHAWK_INSTALL="${BIZHAWK_INSTALL:-}"
ARGS=()
if [ -n "$BIZHAWK_INSTALL" ]; then
  ARGS+=("-p:BizHawkInstallDir=$BIZHAWK_INSTALL")
fi

dotnet build src/BizHawkMcp/BizHawkMcp.csproj -c Release "${ARGS[@]}"
echo
echo "Done. In EmuHawk: Tools > External Tools > 'BizHawk MCP Server'"
echo "Then point opencode at $(grep -o 'http://[^/]*' src/BizHawkMcp/ExternalToolEntry.cs | head -1 || echo 'http://127.0.0.1:8767/mcp/')"
