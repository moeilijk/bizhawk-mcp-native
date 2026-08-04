# TODO — next improvements for the MCP server

Ideas collected from the agent review, protocol gaps, and API surface not yet
exposed. Roughly ordered by value/effort. Not a commitment — pick what fits.

Legend: `[~]` partially done / covered by another tool · `[ ]` open · `[x]` done

## Protocol / MCP features

- [ ] **Sessions (`mcp-session-id`)**: the dispatch layer is already structured
  for it (`Dispatch` is pure) — add a session map keyed by the header so hosts
  that require sessions (e.g. some clients) work. Also enables JSON-RPC batching.
- [x] **JSON-RPC batching**: a POST with an array of requests returns an array
  of responses in one round trip (the fixed ~17ms per-call overhead is paid
  once); notifications skipped; per-element errors don't kill the batch.
  (Sessions still open — batching did not need them since Dispatch is pure.)
- [ ] **Server-initiated SSE messages**: push framecount/state changes to a
  subscribed client (needs sessions + a client that keeps GET SSE open).
- [ ] **HTTP `PUT`/`DELETE` session endpoints** for full Streamable HTTP parity.
- [x] **Dual-era protocol (2026-07-28 + legacy)**: spec (2026-07-28) made MCP
  stateless — no `initialize`, per-request `_meta` (`io.modelcontextprotocol/
  protocolVersion`/`clientCapabilities`/`clientInfo`). Our server is already
  stateless, so: implement modern mode (per-request version check +
  `UnsupportedProtocolVersionError` `-32022` with `supported` list) + required
  `server/discover` RPC; keep `initialize` for legacy clients; accept
  `MCP-Protocol-Version`/`Mcp-Method`/`Mcp-Name` POST headers (don't require —
  dual-era). Add `resultType: "complete"` to results in modern mode only.
  Bump the legacy-announced version 2025-06-18 → 2025-11-25 (last legacy
  revision). 2026-07-28 also removed `ping` (keep for legacy), replaced
  `resources/subscribe` with `subscriptions/listen` POST stream (we don't
  implement either), and deprecated Roots/Sampling/Logging (we implement none —
  fine). Done: modern + legacy served per request; header validation with
  `-32020` + HTTP 400/404 mapping; `initialize` answers 2025-11-25 in both eras.
- [x] **CacheableResult (`ttlMs` + `cacheScope`)**: required by 2026-07-28 on
  `tools/list`, `prompts/list`, `resources/list`, `resources/templates/list`
  and `resources/read` (SEP-2549). `tools/list` is static per process → long
  TTL; each client poll costs ~17ms + context tokens (94 schemas), so caching
  directly attacks the fixed per-call latency. `resources/list` changes as
  artifacts appear → short TTL / `"private"` scope. `tools/list` order is
  already deterministic (static list) — spec SHOULD for cache hit rates.
  Done: 1 h `"public"` for the static lists (incl. `server/discover`), 30 s /
  10 s `"private"` for resources list/read.
- [x] **`serverInfo.version` hardcoded `"0.2.0"`** (`McpHttpServer.cs`
  initialize response) — read from the assembly version / single source so it
  never drifts from the packaged release. Done: `<Version>0.2.0</Version>` in
  the csproj; `JsonRpc.ServerVersion` reads the informational version (strips
  SourceLink metadata).
- [ ] **Tasks extension (`io.modelcontextprotocol/tasks`, official)**: long
  calls (`wait_until`, `start_fixture` up to 600 frames, watchpoint_wait)
  block the POST response for seconds. Tasks returns a handle immediately +
  `tasks/get` polling; also gives unsolicited task handles. Bigger effort —
  good next iteration once dual-era lands.

## Tools / API surface

- [x] **Lua scripting**: NOT fragile in the pinned BizHawk — the runtime host
  (`LuaLibraries`/`LuaFile`/`LuaSandbox`) lives in **BizHawk.Client.Common**
  (compile-time) and `ExecuteString` is the public REPL path; only the
  instance (`LuaImp` field on the Lua Console) needs reflection, reached via
  the registered `IToolApi.GetTool("LuaConsole")`. Shipped `bizhawk_lua_exec`
  (inline), `lua_load`/`unload`/`enable`/`disable` (script lifecycle) and
  `lua_list`. Scripts run via EmuHawk's frame events every frame (even
  free-running) and survive core reboots.
- [x] **Freeze/cheat support**: not in the ApiHawk set, but the emulator's real
  cheat engine (`MainForm.CheatList` + `Cheat`/`Watch`) is reachable via the
  plugin form's `Owner` (the MainForm): `bizhawk_freeze_add`/`remove`/`list`/
  `clear` plus an optional `freeze: true` on the write tools. Applies every
  frame via EmuHawk's main loop (even free-running) — freeze timers, lives,
  health for analysis.
