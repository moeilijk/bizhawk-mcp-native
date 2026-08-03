# Development

## Prerequisites

- .NET SDK 8+ (Windows: any install; WSL: `~/.dotnet` via the official `dotnet-install.sh`, no sudo needed).
- A BizHawk install with the official zip layout (`EmuHawk.exe` + `dll/`), auto-detected in `Directory.Build.props` or overridden with `-p:BizHawkInstallDir=<path>`.

## Unit tests

```bash
./scripts/test.sh          # net8.0 + xunit; runs on Linux/CI, no BizHawk needed
dotnet test tests/BizHawkMcp.Tests/BizHawkMcp.Tests.csproj
```

The test project **links in the product's `.cs` files** (`McpToolset`, `JsonRpc`, `McpHttpServer`, `IHostApis`, `IUiDispatcher`) and provides:
- `Stubs/BizHawkStubs.cs` — minimal `BizHawk.Client.Common` ApiHawk interfaces (only the members `McpToolset` uses; keep in sync when adding API surface, see constraint 5).
- `Stubs/EmulationCommonStubs.cs` — minimal `BizHawk.Emulation.Common` service interfaces (`IDebuggable`, `IMemoryCallbackSystem`, `IMemoryCallback`, …) used by the watchpoint path.
- `Stubs/SystemStubs.cs` — the net48 `System.Windows.Forms`/`System.Drawing` types used by the linked code (`Application.DoEvents`, `ColorTranslator`).
- `Fakes.cs` — in-memory fakes (`FakeMemoryApi`, `FakeEmuClientApi`, …) wired into a `FakeApis : IHostApis`; the `InlineDispatcher` runs handlers on the calling thread. `FakeApis.EnableWatchpoints()` wires a `FakeDebuggable` whose `FakeMemoryCallbacks.Fire()` simulates core memory accesses.

Tests cover: tool schema contract (every schema has a dispatch arm), JSON-RPC dispatch (parse errors, unknown methods, notifications), memory round-trips, core-aware endianness + override, search narrowing, screenshot→resource base64 round-trip, pause/frame-advance semantics.

Rules when touching tool code:
- Any tool whose params are all optional must accept `null` args (e.g. `GetJoypad`, `UserDataClear` use the optional-args pattern, not `Required`).
- The dispatcher runs everything through `_ui.Invoke`, so handlers are pure w.r.t. threading — keep it that way (tests rely on `InlineDispatcher`).

## Build & deploy

```bash
./scripts/deploy.sh                      # WSL/Linux — build + copy to ExternalTools
BIZHAWK_INSTALL=/path ./scripts/deploy.sh
powershell -File scripts/deploy.ps1      # Windows
```

The Release build's `CopyToExternalTools` target copies the tool + NuGet deps into `<install>/ExternalTools/`; EmuHawk picks it up via `FileSystemWatcher` (reopen the `Tools > External Tools` menu if the item doesn't appear).

## Adding a new tool (recipe)

1. In `McpToolset`, add a descriptor to `ToolSchemas`:
   ```csharp
   Tool("bizhawk_my_tool", "What it does.", [
       Param("foo", "integer", "Meaning of foo.", 1),   // name, JSON type, description, optional default
   ]),
   ```
2. Add the dispatch arm in `Call(string name, JsonElement? args)`:
   ```csharp
   "bizhawk_my_tool" => _ui.Invoke(() => MyTool(args)),
   ```
3. Implement the handler (always on the UI thread — see the `_ui.Invoke` pattern; never touch `Memory`/`EmuClient`/etc. directly in a handler body without it):
   ```csharp
   private string MyTool(JsonElement? args)
   {
       var a = Required(args);
       long n = RequireLong(a, "foo");
       // ... call APIs, build a human-readable string
       return result;
   }
   ```
4. Rebuild + curl smoke test (see `docs/MCP-PROTOCOL.md`), check the form's log box on failure.

Param helpers available: `Required`, `RequireLong`, `RequireInt`, `RequireULong`, `RequireString`, `OptionalString`. Structured results should be serialized with `JsonRpc.Pretty(...)`.

## Debugging

- **Primary surface:** the tool form's log TextBox — every HTTP error and handler exception is appended there (`ExternalToolEntry.Log`). Keep messages short and greppable.
- **Silent load failure:** if the menu item is disabled (red exclamation icon), hover it: EmuHawk prints the reason (`ExternalToolManager.GenerateToolTipFromFileName` → e.g. "doesn't contain a class implementing IExternalToolForm"). Common causes: missing `[ExternalTool]` attribute, or a `[RequiredApi]` type the provider doesn't register (never use it on `ApiContainer`).
- **Watchpoint reflection:** `bizhawk_watchpoint_*` reach `IDebuggable.MemoryCallbacks` via reflection on `EmulationApi.DebuggableCore`. If a BizHawk bump renames that private property, watchpoints fail with a clear error, not a crash. `MemoryCallbackSystem` is also where the core activates its native hooks (`ActiveChanged` → `gpgx_set_mem_callback`).
- **Address semantics on Genesis:** a "bug" report of domains disagreeing is usually the bus-vs-offset convention (see AGENTS.md "Domain & address conventions"). Always pass `domain` explicitly and convert Ghidra's 32-bit addresses to the 24-bit bus before reading.
- **First load on Release builds** shows a trust prompt (checksum stored in `config.ini`); Debug builds skip it.
- **Redeploy while loaded:** Windows locks assemblies in use — if the tool form is open in EmuHawk, the copy to `ExternalTools` fails (MSB3021, non-fatal warning in the csproj). Close the form (or EmuHawk) and rebuild.
- **Crash:** the tool runs inside EmuHawk — an unhandled exception in a handler takes EmuHawk down. Handlers already isolate `JsonRpc.Error` and generic exceptions into JSON-RPC errors; keep `StartServer`/`StopServer` wrapped in try/catch.
- **API signature doubts:** `./scripts/fetch-source.sh` checks out the pinned commit into `bizhawk-src/`; the interfaces live at `src/BizHawk.Client.Common/Api/Interfaces/`.

## Bumping the BizHawk version

1. Update `bizhawk.build` with the new commit (the dev build's commit is in `EmuHawk.exe` → Properties → Details → ProductVersion).
2. Point the build at the matching install (`BizHawkInstallDir`).
3. `./scripts/fetch-source.sh`; diff the ApiHawk interfaces you use against the pinned ones.
4. Rebuild, deploy, run the curl smoke tests, and re-verify `System.Text.Json`/`System.Memory` versions don't drift from what the new `dll/` folder ships (see Known issues).

## Known issues

- **`MSB3277` System.Memory conflict warning** (ours 4.0.1.2 vs BizHawk's 4.0.5.0): benign — the CLR unifies to the higher version at runtime; the code only uses basic `JsonSerializer` surfaces.
- **No memory domain enumeration:** ApiHawk has no domain-list API; `bizhawk_get_info` reports current domain + size, `bizhawk_use_memory_domain` switches. Enumerating all domains would require reaching into the core's `MemoryDomains` via reflection (out of scope so far).
- **Paths are host-side:** `bizhawk_screenshot`/`save_state`/`load_state` take paths on the machine running EmuHawk (e.g. `C:/temp/...` from a WSL-driven agent).
- **`bizhawk_frame_advance` cap:** 600 frames per call (UI responsiveness + request timeout sanity).

## CI / releases

See `.github/workflows/build-and-release.yml` and `docs/CI-RELEASES.md`. Manual run: GitHub → Actions → *build-and-release* → Run workflow (builds stable + dev flavors and uploads artifacts; a `v*` tag push additionally attaches them to a release).
