using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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
				Assert.True(init.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("tools", out _));

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
	}
}
