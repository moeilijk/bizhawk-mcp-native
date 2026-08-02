using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace BizHawkMcp.Mcp
{
	/// <summary>
	/// Minimal JSON-RPC 2.0 / MCP helpers. The Streamable HTTP subset we
	/// implement needs only: requests with an id (POST), notifications
	/// (id == null), and JSON-RPC error objects.
	/// </summary>
	public static class JsonRpc
	{
		public const string MCP_PROTOCOL_VERSION = "2025-06-18";

		public sealed class Error : Exception
		{
			public int Code { get; }

			public Error(int code, string message)
				: base(message)
			{
				Code = code;
			}

			public const int PARSE_ERROR = -32700;
			public const int INVALID_REQUEST = -32600;
			public const int METHOD_NOT_FOUND = -32601;
			public const int INVALID_PARAMS = -32602;
			public const int INTERNAL_ERROR = -32603;
		}

		public static byte[] Success(object? id, object? result)
		{
			return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?> { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
		}

		public static byte[] Failure(object? id, Error error)
		{
			return JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
			{
				["jsonrpc"] = "2.0",
				["id"] = id,
				["error"] = new Dictionary<string, object?> { ["code"] = error.Code, ["message"] = error.Message },
			});
		}

		public static byte[] ParseError(object? id, string message)
		{
			return Failure(id, new Error(Error.PARSE_ERROR, message));
		}

		public static string Pretty(object? value)
		{
			return JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
		}
	}
}
