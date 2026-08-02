#!/usr/bin/env bash
# Optionally fetch the BizHawk source at the pinned commit (bizhawk.build)
# into ./bizhawk-src for API browsing/debugging. The build itself does NOT
# need the source — it references the DLLs of an installed dev/release build.
set -euo pipefail
cd "$(dirname "$0")/.."

COMMIT="$(cat bizhawk.build)"
echo "Pinning BizHawk source at $COMMIT"

if [ ! -d bizhawk-src/.git ]; then
  echo "Cloning (partial clone, blobs fetched on demand)..."
  git clone --filter=blob:none --no-checkout https://github.com/TASEmulators/BizHawk.git bizhawk-src
fi

cd bizhawk-src
git fetch --depth 1 origin "$COMMIT"
git switch -C pinned "$COMMIT"
echo "BizHawk source at $COMMIT checked out in ./bizhawk-src (branch 'pinned')"
