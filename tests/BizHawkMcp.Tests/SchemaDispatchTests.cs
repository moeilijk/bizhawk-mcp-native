using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BizHawkMcp;
using BizHawkMcp.Mcp;
using Xunit;

namespace BizHawkMcp.Tests
{
	public class ToolSchemaTests
	{
		private static List<Dictionary<string, object?>> Schemas() => new FakeApis().Toolset().ToolSchemas.ToList();

		[Fact]
		public void Every_schema_has_required_fields()
		{
			foreach (var s in Schemas())
			{
				Assert.True(s.ContainsKey("name"));
				Assert.True(s.ContainsKey("description"));
				Assert.True(s.ContainsKey("inputSchema"));
				var input = Assert.IsType<Dictionary<string, object?>>(s["inputSchema"]);
				Assert.Equal("object", input["type"]);
				Assert.True(input.ContainsKey("properties"));
			}
		}

		[Fact]
		public void Tool_names_are_unique()
		{
			var names = Schemas().Select(s => (string)s["name"]!).ToList();
			Assert.Equal(names.Count, names.Distinct().Count());
		}

		[Fact]
		public void Every_tool_name_dispatches()
		{
			// A schema entry without a dispatch arm (or vice-versa) breaks tools silently.
			var ts = new FakeApis().Toolset();
			foreach (var s in Schemas())
			{
				var name = (string)s["name"]!;
				string result;
				try
				{
					result = ts.Call(name, null);
				}
				catch (JsonRpc.Error e)
				{
					Assert.NotEqual(JsonRpc.Error.METHOD_NOT_FOUND, e.Code);
					continue;
				}
				Assert.False(string.IsNullOrEmpty(result));
			}
		}

		[Fact]
		public void Params_declared_have_descriptions()
		{
			foreach (var s in Schemas())
			{
				var input = Assert.IsType<Dictionary<string, object?>>(s["inputSchema"]);
				var props = Assert.IsType<Dictionary<string, object?>>(input["properties"]);
				foreach (var kv in props)
				{
					var spec = Assert.IsType<Dictionary<string, object?>>(kv.Value);
					Assert.True(spec.ContainsKey("description"), $"{s["name"]}.{kv.Key} missing description");
					Assert.True(spec.ContainsKey("type"), $"{s["name"]}.{kv.Key} missing type");
				}
			}
		}

		[Fact]
		public void Read_memory_schema_has_defaults()
		{
			var read = Schemas().First(s => (string)s["name"]! == "bizhawk_read_memory");
			var props = Assert.IsType<Dictionary<string, object?>>(
				Assert.IsType<Dictionary<string, object?>>(read["inputSchema"])["properties"]);
			var width = Assert.IsType<Dictionary<string, object?>>(props["width"]);
			Assert.Equal(8, width["default"]);
			Assert.Equal("integer", width["type"]);
		}
	}

	public class DispatchTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpHttpServer _server;

		public DispatchTests()
		{
			_server = new McpHttpServer(_apis, new InlineDispatcher(), _ => { });
		}

		private static JsonDocument ParseResult((object? id, byte[] bytes, bool isNotification, int httpStatus) r)
		{
			Assert.False(r.isNotification);
			Assert.NotNull(r.bytes);
			Assert.NotEmpty(r.bytes);
			return JsonDocument.Parse(r.bytes);
		}

