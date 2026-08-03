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
- [x] **Batch memory ops**: `bizhawk_read_many` (N addr/width/domain in one call),
  `bizhawk_write_range` (contiguous bytes) and `bizhawk_write_many` (non-contiguous
  addr/name+width+value). `read_many` gained `"consistent": true` to pause during
  the batch so all reads come from the same frame.
- [x] **`ram_diff`** (`bizhawk_ram_snapshot`/`ram_diff`): snapshot a domain in
  memory, then list changed runs (old/new hex) — reveals dynamic structures.
  This is the viable version of the agent's `state_diff` (.State files are
  core-compressed binary that doesn't map to RAM addresses).
- [x] **Shared symbols (Ghidra ↔ BizHawk)** (`bizhawk_symbols_set/list/clear`):
  name → (addr, width, domain) table; `read_memory`/`write_memory`/`read_many`
  accept `name` instead of `address`. Kills address-arithmetic bugs.
- [x] **RAM dump** (`bizhawk_dump_memory`): dump a whole domain to a host-side
  file (also exposed as a `bizhawk://` resource) for Ghidra `import_binary`.
- [x] **Geometric overlay** (`bizhawk_overlay_rect`/`overlay_line`): hitboxes and
  collision boxes on the video output via `IGuiApi.DrawRectangle/DrawLine`.
  (2026-08-02: fixed — they were called WITHOUT a surface, so
  `Get2DRenderer(null)` threw and text drew into the invisible EmuCore buffer.
  All overlay tools now wrap their calls in `WithSurface(DisplaySurfaceID.Client,
  ...)`; `screenshot` gained `include_overlays: true` to compose that layer into
  the PNG via EmuHawk's `ScreenshotCaptureOsd`. Overlays now ACCUMULATE: the
  toolset keeps a list and re-renders it on every frame advance (EmuHawk
  discards the ApiHawk surface per frame, same as Lua), and
  `overlay_rect`/`overlay_line` accept `rects`/`lines` arrays.)
- [x] **Watchpoints (read/write/exec)** (`bizhawk_watchpoint_add/remove/list/wait`):
  real `IDebuggable.MemoryCallbacks` reached via reflection on
  `EmulationApi.DebuggableCore`. **Genesis gpgx waterbox core only** (the only
  core exposing memory callbacks); other cores get a clear `INVALID_PARAMS`.
  Callbacks fire on the core thread and only set volatile flags; the wait loop
  frame-advances on the UI thread. **Per-instruction step is impossible** —
  gpgx's `CanStep` returns `false`; only frame stepping exists.
  (2026-08-02: fixed a real "inert watchpoints" bug — `MemoryCallbackImpl`
  returned `AddressMask => null`, but the core's `Call()` matches
  `Address == (addr & AddressMask)`, so address-specific watchpoints never
  fired on real gpgx; the fake `Fire` called every callback unconditionally so
  tests missed it. Now `AddressMask => 0xFFFFFFFF` and the fake replicates the
  address filter.)
- [x] **Watchpoint context dump** (`bizhawk_watchpoint_wait` `context_bytes: N`):
  on a hit, also returns full registers, the PC + disassembled instruction,
  and N raw bytes around the hit address (context.start/bytes/hit_offset).
  Turns "who writes mainFunction?" into a one-call answer.
- [x] **Fixture capture** (`bizhawk_start_fixture`): advance N frames with an
  input timeline (frame → buttons), sampling a set of addresses/symbols each
  frame, writing CSV straight to the host disk. Replaces the manual
  `capture_fixture.lua` + copy-from-captures flow.
- [x] **Struct reads** (`bizhawk_read_struct`): relative-offset fields from a
  base address or symbol, frame-consistent, with per-field endianness. Turns
  sprObjectOffsets into a reusable definition.
- [x] **Persistent symbols**: `symbols_set`/`clear` now persist across EmuHawk
  restarts via the user data store, **scoped per ROM hash + namespace**
  (`mcp.symbols` = `{romHash: {ns: [symbols]}}`). Reloaded automatically when
  the ROM changes (`get_info`); `symbols_clear {namespace}` clears one scope.
- [x] **Polling watchpoint** (`bizhawk_watch_change`): frame-stepping variant that
  watches an address and returns the frame + value the moment it changes —
  baseline = value at call time, no target value needed (unlike `wait_until`),
  which makes it the one-call answer for "first change frame" on dynamic
  structures (framecounters, state flags).
- [x] **VRAM plane decode** (`bizhawk_genesis_read_plane`): nametable (plane A/B, base
  auto-detected from the core's VDP view — Kid Chameleon uses plane A at 0x0000,
  fallback 0xC000/0xE000) + tiles (8×8, 4bpp packed nibbles) + CRAM palette →
  PNG via a self-contained encoder (DeflateStream, no System.Drawing — runs on
  net48 and Linux). `offset_x`/`offset_y` crop to a camera window.
  (Also fixed `read_palette`: Genesis CRAM bits are 0x0RRR0GGG0BBB — R at
  bits 1-3, B at 9-11 — the old decode had R/B in the wrong positions.)
