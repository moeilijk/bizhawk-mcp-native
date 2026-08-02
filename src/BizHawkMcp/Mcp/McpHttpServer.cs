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
	///                asks for text/event-stream
	/// Not implemented: server-initiated messages, sessions, resources/prompts.
	/// </summary>
	public sealed class McpHttpServer
	{
		private readonly ExternalToolEntry _tool;
		private readonly UiDispatcher _ui;
		private readonly Action<string> _log;

		private readonly HttpListener _listener = new();
		private readonly CancellationTokenSource _cts = new();
		private McpToolset? _toolset;
		private Thread? _acceptThread;

		public McpHttpServer(ExternalToolEntry tool, UiDispatcher ui, Action<string> log)
		{
			_tool = tool;
			_ui = ui;
			_log = log;
		}

		public string BaseUrl { get; private set; } = "";

		public void Start()
		{
			var host = Environment.GetEnvironmentVariable("BIZHAWK_MCP_HOST") ?? "127.0.0.1";
			var port = int.TryParse(Environment.GetEnvironmentVariable("BIZHAWK_MCP_PORT"), out var p) ? p : 8767;
			BaseUrl = $"http://{host}:{port}/mcp/";
			_listener.Prefixes.Add(BaseUrl);
			_listener.Start();
			_toolset = new McpToolset(_tool, _ui);
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

		private (object? id, byte[] bytes, bool isNotification) Dispatch(string body)
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
						["capabilities"] = new Dictionary<string, object?> { ["tools"] = new Dictionary<string, object?> { ["listChanged"] = false } },
						["serverInfo"] = new Dictionary<string, object?> { ["name"] = "bizhawk-mcp-native", ["version"] = "0.1.0" },
					},
					"ping" => new Dictionary<string, object?>(),
					"tools/list" => new Dictionary<string, object?> { ["tools"] = _toolset!.ToolSchemas },
					"tools/call" => CallTool(args),
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

		private Dictionary<string, object?> CallTool(JsonElement? args)
		{
			if (args is not { } argsVal
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
