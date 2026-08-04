#!/usr/bin/env bash
# One-command deployment verification against a RUNNING server. Read-only:
# never writes memory, never advances frames, never touches the cheat list.
#
# usage: ./scripts/smoke.sh [--url http://127.0.0.1:8767/mcp/] [--with-lua]
#   --with-lua  also checks lua_docs (opens the Lua Console window —
#               it owns the Lua runtime — so it's opt-in)
# env: BIZHAWK_MCP_URL overrides the default URL
set -uo pipefail
cd "$(dirname "$0")/.."
source "$(dirname "$0")/load-env.sh"

URL="${BIZHAWK_MCP_URL:-http://127.0.0.1:8767/mcp/}"
WITH_LUA=0
while [ $# -gt 0 ]; do
  case "$1" in
    --url) URL="$2"; shift 2 ;;
    --with-lua) WITH_LUA=1; shift ;;
    *) echo "unknown arg: $1"; exit 2 ;;
  esac
done

pass=0; fail=0
check() { # check <label> <python-expr-on-$R>
  local label="$1" expr="$2"
  if python3 -c "import json,sys; d=json.load(sys.stdin); sys.exit(0 if ($expr) else 1)" <<<"$R"; then
    echo "PASS  $label"; pass=$((pass+1))
  else
    echo "FAIL  $label"; fail=$((fail+1))
  fi
}

post() { # post <method> [<params-json>]
  local method="$1" params="${2:-}"
  local body="{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"$method\""
  [ -n "$params" ] && body="$body,\"params\":$params"
  body="$body}"
  R=$(curl -s -m 15 -X POST "$URL" -H 'Content-Type: application/json' -d "$body")
}

echo "== smoke: $URL =="

post initialize '{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}'
check "initialize advertises tools+resources+prompts" \
  "d['result']['capabilities'].get('tools') is not None and d['result']['capabilities'].get('resources') is not None and d['result']['capabilities'].get('prompts') is not None"

post ping
check "ping returns a result" "'result' in d"

post tools/list
check "tools/list has 90+ tools" "len(d['result']['tools']) >= 90"
check "tools/list has the core tools" "all(t in [x['name'] for x in d['result']['tools']] for t in ['ping','read_memory','write_memory','freeze_add','memstate_save','lua_exec','read_bulk'])"

post tools/call '{"name":"get_info","arguments":{}}'
check "get_info has rom/system/paused" "all(k in d['result']['content'][0]['text'] for k in ['rom_name','system_id','paused'])"

post tools/call '{"name":"read_memory","arguments":{"address":0,"width":8}}'
check "read_memory works" "d['result']['content'][0]['text'].find('\"value\"') >= 0"

post tools/call '{"name":"freeze_list","arguments":{}}'
check "freeze_list works" "d['result']['content'][0]['text'].find('\"freezes\"') >= 0"

post resources/list
check "resources/list has bizhawk://lua-docs" "'bizhawk://lua-docs' in d['result']['resources'][0]['uri'] or any(r.get('uri')=='bizhawk://lua-docs' for r in d['result']['resources'])"

post resources/templates/list
check "templates include lua-docs/{library}" "'bizhawk://lua-docs/{library}' in str(d['result']['resourceTemplates'])"

if [ "$WITH_LUA" = "1" ]; then
  post tools/call '{"name":"lua_docs","arguments":{"library":"memory"}}'
  check "lua_docs(memory) has functions" "json.loads(d['result']['content'][0]['text'])['count'] > 0"
fi

echo "== $pass passed, $fail failed =="
[ "$fail" -eq 0 ] && [ "$pass" -gt 0 ]