- [x] **VDP view** (`bizhawk_genesis_get_vdp_view`): reads the Genesis nametable bases +
  dims from the core via reflection on `UpdateVDPViewContext()` (same pattern as
  watchpoints). Note: the gpgx API does NOT expose the individual VDP registers.
- [~] **`pointer_scan`**: find all RAM words/pointers pointing at address X.
  Covered by `bizhawk_search_memory` (u16/u32 `value` = target address) —
  only worth a wrapper if the search tool's `max_results`/domain narrowing is
  not enough.
- [x] **`state_diff`**: implemented as `bizhawk_ram_snapshot`/`ram_diff` — snapshot
  a domain in memory, then list changed runs (old/new hex). Diffing `.State`
  files directly won't map to RAM (core-compressed binary).

## Agent feedback — production bugs (2026-08-02)

Source: `F:\projects\kid\definitive-kid-research\game-attempts\current\docs\mcp-bizhawk-agent-report.md`
(parity-fixture agent, Kid Chameleon remake). Priorities are theirs (P0 =
blocks fixtures). The pure feature requests from that report (F2 symbol
export/import, F4 fixture metadata, F5 labels, F3 custom error envelope) are
deliberately NOT listed here — F3 is a misunderstanding (JSON-RPC codes are
correct; enrich the `data` field instead of replacing codes, see below).

- [x] **B3 (P0) `start_fixture` holds buttons past the end of the input
  timeline** — fixed: `input_mode` `"hold"` (default, backwards compatible) vs
  `"explicit"` (absent frames = no buttons, per-frame like Lua `joypad.set`),
  and `{"frame": N, "buttons": {}}` releases that controller's buttons
  mid-timeline in both modes (`JoypadApi.Set` un-sets every button not in the
  dict). Verified live: vx at 0xFF2506 stayed flat after the timeline ended in
  explicit mode. Repro that used to hold Right on frames 1-3:
  `start_fixture {frames: 4, inputs: [{frame: 0, buttons: {Right: true}}],
  samples: [{address: 0xFF2506, width: 32}]}`.
- [x] **B2 (P0) `read_many` fails the whole batch on one bad item** — fixed:
  per-item errors `{index, requested, error}` + `read`/`failed` counts; valid
  items still read. No name→address fallback (typos stay visible).
- [x] **B1 (P0) `write_range` aborts on 1440 values (~1/3 of the documented
  4096)** — investigated: 1440 values works fine over raw HTTP (curl), so the
  abort is the client dropping requests above ~1-2 KB; the server never sees
  it. Real fix shipped: `{"fill": 0, "length": 1440}` fill mode →
  `{wrote: 1440, address, fill}` (tiny payload) + documented conservative
  limit (chunk `values` ≤ 1024 bytes or use fill).
- [x] **F1 (P1) `write_many` per-item validation feedback** — fixed: bad items
  (unknown symbol, out-of-range, value too wide) fail only themselves →
  `{wrote: N, failed: M, failures: [{index, address, reason}]}`; valid items
  still write.
- [x] **B4 (P2) `read_many` echoes the masked read address, not the requested
  one** — fixed: every read item echoes `requested` (the raw address) alongside
  the effective `address`; same for `read_memory`/`read_signed`/`read_float`.

## Protocol / MCP features

- [ ] **Sessions (`mcp-session-id`)**: the dispatch layer is already structured
  for it (`Dispatch` is pure) — add a session map keyed by the header so hosts
  that require sessions (e.g. some clients) work. Also enables JSON-RPC batching.
- [x] **`tools/list` change notifications**: capabilities advertise
  `listChanged: true`; the first SSE stream of each server lifetime emits
  `notifications/tools/list_changed` (the tool list is fixed per process, so a
  fresh connection after a redeploy may serve a different list — stateless
  subset, no sessions).
- [x] **Prompts**: `prompts/list` + `prompts/get` with `memory_research` and
  `tas_frame` templates (text substitution only; `prompts` capability
  advertised on `initialize`).
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
- [x] **In-memory savestates**: `IMemorySaveStateApi` is NOT registered by the
  ApiHawk provider, so the plugin reaches the core's real **`IStatable` service
  via reflection** (`EmulationApi.Emulator` private property →
  `ServiceProvider.GetService<IStatable>()`, same pattern as watchpoints):
  `bizhawk_memstate_save`/`load`/`list` keep session-local core-state byte
  arrays (no disk, no 10-slot limit). Scope documented: CPU + memory only —
  framecount/lag count are NOT restored. Verified live on gpgx.
- [x] **Movie controls** (`bizhawk_movie_start`/`movie_save`/`movie_stop`):
  load-and-play a .bk2 (or start a new recording), save, stop. Feeds
  `start_fixture` with real inputs for deterministic parity fixtures.
- [x] **Quick-save slots** (`bizhawk_save_slot`/`load_slot`): the emulator's
  1..10 quick-save slots via `ISaveStateApi.SaveSlot/LoadSlot`.
