# Architecture

## Components

```
┌─────────────────────────────── EmuHawk process ──────────────────────────────┐
│                                                                              │
│  ExternalToolManager (BizHawk)                                               │
│   • scans <install>/ExternalTools/*.dll (FileSystemWatcher)                  │
│   • requires one type : IExternalToolForm with [ExternalTool]                │
│   • on click: Activator.CreateInstance → ApiInjector.UpdateApis              │
│        └─ fills [RequiredApi]/[OptionalApi] properties by reflection         │
│                                                                              │
│  ExternalToolEntry (WinForms Form)                                           │
│   • [ExternalTool("BizHawk MCP Server")] entry point                         │
│   • injected APIs: Memory, Emulation, EmuClient, Joypad, SaveState           │
│   • OnShown → McpHttpServer.Start()                                          │
│   • log TextBox = the debugging surface                                      │
│        │                                                                     │
│        ▼                                                                     │
│  McpHttpServer (HttpListener)                                                │
│   • prefix http://127.0.0.1:8767/mcp/  (env BIZHAWK_MCP_HOST/PORT)           │
│   • accept thread + ThreadPool per request                                   │
│   • POST → JSON-RPC dispatch | GET(SSE) → endpoint + keepalive               │
│        │                                                                     │
│        ▼                                                                     │
│  McpToolset (tool registry + handlers)                                       │
│        │                                                                     │
│        ▼                                                                     │
│  UiDispatcher (Control.Invoke marshalling)                                   │
│        │                                                                     │
│        ▼                                                                     │
│  ApiHawk (IMemoryApi, IEmulationApi, IEmuClientApi, IJoypadApi, ISaveStateApi)│
│        │                                                                     │
│        ▼                                                                     │
│  BizHawk cores (memory, timing, rendering, input)                            │
└──────────────────────────────────────────────────────────────────────────────┘
```

## Load flow (first click on the tool)

1. `ExternalToolManager.GenerateToolTipFromFileName` loads the assembly, checks it references a `BizHawk.*` assembly, finds the entry type, adds a menu item. On click: `MainForm` → `ToolManager.LoadExternalToolForm` → `Activator.CreateInstance` (default ctor) → `ApiInjector.UpdateApis(GetOrInitApiProvider, tool)`.
2. `UpdateApis` sets: any property of type `ApiContainer` (no attribute needed), then every property marked `[RequiredApi]` (a miss → load fails, `false`), then `[OptionalApi]`.
3. EmuHawk calls `Show()` → `OnShown` fires → server binds the port and the accept thread starts.
4. EmuHawk calls `UpdateValues(ToolFormUpdateType.General)` periodically while the tool is open; the skeleton ignores it.

## Threading model

- The HTTP listener runs on a dedicated background thread; each request is serviced on a `ThreadPool` thread.
- **All ApiHawk calls must execute on the WinForms UI thread.** The APIs forward into emulator state that is only safe there (frame stepping queues work on the form's timer, screenshots grab the render surface, joypad writes mutate input state read by the next frame).
- `UiDispatcher.Invoke(Func<T>)` uses `Control.Invoke` (synchronous, blocks the HTTP thread until the UI thread processes it). Deadlock is impossible as long as the UI thread is pumping — EmuHawk's message loop is always running while the tool form is open.
- Frame advance pumps `Application.DoEvents()` between frames so the UI stays responsive during long advances.

## Lifecycle

| Event | Behavior |
|---|---|
| Tool loaded / form shown | `McpHttpServer.Start()` — binds, starts accept thread |
| `bizhawk_shutdown` tool | `StopServer()` via UI dispatcher — listener closed, tool stays loaded; the form's "Stop server" button can restart it (re-clicking Start via the form re-binds) |
| Form closing | `OnFormClosing` → `StopServer()` |
| ROM change / core restart | `Restart()` (no-op) — server keeps running |

Note: the server currently only starts on `OnShown`; restarting after `bizhawk_shutdown` requires closing and reopening the tool form (or extending the form with a start button — the `StartServer()` method is already reusable).

## Version pinning

The plugin compiles against the DLLs of an installed BizHawk build (`Directory.Build.props` → `BizHawkInstallDir`, same `dll/` layout as the official zips). `bizhawk.build` records the exact source commit (e.g. `ed78f70a` = 2.11.2). ApiHawk interface signatures must be verified against that commit before changing tool code.
