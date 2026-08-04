using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace BizHawkMcp.Mcp
{
	/// <summary>
	/// Minimal JSON-RPC 2.0 / MCP helpers. The Streamable HTTP subset we
	/// implement needs only: requests with an id (POST), notifications
	/// (id == null), and JSON-RPC error objects.
	///
	/// Dual-era protocol support (see docs/MCP-PROTOCOL.md):
	///   legacy (2025-11-25): "initialize" handshake, session-less, as before.
	///   modern (2026-07-28): stateless — every request declares its protocol
	///     version in params._meta["io.modelcontextprotocol/protocolVersion"]
	///     (and, on HTTP, in the MCP-Protocol-Version header); results carry
	///     "resultType" and _meta["io.modelcontextprotocol/serverInfo"].
	/// </summary>
	public static class JsonRpc
	{
		// Legacy (initialize handshake) revision we answer with.
		public const string MCP_PROTOCOL_VERSION = "2025-11-25";

		// Modern (stateless, per-request _meta) revision.
		public const string MODERN_PROTOCOL_VERSION = "2026-07-28";

		// Ordered list advertised by server/discover and
		// UnsupportedProtocolVersionError. Newest first.
		public static readonly string[] SUPPORTED_VERSIONS = { MODERN_PROTOCOL_VERSION, MCP_PROTOCOL_VERSION };

		public static bool SupportsVersion(string v) =>
			v == MODERN_PROTOCOL_VERSION || v == MCP_PROTOCOL_VERSION;

		public static bool IsModern(string v) => v == MODERN_PROTOCOL_VERSION;

		public const string ServerName = "bizhawk-mcp-native";

		// From the assembly's InformationalVersion (<Version> in the csproj) so
		// the advertised version can never drift from the packaged release.
		public static string ServerVersion
		{
			get
			{
				string v = "";
				var attrs = typeof(JsonRpc).Assembly.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false);
				if (attrs.Length > 0 && attrs[0] is AssemblyInformationalVersionAttribute info)
					v = info.InformationalVersion;
				if (string.IsNullOrEmpty(v))
					v = typeof(JsonRpc).Assembly.GetName().Version?.ToString() ?? "0.0.0";
				int plus = v.IndexOf('+'); // strip SourceLink build metadata
				return plus > 0 ? v.Substring(0, plus) : v;
			}
		}
		public static Dictionary<string, object?> ServerInfo() => new()
		{
			["name"] = ServerName,
			["version"] = ServerVersion,
			["instructions"] = "Controls the running BizHawk (EmuHawk) emulator instance: memory read/write, frame advancing, screenshots, savestates, TAS movie control, Lua scripting, memory search, watchpoints and freezes. Tool results are JSON in content[0].text — JSON.parse it.",
		};

		// Decodes an MCP header value that uses the =?base64?...?= sentinel
		// (Streamable HTTP "Value Encoding"); malformed values are returned raw
		// so the comparison below fails loudly.
		public static string DecodeHeaderValue(string v)
		{
			if (v.StartsWith("=?base64?", StringComparison.Ordinal) && v.EndsWith("?=", StringComparison.Ordinal))
			{
				try
				{
					string payload = v.Substring("=?base64?".Length, v.Length - "=?base64?".Length - 2);
					return Encoding.UTF8.GetString(Convert.FromBase64String(payload));
				}
				catch (FormatException)
				{
					// malformed sentinel — compare raw
				}
			}

			return v;
		}

		public sealed class Error : Exception
		{
			public int Code { get; }
			public object? ErrorData { get; }

			public Error(int code, string message, object? data = null)
				: base(message)
			{
				Code = code;
				ErrorData = data;
			}

			public const int PARSE_ERROR = -32700;
			public const int INVALID_REQUEST = -32600;
			public const int METHOD_NOT_FOUND = -32601;
			public const int INVALID_PARAMS = -32602;
			public const int INTERNAL_ERROR = -32603;

			// MCP-spec reserved range (-32020..-32099), revision 2026-07-28.
			public const int HEADER_MISMATCH = -32020;
			public const int UNSUPPORTED_PROTOCOL_VERSION = -32022;

			public static Error UnsupportedProtocolVersion(string requested) =>
				new(UNSUPPORTED_PROTOCOL_VERSION, "Unsupported protocol version",
					new Dictionary<string, object?> { ["supported"] = SUPPORTED_VERSIONS, ["requested"] = requested });

			public static Error HeaderMismatch(string message) => new(HEADER_MISMATCH, message);
		}

		public static byte[] Success(object? id, object? result, bool modern = false, long? ttlMs = null, string? cacheScope = null)
		{
			if (modern || ttlMs != null || cacheScope != null)
			{
				// results are always freshly-built dictionaries; decorate in place
				var d = result as Dictionary<string, object?>;
				if (d != null)
				{
					if (modern) d["resultType"] = "complete";
					if (ttlMs != null) d["ttlMs"] = ttlMs.Value;
					if (cacheScope != null) d["cacheScope"] = cacheScope;
					if (modern) d["_meta"] = new Dictionary<string, object?> { ["io.modelcontextprotocol/serverInfo"] = ServerInfo() };
				}
			}

			return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
		}

		public static byte[] Failure(object? id, Error error)
		{
			var err = new Dictionary<string, object?> { ["code"] = error.Code, ["message"] = error.Message };
			if (error.ErrorData != null) err["data"] = error.ErrorData;
			return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["error"] = err,
			});
		}

		public static byte[] ParseError(object? id, string message)
		{
			return Failure(id, new Error(Error.PARSE_ERROR, message));
		}

		// Compact JSON (no indentation): every response is agent-consumed, and
		// pretty-printed whitespace costs ~30-40% extra tokens on every call.
		// Human debugging is easier with a JSON formatter on the client side.
		public static string Pretty(object? value)
		{
			return JsonSerializer.Serialize(value);
		}
	}
}
