# AGENTS.md

Guidance for AI agents (and humans) working on this repository.

- **Documentation index:** `docs/` — `ARCHITECTURE.md`, `MCP-PROTOCOL.md`, `DEVELOPMENT.md`, `CI-RELEASES.md`. When in doubt, read the relevant doc before editing. Improvement ideas live in `TODO.md`.
- **Current status (2026-08-03):** 94 tools verified end-to-end against the user's BizHawk dev build (2.11.2, commit `ed78f70a`, Windows via WSL). Server advertises `tools` + `resources` + `prompts` capabilities (incl. `listChanged`) over `http://127.0.0.1:8767/mcp/`, **dual-era protocol** (legacy `2025-11-25` `initialize` + modern `2026-07-28` stateless via `_meta`/`MCP-Protocol-Version`; `server/discover`, `-32020`/`-32022` errors, `ttlMs`/`cacheScope` caching hints — see `docs/MCP-PROTOCOL.md`); 264 unit tests (`./scripts/test.sh`) pass on Linux without BizHawk — including real-HTTP end-to-end tests (HttpEndToEndTests) that boot the real `McpHttpServer` on a random port. Deployed to `F:\projects\kid\emulators\BizHawk-dev-windows\ExternalTools\`. Test loop: an agent tests against Kid Chameleon (UE) on the Genesis gpgx waterbox core.

## What this is

