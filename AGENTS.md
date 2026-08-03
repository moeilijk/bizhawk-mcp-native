# AGENTS.md

Guidance for AI agents (and humans) working on this repository.

- **Documentation index:** `docs/` — `ARCHITECTURE.md`, `MCP-PROTOCOL.md`, `DEVELOPMENT.md`, `CI-RELEASES.md`. When in doubt, read the relevant doc before editing. Improvement ideas live in `TODO.md`.
- **Current status (2026-08-02):** 60 tools verified end-to-end against the user's BizHawk dev build (2.11.2, commit `ed78f70a`, Windows via WSL). Server advertises `tools` + `resources` capabilities over `http://127.0.0.1:8767/mcp/`; 132 unit tests (`./scripts/test.sh`) pass on Linux without BizHawk. Deployed to `F:\projects\kid\emulators\BizHawk-dev-windows\ExternalTools\`. Test loop: an agent tests against Kid Chameleon (UE) on the Genesis gpgx waterbox core.

## What this is

A native [MCP](https://modelcontextprotocol.io) server for BizHawk/EmuHawk implemented as a C# **External Tool** (`net48`, single DLL) that runs inside the EmuHawk process and exposes a Streamable HTTP endpoint (`http://127.0.0.1:8767/mcp` by default). See `README.md` for the rationale vs. the Node/Lua `mcp-bizhawk` bridge.

## Architecture in one paragraph

`ExternalToolEntry` (a WinForms `Form`) is the discovery entry point: EmuHawk's `ExternalToolManager` scans `<install>/ExternalTools/*.dll`, requires exactly one type implementing `IExternalToolForm` annotated with `[ExternalTool(...)]`, and calls `ApiInjector.UpdateApis` to fill properties marked `[RequiredApi]`/`[OptionalApi]` by reflection. The form starts `McpHttpServer` (an `HttpListener` loop) which dispatches JSON-RPC into `McpToolset`; every handler runs on the UI thread through `UiDispatcher` because BizHawk's ApiHawk implementations are not thread-safe off it.

## Hard constraints (do not break these)

1. **TFM must stay `net48`.** It is the only target that loads on both Windows (.NET 8 EmuHawk) and Linux (Mono EmuHawk). Consequently the official MCP SDKs (which need .NET 8+) are **off-limits** — the protocol layer in `src/BizHawkMcp/Mcp/` is hand-rolled and must stay dependency-light (in-box `HttpListener` + `System.Text.Json` via NuGet).
2. **Never put `[RequiredApi]` on an `ApiContainer` property.** `ApiInjector.UpdateApis` only resolves interface types registered by the provider; a miss returns `false` and the tool silently fails to load. The registered set (see `ApiContainer.cs` in the pinned commit) is: `ICommApi`, `IEmuClientApi`, `IEmulationApi`, `IGuiApi`, `IInputApi`, `IJoypadApi`, `IMemoryApi`, `IMovieApi`, `ISaveStateApi`, `ISQLiteApi`, `IUserDataApi`, `IToolApi` — **`IMemorySaveStateApi`/`IMemoryEventsApi` are NOT registered**, so they can't be `[RequiredApi]`.
3. **All emulator API calls must go through `UiDispatcher`** (frame stepping, screenshots and joypad in particular break from background threads). New tools: write the handler in `McpToolset` using the `_ui.Invoke(...)` pattern.
4. **`System.Text.Json` version must match BizHawk's `dll/` folder** (currently 9.0.0) to avoid runtime assembly conflicts in the host process.
5. **ApiHawk property names come from the pinned BizHawk commit** (`bizhawk.build`). E.g. `IGameInfo` exposes `Name`/`Hash`/`System` (not `RomName`/`RomHash`), `IEmulationApi.GetGameInfo()` returns it, `IMemoryApi` has `ReadByte/ReadU16/ReadU32` + `WriteU8/U16/U32` + signed/float + `HashRegion` + `GetMemoryDomainList`, `IEmuClientApi` has `DoFrameAdvance`/`Screenshot`/`IsPaused`/`Pause`/`Unpause`/`TogglePause`/`SpeedMode`, `IJoypadApi` has `Set(IReadOnlyDictionary<string,bool>, int?)`/`Get(int?)`. Note `IGuiApi.DrawText` has **no `fontsize`** (that's `DrawString`), and `IMovieApi.GetInputAsMnemonic` takes **only `frame`**. Verify against `src/BizHawk.Client.Common/Api/Interfaces/` of the pinned commit before touching tools.

## Building

```bash
# WSL/Linux or Windows; SDK 8+ required (WSL: ~/.dotnet via dotnet-install.sh)
./scripts/deploy.sh            # builds Release and copies into <install>/ExternalTools
./scripts/test.sh              # unit tests (net8.0 + xunit; no BizHawk needed)
dotnet build src/BizHawkMcp/BizHawkMcp.csproj -c Release   # build only
```

- Install dir auto-detection lives in `Directory.Build.props`; override with `-p:BizHawkInstallDir=/path` or the `BIZHAWK_INSTALL` env var used by `deploy.sh`.
- `net48` builds on Linux thanks to `Microsoft.NETFramework.ReferenceAssemblies` (already in the csproj). No Windows runner needed.
- The test project links the product's `.cs` files and stubs the ApiHawk interfaces (`tests/BizHawkMcp.Tests/Stubs/`) — **keep the stubs in sync** when a tool uses new API surface, and add a test when adding a tool (see `docs/DEVELOPMENT.md`).
- CI equivalent: `.github/workflows/build-and-release.yml` (matrix stable/dev, `-p:BizHawkInstallDir="$(pwd)/bizhawk"`, `dotnet test` step).

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

Recipe with code in `docs/DEVELOPMENT.md`. In short: add a `Tool(...)` descriptor to `McpToolset.ToolSchemas`, add the dispatch arm in `Call(...)`, implement the handler using the `_ui.Invoke(...)` pattern and the param helpers (`Required`, `RequireLong`, `RequireInt`, `RequireULong`, `RequireString`, `OptionalString`). Structured results → `JsonRpc.Pretty(...)`. Tools with all-optional params must accept `null` args (don't call `Required`). Add a unit test in `tests/BizHawkMcp.Tests/` (extend the stubs/fakes if new API surface is used) and run `./scripts/test.sh`.

## Bumping the BizHawk version

1. Update `bizhawk.build` with the new commit (e.g. from `EmuHawk.exe` → Properties → Details → ProductVersion, or the release tag).
2. `./scripts/fetch-source.sh` to check out the API source; grep for changed signatures in `src/BizHawk.Client.Common/Api/Interfaces/`.
3. Update the DLLs the build references — the installed build's `dll/` folder must be that version (update `Directory.Build.props` paths if the install changed).
4. Rebuild + verify with the curl smoke test; keep the MCP protocol version in `JsonRpc.MCP_PROTOCOL_VERSION` current.

## Known limitations (skeleton state)

- No in-memory savestates: `IMemorySaveStateApi` is not registered by the provider, so only disk-based `bizhawk_save_state`/`load_state` exist.
- Watchers/breakpoints are **polling-based** (`bizhawk_watch_*`, `bizhawk_wait_until`) OR **real watchpoints** (`bizhawk_watchpoint_*`) — the latter use `IDebuggable.MemoryCallbacks` reached via reflection on `EmulationApi.DebuggableCore` (private `[OptionalService]`). **Only the Genesis gpgx waterbox core exposes memory callbacks**; every other core returns a clear `INVALID_PARAMS` error. No per-instruction stepping exists (`CanStep` is false on gpgx) — `bizhawk_trace` samples PC per frame.
- Watchpoint callbacks fire on the **core's thread**; they only set volatile flags, and `bizhawk_watchpoint_wait` does the frame-advancing on the UI thread. Do NOT call any emulator API from inside a callback. `watchpoint_wait` with `context_bytes: N` (>0) dumps registers + PC/disasm + N raw bytes around the hit on success. Also note `MemoryCallbackImpl.AddressMask` must stay `0xFFFFFFFF` (not null) or address-specific watchpoints silently never fire.
- Streamable HTTP subset: no sessions, no server-initiated messages, `GET` SSE is endpoint + keepalive only.
- `bizhawk_frame_advance` pumps `Application.DoEvents` between frames so the UI stays responsive; long counts (max 600) are intentionally capped.
- `bizhawk_screenshot`/`save_state`/`load_state` paths are host-side (Windows paths when EmuHawk runs on Windows). `bizhawk_screenshot` returns the effective path plus a `bizhawk://` resource URI; `resources/read` serves the PNG as base64.
- `bizhawk_search_memory` matches via the same endianness semantics as `bizhawk_read_memory` (per-domain default, overridable per call with `"endianness"` or globally with `bizhawk_set_big_endian`).
- Endianness defaults are domain-aware: `68K RAM`/`M68K BUS` are big on
  GEN/SMD/32X/SAT while `Z80 RAM` is little (sound CPU), SNES/N64 big. The
  toolset tracks its own state (`_bigEndianOverride`) because ApiHawk's
  `SetBigEndian` has no getter; `bizhawk_set_big_endian` is the global override.

## Domain & address conventions (hard-won, Genesis/Kid Chameleon)

These were the root cause of several false "bugs" reported by test agents — read
before debugging anything on the Genesis core.

- **RAM base:** 68K work RAM is 64KB at bus `0xFF0000-0xFFFFFF`. In `M68K BUS`,
  use raw bus addresses (`0xFFFBC8`); in `68K RAM`, use 0-based offsets
  (`0xFBC8`). The two domains read the same physical RAM — writes cross-visible.
  `bizhawk_list_memory_domains` reports `bus_base` (e.g. 68K RAM = 0xFF0000).
- **32-bit disassembly addresses:** the game code (and Ghidra, which models the
  68000 as 32-bit) references RAM as `0xFFFFxxxx` (e.g. `move.l (0xfffff832).w`)
  — the real 24-bit bus masks them, so `0xFFFFF832 == 0xFFF832`. `ValidateAddress`
  applies this mask **only on 68K-family systems** (GEN/SMD/32X/SAT bus domains);
  other cores keep strict out-of-range rejection. Agents can copy addresses
  straight from Ghidra into `bizhawk_read_memory`.
- **Register names are prefixed:** the gpgx core names them `M68K PC`, `M68K A0`,
  `M68K SR`, `M68K SP`, … — NOT `PC`. `bizhawk_get_registers` shows the raw keys;
  `bizhawk_trace`/`FindRegister` match by suffix. `bizhawk_set_register` needs the
  exact key (e.g. `M68K PC`) — and gpgx does NOT implement register writes at all
  (`SetCpuRegister` throws `NotImplementedException`, swallowed by ApiHawk).
- **Endianness is per-domain, not just per-system:** every memory tool accepts an
  optional `"endianness": "big" | "little" | "auto"` param (default `"auto"`).
  `auto` = the **domain's** native endianness: `Z80 RAM` is little even on
  big-endian Genesis (68K vs Z80 sound CPU), while `68K RAM`/`M68K BUS` are big.
  Precedence: explicit param > `bizhawk_set_big_endian` override > domain default.
  All multi-byte reads/writes are byte-level (`ReadValue`/`WriteValue`/
  `ReadFloatRaw`) so the result never depends on ApiHawk's global `SetBigEndian`
  state — a per-call param always wins. Every read tool also returns the
  `"endianness"` actually used, so clients never misread a value.
- **Palette formats:** Genesis CRAM = 16-bit BGR with 3 bits/channel packed
  `0BBB0GGG0RRR0` (R in bits 0-2), big-endian bytes; SNES CGRAM = 16-bit BGR555
  (R in bits 0-4), little-endian bytes. `bizhawk_read_palette` handles both,
  endianness-independent of `set_big_endian`.
- **Validated Kid Chameleon addresses:** mainFunction = `0xFFFBCA` (u16),
  cameraX = `0xFFF81C` (u32), isFading = `0xFFFBCE`, levelLayout = `0xFFA652`,
  playerSprPtr = `0xFFF85E`; RAM also holds resident code (sound driver, e.g.
  `0x60FB`/`0x60FC` = BRA opcodes around `0xFFF81C+`).

## Deploy quirks (do not get bitten twice)

- EmuHawk locks loaded DLLs: redeploy fails with MSB3021 (non-fatal) while the
  tool form is open. Close the form (or EmuHawk), then `./scripts/deploy.sh`.
- The csproj copy target (`CopyToExternalTools`) broke twice: (1) a condition
  with unquoted `$(BizHawkInstallDir)` → MSB4090; (2) an `Inputs/Outputs` target
  whose `ItemGroup` lived inside the target → the copy was silently skipped.
  Keep the `_ToolOutput` ItemGroup **outside** the target; no Inputs/Outputs.
- `dotnet` may live in `~/.dotnet` (WSL): `deploy.sh`/`test.sh` add it to PATH
  automatically.

## CI notes

- Workflow triggers: `workflow_dispatch` and `push` of `v*` tags. Stable flavor resolves the latest release via the GitHub API; dev flavor downloads the nightly build from nightly.link.
- Packaging copies only the tool + NuGet deps from `bin/Release` (BizHawk assemblies are `Private=false` and never shipped).
- The release job attaches all matrix zips to the tag's GitHub release (`gh release`).
