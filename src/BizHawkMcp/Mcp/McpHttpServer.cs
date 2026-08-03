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
	/// Implemented subset of the 2025-06-18 spec:
	///   POST /mcp  → JSON-RPC request/response (stateless, no sessions)
	///   GET  /mcp  → SSE stream (endpoint event + keepalive) when the client
	///                asks for text/event-stream; the first stream of each
	///                server lifetime carries a tools/list_changed notification
	///                (the tool list is fixed per process, so a fresh connection
	///                after a restart may serve a different list)
	/// Not implemented: sessions, server-initiated messages beyond the above,
	/// resources subscribe.
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

		private static async Task<string> ReadBodyAsync(HttpListenerRequest request)
		{
			using var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8);
			return await reader.ReadToEndAsync();
		}

		private async Task HandlePost(HttpListenerContext ctx)
		{
			var body = await ReadBodyAsync(ctx.Request);
			var (id, responseBytes, isNotification) = Dispatch(body);
			if (isNotification)
			{
				ctx.Response.StatusCode = 202;
				ctx.Response.Close();
				return;
			}

			ctx.Response.StatusCode = 200;
			ctx.Response.ContentType = "application/json";
			ctx.Response.ContentEncoding = Encoding.UTF8;
			await ctx.Response.OutputStream.WriteAsync(responseBytes, 0, responseBytes.Length, _cts.Token);
			ctx.Response.Close();
		}

		internal (object? id, byte[] bytes, bool isNotification) Dispatch(string body)
		{
			object? id = null;
			JsonElement root;
			try
			{
				root = JsonDocument.Parse(body).RootElement;
			}
			catch (JsonException e)
			{
				return (null, JsonRpc.ParseError(null, $"parse error: {e.Message}"), false);
			}

			if (root.ValueKind != JsonValueKind.Object)
				return (null, JsonRpc.ParseError(null, "expected a JSON object"), false);

			if (root.TryGetProperty("id", out var idEl)) id = idEl.ValueKind == JsonValueKind.Null ? null : (object?)idEl.GetRawText();

			string? method = root.TryGetProperty("method", out var m) ? m.GetString() : null;
			JsonElement? args = root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object ? p : null;

			if (id == null)
			{
				// notification — fire and forget
				return (null, Array.Empty<byte>(), true);
			}

			if (string.IsNullOrEmpty(method))
				return (id, JsonRpc.ParseError(id, "missing method"), false);

			try
			{
				object? result = method switch
				{
					"initialize" => new Dictionary<string, object?>
					{
						["protocolVersion"] = JsonRpc.MCP_PROTOCOL_VERSION,
						["capabilities"] = new Dictionary<string, object?>
						{
							["tools"] = new Dictionary<string, object?> { ["listChanged"] = true },
							["resources"] = new Dictionary<string, object?> { ["listChanged"] = false, ["subscribe"] = false },
							["prompts"] = new Dictionary<string, object?>(),
						},
						["serverInfo"] = new Dictionary<string, object?> { ["name"] = "bizhawk-mcp-native", ["version"] = "0.2.0" },
					},
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
				return (id, JsonRpc.Success(id, result), false);
			}
			catch (JsonRpc.Error e)
			{
				return (id, JsonRpc.Failure(id, e), false);
			}
			catch (Exception e)
			{
				return (id, JsonRpc.Failure(id, new JsonRpc.Error(JsonRpc.Error.INTERNAL_ERROR, $"{e.GetType().Name}: {e.Message}")), false);
			}
		}

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
					sb.Append("1. Register known addresses as symbols (bizhawk_symbols_set) — import Ghidra exports into a namespace.\n");
					sb.Append("2. Locate the value: bizhawk_search_memory (try u8/u16/u32, both endianness) in domain ").Append(domain ?? "the current domain").Append(".\n");
					sb.Append("3. Narrow with bizhawk_wait_until / bizhawk_watch_change to catch dynamic changes across frames.\n");
					sb.Append("4. Pin down the writer: a bizhawk_watchpoint_add (write) on the address, then bizhawk_watchpoint_wait — or bizhawk_trace to see where the code runs.\n");
					sb.Append("5. Save the core state (bizhawk_memstate_save) before experiments and restore (bizhawk_memstate_load) between attempts.\n");
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
					sb.Append("1. Save the starting core state: bizhawk_memstate_save {slot: \"start\"}.\n");
					sb.Append("2. Use bizhawk_start_fixture with an input timeline (input_mode \"explicit\" releases buttons between entries) and sample position/velocity per frame.\n");
					sb.Append("3. After each attempt, restore with bizhawk_memstate_load {slot: \"start\"} so the next try starts identical.\n");
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
