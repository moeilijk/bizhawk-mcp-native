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
		private readonly IHostApis _tool;
		private readonly IUiDispatcher _ui;

		// endianness state: SetBigEndian() has no getter in ApiHawk, so we track
		// it ourselves. Defaults are core-aware (big-endian on 68K/SNES/N64-ish
		// systems), overridable per session via bizhawk_set_big_endian.
		private bool? _bigEndianOverride;
		private string? _lastSystemId;

		public McpToolset(IHostApis tool, IUiDispatcher ui)
		{
			_tool = tool;
			_ui = ui;
		}

		public IReadOnlyList<Dictionary<string, object?>> ToolSchemas { get; } =
		[
			Tool("bizhawk_ping", "Ping the tool. Returns \"pong\" if the plugin and server are alive.", []),
			Tool("bizhawk_get_info", "ROM info, framecount, pause state, current endianness and active memory domain (JSON).", []),
			Tool("bizhawk_read_memory", "Read u8/u16/u32 from a memory domain. Endianness follows the core default (big-endian on Genesis/SNES/N64) unless bizhawk_set_big_endian overrode it.", [
				Param("address", "integer", "Offset in the domain, 0-based. For bus domains (e.g. M68K BUS) use the raw bus address (e.g. 0xFFFBCA)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to BizHawk's current one)."),
			]),
			Tool("bizhawk_write_memory", "Write u8/u16/u32 to a memory domain. Endianness follows the core default unless bizhawk_set_big_endian overrode it.", [
				Param("address", "integer", "Offset in the domain, 0-based. For bus domains use the raw bus address."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("value", "integer", "Value to write (must fit the width)."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_range", "Read a contiguous range (up to 4096 bytes) and return it as hex.", [
				Param("address", "integer", "Start offset."),
				Param("length", "integer", "Bytes to read, 1..4096.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_list_memory_domains", "List all memory domains with sizes (JSON). Offsets are domain-relative: RAM domains use 0-based offsets (68K RAM 0xFBCA = bus 0xFFFBCA), bus domains take raw bus addresses.", []),
			Tool("bizhawk_use_memory_domain", "Switch the active memory domain.", [
				Param("domain", "string", "Domain name, e.g. \"WRAM\"."),
			]),
			Tool("bizhawk_search_memory", "Scan a memory domain for a value (stateless one-shot; endianness as bizhawk_read_memory). Returns JSON with matching addresses. Pass previous hits in \"addresses\" to narrow down across calls.", [
				Param("value", "integer", "Value to match (must fit the width)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to BizHawk's current one)."),
				Param("range_start", "integer", "First offset to scan.", 0),
				Param("range_length", "integer", "Bytes to scan (default: whole domain)."),
				Param("max_results", "integer", "Stop after this many matches, 1..4096.", 256),
				Param("addresses", "array", "Optional list of addresses to restrict the scan to (up to 4096)."),
			]),
			Tool("bizhawk_set_big_endian", "Toggle big-endian interpretation for u16/u32 reads/writes.", [
				Param("enabled", "boolean", "True for big-endian.", false),
			]),
			Tool("bizhawk_hash_region", "SHA1 hash of a memory region (useful to detect changes).", [
				Param("address", "integer", "Start offset."),
				Param("length", "integer", "Bytes to hash, 1..1048576.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_signed", "Read s8/s16/s24/s32 from a memory domain. Endianness as bizhawk_read_memory.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("width", "integer", "8, 16, 24 or 32.", 8),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_write_signed", "Write s8/s16/s24/s32 to a memory domain. Endianness as bizhawk_read_memory.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("width", "integer", "8, 16, 24 or 32.", 8),
				Param("value", "integer", "Value to write (must fit the width)."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_float", "Read a 32-bit float from a memory domain. Endianness as bizhawk_read_memory.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_write_float", "Write a 32-bit float to a memory domain. Endianness as bizhawk_read_memory.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("value", "number", "Float value to write."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_press_buttons", "Set joypad state for the NEXT frame.", [
				Param("buttons", "object", "Map of button name -> pressed bool, e.g. {\"A\": true, \"Right\": true}."),
				Param("controller", "integer", "Optional controller index (1-based).", 1),
			]),
			Tool("bizhawk_frame_advance", "Advance exactly N frames. If paused, temporarily unpauses and restores the pause afterwards, so frames actually run.", [
				Param("count", "integer", "Frames to advance, 1..600.", 1),
			]),
			Tool("bizhawk_pause", "Pause emulation. Returns the new paused state.", []),
			Tool("bizhawk_unpause", "Unpause emulation. Returns the new paused state.", []),
			Tool("bizhawk_toggle_pause", "Toggle pause. Returns the new paused state.", []),
			Tool("bizhawk_speed_mode", "Set emulation speed as a percent of full speed.", [
				Param("percent", "integer", "e.g. 100 = normal, 50 = half speed, 400 = turbo."),
			]),
			Tool("bizhawk_get_joypad", "Read the current joypad state as a map of button -> value (bool or int for analog).", [
				Param("controller", "integer", "Optional controller index (1-based).", 1),
			]),
			Tool("bizhawk_get_registers", "CPU registers as a map of name -> value.", []),
			Tool("bizhawk_set_register", "Write a CPU register.", [
				Param("register", "string", "Register name, e.g. \"PC\", \"A\"."),
				Param("value", "integer", "Value to write."),
			]),
			Tool("bizhawk_disassemble", "Disassemble the instruction at a program counter address.", [
				Param("pc", "integer", "Program counter address."),
				Param("name", "string", "Optional disassembler name (defaults to the core's)."),
			]),
			Tool("bizhawk_lag_count", "Lag status: is the current frame lagging and the total lag count.", []),
			Tool("bizhawk_screenshot", "Save a PNG of the current frame. Omit \"path\" to save into the host temp dir (bizhawk-mcp). Paths are host-side: when EmuHawk runs on Windows they must be Windows paths (e.g. F:/temp/shot.png). Returns the effective absolute path and an MCP resource URI to fetch the image bytes.", [
				Param("path", "string", "Optional absolute path writable by EmuHawk, e.g. C:/temp/snap.png. Defaults to a temp file."),
			]),
			Tool("bizhawk_save_state", "Save an emulator state to a file.", [
				Param("path", "string", "Absolute .State path."),
			]),
			Tool("bizhawk_load_state", "Load an emulator state from a file.", [
				Param("path", "string", "Absolute .State path."),
			]),
			Tool("bizhawk_shutdown", "Stop the MCP server (plugin stays loaded; restart via the form's button or the emulator's Lua/tools menu).", []),
			Tool("bizhawk_overlay_text", "Draw text on the emulator's video output (GUI layer).", [
				Param("x", "integer", "X position."),
				Param("y", "integer", "Y position."),
				Param("text", "string", "Text to draw."),
				Param("color", "string", "Optional hex color, e.g. \"#FFFFFF\"."),
				Param("fontsize", "integer", "Optional font size in pixels."),
			]),
			Tool("bizhawk_clear_overlay", "Remove all text drawn on the video output.", []),
			Tool("bizhawk_osd_message", "Show a message in the emulator's OSD (on-screen display).", [
				Param("message", "string", "Text to show."),
				Param("duration", "integer", "Optional duration in ms."),
			]),
			Tool("bizhawk_movie_info", "TAS movie info: loaded, filename, mode, length, rerecords, fps, header.", []),
			Tool("bizhawk_movie_input", "Get the input log of a movie frame as a mnemonic string.", [
				Param("frame", "integer", "Frame number (0-based)."),
			]),
			Tool("bizhawk_host_input", "Read the host's physical input (keyboard, mouse, gamepad).", []),
			Tool("bizhawk_userdata_set", "Store a value in EmuHawk's user data store (persists across sessions).", [
				Param("key", "string", "Key name."),
				Param("value", "string", "Value to store."),
			]),
			Tool("bizhawk_userdata_get", "Read a value from EmuHawk's user data store.", [
				Param("key", "string", "Key name."),
			]),
			Tool("bizhawk_userdata_clear", "Clear all stored user data (or a single key).", [
				Param("key", "string", "Optional key to remove; omit to clear all."),
			]),
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
				"bizhawk_list_memory_domains" => _ui.Invoke(ListMemoryDomains),
				"bizhawk_search_memory" => _ui.Invoke(() => SearchMemory(args)),
				"bizhawk_set_big_endian" => _ui.Invoke(() => SetBigEndian(args)),
				"bizhawk_hash_region" => _ui.Invoke(() => HashRegion(args)),
				"bizhawk_read_signed" => _ui.Invoke(() => ReadSigned(args)),
				"bizhawk_write_signed" => _ui.Invoke(() => WriteSigned(args)),
				"bizhawk_read_float" => _ui.Invoke(() => ReadFloat(args)),
				"bizhawk_write_float" => _ui.Invoke(() => WriteFloat(args)),
				"bizhawk_press_buttons" => _ui.Invoke(() => PressButtons(args)),
				"bizhawk_frame_advance" => _ui.Invoke(() => FrameAdvance(args)),
				"bizhawk_pause" => _ui.Invoke(() => PauseTool()),
				"bizhawk_unpause" => _ui.Invoke(() => UnpauseTool()),
				"bizhawk_toggle_pause" => _ui.Invoke(() => TogglePauseTool()),
				"bizhawk_speed_mode" => _ui.Invoke(() => SpeedMode(args)),
				"bizhawk_get_joypad" => _ui.Invoke(() => GetJoypad(args)),
				"bizhawk_get_registers" => _ui.Invoke(GetRegisters),
				"bizhawk_set_register" => _ui.Invoke(() => SetRegister(args)),
				"bizhawk_disassemble" => _ui.Invoke(() => Disassemble(args)),
				"bizhawk_lag_count" => _ui.Invoke(LagCount),
				"bizhawk_screenshot" => _ui.Invoke(() => Screenshot(args)),
				"bizhawk_save_state" => _ui.Invoke(() => SaveState(args)),
				"bizhawk_load_state" => _ui.Invoke(() => LoadState(args)),
				"bizhawk_shutdown" => Shutdown(),
				"bizhawk_overlay_text" => _ui.Invoke(() => OverlayText(args)),
				"bizhawk_clear_overlay" => _ui.Invoke(() => ClearOverlay()),
				"bizhawk_osd_message" => _ui.Invoke(() => OsdMessage(args)),
				"bizhawk_movie_info" => _ui.Invoke(MovieInfo),
				"bizhawk_movie_input" => _ui.Invoke(() => MovieInput(args)),
				"bizhawk_host_input" => _ui.Invoke(HostInput),
				"bizhawk_userdata_set" => _ui.Invoke(() => UserDataSet(args)),
				"bizhawk_userdata_get" => _ui.Invoke(() => UserDataGet(args)),
				"bizhawk_userdata_clear" => _ui.Invoke(() => UserDataClear(args)),
				_ => throw new JsonRpc.Error(JsonRpc.Error.METHOD_NOT_FOUND, $"unknown tool: {name}"),
			};
		}

		// ── handlers ───────────────────────────────────────────────────────────

		private string GetInfo()
		{
			EnsureEndianness();
			var game = _tool.Emulation!.GetGameInfo();
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["rom_name"] = game?.Name,
				["rom_hash"] = game?.Hash,
				["system_id"] = _tool.Emulation!.GetSystemId(),
				["framecount"] = _tool.Emulation!.FrameCount(),
				["paused"] = _tool.EmuClient!.IsPaused(),
				["endianness"] = EffectiveEndianness(),
				["memory_domain"] = _tool.Memory!.GetCurrentMemoryDomain(),
				["memory_domain_size"] = _tool.Memory!.GetCurrentMemoryDomainSize(),
				["server"] = _tool.ServerUrl,
			});
		}

		private string ReadMemory(JsonElement? args)
		{
			EnsureEndianness();
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
			EnsureEndianness();
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

		private string ListMemoryDomains()
		{
			var mem = _tool.Memory!;
			var domains = new Dictionary<string, object?>();
			foreach (var name in mem.GetMemoryDomainList()) domains[name] = mem.GetMemoryDomainSize(name);
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["domains"] = domains,
				["current"] = mem.GetCurrentMemoryDomain(),
			});
		}

		private string SearchMemory(JsonElement? args)
		{
			EnsureEndianness();
			var a = Required(args);
			ulong value = RequireULong(a, "value");
			int width = RequireInt(a, "width", 8);
			string? domain = OptionalString(a, "domain");
			int maxResults = RequireInt(a, "max_results", 256);
			var mem = _tool.Memory!;

			ulong max = width switch
			{
				8 => 0xFFUL,
				16 => 0xFFFFUL,
				32 => 0xFFFFFFFFUL,
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
			};
			if (value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
			if (maxResults is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "max_results must be 1..4096");

			var matches = new List<object>();

			// restricted scan over a caller-provided address list
			if (a.TryGetProperty("addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
			{
				int i = 0;
				foreach (var el in addrs.EnumerateArray())
				{
					if (i++ >= 4096) break;
					if (matches.Count >= maxResults) break;
					long addr = el.GetInt64();
					if (ReadLe(mem, addr, width, domain) == value)
						matches.Add(new Dictionary<string, object?> { ["address"] = addr, ["value"] = value });
				}
			}
			else
			{
				long rangeStart = a.TryGetProperty("range_start", out var rs) && rs.ValueKind == JsonValueKind.Number ? rs.GetInt64() : 0;
				long rangeLen = a.TryGetProperty("range_length", out var rl) && rl.ValueKind == JsonValueKind.Number ? rl.GetInt64() : mem.GetMemoryDomainSize(domain);
				if (rangeStart < 0 || rangeLen < 1) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range_start/range_length must be positive");
				if (rangeLen > 16 * 1024 * 1024) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range_length too large (max 16 MiB)");

				var bytes = mem.ReadByteRange(rangeStart, (int)rangeLen, domain);
				int bytesPer = width / 8;
				for (int off = 0; off < bytes.Count && matches.Count < maxResults; off++)
				{
					if (off + bytesPer > bytes.Count) break;
					ulong v = LeValue(bytes, off, bytesPer);
					if (v == value)
						matches.Add(new Dictionary<string, object?> { ["address"] = rangeStart + off, ["value"] = v });
				}
			}

			return JsonRpc.Pretty(new Dictionary<string, object?> { ["count"] = matches.Count, ["matches"] = matches });
		}

		private static ulong ReadLe(IMemoryApi mem, long addr, int width, string? domain) =>
			LeValue(mem.ReadByteRange(addr, width / 8, domain), 0, width / 8);

		private static ulong LeValue(IReadOnlyList<byte> bytes, int off, int bytesPer)
		{
			ulong v = 0;
			for (int i = 0; i < bytesPer; i++) v |= (ulong)bytes[off + i] << (8 * i);
			return v;
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
			_bigEndianOverride = enabled;
			_tool.Memory!.SetBigEndian(enabled);
			return $"big-endian = {enabled}";
		}

		private string HashRegion(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int length = RequireInt(a, "length", 256);
			string? domain = OptionalString(a, "domain");
			if (length is < 1 or > 1048576) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..1048576");
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["hash"] = _tool.Memory!.HashRegion(address, length, domain) });
		}

		private string ReadSigned(JsonElement? args)
		{
			EnsureEndianness();
			var a = Required(args);
			long address = RequireLong(a, "address");
			int width = RequireInt(a, "width", 8);
			string? domain = OptionalString(a, "domain");
			long value = width switch
			{
				8 => _tool.Memory!.ReadS8(address, domain),
				16 => _tool.Memory!.ReadS16(address, domain),
				24 => _tool.Memory!.ReadS24(address, domain),
				32 => _tool.Memory!.ReadS32(address, domain),
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16, 24 or 32"),
			};
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["value"] = value });
		}

		private string WriteSigned(JsonElement? args)
		{
			EnsureEndianness();
			var a = Required(args);
			long address = RequireLong(a, "address");
			int width = RequireInt(a, "width", 8);
			long value = RequireLong(a, "value");
			string? domain = OptionalString(a, "domain");
			long min = width switch
			{
				8 => -0x80L,
				16 => -0x8000L,
				24 => -0x800000L,
				32 => -0x80000000L,
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16, 24 or 32"),
			};
			long max = width switch { 8 => 0x7F, 16 => 0x7FFF, 24 => 0x7FFFFF, _ => 0x7FFFFFFF };
			if (value < min || value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
			switch (width)
			{
				case 8: _tool.Memory!.WriteS8(address, (int)value, domain); break;
				case 16: _tool.Memory!.WriteS16(address, (int)value, domain); break;
				case 24: _tool.Memory!.WriteS24(address, (int)value, domain); break;
				case 32: _tool.Memory!.WriteS32(address, (int)value, domain); break;
			}
			return "ok";
		}

		private string ReadFloat(JsonElement? args)
		{
			EnsureEndianness();
			var a = Required(args);
			long address = RequireLong(a, "address");
			string? domain = OptionalString(a, "domain");
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["value"] = _tool.Memory!.ReadFloat(address, domain) });
		}

		private string WriteFloat(JsonElement? args)
		{
			EnsureEndianness();
			var a = Required(args);
			long address = RequireLong(a, "address");
			if (!a.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "missing number param: value");
			string? domain = OptionalString(a, "domain");
			_tool.Memory!.WriteFloat(address, v.GetSingle(), domain);
			return "ok";
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
			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();
			for (var i = 0; i < count; i++)
			{
				_tool.EmuClient!.DoFrameAdvance();
				System.Windows.Forms.Application.DoEvents();
			}
			if (wasPaused) _tool.EmuClient!.Pause();
			return wasPaused ? $"advanced {count} frame(s) (was paused; pause restored)" : $"advanced {count} frame(s)";
		}

		private string PauseTool()
		{
			_tool.EmuClient!.Pause();
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["paused"] = _tool.EmuClient!.IsPaused() });
		}

		private string UnpauseTool()
		{
			_tool.EmuClient!.Unpause();
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["paused"] = _tool.EmuClient!.IsPaused() });
		}

		private string TogglePauseTool()
		{
			_tool.EmuClient!.TogglePause();
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["paused"] = _tool.EmuClient!.IsPaused() });
		}

		private string SpeedMode(JsonElement? args)
		{
			var a = Required(args);
			int percent = RequireInt(a, "percent", 100);
			if (percent is < 1 or > 6400) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "percent must be 1..6400");
			_tool.EmuClient!.SpeedMode(percent);
			return $"speed mode set to {percent}%";
		}

		private string GetJoypad(JsonElement? args)
		{
			int? controller = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.Number)
				controller = c.GetInt32();
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["buttons"] = _tool.Joypad!.Get(controller) });
		}

		private string GetRegisters()
		{
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["registers"] = _tool.Emulation!.GetRegisters() });
		}

		private string SetRegister(JsonElement? args)
		{
			var a = Required(args);
			string register = RequireString(a, "register");
			int value = RequireInt(a, "value", 0);
			_tool.Emulation!.SetRegister(register, value);
			return $"register {register} set to {value}";
		}

		private string Disassemble(JsonElement? args)
		{
			var a = Required(args);
			uint pc = (uint)RequireLong(a, "pc");
			string? name = OptionalString(a, "name");
			var (disasm, _) = _tool.Emulation!.Disassemble(pc, name);
			if (string.IsNullOrEmpty(disasm)) throw new JsonRpc.Error(JsonRpc.Error.INTERNAL_ERROR, $"failed to disassemble at {pc}");
			return disasm;
		}

		private string LagCount()
		{
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["is_lagged"] = _tool.Emulation!.IsLagged(),
				["lag_count"] = _tool.Emulation!.LagCount(),
			});
		}

		private string Screenshot(JsonElement? args)
		{
			string? path = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object) path = OptionalString(a, "path");
			if (string.IsNullOrEmpty(path))
			{
				var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp");
				System.IO.Directory.CreateDirectory(dir);
				path = System.IO.Path.Combine(dir, $"shot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
			}

			_tool.EmuClient!.Screenshot(path);
			string uri = RegisterArtifact(path, "image/png", $"screenshot {System.IO.Path.GetFileName(path)}");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["path"] = path,
				["resource"] = uri,
			});
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

		private string OverlayText(JsonElement? args)
		{
			var a = Required(args);
			int x = RequireInt(a, "x", 0);
			int y = RequireInt(a, "y", 0);
			string text = RequireString(a, "text");
			var color = ParseColor(a);
			int? fontSize = a.TryGetProperty("fontsize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt32() : null;
			_tool.Gui!.DrawString(x, y, text, color, null, fontSize, null, null, "Left", "Top");
			return "ok";
		}

		private string ClearOverlay()
		{
			_tool.Gui!.ClearText();
			return "overlay cleared";
		}

		private string OsdMessage(JsonElement? args)
		{
			var a = Required(args);
			string message = RequireString(a, "message");
			int? duration = a.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt32() : null;
			_tool.Gui!.AddMessage(message, duration);
			return "message shown";
		}

		private string MovieInfo()
		{
			var movie = _tool.Movie!;
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["loaded"] = movie.IsLoaded(),
				["filename"] = movie.Filename(),
				["mode"] = movie.Mode(),
				["length"] = movie.Length(),
				["rerecords"] = movie.GetRerecordCount(),
				["read_only"] = movie.GetReadOnly(),
				["fps"] = movie.GetFps(),
				["header"] = movie.GetHeader(),
			});
		}

		private string MovieInput(JsonElement? args)
		{
			var a = Required(args);
			int frame = RequireInt(a, "frame", 0);
			return _tool.Movie!.GetInputAsMnemonic(frame);
		}

		private string HostInput()
		{
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["pressed"] = _tool.Input!.GetPressedButtons(),
				["mouse"] = _tool.Input!.GetMouse(),
			});
		}

		private string UserDataSet(JsonElement? args)
		{
			var a = Required(args);
			string key = RequireString(a, "key");
			string value = RequireString(a, "value");
			_tool.UserData!.Set(key, value);
			return $"stored {key}";
		}

		private string UserDataGet(JsonElement? args)
		{
			var a = Required(args);
			string key = RequireString(a, "key");
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["value"] = _tool.UserData!.Get(key) });
		}

		private string UserDataClear(JsonElement? args)
		{
			if (args is { } a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
			{
				bool removed = _tool.UserData!.Remove(k.GetString()!);
				return removed ? $"removed {k.GetString()}" : $"key not found: {k.GetString()}";
			}
			_tool.UserData!.Clear();
			return "user data cleared";
		}

		private string Shutdown()
		{
			_ui.Invoke(_tool.StopServer);
			return "server stopping";
		}

		// ── param helpers ──────────────────────────────────────────────────────

		// Applies the core-appropriate endianness default once per loaded system
		// (SetBigEndian has no getter, so we keep our own state). Explicit
		// bizhawk_set_big_endian calls take precedence and stick.
		private void EnsureEndianness()
		{
			if (_bigEndianOverride != null) return;
			var sys = _tool.Emulation!.GetSystemId();
			if (_lastSystemId == sys) return;
			_lastSystemId = sys;
			_tool.Memory!.SetBigEndian(SystemIsBigEndian(sys));
		}

		private string EffectiveEndianness()
		{
			if (_bigEndianOverride is { } o) return o ? "big" : "little";
			return SystemIsBigEndian(_tool.Emulation!.GetSystemId()) ? "big" : "little";
		}

		private static bool SystemIsBigEndian(string systemId)
		{
			switch (systemId)
			{
				case "GEN":      // Genesis / Mega Drive (68K)
				case "SMD":
				case "32X":
				case "SNES":
				case "SNESBG":   // Super Game Boy
				case "N64":
				case "SAT":      // Saturn
					return true;
				default:
					return false; // GB/GBA/NES/PCE/PSX/... are little-endian
			}
		}

		// ── MCP resources (artifacts the server can serve back as base64) ─────

		private readonly List<Artifact> _artifacts = new();

		private sealed class Artifact
		{
			public string Uri = "";
			public string Path = "";
			public string Mime = "";
			public string Name = "";
		}

		private string RegisterArtifact(string path, string mime, string name)
		{
			var uri = $"bizhawk://{Guid.NewGuid():N}";
			_artifacts.Add(new Artifact { Uri = uri, Path = path, Mime = mime, Name = name });
			return uri;
		}

		public Dictionary<string, object?> ListResources()
		{
			var resources = new List<object?>();
			foreach (var a in _artifacts)
			{
				long size = 0;
				try { size = new System.IO.FileInfo(a.Path).Length; }
				catch { /* file gone — still list the URI */ }
				resources.Add(new Dictionary<string, object?>
				{
					["uri"] = a.Uri,
					["name"] = a.Name,
					["mimeType"] = a.Mime,
					["size"] = size,
				});
			}
			return new Dictionary<string, object?> { ["resources"] = resources };
		}

		public Dictionary<string, object?> ReadResource(string uri)
		{
			var artifact = _artifacts.Find(a => a.Uri == uri);
			if (artifact == null) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown resource: {uri}");
			byte[] bytes;
			try
			{
				bytes = System.IO.File.ReadAllBytes(artifact.Path);
			}
			catch (Exception e)
			{
				throw new JsonRpc.Error(JsonRpc.Error.INTERNAL_ERROR, $"cannot read resource: {e.Message}");
			}
			return new Dictionary<string, object?>
			{
				["contents"] = new List<object?>
				{
					new Dictionary<string, object?> { ["uri"] = uri, ["mimeType"] = artifact.Mime, ["blob"] = Convert.ToBase64String(bytes) },
				},
			};
		}

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

		private static System.Drawing.Color? ParseColor(JsonElement a)
		{
			if (!a.TryGetProperty("color", out var v) || v.ValueKind != JsonValueKind.String) return null;
			try { return System.Drawing.ColorTranslator.FromHtml(v.GetString()!); }
			catch { throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"invalid color: {v.GetString()}"); }
		}

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
