# TODO — next improvements for the MCP server

Ideas collected from the agent review, protocol gaps, and API surface not yet
exposed. Roughly ordered by value/effort. Not a commitment — pick what fits.

## Protocol / MCP features

- [ ] **Sessions (`mcp-session-id`)**: the dispatch layer is already structured
  for it (`Dispatch` is pure) — add a session map keyed by the header so hosts
  that require sessions (e.g. some clients) work. Also enables JSON-RPC batching.
- [ ] **`tools/list` change notifications**: advertise `listChanged: true` and
  emit a `notifications/tools/list_changed` when the server restarts/changes.
- [ ] **Prompts**: e.g. a "TAS workflow" prompt or "memory research" prompt the
  client can surface to the user.
- [ ] **Server-initiated SSE messages**: push framecount/state changes to a
  subscribed client (needs sessions + a client that keeps GET SSE open).
- [ ] **HTTP `PUT`/`DELETE` session endpoints** for full Streamable HTTP parity.

## Tools / API surface

- [ ] **In-memory savestates**: `IMemorySaveStateApi` is NOT registered by the
  ApiHawk provider, so a `[RequiredApi]` won't load. Investigate reaching the
  core's memory-save-state machinery via reflection on the `ApiContainer`/core
  (risky — document before doing). Would give fast save/restore for search/TAS.
- [ ] **VDP/VRAM tools on Genesis** (VRAM/CRAM/VSRAM): reviewer flagged this as
  the missing piece for Genesis hacking — nametable + palette reads.
- [ ] **Batch memory ops**: `bizhawk_write_range` (via `WriteByteRange`) and
  `bizhawk_read_many` (list of (addr,width) pairs) to cut round-trips.
- [ ] **Core/board info**: `bizhawk_get_board_info` (`GetBoardName`,
  `GetDisplayType`, `GetGameOptions`) — helps agents identify the game revision.
- [ ] **Rewind/frameskip**: `bizhawk_enable_rewind`, `bizhawk_frameskip`,
  `bizhawk_limit_framerate` (`IEmuClientApi`/`IEmulationApi`).
- [ ] **ROM management**: `bizhawk_open_rom`/`bizhawk_close_rom`/`bizhawk_reboot`
  (careful: path is host-side).
- [ ] **Sound**: `bizhawk_set_sound` / `bizhawk_get_sound` (`SetSoundOn`,
  `GetSoundOn`).
- [ ] **Movie controls**: `bizhawk_movie_start`/`bizhawk_movie_stop`/`save`
  (`PlayFromStart`, `Stop`, `Save` on `IMovieApi`).
- [ ] **Cheat API?** Not in the ApiHawk set at the pinned commit — would need
  `IToolApi`/core access. Probably out of scope.

## Robustness / correctness

- [ ] **Per-domain addressing docs**: the reviewer's "BUG #1" was address
  convention (bus = raw addr, RAM = 0-based offset). `bizhawk_list_memory_domains`
  should return per-domain metadata: size, current, and a `busBase` hint where
  derivable (needs core access — see known limitations).
- [ ] **`bizhawk_screenshot` OSD toggle**: `SetScreenshotOSD(false)` before
  capture so the OSD/frame counter doesn't pollute the PNG, restore after.
- [ ] **Larger reads**: `bizhawk_read_range` caps at 4096 bytes; consider a
  chunked resource (`bizhawk://range/...`) for bigger dumps.
- [ ] **Concurrency guard**: handlers run on the UI thread, but two parallel
  HTTP requests can interleave long ops (`frame_advance` 600 frames). Consider
  a per-request lock or serializing long ops.
- [ ] **Better errors**: `bizhawk_use_memory_domain` returns a message instead
  of failing on unknown domain — should probably be `INVALID_PARAMS`.

## DX / tooling

- [ ] **More tests**: per-tool parameter validation matrix, endianness
  round-trips for every width (8/16/24/32), `resources/list` after multiple
  screenshots, dispatch fuzz (malformed params, wrong types).
- [ ] **Test the HTTP layer end-to-end**: spin up `McpHttpServer` on a random
  port in a test (Linux `HttpListener` works for loopback) and hit it with real
  HTTP requests.
- [ ] **Version bump helper**: script to update `bizhawk.build` +
  re-fetch source + grep for changed ApiHawk signatures (half-automate the
  "Bumping the BizHawk version" steps in AGENTS.md).
- [ ] **`opencode` config sample**: tidy up `opencode.mcp.example.json` /
  `opencode.mcp.example copy.json` — commit exactly one example with both
  `tools` and `resources` client support.

## Misc / research

- [ ] **Verify Mono (Linux EmuHawk)**: all tests + smoke on Mono; check
  `HttpListener` and `System.Text.Json` behave (known limitation: Linux is a
  compile target but Windows is the tested host).
- [ ] **Endianness per domain**: some cores have mixed-endian domains (e.g.
  GB VRAM is little, Genesis bus is big). Investigate whether ApiHawk's
  `SetBigEndian` is global or per-domain — if global, document the limitation
  in `bizhawk_get_info`.
- [ ] **Latency**: measure per-call overhead (JSON parse, UI-thread marshaling)
  for `read_memory`-heavy loops; consider a `bizhawk_read_bulk` that returns
  base64 to cut JSON size.
