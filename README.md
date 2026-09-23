# BizHawk MCP

A native [MCP](https://modelcontextprotocol.io) server for [BizHawk](https://github.com/TASEmulators/BizHawk)/EmuHawk, implemented as a **C# External Tool** that lives **inside the EmuHawk process**. It exposes the emulator to LLM agents (opencode, Claude Desktop, any MCP client) over a **Streamable HTTP** endpoint: read/write memory, drive the joypad, step frames, set breakpoints, take screenshots, manage savestates — the full ApiHawk surface plus deeper emulator internals (watchpoints, cheat engine, VDP state).

Warning: This is **mostly** built with LLM agents (vibe coding — see [A note on how this is built](#a-note-on-how-this-is-built)). It is intended for **research and experimentation** with LLMs controlling emulators.

## A note from the author

This project started as a **personal tool** to fill a specific need: driving
the BizHawk emulator from an AI agent during game research (memory hunting,
TAS-style experiments, parity fixtures). It is MIT-licensed and shared
openly — **fork it freely**, adapt it, and build on it.

Two things to know before you contribute:

- **The author checks the repository sporadically** — sometimes weeks or
  months go by. Issues and pull requests are read and eventually answered, but
  there is no SLA. If you need something fast, your fork is the way.
- **Pull requests are welcome**, and the bar is a good one: small and focused,
  tests green, zero build warnings, docs updated. See
  [CONTRIBUTING.md](CONTRIBUTING.md) for the full guidelines.

## Status

- **94 tools** verified end-to-end against the user's BizHawk dev build (2.11.2, commit `ed78f70a`, running on Windows via WSL).
- **222 unit tests** (`./scripts/test.sh`) pass on Linux without BizHawk — including `HttpEndToEndTests`, which boot the real `McpHttpServer` on a random port and hit it with actual HTTP requests.
- Test loop: agents drive Kid Chameleon (UE) on the Genesis gpgx waterbox core.
- Server advertises `tools` + `resources` + `prompts` capabilities (incl. `listChanged`) at `http://127.0.0.1:8767/mcp/`.

## How it works

EmuHawk's `ExternalToolManager` scans `<install>/ExternalTools/*.dll` and surfaces each `[ExternalTool]` entry in the `Tools > External Tools` menu. Clicking the entry instantiates the plugin form, which starts an `HttpListener`-based JSON-RPC server on the loopback interface. Every tool call is dispatched to a handler that marshals onto EmuHawk's UI thread, because BizHawk's ApiHawk implementations are not thread-safe off it.

```
opencode / any MCP client
        │  Streamable HTTP (JSON-RPC 2.0 over HTTP, protocol 2025-06-18)
        ▼
┌─────────────────────────────── EmuHawk process (Windows .NET 8 / Linux Mono) ─┐
│  Tools > External Tools → BizHawkMcp.dll (net48, single DLL, both runtimes)    │
│   └─ ExternalToolEntry (WinForms Form)  ◄── [RequiredApi] injected by         │
│        │                                     ApiInjector.UpdateApis           │
│        └─ McpHttpServer (HttpListener on 127.0.0.1:8767/mcp)                  │
│             │  JSON-RPC dispatch: initialize, ping, tools/*, resources/*,     │
│             │  prompts/*                                                      │
│             └─ McpToolset ──► UiDispatcher (Control.Invoke) ──► ApiHawk       │
│                  (tool schemas + handlers)      │          (Memory, Emulation, │
│                                                │           EmuClient, Joypad,  │
│                                                │           SaveState, ...)     │
│                                                ▼                               │
│                                             BizHawk cores                      │
└──────────────────────────────────────────────────────────────────────────────┘
```

### Design decisions

- **Native, in-process.** The server and the emulator share one process and one lifecycle: no separate server to spawn, nothing to reconnect, no port fights between sessions. Every opencode session connects to the **same** instance while EmuHawk runs.
- **`net48` target.** It is the only TFM that loads on both Windows (.NET 8 EmuHawk) and Linux (Mono EmuHawk). The official MCP SDKs need .NET 8+, so the protocol layer in `src/BizHawkMcp/Mcp/` is **hand-rolled** and dependency-light: in-box `HttpListener` + `System.Text.Json` (pinned to match BizHawk's `dll/`).
- **UI-thread marshaling.** All emulator API calls go through `UiDispatcher` (`Control.Invoke`, synchronous) — frame stepping, screenshots and joypad in particular break from background threads.
- **Reflection for the gaps.** ApiHawk does not cover everything, so a few tools reach deeper emulator internals by reflection: real watchpoints (`IDebuggable.MemoryCallbacks`, Genesis gpgx only), in-memory core savestates (`IStatable`), the VDP view, and the cheat engine (`MainForm.CheatList` — the same list the hex editor's Freeze uses).
- **Honest, measured cost model.** Per-call latency is ~17ms **fixed** (HTTP + JSON + UI-thread marshaling), independent of payload. Tools are designed around it: batch aggressively (`read_many`/`write_many`), contiguous regions → `read_bulk` (raw base64, one call, up to 64 KiB), whole domains → `dump_memory` or the `bizhawk://read/{domain}/{range}` resource.

## Tools (94)

Every tool is registered with a JSON schema, so clients get typed params and descriptions. Result convention: a single text blob (`content[0].text`) — JSON when the description says so, plain strings otherwise.

### Server & status

| Tool | Params | Returns |
|---|---|---|
| `ping` | — | `pong` |
| `get_info` | — | ROM name/hash, system, framecount, pause state, active memory domain + size, server URL |
| `get_board_info` | — | board name, display type (NTSC/PAL), game options (JSON) |
| `shutdown` | — | stops the server (plugin stays loaded) |

### Memory

| Tool | Params | Returns |
|---|---|---|
| `list_memory_domains` | — | all domains + sizes + known bus bases (JSON) |
| `use_memory_domain` | `domain` | confirmation |
| `read_memory` | `address` or `name`, `width` (8/16/32), `domain?`, `endianness?` | unsigned value + endianness used |
| `write_memory` | `address` or `name`, `width`, `value`, `domain?`, `endianness?`, `freeze?` | `ok` |
| `read_signed` | `address`, `width` (8/16/24/32), `domain?`, `endianness?` | signed value |
| `write_signed` | `address`, `width`, `value`, `domain?`, `endianness?` | `ok` |
| `read_float` / `write_float` | `address`, `domain?`, `endianness?` | float value / `ok` |
| `read_many` | `items` (addr/name + width/domain), `consistent?` | values (JSON, frame-consistent when `consistent`) |
| `write_many` | `items` (addr/name + width + value + `freeze?`) | `wrote N value(s)`, per-item failures |
| `read_range` | `address`, `length` (1–4096), `domain?` | hex dump |
| `read_bulk` | `address`/`name`, `length` (1–65536), `domain?` | `{address, length, base64}` (JSON) |
| `write_range` | `address`, `values` or `fill`+`length`, `domain?`, `freeze?` | `wrote N byte(s)` |
| `search_memory` | `value`, `width`, `domain?`, `range_start?`, `range_length?`, `max_results?`, `addresses?`, `endianness?` | matching addresses (JSON) |
| `hash_region` | `address`, `length`, `domain?` | SHA1 of region |
| `dump_memory` | `domain?`, `path?` | `{path, size, resource}` (JSON) |
| `ram_snapshot` / `ram_diff` | `domain?`, `label?` / `domain?`, `max_results?` | snapshot captured / changed runs with old+new hex (JSON) |
| `read_palette` | `count?`, `domain?` | hex RGB colors (JSON; GEN/SNES) |
| `read_struct` | `address`/`name`, `fields` (name/offset/width), `domain?` | fields with address/value/endianness (JSON) |
| `set_big_endian` | `enabled` | global endianness override for the session |

### Symbols

| Tool | Params | Returns |
|---|---|---|
| `symbols_set` | `symbols` (name/address/width/domain), `namespace?` | `registered N symbol(s) in "<ns>" (persisted)` |
| `symbols_list` | — | registered symbols + namespaces (JSON) |
| `symbols_clear` | `namespace?` | `cleared N symbol(s)` |

Symbols persist across restarts, scoped per ROM hash + namespace — paste Ghidra exports in once and use `"name"` instead of raw addresses everywhere (memory tools, freezes, watchpoints, fixtures).

### Emulation control

| Tool | Params | Returns |
|---|---|---|
| `frame_advance` | `count` (1–600), `buttons?` (held on each frame), `controller?` | confirmation |
| `pause` / `unpause` / `toggle_pause` | — | new paused state |
| `speed_mode` | `percent` | confirmation |
| `frameskip` | `count` | confirmation |
| `limit_framerate` | `enabled` | confirmation |
| `enable_rewind` | `enabled` | confirmation |
| `get_sound` / `set_sound` | `enabled` | state / confirmation |
| `open_rom` / `close_rom` / `reboot` | `path` / — / — | confirmation |

### Input

| Tool | Params | Returns |
|---|---|---|
| `press_buttons` | `buttons` (map; console buttons such as `Reset` as they are), `controller?` | confirmation (for the NEXT frame) |
| `get_joypad` | `controller?` | button map (JSON) |
| `host_input` | — | host keyboard/mouse (JSON) |

### CPU & tracing

| Tool | Params | Returns |
|---|---|---|
| `get_registers` | — | CPU registers (JSON, raw core keys) |
| `set_register` | `register`, `value` | confirmation (some cores don't implement writes) |
| `disassemble` | `pc`, `name?` | disassembly line |
| `trace` | `count`, `step?` | per-frame PC + disassembly samples (JSON) |
| `lag_count` | — | lag state + count (JSON) |

### Overlays & OSD

| Tool | Params | Returns |
|---|---|---|
| `overlay_text` | `x`, `y`, `text`, `color?`, `fontsize?` | draws on video output |
| `overlay_rect` | `x`, `y`, `width`, `height`, `color?`, `fill?`, `rects?` | rectangle(s) on video output |
| `overlay_line` | `x1`, `y1`, `x2`, `y2`, `color?`, `lines?` | line(s) on video output |
| `clear_overlay` | — | clears all overlays |
| `osd_message` | `message`, `duration?` | OSD message |

Overlays **accumulate** until `clear_overlay` — they are re-rendered on every frame advance, so hitboxes/labels stay on screen while the game runs. `include_overlays: true` on `screenshot` composes them into the PNG.

### Savestates

| Tool | Params | Returns |
|---|---|---|
| `save_state` / `load_state` | `path` | confirmation (disk) |
| `save_slot` / `load_slot` | `slot` (1–10) | confirmation (quick-save slots) |
| `memstate_save` | `slot` (any name) | `{slot, size, states}` (JSON) |
| `memstate_load` | `slot` | `{slot, size}` (JSON) |
| `memstate_list` | — | slots + sizes (JSON) |

`memstate_*` keeps **core state in RAM** (via the real `IStatable` service) — no disk, no slot limit; fast save/restore for search/TAS iteration. Core state only (CPU + memory; framecount/lag count are not restored).

### Freezes (cheat engine)

| Tool | Params | Returns |
|---|---|---|
| `freeze_add` | `address`/`name`, `width?`, `value?`, `length?`, `note?`, `domain?`, `endianness?` | `{address, width, value, domain}` (JSON) |
| `freeze_remove` | `note` or `address` (+`length?`/`domain?`) | `{removed}` (JSON) |
| `freeze_list` | — | freezes + count (JSON) |
| `freeze_clear` | — | `{cleared}` (JSON) |
| `lua_exec` | `code` | `{executed, result\|error}` (JSON) — REPL path |
| `lua_load` | `path` | `{path, loaded, enabled}` (JSON) |
| `lua_unload` | `path` | `{removed}` (JSON) |
| `lua_enable` / `lua_disable` | `path` | `{path, enabled}` (JSON) |
| `lua_list` | — | scripts + states (JSON) |
| `lua_docs` | `library?` | Lua API docs as JSON (signatures + examples) |

Freezes drive the emulator's **real cheat engine** (`MainForm.CheatList` — shared with the hex editor's Freeze and the Cheats window): the value is re-written EVERY frame by EmuHawk's main loop, even while emulation runs freely. Lock timers, lives, health for repeated tests. Entries persist on exit; `freeze: true` on any write tool registers on the fly.

### Watchers & watchpoints

| Tool | Params | Returns |
|---|---|---|
| `watch_add` / `remove` / `list` / `read` | `name`, `address`, `width`, `domain?`, `endianness?` | register / remove / list / values + `changed` flags (JSON) |
| `wait_until` | `address`/`name`, `op` (eq/ne/lt/gt/le/ge), `value`, `width?`, `domain?`, `timeout_frames?` | matched? + frames + value (JSON) |
| `watch_change` | `address`/`name`, `width?`, `domain?`, `timeout_frames?` | first change-frame + initial/value (JSON) |
| `watchpoint_add` | `name`, `type` (read/write/execute), `address?`, `domain?` | registered (Genesis gpgx only) |
| `watchpoint_remove` / `list` | `name` / — | removed / list (JSON) |
| `watchpoint_wait` | `timeout_frames?`, `context_bytes?` | hit: name/type/address/value (JSON; + registers/PC/disasm/bytes with `context_bytes`) |

Watchers are polling-based; **watchpoints are real hardware breakpoints** (`IDebuggable.MemoryCallbacks`) that fire the moment the core touches the address — Genesis gpgx only, every other core returns a clear error. `context_bytes: N` dumps full registers + PC/disasm + N raw bytes around the hit.

### Movies

| Tool | Params | Returns |
|---|---|---|
| `movie_info` | — | TAS movie info (JSON) |
| `movie_input` | `frame` | mnemonic input string |
| `movie_start` | `path?` | load-and-play .bk2 / start recording |
| `movie_save` | `path?` | save movie |
| `movie_stop` | — | stop movie |

### Capture & analysis

| Tool | Params | Returns |
|---|---|---|
| `screenshot` | `path?`, `include_overlays?` | `{path, resource}` (JSON) — effective path + `bizhawk://` resource URI |
| `start_fixture` | `frames`, `samples`, `inputs?`, `delay?`, `input_mode?`, `path?` | fixture CSV on host disk + `{path, frames, samples}` |

`start_fixture` is the orchestrated capture: an input timeline (with `hold`/`explicit` modes) advanced frame by frame while sampling a set of addresses/symbols per frame, written straight to CSV — no hand-rolled capture scripts needed.

### Genesis (gpgx-only)

| Tool | Params | Returns |
|---|---|---|
| `genesis_get_vdp_view` | — | plane A/B nametable bases + dimensions (JSON, via the core) |
| `genesis_read_plane` | `plane?` (A/B), `base?`, `columns?`, `rows?`, `offset_x?`, `offset_y?`, `scale?`, `path?` | nametable + 4bpp tiles + CRAM → PNG (resource URI) |

Core-specific tools are named with a system prefix on purpose; generic tools keep core-neutral behavior.

### User data

| Tool | Params | Returns |
|---|---|---|
| `userdata_set` | `key`, `value` | `stored <key>` |
| `userdata_get` | `key` | stored value |
| `userdata_clear` | `key?` | cleared/removed |

## Quick start

### Requirements

- A BizHawk install with the **same layout as the official zips** (release or dev): `EmuHawk.exe` at the root, assemblies in `dll/`. The dev build used here is pinned to commit [`ed78f70a`](bizhawk.build) (2.11.2).
- .NET SDK 8+ to build. Supported build hosts:
  - **Windows** native (`net48` loads on the .NET 8 EmuHawk runtime).
  - **WSL/Linux building for a Windows install** (the repo's original setup — EmuHawk runs on Windows, you build from WSL via `/mnt/...`).
  - **Linux** building for a Linux BizHawk (Mono EmuHawk).
  The CI builds all of them (see [CI & releases](#ci--releases-github-actions)).

### Setup (first time)

1. `cp .env.example .env`
2. Edit `.env` and set `BIZHAWK_INSTALL` to your BizHawk install directory, in the form that matches where you BUILD:
   ```dotenv
   # Windows native:        BIZHAWK_INSTALL=F:\path\to\BizHawk-dev-windows
   # WSL → Windows install: BIZHAWK_INSTALL=/mnt/f/path/to/BizHawk-dev-windows
   # Linux (Mono):          BIZHAWK_INSTALL=/opt/BizHawk-linux
   ```
   The scripts (`deploy.sh`/`test.sh`/...) and the build (`Directory.Build.props`) read it automatically. `.env` is gitignored; `.env.example` documents every option (also `BIZHAWK_MCP_HOST`/`PORT` for the runtime listener and `DOTNET_ROOT` when the SDK isn't on PATH).

### Build & deploy

```bash
./scripts/deploy.sh                 # WSL/Linux — reads BIZHAWK_INSTALL from .env
# or
powershell -File scripts/deploy.ps1 # Windows — reads .env too
```

The Release build copies `BizHawkMcp.dll` + its NuGet deps into `<install>/ExternalTools/`, which EmuHawk's `ExternalToolManager` watches with a `FileSystemWatcher` — no restart needed. Run the tests with `./scripts/test.sh` (net8.0 + xunit, no BizHawk needed). Override the install dir for a single run with `BIZHAWK_INSTALL=/path ./scripts/deploy.sh` or `-p:BizHawkInstallDir=<path>` for `dotnet build`.

### Loading in EmuHawk

1. `Tools` menu → `External Tools` → **BizHawk MCP Server** (the item appears automatically after deploy).
2. The form opens showing the listening URL; the server starts on `OnShown`.
3. On a **Release** build EmuHawk asks to trust the tool on first load (it stores a checksum in `config.ini`). Debug builds skip the prompt.

### Configuration

All of these can live in `.env` (see [Setup](#setup-first-time)) or the process environment.

| Env var | Default | Meaning |
|---|---|---|
| `BIZHAWK_INSTALL` | auto-detected (legacy paths) | BizHawk "output tree" used at build/deploy time (MSBuild reads it) |
| `BIZHAWK_MCP_HOST` | `127.0.0.1` | Bind address for the HTTP listener (read at runtime by the plugin) |
| `BIZHAWK_MCP_PORT` | `8767` | Port (read at runtime by the plugin) |
| `DOTNET_ROOT` | `~/.dotnet` auto-detect | Where the .NET SDK lives when `dotnet` isn't on PATH (WSL) |

### opencode

```jsonc
// opencode.jsonc
{
  "mcp": {
    "bizhawk": {
      "type": "remote",
      "url": "http://127.0.0.1:8767/mcp",
      "enabled": true
    }
  }
}
```

Because the server lives inside EmuHawk, every opencode session connects to the same instance — no per-session spawn, no port conflicts.

## Protocol notes

Implemented subset of MCP **Streamable HTTP** (protocol version `2025-06-18`):

- `POST /mcp` — stateless JSON-RPC 2.0 (no sessions); notifications return `202`.
- `GET /mcp` with `Accept: text/event-stream` — SSE stream with an `endpoint` event + keepalive comments; the first stream of each server lifetime carries a `notifications/tools/list_changed` message (a redeployed DLL may serve a different tool list).
- Methods: `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`, `resources/read`, `prompts/list`, `prompts/get` (`memory_research`, `tas_frame`).
- **Resources** serve binary artifacts back to the client: `screenshot` and `genesis_read_plane` save PNGs on the host (default dir `<temp>/bizhawk-mcp/`) and return a `bizhawk://` URI; `resources/read` returns the bytes as base64 `blob` with the correct mimeType. Resource templates: `bizhawk://read/{domain}/{start}:{end}` (raw memory ranges, up to 256 KiB) and `bizhawk://lua-docs/{library}` (the Lua API reference as JSON — also available as the static `bizhawk://lua-docs` and the `lua_docs` tool).
- Not implemented (yet): sessions (`mcp-session-id`), server-initiated SSE messages, resource subscriptions.

Smoke test with curl:

```bash
curl -s -X POST http://127.0.0.1:8767/mcp/ \
  -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_info","arguments":{}}}'
```

## Memory model & endianness

- **Domains** are the address spaces the core exposes (e.g. `68K RAM`, `M68K BUS`, `Z80 RAM` on Genesis). Offsets are domain-relative; bus domains take raw bus addresses. `list_memory_domains` reports `bus_base` so you can convert (68K RAM offset `0xFBC8` = bus `0xFFFBC8`).
- **Endianness is per-domain**, not per-system: on Genesis, `68K RAM`/`M68K BUS` are big-endian while `Z80 RAM` (sound CPU) is little. Every memory tool accepts `"endianness": "big" | "little" | "auto"` (default `auto` = the domain's native endianness) and every read echoes the endianness actually used. Precedence: explicit param > global `set_big_endian` override > domain default.
- **68K bus masking:** on GEN/SMD/32X/SAT bus domains, 32-bit disassembly addresses (e.g. `0xFFFFF832`) are masked to the real 24-bit bus (`0xFFF832`) like the hardware — so Ghidra addresses work as-is. Other cores reject out-of-range addresses strictly. Reads echo the raw `requested` address alongside the effective one.

## Compatibility & version pinning

- **TFM `net48`** so one DLL runs on Windows (.NET 8 via compat) and Linux (Mono). Trade-off: the official MCP SDKs need .NET 8+, hence the hand-rolled HTTP layer (in-box `HttpListener` + `System.Text.Json`, version matched to BizHawk's `dll/` folder to avoid runtime assembly conflicts).
- The plugin compiles against the DLLs of an installed build; the pinned commit is in [`bizhawk.build`](bizhawk.build). To browse the API source at that exact commit: `./scripts/fetch-source.sh` (partial clone into `bizhawk-src/`).
- To target a newer BizHawk: update `bizhawk.build`, verify the ApiHawk interface names still match (`src/BizHawk.Client.Common/Api/Interfaces/`), rebuild. `scripts/bump-bizhawk.sh` half-automates this (re-pins + diffs the interfaces).

## CI & releases (GitHub Actions)

`.github/workflows/build-and-release.yml`:

- Matrix: **flavor** (`stable` = latest BizHawk release tag from the GitHub API, `dev` = latest dev build via nightly.link) × **platform** (`win`, `linux`) — downloads the official zip/tarball, extracts `dll/`, builds the tool against it, and uploads `BizHawkMcp-<flavor>-<platform>-<bizhawk-label>.zip` (tool DLL + deps only, no BizHawk assemblies).
- Trigger: `workflow_dispatch` (manual) or pushing a `v*` tag — on tag push the zips are attached to a GitHub release (notes from `CHANGELOG.md`).
- Everything runs on `ubuntu-latest`; the Linux flavor validates the Mono target.

## A note on how this is built

Honesty clause: almost every line of this codebase was **written by AI
agents** — the author prompts, directs, reviews, and (crucially) **verifies
everything against the real emulator**. The development loop is: agent writes
code + tests → author deploys into a live BizHawk → a second, independent
agent runs a behavioral test checklist against the running emulator → bugs
found get fixed and regression-tested. That loop has caught real bugs
(an ambiguous reflection lookup, a silently disabled fast path, wrong
endianness handling) that unit tests alone missed.

So: it is "vibe coded" in origin, but the **final delivery is held to a real
standard** — clean builds with zero warnings, 200+ unit tests, documentation,
and live end-to-end verification for every feature. If you find a rough edge,
an issue with a reproducible case is the best contribution.

## Project layout

```
.env.example                   # documented configuration template (copy to .env)
.env                           # your local config (gitignored): BIZHAWK_INSTALL etc.
bizhawk.build                  # pinned BizHawk commit the tool compiles against
Directory.Build.props          # BizHawkInstallDir resolution (env → legacy → error)
LICENSE                        # MIT
CONTRIBUTING.md                 # how to open issues/PRs (async-friendly)
opencode.mcp.example.json      # opencode remote-MCP config
src/BizHawkMcp/
  BizHawkMcp.csproj            # net48, refs the installed dll/ assemblies
  ExternalToolEntry.cs         # [ExternalTool] Form : IExternalToolForm; DI + server lifecycle
  UiDispatcher.cs              # marshals API calls onto the UI thread
  McpToolset.cs                # tool schemas + handlers
  Mcp/JsonRpc.cs               # JSON-RPC 2.0 / MCP helpers
  Mcp/McpHttpServer.cs         # HttpListener-based Streamable HTTP server
scripts/
  load-env.sh                  # sources .env + locates the .NET SDK (sourced by the others)
  deploy.sh / deploy.ps1       # build + copy into <install>/ExternalTools (read .env)
  test.sh                      # unit tests (net8.0 + xunit, no BizHawk needed)
  fetch-source.sh              # optional source checkout at the pinned commit
  bump-bizhawk.sh              # half-automated BizHawk version bump
tests/BizHawkMcp.Tests/        # links product sources + ApiHawk stubs/fakes
docs/
  ARCHITECTURE.md              # components, load flow, threading, lifecycle
  MCP-PROTOCOL.md              # implemented protocol subset + curl examples
  DEVELOPMENT.md               # adding tools, testing, debugging, gotchas
  CI-RELEASES.md               # how the GitHub Actions builds/releases work
TODO.md                        # improvement ideas (protocol, tools, robustness)
AGENTS.md                      # orientation + hard constraints for AI agents
```

## Troubleshooting

- **`BizHawk install dir not found`** at build time — `BIZHAWK_INSTALL` isn't set and no auto-detected path exists. Copy `.env.example` to `.env`, set `BIZHAWK_INSTALL` (see [Setup](#setup-first-time)), or pass `-p:BizHawkInstallDir=<path>`.
- **Deploy fails with `MSB3021`** — EmuHawk locks loaded DLLs while the tool form is open. Close the **BizHawk MCP Server** form (or EmuHawk), then deploy again.
- **`dotnet: command not found`** in WSL — install the SDK via `dotnet-install.sh` (default `~/.dotnet`, auto-detected) or set `DOTNET_ROOT` in `.env`.
- **Port already in use** — set `BIZHAWK_MCP_PORT` (and `BIZHAWK_MCP_HOST`) in `.env`, and make sure the EmuHawk process that hosts the plugin gets them (export before launching EmuHawk).

## Roadmap

See [`TODO.md`](TODO.md) for the full, maintained list.

## License

[MIT](LICENSE) — use it, fork it, ship it. Contributions are welcome
(see [CONTRIBUTING.md](CONTRIBUTING.md)), just keep in mind the author checks
in from time to time, not on a schedule.