		[Fact]
		public void Initialize_advertises_tools_and_resources()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
			using var doc = ParseResult(r);
			var caps = doc.RootElement.GetProperty("result").GetProperty("capabilities");
			Assert.True(caps.TryGetProperty("tools", out _));
			Assert.True(caps.TryGetProperty("resources", out _));
		}

		[Fact]
		public void Ping_returns_empty_result()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}");
			using var doc = ParseResult(r);
			Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("result").ValueKind);
		}

		[Fact]
		public void Tools_list_returns_array()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\",\"params\":{}}");
			using var doc = ParseResult(r);
			var tools = doc.RootElement.GetProperty("result").GetProperty("tools");
			Assert.Equal(JsonValueKind.Array, tools.ValueKind);
			Assert.True(tools.GetArrayLength() > 0);
		}

		[Fact]
		public void Tools_call_returns_text_content()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_ping\",\"arguments\":{}}}");
			using var doc = ParseResult(r);
			var content = doc.RootElement.GetProperty("result").GetProperty("content");
			Assert.Equal("text", content[0].GetProperty("type").GetString());
			Assert.Equal("pong", content[0].GetProperty("text").GetString());
			Assert.False(doc.RootElement.GetProperty("result").GetProperty("isError").GetBoolean());
		}

		[Fact]
		public void Unknown_method_returns_minus_32601()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"bogus\"}");
			using var doc = ParseResult(r);
			Assert.Equal(-32601, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Malformed_json_returns_parse_error()
		{
			var r = _server.Dispatch("{ not json");
			using var doc = ParseResult(r);
			Assert.Equal(-32700, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Notification_is_marked_notification()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
			Assert.True(r.isNotification);
		}

		[Fact]
		public void Unknown_tool_name_returns_error_not_crash()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"nope\",\"arguments\":{}}}");
			using var doc = ParseResult(r);
			Assert.True(doc.RootElement.TryGetProperty("error", out _));
		}

		[Fact]
		public void Resources_read_requires_uri()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"resources/read\",\"params\":{}}");
			using var doc = ParseResult(r);
			Assert.Equal(-32602, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Resources_templates_list_returns_read_template()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"resources/templates/list\"}");
			using var doc = ParseResult(r);
			var templates = doc.RootElement.GetProperty("result").GetProperty("resourceTemplates");
			Assert.Equal(2, templates.GetArrayLength());
			Assert.Equal("bizhawk://read/{domain}/{range}", templates[0].GetProperty("uriTemplate").GetString());
		}

		[Fact]
		public void Resources_read_template_via_dispatch()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"resources/read\",\"params\":{\"uri\":\"bizhawk://read/68K%20RAM/0:1\"}}");
			using var doc = ParseResult(r);
			var contents = doc.RootElement.GetProperty("result").GetProperty("contents")[0];
			Assert.Equal("application/octet-stream", contents.GetProperty("mimeType").GetString());
		}

		// ── dual-era protocol (2025-11-25 legacy + 2026-07-28 modern) ─────────

		[Fact]
		public void Initialize_advertises_latest_legacy_protocol_version()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{}}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			Assert.Equal("2025-11-25", result.GetProperty("protocolVersion").GetString());
			Assert.Equal("bizhawk-mcp-native", result.GetProperty("serverInfo").GetProperty("name").GetString());
			Assert.False(string.IsNullOrEmpty(result.GetProperty("serverInfo").GetProperty("version").GetString()));
			// legacy envelope: no resultType / _meta
			Assert.False(result.TryGetProperty("resultType", out _));
			Assert.False(result.TryGetProperty("_meta", out _));
		}

		[Fact]
		public void Discover_returns_supported_versions_capabilities_and_cache_policy()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"server/discover\",\"params\":{}}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			var versions = result.GetProperty("supportedVersions");
			Assert.Equal(2, versions.GetArrayLength());
			Assert.Equal("2026-07-28", versions[0].GetString());
			Assert.Equal("2025-11-25", versions[1].GetString());
			Assert.True(result.GetProperty("capabilities").TryGetProperty("tools", out _));
			Assert.True(result.GetProperty("capabilities").TryGetProperty("resources", out _));
			Assert.True(result.GetProperty("capabilities").TryGetProperty("prompts", out _));
			Assert.True(result.TryGetProperty("instructions", out _));
			Assert.Equal(3600000, result.GetProperty("ttlMs").GetInt64());
			Assert.Equal("public", result.GetProperty("cacheScope").GetString());
		}

		[Fact]
		public void Modern_request_gets_complete_resultType_serverInfo_and_ttl()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			Assert.Equal("complete", result.GetProperty("resultType").GetString());
			var info = result.GetProperty("_meta").GetProperty("io.modelcontextprotocol/serverInfo");
			Assert.Equal("bizhawk-mcp-native", info.GetProperty("name").GetString());
			Assert.False(string.IsNullOrEmpty(info.GetProperty("version").GetString()));
			Assert.True(result.GetProperty("tools").GetArrayLength() > 0);
			Assert.Equal(3600000, result.GetProperty("ttlMs").GetInt64());
			Assert.Equal("public", result.GetProperty("cacheScope").GetString());
		}

		[Fact]
		public void Modern_tool_call_gets_resultType_but_no_cache_fields()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_ping\",\"arguments\":{},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			Assert.Equal("complete", result.GetProperty("resultType").GetString());
			Assert.False(result.TryGetProperty("ttlMs", out _));
			Assert.Equal("pong", result.GetProperty("content")[0].GetProperty("text").GetString());
		}

		[Fact]
		public void Unsupported_declared_version_returns_32022_with_supported_list()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"1900-01-01\"}}}");
			using var doc = ParseResult(r);
			var err = doc.RootElement.GetProperty("error");
			Assert.Equal(-32022, err.GetProperty("code").GetInt32());
			Assert.Equal("1900-01-01", err.GetProperty("data").GetProperty("requested").GetString());
			var supported = err.GetProperty("data").GetProperty("supported");
			Assert.Contains("2026-07-28", supported.EnumerateArray().Select(e => e.GetString()));
		}

		[Fact]
		public void Header_version_conflict_with_meta_returns_32020()
		{
			var r = _server.Dispatch(
				"{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}",
				hdrVersion: "2025-11-25");
			using var doc = ParseResult(r);
			Assert.Equal(-32020, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Modern_header_method_mismatch_returns_32020()
		{
			var r = _server.Dispatch(
				"{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}",
				hdrVersion: "2026-07-28", hdrMethod: "tools/call");
			using var doc = ParseResult(r);
			Assert.Equal(-32020, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Modern_header_name_mismatch_returns_32020()
		{
			var r = _server.Dispatch(
				"{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_ping\",\"arguments\":{},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}",
				hdrVersion: "2026-07-28", hdrName: "bogus");
			using var doc = ParseResult(r);
			Assert.Equal(-32020, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Matching_modern_headers_are_accepted()
		{
			var r = _server.Dispatch(
				"{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"bizhawk_ping\",\"arguments\":{},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}",
				hdrVersion: "2026-07-28", hdrMethod: "tools/call", hdrName: "bizhawk_ping");
			using var doc = ParseResult(r);
			Assert.Equal("pong", doc.RootElement.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
		}

		[Fact]
		public void Header_declares_modern_without_body_meta()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/list\"}", hdrVersion: "2026-07-28");
			using var doc = ParseResult(r);
			Assert.Equal("complete", doc.RootElement.GetProperty("result").GetProperty("resultType").GetString());
		}

		[Fact]
		public void Header_declares_unsupported_version_returns_32022()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/list\"}", hdrVersion: "1900-01-01");
			using var doc = ParseResult(r);
			Assert.Equal(-32022, doc.RootElement.GetProperty("error").GetProperty("code").GetInt32());
		}

		[Fact]
		public void Legacy_declared_version_gets_legacy_envelope_but_cache_policy()
		{
			// a request declaring the LEGACY revision in _meta is served in the
			// legacy era (no resultType/_meta) — but still cacheable
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/list\",\"params\":{\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2025-11-25\"}}}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			Assert.False(result.TryGetProperty("resultType", out _));
			Assert.True(result.TryGetProperty("ttlMs", out _));
			Assert.Equal("public", result.GetProperty("cacheScope").GetString());
		}

		[Fact]
		public void Tools_list_carries_cache_policy_in_legacy_mode()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/list\"}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			Assert.Equal(3600000, result.GetProperty("ttlMs").GetInt64());
			Assert.Equal("public", result.GetProperty("cacheScope").GetString());
		}

		[Fact]
		public void Resources_list_is_cacheable_but_private()
		{
			var r = _server.Dispatch("{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"resources/list\"}");
			using var doc = ParseResult(r);
			var result = doc.RootElement.GetProperty("result");
			Assert.True(result.GetProperty("ttlMs").GetInt64() > 0);
			Assert.Equal("private", result.GetProperty("cacheScope").GetString());
		}
	}
}
