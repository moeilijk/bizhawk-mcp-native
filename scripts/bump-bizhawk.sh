#!/usr/bin/env bash
# Half-automates the "Bumping the BizHawk version" steps in AGENTS.md:
#   1. update bizhawk.build to the new commit
#   2. re-fetch/checkout the API source (like scripts/fetch-source.sh)
#   3. diff the ApiHawk interfaces between the old and new commits, so changed
#      signatures (e.g. IMemoryApi, IJoypadApi) are visible before rebuilding
#
# usage: ./scripts/bump-bizhawk.sh <new-commit-sha> [old-commit-sha]
#   old-commit-sha defaults to the current contents of bizhawk.build.
#   Pass the same commit twice (or no arg) to just diff against the checkout.
set -euo pipefail
cd "$(dirname "$0")/.."

OLD="$(cat bizhawk.build)"
NEW="${1:?usage: ./scripts/bump-bizhawk.sh <new-commit-sha> [old-commit-sha]}"
if [ "${2:-}" ]; then OLD="$2"; fi

if [ ! -d bizhawk-src/.git ]; then
  echo "Cloning (partial clone, blobs fetched on demand)..."
  git clone --filter=blob:none --no-checkout https://github.com/TASEmulators/BizHawk.git bizhawk-src
fi

cd bizhawk-src

# make sure both commits are present so git can diff them
for c in "$OLD" "$NEW"; do
  if ! git cat-file -e "$c^{commit}" 2>/dev/null; then
    echo "Fetching $c ..."
    git fetch --depth 1 origin "$c"
  fi
done

echo "== ApiHawk interface changes $OLD -> $NEW =="
CHANGED="$(git diff --name-only "$OLD".."$NEW" -- src/BizHawk.Client.Common/Api/Interfaces/)"
if [ -z "$CHANGED" ]; then
  echo "(no interface files changed)"
else
  echo "$CHANGED"
  echo
  echo "== Signature diff (src/BizHawk.Client.Common/Api/Interfaces/) =="
  git diff --unified=2 "$OLD".."$NEW" -- src/BizHawk.Client.Common/Api/Interfaces/ | sed -n '1,200p'
fi

echo
echo "== Source pinned to $NEW =="
git switch -C pinned "$NEW"
cd ..

echo "$NEW" > bizhawk.build
echo
echo "bizhawk.build updated to $NEW."
echo
echo "Reminders (see AGENTS.md 'Bumping the BizHawk version'):"
echo "  - the installed build's dll/ folder must match $NEW (Directory.Build.props)"
echo "  - grep the diff above for signatures the tools use, then rebuild + verify"
echo "  - keep JsonRpc.MCP_PROTOCOL_VERSION current if the protocol changed"
echo "  - deploy: close the MCP form in EmuHawk first, then ./scripts/deploy.sh"
