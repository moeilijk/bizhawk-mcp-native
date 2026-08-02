# BizHawk MCP (native external tool)

A native [MCP](https://modelcontextprotocol.io) server for [BizHawk](https://github.com/TASEmulators/BizHawk)/EmuHawk, implemented as a **C# External Tool** that runs inside the EmuHawk process and exposes a **Streamable HTTP** endpoint. An LLM agent (opencode, Claude, etc.) can read/write emulator memory, drive the joypad, step frames, take screenshots and manage savestates.

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
| `bizhawk_read_memory` | `address`, `width` (8/16/32), `domain?` | unsigned value |
| `bizhawk_write_memory` | `address`, `width`, `value`, `domain?` | `ok` |
| `bizhawk_read_range` | `address`, `length` (1–4096), `domain?` | hex dump |
| `bizhawk_use_memory_domain` | `domain` | confirmation |
| `bizhawk_set_big_endian` | `enabled` | confirmation |
| `bizhawk_press_buttons` | `buttons` (map), `controller?` | confirmation |
| `bizhawk_frame_advance` | `count` (1–600) | confirmation |
| `bizhawk_screenshot` | `path` | confirmation |
| `bizhawk_save_state` | `path` | confirmation |
| `bizhawk_load_state` | `path` | confirmation |
| `bizhawk_shutdown` | — | stops the server |

## Protocol notes

Implemented subset of MCP **Streamable HTTP** (protocol version `2025-06-18`):

- `POST /mcp` — stateless JSON-RPC 2.0 (no sessions); notifications return `202`.
- `GET /mcp` with `Accept: text/event-stream` — SSE stream with an `endpoint` event + keepalive comments.
- Methods: `initialize`, `ping`, `tools/list`, `tools/call`.
- Not implemented (yet): sessions, server-initiated messages, resources, prompts.

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
  fetch-source.sh              # optional source checkout at the pinned commit
docs/
  ARCHITECTURE.md              # components, load flow, threading, lifecycle
  MCP-PROTOCOL.md              # implemented protocol subset + curl examples
  DEVELOPMENT.md               # adding tools, debugging, version bumps, gotchas
  CI-RELEASES.md               # how the GitHub Actions builds/releases work
AGENTS.md                      # orientation + hard constraints for AI agents
```

## Roadmap

- [ ] Memory domain enumeration (ApiHawk has no domain list; currently reports current domain + size)
- [ ] SSE server-initiated messages / sessions
- [ ] Resources (e.g. savestate slots as MCP resources)
- [ ] End-to-end smoke test against a pinned ROM in CI (headless)
