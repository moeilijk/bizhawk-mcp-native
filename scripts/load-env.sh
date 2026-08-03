#!/usr/bin/env bash
# Sources the project .env (if present), exporting every KEY=VALUE line so
# the calling script sees the project configuration. No-op when .env is
# absent. Usage: source "$(dirname "$0")/load-env.sh"
if [ -f "$(dirname "$0")/../.env" ]; then
  set -a
  # shellcheck disable=SC1091
  . "$(dirname "$0")/../.env"
  set +a
fi

# WSL/Linux: dotnet-install.sh puts the SDK in ~/.dotnet (outside PATH);
# DOTNET_ROOT (from .env or the environment) overrides the auto-detect.
if ! command -v dotnet >/dev/null 2>&1; then
  if [ -n "${DOTNET_ROOT:-}" ] && [ -x "$DOTNET_ROOT/dotnet" ]; then
    export PATH="$DOTNET_ROOT:$PATH"
  elif [ -x "$HOME/.dotnet/dotnet" ]; then
    export PATH="$HOME/.dotnet:$PATH"
  fi
fi
