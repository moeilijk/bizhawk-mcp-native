# AGENTS.md

Guidance for AI agents (and humans) working on this repository.

- **Documentation index:** `docs/` — `ARCHITECTURE.md`, `MCP-PROTOCOL.md`, `DEVELOPMENT.md`, `CI-RELEASES.md`. When in doubt, read the relevant doc before editing.
- **Current status (2026-08-02):** skeleton complete and **verified end-to-end** against the user's BizHawk dev build (2.11.2, commit `ed78f70a`, Windows via WSL). `initialize`, `tools/list`, `tools/call` (`bizhawk_ping`, `bizhawk_get_info`, `bizhawk_read_memory`) all return correct responses over `http://127.0.0.1:8767/mcp/`. Deployed to `F:\projects\kid\emulators\BizHawk-dev-windows\ExternalTools\`.

## What this is

A native [MCP](https://modelcontextprotocol.io) server for BizHawk/EmuHawk implemented as a C# **External Tool** (`net48`, single DLL) that runs inside the EmuHawk process and exposes a Streamable HTTP endpoint (`http://127.0.0.1:8767/mcp` by default). See `README.md` for the rationale vs. the Node/Lua `mcp-bizhawk` bridge.

## Architecture in one paragraph

`ExternalToolEntry` (a WinForms `Form`) is the discovery entry point: EmuHawk's `ExternalToolManager` scans `<install>/ExternalTools/*.dll`, requires exactly one type implementing `IExternalToolForm` annotated with `[ExternalTool(...)]`, and calls `ApiInjector.UpdateApis` to fill properties marked `[RequiredApi]`/`[OptionalApi]` by reflection. The form starts `McpHttpServer` (an `HttpListener` loop) which dispatches JSON-RPC into `McpToolset`; every handler runs on the UI thread through `UiDispatcher` because BizHawk's ApiHawk implementations are not thread-safe off it.

## Hard constraints (do not break these)

1. **TFM must stay `net48`.** It is the only target that loads on both Windows (.NET 8 EmuHawk) and Linux (Mono EmuHawk). Consequently the official MCP SDKs (which need .NET 8+) are **off-limits** — the protocol layer in `src/BizHawkMcp/Mcp/` is hand-rolled and must stay dependency-light (in-box `HttpListener` + `System.Text.Json` via NuGet).
2. **Never put `[RequiredApi]` on an `ApiContainer` property.** `ApiInjector.UpdateApis` only resolves interface types registered by the provider; a miss returns `false` and the tool silently fails to load.
3. **All emulator API calls must go through `UiDispatcher`** (frame stepping, screenshots and joypad in particular break from background threads). New tools: write the handler in `McpToolset` using the `_ui.Invoke(...)` pattern.
4. **`System.Text.Json` version must match BizHawk's `dll/` folder** (currently 9.0.0) to avoid runtime assembly conflicts in the host process.
5. **ApiHawk property names come from the pinned BizHawk commit** (`bizhawk.build`). E.g. `IGameInfo` exposes `Name`/`Hash`/`System` (not `RomName`/`RomHash`), `IEmulationApi.GetGameInfo()` returns it, `IMemoryApi` has `ReadByte/ReadU16/ReadU32` + `WriteU8/U16/U32`, `IEmuClientApi` has `DoFrameAdvance`/`Screenshot`/`IsPaused`, `IJoypadApi.Set(IReadOnlyDictionary<string,bool>, int?)`. Verify against `src/BizHawk.Client.Common/Api/Interfaces/` of the pinned commit before touching tools.

## Building

```bash
# WSL/Linux or Windows; SDK 8+ required (WSL: ~/.dotnet via dotnet-install.sh)
./scripts/deploy.sh            # builds Release and copies into <install>/ExternalTools
dotnet build src/BizHawkMcp/BizHawkMcp.csproj -c Release   # build only
```

- Install dir auto-detection lives in `Directory.Build.props`; override with `-p:BizHawkInstallDir=/path` or the `BIZHAWK_INSTALL` env var used by `deploy.sh`.
- `net48` builds on Linux thanks to `Microsoft.NETFramework.ReferenceAssemblies` (already in the csproj). No Windows runner needed.
- CI equivalent: `.github/workflows/build-and-release.yml` (matrix stable/dev, `-p:BizHawkInstallDir="$(pwd)/bizhawk"`).

## Manual verification loop

1. `./scripts/deploy.sh` — DLL lands in `<install>/ExternalTools/` (watched by the FileSystemWatcher).
2. In EmuHawk: `Tools > External Tools > BizHawk MCP Server`. Release builds prompt to trust the tool once.
3. Smoke test with curl (server is stateless JSON-RPC over POST; full sequence in `docs/MCP-PROTOCOL.md`):
   ```bash
   curl -s -X POST http://127.0.0.1:8767/mcp/ \
     -H 'Content-Type: application/json' \
     -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'
   curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
     -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"bizhawk_get_info","arguments":{}}}'
   ```
4. EmuHawk's form shows request errors in its log box; check it if a call misbehaves.

## Adding a tool

Recipe with code in `docs/DEVELOPMENT.md`. In short: add a `Tool(...)` descriptor to `McpToolset.ToolSchemas`, add the dispatch arm in `Call(...)`, implement the handler using the `_ui.Invoke(...)` pattern and the param helpers (`Required`, `RequireLong`, `RequireInt`, `RequireULong`, `RequireString`, `OptionalString`). Structured results → `JsonRpc.Pretty(...)`.

## Bumping the BizHawk version

1. Update `bizhawk.build` with the new commit (e.g. from `EmuHawk.exe` → Properties → Details → ProductVersion, or the release tag).
2. `./scripts/fetch-source.sh` to check out the API source; grep for changed signatures in `src/BizHawk.Client.Common/Api/Interfaces/`.
3. Update the DLLs the build references — the installed build's `dll/` folder must be that version (update `Directory.Build.props` paths if the install changed).
4. Rebuild + verify with the curl smoke test; keep the MCP protocol version in `JsonRpc.MCP_PROTOCOL_VERSION` current.

## Known limitations (skeleton state)

- No memory domain list in the API: `bizhawk_get_info` reports current domain + size only; `UseMemoryDomain` is the way to switch.
- Streamable HTTP subset: no sessions, no server-initiated messages, `GET` SSE is endpoint + keepalive only.
- `bizhawk_frame_advance` pumps `Application.DoEvents` between frames so the UI stays responsive; long counts (max 600) are intentionally capped.
- `bizhawk_screenshot`/`save_state`/`load_state` paths are host-side (Windows paths when EmuHawk runs on Windows).

## CI notes

- Workflow triggers: `workflow_dispatch` and `push` of `v*` tags. Stable flavor resolves the latest release via the GitHub API; dev flavor downloads the nightly build from nightly.link.
- Packaging copies only the tool + NuGet deps from `bin/Release` (BizHawk assemblies are `Private=false` and never shipped).
- The release job attaches all matrix zips to the tag's GitHub release (`gh release`).