A native [MCP](https://modelcontextprotocol.io) server for BizHawk/EmuHawk implemented as a C# **External Tool** (`net48`, single DLL) that runs inside the EmuHawk process and exposes a Streamable HTTP endpoint (`http://127.0.0.1:8767/mcp` by default). See `README.md` for how it works and its design decisions.

## Architecture in one paragraph

`ExternalToolEntry` (a WinForms `Form`) is the discovery entry point: EmuHawk's `ExternalToolManager` scans `<install>/ExternalTools/*.dll`, requires exactly one type implementing `IExternalToolForm` annotated with `[ExternalTool(...)]`, and calls `ApiInjector.UpdateApis` to fill properties marked `[RequiredApi]`/`[OptionalApi]` by reflection. The form starts `McpHttpServer` (an `HttpListener` loop) which dispatches JSON-RPC into `McpToolset`; every handler runs on the UI thread through `UiDispatcher` because BizHawk's ApiHawk implementations are not thread-safe off it.

## Hard constraints (do not break these)

1. **TFM must stay `net48`.** It is the only target that loads on both Windows (.NET 8 EmuHawk) and Linux (Mono EmuHawk). Consequently the official MCP SDKs (which need .NET 8+) are **off-limits** — the protocol layer in `src/BizHawkMcp/Mcp/` is hand-rolled and must stay dependency-light (in-box `HttpListener` + `System.Text.Json` via NuGet).
2. **Never put `[RequiredApi]` on an `ApiContainer` property.** `ApiInjector.UpdateApis` only resolves interface types registered by the provider; a miss returns `false` and the tool silently fails to load. The registered set (see `ApiContainer.cs` in the pinned commit) is: `ICommApi`, `IEmuClientApi`, `IEmulationApi`, `IGuiApi`, `IInputApi`, `IJoypadApi`, `IMemoryApi`, `IMovieApi`, `ISaveStateApi`, `ISQLiteApi`, `IUserDataApi`, `IToolApi` — **`IMemorySaveStateApi`/`IMemoryEventsApi` are NOT registered**, so they can't be `[RequiredApi]`.
3. **All emulator API calls must go through `UiDispatcher`** (frame stepping, screenshots and joypad in particular break from background threads). New tools: write the handler in `McpToolset` using the `_ui.Invoke(...)` pattern.
4. **`System.Text.Json` version must match BizHawk's `dll/` folder** (currently 9.0.0) to avoid runtime assembly conflicts in the host process.
5. **ApiHawk property names come from the pinned BizHawk commit** (`bizhawk.build`). E.g. `IGameInfo` exposes `Name`/`Hash`/`System` (not `RomName`/`RomHash`), `IEmulationApi.GetGameInfo()` returns it, `IMemoryApi` has `ReadByte/ReadU16/ReadU32` + `WriteU8/U16/U32` + signed/float + `HashRegion` + `GetMemoryDomainList`, `IEmuClientApi` has `DoFrameAdvance`/`Screenshot`/`IsPaused`/`Pause`/`Unpause`/`TogglePause`/`SpeedMode`, `IJoypadApi` has `Set(IReadOnlyDictionary<string,bool>, int?)`/`Get(int?)`. Note `IGuiApi.DrawText` has **no `fontsize`** (that's `DrawString`), and `IMovieApi.GetInputAsMnemonic` takes **only `frame`**. Verify against `src/BizHawk.Client.Common/Api/Interfaces/` of the pinned commit before touching tools.
6. **ALWAYS convert numbers (decimal↔hex, widths, masks, offsets) with a command or script** — e.g. `python3 -c "print(hex(16785444))"` or a one-liner in the shell — **never by hand**. Hand arithmetic has caused real bugs in this repo (a wrong `0xFF2506` vs `0x272F06` sample address during B3 reproduction, and a wrong mask example in a tool description: `0x1000424` was claimed to mask to `0x2024`, it masks to `0x424`). Every hex address in a description, test, or message must be produced/verified by a script before being written down.
7. **Core-specific tools must say so in their NAME** (`bizhawk_genesis_*` for Genesis-gpgx-only tools like `genesis_read_plane`/`genesis_get_vdp_view`); generic tools (memory read/write, watchpoints, palette, symbols…) must keep core-neutral descriptions — the 68K 24-bit bus masking only exists on GEN/SMD/32X/SAT bus domains, so it must be described as such, never as universal behavior.

## Building

```bash
# WSL/Linux or Windows; SDK 8+ required (WSL: ~/.dotnet via dotnet-install.sh)
./scripts/deploy.sh            # builds Release and copies into <install>/ExternalTools
./scripts/test.sh              # unit tests (net8.0 + xunit; no BizHawk needed)
dotnet build src/BizHawkMcp/BizHawkMcp.csproj -c Release   # build only
```

- Install dir resolution lives in `Directory.Build.props` (order: `-p:BizHawkInstallDir` → env `BIZHAWK_INSTALL` → legacy auto-detected paths → clear error). The shell scripts source the project's `.env` via `scripts/load-env.sh` (copy `.env.example` → `.env`; `.env` is gitignored); `deploy.ps1` parses it too. `BIZHAWK_MCP_HOST`/`BIZHAWK_MCP_PORT` are read at runtime by the plugin.
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

## Working with subagents (do this)

- **Use subagents to go deep on any subject/behavior**: investigating a game
  mechanic, understanding how a BizHawk feature works, or researching a
  question against the pinned source — delegate to a focused subagent instead
  of accumulating all that context in the main thread. A subagent focused on
  one task does better work and carries less bias than the main agent.
- **Use subagents to verify behavioral tests / live QA**: after deploying a
  new build, delegate the verification (the live-emulator test loop) to a
  subagent with a detailed checklist (expected results per item, restore-slot
  rules, hex-conversion rule). This gives an independent "second opinion" —
  it has caught real bugs the main agent missed (e.g. the
  `MemoryDomainList` double-indexer `AmbiguousMatchException`, and the
  `memstate_save` empty-slot acceptance).
- Write subagent prompts with: the exact tools to use, verified numbers
  (converted by script — see Hard constraints #6), the expected result per
  item, and how to restore the emulator state afterwards.

## Adding a tool

Recipe with code in `docs/DEVELOPMENT.md`. In short: add a `Tool(...)` descriptor to `McpToolset.ToolSchemas`, add the dispatch arm in `Call(...)`, implement the handler using the `_ui.Invoke(...)` pattern and the param helpers (`Required`, `RequireLong`, `RequireInt`, `RequireULong`, `RequireString`, `OptionalString`). Structured results → `JsonRpc.Pretty(...)`. Tools with all-optional params must accept `null` args (don't call `Required`). Add a unit test in `tests/BizHawkMcp.Tests/` (extend the stubs/fakes if new API surface is used) and run `./scripts/test.sh`.

## Bumping the BizHawk version

1. Update `bizhawk.build` with the new commit (e.g. from `EmuHawk.exe` → Properties → Details → ProductVersion, or the release tag).
2. `./scripts/fetch-source.sh` to check out the API source; grep for changed signatures in `src/BizHawk.Client.Common/Api/Interfaces/`.
3. Update the DLLs the build references — the installed build's `dll/` folder must be that version (update `Directory.Build.props` paths if the install changed).
4. Rebuild + verify with the curl smoke test; keep the MCP protocol version in `JsonRpc.MCP_PROTOCOL_VERSION` current.

## Known limitations (skeleton state)

- **Lua docs** (`bizhawk_lua_docs` + `bizhawk://lua-docs[/{library}]`): agent-friendly
  JSON of the Lua API served from the RUNNING emulator — the same `[LuaMethod]`
  → `LuaLibraries.Docs` chain that generates tasvideos.org/Bizhawk/LuaFunctions,
  with examples the wiki omits. Use it instead of fetching the wiki page.
- **Lua** (`bizhawk_lua_*`): the host is `LuaLibraries` (BizHawk.Client.Common,
  compile-time) owned by the Lua Console tool; the plugin reaches it via the
  REGISTERED `IToolApi.GetTool("LuaConsole")` + reflection on the private
  `LuaImp` field (single-field reflection, like watchpoints). `lua_exec` runs
  through the same public `ExecuteString` path as the console's REPL. Loaded
  scripts are pumped every frame by EmuHawk's frame events (`ResumeScripts` +
  frame callbacks in the main loop) — they run even when emulation runs
  freely, and survive core reboots (`Restart()` re-enables `Enabled` scripts
  from `ScriptList`). First `lua_*` call opens the Lua Console window.
- **Freeze** (`bizhawk_freeze_*`): drives the emulator's real cheat engine —
  `MainForm.CheatList`, the same list the hex editor's Freeze uses (reached via
  the plugin form's `Owner` = MainForm, or `GlobalWin.MainForm` fallback).
  EmuHawk pulses the list EVERY frame in its main loop, so freezes apply even
  while emulation runs freely (no toolset involvement). Entries are shared with
  the Cheats window and persist on exit; `freeze_clear` wipes the whole list
  including manual cheats. Range freezes create one 8-bit Cheat per byte.
- In-memory savestates: `IMemorySaveStateApi` is not registered by the provider, so the plugin reaches the core's real **`IStatable` service via reflection** instead (`EmulationApi.Emulator` private property → `ServiceProvider.GetService<IStatable>()` — same pattern as watchpoints): `bizhawk_memstate_save`/`load`/`list` keep session-local core-state byte arrays (no disk, no 10-slot limit; CPU+memory only — framecount/lag count are NOT restored, documented in the tool). Disk-based `bizhawk_save_state`/`load_state` and the emulator's **quick-save slots** (`bizhawk_save_slot`/`load_slot`, 1..10, via `ISaveStateApi.SaveSlot/LoadSlot`) also exist.
- Movie controls: `bizhawk_movie_start` (with `path` = load-and-play a .bk2; without = start recording for the loaded ROM), `bizhawk_movie_save`, `bizhawk_movie_stop`. `bizhawk_get_board_info` reports board name/display type/game options (game revision).
- `bizhawk_genesis_get_vdp_view` returns the Genesis nametable bases + dims from the core (Genesis gpgx only; error otherwise).
- Symbols persist across EmuHawk restarts via the plugin's user data store, **scoped per ROM hash + namespace** (key `mcp.symbols`, shape `{romHash: {namespace: [symbols]}}`); saved on every `symbols_set`/`symbols_clear`, reloaded automatically when the ROM changes (`get_info`). Default namespace `"default"`; agents on the same ROM partition with explicit namespaces (`"ghidra"`, `"fixture"`, …). `symbols_clear` accepts `namespace` to clear just one. Everything else (watchers, watchpoints, endianness override) is session-local.
- `bizhawk_start_fixture` is the orchestrated fixture capture: input timeline + per-frame samples + CSV on the host disk. It frame-advances (pausing/unpausing like `frame_advance`) and samples after each frame; max 600 frames. `bizhawk_read_struct` reads relative-offset fields from a base/symbol in one pass.
- **`write_range` bulk path:** ApiHawk's `WriteByteRange` loops `PokeByte` per byte — on gpgx that's one waterbox interop call per byte (slow for hundreds of bytes). `WriteRange` first tries `TryBulkWrite`, which reaches the domain's raw `Data` pointer (via reflection on `MemoryApi.DomainList[name]`, like watchpoints) and does ONE `Marshal.Copy` inside a single `Enter`/`Exit` — up to ~400x fewer crossings — falling back to `WriteByteRange` for domains without a pointer.
- Watchers/breakpoints are **polling-based** (`bizhawk_watch_*`, `bizhawk_wait_until`, `bizhawk_watch_change`) OR **real watchpoints** (`bizhawk_watchpoint_*`) — the latter use `IDebuggable.MemoryCallbacks` reached via reflection on `EmulationApi.DebuggableCore` (private `[OptionalService]`). **Only the Genesis gpgx waterbox core exposes memory callbacks**; every other core returns a clear `INVALID_PARAMS` error. No per-instruction stepping exists (`CanStep` is false on gpgx) — `bizhawk_trace` samples PC per frame.
- Watchpoint callbacks fire on the **core's thread**; they only set volatile flags, and `bizhawk_watchpoint_wait` does the frame-advancing on the UI thread. Do NOT call any emulator API from inside a callback. `watchpoint_wait` with `context_bytes: N` (>0) dumps registers + PC/disasm + N raw bytes around the hit on success. Also note `MemoryCallbackImpl.AddressMask` must stay `0xFFFFFFFF` (not null) or address-specific watchpoints silently never fire.
- Streamable HTTP subset (dual-era): no sessions, no server-initiated messages, `GET` SSE is endpoint + keepalive only (legacy clients). Modern `2026-07-28` requests are stateless: version in `params._meta["io.modelcontextprotocol/protocolVersion"]` or the `MCP-Protocol-Version` header; modern results add `resultType` + `_meta.serverInfo`; cacheable lists carry `ttlMs`/`cacheScope`; unsupported version → `-32022` + HTTP 400, header/body mismatch → `-32020` + HTTP 400, unknown modern method → HTTP 404 (legacy stays 200 + error).
- `bizhawk_frame_advance` pumps `Application.DoEvents` between frames so the UI stays responsive; long counts (max 600) are intentionally capped.
- `bizhawk_screenshot`/`save_state`/`load_state` paths are host-side (Windows paths when EmuHawk runs on Windows). `bizhawk_screenshot` returns the effective path plus a `bizhawk://` resource URI; `resources/read` serves the PNG as base64. `include_overlays: true` composes the overlay/OSD layer into the PNG (EmuHawk's `ScreenshotCaptureOsd` → `CaptureOSD()`).
- **Overlay tools MUST draw on the Client surface** (`DisplaySurfaceID.Client` via `WithSurface`): `GuiApi.Get2DRenderer(null)` throws when no surface is selected, and `EmuCore` draws into the core framebuffer which is not visible in the window. `overlay_text/rect/line` all go through `WithSurface(Client, ...)`; `osd_message` (`AddMessage`) needs no surface. EmuHawk **discards the ApiHawk surface after each rendered frame** (like the Lua scripts that redraw every frame), so overlays keep a toolset-side list and are re-rendered on every frame advance (`AdvanceFrame()` → `RedrawOverlays()`) — they **accumulate** until `bizhawk_clear_overlay`. `overlay_rect`/`overlay_line` also accept a `rects`/`lines` array to draw many shapes in one call.
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
- **Palette formats:** Genesis CRAM = 16-bit `0x0RRR0GGG0BBB` (R at bits 1-3,
  G 5-7, B 9-11), big-endian bytes; SNES CGRAM = 16-bit BGR555 (R at bits 0-4),
  little-endian bytes. `bizhawk_read_palette` handles both. **`bizhawk_genesis_read_plane`**
  decodes a Genesis background nametable (plane A/B) + 8×8 4bpp tiles + CRAM →
  PNG. The plane base is **auto-detected from the core's VDP view** when `base`
  isn't given (`genesis_get_vdp_view` exposes NTA/NTB via reflection on
  `UpdateVDPViewContext()`, same pattern as watchpoints; Kid Chameleon uses
  plane A at 0x0000, NOT the typical 0xC000 — fallback constants 0xC000/0xE000
  only apply when the core doesn't expose the view). `offset_x`/`offset_y`
  (tiles) crop to a camera window (~40×28 visible tiles; the rest of the 64×32
  nametable is uninitialized "garbage" — not a bug). Genesis tiles are NOT
  plane-per-byte: each tile row is 4 bytes holding TWO packed 4-bit pixels each
  (high nibble = left pixel); pixel color = `(byte[x>>1] >> ((x&1)?0:4)) & 0xF`,
  tile base = `tileIndex * 0x20 + row*4`. Nametable entry (16-bit BE): bit15
  priority, bits14-13 palette block (×16 CRAM), bit12 V-flip, bit11 H-flip,
  bits10-0 tile index. The PNG encoder is self-contained (DeflateStream, no
  System.Drawing) so it runs on net48 and Linux. Note: the gpgx API does NOT
  expose the individual VDP registers (only the nametable bases via the view).
- **Validated Kid Chameleon addresses:** mainFunction = `0xFFFBCA` (u16),
  cameraX = `0xFFF81C` (u32), isFading = `0xFFFBCE`, levelLayout = `0xFFA652`,
  playerSprPtr = `0xFFF85E`; RAM also holds resident code (sound driver, e.g.
  `0x60FB`/`0x60FC` = BRA opcodes around `0xFFF81C+`).

## Deploy quirks (do not get bitten twice)

- EmuHawk locks loaded DLLs: redeploy fails with MSB3021 (non-fatal) while the
  tool form is open. Close the form (or EmuHawk), then `./scripts/deploy.sh`.
- **Per-call latency is ~17ms FIXED** (HTTP + JSON + UI-thread marshaling),
  independent of payload: one `read_memory` == one `read_many` of 256 items ==
  17ms. Agents should batch aggressively; contiguous regions →
  `bizhawk_read_bulk` (base64, one call); whole domains → `dump_memory` or the
  `bizhawk://read/{domain}/{range}` resource.
- **`MemoryDomainList` has TWO `Item` indexers** (`this[int]` inherited from
  `ReadOnlyCollection<MemoryDomain>` + `this[string]` declared) — `GetProperty("Item")`
  throws `AmbiguousMatchException`. Any reflection that resolves a domain by
  name must find the string indexer by parameter type (`FindStringIndexer` in
  McpToolset). This bit the freeze tools AND silently disabled the `write_range`
  bulk fast path (it swallowed the exception and fell back) until the 2026-08-03
  live QA; the test fake now mirrors both indexers as a regression guard.
- The csproj copy target (`CopyToExternalTools`) broke twice: (1) a condition
  with unquoted `$(BizHawkInstallDir)` → MSB4090; (2) an `Inputs/Outputs` target
  whose `ItemGroup` lived inside the target → the copy was silently skipped.
  Keep the `_ToolOutput` ItemGroup **outside** the target; no Inputs/Outputs.
- `dotnet` may live in `~/.dotnet` (WSL): `deploy.sh`/`test.sh` add it to PATH
  automatically.

## Changelog convention

- Keep a Changelog in `CHANGELOG.md` (Keep a Changelog + SemVer format). Every
  feature/fix commit that is user-visible **must** also add a bullet under
  `## [Unreleased]` (`### Added` / `### Fixed` / `### Changed`).
- **Never create a versioned section, bump the version, tag, or commit a
  release on your own initiative.** Changes always accumulate under
  `## [Unreleased]`; if you add `## [vX.Y.Z]` sections by yourself, every
  iteration gets recorded as a new release.
- Only when the user **explicitly asks to cut a release** (e.g. "crie a versão
  v0.2.0", "fazer bump", "faça o release"): move the `## [Unreleased]` bullets
  under a new `## [vX.Y.Z] - YYYY-MM-DD` section, commit, then create and push
  the `vX.Y.Z` tag. Nothing else is required.
- The release job reads the section whose heading exactly matches the pushed
  tag (`## [<tag>]`) and uses it as the GitHub release notes; if no such
  section exists it falls back to `--generate-notes`, so a forgotten changelog
  never breaks the release.
- `workflow_dispatch` only builds and uploads artifacts; **a GitHub release is
  created only by pushing a `v*` tag**.

## CI notes

- Workflow triggers: `workflow_dispatch` and `push` of `v*` tags. Matrix is flavor (`stable`/`dev`) × platform (`win`/`linux`): stable resolves the latest release (win-x64.zip + linux-x64.tar.gz assets) via the GitHub API; dev downloads the `BizHawk-dev-{windows,linux}` nightly artifacts from nightly.link. Each zip carries a `build-info.json` with the BizHawk version (stable) / commit (dev) it was built against.
- Packaging copies only the tool + NuGet deps from `bin/Release` (BizHawk assemblies are `Private=false` and never shipped).
- The release job attaches all matrix zips to the tag's GitHub release (`gh release`); notes come from the `## [<tag>]` section of `CHANGELOG.md` (fallback: `--generate-notes`).
