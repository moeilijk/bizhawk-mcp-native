using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BizHawkMcp;
using BizHawkMcp.Mcp;
using Xunit;

namespace BizHawkMcp.Tests
{
	// Boots the REAL McpHttpServer (HttpListener) on a random port and talks to
	// it over actual HTTP — the same path a client (opencode, curl) uses. Linux
	// HttpListener supports loopback, so this runs in the test host too.
	public class HttpEndToEndTests
	{
		private static int FindFreePort()
		{
			var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
			l.Start();
			int port = ((IPEndPoint)l.LocalEndpoint).Port;
			l.Stop();
			return port;
		}

		private static async Task<(HttpStatusCode status, string body)> Post(string url, string json)
		{
			using var client = new HttpClient();
			var resp = await client.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
			return (resp.StatusCode, await resp.Content.ReadAsStringAsync());
		}

		[Fact]
		public async Task Initialize_tools_list_and_ping_over_real_http()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var apis = new FakeApis();
			var server = new McpHttpServer(apis, new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				var url = server.BaseUrl;

				// initialize
				var (status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"0\"}}}");
				Assert.Equal(HttpStatusCode.OK, status);
				using var init = JsonDocument.Parse(body);
				Assert.Equal("bizhawk-mcp-native", init.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
				Assert.True(init.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out var toolsCaps));
				Assert.True(toolsCaps.GetProperty("listChanged").GetBoolean());

				// tools/list
				(status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
				Assert.Equal(HttpStatusCode.OK, status);
				using var tools = JsonDocument.Parse(body);
				Assert.True(tools.RootElement.GetProperty("result").GetProperty("tools").GetArrayLength() > 50);

				// ping
				(status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"ping\"}");
				Assert.Equal(HttpStatusCode.OK, status);
				using var pong = JsonDocument.Parse(body);
				Assert.Equal("2.0", pong.RootElement.GetProperty("jsonrpc").GetString());
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}

		[Fact]
		public async Task Tools_call_roundtrip_over_real_http()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var apis = new FakeApis();
			var server = new McpHttpServer(apis, new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				var url = server.BaseUrl;

				// write then read via the real HTTP endpoint
				var (status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_write_memory\",\"arguments\":{\"address\":100,\"width\":8,\"value\":165}}}");
				Assert.Equal(HttpStatusCode.OK, status);

				(status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_read_memory\",\"arguments\":{\"address\":100,\"width\":8}}}");
				using var doc = JsonDocument.Parse(body);
				var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
				using var value = JsonDocument.Parse(text);
				Assert.Equal((ulong)165, value.RootElement.GetProperty("value").GetUInt64());
				Assert.Equal("big", value.RootElement.GetProperty("endianness").GetString());
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}

		[Fact]
		public async Task Unknown_method_returns_jsonrpc_error_over_http()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var server = new McpHttpServer(new FakeApis(), new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				var (status, body) = await Post(server.BaseUrl, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"nope\"}");
				Assert.Equal(HttpStatusCode.OK, status);
				using var doc = JsonDocument.Parse(body);
				Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}

		[Fact]
		public async Task Prompts_list_and_get_over_real_http()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var server = new McpHttpServer(new FakeApis(), new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				var url = server.BaseUrl;

				var (status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"prompts/list\"}");
				Assert.Equal(HttpStatusCode.OK, status);
				using var list = JsonDocument.Parse(body);
				var prompts = list.RootElement.GetProperty("result").GetProperty("prompts");
				Assert.Equal(2, prompts.GetArrayLength());
				Assert.Equal("memory_research", prompts[0].GetProperty("name").GetString());
				Assert.Equal("tas_frame", prompts[1].GetProperty("name").GetString());

				(status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"prompts/get\",\"params\":{\"name\":\"memory_research\",\"arguments\":{\"target\":\"jump height\"}}}");
				Assert.Equal(HttpStatusCode.OK, status);
				using var got = JsonDocument.Parse(body);
				var messages = got.RootElement.GetProperty("result").GetProperty("messages");
				Assert.Equal(1, messages.GetArrayLength());
				Assert.Equal("user", messages[0].GetProperty("role").GetString());
				var text = messages[0].GetProperty("content").GetProperty("text").GetString();
				Assert.Contains("jump height", text);
				Assert.Contains("bizhawk_search_memory", text);

				// initialize advertises the prompts capability
				(status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"0\"}}}");
				using var init = JsonDocument.Parse(body);
				Assert.True(init.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("prompts", out _));

				// unknown prompt → INVALID_PARAMS
				(status, body) = await Post(url, "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"prompts/get\",\"params\":{\"name\":\"nope\"}}");
				using var bad = JsonDocument.Parse(body);
				Assert.Equal(-32602, bad.RootElement.GetProperty("error").GetProperty("code").GetInt32());
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}

		[Fact]
		public async Task Sse_stream_carries_endpoint_then_tools_list_changed_notification()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var server = new McpHttpServer(new FakeApis(), new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				using var client = new HttpClient();
				client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/event-stream"));
				using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
				using var resp = await client.GetAsync(server.BaseUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
				Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
				Assert.Equal("text/event-stream", resp.Content.Headers.ContentType?.MediaType);

				using var reader = new System.IO.StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
				string? line;
				string endpointUrl = "";
				bool sawMessage = false;
				string messageData = "";
				while ((line = await reader.ReadLineAsync(cts.Token)) != null)
				{
					if (line.StartsWith("event: endpoint")) continue;
					if (line.StartsWith("data: ") && string.IsNullOrEmpty(endpointUrl)) endpointUrl = line["data: ".Length..];
					if (line.StartsWith("event: message")) sawMessage = true;
					if (sawMessage && line.StartsWith("data: "))
					{
						messageData = line["data: ".Length..];
						break;
					}
				}

				Assert.StartsWith("http://", endpointUrl);
				Assert.True(sawMessage, "first SSE stream must carry the tools/list_changed notification");
				Assert.Contains("notifications/tools/list_changed", messageData);
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}
		[Fact]
		public async Task Jsonrpc_batch_serves_each_request_in_one_roundtrip()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var server = new McpHttpServer(new FakeApis(), new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				// write + read + an error, all in ONE POST (array → array)
				var batch = "[{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_write_memory\",\"arguments\":{\"address\":100,\"width\":8,\"value\":165}}},"
					+ "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_read_memory\",\"arguments\":{\"address\":100,\"width\":8}}},"
					+ "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"nope\"}]";
				var (status, body) = await Post(server.BaseUrl, batch);
				Assert.Equal(HttpStatusCode.OK, status);
				using var doc = JsonDocument.Parse(body);
				Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
				Assert.Equal(3, doc.RootElement.GetArrayLength());
				Assert.True(doc.RootElement[0].TryGetProperty("result", out _));
				// the read call: result → content[0].text → JSON with value 165
				var text = doc.RootElement[1].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
				using var value = JsonDocument.Parse(text);
				Assert.Equal((ulong)165, value.RootElement.GetProperty("value").GetUInt64());
				// unknown method → per-element error, batch survives
				Assert.Equal(-32601, doc.RootElement[2].GetProperty("error").GetProperty("code").GetInt32());
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}

		[Fact]
		public async Task Raw_get_read_returns_memory_bytes_directly()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var apis = new FakeApis();
			var server = new McpHttpServer(apis, new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				apis.MemoryApi.Bytes[0] = 0xDE;
				apis.MemoryApi.Bytes[99] = 0xAD;
				using var client = new HttpClient();
				// range is HEX: 0:64 = 0x64 = 100 bytes
				var resp = await client.GetAsync($"{server.BaseUrl}read/68K%20RAM/0:64");
				Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
				Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType?.MediaType);
				var bytes = await resp.Content.ReadAsByteArrayAsync();
				Assert.Equal(100, bytes.Length);
				Assert.Equal((byte)0xDE, bytes[0]);
				Assert.Equal((byte)0xAD, bytes[99]);

				// out-of-domain range → 400, not a crash
				var bad = await client.GetAsync($"{server.BaseUrl}read/68K%20RAM/0:100000");
				Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}

		[Fact]
		public async Task Raw_get_artifact_serves_file_bytes()
		{
			int port = FindFreePort();
			Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", port.ToString());
			var apis = new FakeApis();
			var server = new McpHttpServer(apis, new InlineDispatcher(), _ => { });
			server.Start();
			try
			{
				// create an artifact (dump_memory) via the JSON-RPC endpoint
				var (_, body) = await Post(server.BaseUrl, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_dump_memory\",\"arguments\":{\"domain\":\"68K RAM\"}}}");
				using var doc = JsonDocument.Parse(body);
				var text = doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
				using var result = JsonDocument.Parse(text);
				string uri = result.RootElement.GetProperty("resource").GetString()!;
				Assert.StartsWith("bizhawk://", uri);

				using var client = new HttpClient();
				var resp = await client.GetAsync($"{server.BaseUrl}artifacts/{uri["bizhawk://".Length..]}");
				Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
				Assert.Equal("application/octet-stream", resp.Content.Headers.ContentType?.MediaType);
				var bytes = await resp.Content.ReadAsByteArrayAsync();
				Assert.Equal(65536, bytes.Length);

				// unknown artifact → 404
				var missing = await client.GetAsync($"{server.BaseUrl}artifacts/deadbeef");
				Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
			}
			finally
			{
				server.Stop();
				Environment.SetEnvironmentVariable("BIZHAWK_MCP_PORT", null);
			}
		}
	}
}
