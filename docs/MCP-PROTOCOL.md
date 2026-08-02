# MCP protocol implementation

Implemented subset of the MCP **Streamable HTTP** transport, protocol version `2025-06-18` (`JsonRpc.MCP_PROTOCOL_VERSION`).

## Transport

| Method | Path | Behavior |
|---|---|---|
| `POST` | `/mcp` | JSON-RPC 2.0 request/response. Stateless — **no sessions**, no `mcp-session-id` header. |
| `GET` | `/mcp` | SSE stream when `Accept: text/event-stream`; emits `event: endpoint` + `data: <url>` once, then `: keepalive` comments every 15 s. Never sends server-initiated messages. |
| other | — | `405` |

- Requests with an `id` → `200 application/json` response.
- Notifications (`id: null`) → `202 Accepted`, empty body.
- Malformed JSON → JSON-RPC error `-32700` (id `null`).
- Response `Content-Encoding: UTF-8`.

## Methods

| Method | Result |
|---|---|
| `initialize` | `{ protocolVersion, capabilities: { tools: { listChanged: false } }, serverInfo: { name: "bizhawk-mcp-native", version } }` |
| `ping` | `{}` |
| `tools/list` | `{ tools: [ ...schemas ] }` — schemas are static dictionaries built in `McpToolset.ToolSchemas` |
| `tools/call` | `{ content: [ { type: "text", text } ], isError: false }` |

Anything else → `-32601` (method not found). Tool handler failures throw `JsonRpc.Error` (`-32602` invalid params) or general exceptions (`-32603`).

## Sample session (curl)

```bash
# 1. initialize
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"curl","version":"0"}}}'

# 2. list tools
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}'

# 3. call a tool
curl -s -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"bizhawk_get_info","arguments":{}}}'

# 4. notification (no reply expected)
curl -s -i -X POST http://127.0.0.1:8767/mcp/ -H 'Content-Type: application/json' \
  -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'
# → HTTP/1.1 202 Accepted
```

## Tool result convention

Every tool returns a **single text blob** as `content[0].text` (same style as the old Lua bridge). Structured data (e.g. `bizhawk_get_info`, `bizhawk_read_memory`) is JSON inside the text; simple operations return plain strings (`pong`, `ok`, paths). Agents should `JSON.parse` the text when the tool description says it returns JSON.

## Not implemented (deliberately)

- Sessions / `mcp-session-id`
- Server-initiated messages (SSE push to client)
- Resources and prompts (`capabilities` only advertises `tools`)
- `tools/list` change notifications
- HTTP `PUT`/`DELETE` session endpoints

If the host MCP client requires sessions, the dispatch layer needs a session map keyed by an `mcp-session-id` header with per-session JSON-RPC batching — the POST path is already structured for it (`Dispatch(body)` is pure).
