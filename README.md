# BizHawk MCP (native external tool)

A native [MCP](https://modelcontextprotocol.io) server for [BizHawk](https://github.com/TASEmulators/BizHawk)/EmuHawk, implemented as a **C# External Tool** that runs inside the EmuHawk process and exposes a **Streamable HTTP** endpoint. An LLM agent (opencode, Claude, etc.) can read/write emulator memory, drive the joypad, step frames, take screenshots and manage savestates.

Warning: This is **mostly** made using LLM agents, so it is not a polished product. It is intended for **research and experimentation** with LLMs controlling emulators.

## Why a native tool instead of the Lua bridge

The existing `mcp-bizhawk` (Node.js + `bridge.lua`) works but has structural weaknesses that this project removes:

| | `mcp-bizhawk` (Lua bridge) | `bizhawk-mcp-native` (this project) |
|---|---|---|
| Server lifecycle | Node process spawned per opencode session; port goes orphaned / `EADDRINUSE` between sessions | Lives inside EmuHawk; starts/stops with the tool |
| Bridge reconnection | Lua script connects **once** at open time; any server restart silently breaks it forever | Nothing to reconnect — the HTTP server and the emulator share one process |
| Latency | One round-trip per emulated frame (~16 ms), JSON parsed in Lua | Direct synchronous calls on the UI thread |
| opencode config | `type: local` + per-session spawn | `type: remote` + plain URL (no spawn, no port fights) |
| API surface | What the Lua translation layer exposes | Full ApiHawk surface |

## Architecture

```
opencode / any MCP client
        │  Streamable HTTP (JSON-RPC over HTTP, 2025-06-18)
        ▼
┌─────────────────────────────── EmuHawk process (Windows .NET 8 / Linux Mono) ─┐
│  External Tools menu → BizHawkMcp.dll (net48, loads on both runtimes)          │
│   └─ Form (ExternalToolEntry)  ◄── [RequiredApi] injected by ApiInjector      │
│        └─ McpHttpServer (HttpListener on 127.0.0.1:8767/mcp)                  │
│             └─ McpToolset ──► UiDispatcher ──► ApiHawk (Memory, Emulation,     │
│                                   EmuClient, Joypad, SaveState)               │
└──────────────────────────────────────────────────────────────────────────────┘
```

Every emulator API call is marshalled onto EmuHawk's UI thread (`UiDispatcher`); BizHawk's ApiHawk implementations are not safe off it.

## Requirements

- A BizHawk install with the **same layout as the official zips** (release or dev): `EmuHawk.exe` at the root, assemblies in `dll/`. The dev build here is pinned to commit [`ed78f70a`](bizhawk.build) (2.11.2).
- .NET SDK 8+ to build (builds fine on Windows **and** Linux/WSL; the target framework is `net48`, so the DLL loads on both the .NET 8 Windows runtime and Mono on Linux).

## Build & deploy

```bash
./scripts/deploy.sh                 # WSL/Linux — auto-detects the install dir
# or
powershell -File scripts/deploy.ps1 # Windows
```

`BIZHAWK_INSTALL=/path/to/BizHawk ./scripts/deploy.sh` overrides the install dir (see `Directory.Build.props` for the auto-detected paths).

The Release build copies `BizHawkMcp.dll` + its NuGet deps into `<install>/ExternalTools/`, which EmuHawk's `ExternalToolManager` watches with a `FileSystemWatcher` — no restart needed.

## Loading in EmuHawk

1. `Tools` menu → `External Tools` → **BizHawk MCP Server** (the item appears automatically after deploy).
2. The form opens showing the listening URL; the server starts on `OnShown`.
3. On a **Release** build EmuHawk asks to trust the tool on first load (it stores a checksum in `config.ini`). Debug builds skip the prompt.

## Configuration

| Env var | Default | Meaning |
|---|---|---|
| `BIZHAWK_MCP_HOST` | `127.0.0.1` | Bind address for the HTTP listener |
| `BIZHAWK_MCP_PORT` | `8767` | Port |

## opencode

```jsonc
// opencode.jsonc — replace the old "local" bizhawk MCP entry
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

Because the server lives inside EmuHawk, every opencode session connects to the **same** instance — no per-session spawn, no port conflicts, no bridge-restart ritual.

## Tools

| Tool | Params | Returns |
|---|---|---|
| `bizhawk_ping` | — | `pong` |
| `bizhawk_get_info` | — | ROM name/hash, system, framecount, pause state, active memory domain + size, server URL |
| `bizhawk_get_board_info` | — | board name, display type (NTSC/PAL), game options (JSON) |
| `bizhawk_read_memory` | `address` **ou** `name`, `width` (8/16/32), `domain?` | unsigned value |
| `bizhawk_write_memory` | `address` **ou** `name`, `width`, `value`, `domain?` | `ok` |
| `bizhawk_read_signed` | `address`, `width` (8/16/24/32), `domain?` | signed value |
| `bizhawk_write_signed` | `address`, `width`, `value`, `domain?` | `ok` |
| `bizhawk_read_float` | `address`, `domain?` | float value |
| `bizhawk_write_float` | `address`, `value`, `domain?` | `ok` |
| `bizhawk_read_many` | `items` (addr/name + width/domain), `consistent?` | values (JSON, frame-consistent when `consistent`) |
| `bizhawk_write_range` | `address`, `values` (bytes), `domain?` | `wrote N byte(s)` |
| `bizhawk_write_many` | `items` (addr/name + width + value) | `wrote N value(s)` |
| `bizhawk_start_fixture` | `frames`, `samples`, `inputs?`, `delay?`, `path?` | fixture CSV on host disk + `{path, frames, samples}` |
| `bizhawk_read_struct` | `address`/`name`, `fields` (name/offset/width), `domain?` | fields with address/value/endianness (JSON) |
| `bizhawk_dump_memory` | `domain?`, `path?` | `{path, size, resource}` (JSON) |
| `bizhawk_ram_snapshot` | `domain?`, `label?` | snapshot captured |
| `bizhawk_ram_diff` | `domain?`, `max_results?` | changed runs with old/new hex (JSON) |
| `bizhawk_symbols_set` | `symbols` (name/address/width/domain), `namespace?` | `registered N symbol(s) in "<ns>" (persisted)` |
| `bizhawk_symbols_list` | — | registered symbols + namespaces (JSON) |
| `bizhawk_symbols_clear` | `namespace?` | `cleared N symbol(s)` |
| `bizhawk_read_palette` | `count?`, `domain?` | hex RGB colors (JSON; GEN/SNES) |
| `bizhawk_read_plane` | `plane?` (A/B), `base?`, `columns?`, `rows?`, `scale?`, `path?` | decoded nametable → PNG (Genesis; resource URI) |
| `bizhawk_hash_region` | `address`, `length`, `domain?` | SHA1 of region |
| `bizhawk_read_range` | `address`, `length` (1–4096), `domain?` | hex dump |
| `bizhawk_use_memory_domain` | `domain` | confirmation |
| `bizhawk_list_memory_domains` | — | all domains + sizes + known bus bases (JSON) |
| `bizhawk_search_memory` | `value`, `width` (8/16/32), `domain?`, `range_start?`, `range_length?`, `max_results?`, `addresses?` | matching addresses (JSON) |
| `bizhawk_set_big_endian` | `enabled` | confirmation |
| `bizhawk_press_buttons` | `buttons` (map), `controller?` | confirmation |
| `bizhawk_frame_advance` | `count` (1–600) | confirmation |
| `bizhawk_pause` | — | new paused state |
| `bizhawk_unpause` | — | new paused state |
| `bizhawk_toggle_pause` | — | new paused state |
| `bizhawk_speed_mode` | `percent` | confirmation |
| `bizhawk_get_joypad` | `controller?` | button map (JSON) |
| `bizhawk_get_registers` | — | CPU registers (JSON) |
| `bizhawk_set_register` | `register`, `value` | confirmation |
| `bizhawk_disassemble` | `pc`, `name?` | disassembly line |
| `bizhawk_lag_count` | — | lag state + count (JSON) |
| `bizhawk_overlay_text` | `x`, `y`, `text`, `color?`, `fontsize?` | draws on video output |
| `bizhawk_overlay_rect` | `x`, `y`, `width`, `height`, `color?`, `fill?` | rectangle on video output |
| `bizhawk_overlay_line` | `x1`, `y1`, `x2`, `y2`, `color?` | line on video output |
| `bizhawk_clear_overlay` | — | clears drawn text |
| `bizhawk_osd_message` | `message`, `duration?` | OSD message |
| `bizhawk_movie_info` | — | TAS movie info (JSON) |
| `bizhawk_movie_input` | `frame` | mnemonic input string |
| `bizhawk_movie_start` | `path?` | load-and-play .bk2 / start recording |
| `bizhawk_movie_save` | `path?` | save movie |
| `bizhawk_movie_stop` | — | stop movie |
| `bizhawk_host_input` | — | host keyboard/mouse (JSON) |
| `bizhawk_userdata_set` | `key`, `value` | `stored <key>` |
| `bizhawk_userdata_get` | `key` | stored value |
| `bizhawk_userdata_clear` | `key?` | cleared/removed |
| `bizhawk_watch_add` | `name`, `address`, `width`, `domain?` | watcher registered |
| `bizhawk_watch_remove` | `name` | removed/not found |
| `bizhawk_watch_list` | — | watchers + current values (JSON) |
| `bizhawk_watch_read` | — | values + `changed` flags (JSON) |
| `bizhawk_wait_until` | `address`, `op` (eq/ne/lt/gt/le/ge), `value`, `width?`, `domain?`, `timeout_frames?` | matched? + frames + value (JSON) |
| `bizhawk_watchpoint_add` | `name`, `type` (read/write/execute), `address?`, `domain?` | registered (Genesis gpgx only) |
| `bizhawk_watchpoint_remove` | `name` | removed/not found |
| `bizhawk_watchpoint_list` | — | registered watchpoints (JSON) |
| `bizhawk_watchpoint_wait` | `timeout_frames?`, `context_bytes?` | hit: name/type/address/value (JSON; +registers/PC/disasm/bytes with context_bytes) |
| `bizhawk_trace` | `count`, `step?` | per-frame PC + disassembly samples (JSON) |
| `bizhawk_screenshot` | `path?` | `{path, resource}` (JSON) — effective path + resource URI |
| `bizhawk_save_state` | `path` | confirmation |
| `bizhawk_load_state` | `path` | confirmation |
| `bizhawk_save_slot` | `slot` (1–10) | confirmation |
| `bizhawk_load_slot` | `slot` (1–10) | confirmation |
| `bizhawk_shutdown` | — | stops the server |

## Protocol notes

Implemented subset of MCP **Streamable HTTP** (protocol version `2025-06-18`):

- `POST /mcp` — stateless JSON-RPC 2.0 (no sessions); notifications return `202`.
- `GET /mcp` with `Accept: text/event-stream` — SSE stream with an `endpoint` event + keepalive comments.
- Methods: `initialize`, `ping`, `tools/list`, `tools/call`, `resources/list`, `resources/read`.
- **Resources** serve binary artifacts back to the client: `bizhawk_screenshot` saves a PNG on the host (default dir `<temp>/bizhawk-mcp/`) and returns a `bizhawk://…` URI; `resources/read` returns the bytes as base64 `blob` with the `image/png` mimeType.
- Not implemented (yet): sessions, server-initiated messages, prompts.

## Endianness

- ApiHawk's `SetBigEndian` has no getter, so the plugin tracks its own state.
- The default is core-aware: **big-endian on Genesis/Mega Drive, 32X, SNES, Super Game Boy, N64 and Saturn; little-endian elsewhere** (GB/GBA/NES/PCE/PSX/…). The default is applied once per loaded system.
- `bizhawk_set_big_endian` overrides the default for the session; `bizhawk_get_info` reports the effective endianness.

## Compatibility & version pinning

- **TFM `net48`** (same as the repo's `ExternalToolProjects/`) so one DLL runs on Windows (.NET 8 via compat) and Linux (Mono). Trade-off: the official MCP SDKs need .NET 8+, hence the hand-rolled HTTP layer (uses only in-box `HttpListener` + `System.Text.Json`).
- The plugin is compiled against the DLLs of an installed build; the pinned commit is in [`bizhawk.build`](bizhawk.build). To browse the API source at that exact commit: `./scripts/fetch-source.sh` (partial clone into `bizhawk-src/`).
- To target a newer BizHawk: update `bizhawk.build`, verify the `ApiHawk` interface names still match (`src/BizHawk.Client.Common/Api/Interfaces/`), rebuild.

## CI & releases (GitHub Actions)

`.github/workflows/build-and-release.yml`:

- Matrix over **stable** (latest BizHawk release tag from the GitHub API) and **dev** (latest dev build via nightly.link) — downloads the official Windows zip, extracts `dll/`, builds the tool against it, and uploads `BizHawkMcp-<flavor>-<bizhawk-tag>.zip` (tool DLL + deps only, no BizHawk assemblies).
- Trigger: `workflow_dispatch` (manual) or pushing a `v*` tag — on tag push the zips are attached to a GitHub release.
- The whole build runs on `ubuntu-latest`; no Windows runners needed.

## Project layout

```
bizhawk.build                  # pinned BizHawk commit the tool compiles against
Directory.Build.props          # BizHawkInstallDir auto-detection
opencode.mcp.example.json      # opencode remote-MCP config
src/BizHawkMcp/
  BizHawkMcp.csproj            # net48, refs the installed dll/ assemblies
  ExternalToolEntry.cs         # [ExternalTool] Form : IExternalToolForm; DI + server lifecycle
  UiDispatcher.cs              # marshals API calls onto the UI thread
  McpToolset.cs                # tool schemas + handlers
  Mcp/JsonRpc.cs               # JSON-RPC 2.0 / MCP helpers
  Mcp/McpHttpServer.cs         # HttpListener-based Streamable HTTP server
scripts/
  deploy.sh / deploy.ps1       # build + copy into <install>/ExternalTools
  test.sh                      # unit tests (net8.0 + xunit, no BizHawk needed)
  fetch-source.sh              # optional source checkout at the pinned commit
tests/BizHawkMcp.Tests/        # links product sources + ApiHawk stubs/fakes
docs/
  ARCHITECTURE.md              # components, load flow, threading, lifecycle
  MCP-PROTOCOL.md              # implemented protocol subset + curl examples
  DEVELOPMENT.md               # adding tools, testing, debugging, gotchas
  CI-RELEASES.md               # how the GitHub Actions builds/releases work
TODO.md                        # improvement ideas (protocol, tools, robustness)
AGENTS.md                      # orientation + hard constraints for AI agents
```

## Roadmap

See [`TODO.md`](TODO.md) for the full, maintained list. Current highlights:

- [ ] Sessions (`mcp-session-id`) / SSE server-initiated messages (not required by opencode today)
- [ ] VRAM plane decode → PNG (`read_plane`: nametable + tiles + palette)
- [ ] `fixture_capture(scenario.json)` — declarative input+read-per-frame → CSV
- [ ] End-to-end HTTP test (spins up the real `HttpListener` on a random port)
- [ ] `run_lua` (fragile, deferred — deep reflection into EmuHawk's Lua runtime)

Done recently: real watchpoints (Genesis gpgx only), symbols (Ghidra↔BizHawk
names), memory dumps as resources, batch read/write, RAM snapshots/diffs,
geometric overlays, VDP palette reader, 68K bus address masking, core-aware
endianness, `bus_base` per domain.
