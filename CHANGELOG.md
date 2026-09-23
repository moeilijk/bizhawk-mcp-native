# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
adheres to [Semantic Versioning](https://semver.org/).

The release job extracts the section matching the pushed tag (`## [vX.Y.Z]`)
and uses it as the GitHub release notes; if no section exists it falls back to
auto-generated notes. A versioned section is only created when a release is cut
on explicit request — otherwise changes accumulate under `## [Unreleased]`.

## [v0.3.1] - 2026-09-23

A release of the fork [moeilijk/bizhawk-mcp-native](https://github.com/moeilijk/bizhawk-mcp-native) for
[ai-assisted-speedruns](https://github.com/moeilijk/ai-assisted-speedruns): StealthC's v0.3.0 with these changes,
offered upstream as [#1](https://github.com/StealthC/bizhawk-mcp-native/pull/1) and
[#2](https://github.com/StealthC/bizhawk-mcp-native/pull/2) (the steps are not offered yet). The version number is
this fork's own; StealthC's next release may number differently.

### Fixed
- **`press_buttons` can press console buttons.** Every name got the `P<controller> ` prefix, so `Reset` and `Power`
  (and a full name such as `P1 A`) could not be pressed. A name the core lists as it is now goes to the joypad API
  without a controller; every other name keeps the prefix.

### Added
- **`frame_advance` holds buttons.** Optional `buttons` (and `controller`) are set before each of the N frames.
- **`frame_advance` plays a list of steps in one call** (`steps`: `[{buttons?, frames}]`, at most 600 frames). Each
  call leaves the emulator paused until the next one, so input sent one short step per call cut the game's sound up.
  Measured on BizHawk 2.11.1 with `SoundThrottle` on, silences of 20 ms or more per second of Super Mario Bros. 3-1:
  2.4 running unpaused (the music's own rests), 2.3 with 600-frame calls, 2.7 with 60-frame calls, 15.2 with 1-frame
  calls.

## [v0.3.0] - 2026-08-03

### Added
- **Dual-era MCP protocol** (`2025-11-25` legacy + `2026-07-28` modern):
  - `server/discover` RPC advertising `supportedVersions`, capabilities,
    server identity and instructions (required by the 2026-07-28 spec;
    served in both eras).
  - Modern (stateless) mode: a request that declares its protocol version in
    `params._meta["io.modelcontextprotocol/protocolVersion"]` (or the
    `MCP-Protocol-Version` header) is served without the `initialize`
    handshake; results carry `resultType: "complete"` and
    `_meta["io.modelcontextprotocol/serverInfo"]`.
  - Version negotiation: unsupported declared versions → `-32022`
    `UnsupportedProtocolVersionError` with `data: { supported, requested }`
    (HTTP `400`); header/body conflicts (`MCP-Protocol-Version`,
    `Mcp-Method`, `Mcp-Name`, with `=?base64?...?=` decoding) → `-32020`
    `HeaderMismatch` (HTTP `400`); unknown modern methods → HTTP `404`.
    Legacy requests keep `200` + JSON-RPC error, and `initialize` still
    answers the latest legacy revision.
- **Cacheable list results** (SEP-2549): `ttlMs` + `cacheScope` on
  `tools/list`, `prompts/list`, `resources/templates/list` and
  `server/discover` (1 h, `"public"` — the tool list is static per process,
  so clients can cache it instead of polling ~17ms + 94 schemas per refresh),
  `resources/list` (30 s) and `resources/read` (10 s) as `"private"`.
- **Code/Data Logger tools** (`cdl_*`): drives the core's real
  `ICodeDataLogger` service (the same one EmuHawk's Code Data Logger tool
  uses, reached via reflection like watchpoints) — the core ORs access flags
  into a per-domain bitmap (1 byte per address) as it runs. `cdl_start`
  blanks + installs a fresh log (gpgx blocks: "MD CART"/"68K RAM"/"Z80 RAM",
  flag bits: 0x01 Exec68k, 0x04 Data68k, 0x08/0x10 ExecZ80First/Operand,
  0x20 DataZ80, 0x40 DMASource), `cdl_stop` keeps the data,
  `cdl_get` returns per-block executed/touched byte counts, coverage
  %, flag counts and the executed address ranges (mask `"exec"`/`"any"`,
  block filter, capped ranges), `cdl_export` writes the real
  BIZHAWK-CDL-2 binary (loadable by EmuHawk's CDL tool and Ghidra scripts) or
  a text ranges listing to a host file + `bizhawk://` resource. Dead-code /
  coverage analysis for the Ghidra workflow; cores without the service get a
  clear error.
- **Agent/script optimizations** (token + round-trip savings):
  - JSON responses are now compact (no indentation — ~30-40% fewer tokens on
    every call).
  - `read_many`/`watch_read`/`search_memory` accept
    `"compact": true`: aligned values-only arrays (`read_many`: null per
    failed item + failures list; `watch_read`: names/values/changed; `search`:
    addresses only) — ~10x smaller payloads on batch reads.
  - **JSON-RPC batching**: a POST with an array of requests returns an array
    of responses in one round trip (the ~17ms fixed per-call cost is paid
    once); notifications produce no entry; per-element errors don't kill the
    batch.
  - **Raw GET endpoints** for shell-capable agents (no MCP client, no JSON):
    `GET /mcp/read/{domain}/{start}:{end}` streams raw memory bytes
    (octet-stream, hex range, same 256 KiB cap as the resource template) and
    `GET /mcp/artifacts/{id}` streams artifact file bytes (404 when unknown;
    ​400 with the error text on a bad range).
- `dump_memory` now accepts `range_start`/`range_length` to dump only a
  sub-range of a domain to a host-side file (whole domain remains the
  default) — agents can write memory to disk without pulling it into context.
- `resources/list` now reports each artifact's host `path` alongside
  `uri`/`name`/`mimeType`/`size`, so shell-capable agents (e.g. WSL) can read
  the underlying file directly (`/mnt/c/...`) instead of fetching base64 into
  context.
- `get_info` now returns a `paths` block exposing where the emulator
  runs: `install_dir` (EmuHawk's folder), `working_dir`, `temp_dir` (the
  `bizhawk-mcp` dir where `screenshot`/`dump_memory`/`start_fixture` save by
  default) and the loaded ROM's `rom_path`/`rom_dir` — agents can resolve
  relative paths against a known base instead of guessing.
- `search_memory` stateful comparative search: without `value`, `op`
  (`ne`/`lt`/`gt`/`le`/`ge`/`changed`/`unchanged`) compares against the
  domain's previous state — the first call takes a baseline (`"baseline":
  true`), then advancing frames and re-calling (narrowing with `addresses`)
  finds what changed/increased/decreased, like a classic RAM search. New
  constant-comparison ops (`lt`/`gt`/`le`/`ge`/`ne` with `value`) too. The
  reference is per-domain and cleared when the ROM changes.
- `frame_hash`: SHA1 of the current rendered frame's PNG (deterministic
  for identical output) — a cheap screen-change detector that never transfers
  pixels; returns `{sha1, frame, path, resource}`.
- `genesis_get_z80_registers`: filters the Z80 sound CPU registers
  (`Z80 pc`, `Z80 sp`, ...) out of the gpgx register table (Genesis gpgx only;
  other cores error).
- `wait_until` multi-condition mode: a `conditions` array of
  `{address|name, op, value, width?, domain?, endianness?}` waits until ALL
  hold on the SAME frame (AND), returning per-condition results — replaces
  fragile nested single waits; single-address mode unchanged.
- Z80 sound-CPU debugging on the Genesis gpgx core:
  `genesis_disassemble_z80(address, count)` disassembles code from the
  Z80 bus space (0x0000-0xFFFF; 0x2000-0x3FFF = Z80 RAM) via BizHawk's static
  `Z80ADisassembler` (the gpgx core's own disassembler only speaks 68K);
  `genesis_trace_z80(count, step, stack_words)` samples PC/SP +
  instruction at PC every step and optionally dumps little-endian 16-bit stack
  words — shows the sound driver's main loop and busy-waits. Both require the
  "Z80 BUS" domain (gpgx only); other cores error clearly.
- Z80 bus synthesis: the Genesis gpgx core has NO "Z80 BUS" domain (verified in
  the pinned `GPGX.IMemoryDomains.cs` — it is only created for SMS/GG), so the
  Z80 debug tools synthesize the bus from "MD CART"/"ROM" (bank 0, 0x0000-0x1FFF)
  + "Z80 RAM" (0x2000-0x5FFF + mirror, 0x6000+ = open bus); SMS/GG use the
  native "Z80 BUS" domain.
- Z80 bus synthesis mapping fixed after live re-QA: on the GEN gpgx core the
  Z80 executes from ITS OWN RAM at bus 0x0000-0x1FFF (the 68K uploads the
  sound driver there; the reset vector runs RAM@0x0000), aliased at
  0x2000-0x3FFF, with 0x4000+ as sound I/O/open bus — not the "0x0000 ROM
  window / 0x2000 RAM" layout first assumed. `disassemble_z80`/`trace_z80`
  now decode the real executed driver (verified against live registers).
- **Domain safety**: ApiHawk's `NamedDomainOrCurrent` silently falls back to
  the current domain when a requested name doesn't exist (reads mislabeled
  data with the wrong endianness) — the toolset now rejects unknown domain
  names up front (INVALID_PARAMS) across all memory tools.
- `start_fixture` description now documents the sampling semantics:
  rows are the post-frame state (the pre-existing current frame is never
  sampled), chaining calls with `"delay": 0` resumes seamlessly at the next
  frame (no overlap/gap) while a positive delay creates an unsampled gap, and
  CSV rows restart at 0 per file (global frame = chunk * frames + row + delay
  of earlier chunks).

### Fixed
- Zero compiler warnings: nullable-annotated the ApiHawk stubs/fakes
  (`string? domain = null`), migrated the overlay tools to the non-obsolete
  `WithSurface(DisplaySurfaceID, Action<IGuiApi>)` overload, silenced the
  intentional System.Drawing stub shadow (CS0436) and the System.Memory
  facade unification (MSB3277), and null-hardened the path/JSON call sites.
- CI: actions bumped to v5 (Node 24 — removes the Node 20 deprecation
  warning).

### Changed
- **Tool names dropped the `bizhawk_` prefix** (per MCP best practices —
  uniqueness is scoped to a single server and disambiguation across servers is
  the client's job, e.g. opencode shows them as `bizhawk_get_info` via its own
  server-name prefix; official reference servers don't self-prefix either):
  `bizhawk_read_memory` → `read_memory`, `bizhawk_watchpoint_add` →
  `watchpoint_add`, `bizhawk_cdl_start` → `cdl_start`, … — all 102 tools.
  Core-specific tools keep their core marker (rule in AGENTS.md): 
  `genesis_read_plane`, `genesis_get_vdp_view`, `genesis_trace_z80`, … The
  `bizhawk://` resource scheme and the `bizhawk-mcp` temp dir are unchanged.
- Legacy protocol version bumped `2025-06-18` → `2025-11-25` (last legacy
  revision).
- `serverInfo.version` now comes from the assembly's `<Version>` (csproj,
  single source of truth) instead of a hardcoded string, so it can never
  drift from the packaged release.

## [Unreleased]
## [v0.2.0] - 2026-08-03

### Added
- WSL↔Windows host path translation: tools that take host-side paths
  (`open_rom`, `save_state`/`load_state`, `screenshot`, `dump_memory`,
  `start_fixture`, `lua_load`, movies, planes) accept `/mnt/f/...` forms when
  EmuHawk runs on Windows (and `C:\...` forms when it runs on Linux/Mono),
  converting automatically.
- `scripts/smoke.sh`: one-command deployment verification against a running
  server (read-only: initialize/ping/tools/get_info/read/freeze/resources,
  optional `--with-lua`).
- CI: the linux flavor now runs `mono --verify-all` on the built DLL (IL
  sanity for the Mono target).
- Per-domain endianness: optional `endianness` param on all memory tools
  (default `auto` = the domain's native endianness, e.g. Z80 RAM little vs
  68K RAM big on Genesis); every read returns the endianness actually used.
- Watchpoint context dump (`watchpoint_wait context_bytes`): registers +
  PC/disasm + raw bytes around the hit address in one call.
- Fixture capture (`start_fixture`): scripted input timeline + per-frame
  samples written straight to CSV on the host disk.
- Struct reads (`read_struct`): relative-offset fields from a base
  address or symbol, per-field endianness.
- Plane decode (`genesis_read_plane`): Genesis background nametable (plane A/B)
  + 4bpp tiles + CRAM → PNG (self-contained encoder, exposed as a resource).
  Plane base auto-detected from the core's VDP view; `offset_x`/`offset_y`
  crop to a camera window.
- VDP view (`genesis_get_vdp_view`): Genesis nametable bases + dimensions from
  the core (via reflection on `UpdateVDPViewContext`, like watchpoints).
- `bizhawk://read/{domain}/{start}:{end}` resource template for raw memory reads.
- Symbols persist across restarts, scoped per ROM hash + namespace
  (`symbols_set/list/clear` accept `namespace`; `get_info` reloads on ROM change).
- Save/load quick-save slots (`save_slot`/`load_slot`, 1..10).
- Movie controls (`movie_start`/`movie_save`/`movie_stop`): load-and-play
  a .bk2 or start a new recording; feeds `start_fixture` with real inputs.
- Board info (`get_board_info`): board name, display type, game options.
- Real-HTTP end-to-end tests: `McpHttpServer` boots on a random port and is hit
  with actual requests (initialize/tools/list/ping/tools-call/errors).
- `write_range` bulk path: writes through the domain's raw pointer in a
  single waterbox crossing (up to ~400x fewer crossings) with a safe fallback.
- Sound control (`get_sound`/`set_sound`), rewind toggle
  (`enable_rewind`), frameskip (`frameskip`), framerate limit
  (`limit_framerate`), and ROM management (`open_rom`/
  `close_rom`/`reboot`).

- `lua_docs` + `bizhawk://lua-docs` / `bizhawk://lua-docs/{library}`:
  agent-friendly JSON of the Lua API docs, served live from the running
  emulator — the same `[LuaMethod]` → `LuaLibraries.Docs` chain that generates
  the tasvideos.org LuaFunctions page, with signatures AND examples (which the
  wiki omits).
- Lua scripting (`lua_exec`/`load`/`unload`/`enable`/`disable`/`list`):
  drives EmuHawk's real Lua runtime. The host (`LuaLibraries` in
  BizHawk.Client.Common) is reached via `IToolApi.GetTool("LuaConsole")` +
  reflection on its private `LuaImp` field (same pattern as watchpoints) — no
  deep reflection into the Lua machinery. `lua_exec` runs snippets through the
  same path as the console's REPL (memory API uses underscore forms:
  `read_u8`/`read_u16_be`/`read_u32_le`/`write_u8`/...); loaded scripts are pumped every frame by
  EmuHawk's frame events (even free-running emulation) and survive core
  reboots.
- `read_bulk`: contiguous range as raw base64 in one call (up to 64 KiB).
  Measured live: per-call latency is ~17ms fixed (HTTP + JSON + UI-thread
  marshaling) regardless of payload, so batching wins — 4096 bytes via
  `read_many` costs 16 calls (~290ms), via `read_bulk` costs 1 (~17ms), and
  base64 payloads are ~4x smaller than per-item JSON.
- Memory freeze (`freeze_add`/`remove`/`list`/`clear`): drives the emulator's
  real cheat engine (`MainForm.CheatList` — the same list the hex editor's
  Freeze uses), so frozen values are re-written EVERY frame by EmuHawk's main
  loop, even while emulation runs freely. Snapshot or explicit value; ranges
  (8-bit entries); optional `freeze: true` on `write_memory`/`write_many`/
  `write_range`. Lock timers, lives, health for analysis. Entries are shared
  with the Cheats window and persist on exit.
- In-memory core savestates (`memstate_save`/`load`/`list`): session-local
  byte arrays of the core state via the real `IStatable` service (reached by
  reflection on `EmulationApi.Emulator`, like watchpoints) — fast save/restore
  for search/TAS iteration, no disk, no 10-slot limit. Core state only
  (CPU + memory); framecount/lag count are NOT restored.
- MCP prompts: `prompts/list` + `prompts/get` (`memory_research`, `tas_frame`)
  with a `prompts` capability advertised on `initialize`.
- `watch_change`: advance frames until the value at an address changes
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
- Overlays now **accumulate** until `clear_overlay` (EmuHawk discards
  the ApiHawk surface each rendered frame, so the toolset re-renders the full
  list on every frame advance — like Lua scripts do). `overlay_rect`/
  `overlay_line` accept `rects`/`lines` arrays to draw many shapes in one call.
- `screenshot` gained `include_overlays: true` to compose the
  overlay/OSD layer into the PNG (EmuHawk's `ScreenshotCaptureOsd`).
- `use_memory_domain` now returns `INVALID_PARAMS` on an unknown domain
  with the list of known domains in the message (was a bare status string).
- `start_fixture` no longer holds buttons past the end of the input
  timeline: new `input_mode` (`"hold"` default — buttons persist until the next
  timeline entry; `"explicit"` — absent timeline frames mean no buttons), and
  `{"frame": N, "buttons": {}}` releases that controller's buttons mid-timeline
  in both modes. Fixes walk_stop/jump_tap/walk_turn fixtures.
- `read_many` no longer fails the whole batch on one bad item (unknown
  symbol, out-of-range address): per-item errors as `{index, requested, error}`,
  plus `read`/`failed` counts.
- `write_many` reports per-item validation failures
  (`{wrote, failed, failures: [{index, address, reason}]}`) instead of aborting
  the batch — a typo'd address fails only itself.
- `write_range` gained `fill` + `length` mode (`{wrote, address, fill}`):
  writes one repeated byte with a tiny payload — the transport-safe way to clear
  large regions (the 1440-value `values` array aborts in some MCP clients at
  ~1-2 KB before the server ever sees it).
- Read tools echo the raw `requested` address alongside the effective (bus-masked)
  `address` — `read_many` items and `read_memory`/`read_signed`/`read_float`
  responses — so off-bus arithmetic mistakes (e.g. `0x1002024`) are visible
  diagnostics instead of silent masking.

### Changed
- Genesis-only tools are now named with a system prefix so agents don't assume
  they work on every core: `bizhawk_read_plane` → `genesis_read_plane`,
  `bizhawk_get_vdp_view` → `genesis_get_vdp_view`. Generic tools keep
  neutral descriptions (68K bus masking is called out as GEN/SMD/32X/SAT-only).

## [v0.1.0] - 2026-08-02

### Added
- Baseline: native MCP server (Streamable HTTP over `HttpListener`), memory
  read/write/search, symbols, watchers, watchpoints, trace, save/load,
  screenshot, overlays, movies, userdata.
