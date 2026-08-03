using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;

using BizHawk.Client.Common;
using BizHawk.Emulation.Common;

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
			Tool("bizhawk_read_memory", "Read u8/u16/u32 from a memory domain. Endianness follows the core default (big-endian on Genesis/SNES/N64) unless bizhawk_set_big_endian overrode it. Bus domains accept 32-bit disassembly addresses (e.g. 0xFFFFF832): the 68K's 24-bit bus masks them, so 0xFFFFF832 == 0xFFF832. Either \"address\" or a symbol \"name\" (from bizhawk_symbols_set) is required.", [
				Param("address", "integer", "Offset in the domain, 0-based. For bus domains (e.g. M68K BUS) use the raw bus address (e.g. 0xFFFBCA); 32-bit forms (0xFFFFFBCA) are masked like the hardware."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to BizHawk's current one)."),
			]),
			Tool("bizhawk_write_memory", "Write u8/u16/u32 to a memory domain. Endianness follows the core default unless bizhawk_set_big_endian overrode it. Bus domains mask 32-bit addresses as in read. Either \"address\" or a symbol \"name\" is required.", [
				Param("address", "integer", "Offset in the domain, 0-based. For bus domains use the raw bus address."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("value", "integer", "Value to write (must fit the width)."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_range", "Read a contiguous range (up to 4096 bytes) and return it as hex.", [
				Param("address", "integer", "Start offset."),
				Param("length", "integer", "Bytes to read, 1..4096.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_list_memory_domains", "List all memory domains with sizes (JSON). Each entry reports \"size\" and, when known, \"bus_base\" (the domain's location in the raw bus space, e.g. 68K RAM = 0xFF0000 on Genesis). Offsets are domain-relative: RAM offset 0xFBC8 = bus 0xFFFBC8; bus domains take raw bus addresses.", []),
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
			Tool("bizhawk_read_many", "Read several addresses in one call (up to 256). Returns JSON: [{address, width, value, domain}]. Endianness as bizhawk_read_memory. Set \"consistent\": true to pause during the batch so all reads come from the same frame.", [
				Param("items", "array", "Array of {\"address\": int | \"name\": string, \"width\"?: 8|16|32, \"domain\"?: string}."),
				Param("consistent", "boolean", "Pause emulation for the duration of the batch so reads are frame-consistent.", false),
			]),
			Tool("bizhawk_write_range", "Write a contiguous byte range from a values array (up to 4096 bytes).", [
				Param("address", "integer", "Start offset in the domain, 0-based."),
				Param("values", "array", "Byte values (0..255) to write in order."),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_write_many", "Write several values in one call (up to 256; non-contiguous). Each item accepts \"address\" or symbol \"name\", width and value.", [
				Param("items", "array", "Array of {\"address\": int | \"name\": string, \"width\"?: 8|16|32, \"value\": int, \"domain\"?: string}."),
			]),
			Tool("bizhawk_dump_memory", "Dump an entire memory domain to a host-side file (also exposed as a bizhawk:// resource). Omit \"path\" to save into the host temp dir (bizhawk-mcp).", [
				Param("domain", "string", "Domain name to dump (defaults to current)."),
				Param("path", "string", "Optional absolute path writable by EmuHawk, e.g. C:/temp/ram.bin."),
			]),
			Tool("bizhawk_ram_snapshot", "Capture the full contents of a memory domain as a snapshot for later diffing (bizhawk_ram_diff). One snapshot per domain is kept.", [
				Param("domain", "string", "Domain name (defaults to current)."),
				Param("label", "string", "Optional label for the snapshot."),
			]),
			Tool("bizhawk_ram_diff", "Compare the current contents of a domain against its snapshot (taken with bizhawk_ram_snapshot) and list changed addresses (JSON).", [
				Param("domain", "string", "Domain name (defaults to current)."),
				Param("max_results", "integer", "Stop after this many changes, 1..4096.", 256),
			]),
			Tool("bizhawk_symbols_set", "Register symbol names for addresses (from Ghidra exports, fixtures, etc.). Symbols can then be used as \"name\" in read_memory/write_memory/read_many instead of raw addresses.", [
				Param("symbols", "array", "Array of {\"name\": string, \"address\": int, \"width\"?: 8|16|32, \"domain\"?: string}."),
			]),
			Tool("bizhawk_symbols_list", "List registered symbols (JSON).", []),
			Tool("bizhawk_symbols_clear", "Remove all registered symbols.", []),
			Tool("bizhawk_read_palette", "Read a core's color palette as hex RGB strings. Genesis: CRAM (64 colors, 16-bit BGR). SNES: CGRAM (256 colors, 16-bit BGR555). Other systems: unsupported.", [
				Param("count", "integer", "Number of colors to read, 1..256.", 64),
				Param("domain", "string", "Optional palette domain (defaults to CRAM on GEN, CGRAM on SNES)."),
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
			Tool("bizhawk_set_register", "Write a CPU register. Use the exact key from bizhawk_get_registers (e.g. \"M68K PC\" on Genesis). Note: some cores (gpgx) do not implement register writes at all — check the response.", [
				Param("register", "string", "Register name, e.g. \"M68K PC\", \"M68K A0\"."),
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
			Tool("bizhawk_overlay_rect", "Draw a rectangle on the video output (hitboxes, regions). Cleared with bizhawk_clear_overlay.", [
				Param("x", "integer", "X position."),
				Param("y", "integer", "Y position."),
				Param("width", "integer", "Width in pixels."),
				Param("height", "integer", "Height in pixels."),
				Param("color", "string", "Optional line color, e.g. \"#FF0000\"."),
				Param("fill", "string", "Optional fill color, e.g. \"#00FF0080\" (ARGB)."),
			]),
			Tool("bizhawk_overlay_line", "Draw a line on the video output. Cleared with bizhawk_clear_overlay.", [
				Param("x1", "integer", "Start X."),
				Param("y1", "integer", "Start Y."),
				Param("x2", "integer", "End X."),
				Param("y2", "integer", "End Y."),
				Param("color", "string", "Optional color, e.g. \"#00FF00\"."),
			]),
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
			Tool("bizhawk_watch_add", "Register a memory watcher (address + width + domain). Values are read with bizhawk_watch_read; the watcher list is session-local.", [
				Param("name", "string", "Watcher name (unique)."),
				Param("address", "integer", "Offset in the domain (see bizhawk_list_memory_domains for conventions)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to current)."),
			]),
			Tool("bizhawk_watch_remove", "Remove a memory watcher by name.", [
				Param("name", "string", "Watcher name."),
			]),
			Tool("bizhawk_watch_list", "List registered watchers with their current values (JSON).", []),
			Tool("bizhawk_watch_read", "Read all watcher values in one call (JSON). Each entry has \"value\" and \"changed\" (true when it differs from the previous read).", []),
			Tool("bizhawk_wait_until", "Advance frames until a memory condition holds (or timeout). Pauses when done. Condition ops: eq, ne, lt, gt, le, ge.", [
				Param("address", "integer", "Offset in the domain."),
				Param("op", "string", "eq | ne | lt | gt | le | ge.", "eq"),
				Param("value", "integer", "Value to compare against."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain."),
				Param("timeout_frames", "integer", "Max frames to advance, 1..600.", 600),
			]),
			Tool("bizhawk_watchpoint_add", "Register a real memory watchpoint (read/write/execute) that fires the moment the core touches the address. GENESIS gpgx core ONLY: requires IDebuggable memory callbacks; other cores return an error. Execute watchpoints need an explicit address. See bizhawk_watchpoint_wait to block until one fires.", [
				Param("name", "string", "Watchpoint name (unique)."),
				Param("type", "string", "read | write | execute.", "write"),
				Param("address", "integer", "Bus address to watch (required for execute; omit for read/write to watch all)."),
				Param("domain", "string", "Optional scope, e.g. \"M68K BUS\" (defaults to the core's first available scope)."),
			]),
			Tool("bizhawk_watchpoint_remove", "Remove a registered memory watchpoint.", [
				Param("name", "string", "Watchpoint name."),
			]),
			Tool("bizhawk_watchpoint_list", "List registered memory watchpoints (JSON).", []),
			Tool("bizhawk_watchpoint_wait", "Advance frames until a registered watchpoint fires (or timeout). Pauses when done. Returns the hit: watchpoint name, type, address and value.", [
				Param("timeout_frames", "integer", "Max frames to advance, 1..600.", 600),
			]),
			Tool("bizhawk_trace", "Advance N frames and sample the CPU each step: frame, PC, and disassembly at PC (JSON).", [
				Param("count", "integer", "Frames to trace, 1..600.", 60),
				Param("step", "integer", "Sample every step frames.", 1),
			]),
		];

		// Serializes all tool calls: EmuHawk's API state (active memory domain,
		// endianness) is global, so two concurrent requests could otherwise
		// clobber each other (e.g. a read racing a use_memory_domain).
		private readonly object _callGate = new();

		public string Call(string name, JsonElement? args)
		{
			lock (_callGate)
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
				"bizhawk_read_many" => _ui.Invoke(() => ReadMany(args)),
				"bizhawk_write_range" => _ui.Invoke(() => WriteRange(args)),
				"bizhawk_write_many" => _ui.Invoke(() => WriteMany(args)),
				"bizhawk_dump_memory" => _ui.Invoke(() => DumpMemory(args)),
				"bizhawk_ram_snapshot" => _ui.Invoke(() => RamSnapshot(args)),
				"bizhawk_ram_diff" => _ui.Invoke(() => RamDiff(args)),
				"bizhawk_symbols_set" => _ui.Invoke(() => SymbolsSet(args)),
				"bizhawk_symbols_list" => _ui.Invoke(SymbolsList),
				"bizhawk_symbols_clear" => _ui.Invoke(SymbolsClear),
				"bizhawk_read_palette" => _ui.Invoke(() => ReadPalette(args)),
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
				"bizhawk_overlay_rect" => _ui.Invoke(() => OverlayRect(args)),
				"bizhawk_overlay_line" => _ui.Invoke(() => OverlayLine(args)),
				"bizhawk_osd_message" => _ui.Invoke(() => OsdMessage(args)),
				"bizhawk_movie_info" => _ui.Invoke(MovieInfo),
				"bizhawk_movie_input" => _ui.Invoke(() => MovieInput(args)),
				"bizhawk_host_input" => _ui.Invoke(HostInput),
				"bizhawk_userdata_set" => _ui.Invoke(() => UserDataSet(args)),
				"bizhawk_userdata_get" => _ui.Invoke(() => UserDataGet(args)),
				"bizhawk_userdata_clear" => _ui.Invoke(() => UserDataClear(args)),
				"bizhawk_watch_add" => _ui.Invoke(() => WatchAdd(args)),
				"bizhawk_watch_remove" => _ui.Invoke(() => WatchRemove(args)),
				"bizhawk_watch_list" => _ui.Invoke(() => WatchList()),
				"bizhawk_watch_read" => _ui.Invoke(() => WatchRead()),
				"bizhawk_wait_until" => _ui.Invoke(() => WaitUntil(args)),
				"bizhawk_watchpoint_add" => _ui.Invoke(() => WatchpointAdd(args)),
				"bizhawk_watchpoint_remove" => _ui.Invoke(() => WatchpointRemove(args)),
				"bizhawk_watchpoint_list" => _ui.Invoke(WatchpointList),
				"bizhawk_watchpoint_wait" => _ui.Invoke(() => WatchpointWait(args)),
				"bizhawk_trace" => _ui.Invoke(() => Trace(args)),
				_ => throw new JsonRpc.Error(JsonRpc.Error.METHOD_NOT_FOUND, $"unknown tool: {name}"),
				};
			}
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
			var (address, width, domain) = ResolveTarget(a);
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
			var (address, width, domain) = ResolveTarget(a);
			ulong value = RequireULong(a, "value");
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

		private string DumpMemory(JsonElement? args)
		{
			string? domain = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object) domain = OptionalString(a, "domain");
			uint size = _tool.Memory!.GetMemoryDomainSize(domain);

			string? path = null;
			if (args is { } b && b.ValueKind == JsonValueKind.Object) path = OptionalString(b, "path");
			if (string.IsNullOrEmpty(path))
			{
				var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp");
				System.IO.Directory.CreateDirectory(dir);
				path = System.IO.Path.Combine(dir, $"dump-{domain ?? _tool.Memory!.GetCurrentMemoryDomain()}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
			}

			// read the whole domain in chunks via ReadByteRange and write to disk
			using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
			{
				const int chunk = 0x10000;
				for (long off = 0; off < size; off += chunk)
				{
					int len = (int)Math.Min(chunk, size - off);
					var bytes = _tool.Memory!.ReadByteRange(off, len, domain);
					var buf = new byte[len];
					for (var i = 0; i < len; i++) buf[i] = bytes[i];
					fs.Write(buf, 0, len);
				}
			}

			string uri = RegisterArtifact(path, "application/octet-stream", $"memory dump {domain ?? _tool.Memory!.GetCurrentMemoryDomain()} ({size} bytes)");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["path"] = path,
				["size"] = size,
				["domain"] = domain ?? _tool.Memory!.GetCurrentMemoryDomain(),
				["resource"] = uri,
			});
		}

		// ── RAM snapshots / diffs ─────────────────────────────────────────────
		// Capture a domain's bytes in memory, then compare later to find what
		// changed (dynamic structures, level layout population, ...).

		private sealed class RamSnapshotData
		{
			public string Domain = "";
			public string? Label;
			public byte[] Bytes = Array.Empty<byte>();
		}

		private readonly Dictionary<string, RamSnapshotData> _ramSnapshots = new(StringComparer.OrdinalIgnoreCase);

		private string RamSnapshot(JsonElement? args)
		{
			string? domain = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object) domain = OptionalString(a, "domain");
			string name = domain ?? _tool.Memory!.GetCurrentMemoryDomain();
			uint size = _tool.Memory!.GetMemoryDomainSize(domain);

			var bytes = new byte[size];
			const int chunk = 0x10000;
			for (long off = 0; off < size; off += chunk)
			{
				int len = (int)Math.Min(chunk, size - off);
				var part = _tool.Memory!.ReadByteRange(off, len, domain);
				for (var i = 0; i < len; i++) bytes[off + i] = part[i];
			}

			string? label = null;
			if (args is { } b && b.ValueKind == JsonValueKind.Object) label = OptionalString(b, "label");
			_ramSnapshots[name] = new RamSnapshotData { Domain = name, Label = label, Bytes = bytes };
			return $"snapshot of {name} captured ({size} bytes)";
		}

		private string RamDiff(JsonElement? args)
		{
			string? domain = null;
			int maxResults = 256;
			if (args is { } a && a.ValueKind == JsonValueKind.Object)
			{
				domain = OptionalString(a, "domain");
				maxResults = RequireInt(a, "max_results", 256);
			}
			if (maxResults is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "max_results must be 1..4096");

			string name = domain ?? _tool.Memory!.GetCurrentMemoryDomain();
			if (!_ramSnapshots.TryGetValue(name, out var snap))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"no snapshot for domain {name}; call bizhawk_ram_snapshot first");

			uint size = _tool.Memory!.GetMemoryDomainSize(domain);
			var changes = new List<object?>();
			var prev = snap.Bytes;
			var cur = new byte[Math.Max(prev.Length, (int)size)];
			const int chunk = 0x10000;
			for (long off = 0; off < size; off += chunk)
			{
				int len = (int)Math.Min(chunk, size - off);
				var part = _tool.Memory!.ReadByteRange(off, len, domain);
				for (var i = 0; i < len; i++) cur[off + i] = part[i];
			}

			int limit = Math.Min(prev.Length, cur.Length);
			for (long off = 0; off < limit && changes.Count < maxResults; off++)
			{
				if (prev[off] == cur[off]) continue;
				// coalesce contiguous runs into one entry
				long start = off;
				while (off < limit && prev[off] != cur[off]) off++;
				long end = off - 1;
				var oldBytes = new byte[end - start + 1];
				var newBytes = new byte[end - start + 1];
				for (long i = start; i <= end; i++)
				{
					oldBytes[i - start] = prev[i];
					newBytes[i - start] = cur[i];
				}
				changes.Add(new Dictionary<string, object?>
				{
					["start"] = start,
					["length"] = end - start + 1,
					["old"] = Hex(oldBytes),
					["new"] = Hex(newBytes),
				});
				off--; // the for-loop increments past the run
			}

			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["domain"] = name,
				["label"] = snap.Label,
				["count"] = changes.Count,
				["changes"] = changes,
			});
		}

		private string ListMemoryDomains()
		{
			var mem = _tool.Memory!;
			var systemId = _tool.Emulation!.GetSystemId();
			var domains = new Dictionary<string, object?>();
			foreach (var name in mem.GetMemoryDomainList())
			{
				var info = new Dictionary<string, object?> { ["size"] = mem.GetMemoryDomainSize(name) };
				if (KnownBusBases.TryGetValue(systemId, out var bases) && bases.TryGetValue(name, out var busBase))
					info["bus_base"] = busBase; // where this domain sits in the raw bus space
				domains[name] = info;
			}
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["domains"] = domains,
				["current"] = mem.GetCurrentMemoryDomain(),
			});
		}

		// Best-effort bus base per system+domain, so agents can convert between
		// RAM offsets and raw bus addresses without guessing. Only entries we
		// are confident about; missing entries just omit the field.
		private static readonly Dictionary<string, Dictionary<string, long>> KnownBusBases = new()
		{
			["GEN"] = new Dictionary<string, long>
			{
				["68K RAM"] = 0xFF0000,
				["Z80 RAM"] = 0xA00000,
				["MD CART"] = 0x000000,
			},
			["SNES"] = new Dictionary<string, long>
			{
				["WRAM"] = 0x7E0000,
			},
			["GB"] = new Dictionary<string, long>
			{
				["WRAM"] = 0xC000,
				["VRAM"] = 0x8000,
				["HRAM"] = 0xFF80,
				["ROM"] = 0x0000,
			},
		};

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

		private string ReadMany(JsonElement? args)
		{
			var a = Required(args);
			if (!a.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must be an array");
			if (items.GetArrayLength() is < 1 or > 256)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must contain 1..256 entries");

			bool consistent = a.TryGetProperty("consistent", out var c) && c.ValueKind == JsonValueKind.True;
			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (consistent && !wasPaused) _tool.EmuClient!.Pause();
			try
			{
				var results = new List<object?>();
				foreach (var item in items.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object)
						throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each item must be an object");
					var (address, width, domain) = ResolveTarget(item);
					ulong value = width switch
					{
						8 => _tool.Memory!.ReadByte(address, domain),
						16 => _tool.Memory!.ReadU16(address, domain),
						_ => _tool.Memory!.ReadU32(address, domain),
					};
					results.Add(new Dictionary<string, object?> { ["address"] = address, ["width"] = width, ["value"] = value, ["domain"] = domain });
				}
				return JsonRpc.Pretty(new Dictionary<string, object?> { ["reads"] = results });
			}
			finally
			{
				if (consistent && !wasPaused) _tool.EmuClient!.Unpause();
			}
		}

		private string WriteRange(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			if (!a.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "values must be an array");
			int len = values.GetArrayLength();
			if (len is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "values must contain 1..4096 bytes");
			string? domain = OptionalString(a, "domain");
			address = ValidateAddress(address, 8, domain);
			var bytes = new byte[len];
			var i = 0;
			foreach (var el in values.EnumerateArray())
			{
				if (el.ValueKind != JsonValueKind.Number) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "values must be numbers");
				long v = el.GetInt64();
				if (v is < 0 or > 0xFF) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {v} does not fit a byte");
				bytes[i++] = (byte)v;
			}
			_tool.Memory!.WriteByteRange(address, bytes, domain);
			return $"wrote {len} byte(s) at {address}";
		}

		private string WriteMany(JsonElement? args)
		{
			var a = Required(args);
			if (!a.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must be an array");
			if (items.GetArrayLength() is < 1 or > 256)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must contain 1..256 entries");

			var written = 0;
			foreach (var item in items.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each item must be an object");
				var (address, width, domain) = ResolveTarget(item);
				ulong value = RequireULong(item, "value");
				ulong max = width switch
				{
					8 => 0xFFUL,
					16 => 0xFFFFUL,
					_ => 0xFFFFFFFFUL,
				};
				if (value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
				switch (width)
				{
					case 8: _tool.Memory!.WriteU8(address, (uint)value, domain); break;
					case 16: _tool.Memory!.WriteU16(address, (uint)value, domain); break;
					case 32: _tool.Memory!.WriteU32(address, (uint)value, domain); break;
				}
				written++;
			}
			return $"wrote {written} value(s)";
		}

		private static int To8Bit(int v, int mask) => (v * 255) / mask;

		private static string Hex(byte[] bytes)
		{
			var sb = new System.Text.StringBuilder(bytes.Length * 2);
			foreach (var b in bytes) sb.Append(b.ToString("X2"));
			return sb.ToString();
		}

		private string ReadPalette(JsonElement? args)
		{
			var a = Required(args);
			int count = RequireInt(a, "count", 64);
			if (count is < 1 or > 256) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "count must be 1..256");
			var sys = _tool.Emulation!.GetSystemId();
			string domain;
			int entryBits;
			bool bigEndian;
			switch (sys)
			{
				case "GEN":
				case "SMD":
					domain = "CRAM";
					entryBits = 3;   // 16-bit BGR, 3 bits per channel (R=0-2, G=5-7, B=10-12)
					bigEndian = true;
					break;
				case "SNES":
				case "SNESBG":
					domain = "CGRAM";
					entryBits = 5;   // 16-bit BGR555, 5 bits per channel
					bigEndian = false;
					break;
				default:
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"palette not supported for system {sys}");
			}

			string? overrideDomain = OptionalString(a, "domain");
			if (overrideDomain != null) domain = overrideDomain;

			// read raw bytes and assemble the 16-bit entry in the palette's own
			// endianness (independent of bizhawk_set_big_endian)
			var colors = new List<string>();
			for (var i = 0; i < count; i++)
			{
				long addr = i * 2;
				ValidateAddress(addr, 16, domain);
				byte lo = (byte)_tool.Memory!.ReadByte(addr, domain);
				byte hi = (byte)_tool.Memory!.ReadByte(addr + 1, domain);
				int entry = bigEndian ? (lo << 8) | hi : (lo | (hi << 8));
				int mask = (1 << entryBits) - 1;
				int r = entry & mask;
				int g = (entry >> 5) & mask;
				int b = (entry >> 10) & mask;
				colors.Add($"#{To8Bit(r, mask):X2}{To8Bit(g, mask):X2}{To8Bit(b, mask):X2}");
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["system"] = sys, ["domain"] = domain, ["colors"] = colors });
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

		// Core register names are prefixed (gpgx: "M68K PC", "M68K A0", ...),
		// so match the bare name either exactly or as a key ending in it.
		private static ulong FindRegister(IReadOnlyDictionary<string, ulong> regs, string name)
		{
			if (regs.TryGetValue(name, out var v)) return v;
			foreach (var kv in regs)
				if (kv.Key.EndsWith(name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
			return 0;
		}

		private string SetRegister(JsonElement? args)
		{
			var a = Required(args);
			string register = RequireString(a, "register");
			int value = RequireInt(a, "value", 0);
			// EmulationApi.SetRegister swallows NotImplementedException (cores
			// like gpgx don't support it), so we can't detect failure here.
			_tool.Emulation!.SetRegister(register, value);
			return $"register {register} set to {value} (may be unsupported by this core)";
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

			_tool.EmuClient!.SetScreenshotOSD(false);
			try
			{
				_tool.EmuClient!.Screenshot(path);
			}
			finally
			{
				_tool.EmuClient!.SetScreenshotOSD(true);
			}
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

		private string OverlayRect(JsonElement? args)
		{
			var a = Required(args);
			int x = RequireInt(a, "x", 0);
			int y = RequireInt(a, "y", 0);
			int width = RequireInt(a, "width", 0);
			int height = RequireInt(a, "height", 0);
			var line = ParseColor(a);
			var fill = ParseColorArg(a, "fill");
			_tool.Gui!.DrawRectangle(x, y, width, height, line, fill);
			return "ok";
		}

		private string OverlayLine(JsonElement? args)
		{
			var a = Required(args);
			int x1 = RequireInt(a, "x1", 0);
			int y1 = RequireInt(a, "y1", 0);
			int x2 = RequireInt(a, "x2", 0);
			int y2 = RequireInt(a, "y2", 0);
			var color = ParseColor(a);
			_tool.Gui!.DrawLine(x1, y1, x2, y2, color);
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
			if (!movie.IsLoaded())
			{
				// nothing loaded: return an empty summary instead of crashing
				// (the underlying IMovieApi members may throw/return null)
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["loaded"] = false,
					["filename"] = null,
					["mode"] = null,
					["length"] = 0,
					["rerecords"] = 0,
					["read_only"] = false,
					["fps"] = 0,
					["header"] = null,
				});
			}

			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["loaded"] = true,
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
			if (!_tool.Movie!.IsLoaded())
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "no movie loaded");
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

		// ── watchers ───────────────────────────────────────────────────────────
		// Session-local memory watchers: register (address/width/domain) and read
		// all in one call. No event hooks (IMemoryEventsApi is not registered),
		// so this is polling-based — read after frame_advance to detect changes.

		private sealed class Watch
		{
			public string Name = "";
			public long Address;
			public int Width;
			public string? Domain;
			public ulong? Last;
		}

		private readonly List<Watch> _watches = new();

		// ── symbols ────────────────────────────────────────────────────────────
		// name → (address, width, domain). Lets agents use names from Ghidra /
		// fixtures in read_memory/write_memory/read_many instead of raw addresses.

		private sealed class Symbol
		{
			public long Address;
			public int Width;
			public string? Domain;
		}

		private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);

		private string SymbolsSet(JsonElement? args)
		{
			var a = Required(args);
			if (!a.TryGetProperty("symbols", out var syms) || syms.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "symbols must be an array");
			if (syms.GetArrayLength() is < 1 or > 4096)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "symbols must contain 1..4096 entries");

			var added = 0;
			foreach (var s in syms.EnumerateArray())
			{
				if (s.ValueKind != JsonValueKind.Object)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each symbol must be an object");
				string name = RequireString(s, "name");
				long address = RequireLong(s, "address");
				int width = RequireInt(s, "width", 8);
				string? domain = OptionalString(s, "domain");
				if (width is not (8 or 16 or 32)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"symbol {name}: width must be 8, 16 or 32");
				_symbols[name] = new Symbol { Address = address, Width = width, Domain = domain };
				added++;
			}
			return $"registered {added} symbol(s)";
		}

		private string SymbolsList()
		{
			var list = new List<object?>();
			foreach (var kv in _symbols)
			{
				var s = kv.Value;
				list.Add(new Dictionary<string, object?>
				{
					["name"] = kv.Key,
					["address"] = s.Address,
					["width"] = s.Width,
					["domain"] = s.Domain,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["symbols"] = list });
		}

		private string SymbolsClear()
		{
			int n = _symbols.Count;
			_symbols.Clear();
			return $"cleared {n} symbol(s)";
		}

		// Resolves a read/write request: either an explicit address (with
		// optional width/domain) or a symbol name. Returns the effective
		// (address, width, domain) triple, masking bus addresses.
		private (long address, int width, string? domain) ResolveTarget(JsonElement a)
		{
			string? name = OptionalString(a, "name");
			if (name != null)
			{
				if (!_symbols.TryGetValue(name, out var s))
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown symbol: {name}");
				int width = RequireInt(a, "width", s.Width);
				if (width is not (8 or 16 or 32)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32");
				string? domain = OptionalString(a, "domain") ?? s.Domain;
				long address = ValidateAddress(s.Address, width, domain);
				return (address, width, domain);
			}

			long addr = RequireLong(a, "address");
			int w = RequireInt(a, "width", 8);
			if (w is not (8 or 16 or 32)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32");
			string? dom = OptionalString(a, "domain");
			addr = ValidateAddress(addr, w, dom);
			return (addr, w, dom);
		}

		private static (int width, string? domain) WatchWidth(JsonElement a)
		{
			int width = RequireInt(a, "width", 8);
			if (width is not (8 or 16 or 32)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32");
			return (width, OptionalString(a, "domain"));
		}

		private string WatchAdd(JsonElement? args)
		{
			var a = Required(args);
			string name = RequireString(a, "name");
			if (_watches.Exists(w => w.Name == name)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"watcher already exists: {name}");
			long address = RequireLong(a, "address");
			var (width, domain) = WatchWidth(a);
			address = ValidateAddress(address, width, domain);
			_watches.Add(new Watch { Name = name, Address = address, Width = width, Domain = domain });
			return $"watcher added: {name} @ {address} (w{width})";
		}

		private string WatchRemove(JsonElement? args)
		{
			var a = Required(args);
			string name = RequireString(a, "name");
			int removed = _watches.RemoveAll(w => w.Name == name);
			return removed > 0 ? $"watcher removed: {name}" : $"watcher not found: {name}";
		}

		private string WatchList()
		{
			var watches = new List<object?>();
			foreach (var w in _watches)
			{
				watches.Add(new Dictionary<string, object?>
				{
					["name"] = w.Name,
					["address"] = w.Address,
					["width"] = w.Width,
					["domain"] = w.Domain,
					["value"] = ReadWatchValue(w),
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["watchers"] = watches });
		}

		private string WatchRead()
		{
			var watches = new List<object?>();
			foreach (var w in _watches)
			{
				ulong value = ReadWatchValue(w);
				bool changed = w.Last != null && w.Last != value;
				w.Last = value;
				watches.Add(new Dictionary<string, object?>
				{
					["name"] = w.Name,
					["value"] = value,
					["changed"] = changed,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["watchers"] = watches });
		}

		private ulong ReadWatchValue(Watch w)
		{
			EnsureEndianness();
			return w.Width switch
			{
				8 => _tool.Memory!.ReadByte(w.Address, w.Domain),
				16 => _tool.Memory!.ReadU16(w.Address, w.Domain),
				_ => _tool.Memory!.ReadU32(w.Address, w.Domain),
			};
		}

		// ── watchpoints (real memory callbacks, gpgx/Genesis only) ────────────
		// Reaches into the core's IDebuggable.MemoryCallbacks via reflection on
		// EmulationApi.DebuggableCore. Only cores exposing memory callbacks
		// (the Genesis gpgx waterbox core does) support this; everything else
		// gets a clear INVALID_PARAMS error. The callback fires on the core's
		// thread, so it only sets volatile flags — the actual reads happen in
		// WatchpointWait on the UI thread.

		private sealed class Wp
		{
			public string Name = "";
			public MemoryCallbackType Type;
			public uint? Address;
			public string Scope = "";
			public IMemoryCallback? Callback;
		}

		private readonly List<Wp> _watchpoints = new();

		private volatile bool _wpFired;
		private volatile uint _wpAddr;
		private volatile uint _wpValue;
		private volatile string _wpName = "";

		private IMemoryCallbackSystem? TryGetMemoryCallbacks()
		{
			var emu = _tool.Emulation;
			if (emu == null) return null;
			var prop = emu.GetType().GetProperty("DebuggableCore", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			var dbg = prop?.GetValue(emu) as IDebuggable;
			return dbg?.MemoryCallbacks;
		}

		private string WatchpointAdd(JsonElement? args)
		{
			var a = Required(args);
			string name = RequireString(a, "name");
			if (_watchpoints.Exists(w => w.Name == name)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"watchpoint already exists: {name}");
			string typeStr = a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "write";
			var type = typeStr switch
			{
				"read" => MemoryCallbackType.Read,
				"write" => MemoryCallbackType.Write,
				"execute" => MemoryCallbackType.Execute,
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "type must be read, write or execute"),
			};
			uint? address = a.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.Number ? (uint)ad.GetInt64() : null;
			string? scope = OptionalString(a, "domain");
			if (type == MemoryCallbackType.Execute && address == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "execute watchpoints require an address");

			var mcs = TryGetMemoryCallbacks();
			if (mcs == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "watchpoints unsupported: this core does not expose memory callbacks (only the Genesis gpgx core does)");
			if (type == MemoryCallbackType.Execute && !mcs.ExecuteCallbacksAvailable)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "execute callbacks not available on this core");

			if (scope == null)
			{
				scope = mcs.AvailableScopes.Length > 0 ? mcs.AvailableScopes[0] : null;
				if (scope == null) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "core exposes no callback scopes");
			}
			if (!mcs.AvailableScopes.Contains(scope))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"scope \"{scope}\" not in available callback scopes ({string.Join(", ", mcs.AvailableScopes)})");

			var cb = new MemoryCallbackImpl
			{
				Name = name,
				Type = type,
				Address = address,
				Scope = scope,
				Callback = (addr, value, flags) =>
				{
					_wpFired = true;
					_wpAddr = addr;
					_wpValue = value;
					_wpName = name;
					return null; // don't override the access
				},
			};
			mcs.Add(cb);
			_watchpoints.Add(new Wp { Name = name, Type = type, Address = address, Scope = scope, Callback = cb });
			return $"watchpoint added: {name} ({typeStr}{(address != null ? $" @ 0x{address:X}" : " (any)")} in {scope})";
		}

		private string WatchpointRemove(JsonElement? args)
		{
			var a = Required(args);
			string name = RequireString(a, "name");
			var wp = _watchpoints.Find(w => w.Name == name);
			if (wp == null) return $"watchpoint not found: {name}";
			TryGetMemoryCallbacks()?.Remove(wp.Callback!.Callback);
			_watchpoints.Remove(wp);
			return $"watchpoint removed: {name}";
		}

		private string WatchpointList()
		{
			var list = new List<object?>();
			foreach (var w in _watchpoints)
			{
				list.Add(new Dictionary<string, object?>
				{
					["name"] = w.Name,
					["type"] = w.Type.ToString().ToLowerInvariant(),
					["address"] = w.Address,
					["scope"] = w.Scope,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["watchpoints"] = list });
		}

		private string WatchpointWait(JsonElement? args)
		{
			int timeout = 600;
			if (args is { } a && a.ValueKind == JsonValueKind.Object) timeout = RequireInt(a, "timeout_frames", 600);
			if (timeout is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "timeout_frames must be 1..600");
			if (_watchpoints.Count == 0) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "no watchpoints registered; add one with bizhawk_watchpoint_add first");

			_wpFired = false;
			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			int frames = 0;
			try
			{
				for (; frames < timeout; frames++)
				{
					_tool.EmuClient!.DoFrameAdvance();
					System.Windows.Forms.Application.DoEvents();
					if (_wpFired) break;
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			bool matched = _wpFired;
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["matched"] = matched,
				["frames"] = matched ? frames + 1 : frames,
				["watchpoint"] = _wpName,
				["type"] = matched ? _watchpoints.Find(w => w.Name == _wpName)?.Type.ToString().ToLowerInvariant() : null,
				["address"] = _wpAddr,
				["value"] = _wpValue,
				["framecount"] = _tool.Emulation!.FrameCount(),
			});
		}

		private sealed class MemoryCallbackImpl : IMemoryCallback
		{
			public MemoryCallbackType Type { get; init; }
			public string Name { get; init; } = "";
			public MemoryCallbackDelegate Callback { get; init; } = (_, _, _) => null;
			public uint? Address { get; init; }
			public uint? AddressMask => null;
			public string Scope { get; init; } = "";
		}

		private string WaitUntil(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			string op = a.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "eq";
			if (op is not ("eq" or "ne" or "lt" or "gt" or "le" or "ge")) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown op: {op}");
			ulong value = RequireULong(a, "value");
			var (width, domain) = WatchWidth(a);
			address = ValidateAddress(address, width, domain);
			int timeout = RequireInt(a, "timeout_frames", 600);
			if (timeout is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "timeout_frames must be 1..600");

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			ulong current = 0;
			int frames = 0;
			try
			{
				for (; frames < timeout; frames++)
				{
					_tool.EmuClient!.DoFrameAdvance();
					System.Windows.Forms.Application.DoEvents();
					EnsureEndianness();
					current = width switch
					{
						8 => _tool.Memory!.ReadByte(address, domain),
						16 => _tool.Memory!.ReadU16(address, domain),
						_ => _tool.Memory!.ReadU32(address, domain),
					};
					if (Compare(op, current, value)) break;
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			bool matched = frames < timeout;
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["matched"] = matched,
				["frames"] = matched ? frames + 1 : frames,
				["value"] = current,
				["framecount"] = _tool.Emulation!.FrameCount(),
			});
		}

		private static bool Compare(string op, ulong current, ulong target) => op switch
		{
			"eq" => current == target,
			"ne" => current != target,
			"lt" => current < target,
			"gt" => current > target,
			"le" => current <= target,
			_ => current >= target,
		};

		private string Trace(JsonElement? args)
		{
			var a = Required(args);
			int count = RequireInt(a, "count", 60);
			int step = RequireInt(a, "step", 1);
			if (count is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "count must be 1..600");
			if (step is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "step must be 1..600");

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			var samples = new List<object?>();
			try
			{
				for (var i = 0; i < count; i++)
				{
					_tool.EmuClient!.DoFrameAdvance();
					System.Windows.Forms.Application.DoEvents();
					if (i % step != 0) continue;
					var regs = _tool.Emulation!.GetRegisters();
					ulong pc = FindRegister(regs, "PC");
					var (disasm, _) = _tool.Emulation!.Disassemble((uint)pc);
					samples.Add(new Dictionary<string, object?>
					{
						["frame"] = _tool.Emulation!.FrameCount(),
						["pc"] = pc,
						["disasm"] = disasm,
						["sp"] = FindRegister(regs, "SP"),
						["sr"] = FindRegister(regs, "SR"),
					});
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			return JsonRpc.Pretty(new Dictionary<string, object?> { ["samples"] = samples });
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

		// Rejects addresses that fall outside the target domain instead of
		// letting the core silently return 0/open-bus data.
		// Normalizes an address for a memory domain and returns the effective
		// address to use. Bus domains replicate the 68K's 24-bit address bus:
		// 32-bit addresses seen in disassembly (e.g. 0xFFFFF832, which games
		// really do use) truncate to the domain size. Linear domains (RAM,
		// VRAM, ...) are offsets and must fit — out-of-range is an error.
		private long ValidateAddress(long address, int width, string? domain)
		{
			uint size = _tool.Memory!.GetMemoryDomainSize(domain);
			string name = domain ?? _tool.Memory.GetCurrentMemoryDomain();
			if (Has24BitBus() && name.IndexOf("BUS", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				// 68K bus (GEN/SMD/32X/SAT): only the low 24 bits of the address
				// exist — the 68K mirrors 32-bit disassembly addresses (e.g.
				// 0xFFFFF832) down onto its 24-bit bus, and games rely on it.
				// Mask like the real hardware decoder instead of rejecting.
				address &= size - 1;
			}
			if (address < 0 || address + (width / 8) > size)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"address {address} (width {width}) outside domain \"{name}\" (size {size})");
			return address;
		}

		// Cores whose main CPU has a 24-bit address bus (68000 family). Other
		// systems (N64 32-bit bus, Z80 16-bit, ...) have different semantics
		// and keep the strict out-of-range check for now.
		private bool Has24BitBus()
		{
			switch (_tool.Emulation!.GetSystemId())
			{
				case "GEN":
				case "SMD":
				case "32X":
				case "SAT":
					return true;
				default:
					return false;
			}
		}

		private static System.Drawing.Color? ParseColor(JsonElement a)
		{
			if (!a.TryGetProperty("color", out var v) || v.ValueKind != JsonValueKind.String) return null;
			try { return System.Drawing.ColorTranslator.FromHtml(v.GetString()!); }
			catch { throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"invalid color: {v.GetString()}"); }
		}

		private static System.Drawing.Color? ParseColorArg(JsonElement a, string arg)
		{
			if (!a.TryGetProperty(arg, out var v) || v.ValueKind != JsonValueKind.String) return null;
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