- [x] **Core/board info** (`bizhawk_get_board_info`): `GetBoardName`,
  `GetDisplayType`, `GetGameOptions` — identifies the game revision.
- [x] **Rewind/frameskip**: `bizhawk_enable_rewind`, `bizhawk_frameskip`,
  `bizhawk_limit_framerate` (`IEmuClientApi`/`IEmulationApi`).
- [x] **ROM management**: `bizhawk_open_rom`/`bizhawk_close_rom`/`bizhawk_reboot`
  (path is host-side).
- [x] **Sound**: `bizhawk_set_sound` / `bizhawk_get_sound` (`SetSoundOn`,
  `GetSoundOn`).
- [~] **`fixture_capture(scenario.json)`**: orchestrate press_buttons +
  read_many per frame → CSV. Implemented as `bizhawk_start_fixture` (input
  timeline + per-frame samples → CSV).
- [ ] **`run_lua`**: execute Lua inside EmuHawk from the plugin. The Lua runtime
  lives in EmuHawk internals (LuaConsole/LuaEnvironment) — deep reflection,
  fragile. Defer unless a gap really needs it.
- [ ] **Cheat API?** Not in the ApiHawk set at the pinned commit — would need
  `IToolApi`/core access. Probably out of scope.

## Robustness / correctness

- [x] **Screenshot OSD toggle**: `SetScreenshotOSD(false)` before capture,
  restore after (`bizhawk_screenshot`); gained `include_overlays: true` to
  compose the overlay/OSD layer into the PNG.
- [x] **`write_range` bulk path**: `TryBulkWrite` reaches the domain's raw
  `Data` pointer via reflection on `MemoryApi.DomainList[name]` and does one
  `Marshal.Copy` in a single waterbox `Enter`/`Exit` — up to ~400x fewer
  crossings vs ApiHawk's per-byte `PokeByte` loop. Falls back safely.
- [x] **Concurrency guard**: all tool calls serialized (`McpToolset._callGate`) —
  the active domain/endianness are global emulator state.
- [x] **Out-of-range address validation**: `read/write*` reject addresses beyond
  the domain (with the 68K bus masking exception), instead of silent 0.
- [x] **`movie_info`/`movie_input` without a movie**: return empty/clean error
  instead of NullReferenceException.
- [x] **Better errors**: `bizhawk_use_memory_domain` now throws `INVALID_PARAMS`
  on an unknown domain, listing the known domains in the message.
- [x] **Larger reads**: covered by the `bizhawk://read/{domain}/{range}` resource
  template (raw base64 blob, cap raised to 256 KiB) — `bizhawk_read_range`
  stays capped at 4096 for inline hex, `bizhawk_dump_memory` covers
  whole-domain dumps as resources.

## DX / tooling

- [x] **More tests**: 163 unit tests — schema contract, dispatch, memory
  round-trips, endianness, search, watchers, trace, palette, bus masking,
  resources, symbols, overlays, fixtures, structs, planes, slots, movies.
- [x] **Test the HTTP layer end-to-end**: `McpHttpServer` boots on a random
  port in tests (`HttpEndToEndTests`) and gets real HTTP requests —
  initialize/tools/list/ping/tools-call roundtrips + JSON-RPC errors.
- [x] **Version bump helper**: `scripts/bump-bizhawk.sh` — updates `bizhawk.build`,
  re-pins the source, and diffs the ApiHawk interface files between old and new
  commits (half of the "Bumping the BizHawk version" steps in AGENTS.md).
- [x] **`opencode` config sample**: exactly one example committed
  (`opencode.mcp.example.json`) with `tools` + `resources` client support; the
  duplicate `opencode.mcp.example copy.json` is gone.

## Misc / research

- [ ] **Verify Mono (Linux EmuHawk)**: all tests + smoke on Mono; check
  `HttpListener` and `System.Text.Json` behave (known limitation: Linux is a
  compile target but Windows is the tested host).
- [x] **Endianness per domain**: implemented — every memory tool accepts an
  explicit `endianness` param (default `auto` = the domain's native endianness,
  e.g. Z80 RAM little vs 68K RAM big on Genesis), independent of the global
  `bizhawk_set_big_endian` override.
- [ ] **Latency**: measure per-call overhead (JSON parse, UI-thread marshaling)
  for `read_memory`-heavy loops; consider a `bizhawk_read_bulk` that returns
  base64 to cut JSON size.
- [x] **Core-specific tool naming**: Genesis-only tools now carry a `genesis_`
  prefix (`bizhawk_genesis_read_plane`, `bizhawk_genesis_get_vdp_view`) so
  agents don't assume they work on every core; generic tools (memory, watchpoints,
  palette, symbols) keep core-neutral descriptions and name the 68K 24-bit bus
  masking as GEN/SMD/32X/SAT-only behavior. (2026-08-03; watchpoints stay generic
  — the feature is per-core support, not per-core concept.)
