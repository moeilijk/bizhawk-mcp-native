# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/).

The release job extracts the section matching the pushed tag (`## [vX.Y.Z]`)
and uses it as the GitHub release notes; if no section exists it falls back to
auto-generated notes. A versioned section is only created when a release is cut
on explicit request — otherwise changes accumulate under `## [Unreleased]`.

## [Unreleased]

### Added
- Per-domain endianness: optional `endianness` param on all memory tools
  (default `auto` = the domain's native endianness, e.g. Z80 RAM little vs
  68K RAM big on Genesis); every read returns the endianness actually used.
- Watchpoint context dump (`bizhawk_watchpoint_wait context_bytes`): registers +
  PC/disasm + raw bytes around the hit address in one call.
- Fixture capture (`bizhawk_start_fixture`): scripted input timeline + per-frame
  samples written straight to CSV on the host disk.
- Struct reads (`bizhawk_read_struct`): relative-offset fields from a base
  address or symbol, per-field endianness.
- Plane decode (`bizhawk_genesis_read_plane`): Genesis background nametable (plane A/B)
  + 4bpp tiles + CRAM → PNG (self-contained encoder, exposed as a resource).
  Plane base auto-detected from the core's VDP view; `offset_x`/`offset_y`
  crop to a camera window.
- VDP view (`bizhawk_genesis_get_vdp_view`): Genesis nametable bases + dimensions from
  the core (via reflection on `UpdateVDPViewContext`, like watchpoints).
- `bizhawk://read/{domain}/{start}:{end}` resource template for raw memory reads.
- Symbols persist across restarts, scoped per ROM hash + namespace
  (`symbols_set/list/clear` accept `namespace`; `get_info` reloads on ROM change).
- Save/load quick-save slots (`bizhawk_save_slot`/`load_slot`, 1..10).
- Movie controls (`bizhawk_movie_start`/`movie_save`/`movie_stop`): load-and-play
  a .bk2 or start a new recording; feeds `start_fixture` with real inputs.
- Board info (`bizhawk_get_board_info`): board name, display type, game options.
- Real-HTTP end-to-end tests: `McpHttpServer` boots on a random port and is hit
  with actual requests (initialize/tools/list/ping/tools-call/errors).
- `bizhawk_write_range` bulk path: writes through the domain's raw pointer in a
  single waterbox crossing (up to ~400x fewer crossings) with a safe fallback.
- Sound control (`bizhawk_get_sound`/`set_sound`), rewind toggle
  (`bizhawk_enable_rewind`), frameskip (`bizhawk_frameskip`), framerate limit
  (`bizhawk_limit_framerate`), and ROM management (`bizhawk_open_rom`/
  `close_rom`/`reboot`).

### Fixed
- `freeze_*` and the `write_range` bulk path failed on the real core with
  `AmbiguousMatchException`: the real `MemoryDomainList` has TWO `Item`
  indexers (`this[int]` inherited + `this[string]`), so `GetProperty("Item")`
  is ambiguous. The string indexer is now found by parameter type — this also
  means the bulk write fast path (~400x fewer waterbox crossings) actually
  engages on gpgx instead of silently falling back to per-byte pokes.
  (Found by the 2026-08-03 live QA; regression-tested with a fake that
  mirrors both indexers.)
- `write_memory`/`write_many`/`write_range` with `freeze: true` now resolve
  the domain and cheat list BEFORE writing, so a freeze failure can never
  leave the memory written while the call errored.
- `freeze_add` validates that an explicit `value` fits the width (single
  address) or is 0..255 (range fill) instead of silently truncating.
- `read_many`/`search_memory` ignored configured endianness (little-endian
  reads) — both now resolve the effective endianness.
- Inert watchpoints: `MemoryCallbackImpl.AddressMask` was `null`, so
  address-specific watchpoints never fired on the real core.
- `wait_until` now accepts a symbol `name` (not just a raw address).
- `read_palette` decoded Genesis CRAM with R/B in the wrong bit positions —
  the hardware format is `0x0RRR0GGG0BBB` (R at bits 1-3, B at 9-11).
- `overlay_text`/`overlay_rect`/`overlay_line` were broken: the Gui calls ran
  without a display surface, so `Get2DRenderer(null)` threw (rect/line) or drew
  into the invisible EmuCore buffer (text). They now draw on the Client surface
  via `WithSurface(DisplaySurfaceID.Client, ...)`.
- Overlays now **accumulate** until `bizhawk_clear_overlay` (EmuHawk discards
  the ApiHawk surface each rendered frame, so the toolset re-renders the full
  list on every frame advance — like Lua scripts do). `overlay_rect`/
  `overlay_line` accept `rects`/`lines` arrays to draw many shapes in one call.
