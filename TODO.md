# TODO — next improvements for the MCP server

Ideas collected from the agent review, protocol gaps, and API surface not yet
exposed. Roughly ordered by value/effort. Not a commitment — pick what fits.

Legend: `[x]` done · `[~]` partially done / covered by another tool · `[ ]` open

## High priority (agent test-loop wants)

- [x] **Watchers** (`bizhawk_watch_*`): session-local list + one-call reads with
  `changed` detection (polling-based — `IMemoryEventsApi` isn't registered).
- [x] **Wait/condition breakpoint** (`bizhawk_wait_until`): advance frames until
  `eq/ne/lt/gt/le/ge` holds, pause-restoring.
- [x] **Frame-level trace** (`bizhawk_trace`): sample PC+SP+SR+disasm per frame.
- [x] **Batch memory ops**: `bizhawk_read_many` (N addr/width/domain in one call)
  and `bizhawk_write_range` (contiguous bytes). `read_many` gained `"consistent": true`
  to pause during the batch so all reads come from the same frame.
- [x] **Shared symbols (Ghidra ↔ BizHawk)** (`bizhawk_symbols_set/list/clear`):
  name → (addr, width, domain) table; `read_memory`/`write_memory`/`read_many`
  accept `name` instead of `address`. Kills address-arithmetic bugs.
- [x] **RAM dump** (`bizhawk_dump_memory`): dump a whole domain to a host-side
  file (also exposed as a `bizhawk://` resource) for Ghidra `import_binary`.
- [x] **Geometric overlay** (`bizhawk_overlay_rect`/`overlay_line`): hitboxes and
  collision boxes on the video output via `IGuiApi.DrawRectangle/DrawLine`.
- [ ] **Watchpoints (read/write/exec) + step**: the big one. The gpgx64 core
  already wires `MemoryCallbacks` (exec/read/write) but nothing activates them;
  reaching them needs reflection into the concrete `EmulationApi` →
  `DebuggableCore.MemoryCallbacks` (IMemoryEventsApi is NOT registered). Risk:
  fragile across BizHawk bumps. **Per-instruction step is impossible** — gpgx's
  `CanStep` returns `false`; only frame stepping exists. Prototype separately.
- [ ] **VRAM plane decode** (`bizhawk_read_plane`): nametable (64×32) + tiles
  (8×8, 4bpp planar) + palette → PNG. Most hardware-specific work; no new APIs
  needed (VRAM/CRAM domains exist).
- [ ] **`pointer_scan`**: find all RAM words/pointers pointing at address X.
  Mostly covered by `bizhawk_search_memory` (u16/u32 `value` = target address) —
  only worth a wrapper if the search tool's `max_results`/domain narrowing is
  not enough.
- [ ] **`state_diff`**: compare two savestates and list changed RAM addresses.
  `.State` files are core-compressed binary, so diffing them directly won't map
  to RAM; a `bizhawk_ram_diff` (snapshot a domain, compare later) is the viable
  version — reveals dynamic structures between frames.

## Protocol / MCP features

- [ ] **Sessions (`mcp-session-id`)**: the dispatch layer is already structured
  for it (`Dispatch` is pure) — add a session map keyed by the header so hosts
  that require sessions (e.g. some clients) work. Also enables JSON-RPC batching.
- [ ] **`tools/list` change notifications**: advertise `listChanged: true` and
  emit a `notifications/tools/list_changed` when the server restarts/changes.
- [ ] **Prompts**: e.g. a "TAS workflow" prompt or "memory research" prompt the
  client can surface to the user.
- [ ] **Server-initiated SSE messages**: push framecount/state changes to a
  subscribed client (needs sessions + a client that keeps GET SSE open).
- [ ] **HTTP `PUT`/`DELETE` session endpoints** for full Streamable HTTP parity.

## Tools / API surface

- [x] **VDP/VRAM tools on Genesis** (`bizhawk_read_palette`): CRAM (GEN, 16-bit
  BGR 3-bit) and CGRAM (SNES, BGR555) as hex RGB; endianness-independent of
  `set_big_endian`. VRAM/VSRAM still readable via `read_memory` domains.
- [x] **Core-aware endianness default**: GEN/SMD/32X/SNES/SNESBG/N64/SAT → big,
  rest little; applied once per system, overridable, reported in `get_info`.
- [x] **68K bus address masking**: bus domains mask 32-bit disassembly addresses
  down to the 24-bit bus (`0xFFFFF832 == 0xFFF832`) for GEN/SMD/32X/SAT.
- [x] **`bus_base` per domain**: `list_memory_domains` reports where each known
  domain sits in the bus space (GEN/SNES/GB maps).
- [ ] **In-memory savestates**: `IMemorySaveStateApi` is NOT registered by the
  ApiHawk provider, so a `[RequiredApi]` won't load. Investigate reaching the
  core's memory-save-state machinery via reflection on the `ApiContainer`/core
  (risky — document before doing). Would give fast save/restore for search/TAS.
- [ ] **Core/board info**: `bizhawk_get_board_info` (`GetBoardName`,
  `GetDisplayType`, `GetGameOptions`) — helps agents identify the game revision.
- [ ] **Rewind/frameskip**: `bizhawk_enable_rewind`, `bizhawk_frameskip`,
  `bizhawk_limit_framerate` (`IEmuClientApi`/`IEmulationApi`).
- [ ] **ROM management**: `bizhawk_open_rom`/`bizhawk_close_rom`/`bizhawk_reboot`
  (careful: path is host-side).
- [ ] **Sound**: `bizhawk_set_sound` / `bizhawk_get_sound` (`SetSoundOn`,
  `GetSoundOn`).
- [ ] **Movie controls**: `bizhawk_movie_start`/`bizhawk_movie_stop`/`save`
  (`PlayFromStart`, `Stop`, `Save` on `IMovieApi`).
- [ ] **`fixture_capture(scenario.json)`**: orchestrate press_buttons +
  read_many per frame → CSV. All pieces exist; just needs an orchestrator.
- [ ] **`run_lua`**: execute Lua inside EmuHawk from the plugin. The Lua runtime
  lives in EmuHawk internals (LuaConsole/LuaEnvironment) — deep reflection,
  fragile. Defer unless a gap really needs it.
- [ ] **Cheat API?** Not in the ApiHawk set at the pinned commit — would need
  `IToolApi`/core access. Probably out of scope.

## Robustness / correctness

- [x] **Screenshot OSD toggle**: `SetScreenshotOSD(false)` before capture,
  restore after (`bizhawk_screenshot`).
- [x] **Concurrency guard**: all tool calls serialized (`McpToolset._callGate`) —
  the active domain/endianness are global emulator state.
- [x] **Out-of-range address validation**: `read/write*` reject addresses beyond
  the domain (with the 68K bus masking exception), instead of silent 0.
- [x] **`movie_info`/`movie_input` without a movie**: return empty/clean error
  instead of NullReferenceException.
- [ ] **Better errors**: `bizhawk_use_memory_domain` returns a message instead
  of failing on unknown domain — should probably be `INVALID_PARAMS`.
- [ ] **Larger reads**: `bizhawk_read_range` caps at 4096 bytes; consider a
  chunked resource (`bizhawk://range/...`) for bigger dumps (dump_memory covers
  whole-domain dumps as resources already).

## DX / tooling

- [x] **More tests**: 80+ unit tests — schema contract, dispatch, memory
  round-trips, endianness, search, watchers, trace, palette, bus masking,
  resources, symbols.
- [ ] **Test the HTTP layer end-to-end**: spin up `McpHttpServer` on a random
  port in a test (Linux `HttpListener` works for loopback) and hit it with real
  HTTP requests.
- [ ] **Version bump helper**: script to update `bizhawk.build` +
  re-fetch source + grep for changed ApiHawk signatures (half-automate the
  "Bumping the BizHawk version" steps in AGENTS.md).
- [ ] **`opencode` config sample**: tidy up `opencode.mcp.example.json` /
  `opencode.mcp.example copy.json` — commit exactly one example with both
  `tools` and `resources` client support.

## Misc / research

- [ ] **Verify Mono (Linux EmuHawk)**: all tests + smoke on Mono; check
  `HttpListener` and `System.Text.Json` behave (known limitation: Linux is a
  compile target but Windows is the tested host).
- [ ] **Endianness per domain**: some cores have mixed-endian domains (e.g.
  GB VRAM is little, Genesis bus is big). Investigate whether ApiHawk's
  `SetBigEndian` is global or per-domain — if global, document the limitation
  in `bizhawk_get_info`.
- [ ] **Latency**: measure per-call overhead (JSON parse, UI-thread marshaling)
  for `read_memory`-heavy loops; consider a `bizhawk_read_bulk` that returns
  base64 to cut JSON size.
