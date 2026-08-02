using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

using BizHawk.Client.Common;

using BizHawkMcp.Mcp;

namespace BizHawkMcp
{
	/// <summary>
	/// Tool registry + dispatch. Every handler runs on the UI thread via
	/// <see cref="UiDispatcher"/> (BizHawk APIs are not safe off the UI thread).
	/// </summary>
	public sealed class McpToolset
	{
		private readonly ExternalToolEntry _tool;
		private readonly UiDispatcher _ui;

		public McpToolset(ExternalToolEntry tool, UiDispatcher ui)
		{
			_tool = tool;
			_ui = ui;
		}

		public IReadOnlyList<Dictionary<string, object?>> ToolSchemas { get; } =
		[
			Tool("bizhawk_ping", "Ping the tool. Returns \"pong\" if the plugin and server are alive.", []),
			Tool("bizhawk_get_info", "ROM info, framecount, pause state and active memory domain.", []),
			Tool("bizhawk_read_memory", "Read u8/u16/u32 (little-endian by default) from a memory domain.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to BizHawk's current one)."),
			]),
			Tool("bizhawk_write_memory", "Write u8/u16/u32 to a memory domain.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("value", "integer", "Value to write (must fit the width)."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_range", "Read a contiguous range (up to 4096 bytes) and return it as hex.", [
				Param("address", "integer", "Start offset."),
				Param("length", "integer", "Bytes to read, 1..4096.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_use_memory_domain", "Switch the active memory domain.", [
				Param("domain", "string", "Domain name, e.g. \"WRAM\"."),
			]),
			Tool("bizhawk_set_big_endian", "Toggle big-endian interpretation for u16/u32 reads/writes.", [
				Param("enabled", "boolean", "True for big-endian.", false),
			]),
			Tool("bizhawk_press_buttons", "Set joypad state for the NEXT frame.", [
				Param("buttons", "object", "Map of button name -> pressed bool, e.g. {\"A\": true, \"Right\": true}."),
				Param("controller", "integer", "Optional controller index (1-based).", 1),
			]),
			Tool("bizhawk_frame_advance", "Advance exactly N frames.", [
				Param("count", "integer", "Frames to advance, 1..600.", 1),
			]),
			Tool("bizhawk_screenshot", "Save a PNG of the current frame.", [
				Param("path", "string", "Absolute path writable by EmuHawk, e.g. C:/temp/snap.png."),
			]),
			Tool("bizhawk_save_state", "Save an emulator state to a file.", [
				Param("path", "string", "Absolute .State path."),
			]),
			Tool("bizhawk_load_state", "Load an emulator state from a file.", [
				Param("path", "string", "Absolute .State path."),
			]),
			Tool("bizhawk_shutdown", "Stop the MCP server (plugin stays loaded; restart via the form's button or the emulator's Lua/tools menu).", []),
		];

		public string Call(string name, JsonElement? args)
		{
			return name switch
			{
				"bizhawk_ping" => _ui.Invoke(() => "pong"),
				"bizhawk_get_info" => _ui.Invoke(GetInfo),
				"bizhawk_read_memory" => _ui.Invoke(() => ReadMemory(args)),
				"bizhawk_write_memory" => _ui.Invoke(() => WriteMemory(args)),
				"bizhawk_read_range" => _ui.Invoke(() => ReadRange(args)),
				"bizhawk_use_memory_domain" => _ui.Invoke(() => UseMemoryDomain(args)),
				"bizhawk_set_big_endian" => _ui.Invoke(() => SetBigEndian(args)),
				"bizhawk_press_buttons" => _ui.Invoke(() => PressButtons(args)),
				"bizhawk_frame_advance" => _ui.Invoke(() => FrameAdvance(args)),
				"bizhawk_screenshot" => _ui.Invoke(() => Screenshot(args)),
				"bizhawk_save_state" => _ui.Invoke(() => SaveState(args)),
				"bizhawk_load_state" => _ui.Invoke(() => LoadState(args)),
				"bizhawk_shutdown" => Shutdown(),
				_ => throw new JsonRpc.Error(JsonRpc.Error.METHOD_NOT_FOUND, $"unknown tool: {name}"),
			};
		}

		// ── handlers ───────────────────────────────────────────────────────────

		private string GetInfo()
		{
			var game = _tool.Emulation!.GetGameInfo();
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["rom_name"] = game?.Name,
				["rom_hash"] = game?.Hash,
				["system_id"] = _tool.Emulation!.GetSystemId(),
				["framecount"] = _tool.Emulation!.FrameCount(),
				["paused"] = _tool.EmuClient!.IsPaused(),
				["memory_domain"] = _tool.Memory!.GetCurrentMemoryDomain(),
				["memory_domain_size"] = _tool.Memory!.GetCurrentMemoryDomainSize(),
				["server"] = _tool.ServerUrl,
			});
		}

		private string ReadMemory(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int width = RequireInt(a, "width", 8);
			string? domain = OptionalString(a, "domain");
			ulong value = width switch
			{
				8 => _tool.Memory!.ReadByte(address, domain),
				16 => _tool.Memory!.ReadU16(address, domain),
				32 => _tool.Memory!.ReadU32(address, domain),
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
			};
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["value"] = value });
		}

		private string WriteMemory(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int width = RequireInt(a, "width", 8);
			ulong value = RequireULong(a, "value");
			string? domain = OptionalString(a, "domain");
			ulong max = width switch
			{
				8 => 0xFFUL,
				16 => 0xFFFFUL,
				32 => 0xFFFFFFFFUL,
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
			};
			if (value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
			switch (width)
			{
				case 8: _tool.Memory!.WriteU8(address, (uint)value, domain); break;
				case 16: _tool.Memory!.WriteU16(address, (uint)value, domain); break;
				case 32: _tool.Memory!.WriteU32(address, (uint)value, domain); break;
			}
			return "ok";
		}

		private string ReadRange(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int length = RequireInt(a, "length", 256);
			string? domain = OptionalString(a, "domain");
			if (length is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..4096");
			var sb = new System.Text.StringBuilder(length * 3);
			for (var i = 0; i < length; i++) sb.Append(_tool.Memory!.ReadByte(address + i, domain).ToString("X2")).Append(' ');
			return sb.ToString().TrimEnd();
		}

		private string UseMemoryDomain(JsonElement? args)
		{
			var a = Required(args);
			string? domain = RequireString(a, "domain");
			bool ok = _tool.Memory!.UseMemoryDomain(domain);
			return ok ? $"domain set to {domain}" : $"unknown domain: {domain}";
		}

		private string SetBigEndian(JsonElement? args)
		{
			var a = Required(args);
			bool enabled = a.TryGetProperty("enabled", out var v) && v.GetBoolean();
			_tool.Memory!.SetBigEndian(enabled);
			return $"big-endian = {enabled}";
		}

		private string PressButtons(JsonElement? args)
		{
			var a = Required(args);
			if (!a.TryGetProperty("buttons", out var buttons) || buttons.ValueKind != JsonValueKind.Object)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "buttons must be an object {button: bool}");
			int? controller = a.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1;
			var map = new Dictionary<string, bool>();
			foreach (var prop in buttons.EnumerateObject()) map[prop.Name] = prop.Value.GetBoolean();
			_tool.Joypad!.Set(map, controller);
			return $"joypad set for next frame: {string.Join("+", map.Keys)}";
		}

		private string FrameAdvance(JsonElement? args)
		{
			var a = Required(args);
			int count = RequireInt(a, "count", 1);
			if (count is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "count must be 1..600");
			for (var i = 0; i < count; i++)
			{
				_tool.EmuClient!.DoFrameAdvance();
				System.Windows.Forms.Application.DoEvents();
			}
			return $"advanced {count} frame(s)";
		}

		private string Screenshot(JsonElement? args)
		{
			var a = Required(args);
			string path = RequireString(a, "path");
			_tool.EmuClient!.Screenshot(path);
			return $"screenshot saved: {path}";
		}

		private string SaveState(JsonElement? args)
		{
			var a = Required(args);
			string path = RequireString(a, "path");
			_tool.SaveState!.Save(path);
			return $"state saved: {path}";
		}

		private string LoadState(JsonElement? args)
		{
			var a = Required(args);
			string path = RequireString(a, "path");
			bool ok = _tool.SaveState!.Load(path);
			return ok ? $"state loaded: {path}" : $"failed to load state: {path}";
		}

		private string Shutdown()
		{
			_ui.Invoke(_tool.StopServer);
			return "server stopping";
		}

		// ── param helpers ──────────────────────────────────────────────────────

		private static JsonElement Required(JsonElement? args)
		{
			if (args is not { } a) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "missing arguments");
			return a;
		}

		private static long RequireLong(JsonElement a, string name)
		{
			if (a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetInt64();
			throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"missing integer param: {name}");
		}

		private static int RequireInt(JsonElement a, string name, int fallback)
			=> a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

		private static ulong RequireULong(JsonElement a, string name)
		{
			if (a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) return v.GetUInt64();
			throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"missing integer param: {name}");
		}

		private static string RequireString(JsonElement a, string name)
		{
			if (a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString()!;
			throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"missing string param: {name}");
		}

		private static string? OptionalString(JsonElement a, string name)
			=> a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

		private static Dictionary<string, object?> Tool(string name, string description, List<object?> parameters)
		{
			return new Dictionary<string, object?>
			{
				["name"] = name,
				["description"] = description,
				["inputSchema"] = new Dictionary<string, object?>
				{
					["type"] = "object",
					["properties"] = SchemaProperties(parameters),
				},
			};
		}

		private static Dictionary<string, object?> SchemaProperties(List<object?> parameters)
		{
			var props = new Dictionary<string, object?>();
			foreach (var p in parameters)
			{
				var d = (Dictionary<string, object?>)p!;
				var spec = new Dictionary<string, object?> { ["type"] = d["type"], ["description"] = d["desc"] };
				if (d.TryGetValue("default", out var def)) spec["default"] = def;
				props[(string)d["name"]!] = spec;
			}

			return props;
		}

		private static object? Param(string name, string type, string desc, object? def = null)
		{
			var d = new Dictionary<string, object?> { ["name"] = name, ["type"] = type, ["desc"] = desc };
			if (def != null) d["default"] = def;
			return d;
		}
	}
}
