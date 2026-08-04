# MCP protocol implementation

Implemented subset of the MCP **Streamable HTTP** transport, **dual-era**:

- **Legacy** `2025-11-25` (`JsonRpc.MCP_PROTOCOL_VERSION`): `initialize` handshake, no sessions. The default for every client that doesn't declare a protocol version.
- **Modern** `2026-07-28` (`JsonRpc.MODERN_PROTOCOL_VERSION`): stateless — every request declares its version in `params._meta["io.modelcontextprotocol/protocolVersion"]` (and the `MCP-Protocol-Version` header); results carry `resultType: "complete"` and `_meta["io.modelcontextprotocol/serverInfo"]`.

Both eras are served from the same endpoint; the server picks the era per request (see [Protocol eras](#protocol-eras)).

## Transport

| Method | Path | Behavior |
|---|---|---|
| `POST` | `/mcp` | JSON-RPC 2.0 request/response. Stateless — **no sessions**, no `mcp-session-id` header. Modern clients may send `MCP-Protocol-Version`/`Mcp-Method`/`Mcp-Name` headers (validated when present, never required). |
| `GET` | `/mcp` | SSE stream when `Accept: text/event-stream` (legacy-era clients only — 2026-07-28 removed the GET stream, we keep it for dual-era compatibility); emits `event: endpoint` + `data: <url>` once, then `: keepalive` comments every 15 s, plus one `tools/list_changed` notification on the first stream of each server lifetime. |
| other | — | `405` |

- Requests with an `id` → `200 application/json` response (modern: `400` for version/header errors, `404` for unknown methods — the body is still the JSON-RPC error).
- Notifications (`id: null`) → `202 Accepted`, empty body.
- Malformed JSON → JSON-RPC error `-32700` (id `null`).
- Response `Content-Encoding: UTF-8`.

## Protocol eras

A request is **modern** iff it declares a protocol version in `params._meta` **or** in the `MCP-Protocol-Version` header. Everything else is served as **legacy**.

- Declared version outside the supported set → `UnsupportedProtocolVersionError` (`-32022`, HTTP `400`), with `data: { supported: ["2026-07-28", "2025-11-25"], requested }` — a client can retry with a mutually supported version.
- Header and `_meta` versions disagree, or `Mcp-Method`/`Mcp-Name` don't match the body → `HeaderMismatch` (`-32020`, HTTP `400`). `Mcp-Name` is compared against `params.name` (`tools/call`, `prompts/get`) or `params.uri` (`resources/read`), with `=?base64?...?=` sentinel decoding.
- Modern results add `resultType: "complete"` and `_meta.serverInfo`; legacy results keep the pre-2026 envelope.
- `initialize` still works in both eras (answers the latest legacy revision); `server/discover` is served in both (harmless, curl-friendly).

## Methods

| Method | Result |
|---|---|
| `initialize` | `{ protocolVersion: "2025-11-25", capabilities: { tools: { listChanged: true }, resources: { listChanged: false, subscribe: false }, prompts: {} }, serverInfo: { name: "bizhawk-mcp-native", version, instructions } }` |
| `server/discover` | `{ supportedVersions: ["2026-07-28", "2025-11-25"], capabilities, instructions }` (+ `resultType`/`_meta.serverInfo`/`ttlMs` in modern mode) |
| `ping` | `{}` (kept in both eras; removed from the 2026-07-28 spec but harmless) |
| `tools/list` | `{ tools: [ ...schemas ], ttlMs: 3600000, cacheScope: "public" }` — schemas are static dictionaries built in `McpToolset.ToolSchemas`, returned in deterministic order |
| `tools/call` | `{ content: [ { type: "text", text } ], isError: false }` |
| `prompts/list` | `{ prompts: [...], ttlMs: 3600000, cacheScope: "public" }` |
| `prompts/get` | `{ description, messages: [...] }` |
| `resources/list` | `{ resources: [ { uri, name, mimeType, size, path } ], ttlMs: 30000, cacheScope: "private" }` — artifacts created by tools (e.g. screenshots); URIs use the `bizhawk://` scheme |
| `resources/templates/list` | `{ resourceTemplates: [...], ttlMs: 3600000, cacheScope: "public" }` |
| `resources/read` | `{ contents: [ { uri, mimeType, blob } ], ttlMs: 10000, cacheScope: "private" }` — `blob` is the file's bytes base64-encoded |

Anything else → `-32601` (method not found). Tool handler failures throw `JsonRpc.Error` (`-32602` invalid params) or general exceptions (`-32603`).

### Caching (SEP-2549)

`ttlMs`/`cacheScope` are advertised on every cacheable result: static lists (`tools/list`, `prompts/list`, `resources/templates/list`, `server/discover`) are `"public"` with a 1-hour TTL — the tool list is fixed per process, so a client can cache it and stop polling (each poll costs ~17ms + the 94-schema payload). `resources/list`/`resources/read` change as tools run, so they get short TTLs and `"private"` scope (artifact `path`s are host paths — not for shared caches).

## Sample session (curl)

```bash
# 1a. modern: discover (declares its version up front)
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -H 'MCP-Protocol-Version: 2026-07-28' -H 'Mcp-Method: server/discover' \
  -d '{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28"}}}'

# 1b. legacy: initialize
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'

# 2. list tools (legacy era — no version declared)
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

# 3. call a tool
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"get_info","arguments":{}}}'

# 4. notification (no reply expected)
curl -s -i -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'
# → HTTP/1.1 202 Accepted
```

## Tool result convention

Every tool returns a **single text blob** as `content[0].text`. Structured data (e.g. `get_info`, `read_memory`) is JSON inside the text; simple operations return plain strings (`pong`, `ok`, paths). Agents should `JSON.parse` the text when the tool description says it returns JSON.

## Resources

The server advertises the `resources` capability. Tools can register artifacts (files the server wrote on the host) — `screenshot`, `frame_hash`, `dump_memory`, `genesis_read_plane`, `cdl_export`, `start_fixture` (CSV) — saving into `<temp>/bizhawk-mcp/` (or the caller-provided path) and returning **three ways to get the same file**: `path` (host-native), `wsl_path` (the same file in WSL form, `/mnt/c/...`, so shell-capable agents read it directly without converting — **present only when the host is Windows**; on Linux hosts `path` is already agent-readable) and `resource` (the `bizhawk://` URI below). Fetch the bytes with:

```bash
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":5,"method":"resources/read","params":{"uri":"bizhawk://<id>"}}'
# → { contents: [ { uri, mimeType: "image/png", blob: "<base64>" } ] }
```

`resources/list` reports each artifact's host `path` **and** `wsl_path`
(Windows hosts only), so shell-capable agents can read the file directly
instead of pulling base64 into context; `get_info`'s `paths_wsl` mirrors
`paths` in WSL form when the host is Windows.

## Script endpoints (no MCP client, no JSON)

- `GET /mcp/read/{domain}/{start}:{end}` — raw memory bytes as
  `application/octet-stream` (hex range, 256 KiB cap, same validation as the
  `bizhawk://read` template):
  ```bash
  curl -s "http://127.0.0.1:8767/mcp/read/68K%20RAM/F800:FFFF" -o ram.bin
  ```
- `GET /mcp/artifacts/{id}` — streams an artifact file's bytes (the same
  `bizhawk://<id>` resources), 404 for unknown ids.

## JSON-RPC batching

A POST with an array of requests returns an array of responses in one round
trip (the fixed ~17ms per-call overhead is paid once); notifications produce
no entry; per-element errors don't kill the batch (batching is a legacy-era
extension — 2026-07-28 POST bodies must be a single request):

```bash
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_info","arguments":{}}}]'
# → [ { jsonrpc, id:1, result: {} }, { jsonrpc, id:2, result: {...} } ]
```

## Not implemented (deliberately)

- Sessions / `mcp-session-id` (removed by 2026-07-28; never needed in legacy here)
- Server-initiated messages (SSE push to client)
- Resource subscriptions / change notifications (`resources/subscribe` legacy,
  `subscriptions/listen` modern)
- HTTP `PUT`/`DELETE` session endpoints
- MRTR (`resultType: "input_required"` / `inputRequests`) — this server never
  needs client input
- Roots, Sampling, Logging (deprecated by 2026-07-28)

If the host MCP client requires sessions, the dispatch layer needs a session map keyed by an `mcp-session-id` header with per-session JSON-RPC batching — the POST path is already structured for it (`Dispatch(body)` is pure).