- `bizhawk_screenshot` gained `include_overlays: true` to compose the
  overlay/OSD layer into the PNG (EmuHawk's `ScreenshotCaptureOsd`).
- `bizhawk_use_memory_domain` now returns `INVALID_PARAMS` on an unknown domain
  with the list of known domains in the message (was a bare status string).
- `bizhawk_start_fixture` no longer holds buttons past the end of the input
  timeline: new `input_mode` (`"hold"` default — buttons persist until the next
  timeline entry; `"explicit"` — absent timeline frames mean no buttons), and
  `{"frame": N, "buttons": {}}` releases that controller's buttons mid-timeline
  in both modes. Fixes walk_stop/jump_tap/walk_turn fixtures.
- `bizhawk_read_many` no longer fails the whole batch on one bad item (unknown
  symbol, out-of-range address): per-item errors as `{index, requested, error}`,
  plus `read`/`failed` counts.
- `bizhawk_write_many` reports per-item validation failures
  (`{wrote, failed, failures: [{index, address, reason}]}`) instead of aborting
  the batch — a typo'd address fails only itself.
- `bizhawk_write_range` gained `fill` + `length` mode (`{wrote, address, fill}`):
  writes one repeated byte with a tiny payload — the transport-safe way to clear
  large regions (the 1440-value `values` array aborts in some MCP clients at
  ~1-2 KB before the server ever sees it).
- Read tools echo the raw `requested` address alongside the effective (bus-masked)
  `address` — `read_many` items and `read_memory`/`read_signed`/`read_float`
  responses — so off-bus arithmetic mistakes (e.g. `0x1002024`) are visible
  diagnostics instead of silent masking.

### Added
- Lua scripting (`bizhawk_lua_exec`/`load`/`unload`/`enable`/`disable`/`list`):
  drives EmuHawk's real Lua runtime. The host (`LuaLibraries` in
  BizHawk.Client.Common) is reached via `IToolApi.GetTool("LuaConsole")` +
  reflection on its private `LuaImp` field (same pattern as watchpoints) — no
  deep reflection into the Lua machinery. `lua_exec` runs snippets through the
  same path as the console's REPL; loaded scripts are pumped every frame by
  EmuHawk's frame events (even free-running emulation) and survive core
  reboots.
- `bizhawk_read_bulk`: contiguous range as raw base64 in one call (up to 64 KiB).
  Measured live: per-call latency is ~17ms fixed (HTTP + JSON + UI-thread
  marshaling) regardless of payload, so batching wins — 4096 bytes via
  `read_many` costs 16 calls (~290ms), via `read_bulk` costs 1 (~17ms), and
  base64 payloads are ~4x smaller than per-item JSON.
- Memory freeze (`bizhawk_freeze_add`/`remove`/`list`/`clear`): drives the emulator's
  real cheat engine (`MainForm.CheatList` — the same list the hex editor's
  Freeze uses), so frozen values are re-written EVERY frame by EmuHawk's main
  loop, even while emulation runs freely. Snapshot or explicit value; ranges
  (8-bit entries); optional `freeze: true` on `write_memory`/`write_many`/
  `write_range`. Lock timers, lives, health for analysis. Entries are shared
  with the Cheats window and persist on exit.
- In-memory core savestates (`bizhawk_memstate_save`/`load`/`list`): session-local
  byte arrays of the core state via the real `IStatable` service (reached by
  reflection on `EmulationApi.Emulator`, like watchpoints) — fast save/restore
  for search/TAS iteration, no disk, no 10-slot limit. Core state only
  (CPU + memory); framecount/lag count are NOT restored.
- MCP prompts: `prompts/list` + `prompts/get` (`memory_research`, `tas_frame`)
  with a `prompts` capability advertised on `initialize`.
- `bizhawk_watch_change`: advance frames until the value at an address changes
  from its call-time baseline (first-change-frame semantics, no target value
  needed — unlike `wait_until`).
- `tools/list` change notifications: capabilities advertise `listChanged: true`
  and the first SSE stream of each server lifetime carries a
  `notifications/tools/list_changed` message (a redeployed DLL may serve a
  different tool list).
- `scripts/bump-bizhawk.sh`: half-automates the BizHawk version bump — updates
  `bizhawk.build`, re-pins the source, and diffs the ApiHawk interface files
  between the old and new commits.
- `bizhawk://read/{domain}/{range}` resource cap raised to 256 KiB (was 64 KiB).

### Changed
- Genesis-only tools are now named with a system prefix so agents don't assume
  they work on every core: `bizhawk_read_plane` → `bizhawk_genesis_read_plane`,
  `bizhawk_get_vdp_view` → `bizhawk_genesis_get_vdp_view`. Generic tools keep
  neutral descriptions (68K bus masking is called out as GEN/SMD/32X/SAT-only).

## [v0.1.0] - 2026-08-02

### Added
- Baseline: native MCP server (Streamable HTTP over `HttpListener`), memory
  read/write/search, symbols, watchers, watchpoints, trace, save/load,
  screenshot, overlays, movies, userdata.
