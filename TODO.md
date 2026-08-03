# TODO — next improvements for the MCP server

Ideas collected from the agent review, protocol gaps, and API surface not yet
exposed. Roughly ordered by value/effort. Not a commitment — pick what fits.

Legend: `[~]` partially done / covered by another tool · `[ ]` open

## Protocol / MCP features

- [ ] **Sessions (`mcp-session-id`)**: the dispatch layer is already structured
  for it (`Dispatch` is pure) — add a session map keyed by the header so hosts
  that require sessions (e.g. some clients) work. Also enables JSON-RPC batching.
- [ ] **Server-initiated SSE messages**: push framecount/state changes to a
  subscribed client (needs sessions + a client that keeps GET SSE open).
- [ ] **HTTP `PUT`/`DELETE` session endpoints** for full Streamable HTTP parity.

## Tools / API surface

- [ ] **`run_lua`**: execute Lua inside EmuHawk from the plugin. The Lua runtime
  lives in EmuHawk internals (LuaConsole/LuaEnvironment) — deep reflection,
  fragile. Defer unless a gap really needs it.
- [ ] **Cheat API?** Not in the ApiHawk set at the pinned commit — would need
  `IToolApi`/core access. Probably out of scope.
- [~] **`pointer_scan`**: find all RAM words/pointers pointing at address X.
  Covered by `bizhawk_search_memory` (u16/u32 `value` = target address) —
  only worth a wrapper if the search tool's `max_results`/domain narrowing is
  not enough.
- [~] **`fixture_capture(scenario.json)`**: orchestrate press_buttons +
  read_many per frame → CSV. Implemented as `bizhawk_start_fixture` (input
  timeline + per-frame samples → CSV).

## Misc / research

- [ ] **Verify Mono (Linux EmuHawk)**: all tests + smoke on Mono; check
  `HttpListener` and `System.Text.Json` behave (known limitation: Linux is a
  compile target but Windows is the tested host).
- [ ] **Latency**: measure per-call overhead (JSON parse, UI-thread marshaling)
  for `read_memory`-heavy loops; consider a `bizhawk_read_bulk` that returns
  base64 to cut JSON size.
