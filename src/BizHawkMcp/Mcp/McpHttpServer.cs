using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BizHawkMcp.Mcp
{
	/// <summary>
	/// Minimal MCP "Streamable HTTP" server hosted in-process via HttpListener
	/// (no ASP.NET Core dependency — net48 + Mono friendly).
	///
	/// Dual-era subset:
	///   POST /mcp  → JSON-RPC request/response (stateless, no sessions).
	///                Legacy (2025-11-25): initialize handshake, as before.
	///                Modern (2026-07-28): per-request version in
	///                params._meta + MCP-Protocol-Version header; results get
	///                resultType/_meta.serverInfo; cacheable results get
	///                ttlMs/cacheScope; version/header mismatches → -32022/-32020
	///                with 400; unknown modern methods → 404.
	///   GET  /mcp  → SSE stream (endpoint event + keepalive) for LEGACY clients
	///                only (2026-07-28 removed the GET stream; we keep it for
	///                dual-era compatibility); the first stream of each server
	///                lifetime carries a tools/list_changed notification
	///                (the tool list is fixed per process, so a fresh connection
	///                after a restart may serve a different list)
	/// Not implemented: sessions, server-initiated messages beyond the above,
	/// resources subscribe, subscriptions/listen.
	/// </summary>
	public sealed class McpHttpServer
	{		private readonly IHostApis _tool;
		private readonly IUiDispatcher _ui;
		private readonly Action<string> _log;

		private readonly HttpListener _listener = new();
		private readonly CancellationTokenSource _cts = new();
		private McpToolset? _toolset;
		private Thread? _acceptThread;
		private volatile bool _toolsNotified;

		public McpHttpServer(IHostApis tool, IUiDispatcher ui, Action<string> log)
		{
			_tool = tool;
			_ui = ui;
			_log = log;
			_toolset = new McpToolset(tool, ui);
		}

		public string BaseUrl { get; private set; } = "";

		public void Start()
		{
			var host = Environment.GetEnvironmentVariable("BIZHAWK_MCP_HOST") ?? "127.0.0.1";
			var port = int.TryParse(Environment.GetEnvironmentVariable("BIZHAWK_MCP_PORT"), out var p) ? p : 8767;
			BaseUrl = $"http://{host}:{port}/mcp/";
			_listener.Prefixes.Add(BaseUrl);
			_listener.Start();
			_acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "mcp-http" };
			_acceptThread.Start();
		}

		public void Stop()
		{
			_cts.Cancel();
			try
			{
				_listener.Stop();
			}
			catch
			{
				// already stopped
			}

			_listener.Close();
		}

		private void AcceptLoop()
		{
			while (!_cts.IsCancellationRequested)
			{
				HttpListenerContext ctx;
				try
				{
					ctx = _listener.GetContext();
				}
				catch (Exception)
				{
					break; // listener stopped
				}

				ThreadPool.QueueUserWorkItem(_ => HandleContext(ctx));
			}
		}

		private async void HandleContext(HttpListenerContext ctx)
		{
			try
			{
				if (ctx.Request.HttpMethod == "POST")
				{
					await HandlePost(ctx);
				}
				else if (ctx.Request.HttpMethod == "GET"
					&& ctx.Request.AcceptTypes is { } accepts
					&& Array.Exists(accepts, static a => a.IndexOf("text/event-stream", StringComparison.OrdinalIgnoreCase) >= 0))
				{
					await HandleSse(ctx);
				}
				else if (ctx.Request.HttpMethod == "GET")
				{
					// script-friendly raw endpoints (no MCP client, no JSON):
					//   GET /mcp/read/{domain}/{start}:{end} → raw bytes
					//   GET /mcp/artifacts/{id}              → artifact bytes
					HandleRawGet(ctx);
				}
				else
				{
					ctx.Response.StatusCode = 405;
					ctx.Response.Close();
				}
			}
			catch (Exception e)
			{
				_log($"HTTP error: {e}");
				try
				{
					ctx.Response.StatusCode = 500;
					ctx.Response.Close();
				}
				catch
				{
					// ignore
				}
			}
		}

		private void HandleRawGet(HttpListenerContext ctx)
		{
			string path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
			try
			{
				if (path.StartsWith("/mcp/read/", StringComparison.Ordinal))
				{
					// same cap + validation as the bizhawk://read resource template
					string uri = "bizhawk://read/" + path.Substring("/mcp/read/".Length);
					byte[] bytes = _ui.Invoke(() => _toolset!.ReadRangeRaw(uri));
					WriteBytes(ctx, 200, bytes, "application/octet-stream");
					return;
				}
				if (path.StartsWith("/mcp/artifacts/", StringComparison.Ordinal))
				{
					string uri = "bizhawk://" + path.Substring("/mcp/artifacts/".Length);
					var artifact = _ui.Invoke(() => _toolset!.ReadArtifactFile(uri));
					if (artifact == null)
					{
						ctx.Response.StatusCode = 404;
						ctx.Response.Close();
						return;
					}
					WriteBytes(ctx, 200, artifact.Value.Bytes, artifact.Value.Mime);
					return;
				}
			}
			catch (JsonRpc.Error e)
			{
				WriteBytes(ctx, 400, Encoding.UTF8.GetBytes(e.Message), "text/plain");
				return;
			}
			ctx.Response.StatusCode = 405;
			ctx.Response.Close();
		}

		private static void WriteBytes(HttpListenerContext ctx, int status, byte[] bytes, string mime)
		{
			ctx.Response.StatusCode = status;
			ctx.Response.ContentType = mime;
			ctx.Response.ContentLength64 = bytes.Length;
			ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
			ctx.Response.Close();
		}

		private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
		{
			using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
			return await reader.ReadToEndAsync();
		}

		private async Task HandlePost(HttpListenerContext ctx)
		{
			var body = await ReadBodyAsync(ctx.Request);
			// Modern (2026-07-28) clients mirror the protocol version + method +
			// name into headers; legacy clients don't send them. We never
			// require them (dual-era), but validate them when present.
			string? hdrVersion = TrimOrNull(ctx.Request.Headers["MCP-Protocol-Version"]);
			string? hdrMethod = TrimOrNull(ctx.Request.Headers["Mcp-Method"]);
			string? hdrName = TrimOrNull(ctx.Request.Headers["Mcp-Name"]);
			var (id, responseBytes, isNotification, httpStatus) = Dispatch(body, hdrVersion, hdrMethod, hdrName);
			if (isNotification)
			{
				ctx.Response.StatusCode = 202;
				ctx.Response.Close();
				return;
			}

			ctx.Response.StatusCode = httpStatus == 0 ? 200 : httpStatus;
			ctx.Response.ContentType = "application/json";
			ctx.Response.ContentEncoding = Encoding.UTF8;
			await ctx.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length, _cts.Token);
			ctx.Response.Close();
		}

		private static string? TrimOrNull(string? v)
		{
			// net48's IsNullOrWhiteSpace carries no [NotNullWhen] annotation,
			// so trim first and re-check instead of narrowing v
			string? trimmed = v?.Trim();
			return string.IsNullOrEmpty(trimmed) ? null : trimmed;
		}

		internal (object? id, byte[] bytes, bool isNotification, int httpStatus) Dispatch(string body, string? hdrVersion = null, string? hdrMethod = null, string? hdrName = null)
		{
			JsonElement root;
			try
			{
				root = JsonDocument.Parse(body).RootElement;
			}
			catch (JsonException e)
			{
				return (null, JsonRpc.ParseError(null, $"parse error: {e.Message}"), false, 0);
			}

			// JSON-RPC batch: an array of requests → an array of responses
			// (notifications produce no entry). Dispatch is stateless, so a
			// single HTTP round trip serves N calls — the fixed ~17ms per-call
			// overhead is paid once.
			if (root.ValueKind == JsonValueKind.Array)
			{
				var results = new List<byte[]>();
				foreach (var el in root.EnumerateArray())
				{
					var (_, bytes, isNotification, _) = DispatchOne(el, hdrVersion, hdrMethod, hdrName);
					if (!isNotification) results.Add(bytes);
				}
				if (results.Count == 0)
					return (null, JsonRpc.ParseError(null, "batch contained no requests"), false, 0);
				var sb = new StringBuilder();
				sb.Append('[');
				for (var i = 0; i < results.Count; i++)
				{
					if (i > 0) sb.Append(',');
					sb.Append(Encoding.UTF8.GetString(results[i]));
				}
				sb.Append(']');
				return (null, Encoding.UTF8.GetBytes(sb.ToString()), false, 0);
			}

			return DispatchOne(root, hdrVersion, hdrMethod, hdrName);
		}

		private (object? id, byte[] bytes, bool isNotification, int httpStatus) DispatchOne(JsonElement root, string? hdrVersion = null, string? hdrMethod = null, string? hdrName = null)
		{
			object? id = null;
			if (root.ValueKind != JsonValueKind.Object)
				return (null, JsonRpc.ParseError(null, "expected a JSON object"), false, 0);

			if (root.TryGetProperty("id", out var idEl)) id = idEl.ValueKind == JsonValueKind.Null ? null : (object?)idEl.GetRawText();

			string? method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			JsonElement? args = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

			if (id == null)
			{
				// notification — fire and forget
				return (null, Array.Empty<byte>(), true, 0);
			}

			if (string.IsNullOrEmpty(method))
				return (id, JsonRpc.ParseError(id, "missing method"), false, 0);

			// ── era detection (2026-07-28) ──────────────────────────────────
			// Modern requests declare their protocol version in
			// params._meta["io.modelcontextprotocol/protocolVersion"] and, on
			// HTTP, in the MCP-Protocol-Version header. No declaration → legacy
			// (initialize handshake era). A declared version outside the
			// supported set → UnsupportedProtocolVersionError (-32022, 400);
			// conflicting header/body declarations → HeaderMismatch (-32020, 400).
			string? metaVersion = null;
			if (args is { } metaArgs && metaArgs.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object
				&& meta.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var pv) && pv.ValueKind == JsonValueKind.String)
			{
				metaVersion = pv.GetString();
			}

			if (metaVersion != null && hdrVersion != null && !string.Equals(metaVersion, hdrVersion, StringComparison.Ordinal))
				return (id, JsonRpc.Failure(id, JsonRpc.Error.HeaderMismatch($"MCP-Protocol-Version header '{hdrVersion}' does not match _meta protocolVersion '{metaVersion}'")), false, 400);

			string? declared = metaVersion ?? hdrVersion;
			if (declared != null && !JsonRpc.SupportsVersion(declared))
				return (id, JsonRpc.Failure(id, JsonRpc.Error.UnsupportedProtocolVersion(declared)), false, 400);

			bool modern = declared != null && JsonRpc.IsModern(declared);

			// Modern clients mirror method/name into headers; validate they
			// match the body so an intermediary can't be routed on one source
			// of truth while the server executes on another.
			if (modern && !string.IsNullOrEmpty(hdrMethod) && !string.Equals(hdrMethod, method, StringComparison.Ordinal))
				return (id, JsonRpc.Failure(id, JsonRpc.Error.HeaderMismatch($"Mcp-Method header '{hdrMethod}' does not match body method '{method}'")), false, 400);
			if (modern && !string.IsNullOrEmpty(hdrName))
			{
				string? bodyName = null;
				if (args is { } nameArgs)
				{
					if (nameArgs.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String) bodyName = n.GetString();
					else if (nameArgs.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String) bodyName = u.GetString();
				}

				if (bodyName == null || JsonRpc.DecodeHeaderValue(hdrName!) != bodyName)
					return (id, JsonRpc.Failure(id, JsonRpc.Error.HeaderMismatch($"Mcp-Name header '{hdrName}' does not match body name '{(bodyName ?? "<none>")}'")), false, 400);
			}

			try
			{
				object? result = method switch
				{
					"initialize" => LegacyInitialize(),
					"server/discover" => Discover(),
					"ping" => new Dictionary<string, object?>(),
					"tools/list" => new Dictionary<string, object?> { ["tools"] = _toolset!.ToolSchemas },
					"tools/call" => CallTool(args),
					"prompts/list" => new Dictionary<string, object?> { ["prompts"] = Prompts },
					"prompts/get" => GetPrompt(args),
					"resources/list" => _ui.Invoke(() => _toolset!.ListResources()),
					"resources/templates/list" => _ui.Invoke(() => _toolset!.ListResourceTemplates()),
					"resources/read" => _ui.Invoke(() => ReadResource(args)),
					_ => throw new JsonRpc.Error(JsonRpc.Error.METHOD_NOT_FOUND, $"unknown method: {method}"),
				};
				var (ttlMs, cacheScope) = CachePolicy(method);
				return (id, JsonRpc.Success(id, result, modern, ttlMs, cacheScope), false, 0);
			}
			catch (JsonRpc.Error e)
			{
				// modern transport maps version/header errors to 400 and
				// unknown methods to 404; legacy clients keep 200 + error body
				int status = 0;
				if (modern)
				{
					if (e.Code == JsonRpc.Error.METHOD_NOT_FOUND) status = 404;
					else if (e.Code == JsonRpc.Error.HEADER_MISMATCH || e.Code == JsonRpc.Error.UNSUPPORTED_PROTOCOL_VERSION) status = 400;
				}

				return (id, JsonRpc.Failure(id, e), false, status);
			}
			catch (Exception e)
			{
				return (id, JsonRpc.Failure(id, new JsonRpc.Error(JsonRpc.Error.INTERNAL_ERROR, $"{e.GetType().Name}: {e.Message}")), false, 0);
			}
		}

		// ── capabilities / identity ─────────────────────────────────────────
		private static Dictionary<string, object?> Capabilities() => new()
		{
			["tools"] = new Dictionary<string, object?> { ["listChanged"] = true },
			["resources"] = new Dictionary<string, object?> { ["listChanged"] = false, ["subscribe"] = false },
			["prompts"] = new Dictionary<string, object?>(),
		};

		private Dictionary<string, object?> LegacyInitialize() => new()
		{
			["protocolVersion"] = JsonRpc.MCP_PROTOCOL_VERSION,
			["capabilities"] = Capabilities(),
			["serverInfo"] = JsonRpc.ServerInfo(),
		};

		// server/discover (required by 2026-07-28): advertises supported
		// versions + capabilities + identity so a client can pick a version
		// before any other request. Served in both eras (harmless, and handy
		// for curl smoke tests); the modern envelope (resultType/_meta/ttlMs)
		// is added by JsonRpc.Success in modern mode.
		private Dictionary<string, object?> Discover() => new()
		{
			["supportedVersions"] = JsonRpc.SUPPORTED_VERSIONS,
			["capabilities"] = Capabilities(),
			["instructions"] = (string)JsonRpc.ServerInfo()["instructions"]!,
		};

		// CacheableResult (SEP-2549): static lists are public with a long TTL
		// (tools never change in a process lifetime — an agent can cache and
		// stop polling, saving ~17ms + the 94-schema payload per refresh);
		// artifact lists/reads change as tools run, so short TTL + private.
		private static (long? ttlMs, string? cacheScope) CachePolicy(string method) => method switch
		{
			"server/discover" or "tools/list" or "prompts/list" or "resources/templates/list" => (3600000, "public"),
			"resources/list" => (30000, "private"),
			"resources/read" => (10000, "private"),
			_ => (null, null),
		};

		private Dictionary<string, object?> ReadResource(JsonElement? args)
		{
			if (args is not { } a
				|| !a.TryGetProperty("uri", out var uriEl)
				|| uriEl.ValueKind != JsonValueKind.String
				|| string.IsNullOrEmpty(uriEl.GetString()))
			{
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "resources/read requires a string 'uri'");
			}

			return _toolset!.ReadResource(uriEl.GetString()!);
		}

		// ── prompts ───────────────────────────────────────────────────────────
		// Simple static prompt templates the client can surface to the user.
		// No emulator interaction needed — pure text substitution.
		private static readonly List<object?> Prompts = new()
		{
			new Dictionary<string, object?>
			{
				["name"] = "memory_research",
				["description"] = "Investigate a game mechanic by tracing memory: register Ghidra symbols, search for the value, and pin down who writes it.",
				["arguments"] = new List<object?>
				{
					new Dictionary<string, object?> { ["name"] = "target", ["description"] = "What to research, e.g. \"player health\" or \"how jump height is computed\"", ["required"] = true },
					new Dictionary<string, object?> { ["name"] = "domain", ["description"] = "Memory domain to search in (default: current)", ["required"] = false },
				},
			},
			new Dictionary<string, object?>
			{
				["name"] = "tas_frame",
				["description"] = "Build and verify an input sequence (walk/jump/attack) with in-memory savestates around each experiment.",
				["arguments"] = new List<object?>
				{
					new Dictionary<string, object?> { ["name"] = "goal", ["description"] = "The input sequence to craft, e.g. \"run right and jump over the pit\"", ["required"] = true },
					new Dictionary<string, object?> { ["name"] = "frames", ["description"] = "Approximate frames per attempt", ["required"] = false },
				},
			},
		};

		private Dictionary<string, object?> GetPrompt(JsonElement? args)
		{
			if (args is not { } a
				|| !a.TryGetProperty("name", out var nameEl)
				|| nameEl.ValueKind != JsonValueKind.String
				|| string.IsNullOrEmpty(nameEl.GetString()))
			{
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "prompts/get requires a string 'name'");
			}

			string name = nameEl.GetString()!;
			switch (name)
			{
				case "memory_research":
				{
					string target = a.TryGetProperty("arguments", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("target", out var tv) && tv.ValueKind == JsonValueKind.String
						? tv.GetString()!
						: "the requested mechanic";
					string? domain = a.TryGetProperty("arguments", out var da) && da.ValueKind == JsonValueKind.Object && da.TryGetProperty("domain", out var dv) && dv.ValueKind == JsonValueKind.String ? dv.GetString() : null;
					var sb = new System.Text.StringBuilder();
					sb.Append("You are analyzing a game running in BizHawk (Genesis gpgx). Goal: research ").Append(target).Append(". Steps:\n");
					sb.Append("1. Register known addresses as symbols (symbols_set) — import Ghidra exports into a namespace.\n");
					sb.Append("2. Locate the value: search_memory (try u8/u16/u32, both endianness) in domain ").Append(domain ?? "the current domain").Append(".\n");
					sb.Append("3. Narrow with wait_until / watch_change to catch dynamic changes across frames.\n");
					sb.Append("4. Pin down the writer: a watchpoint_add (write) on the address, then watchpoint_wait — or trace to see where the code runs.\n");
					sb.Append("5. Save the core state (memstate_save) before experiments and restore (memstate_load) between attempts.\n");
					sb.Append("Report the address, width, endianness, and the code path that writes it (with the disassembly from the hit context).");
					return PromptResult(name, target, sb.ToString());
				}
				case "tas_frame":
				{
					string goal = a.TryGetProperty("arguments", out var t) && t.ValueKind == JsonValueKind.Object && t.TryGetProperty("goal", out var tv) && tv.ValueKind == JsonValueKind.String
						? tv.GetString()!
						: "the requested sequence";
					var sb = new System.Text.StringBuilder();
					sb.Append("You are crafting an input sequence in BizHawk (Genesis gpgx). Goal: ").Append(goal).Append(". Steps:\n");
					sb.Append("1. Save the starting core state: memstate_save {slot: \"start\"}.\n");
					sb.Append("2. Use start_fixture with an input timeline (input_mode \"explicit\" releases buttons between entries) and sample position/velocity per frame.\n");
					sb.Append("3. After each attempt, restore with memstate_load {slot: \"start\"} so the next try starts identical.\n");
					sb.Append("4. Iterate: adjust the timeline (frames, buttons, hold/release) until the CSV shows the intended motion.\n");
					sb.Append("Report the final timeline as JSON inputs and the resulting per-frame CSV rows.");
					return PromptResult(name, goal, sb.ToString());
				}
				default:
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown prompt: {name} (available: memory_research, tas_frame)");
			}
		}

		private static Dictionary<string, object?> PromptResult(string name, string argument, string text)
		{
			return new Dictionary<string, object?>
			{
				["description"] = $"Prompt generated for: {argument}",
				["messages"] = new List<object?>
				{
					new Dictionary<string, object?>
					{
						["role"] = "user",
						["content"] = new Dictionary<string, object?> { ["type"] = "text", ["text"] = text },
					},
				},
			};
		}

		private Dictionary<string, object?> CallTool(JsonElement? args)
		{			if (args is not { } argsVal
				|| !argsVal.TryGetProperty("name", out var nameEl)
				|| nameEl.ValueKind != JsonValueKind.String
				|| string.IsNullOrEmpty(nameEl.GetString()))
			{
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "tools/call requires a string 'name'");
			}

			string name = nameEl.GetString()!;
			JsonElement? toolArgs = argsVal.TryGetProperty("arguments", out var ta) && ta.ValueKind == JsonValueKind.Object ? ta : null;

			var content = new List<object?>
			{
				new Dictionary<string, object?> { ["type"] = "text", ["text"] = _toolset!.Call(name, toolArgs) },
			};
			return new Dictionary<string, object?>
			{
				["content"] = content,
				["isError"] = false,
			};
		}

		private async Task HandleSse(HttpListenerContext ctx)
		{
			ctx.Response.StatusCode = 200;
			ctx.Response.ContentType = "text/event-stream";
			ctx.Response.Headers["Cache-Control"] = "no-cache";
			ctx.Response.Headers["Connection"] = "keep-alive";

			var writer = new StreamWriter(ctx.Response.OutputStream, Encoding.UTF8) { AutoFlush = true };
			await writer.WriteLineAsync($"event: endpoint\ndata: {BaseUrl}\n");
			// the tool list is fixed per server lifetime; the first SSE stream
			// after a (re)start tells the client to re-fetch tools/list — a
			// redeployed DLL can serve a different list than the client cached.
			if (!_toolsNotified)
			{
				_toolsNotified = true;
				await writer.WriteLineAsync("event: message\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\",\"params\":{}}\n");
			}
			// keepalive comment every 15 s until the client goes away
			while (!_cts.IsCancellationRequested)
			{
				try
				{
					await Task.Delay(15000, _cts.Token);
					await writer.WriteLineAsync(": keepalive");
				}
				catch (Exception)
				{
					break; // client disconnected
				}
			}
		}
	}
}