- [~] **`pointer_scan`**: find all RAM words/pointers pointing at address X.
  Covered by `bizhawk_search_memory` (u16/u32 `value` = target address) —
  only worth a wrapper if the search tool's `max_results`/domain narrowing is
  not enough.
- [ ] **Pointer chasing (`read_pointer`)**: `read_struct` covers fixed-offset
  fields, but dereferencing a chain (read ptr → follow → read target) is still
  N hand-rolled calls. A `bizhawk_read_pointer(address, offsets...)` that
  follows a pointer chain in one frame-consistent pass would kill a whole
  class of agent boilerplate.
- [~] **`fixture_capture(scenario.json)`**: orchestrate press_buttons +
  read_many per frame → CSV. Implemented as `bizhawk_start_fixture` (input
  timeline + per-frame samples → CSV).
- [x] **Stateful comparative RAM search**: `bizhawk_search_memory` without
  `value` compares against the previous state (baseline snapshot on first
  call, then `ne`/`lt`/`gt`/`le`/`ge`/`changed`/`unchanged`), narrowing with
  `addresses` — the classic RAM-search flow (find what increased when the
  player took damage, etc.).
- [ ] **Movie input editing** (`movie_set_input(frame, buttons)`): reading a
  movie's input exists (`movie_input`), writing doesn't — the TAS iteration
  loop (play → inspect → patch frame N → replay) is incomplete without it.
- [ ] **`read_string`/`write_string`**: `read_range` returns hex; reading
  ASCII/UTF-8 text (save names, dialogue) needs a dedicated tool (and the
  write half for name-entry hacks).
- [ ] **`memstate_diff(a, b)`**: the plugin already holds both slots' raw core
  state byte arrays in memory (`bizhawk_memstate_*`); comparing two and
  listing changed RAM ranges answers "what changed between pre/post state"
  with zero disk round-trips.
- [ ] **Server-side numeric converter (`bizhawk_convert`)**: agents without a
  local shell (pure MCP clients) can't `python3 -c "hex(...)"`; a
  decimal↔hex↔width/sign converter in the toolset would enforce the "never
  hand-convert" rule server-side.
- [x] **Multi-condition `wait_until`**: "advance until X==N AND Y==M" was nested
  single waits (fragile); `bizhawk_wait_until` now takes a `conditions` array
  (1..32, each `{address|name, op, value, width?, domain?, endianness?}`) and
  returns only when ALL hold on the same frame, with per-condition results.
- [ ] **Autofire pattern input**: `start_fixture` has a timeline; an
  "hold A every N frames" pattern mode would cover TAS autofire without
  scripting a per-frame timeline.
- [x] **Frame hash** (`bizhawk_frame_hash`): SHA1 of the rendered frame's PNG —
  deterministic for identical output, so agents can detect screen changes
  without transferring pixels.
- [x] **Z80 registers** (`bizhawk_genesis_get_z80_registers`): the gpgx core
  reports both CPUs in one register table (`GetCpuFlagsAndRegisters`); the
  tool filters the `Z80 *` half (sound CPU) — other cores error.
- [x] **Z80 code debugging** (`bizhawk_genesis_disassemble_z80` +
  `bizhawk_genesis_trace_z80`): disassembles Z80 bus space through BizHawk's
  static `Z80ADisassembler` (the gpgx core's own disassembler only speaks 68K),
  and traces PC/SP + stack words per frame — the sound driver's main loop and
  busy-waits. The bus is synthesized on GEN (the core has no "Z80 BUS" domain
  there — verified in `GPGX.IMemoryDomains.cs`; only SMS/GG get one). Live QA
  proved the GEN mapping: 0x0000-0x1FFF = Z80 RAM (the 68K uploads the driver;
  reset vector runs RAM@0x0000), 0x2000-0x3FFF aliased, 0x4000+ = sound
  I/O/open bus. Note: real watchpoints on Z80 access are impossible (gpgx
  memory callbacks scope to "M68K BUS" only); polling watchers cover Z80 RAM.
  Also hardened: unknown domain names are now rejected everywhere instead of
  ApiHawk's silent fallback to the current domain (mislabeled reads).

## Misc / research

- [ ] **Verify Mono (Linux EmuHawk)**: all tests + smoke on Mono; check
  `HttpListener` and `System.Text.Json` behave (known limitation: Linux is a
  compile target but Windows is the tested host).
- [x] **Latency**: measured live (2026-08-03, 50-100 calls each): ~17ms per call
  FIXED overhead (HTTP + JSON parse + UI-thread marshaling) regardless of
  payload — 1 read == 256 reads == 17ms. read_many of 256 items returns ~76 KB
  JSON. Shipped `bizhawk_read_bulk` (raw base64, up to 64 KiB, one call —
  4096 contiguous bytes: 1 call instead of 16 read_many calls). Agent advice:
  batch aggressively; contiguous regions → read_bulk; whole domain →
  dump_memory / bizhawk://read resource.
