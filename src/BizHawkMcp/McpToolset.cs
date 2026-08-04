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

		public McpToolset(IHostApis tool, IUiDispatcher ui) : this(tool, ui, null, null) { }

		internal McpToolset(IHostApis tool, IUiDispatcher ui, Func<CheatCollection?>? cheatListResolver, Func<LuaLibraries?>? luaResolver = null, Func<ushort, Func<ushort, byte>, (string Text, int Size)>? z80Disassembler = null)
		{
			_tool = tool;
			_ui = ui;
			_cheatListResolver = cheatListResolver;
			_luaResolver = luaResolver;
			_z80DisasmOverride = z80Disassembler;
			_lastRomHash = CurrentRomHash();
			LoadPersistedSymbols(_lastRomHash);
		}

		// Symbols persist across EmuHawk restarts via the plugin's user data
		// store, scoped per ROM hash and per namespace:
		//   key "mcp.symbols" = { "<romHash>": { "<namespace>": [ {name,address,width,domain}, ... ] } }
		// Default namespace "default"; agents on the same ROM can partition with
		// explicit namespaces ("ghidra", "fixture", "manual"). Saved on every
		// mutation; reloaded automatically when the ROM changes (get_info).
		private const string SymbolsUserKey = "mcp.symbols";
		private const string DefaultNamespace = "default";
		private string? _lastRomHash;

		private string? CurrentRomHash()
		{
			try { return _tool.Emulation?.GetGameInfo()?.Hash; }
			catch { return null; }
		}

		private void LoadPersistedSymbols(string? romHash)
		{
			_symbols.Clear();
			try
			{
				var raw = _tool.UserData?.Get(SymbolsUserKey) as string;
				if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(romHash)) return;
				using var doc = JsonDocument.Parse(raw!);
				if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
				if (!doc.RootElement.TryGetProperty(romHash!, out var byNs)) return;
				if (byNs.ValueKind != JsonValueKind.Object) return;
				foreach (var nsProp in byNs.EnumerateObject())
				{
					string ns = nsProp.Name;
					if (nsProp.Value.ValueKind != JsonValueKind.Array) continue;
					foreach (var s in nsProp.Value.EnumerateArray())
					{
						string? name = s.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
						if (name == null) continue;
						long address = s.TryGetProperty("address", out var ad) && ad.ValueKind == JsonValueKind.Number ? ad.GetInt64() : 0;
						int width = s.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 8;
						string? domain = s.TryGetProperty("domain", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
						_symbols[name] = new Symbol { Address = address, Width = width, Domain = domain, Namespace = ns };
					}
				}
			}
			catch
			{
				// corrupt/old payload: start empty rather than crash the tool
			}
		}

		// Called from get_info: if the loaded ROM changed, swap to that ROM's
		// symbol set so one session never mixes two games' addresses.
		private void MaybeReloadSymbolsForRom()
		{
			var hash = CurrentRomHash();
			if (hash == _lastRomHash) return;
			_lastRomHash = hash;
			LoadPersistedSymbols(hash);
			_searchPrev.Clear();
		}

		private void SaveSymbols()
		{
			try
			{
				// merge the current ROM+namespace groups into the existing payload
				var root = new Dictionary<string, object?>();
				var raw = _tool.UserData?.Get(SymbolsUserKey) as string;
				if (!string.IsNullOrEmpty(raw))
				{
					try
					{
						using var doc = JsonDocument.Parse(raw!);
						if (doc.RootElement.ValueKind == JsonValueKind.Object)
						{
							foreach (var romProp in doc.RootElement.EnumerateObject())
								root[romProp.Name] = CloneJson(romProp.Value);
						}
					}
					catch { /* keep empty root */ }
				}

				// group in-memory symbols by namespace under the current ROM
				var byNs = new Dictionary<string, List<object?>>();
				foreach (var kv in _symbols)
				{
					var s = kv.Value;
					if (!byNs.TryGetValue(s.Namespace, out var list)) byNs[s.Namespace] = list = new List<object?>();
					list.Add(new Dictionary<string, object?>
					{
						["name"] = kv.Key,
						["address"] = s.Address,
						["width"] = s.Width,
						["domain"] = s.Domain,
					});
				}
				var nsDict = new Dictionary<string, object?>();
				foreach (var kv in byNs) nsDict[kv.Key] = kv.Value;

				string romKey = _lastRomHash ?? "";
				if (string.IsNullOrEmpty(romKey))
				{
					// no ROM loaded yet — write the current state under a fallback key
					root["__no_rom__"] = nsDict;
				}
				else
				{
					root[romKey] = nsDict;
				}

				_tool.UserData?.Set(SymbolsUserKey, JsonRpc.Pretty(root));
			}
			catch
			{
				// persisting is best-effort; the in-memory table still works
			}
		}

		// Deep-ish clone of a JsonElement into plain CLR objects so we can
		// merge persisted payloads back without lossy round-trips.
		private static object? CloneJson(JsonElement el)
		{
			switch (el.ValueKind)
			{
				case JsonValueKind.Object:
					var d = new Dictionary<string, object?>();
					foreach (var p in el.EnumerateObject()) d[p.Name] = CloneJson(p.Value);
					return d;
				case JsonValueKind.Array:
					var l = new List<object?>();
					foreach (var item in el.EnumerateArray()) l.Add(CloneJson(item));
					return l;
				case JsonValueKind.String: return el.GetString();
				case JsonValueKind.Number: return el.TryGetInt64(out var i) ? i : el.GetDouble();
				case JsonValueKind.True: return true;
				case JsonValueKind.False: return false;
				default: return null;
			}
		}

		public IReadOnlyList<Dictionary<string, object?>> ToolSchemas { get; } =
		[
			Tool("bizhawk_ping", "Ping the tool. Returns \"pong\" if the plugin and server are alive.", []),
			Tool("bizhawk_get_info", "ROM info, framecount, pause state, current endianness, active memory domain and host paths (JSON). \"paths\" reports where the emulator runs: install_dir (EmuHawk's folder), working_dir, temp_dir (the bizhawk-mcp dir where screenshot/dump_memory/start_fixture save by default) and the loaded ROM's rom_path/rom_dir — so relative paths can always be resolved against the right base.", []),
			Tool("bizhawk_get_board_info", "Board info: board name, display type (NTSC/PAL), and game options — helps identify the game revision.", []),
			Tool("bizhawk_read_memory", "Read u8/u16/u32 from a memory domain. Optional \"endianness\": \"big\" | \"little\" | \"auto\" (default \"auto\" = the domain's native endianness, e.g. big on 68K RAM/M68K BUS but little on Z80 RAM on Genesis). Returns {\"value\": N, \"endianness\": \"big\"|\"little\"} so the interpretation is never ambiguous. On 68K-family bus domains only (GEN/SMD/32X/SAT) 32-bit disassembly addresses are masked by the 24-bit bus, e.g. 0xFFFFF832 == 0xFFF832; other cores/domains reject out-of-range addresses. Either \"address\" or a symbol \"name\" (from bizhawk_symbols_set) is required.", [
				Param("address", "integer", "Offset in the domain, 0-based. For bus domains (e.g. M68K BUS) use the raw bus address (e.g. 0xFFFBCA); 32-bit forms (0xFFFFFBCA) are masked like the hardware."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to BizHawk's current one)."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
			]),
			Tool("bizhawk_write_memory", "Write u8/u16/u32 to a memory domain. Optional \"endianness\" as bizhawk_read_memory (default \"auto\" = domain native). Bus domains mask 32-bit addresses as in read. Either \"address\" or a symbol \"name\" is required. Set \"freeze\": true to also register the written address as a freeze (re-written every frame by the emulator's cheat engine — see bizhawk_freeze_add).", [
				Param("address", "integer", "Offset in the domain, 0-based. For bus domains use the raw bus address."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("value", "integer", "Value to write (must fit the width)."),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
				Param("freeze", "boolean", "Optional: also freeze the written address with this value.", false),
			]),
			Tool("bizhawk_read_range", "Read a contiguous range (up to 4096 bytes) and return it as hex.", [
				Param("address", "integer", "Start offset."),
				Param("length", "integer", "Bytes to read, 1..4096.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_bulk", "Read a contiguous range as raw base64 in ONE call (up to 64 KiB — 16x the read_range cap). Every tool call has ~15-20ms fixed overhead, so batching wins: 4096 bytes via read_many costs 16 calls, via read_bulk costs 1. Returns {address, length, domain, base64}. For whole-domain dumps use bizhawk_dump_memory or the bizhawk://read/{domain}/{range} resource.", [
				Param("address", "integer", "Start offset in the domain, or use a symbol \"name\" instead."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("length", "integer", "Bytes to read, 1..65536.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_list_memory_domains", "List all memory domains with sizes (JSON). Each entry reports \"size\" and, when known, \"bus_base\" (the domain's location in the raw bus space, e.g. 68K RAM = 0xFF0000 on Genesis). Offsets are domain-relative: RAM offset 0xFBC8 = bus 0xFFFBC8; bus domains take raw bus addresses.", []),
			Tool("bizhawk_use_memory_domain", "Switch the active memory domain.", [
				Param("domain", "string", "Domain name, e.g. \"WRAM\"."),
			]),
			Tool("bizhawk_search_memory", "Scan a memory domain. With \"value\": one-shot match (\"op\" eq default | ne | lt | gt | le | ge against that constant). WITHOUT \"value\", \"op\" compares against the PREVIOUS state of the domain (ne/lt/gt/le/ge/changed/unchanged — the classic RAM-search flow): the first call only takes a baseline snapshot (returns \"baseline\": true, 0 matches), then advance frames and call again to find what changed; pass previous hits in \"addresses\" to narrow down across calls. The reference is updated after every stateful call. Keep width/endianness/domain constant between calls; unsigned comparison. Optional \"endianness\" as bizhawk_read_memory (default \"auto\" = domain native). Returns {\"count\", \"endianness\", \"matches\": [{address, value}], \"op\", \"baseline\"}.", [
				Param("value", "integer", "Value to match (must fit the width; omit for stateful compare ops)."),
				Param("op", "string", "eq | ne | lt | gt | le | ge (vs value, or vs previous state without value) | changed | unchanged (vs previous state).", "eq"),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to BizHawk's current one)."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
				Param("range_start", "integer", "First offset to scan.", 0),
				Param("range_length", "integer", "Bytes to scan (default: whole domain)."),
				Param("max_results", "integer", "Stop after this many matches, 1..4096.", 256),
				Param("addresses", "array", "Optional list of addresses to restrict the scan to (up to 4096)."),
				Param("compact", "boolean", "Return only the matching addresses (no per-match values).", false),
			]),
			Tool("bizhawk_set_big_endian", "Toggle big-endian interpretation for u16/u32 reads/writes.", [
				Param("enabled", "boolean", "True for big-endian.", false),
			]),
			Tool("bizhawk_hash_region", "SHA1 hash of a memory region (useful to detect changes).", [
				Param("address", "integer", "Start offset."),
				Param("length", "integer", "Bytes to hash, 1..1048576.", 256),
				Param("domain", "string", "Optional domain."),
			]),
			Tool("bizhawk_read_signed", "Read s8/s16/s24/s32 from a memory domain. Optional \"endianness\" as bizhawk_read_memory (default \"auto\"). Returns {\"value\", \"endianness\"}.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("width", "integer", "8, 16, 24 or 32.", 8),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
			]),
			Tool("bizhawk_write_signed", "Write s8/s16/s24/s32 to a memory domain. Optional \"endianness\" as bizhawk_read_memory (default \"auto\").", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("width", "integer", "8, 16, 24 or 32.", 8),
				Param("value", "integer", "Value to write (must fit the width)."),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
			]),
			Tool("bizhawk_read_float", "Read a 32-bit float from a memory domain. Optional \"endianness\" as bizhawk_read_memory (default \"auto\"). Returns {\"value\", \"endianness\"}.", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
			]),
			Tool("bizhawk_write_float", "Write a 32-bit float to a memory domain. Optional \"endianness\" as bizhawk_read_memory (default \"auto\").", [
				Param("address", "integer", "Offset in the domain, 0-based."),
				Param("value", "number", "Float value to write."),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
			]),
			Tool("bizhawk_read_many", "Read several addresses in one call (up to 256). Returns {reads: [{index, requested, address, width, value, domain, endianness}], read, failed}. Items that fail (unknown symbol, out-of-range address) are reported per-item as {index, requested, error} without killing the batch. \"requested\" echoes the raw address before 24-bit bus masking, which only happens on 68K-family bus domains (GEN/SMD/32X/SAT — e.g. 0x1002024 → requested 0x1002024, address 0x2024); other cores/domains reject out-of-range addresses. Optional per-item \"endianness\" as bizhawk_read_memory (default \"auto\" = each item's domain). Set \"consistent\": true to pause during the batch so all reads come from the same frame.", [
				Param("items", "array", "Array of {\"address\": int | \"name\": string, \"width\"?: 8|16|32, \"domain\"?: string, \"endianness\"?: \"big\"|\"little\"|\"auto\"}."),
				Param("consistent", "boolean", "Pause emulation for the duration of the batch so reads are frame-consistent.", false),
				Param("compact", "boolean", "Return only the values aligned to the items (null = failed) + failures — ~10x smaller payload.", false),
			]),
			Tool("bizhawk_write_range", "Write a contiguous byte range from a values array (up to 4096 bytes). \"fill\" + \"length\" mode writes the same byte across the range with a tiny payload (use it for large clears — some MCP clients drop requests above ~1-2 KB, so prefer fill or chunk values into <=1024-byte calls). Returns {\"wrote\", \"address\", \"fill\"} in fill mode. Set \"freeze\": true to also register the whole range as a freeze (re-written every frame — see bizhawk_freeze_add).", [
				Param("address", "integer", "Start offset in the domain, 0-based."),
				Param("values", "array", "Byte values (0..255) to write in order."),
				Param("fill", "integer", "Optional: write this byte value across the whole range (use with \"length\"; values must be absent).", null),
				Param("length", "integer", "Optional: bytes to write when using \"fill\", 1..4096.", null),
				Param("domain", "string", "Optional domain."),
				Param("freeze", "boolean", "Optional: also freeze the written range with the written bytes.", false),
			]),
			Tool("bizhawk_write_many", "Write several values in one call (up to 256; non-contiguous). Each item accepts \"address\" or symbol \"name\", width, value and optional \"endianness\" as bizhawk_read_memory (default \"auto\" = each item's domain), and optional \"freeze\": true to also register that address as a freeze. Bad items (unknown symbol, out-of-range address, value too wide) fail only themselves: returns {wrote, failed, failures: [{index, address, reason}]} and valid items still write.", [
				Param("items", "array", "Array of {\"address\": int | \"name\": string, \"width\"?: 8|16|32, \"value\": int, \"domain\"?: string, \"endianness\"?: \"big\"|\"little\"|\"auto\", \"freeze\"?: bool}."),
			]),
			Tool("bizhawk_start_fixture", "Scripted fixture capture: advance N frames (optionally after a \"delay\" to skip title screens) with an input timeline, sampling a set of addresses/symbols each frame, and write the result as CSV to a host-side path (default: temp dir). Replaces the manual capture_fixture.lua flow.", [
				Param("frames", "integer", "Frames to run and sample, 1..600."),
				Param("samples", "array", "Array of {\"address\": int | \"name\": string, \"width\"?: 8|16|32, \"domain\"?: string} to sample each frame."),
				Param("inputs", "array", "Optional input timeline: [{\"frame\": int, \"buttons\": {button: bool}, \"controller\"?: int}]. Applied for the NEXT frame. An empty buttons object at a frame releases that controller's buttons (both modes)."),
				Param("input_mode", "string", "\"hold\" (default): buttons persist until the next timeline entry, so a timeline ending in {Right: true} keeps Right held; \"explicit\": absent timeline frames mean no buttons (each frame gets exactly the timeline's buttons, like per-frame Lua joypad.set).", "hold"),
				Param("delay", "integer", "Frames to advance before sampling starts (skip title screens), 0..600.", 0),
				Param("path", "string", "Optional absolute CSV path writable by EmuHawk (default: temp dir)."),
			]),
			Tool("bizhawk_read_struct", "Read relative-offset fields from a base address or symbol in one frame-consistent pass. Returns {base, domain, fields: [{name, offset, address, value, endianness}]}. Replaces hand-rolled sprObjectOffsets arithmetic.", [
				Param("address", "integer", "Base offset in the domain, or use a symbol \"name\" instead."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("fields", "array", "Array of {\"name\": string, \"offset\": int, \"width\"?: 8|16|32, \"endianness\"?: \"big\"|\"little\"|\"auto\"}."),
				Param("domain", "string", "Optional domain override (defaults to the base's domain or current)."),
			]),
			Tool("bizhawk_dump_memory", "Dump a memory domain (or a sub-range with \"range_start\"/\"range_length\") to a host-side file (also exposed as a bizhawk:// resource; resources/list reports the file's host path so shell-capable agents can read it directly, e.g. /mnt/c/... from WSL). Omit \"path\" to save into the host temp dir (bizhawk-mcp).", [
				Param("domain", "string", "Domain name to dump (defaults to current)."),
				Param("range_start", "integer", "First offset to dump (default 0).", 0),
				Param("range_length", "integer", "Bytes to dump (default: the rest of the domain)."),
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
			Tool("bizhawk_symbols_set", "Register symbol names for addresses (from Ghidra exports, fixtures, etc.). Symbols can then be used as \"name\" in read_memory/write_memory/read_many instead of raw addresses. Scoped per ROM (auto) + optional \"namespace\" (default \"default\"); persists across restarts.", [
				Param("symbols", "array", "Array of {\"name\": string, \"address\": int, \"width\"?: 8|16|32, \"domain\"?: string}."),
				Param("namespace", "string", "Namespace to store under (e.g. \"ghidra\", \"fixture\").", "default"),
			]),
			Tool("bizhawk_symbols_list", "List registered symbols with their namespace (JSON).", []),
			Tool("bizhawk_symbols_clear", "Remove all registered symbols, or just one namespace with \"namespace\".", [
				Param("namespace", "string", "Optional namespace to clear; omit to clear everything."),
			]),
			Tool("bizhawk_read_palette", "Read a core's color palette as hex RGB strings. Genesis: CRAM (64 colors, 16-bit BGR). SNES: CGRAM (256 colors, 16-bit BGR555). Other systems: unsupported.", [
				Param("count", "integer", "Number of colors to read, 1..256.", 64),
				Param("domain", "string", "Optional palette domain (defaults to CRAM on GEN, CGRAM on SNES)."),
			]),
			Tool("bizhawk_genesis_read_plane", "Decode a Genesis background nametable (plane A/B) from VRAM into a PNG (also exposed as a bizhawk:// resource). Genesis gpgx core only; other cores error. Plane base auto-detected from the core's VDP view (Kid Chameleon uses plane A at 0x0000, not the typical 0xC000); override with \"base\". \"columns\"/\"rows\" select the region, \"offset_x\"/\"offset_y\" (tiles) crop to a camera window, \"scale\" zooms. Uses the CRAM palette.", [
				Param("plane", "string", "\"A\" or \"B\".", "A"),
				Param("base", "integer", "VRAM offset of the nametable (default: auto-detect from the core)."),
				Param("columns", "integer", "Tile columns to render, 1..128.", 64),
				Param("rows", "integer", "Tile rows to render, 1..128.", 32),
				Param("offset_x", "integer", "Tile column to start at (camera crop).", 0),
				Param("offset_y", "integer", "Tile row to start at (camera crop).", 0),
				Param("scale", "integer", "Pixel zoom factor, 1..8.", 1),
				Param("path", "string", "Optional absolute PNG path writable by EmuHawk (default: temp dir)."),
			]),
			Tool("bizhawk_genesis_get_vdp_view", "Read the Genesis VDP nametable bases from the core (plane A/B addresses + dimensions in tiles, as the game configures them). Genesis gpgx core only; other cores error. Use it to find where the planes live before genesis_read_plane.", []),
			Tool("bizhawk_genesis_get_z80_registers", "Read the Z80 sound CPU registers from the Genesis core (gpgx reports both CPUs in one register table — this filters the Z80 half). The core names them lowercase: \"Z80 pc\", \"Z80 sp\", \"Z80 af\", \"Z80 hl\", ... Genesis gpgx core only; other cores error.", []),
			Tool("bizhawk_genesis_disassemble_z80", "Disassemble Z80 (sound CPU) code from its bus space: 0x0000-0x1FFF is Z80 RAM (where the 68K uploads the sound driver — the reset vector runs RAM@0x0000), aliased at 0x2000-0x3FFF; 0x4000+ is sound I/O/open bus. \"address\" is a raw Z80 bus address; \"count\" instructions follow sequentially. Uses BizHawk's static Z80ADisassembler (the gpgx core's own disassembler only speaks 68K). On GEN the Z80 bus is synthesized from the Z80 RAM domain (the core has no Z80 BUS domain on Genesis); SMS/GG use the native Z80 BUS domain. Other cores error.", [
				Param("address", "integer", "Z80 bus address to start at (0x0000-0xFFFF)."),
				Param("count", "integer", "Instructions to disassemble, 1..64.", 8),
			]),
			Tool("bizhawk_genesis_trace_z80", "Advance N frames sampling the Z80 sound CPU each step: PC, SP and the disassembled instruction at PC — shows the sound driver's main loop, busy-waits (e.g. polling the 68K handshake port) and where it spends each frame. \"stack_words\": N > 0 also dumps that many 16-bit words from the Z80 stack (SP lives in Z80 RAM at bus 0x0000-0x1FFF, little-endian). Z80 bus reads are synthesized from the Z80 RAM domain on GEN (0x0000-0x3FFF, aliased) or use the native Z80 BUS domain on SMS/GG. Genesis gpgx core only; other cores error.", [
				Param("count", "integer", "Frames to trace, 1..600.", 60),
				Param("step", "integer", "Sample every step frames.", 1),
				Param("stack_words", "integer", "16-bit stack words to dump per sample (0..32; 0 = off).", 0),
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
			Tool("bizhawk_get_sound", "Get whether emulator sound is enabled.", []),
			Tool("bizhawk_set_sound", "Enable or disable emulator sound.", [
				Param("enabled", "boolean", "True to enable sound.", true),
			]),
			Tool("bizhawk_enable_rewind", "Enable or disable the emulator's rewind feature (state history).", [
				Param("enabled", "boolean", "True to enable rewind.", true),
			]),
			Tool("bizhawk_frameskip", "Set the emulator frameskip: how many frames to skip between rendered frames. 0 = render every frame.", [
				Param("count", "integer", "Frames to skip, 0..600.", 0),
			]),
			Tool("bizhawk_limit_framerate", "Enable or disable the emulator's framerate limit (clock throttle). Disabling lets emulation run as fast as the CPU allows.", [
				Param("enabled", "boolean", "True to limit framerate.", true),
			]),
			Tool("bizhawk_open_rom", "Open a ROM file. The path is host-side (Windows path when EmuHawk runs on Windows, e.g. F:/roms/game.md).", [
				Param("path", "string", "Absolute path to a ROM file."),
			]),
			Tool("bizhawk_close_rom", "Close the current ROM (emulator returns to the null-ROM state).", []),
			Tool("bizhawk_reboot", "Reboot the current core (restart the loaded game from power-on).", []),
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
			Tool("bizhawk_screenshot", "Save a PNG of the current frame. Omit \"path\" to save into the host temp dir (bizhawk-mcp). Paths are host-side: when EmuHawk runs on Windows they must be Windows paths (e.g. F:/temp/shot.png). Set \"include_overlays\": true to also compose the overlay/OSD layer (overlay_text/rect/line, OSD messages) into the PNG. Returns the effective absolute path and an MCP resource URI to fetch the image bytes.", [
				Param("path", "string", "Optional absolute path writable by EmuHawk, e.g. C:/temp/snap.png. Defaults to a temp file."),
				Param("include_overlays", "boolean", "Compose the overlay/OSD layer into the PNG (default false = bare core framebuffer).", false),
			]),
			Tool("bizhawk_frame_hash", "SHA1 hash of the current rendered frame (screenshot → hash of the PNG bytes). Identical rendered output produces the same hash (BizHawk's PNG save is deterministic), so this is a cheap screen-change detector: hash once, advance, hash again — equal hashes = same screen, no pixel transfer. Returns {sha1, frame, path, resource} (path/resource to fetch the hashed PNG). Same host-path and overlay semantics as bizhawk_screenshot.", [
				Param("path", "string", "Optional absolute path writable by EmuHawk, e.g. C:/temp/hash.png. Defaults to a temp file."),
				Param("include_overlays", "boolean", "Compose the overlay/OSD layer into the PNG before hashing (default false).", false),
			]),
			Tool("bizhawk_save_state", "Save an emulator state to a file.", [
				Param("path", "string", "Absolute .State path."),
			]),
			Tool("bizhawk_load_state", "Load an emulator state from a file.", [
				Param("path", "string", "Absolute .State path."),
			]),
			Tool("bizhawk_save_slot", "Save an emulator state to a quick-save slot (1..10).", [
				Param("slot", "integer", "Slot number, 1..10.", 1),
			]),
			Tool("bizhawk_load_slot", "Load an emulator state from a quick-save slot (1..10).", [
				Param("slot", "integer", "Slot number, 1..10.", 1),
			]),
			Tool("bizhawk_memstate_save", "Save the CORE's state to an in-memory slot (no disk, no 10-slot limit; session-local, lost on restart). Reaches the core's IStatable service via reflection on the emulator (like watchpoints) — fast save/restore for search/TAS iteration. Note: restores the core state only (CPU + memory), not EmuHawk-side state (framecount/lag count).", [
				Param("slot", "string", "Slot name, any string (e.g. \"pre-jump\")."),
			]),
			Tool("bizhawk_memstate_load", "Restore a core state saved with bizhawk_memstate_save. Reaches the core's IStatable service via reflection (like watchpoints); cores without IStatable get a clear error. See bizhawk_memstate_save for the scope (core state only).", [
				Param("slot", "string", "Slot name previously saved."),
			]),
			Tool("bizhawk_memstate_list", "List in-memory core state slots (names + sizes).", []),
			Tool("bizhawk_freeze_add", "Freeze a memory address or range: the emulator's cheat engine (the same MainForm.CheatList the hex editor's Freeze uses) re-writes the value EVERY frame, even while emulation runs freely — so the game can't change it. Use it to lock a timer (\"freeze time\"), lives/health (repeated death tests), or any value you need stable while analyzing. Without \"value\", the current contents are snapshotted; with \"value\", that value is written every frame. \"length\" > 1 freezes a range as 8-bit entries (\"value\" then fills every byte, 0..255). Width 8/16/32 applies to single-address freezes. Entries are the emulator's real cheats: they appear in the Cheats window and persist on exit.", [
				Param("address", "integer", "Offset in the domain, or use a symbol \"name\" instead."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("note", "string", "Optional label for the freeze entry (shown in freeze_list; usable in freeze_remove)."),
				Param("width", "integer", "8, 16 or 32 (single-address freezes).", 8),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
				Param("value", "integer", "Optional value to write every frame (default: snapshot the current contents)."),
				Param("length", "integer", "Optional: freeze a range of this many bytes (8-bit entries).", 1),
			]),
			Tool("bizhawk_freeze_remove", "Un-freeze entries: by the \"note\" label given at freeze_add, or by address (or symbol \"name\") with optional \"length\" and \"domain\" — removes every entry starting in that range, same domain only.", [
				Param("note", "string", "Label of the freeze to remove (from freeze_add or freeze_list)."),
				Param("address", "integer", "Address to un-freeze, or use a symbol \"name\" instead."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("length", "integer", "Optional: un-freeze the range [address, address+length).", 1),
				Param("domain", "string", "Optional domain (default: current)."),
			]),
			Tool("bizhawk_freeze_list", "List the emulator's current freezes (cheat entries): name, domain, address, width, value, endianness, enabled. Shared with the Cheats window / hex editor freezes.", []),
			Tool("bizhawk_freeze_clear", "Remove ALL freezes/cheats in the emulator's cheat list (including manual entries made in the Cheats window).", []),
			Tool("bizhawk_lua_exec", "Execute a Lua snippet inline in EmuHawk's Lua runtime (the same path the Lua Console's REPL box uses). The memory/gui/emu/... libraries are available (note the BizHawk memory API uses underscore forms: memory.read_u8 / read_u16_be / read_u32_le / write_u8 / write_u16_be / write_u32_le). A Lua syntax/runtime error is returned as {\"executed\": false, \"error\": ...}, not a server error. Returns the expression's values (\"return ...\" is implied, like the REPL).", [
				Param("code", "string", "Lua code to execute, e.g. \"memory.read_u32_be(0xFF2506)\"."),
			]),
			Tool("bizhawk_lua_load", "Load a .lua script file into the emulator's script list and start it (same as loading it in the Lua Console). The script then runs every frame via EmuHawk's own frame events — even while emulation runs freely — with no further tool involvement. If already loaded but disabled, re-starts it. Opens the Lua Console window if it isn't open (it owns the Lua runtime).", [
				Param("path", "string", "Absolute .lua path (host-side, e.g. C:/temp/script.lua)."),
			]),
			Tool("bizhawk_lua_unload", "Stop and remove a loaded Lua script.", [
				Param("path", "string", "Absolute .lua path as given to lua_load."),
			]),
			Tool("bizhawk_lua_enable", "Start (or resume) a loaded Lua script that is currently disabled.", [
				Param("path", "string", "Absolute .lua path."),
			]),
			Tool("bizhawk_lua_disable", "Stop a running Lua script (it stays in the script list, disabled).", [
				Param("path", "string", "Absolute .lua path."),
			]),
			Tool("bizhawk_lua_list", "List the emulator's loaded Lua scripts: path, enabled, paused.", []),
			Tool("bizhawk_lua_docs", "Agent-friendly JSON of the emulator's Lua API documentation — the same chain that generates the tasvideos.org LuaFunctions page ([LuaMethod] attributes via LuaLibraries.Docs), served live from the running build, with examples the wiki omits. Each function: {name, signature (e.g. \"uint memory.read_u8(long addr, [string domain = nil])\"), description, example, deprecated}. Optional \"library\" filters to one (memory, gui, emu, ...).", [
				Param("library", "string", "Optional: only this library (e.g. \"memory\")."),
			]),
			Tool("bizhawk_shutdown", "Stop the MCP server (plugin stays loaded; restart via the form's button or the emulator's Lua/tools menu).", []),
			Tool("bizhawk_overlay_text", "Draw text on the emulator's video output. Overlays ACCUMULATE until bizhawk_clear_overlay (all are re-rendered on every frame advance), so multiple hitboxes/labels can stay on screen at once.", [
				Param("x", "integer", "X position."),
				Param("y", "integer", "Y position."),
				Param("text", "string", "Text to draw."),
				Param("color", "string", "Optional hex color, e.g. \"#FFFFFF\"."),
				Param("fontsize", "integer", "Optional font size in pixels."),
			]),
			Tool("bizhawk_clear_overlay", "Remove all overlays drawn on the video output (graphics + text).", []),
			Tool("bizhawk_overlay_rect", "Draw a rectangle on the video output (hitboxes, regions). Overlays ACCUMULATE until bizhawk_clear_overlay. Accepts a single rect or a list via \"rects\": [{x,y,width,height,color,fill}].", [
				Param("x", "integer", "X position."),
				Param("y", "integer", "Y position."),
				Param("width", "integer", "Width in pixels."),
				Param("height", "integer", "Height in pixels."),
				Param("color", "string", "Optional line color, e.g. \"#FF0000\"."),
				Param("fill", "string", "Optional fill color, e.g. \"#00FF0080\" (ARGB)."),
				Param("rects", "array", "Optional list of rects to draw in one call."),
			]),
			Tool("bizhawk_overlay_line", "Draw a line on the video output. Overlays ACCUMULATE until bizhawk_clear_overlay. Accepts a single line or a list via \"lines\": [{x1,y1,x2,y2,color}].", [
				Param("x1", "integer", "Start X."),
				Param("y1", "integer", "Start Y."),
				Param("x2", "integer", "End X."),
				Param("y2", "integer", "End Y."),
				Param("color", "string", "Optional color, e.g. \"#00FF00\"."),
				Param("lines", "array", "Optional list of lines to draw in one call."),
			]),
			Tool("bizhawk_osd_message", "Show a message in the emulator's OSD (on-screen display).", [
				Param("message", "string", "Text to show."),
				Param("duration", "integer", "Optional duration in ms."),
			]),
			Tool("bizhawk_movie_info", "TAS movie info: loaded, filename, mode, length, rerecords, fps, header.", []),
			Tool("bizhawk_movie_input", "Get the input log of a movie frame as a mnemonic string.", [
				Param("frame", "integer", "Frame number (0-based)."),
			]),
			Tool("bizhawk_movie_start", "Start a TAS movie. With \"path\": load that .bk2 file and play from frame 0. Without: start recording a new movie for the loaded ROM.", [
				Param("path", "string", "Optional .bk2 movie path to load and play (default: new recording)."),
			]),
			Tool("bizhawk_movie_save", "Save the current TAS movie. With \"path\": save to that .bk2 file (default: current movie filename).", [
				Param("path", "string", "Optional .bk2 save path."),
			]),
			Tool("bizhawk_movie_stop", "Stop the current TAS movie (saves changes).", []),
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
			Tool("bizhawk_watch_add", "Register a memory watcher (address + width + domain + optional endianness). Values are read with bizhawk_watch_read; the watcher list is session-local.", [
				Param("name", "string", "Watcher name (unique)."),
				Param("address", "integer", "Offset in the domain (see bizhawk_list_memory_domains for conventions)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain (defaults to current)."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
			]),
			Tool("bizhawk_watch_remove", "Remove a memory watcher by name.", [
				Param("name", "string", "Watcher name."),
			]),
			Tool("bizhawk_watch_list", "List registered watchers with their current values (JSON).", []),
			Tool("bizhawk_watch_read", "Read all watcher values in one call (JSON). Each entry has \"value\" and \"changed\" (true when it differs from the previous read). With \"compact\": true, returns three aligned arrays (names/values/changed) — smaller payload.", [
				Param("compact", "boolean", "Return aligned names/values/changed arrays instead of objects.", false),
			]),
			Tool("bizhawk_wait_until", "Advance frames until a memory condition holds (or timeout). Pauses when done. Single mode: \"address\" (or symbol \"name\") + \"op\" (eq|ne|lt|gt|le|ge) + \"value\", optional width/domain/endianness. Multi mode: pass \"conditions\": [{address|name, op, value, width?, domain?, endianness?}, ...] — advances until ALL conditions hold on the SAME frame (AND), so nested single waits are no longer needed; returns per-condition results. Optional \"endianness\" as bizhawk_read_memory (default \"auto\").", [
				Param("address", "integer", "Offset in the domain, or use a symbol \"name\" instead."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("op", "string", "eq | ne | lt | gt | le | ge.", "eq"),
				Param("value", "integer", "Value to compare against."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
				Param("timeout_frames", "integer", "Max frames to advance, 1..600.", 600),
				Param("conditions", "array", "Optional multi-condition mode: [{address|name, op, value, width?, domain?, endianness?}, ...] — wait until ALL hold on the same frame (1..32)."),
			]),
			Tool("bizhawk_watch_change", "Advance frames until the value at an address changes from its value at call time (or timeout). Pauses when done. Unlike wait_until you don't need to know the target value — this gives \"first change frame\" semantics for finding dynamic structures. Optional \"endianness\" as bizhawk_read_memory (default \"auto\"). Accepts \"address\" or a symbol \"name\" (from bizhawk_symbols_set).", [
				Param("address", "integer", "Offset in the domain, or use a symbol \"name\" instead."),
				Param("name", "string", "Symbol name registered via bizhawk_symbols_set (overrides address/domain)."),
				Param("width", "integer", "8, 16 or 32.", 8),
				Param("domain", "string", "Optional domain."),
				Param("endianness", "string", "\"auto\" (domain default), \"big\" or \"little\".", "auto"),
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
			Tool("bizhawk_watchpoint_wait", "Advance frames until a registered watchpoint fires (or timeout). Pauses when done. On a hit with \"context_bytes\": N > 0, also returns full registers, the PC + disassembled instruction, and N raw bytes around the hit address (context.start/bytes/hit_offset). Returns the hit: watchpoint name, type, address and value.", [
				Param("timeout_frames", "integer", "Max frames to advance, 1..600.", 600),
				Param("context_bytes", "integer", "Bytes of RAM to include around the hit address (0..512; 0 = no context).", 0),
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

		// Persistent overlay list. EmuHawk's Client surface holds ONE drawing
		// (drawing a new shape replaces the previous), so we keep the full list
		// here and re-render everything after every mutation and every
		// frame-advance. Shapes accumulate until bizhawk_clear_overlay.
		private readonly List<System.Action> _overlays = new();

		private void RedrawOverlays()
		{
			if (_overlays.Count == 0) return;
			_tool.Gui!.WithSurface(DisplaySurfaceID.Client, gui => gui.ClearGraphics());
			foreach (var draw in _overlays) draw();
		}

		// Single place for "advance one frame": runs the core frame, pumps the
		// UI, and re-renders any persistent overlays (EmuHawk discards the
		// ApiHawk surface after each rendered frame).
		private void AdvanceFrame()
		{
			_tool.EmuClient!.DoFrameAdvance();
			System.Windows.Forms.Application.DoEvents();
			RedrawOverlays();
		}

		public string Call(string name, JsonElement? args)
		{
			lock (_callGate)
			{
				return name switch
			{
				"bizhawk_ping" => _ui.Invoke(() => "pong"),
				"bizhawk_get_info" => _ui.Invoke(GetInfo),
				"bizhawk_get_board_info" => _ui.Invoke(GetBoardInfo),
				"bizhawk_read_memory" => _ui.Invoke(() => ReadMemory(args)),
				"bizhawk_write_memory" => _ui.Invoke(() => WriteMemory(args)),
				"bizhawk_read_range" => _ui.Invoke(() => ReadRange(args)),
				"bizhawk_read_bulk" => _ui.Invoke(() => ReadBulk(args)),
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
				"bizhawk_start_fixture" => _ui.Invoke(() => StartFixture(args)),
				"bizhawk_read_struct" => _ui.Invoke(() => ReadStruct(args)),
				"bizhawk_dump_memory" => _ui.Invoke(() => DumpMemory(args)),
				"bizhawk_ram_snapshot" => _ui.Invoke(() => RamSnapshot(args)),
				"bizhawk_ram_diff" => _ui.Invoke(() => RamDiff(args)),
				"bizhawk_symbols_set" => _ui.Invoke(() => SymbolsSet(args)),
				"bizhawk_symbols_list" => _ui.Invoke(SymbolsList),
				"bizhawk_symbols_clear" => _ui.Invoke(() => SymbolsClear(args)),
				"bizhawk_read_palette" => _ui.Invoke(() => ReadPalette(args)),
				"bizhawk_genesis_read_plane" => _ui.Invoke(() => ReadPlane(args)),
				"bizhawk_genesis_get_vdp_view" => _ui.Invoke(GetVdpView),
				"bizhawk_genesis_get_z80_registers" => _ui.Invoke(GenesisGetZ80Registers),
				"bizhawk_genesis_disassemble_z80" => _ui.Invoke(() => Z80DisassembleTool(args)),
				"bizhawk_genesis_trace_z80" => _ui.Invoke(() => Z80Trace(args)),
				"bizhawk_press_buttons" => _ui.Invoke(() => PressButtons(args)),
				"bizhawk_frame_advance" => _ui.Invoke(() => FrameAdvance(args)),
				"bizhawk_pause" => _ui.Invoke(() => PauseTool()),
				"bizhawk_unpause" => _ui.Invoke(() => UnpauseTool()),
				"bizhawk_toggle_pause" => _ui.Invoke(() => TogglePauseTool()),
				"bizhawk_speed_mode" => _ui.Invoke(() => SpeedMode(args)),
				"bizhawk_get_sound" => _ui.Invoke(GetSound),
				"bizhawk_set_sound" => _ui.Invoke(() => SetSound(args)),
				"bizhawk_enable_rewind" => _ui.Invoke(() => EnableRewind(args)),
				"bizhawk_frameskip" => _ui.Invoke(() => FrameSkipTool(args)),
				"bizhawk_limit_framerate" => _ui.Invoke(() => LimitFramerate(args)),
				"bizhawk_open_rom" => _ui.Invoke(() => OpenRom(args)),
				"bizhawk_close_rom" => _ui.Invoke(CloseRom),
				"bizhawk_reboot" => _ui.Invoke(Reboot),
				"bizhawk_get_joypad" => _ui.Invoke(() => GetJoypad(args)),
				"bizhawk_get_registers" => _ui.Invoke(GetRegisters),
				"bizhawk_set_register" => _ui.Invoke(() => SetRegister(args)),
				"bizhawk_disassemble" => _ui.Invoke(() => Disassemble(args)),
				"bizhawk_lag_count" => _ui.Invoke(LagCount),
				"bizhawk_screenshot" => _ui.Invoke(() => Screenshot(args)),
				"bizhawk_frame_hash" => _ui.Invoke(() => FrameHash(args)),
				"bizhawk_save_state" => _ui.Invoke(() => SaveState(args)),
				"bizhawk_load_state" => _ui.Invoke(() => LoadState(args)),
				"bizhawk_save_slot" => _ui.Invoke(() => SaveSlot(args)),
				"bizhawk_load_slot" => _ui.Invoke(() => LoadSlot(args)),
				"bizhawk_memstate_save" => _ui.Invoke(() => MemStateSave(args)),
				"bizhawk_memstate_load" => _ui.Invoke(() => MemStateLoad(args)),
				"bizhawk_memstate_list" => _ui.Invoke(MemStateList),
				"bizhawk_freeze_add" => _ui.Invoke(() => FreezeAdd(args)),
				"bizhawk_freeze_remove" => _ui.Invoke(() => FreezeRemove(args)),
				"bizhawk_freeze_list" => _ui.Invoke(FreezeList),
				"bizhawk_freeze_clear" => _ui.Invoke(FreezeClear),
				"bizhawk_lua_exec" => _ui.Invoke(() => LuaExec(args)),
				"bizhawk_lua_load" => _ui.Invoke(() => LuaLoad(args)),
				"bizhawk_lua_unload" => _ui.Invoke(() => LuaUnload(args)),
				"bizhawk_lua_enable" => _ui.Invoke(() => LuaEnable(args)),
				"bizhawk_lua_disable" => _ui.Invoke(() => LuaDisable(args)),
				"bizhawk_lua_list" => _ui.Invoke(LuaList),
				"bizhawk_lua_docs" => _ui.Invoke(() => LuaDocs(args)),
				"bizhawk_shutdown" => Shutdown(),
				"bizhawk_overlay_text" => _ui.Invoke(() => OverlayText(args)),
				"bizhawk_clear_overlay" => _ui.Invoke(() => ClearOverlay()),
				"bizhawk_overlay_rect" => _ui.Invoke(() => OverlayRect(args)),
				"bizhawk_overlay_line" => _ui.Invoke(() => OverlayLine(args)),
				"bizhawk_osd_message" => _ui.Invoke(() => OsdMessage(args)),
				"bizhawk_movie_info" => _ui.Invoke(MovieInfo),
				"bizhawk_movie_input" => _ui.Invoke(() => MovieInput(args)),
				"bizhawk_movie_start" => _ui.Invoke(() => MovieStart(args)),
				"bizhawk_movie_save" => _ui.Invoke(() => MovieSave(args)),
				"bizhawk_movie_stop" => _ui.Invoke(() => MovieStop(args)),
				"bizhawk_host_input" => _ui.Invoke(HostInput),
				"bizhawk_userdata_set" => _ui.Invoke(() => UserDataSet(args)),
				"bizhawk_userdata_get" => _ui.Invoke(() => UserDataGet(args)),
				"bizhawk_userdata_clear" => _ui.Invoke(() => UserDataClear(args)),
				"bizhawk_watch_add" => _ui.Invoke(() => WatchAdd(args)),
				"bizhawk_watch_remove" => _ui.Invoke(() => WatchRemove(args)),
				"bizhawk_watch_list" => _ui.Invoke(() => WatchList()),
				"bizhawk_watch_read" => _ui.Invoke(() => WatchRead(args)),
				"bizhawk_wait_until" => _ui.Invoke(() => WaitUntil(args)),
				"bizhawk_watch_change" => _ui.Invoke(() => WatchChange(args)),
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
			MaybeReloadSymbolsForRom();
			var game = _tool.Emulation!.GetGameInfo();
			string curDomain = _tool.Memory!.GetCurrentMemoryDomain();
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["rom_name"] = game?.Name,
				["rom_hash"] = game?.Hash,
				["system_id"] = _tool.Emulation!.GetSystemId(),
				["framecount"] = _tool.Emulation!.FrameCount(),
				["paused"] = _tool.EmuClient!.IsPaused(),
				["endianness"] = EndianName(ResolveBigEndian(null, curDomain)),
				["memory_domain"] = curDomain,
				["memory_domain_size"] = _tool.Memory!.GetCurrentMemoryDomainSize(),
				["paths"] = GetPaths(),
				["server"] = _tool.ServerUrl,
			});
		}

		// Where the emulator runs and where host-side files land by default,
		// so agents can resolve relative paths against a known base. All
		// paths are on the host that EmuHawk runs on (Windows when the agent
		// sees C:\..., Linux/Mono otherwise).
		private Dictionary<string, object?> GetPaths()
		{
			string? romPath = ResolveCurrentRomPath(_tool);
			return new Dictionary<string, object?>
			{
				["install_dir"] = AppDomain.CurrentDomain.BaseDirectory,
				["working_dir"] = Environment.CurrentDirectory,
				["temp_dir"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp"),
				["rom_path"] = romPath,
				["rom_dir"] = string.IsNullOrEmpty(romPath) ? null : System.IO.Path.GetDirectoryName(romPath),
				["host_is_windows"] = IsWindowsHost(),
			};
		}

		// MainForm.CurrentlyOpenRom (the loaded ROM's path), via the same
		// reflection route as ResolveCheatList: the plugin form's Owner is the
		// MainForm, with a GlobalWin.MainForm fallback. get_info must never
		// throw when the EmuHawk assembly is unreachable — returns null.
		private static string? ResolveCurrentRomPath(object? tool)
		{
			try
			{
				object? mf = tool?.GetType().GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance)?.GetValue(tool);
				if (mf == null)
				{
					var gwType = Type.GetType("BizHawk.Client.EmuHawk.GlobalWin, BizHawk.Client.EmuHawk");
					mf = gwType?.GetProperty("MainForm", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
				}
				return mf?.GetType().GetProperty("CurrentlyOpenRom", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mf) as string;
			}
			catch
			{
				return null;
			}
		}

		private string GetBoardInfo()
		{
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["board_name"] = _tool.Emulation!.GetBoardName(),
				["display_type"] = _tool.Emulation!.GetDisplayType(),
				["game_options"] = _tool.Emulation!.GetGameOptions(),
			});
		}

		private string ReadMemory(JsonElement? args)
		{
			var a = Required(args);
			long? requested = null;
			if (a.TryGetProperty("address", out var ra) && ra.ValueKind == JsonValueKind.Number)
				requested = ra.GetInt64();
			var (address, width, domain) = ResolveTarget(a);
			bool bigEndian = ResolveBigEndian(a, domain);
			ulong value = width switch
			{
				8 => _tool.Memory!.ReadByte(address, domain),
				16 or 32 => ReadValue(address, width, domain, bigEndian),
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
			};
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["value"] = value,
				["requested"] = requested,
				["address"] = address,
				["endianness"] = EndianName(bigEndian),
			});
		}

		private string WriteMemory(JsonElement? args)
		{
			var a = Required(args);
			var (address, width, domain) = ResolveTarget(a);
			ulong value = RequireULong(a, "value");
			bool bigEndian = ResolveBigEndian(a, domain);
			ulong max = width switch
			{
				8 => 0xFFUL,
				16 => 0xFFFFUL,
				32 => 0xFFFFFFFFUL,
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
			};
			if (value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
			bool doFreeze = a.TryGetProperty("freeze", out var fz) && fz.ValueKind == JsonValueKind.True;
			if (doFreeze)
			{
				// resolve BEFORE writing so a freeze failure never leaves the
				// memory written but the call errored (2026-08-03 QA finding)
				ResolveDomain(domain);
				ResolveCheatList();
			}
			switch (width)
			{
				case 8: _tool.Memory!.WriteU8(address, (uint)value, domain); break;
				case 16: WriteValue(address, 16, domain, value, bigEndian); break;
				case 32: WriteValue(address, 32, domain, value, bigEndian); break;
			}
			if (doFreeze) FreezeWritten(address, width, domain, bigEndian, value);
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["ok"] = true,
				["endianness"] = EndianName(bigEndian),
				["frozen"] = doFreeze,
			});
		}

		private string ReadRange(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int length = RequireInt(a, "length", 256);
			string? domain = OptionalString(a, "domain");
			EnsureKnownDomain(domain);
			if (length is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..4096");
			var sb = new System.Text.StringBuilder(length * 3);
			for (var i = 0; i < length; i++) sb.Append(_tool.Memory!.ReadByte(address + i, domain).ToString("X2")).Append(' ');
			return sb.ToString().TrimEnd();
		}

		// Raw contiguous read as base64 in one call. Per-call latency is dominated
		// by fixed overhead (~15-20ms: HTTP + JSON + UI-thread marshaling), so a
		// single bulk call beats N read_many calls for contiguous regions, and
		// base64 payloads are ~4x smaller than the per-item JSON of read_many.
		private string ReadBulk(JsonElement? args)
		{
			var a = Required(args);
			int length = RequireInt(a, "length", 256);
			if (length is < 1 or > 65536) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..65536");
			var (address, _, domain) = ResolveTarget(a);
			address = ValidateAddress(address, 8, domain);
			string name = domain ?? _tool.Memory!.GetCurrentMemoryDomain();
			uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
			if (address + length > size)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"range {address}:{address + length} outside domain \"{name}\" (size {size})");
			var raw = _tool.Memory!.ReadByteRange(address, length, domain);
			var buf = new byte[length];
			for (var i = 0; i < length; i++) buf[i] = raw[i];
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["address"] = address,
				["length"] = length,
				["domain"] = name,
				["base64"] = Convert.ToBase64String(buf),
			});
		}

		private string DumpMemory(JsonElement? args)
		{
			string? domain = null;
			long rangeStart = 0;
			long? rangeLen = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object)
			{
				domain = OptionalString(a, "domain");
				if (a.TryGetProperty("range_start", out var rs) && rs.ValueKind == JsonValueKind.Number) rangeStart = rs.GetInt64();
				if (a.TryGetProperty("range_length", out var rl) && rl.ValueKind == JsonValueKind.Number) rangeLen = rl.GetInt64();
			}
			EnsureKnownDomain(domain);
			uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
			if (rangeStart < 0) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range_start must be >= 0");
			long len = rangeLen ?? (size - rangeStart);
			if (len < 1) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range_length must be >= 1");
			if (rangeStart + len > size)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"range {rangeStart}:{rangeStart + len} outside domain \"{domain ?? _tool.Memory!.GetCurrentMemoryDomain()}\" (size {size})");

			string? path = null;
			if (args is { } b && b.ValueKind == JsonValueKind.Object) path = OptionalString(b, "path");
			if (string.IsNullOrEmpty(path))
			{
				var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp");
				System.IO.Directory.CreateDirectory(dir);
				string rangePart = rangeLen == null ? "" : $"-{rangeStart:X}";
				path = System.IO.Path.Combine(dir, $"dump-{domain ?? _tool.Memory!.GetCurrentMemoryDomain()}{rangePart}-{DateTime.Now:yyyyMMdd-HHmmss}.bin");
			}

			// read the range in chunks via ReadByteRange and write to disk
			using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
			{
				const int chunk = 0x10000;
				for (long off = 0; off < len; off += chunk)
				{
					int c = (int)Math.Min(chunk, len - off);
					var bytes = _tool.Memory!.ReadByteRange(rangeStart + off, c, domain);
					var buf = new byte[c];
					for (var i = 0; i < c; i++) buf[i] = bytes[i];
					fs.Write(buf, 0, c);
				}
			}

			string uri = RegisterArtifact(path!, "application/octet-stream", $"memory dump {domain ?? _tool.Memory!.GetCurrentMemoryDomain()} ({len} bytes)");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["path"] = path,
				["size"] = len,
				["domain"] = domain ?? _tool.Memory!.GetCurrentMemoryDomain(),
				["range_start"] = rangeLen == null ? null : rangeStart,
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

		// RAM-search reference states: raw domain bytes captured by the last
		// stateful bizhawk_search_memory call (op without "value"), keyed by
		// domain name — the classic "baseline then compare" search flow.
		private readonly Dictionary<string, byte[]> _searchPrev = new(StringComparer.OrdinalIgnoreCase);

		private string RamSnapshot(JsonElement? args)
		{
			string? domain = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object) domain = OptionalString(a, "domain");
			EnsureKnownDomain(domain);
			string name = domain ?? _tool.Memory!.GetCurrentMemoryDomain();
			uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");

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
			EnsureKnownDomain(domain);
			if (maxResults is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "max_results must be 1..4096");

			string name = domain ?? _tool.Memory!.GetCurrentMemoryDomain();
			if (!_ramSnapshots.TryGetValue(name, out var snap))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"no snapshot for domain {name}; call bizhawk_ram_snapshot first");

			uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
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
			string op = a.TryGetProperty("op", out var opEl) && opEl.ValueKind == JsonValueKind.String ? opEl.GetString()! : "eq";
			if (op is not ("eq" or "ne" or "lt" or "gt" or "le" or "ge" or "changed" or "unchanged"))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown op: {op} (eq|ne|lt|gt|le|ge|changed|unchanged)");
			bool hasValue = a.TryGetProperty("value", out var vEl) && vEl.ValueKind == JsonValueKind.Number;
			ulong value = hasValue ? vEl.GetUInt64() : 0;
			int width = RequireInt(a, "width", 8);
			string? domain = OptionalString(a, "domain");
			EnsureKnownDomain(domain);
			int maxResults = RequireInt(a, "max_results", 256);
			var mem = _tool.Memory!;

			ulong max = width switch
			{
				8 => 0xFFUL,
				16 => 0xFFFFUL,
				32 => 0xFFFFFFFFUL,
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
			};
			if (hasValue && value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
			if (maxResults is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "max_results must be 1..4096");

			string domName = domain ?? mem.GetCurrentMemoryDomain();
			bool stateful = !hasValue;
			if (stateful && op == "eq")
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "op \"eq\" needs a \"value\" — omit value only for stateful ops (ne/lt/gt/le/ge/changed/unchanged)");
			if (hasValue && op is "changed" or "unchanged")
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "op \"changed\"/\"unchanged\" compares against the previous state — omit \"value\"");

			byte[]? prevBuf = null;
			if (stateful && !_searchPrev.TryGetValue(domName, out prevBuf))
			{
				// first stateful call: capture the baseline and ask the caller
				// to advance frames before comparing (classic RAM search)
				_searchPrev[domName] = ReadDomainBytes(domain);
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["count"] = 0,
					["endianness"] = EndianName(ResolveBigEndian(a, domain)),
					["op"] = op,
					["baseline"] = true,
					["matches"] = Array.Empty<object>(),
					["message"] = "no previous state for this domain — baseline snapshot taken. Advance frames, then call again to find what changed.",
				});
			}

			bool bigEndian = ResolveBigEndian(a, domain);
			int bytesPer = width / 8;
			var matches = new List<object>();

			bool Matches(ulong cur, ulong target) => op switch
			{
				"eq" => cur == target,
				"ne" => cur != target,
				"lt" => cur < target,
				"gt" => cur > target,
				"le" => cur <= target,
				"ge" => cur >= target,
				"changed" => cur != target,
				_ => cur == target, // unchanged
			};

			// constant mode compares against "value"; stateful mode against the
			// reference bytes captured by the previous call
			ulong TargetAt(long addr)
			{
				if (!stateful) return value;
				if (addr < 0 || addr + bytesPer > prevBuf!.Length) return 0;
				return BytesToValue(prevBuf!, (int)addr, bytesPer, bigEndian);
			}

			// restricted scan over a caller-provided address list
			if (a.TryGetProperty("addresses", out var addrs) && addrs.ValueKind == JsonValueKind.Array)
			{
				int i = 0;
				foreach (var el in addrs.EnumerateArray())
				{
					if (i++ >= 4096) break;
					if (matches.Count >= maxResults) break;
					long addr = el.GetInt64();
					ulong cur = ReadValue(addr, width, domain, bigEndian);
					if (Matches(cur, TargetAt(addr)))
						matches.Add(new Dictionary<string, object?> { ["address"] = addr, ["value"] = cur });
				}
			}
			else
			{
				long rangeStart = a.TryGetProperty("range_start", out var rs) && rs.ValueKind == JsonValueKind.Number ? rs.GetInt64() : 0;
				long rangeLen = a.TryGetProperty("range_length", out var rl) && rl.ValueKind == JsonValueKind.Number ? rl.GetInt64() : mem.GetMemoryDomainSize(domain ?? "");
				if (rangeStart < 0 || rangeLen < 1) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range_start/range_length must be positive");
				if (rangeLen > 16 * 1024 * 1024) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range_length too large (max 16 MiB)");

				var bytes = mem.ReadByteRange(rangeStart, (int)rangeLen, domain);
				for (int off = 0; off < bytes.Count && matches.Count < maxResults; off++)
				{
					if (off + bytesPer > bytes.Count) break;
					ulong cur = BytesToValue(bytes, off, bytesPer, bigEndian);
					if (Matches(cur, TargetAt(rangeStart + off)))
						matches.Add(new Dictionary<string, object?> { ["address"] = rangeStart + off, ["value"] = cur });
				}
			}

			if (stateful)
			{
				// the reference for the next call is the state we just scanned
				_searchPrev[domName] = ReadDomainBytes(domain);
			}

			if (a.TryGetProperty("compact", out var compEl) && compEl.ValueKind == JsonValueKind.True)
			{
				// addresses only — the caller can re-read the few interesting
				// ones; a large match set would otherwise be mostly value noise
				var addresses = matches.ConvertAll(m => ((Dictionary<string, object?>)m!)["address"]);
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["count"] = matches.Count,
					["addresses"] = addresses,
					["op"] = op,
					["baseline"] = false,
				});
			}

			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["count"] = matches.Count,
				["endianness"] = EndianName(bigEndian),
				["op"] = op,
				["baseline"] = false,
				["matches"] = matches,
			});
		}

		// Whole-domain read in 64 KiB chunks (same shape as RamSnapshot), used
		// as the stateful search reference.
		private byte[] ReadDomainBytes(string? domain)
		{
			uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
			var buf = new byte[size];
			const int chunk = 0x10000;
			for (long off = 0; off < size; off += chunk)
			{
				int len = (int)Math.Min(chunk, size - off);
				var part = _tool.Memory!.ReadByteRange(off, len, domain);
				for (var i = 0; i < len; i++) buf[off + i] = part[i];
			}
			return buf;
		}

		private static ulong BytesToValue(IReadOnlyList<byte> bytes, int off, int bytesPer, bool bigEndian)
		{
			ulong v = 0;
			if (bigEndian)
			{
				for (int i = 0; i < bytesPer; i++) v = (v << 8) | bytes[off + i];
			}
			else
			{
				for (int i = 0; i < bytesPer; i++) v |= (ulong)bytes[off + i] << (8 * i);
			}
			return v;
		}

		private string UseMemoryDomain(JsonElement? args)
		{
			var a = Required(args);
			string? domain = RequireString(a, "domain");
			bool ok = _tool.Memory!.UseMemoryDomain(domain);
			if (!ok)
			{
				string known = string.Join(", ", _tool.Memory!.GetMemoryDomainList());
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown domain: {domain} (known domains: {known})");
			}
			return $"domain set to {domain}";
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
			EnsureKnownDomain(domain);
			if (length is < 1 or > 1048576) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..1048576");
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["hash"] = _tool.Memory!.HashRegion(address, length, domain) });
		}

		private string ReadSigned(JsonElement? args)
		{
			var a = Required(args);
			long requested = RequireLong(a, "address");
			int width = RequireInt(a, "width", 8);
			string? domain = OptionalString(a, "domain");
			bool bigEndian = ResolveBigEndian(a, domain);
			long address = ValidateAddress(requested, width, domain);
			long value = width switch
			{
				8 => (sbyte)_tool.Memory!.ReadByte(address, domain),
				16 => SignExtend(ReadValue(address, 16, domain, bigEndian), 2),
				24 => SignExtend(ReadValue(address, 24, domain, bigEndian), 3),
				32 => (int)ReadValue(address, 32, domain, bigEndian),
				_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16, 24 or 32"),
			};
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["value"] = value,
				["requested"] = requested,
				["endianness"] = EndianName(bigEndian),
			});
		}

		private string WriteSigned(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int width = RequireInt(a, "width", 8);
			long value = RequireLong(a, "value");
			string? domain = OptionalString(a, "domain");
			bool bigEndian = ResolveBigEndian(a, domain);
			address = ValidateAddress(address, width, domain);
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
				case 16: WriteValue(address, 16, domain, (ulong)(ushort)value, bigEndian); break;
				case 24: WriteValue(address, 24, domain, (ulong)(uint)value & 0xFFFFFFUL, bigEndian); break;
				case 32: WriteValue(address, 32, domain, (uint)value, bigEndian); break;
			}
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["ok"] = true,
				["endianness"] = EndianName(bigEndian),
			});
		}

		private string ReadFloat(JsonElement? args)
		{
			var a = Required(args);
			long requested = RequireLong(a, "address");
			string? domain = OptionalString(a, "domain");
			bool bigEndian = ResolveBigEndian(a, domain);
			long address = ValidateAddress(requested, 32, domain);
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["value"] = ReadFloatRaw(address, domain, bigEndian),
				["requested"] = requested,
				["endianness"] = EndianName(bigEndian),
			});
		}

		private string WriteFloat(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			if (!a.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Number) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "missing number param: value");
			string? domain = OptionalString(a, "domain");
			bool bigEndian = ResolveBigEndian(a, domain);
			address = ValidateAddress(address, 32, domain);
			WriteFloatRaw(address, domain, v.GetSingle(), bigEndian);
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["ok"] = true,
				["endianness"] = EndianName(bigEndian),
			});
		}

		private string ReadMany(JsonElement? args)
		{
			EnsureEndianness();
			var a = Required(args);
			if (!a.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must be an array");
			if (items.GetArrayLength() is < 1 or > 256)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must contain 1..256 entries");

			bool consistent = a.TryGetProperty("consistent", out var c) && c.ValueKind == JsonValueKind.True;
			bool compact = a.TryGetProperty("compact", out var cmp) && cmp.ValueKind == JsonValueKind.True;
			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (consistent && !wasPaused) _tool.EmuClient!.Pause();
			try
			{
				// Per-item errors: one bad item (unknown symbol, out-of-range
				// address) must not kill the batch — report it and keep going.
				// "requested" echoes the raw address before 68K bus masking so
				// clients can spot their own arithmetic mistakes.
				var results = new List<object?>();
				var values = new List<object?>();
				var failures = new List<object?>();
				int read = 0, failed = 0;
				var index = 0;
				foreach (var item in items.EnumerateArray())
				{
					if (item.ValueKind != JsonValueKind.Object)
					{
						failed++;
						values.Add(null);
						failures.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = "each item must be an object" });
						index++;
						continue;
					}
					long? requested = null;
					if (item.TryGetProperty("address", out var ra) && ra.ValueKind == JsonValueKind.Number)
						requested = ra.GetInt64();
					try
					{
						var (address, width, domain) = ResolveTarget(item);
						bool bigEndian = ResolveBigEndian(item, domain);
						ulong value = width switch
						{
							8 => _tool.Memory!.ReadByte(address, domain),
							16 or 32 => ReadValue(address, width, domain, bigEndian),
							_ => throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "width must be 8, 16 or 32"),
						};
						values.Add(value);
						results.Add(new Dictionary<string, object?>
						{
							["index"] = index,
							["requested"] = requested,
							["address"] = address,
							["width"] = width,
							["value"] = value,
							["domain"] = domain,
							["endianness"] = EndianName(bigEndian),
						});
						read++;
					}
					catch (JsonRpc.Error ex)
					{
						failed++;
						values.Add(null);
						failures.Add(new Dictionary<string, object?> { ["index"] = index, ["error"] = ex.Message });
						results.Add(new Dictionary<string, object?>
						{
							["index"] = index,
							["requested"] = requested,
							["address"] = null,
							["error"] = ex.Message,
						});
					}
					index++;
				}
				if (compact)
				{
					// aligned with the items array (null = failed); the caller
					// knows the addresses it asked for — ~10x smaller payload
					return JsonRpc.Pretty(new Dictionary<string, object?>
					{
						["values"] = values,
						["read"] = read,
						["failed"] = failed,
						["failures"] = failures,
					});
				}
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["reads"] = results,
					["read"] = read,
					["failed"] = failed,
				});
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
			string? domain = OptionalString(a, "domain");
			EnsureKnownDomain(domain);

			// fill mode: one byte repeated "length" times — a tiny payload for
			// large clears (e.g. zeroing 1440 bytes of level layout). The values
			// array mode aborts in some MCP clients around 1-2 KB of payload
			// (the request never reaches the server — TODO B1), so this is the
			// transport-safe way to write big contiguous regions.
			byte[] bytes;
			long? fill = null;
			if (a.TryGetProperty("fill", out var fillEl) && fillEl.ValueKind == JsonValueKind.Number)
			{
				long fillValue = fillEl.GetInt64();
				if (fillValue is < 0 or > 0xFF) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"fill {fillValue} does not fit a byte");
				int length = RequireInt(a, "length", 0);
				if (length is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..4096");
				bytes = new byte[length];
				for (var i = 0; i < length; i++) bytes[i] = (byte)fillValue;
				fill = fillValue;
			}
			else
			{
				if (!a.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "values must be an array (or use fill + length)");
				int len = values.GetArrayLength();
				if (len is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "values must contain 1..4096 bytes");
				bytes = new byte[len];
				var i = 0;
				foreach (var el in values.EnumerateArray())
				{
					if (el.ValueKind != JsonValueKind.Number) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "values must be numbers");
					long v = el.GetInt64();
					if (v is < 0 or > 0xFF) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {v} does not fit a byte");
					bytes[i++] = (byte)v;
				}
			}

			address = ValidateAddress(address, 8, domain);
			bool doFreeze = a.TryGetProperty("freeze", out var fz) && fz.ValueKind == JsonValueKind.True;
			if (doFreeze)
			{
				// resolve BEFORE writing (2026-08-03 QA finding)
				ResolveDomain(domain);
				ResolveCheatList();
			}
			if (!TryBulkWrite(domain, address, bytes))
				_tool.Memory!.WriteByteRange(address, bytes, domain);
			if (doFreeze)
			{
				var md = ResolveDomain(domain);
				var list = new List<Cheat>();
				for (var i = 0; i < bytes.Length; i++)
					list.Add(MakeCheat(md, address + i, 8, false, bytes[i], null));
				ResolveCheatList().AddRange(list);
			}
			if (fill != null)
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["wrote"] = bytes.Length,
					["address"] = address,
					["fill"] = fill.Value,
					["frozen"] = doFreeze,
				});
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["wrote"] = bytes.Length,
				["address"] = address,
				["frozen"] = doFreeze,
			});
		}

		// Writes a whole range with ONE waterbox crossing when the domain exposes
		// a raw pointer (gpgx's Main RAM / 68K RAM is a MemoryDomainIntPtrMonitor).
		// The ApiHawk WriteByteRange loops PokeByte per byte → one interop call per
		// byte, which is the dominant cost (hundreds of crossings for a few hundred
		// bytes). Writing straight into the domain's Data pointer inside a single
		// Enter/Exit is up to ~400x fewer crossings. Falls back to the ApiHawk path
		// if the domain isn't pointer-backed (reflection-safe, like watchpoints).
		// GetMethod can miss methods on nested generic/closed types in some
		// compilation contexts; enumerate instead.
		private static System.Reflection.MethodInfo? FindMethod(Type type, string name)
		{
			var all = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
			foreach (var m in all)
			{
				if (m.Name == name)
				{
					var ps = m.GetParameters();
					if (ps.Length == 0) return m;
				}
			}
			return null;
		}

		private bool TryBulkWrite(string? domain, long address, byte[] bytes)
		{
			try
			{
				var memApi = _tool.Memory!;
				// IMemoryDomains indexer by name → MemoryDomain
				var listProp = memApi.GetType().GetProperty("DomainList", BindingFlags.Public | BindingFlags.Instance);
				if (listProp == null) return false;
				var list = listProp.GetValue(memApi);
				if (list == null) return false;
				// the real MemoryDomainList has both this[int] and this[string]:
				// GetProperty("Item") is ambiguous (AmbiguousMatchException), so
				// the bulk path silently fell back on every write until the
				// string-indexer lookup was fixed (2026-08-03 live QA).
				var indexer = FindStringIndexer(list.GetType());
				if (indexer == null) return false;
				string domainName = domain ?? memApi.GetCurrentMemoryDomain();
				var memDomain = indexer.GetValue(list, new object[] { domainName });
				if (memDomain == null) return false;

				var dataProp = memDomain.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
				if (dataProp == null) return false;
				var data = (IntPtr)dataProp.GetValue(memDomain)!;
				if (data == IntPtr.Zero) return false;

				// single Enter/Exit around the whole copy (Marshal.Copy is one memcpy)
				var enter = FindMethod(memDomain.GetType(), "Enter");
				var exit = FindMethod(memDomain.GetType(), "Exit");
				if (enter == null || exit == null) return false;

				enter.Invoke(memDomain, null);
				try
				{
					System.Runtime.InteropServices.Marshal.Copy(bytes, 0, IntPtr.Add(data, checked((int)address)), bytes.Length);
				}
				finally
				{
					exit.Invoke(memDomain, null);
				}
				return true;
			}
			catch
			{
				return false;
			}
		}

		private string WriteMany(JsonElement? args)
		{
			var a = Required(args);
			if (!a.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must be an array");
			if (items.GetArrayLength() is < 1 or > 256)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "items must contain 1..256 entries");

			// Per-item validation: a bad item (unknown symbol, out-of-range
			// address, value too wide) fails only itself; valid items still
			// write. Failures carry the requested address + reason so clients
			// can find the typo'd item instead of replaying the batch (TODO F1).
			var failures = new List<object?>();
			int written = 0;
			var index = 0;
			bool anyFreeze = false;
			foreach (var item in items.EnumerateArray())
				if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("freeze", out var f) && f.ValueKind == JsonValueKind.True)
					anyFreeze = true;
			if (anyFreeze) ResolveCheatList(); // fail the batch before writing anything (QA finding)
			foreach (var item in items.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object)
				{
					failures.Add(new Dictionary<string, object?> { ["index"] = index, ["address"] = null, ["reason"] = "each item must be an object" });
					index++;
					continue;
				}
				long? requested = null;
				if (item.TryGetProperty("address", out var ra) && ra.ValueKind == JsonValueKind.Number)
					requested = ra.GetInt64();
				try
				{
					var (address, width, domain) = ResolveTarget(item);
					bool bigEndian = ResolveBigEndian(item, domain);
					ulong value = RequireULong(item, "value");
					ulong max = width switch
					{
						8 => 0xFFUL,
						16 => 0xFFFFUL,
						_ => 0xFFFFFFFFUL,
					};
					if (value > max) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
					bool itemFreeze = item.TryGetProperty("freeze", out var fz) && fz.ValueKind == JsonValueKind.True;
					if (itemFreeze) ResolveDomain(domain); // validate before writing this item
					switch (width)
					{
						case 8: _tool.Memory!.WriteU8(address, (uint)value, domain); break;
						case 16: WriteValue(address, 16, domain, value, bigEndian); break;
						case 32: WriteValue(address, 32, domain, value, bigEndian); break;
					}
					if (itemFreeze) FreezeWritten(address, width, domain, bigEndian, value);
					written++;
				}
				catch (JsonRpc.Error ex)
				{
					failures.Add(new Dictionary<string, object?> { ["index"] = index, ["address"] = requested, ["reason"] = ex.Message });
				}
				index++;
			}
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["wrote"] = written,
				["failed"] = failures.Count,
				["failures"] = failures,
			});
		}

		// ── fixture capture ─────────────────────────────────────────────────────
		// Orchestrates a scripted capture: advance N frames with an input
		// timeline, sampling a set of addresses/symbols each frame (frame-atomic,
		// like read_many consistent:true), and write the result as CSV. This is
		// the direct replacement for the manual capture_fixture.lua flow — the
		// CSV lands on the host disk in one call.

		private string StartFixture(JsonElement? args)
		{
			var a = Required(args);
			int frames = RequireInt(a, "frames", 0);
			if (frames is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "frames must be 1..600");
			int delay = RequireInt(a, "delay", 0);
			if (delay is < 0 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "delay must be 0..600");
			if (!a.TryGetProperty("samples", out var samples) || samples.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "samples must be an array of {\"name\"|\"address\", width?, domain?}");
			if (samples.GetArrayLength() is < 1 or > 256)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "samples must contain 1..256 entries");

			// resolve every sample once: name (symbol) or address + width + domain
			var resolved = new List<(string label, long address, int width, string? domain, bool bigEndian)>();
			foreach (var s in samples.EnumerateArray())
			{
				if (s.ValueKind != JsonValueKind.Object) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each sample must be an object");
				var (address, width, domain) = ResolveTarget(s);
				bool bigEndian = ResolveBigEndian(s, domain);
				string label = OptionalString(s, "name") ?? $"{domain ?? "?"}@{address:X}";
				resolved.Add((label, address, width, domain, bigEndian));
			}

			// input timeline: frame → buttons/controller
			var timeline = new Dictionary<int, (IReadOnlyDictionary<string, bool> buttons, int? controller)>();
			if (a.TryGetProperty("inputs", out var inputs) && inputs.ValueKind == JsonValueKind.Array)
			{
				foreach (var i in inputs.EnumerateArray())
				{
					if (i.ValueKind != JsonValueKind.Object) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each input must be an object");
					int at = RequireInt(i, "frame", 0);
					if (at is < 0 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "input frame must be 0..600");
					if (!i.TryGetProperty("buttons", out var btns) || btns.ValueKind != JsonValueKind.Object)
						throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "input.buttons must be an object {button: bool}");
					int? controller = i.TryGetProperty("controller", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 1;
					var map = new Dictionary<string, bool>();
					foreach (var prop in btns.EnumerateObject()) map[prop.Name] = prop.Value.GetBoolean();
					timeline[at] = (map, controller);
				}
			}
			// hold (default): buttons persist until the next timeline entry, so a
			// timeline ending in {Right: true} keeps Right held for all remaining
			// frames. explicit: absent timeline frames release ALL buttons (each
			// frame gets exactly the timeline's buttons). In both modes an empty
			// buttons object at a frame releases that controller (JoypadApi.Set
			// un-sets every button not present in the dict).
			string inputMode = OptionalString(a, "input_mode") ?? "hold";
			if (inputMode != "hold" && inputMode != "explicit")
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "input_mode must be \"hold\" or \"explicit\"");
			bool explicitMode = inputMode == "explicit";

			string? path = OptionalString(a, "path");
			path = NormalizeHostPath(path, IsWindowsHost());
			if (string.IsNullOrEmpty(path))
			{
				var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp");
				System.IO.Directory.CreateDirectory(dir);
				path = System.IO.Path.Combine(dir, $"fixture-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
			}

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();
			try
			{
				// optional leading delay (skip title screens, reach gameplay)
				for (var i = 0; i < delay; i++)
				{
					AdvanceFrame();
				}

				var lines = new System.Text.StringBuilder();
				lines.Append("frame");
				foreach (var (label, _, _, _, _) in resolved) lines.Append(',').Append(label);
				lines.AppendLine();

				for (var f = 0; f < frames; f++)
				{
					if (timeline.TryGetValue(f, out var ev)) _tool.Joypad!.Set(ev.buttons, ev.controller);
					else if (explicitMode) _tool.Joypad!.Set(new Dictionary<string, bool>(), null); // absent frame = no buttons
					AdvanceFrame();

					// sample after the frame, while paused-at-frame (single step)
					lines.Append(f);
					foreach (var (_, address, width, domain, bigEndian) in resolved)
					{
						ulong v = width switch
						{
							8 => _tool.Memory!.ReadByte(address, domain),
							16 or 32 => ReadValue(address, width, domain, bigEndian),
							_ => 0,
						};
						lines.Append(',').Append(v);
					}
					lines.AppendLine();
				}

				System.IO.File.WriteAllText(path, lines.ToString());
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["path"] = path,
					["frames"] = frames,
					["samples"] = resolved.Count,
					["row_count"] = frames,
				});
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}
		}

		// ── struct reads ───────────────────────────────────────────────────────
		// Read a set of relative-offset fields from a base address (or symbol),
		// all in one frame-consistent pass. Replaces hand-rolled sprObjectOffsets
		// arithmetic: define the struct once, read it per frame.

		private string ReadStruct(JsonElement? args)
		{
			var a = Required(args);
			var (baseAddr, _, baseDomain) = ResolveTarget(a); // accepts "address" or "name"
			if (!a.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "fields must be an array of {\"name\", \"offset\", width?, endianness?}");
			if (fields.GetArrayLength() is < 1 or > 256)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "fields must contain 1..256 entries");

			string? domain = OptionalString(a, "domain") ?? baseDomain;
			var outFields = new List<object?>();
			foreach (var f in fields.EnumerateArray())
			{
				if (f.ValueKind != JsonValueKind.Object) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each field must be an object");
				string fieldName = RequireString(f, "name");
				int offset = RequireInt(f, "offset", 0);
				int width = RequireInt(f, "width", 8);
				if (width is not (8 or 16 or 32)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"field {fieldName}: width must be 8, 16 or 32");
				// per-field endianness (defaults to the domain, like the rest)
				bool bigEndian = ResolveBigEndian(f, domain);
				long address = ValidateAddress(baseAddr + offset, width, domain);
				ulong value = width switch
				{
					8 => _tool.Memory!.ReadByte(address, domain),
					16 or 32 => ReadValue(address, width, domain, bigEndian),
					_ => 0,
				};
				outFields.Add(new Dictionary<string, object?>
				{
					["name"] = fieldName,
					["offset"] = offset,
					["address"] = address,
					["value"] = value,
					["endianness"] = EndianName(bigEndian),
				});
			}

			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["base"] = baseAddr,
				["domain"] = domain,
				["fields"] = outFields,
			});
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
			int rShift, bShift;
			bool bigEndian;
			switch (sys)
			{
				case "GEN":
				case "SMD":
					domain = "CRAM";
					entryBits = 3;   // 16-bit 0x0RRR0GGG0BBB: R at bits 1-3, G 5-7, B 9-11
					rShift = 1;
					bShift = 9;
					bigEndian = true;
					break;
				case "SNES":
				case "SNESBG":
					domain = "CGRAM";
					entryBits = 5;   // 16-bit BGR555: R at bits 0-4, G 5-9, B 10-14
					rShift = 0;
					bShift = 10;
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
				int r = (entry >> rShift) & mask;
				int g = (entry >> 5) & mask;
				int b = (entry >> bShift) & mask;
				colors.Add($"#{To8Bit(r, mask):X2}{To8Bit(g, mask):X2}{To8Bit(b, mask):X2}");
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["system"] = sys, ["domain"] = domain, ["colors"] = colors });
		}

		// ── plane decode (Genesis VDP → PNG) ───────────────────────────────────
		// Decodes a background nametable (plane A/B) from VRAM into a PNG using
		// the CRAM palette. Genesis Mode 5 details (from Genesis Plus GX):
		//   nametable entry (16-bit BE): bit15 P, bits14-13 palette (0..3 = CRAM
		//     block of 16), bit12 V-flip, bit11 H-flip, bits10-0 tile index.
		//   tile row (8px, 4bpp): 4 bytes, each byte holds TWO pixels packed as
		//     high nibble (left) + low nibble (right). Pixel color = (byte[x>>1]
		//     >> ((x&1)?0:4)) & 0xF — the color index into the 16-color palette.
		//   tile base = tileIndex * 0x20 (+ row*4 for the row bytes).
		// The plane bases come from the core's VDP view (gpgx exposes NTA/NTB via
		// UpdateVDPViewContext — reached by reflection like the watchpoints); when
		// that's unavailable we fall back to the typical reg2/reg4 values.

		// Returns (planeA, planeB) nametable base addresses from the core, or null.
		private (long a, long b, int aw, int ah, int bw, int bh)? TryGetVdpPlaneBases()
		{
			try
			{
				var emu = _tool.Emulation;
				if (emu == null) return null;
				var prop = emu.GetType().GetProperty("DebuggableCore", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
				var core = prop?.GetValue(emu);
				if (core == null) return null;
				var m = core.GetType().GetMethod("UpdateVDPViewContext", BindingFlags.Public | BindingFlags.Instance);
				var view = m?.Invoke(core, null);
				if (view == null) return null;
				var ntA = view.GetType().GetField("NTA")?.GetValue(view);
				var ntB = view.GetType().GetField("NTB")?.GetValue(view);
				if (ntA == null || ntB == null) return null;
				int ABase = (int)ntA.GetType().GetField("Baseaddr")!.GetValue(ntA)!;
				int BBase = (int)ntB.GetType().GetField("Baseaddr")!.GetValue(ntB)!;
				int AW = (int)ntA.GetType().GetField("Width")!.GetValue(ntA)!;
				int AH = (int)ntA.GetType().GetField("Height")!.GetValue(ntA)!;
				int BW = (int)ntB.GetType().GetField("Width")!.GetValue(ntB)!;
				int BH = (int)ntB.GetType().GetField("Height")!.GetValue(ntB)!;
				return (ABase, BBase, AW, AH, BW, BH);
			}
			catch
			{
				return null;
			}
		}

		private string GetVdpView()
		{
			var v = TryGetVdpPlaneBases();
			if (v == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "VDP view unavailable: the loaded core does not expose nametable bases (Genesis gpgx only)");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["planeA"] = new Dictionary<string, object?> { ["base"] = v.Value.a, ["width"] = v.Value.aw, ["height"] = v.Value.ah },
				["planeB"] = new Dictionary<string, object?> { ["base"] = v.Value.b, ["width"] = v.Value.bw, ["height"] = v.Value.bh },
			});
		}

		// The gpgx core reports BOTH CPUs in one register table
		// (GetCpuFlagsAndRegisters: "M68K PC", "Z80 pc", ...) — this filters
		// the Z80 sound CPU half. Core-specific surface, so the name says so.
		private string GenesisGetZ80Registers()
		{
			var regs = _tool.Emulation!.GetRegisters();
			var z80 = regs.Where(kv => kv.Key.StartsWith("Z80", StringComparison.Ordinal))
				.ToDictionary(kv => kv.Key, kv => kv.Value);
			if (z80.Count == 0)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "no Z80 registers: the loaded core does not expose the sound CPU (Genesis gpgx only — e.g. Z80 PC, Z80 SP, Z80 A)");
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["registers"] = z80 });
		}

		// ── Z80 disassembly ──────────────────────────────────────────────────
		// The gpgx core's own IDisassemblable only speaks 68K, but BizHawk ships
		// a pure static Z80 table disassembler (Z80ADisassembler in
		// BizHawk.Emulation.Cores — loaded in-process with the core), so no CPU
		// instance is needed. Reached via reflection; tests inject a fake.
		private Func<ushort, Func<ushort, byte>, (string Text, int Size)>? _z80DisasmOverride;
		private static MethodInfo? _z80DisasmMethod;

		private static MethodInfo? Z80Disasm()
		{
			if (_z80DisasmMethod != null) return _z80DisasmMethod;
			var t = Type.GetType("BizHawk.Emulation.Cores.Components.Z80A.Z80ADisassembler, BizHawk.Emulation.Cores");
			foreach (var m in t?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? Array.Empty<MethodInfo>())
			{
				if (m.Name == "Disassemble" && m.GetParameters().Length == 3)
				{
					_z80DisasmMethod = m;
					break;
				}
			}
			return _z80DisasmMethod;
		}

		private (string Text, int Size) Z80Disassemble(ushort addr, Func<ushort, byte> read)
		{
			if (_z80DisasmOverride != null) return _z80DisasmOverride(addr, read);
			var m = Z80Disasm();
			if (m == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "Z80 disassembler unavailable: BizHawk.Emulation.Cores not loaded (Genesis gpgx core only)");
			var args = new object[] { addr, read, 0 };
			var result = m.Invoke(null, args);
			return ((string)result!, (int)args[2]);
		}

		// Z80 debugging needs the Z80's 16-bit bus space. On SMS/GG the core
		// exposes a native "Z80 BUS" domain (gpgx_peek_z80_bus); on GEN it does
		// NOT (verified in the pinned GPGX.IMemoryDomains.cs — the Z80 BUS
		// domain is only created in the non-GEN branch), and the bus is
		// synthesized. Live QA (Kid Chameleon) proved the GEN mapping is:
		//   0x0000-0x1FFF = Z80 RAM (where sound drivers run — the 68K uploads
		//                    them; the reset vector executes RAM@0x0000)
		//   0x2000-0x3FFF = Z80 RAM aliased (& 0x1FFF)
		//   0x4000-0x7FFF = sound I/O (YM2612/PSG ports — reads as open bus)
		//   0x8000-0xFFFF = open bus (0xFF)
		// NOT the "0x0000-0x1FFF ROM window / 0x2000 RAM" layout — the first
		// version of this synthesis had it inverted (caught by re-QA).
		// Point of view matters: the domain's "bus_base 0xA00000" is the 68K's
		// WINDOW onto the same physical RAM — from the Z80's perspective there
		// is no 68K/ROM/RAM split, only its own bus, with its RAM at 0x0000.
		private void EnsureZ80Accessible()
		{
			var known = _tool.Memory!.GetMemoryDomainList();
			bool ok = known.Contains("Z80 BUS") || known.Contains("Z80 RAM");
			if (!ok)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "Z80 debugging requires a Genesis gpgx or SMS/GG core (Z80 BUS/Z80 RAM domain missing)");
		}

		private byte Z80Read(ushort addr)
		{
			var mem = _tool.Memory!;
			var known = mem.GetMemoryDomainList();
			if (known.Contains("Z80 BUS")) return (byte)mem.ReadByte(addr, "Z80 BUS");
			if (addr >= 0x4000) return 0xFF; // sound I/O + open bus read as 0xFF
			return (byte)mem.ReadByte(addr & 0x1FFF, "Z80 RAM");
		}

		private string Z80DisassembleTool(JsonElement? args)
		{
			var a = Required(args);
			long address = RequireLong(a, "address");
			int count = RequireInt(a, "count", 8);
			if (count is < 1 or > 64) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "count must be 1..64");
			if (address is < 0 or > 0xFFFF) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "address must be in Z80 bus space 0x0000-0xFFFF");
			EnsureZ80Accessible();

			var instructions = new List<object?>();
			ushort pc = (ushort)address;
			for (var i = 0; i < count; i++)
			{
				var (text, size) = Z80Disassemble(pc, Z80Read);
				if (size < 1) size = 1;
				var raw = new List<byte>(size);
				for (var b = 0; b < size; b++) raw.Add(Z80Read((ushort)(pc + b)));
				var hex = new System.Text.StringBuilder(size * 3);
				for (var b = 0; b < size; b++) hex.Append(raw[b].ToString("X2")).Append(' ');
				instructions.Add(new Dictionary<string, object?>
				{
					["address"] = pc,
					["bytes"] = hex.ToString().TrimEnd(),
					["instruction"] = text,
				});
				pc = (ushort)(pc + size);
			}

			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["address"] = address,
				["count"] = instructions.Count,
				["instructions"] = instructions,
			});
		}

		// Z80 analog of bizhawk_trace: sample the sound CPU's PC/SP each frame
		// and disassemble at PC — shows the sound driver's main loop, busy-waits
		// and per-frame cost. Optional stack_words dumps the Z80 stack (SP
		// points into Z80 RAM at 0x2000-0x3FFF of bus space).
		private string Z80Trace(JsonElement? args)
		{
			var a = Required(args);
			int count = RequireInt(a, "count", 60);
			int step = RequireInt(a, "step", 1);
			int stackWords = RequireInt(a, "stack_words", 0);
			if (count is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "count must be 1..600");
			if (step is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "step must be 1..600");
			if (stackWords is < 0 or > 32) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "stack_words must be 0..32");
			EnsureZ80Accessible();

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			var samples = new List<object?>();
			try
			{
				for (var i = 0; i < count; i++)
				{
					AdvanceFrame();
					if (i % step != 0) continue;
					var regs = _tool.Emulation!.GetRegisters();
					var z80 = regs.Where(kv => kv.Key.StartsWith("Z80", StringComparison.OrdinalIgnoreCase)).ToDictionary(kv => kv.Key, kv => kv.Value);
					if (z80.Count == 0)
						throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "no Z80 registers: the loaded core does not expose the sound CPU (Genesis gpgx only)");
					ulong pc = FindRegister(z80, "pc");
					ulong sp = FindRegister(z80, "sp");

					string? disasm = null;
					try { (disasm, _) = Z80Disassemble((ushort)pc, Z80Read); }
					catch { disasm = null; }

					var sample = new Dictionary<string, object?>
					{
						["frame"] = _tool.Emulation!.FrameCount(),
						["pc"] = pc,
						["sp"] = sp,
						["instruction"] = disasm,
					};
					if (stackWords > 0)
					{
						var stack = new List<object?>();
						for (var w = 0; w < stackWords; w++)
						{
							ulong addr = (sp + (ulong)(2 * w)) & 0xFFFF;
							uint lo = Z80Read((ushort)addr);
							uint hi = Z80Read((ushort)((addr + 1) & 0xFFFF));
							stack.Add(new Dictionary<string, object?> { ["address"] = addr, ["value"] = (ulong)(lo | (hi << 8)) });
						}
						sample["stack"] = stack;
					}
					samples.Add(sample);
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			return JsonRpc.Pretty(new Dictionary<string, object?> { ["samples"] = samples });
		}

		private string ReadPlane(JsonElement? args)
		{
			var a = Required(args);
			string plane = a.TryGetProperty("plane", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "A";
			if (plane is not ("A" or "B")) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "plane must be \"A\" or \"B\"");

			// base: explicit param wins; otherwise ask the core for the real
			// nametable address (Kid Chameleon uses plane A at 0x0000, not the
			// typical 0xC000); fall back to the common reg2/reg4 values.
			long baseAddr;
			if (a.TryGetProperty("base", out var b) && b.ValueKind == JsonValueKind.Number)
			{
				baseAddr = b.GetInt64();
			}
			else
			{
				var v = TryGetVdpPlaneBases();
				baseAddr = v != null ? (plane == "A" ? v.Value.a : v.Value.b) : (plane == "A" ? 0xC000 : 0xE000);
			}

			int cols = RequireInt(a, "columns", 64);
			int rows = RequireInt(a, "rows", 32);
			int scale = RequireInt(a, "scale", 1);
			int offsetX = RequireInt(a, "offset_x", 0);
			int offsetY = RequireInt(a, "offset_y", 0);
			if (cols is < 1 or > 128) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "columns must be 1..128");
			if (rows is < 1 or > 128) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "rows must be 1..128");
			if (scale is < 1 or > 8) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "scale must be 1..8");
			if (offsetX < 0 || offsetY < 0) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "offset_x/offset_y must be >= 0");
			if (baseAddr < 0 || baseAddr + (long)(offsetY + rows) * 128 + (long)(offsetX + cols) * 2 > 0x10000)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"base {baseAddr:X} + window {offsetX},{offsetY}+{cols}x{rows} exceeds VRAM (64KB)");

			// palette: 64 entries × 16-bit BE from CRAM, hardware 0x0RRR0GGG0BBB
			var palette = new byte[64 * 3];
			for (var i = 0; i < 64; i++)
			{
				byte lo = (byte)_tool.Memory!.ReadByte(i * 2, "CRAM");
				byte hi = (byte)_tool.Memory!.ReadByte(i * 2 + 1, "CRAM");
				int entry = (lo << 8) | hi;
				int mask = 0x7;
				palette[i * 3 + 0] = (byte)To8Bit((entry >> 1) & mask, mask);
				palette[i * 3 + 1] = (byte)To8Bit((entry >> 5) & mask, mask);
				palette[i * 3 + 2] = (byte)To8Bit((entry >> 9) & mask, mask);
			}

			// render tiles row by row
			int px = cols * 8 * scale, py = rows * 8 * scale;
			var img = new byte[px * py * 3];
			for (var ty = 0; ty < rows; ty++)
			{
				for (var tx = 0; tx < cols; tx++)
				{
					// nametable row stride is 64 entries (128 bytes); the window
					// offset selects a camera-sized region within it
					long nt = baseAddr + (long)(offsetY + ty) * 128 + (long)(offsetX + tx) * 2;
					byte lo = (byte)_tool.Memory!.ReadByte(nt, "VRAM");
					byte hi = (byte)_tool.Memory!.ReadByte(nt + 1, "VRAM");
					int attr = (lo << 8) | hi;
					int tileIndex = attr & 0x7FF;
					int paletteSel = (attr >> 13) & 0x3;   // CRAM block 0..3
					bool hFlip = (attr & 0x800) != 0;      // bit 11
					bool vFlip = (attr & 0x1000) != 0;     // bit 12
					int palBase = paletteSel * 16;

					for (var y = 0; y < 8; y++)
					{
						int row = vFlip ? 7 - y : y;
						long tileAddr = tileIndex * 0x20 + row * 4;
						for (var x = 0; x < 8; x++)
						{
							int col = hFlip ? 7 - x : x;
							byte rowByte = (byte)_tool.Memory!.ReadByte(tileAddr + (col >> 1), "VRAM");
							int colorIndex = (rowByte >> ((col & 1) != 0 ? 0 : 4)) & 0xF;
							int palIdx = palBase + colorIndex;
							byte r = palette[palIdx * 3], g = palette[palIdx * 3 + 1], bl = palette[palIdx * 3 + 2];

							// fill scale×scale block
							for (var sy = 0; sy < scale; sy++)
							{
								int iy = (ty * 8 + y) * scale + sy;
								for (var sx = 0; sx < scale; sx++)
								{
									int ix = (tx * 8 + x) * scale + sx;
									int off = (iy * px + ix) * 3;
									img[off] = r; img[off + 1] = g; img[off + 2] = bl;
								}
							}
						}
					}
				}
			}

			string? outPath = NormalizeHostPath(OptionalString(a, "path"), IsWindowsHost());
			string path;
			if (string.IsNullOrEmpty(outPath))
			{
				path = WritePngToTemp("plane", px, py, img);
			}
			else
			{
				path = outPath!;
				var dir = System.IO.Path.GetDirectoryName(path);
				if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
				WritePng(path, px, py, img);
			}
			string uri = RegisterArtifact(path!, "image/png", $"plane {plane} ({cols}x{rows} tiles @0x{baseAddr:X})");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["plane"] = plane,
				["base"] = baseAddr,
				["columns"] = cols,
				["rows"] = rows,
				["width"] = px,
				["height"] = py,
				["path"] = path,
				["resource"] = uri,
			});
		}

		// Minimal PNG encoder (24-bit RGB, zlib via DeflateStream) so the tool
		// runs on both net48 (EmuHawk) and Linux without System.Drawing.Bitmap.
		private string WritePngToTemp(string prefix, int width, int height, byte[] rgb)
		{
			var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp");
			System.IO.Directory.CreateDirectory(dir);
			string path = System.IO.Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
			WritePng(path, width, height, rgb);
			return path;
		}

		private static void WritePng(string path, int width, int height, byte[] rgb)
		{
			// scanlines: each row prefixed with filter byte 0 (None), then RGB
			int stride = width * 3;
			var raw = new byte[(stride + 1) * height];
			for (var y = 0; y < height; y++)
			{
				int dst = y * (stride + 1);
				raw[dst] = 0;
				Buffer.BlockCopy(rgb, y * stride, raw, dst + 1, stride);
			}

			// zlib stream: 0x78 0x9C header + deflate + adler32
			byte[] deflated;
			using (var ms = new System.IO.MemoryStream())
			{
				using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, true))
				{
					ds.Write(raw, 0, raw.Length);
				}
				deflated = ms.ToArray();
			}
			uint adler = Adler32(raw);
			var idat = new byte[deflated.Length + 6];
			idat[0] = 0x78; idat[1] = 0x9C;
			Buffer.BlockCopy(deflated, 0, idat, 2, deflated.Length);
			idat[idat.Length - 4] = (byte)(adler >> 24);
			idat[idat.Length - 3] = (byte)(adler >> 16);
			idat[idat.Length - 2] = (byte)(adler >> 8);
			idat[idat.Length - 1] = (byte)adler;

			using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write))
			{
				fs.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
				WriteChunk(fs, "IHDR", IhdrBytes(width, height));
				WriteChunk(fs, "IDAT", idat);
				WriteChunk(fs, "IEND", System.Array.Empty<byte>());
			}
		}

		private static byte[] IhdrBytes(int width, int height)
		{
			var b = new byte[13];
			b[0] = (byte)(width >> 24); b[1] = (byte)(width >> 16); b[2] = (byte)(width >> 8); b[3] = (byte)width;
			b[4] = (byte)(height >> 24); b[5] = (byte)(height >> 16); b[6] = (byte)(height >> 8); b[7] = (byte)height;
			b[8] = 8;  // bit depth
			b[9] = 2;  // color type: truecolor RGB
			b[10] = 0; // compression
			b[11] = 0; // filter
			b[12] = 0; // interlace
			return b;
		}

		private static void WriteChunk(System.IO.FileStream fs, string type, byte[] data)
		{
			var len = new[] { (byte)(data.Length >> 24), (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length };
			fs.Write(len, 0, 4);
			var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
			fs.Write(typeBytes, 0, 4);
			fs.Write(data, 0, data.Length);
			uint crc = Crc32(typeBytes, data);
			fs.Write(new[] { (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc }, 0, 4);
		}

		private static uint Adler32(byte[] data)
		{
			uint a = 1, b = 0;
			for (var i = 0; i < data.Length; i++)
			{
				a = (a + data[i]) % 65521;
				b = (b + a) % 65521;
			}
			return (b << 16) | a;
		}

		private static readonly uint[] CrcTable = BuildCrcTable();

		private static uint[] BuildCrcTable()
		{
			var t = new uint[256];
			for (uint n = 0; n < 256; n++)
			{
				uint c = n;
				for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				t[n] = c;
			}
			return t;
		}

		private static uint Crc32(byte[] type, byte[] data)
		{
			uint c = 0xFFFFFFFF;
			foreach (var t in type) c = CrcTable[(c ^ t) & 0xFF] ^ (c >> 8);
			foreach (var d in data) c = CrcTable[(c ^ d) & 0xFF] ^ (c >> 8);
			return c ^ 0xFFFFFFFF;
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
				AdvanceFrame();
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

		private string GetSound()
		{
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["sound_on"] = _tool.EmuClient!.GetSoundOn() });
		}

		private string SetSound(JsonElement? args)
		{
			bool enabled = args is not { } a || !a.TryGetProperty("enabled", out var v) || v.GetBoolean();
			_tool.EmuClient!.SetSoundOn(enabled);
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["sound_on"] = _tool.EmuClient!.GetSoundOn() });
		}

		private string EnableRewind(JsonElement? args)
		{
			bool enabled = args is not { } a || !a.TryGetProperty("enabled", out var v) || v.GetBoolean();
			_tool.EmuClient!.EnableRewind(enabled);
			return $"rewind {(enabled ? "enabled" : "disabled")}";
		}

		private string FrameSkipTool(JsonElement? args)
		{
			int count = args is { } a ? RequireInt(a, "count", 0) : 0;
			if (count is < 0 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "count must be 0..600");
			_tool.EmuClient!.FrameSkip(count);
			return count == 0 ? "frameskip disabled" : $"frameskip set to {count}";
		}

		private string LimitFramerate(JsonElement? args)
		{
			bool enabled = args is not { } a || !a.TryGetProperty("enabled", out var v) || v.GetBoolean();
			_tool.Emulation!.LimitFramerate(enabled);
			return $"framerate limit {(enabled ? "enabled" : "disabled")}";
		}

		private string OpenRom(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			bool ok = _tool.EmuClient!.OpenRom(path);
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["loaded"] = ok, ["path"] = path });
		}

		private string CloseRom()
		{
			_tool.EmuClient!.CloseRom();
			return "rom closed";
		}

		private string Reboot()
		{
			_tool.EmuClient!.RebootCore();
			return "core rebooted";
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
			string path = CapturePng(args, "shot", out bool includeOverlays);
			string uri = RegisterArtifact(path, "image/png", $"screenshot {System.IO.Path.GetFileName(path)}");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["path"] = path,
				["include_overlays"] = includeOverlays,
				["resource"] = uri,
			});
		}

		// Screenshot to a PNG file — explicit path or the temp dir default —
		// with the OSD-overlay flag, restoring the no-overlay default after the
		// call (no getter, so each call re-applies its own preference). Shared
		// by screenshot and frame_hash. Returns the effective absolute path.
		private string CapturePng(JsonElement? args, string prefix, out bool includeOverlays)
		{
			string? path = null;
			includeOverlays = false;
			if (args is { } a && a.ValueKind == JsonValueKind.Object)
			{
				path = NormalizeHostPath(OptionalString(a, "path"), IsWindowsHost());
				includeOverlays = a.TryGetProperty("include_overlays", out var io) && io.ValueKind == JsonValueKind.True;
			}
			if (string.IsNullOrEmpty(path))
			{
				var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp");
				System.IO.Directory.CreateDirectory(dir);
				path = System.IO.Path.Combine(dir, $"{prefix}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
			}

			// ScreenshotCaptureOsd=true makes EmuHawk's CaptureOSD() compose the
			// video surface (overlay_text/rect/line + OSD) into the PNG instead of
			// the bare core framebuffer.
			_tool.EmuClient!.SetScreenshotOSD(includeOverlays);
			try
			{
				_tool.EmuClient!.Screenshot(path);
			}
			finally
			{
				_tool.EmuClient!.SetScreenshotOSD(false);
			}
			return path;
		}

		// SHA1 of the current rendered frame's PNG. Identical rendered output
		// produces identical bytes (BizHawk's PNG save is deterministic), so the
		// hash is a cheap screen-change detector: equal hashes = same screen,
		// and the agent never has to transfer pixels to compare frames.
		private string FrameHash(JsonElement? args)
		{
			string path = CapturePng(args, "hash", out bool includeOverlays);
			if (!System.IO.File.Exists(path))
				throw new JsonRpc.Error(JsonRpc.Error.INTERNAL_ERROR, $"screenshot did not produce a file: {path}");
			string sha1;
			using (var fs = System.IO.File.OpenRead(path))
			using (var sha = System.Security.Cryptography.SHA1.Create())
				sha1 = BitConverter.ToString(sha.ComputeHash(fs)).Replace("-", "").ToLowerInvariant();
			string uri = RegisterArtifact(path, "image/png", $"frame hash {System.IO.Path.GetFileName(path)}");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["sha1"] = sha1,
				["frame"] = _tool.Emulation!.FrameCount(),
				["path"] = path,
				["include_overlays"] = includeOverlays,
				["resource"] = uri,
			});
		}

		private string SaveState(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			_tool.SaveState!.Save(path);
			return $"state saved: {path}";
		}

		private string LoadState(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			bool ok = _tool.SaveState!.Load(path);
			return ok ? $"state loaded: {path}" : $"failed to load state: {path}";
		}

		private string SaveSlot(JsonElement? args)
		{
			var a = Required(args);
			int slot = RequireInt(a, "slot", 1);
			if (slot is < 1 or > 10) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "slot must be 1..10");
			_tool.SaveState!.SaveSlot(slot);
			return $"state saved to slot {slot}";
		}

		private string LoadSlot(JsonElement? args)
		{
			var a = Required(args);
			int slot = RequireInt(a, "slot", 1);
			if (slot is < 1 or > 10) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "slot must be 1..10");
			bool ok = _tool.SaveState!.LoadSlot(slot);
			return ok ? $"state loaded from slot {slot}" : $"failed to load slot {slot}";
		}

		// ── in-memory core states ─────────────────────────────────────────────
		// IMemorySaveStateApi is NOT registered by the ApiHawk provider, so the
		// plugin reaches the core's real IStatable service instead: reflect the
		// private `Emulator` property on the EmulationApi instance (same pattern
		// as watchpoints), then `ServiceProvider.GetService<IStatable>()`. That's
		// exactly what EmuHawk's StateManager uses for its own savestates, so
		// save/load is deterministic for the core. Scope: CORE state only (CPU +
		// memory) — EmuHawk-side state (framecount, lag count) is NOT restored.
		// Slots are session-local byte arrays, no disk, no 10-slot limit.
		private readonly Dictionary<string, byte[]> _memStates = new();

		private IStatable ResolveStatable()
		{
			var emuApi = _tool.Emulation!;
			var emuProp = emuApi.GetType().GetProperty("Emulator", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
			if (emuProp == null || emuProp.GetValue(emuApi) is not IEmulator emu || emu.ServiceProvider == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "in-memory states unsupported: cannot reach the core's IEmulator via the emulator API");
			var statable = emu.ServiceProvider.GetService<IStatable>();
			if (statable == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "in-memory states unsupported: this core does not expose the IStatable service");
			return statable;
		}

		private byte[] CaptureMemState()
		{
			using var ms = new System.IO.MemoryStream();
			using var bw = new System.IO.BinaryWriter(ms);
			ResolveStatable().SaveStateBinary(bw);
			bw.Flush();
			return ms.ToArray();
		}

		private void RestoreMemState(byte[] state)
		{
			using var ms = new System.IO.MemoryStream(state, false);
			using var br = new System.IO.BinaryReader(ms);
			ResolveStatable().LoadStateBinary(br);
		}

		private string MemStateSave(JsonElement? args)
		{
			var a = Required(args);
			string slot = RequireString(a, "slot");
			if (string.IsNullOrWhiteSpace(slot)) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "slot must be a non-empty name");
			byte[] state = CaptureMemState();
			_memStates[slot] = state;
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["slot"] = slot,
				["size"] = state.Length,
				["states"] = _memStates.Count,
			});
		}

		private string MemStateLoad(JsonElement? args)
		{
			var a = Required(args);
			string slot = RequireString(a, "slot");
			if (!_memStates.TryGetValue(slot, out var state))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"no in-memory state in slot \"{slot}\" (saved: {string.Join(", ", _memStates.Keys)})");
			RestoreMemState(state);
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["slot"] = slot,
				["size"] = state.Length,
			});
		}

		private string MemStateList()
		{
			var states = new List<object?>();
			foreach (var kv in _memStates)
				states.Add(new Dictionary<string, object?> { ["slot"] = kv.Key, ["size"] = kv.Value.Length });
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["states"] = states });
		}

		// ── freeze (emulator cheat engine) ────────────────────────────────────
		// The hex editor's "Freeze" is implemented as Cheat entries in
		// MainForm.CheatList; EmuHawk's main loop pulses the whole list every
		// frame (even while running freely), so frozen values survive the game
		// overwriting them. We drive the same list, with the same semantics:
		//   freeze   = Watch.GenerateWatch(domain, addr, size, Hex, bigEndian)
		//              + new Cheat(watch, value)  (Cheat ctor pulses immediately)
		//   unfreeze = CheatList.RemoveRange(cheats.Where(c => c.Contains(addr)))
		// The CheatCollection is on MainForm; the plugin form's Owner is the
		// MainForm (ToolManager sets form.Owner). Reached via reflection (the
		// plugin cannot reference BizHawk.Client.EmuHawk), same as watchpoints.
		private readonly Func<CheatCollection?>? _cheatListResolver;

		private CheatCollection ResolveCheatList()
		{
			if (_cheatListResolver != null)
			{
				var injected = _cheatListResolver();
				if (injected != null) return injected;
			}
			var owner = _tool.GetType().GetProperty("Owner", BindingFlags.Public | BindingFlags.Instance)?.GetValue(_tool);
			if (owner != null)
			{
				var p = owner.GetType().GetProperty("CheatList", BindingFlags.Public | BindingFlags.Instance);
				if (p?.GetValue(owner) is CheatCollection cl) return cl;
			}
			// fallback: GlobalWin.MainForm in the EmuHawk assembly
			var gwType = Type.GetType("BizHawk.Client.EmuHawk.GlobalWin, BizHawk.Client.EmuHawk");
			var mf = gwType?.GetProperty("MainForm", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
			if (mf != null && mf.GetType().GetProperty("CheatList", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mf) is CheatCollection cl2)
				return cl2;
			throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "freeze unsupported: cannot reach the emulator's cheat list (MainForm.CheatList)");
		}

		// Reaches MemoryApi.DomainList[name] (same reflection as TryBulkWrite)
		// and returns the real MemoryDomain for the cheat Watch. NOTE: the real
		// MemoryDomainList has TWO "Item" indexers (this[int] inherited from
		// ReadOnlyCollection + this[string] declared), so GetProperty("Item")
		// throws AmbiguousMatchException — look the string indexer up by its
		// parameter type instead (found by the 2026-08-03 live QA).
		private static PropertyInfo? FindStringIndexer(Type type)
		{
			foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
			{
				if (p.Name != "Item") continue;
				var ps = p.GetIndexParameters();
				if (ps.Length == 1 && ps[0].ParameterType == typeof(string)) return p;
			}
			return null;
		}

		private MemoryDomain ResolveDomain(string? domainName)
		{
			var memApi = _tool.Memory!;
			var listProp = memApi.GetType().GetProperty("DomainList", BindingFlags.Public | BindingFlags.Instance);
			var list = listProp?.GetValue(memApi);
			var indexer = list == null ? null : FindStringIndexer(list.GetType());
			var md = indexer?.GetValue(list, new object[] { domainName ?? memApi.GetCurrentMemoryDomain() }) as MemoryDomain;
			if (md == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown domain: {domainName ?? memApi.GetCurrentMemoryDomain()}");
			return md;
		}

		private static WatchSize SizeOfWidth(int width) => width switch
		{
			8 => WatchSize.Byte,
			16 => WatchSize.Word,
			_ => WatchSize.DWord,
		};

		private Cheat MakeCheat(MemoryDomain domain, long address, int width, bool bigEndian, int value, string? note)
		{
			var watch = Watch.GenerateWatch(domain, address, SizeOfWidth(width), WatchDisplayType.Hex, bigEndian, note ?? "");
			return new Cheat(watch, value);
		}

		private string FreezeAdd(JsonElement? args)
		{
			var a = Required(args);
			string? note = OptionalString(a, "note");
			var (address, width, domain) = ResolveTarget(a);
			bool bigEndian = ResolveBigEndian(a, domain);
			int length = RequireInt(a, "length", 1);
			if (length is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..4096");
			if (length > 1 && width != 8)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "range freezes use 8-bit entries; set width to 8 (or omit it)");
			long? value = a.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : (long?)null;
			if (length > 1 && value.HasValue && value is < 0 or > 0xFF)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"range fill value must fit a byte (got {value})");
			if (length == 1 && value.HasValue)
			{
				ulong max = width switch { 8 => 0xFFUL, 16 => 0xFFFFUL, _ => 0xFFFFFFFFUL };
				if ((ulong)value.Value > max)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"value {value} does not fit width {width}");
			}

			var md = ResolveDomain(domain);
			if (!md.Writable) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"domain \"{md.Name}\" is not writable");
			var cheats = ResolveCheatList();

			if (length > 1)
			{
				uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
				if (address + length > size)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"range {address}:{address + length} outside domain \"{md.Name}\" (size {size})");
				var list = new List<Cheat>();
				for (var i = 0; i < length; i++)
				{
					long addr = address + i;
					int byteVal = value.HasValue ? (int)value.Value : (int)_tool.Memory!.ReadByte(addr, domain);
					list.Add(MakeCheat(md, addr, 8, bigEndian, byteVal, note));
				}
				cheats.AddRange(list);
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["frozen"] = length,
					["address"] = address,
					["length"] = length,
					["domain"] = md.Name,
					["mode"] = value.HasValue ? "fill" : "snapshot",
				});
			}

			ulong current = width switch
			{
				8 => _tool.Memory!.ReadByte(address, domain),
				16 => ReadValue(address, 16, domain, bigEndian),
				_ => ReadValue(address, 32, domain, bigEndian),
			};
			int val = width switch
			{
				8 => value.HasValue ? (int)value.Value : (int)current,
				16 => value.HasValue ? (int)value.Value : (int)current,
				_ => value.HasValue ? unchecked((int)(uint)value.Value) : unchecked((int)current),
			};
			cheats.Add(MakeCheat(md, address, width, bigEndian, val, note));
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["note"] = note,
				["address"] = address,
				["width"] = width,
				["value"] = val,
				["domain"] = md.Name,
				["endianness"] = EndianName(bigEndian),
			});
		}

		private string FreezeRemove(JsonElement? args)
		{
			var a = Required(args);
			var cheats = ResolveCheatList();
			string? note = OptionalString(a, "note");
			long? start = null;
			long end = 0;
			string? domain = null;
			if (note == null)
			{
				var (masked, _, dom) = ResolveTarget(a); // validates/masks like reads
				start = masked;
				domain = dom;
				int length = RequireInt(a, "length", 1);
				if (length is < 1 or > 4096) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "length must be 1..4096");
				end = masked + length;
			}

			var toRemove = new List<Cheat>();
			foreach (var cheat in cheats)
			{
				if (cheat.IsSeparator) continue;
				if (note != null)
				{
					if (cheat.Name == note) toRemove.Add(cheat);
				}
				else if (cheat.Domain.Name == (domain ?? _tool.Memory!.GetCurrentMemoryDomain())
					&& cheat.Address >= start && cheat.Address < end)
				{
					toRemove.Add(cheat);
				}
			}
			cheats.RemoveRange(toRemove);
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["removed"] = toRemove.Count });
		}

		private string FreezeList()
		{
			var cheats = ResolveCheatList();
			var list = new List<object?>();
			foreach (var c in cheats)
			{
				if (c.IsSeparator) continue;
				list.Add(new Dictionary<string, object?>
				{
					["name"] = c.Name,
					["domain"] = c.Domain.Name,
					["address"] = c.Address,
					["width"] = c.Size switch { WatchSize.Byte => 8, WatchSize.Word => 16, _ => 32 },
					["value"] = c.Value,
					["endianness"] = c.BigEndian == true ? "big" : "little",
					["enabled"] = c.Enabled,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["freezes"] = list, ["count"] = list.Count });
		}

		private string FreezeClear()
		{
			var cheats = ResolveCheatList();
			int count = cheats.Count;
			cheats.Clear();
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["cleared"] = count });
		}

		// shared by the write tools: registers the written value(s) as a freeze
		private void FreezeWritten(long address, int width, string? domain, bool bigEndian, ulong value)
		{
			var md = ResolveDomain(domain);
			int val = width switch
			{
				8 => (int)value,
				16 => (int)value,
				_ => unchecked((int)(uint)value),
			};
			ResolveCheatList().Add(MakeCheat(md, address, width, bigEndian, val, null));
		}

		// ── Lua ────────────────────────────────────────────────────────────────
		// The Lua runtime host (LuaLibraries) lives in BizHawk.Client.Common and
		// is owned by the Lua Console tool (field "LuaImp"). IToolApi.GetTool
		// loads/instantiates the console (registered ApiHawk API), then we reach
		// the host by reflecting the single private field — the same pattern as
		// watchpoints. Scripts added to ScriptList are pumped by EmuHawk's main
		// loop (ResumeScripts + frame events every frame), so they run even when
		// emulation runs freely, with zero plugin involvement.
		private readonly Func<LuaLibraries?>? _luaResolver;

		private LuaLibraries ResolveLua()
		{
			if (_luaResolver != null)
			{
				var injected = _luaResolver();
				if (injected != null) return injected;
			}
			var console = _tool.ToolApi?.GetTool("LuaConsole");
			if (console != null)
			{
				var f = console.GetType().GetField("LuaImp", BindingFlags.NonPublic | BindingFlags.Instance);
				if (f?.GetValue(console) is LuaLibraries lua) return lua;
			}
			throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "lua unsupported: cannot reach the emulator's Lua runtime (Lua Console not available)");
		}

		private static string LuaValueText(object? value) => value switch
		{
			null => "nil",
			string s => s,
			double d => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
			bool b => b ? "true" : "false",
			_ => value.ToString() ?? "nil",
		};

		// Host-side paths: EmuHawk owns the filesystem, and on a Windows host
		// an agent driving it from WSL passes /mnt/f/... paths. Convert WSL
		// mount paths to Windows drive paths (and the reverse when the host is
		// Linux/Mono and the caller passes C:\...). Applied to every tool that
		// takes a host-side path (open_rom, save/load_state, screenshot, ...).
		public static string? NormalizeHostPath(string? path, bool windowsHost)
		{
			if (string.IsNullOrEmpty(path)) return path;
			if (windowsHost)
			{
				var m = System.Text.RegularExpressions.Regex.Match(path, @"^/mnt/([a-zA-Z])/(.*)$");
				if (m.Success)
					return $"{char.ToUpperInvariant(m.Groups[1].Value[0])}:\\{m.Groups[2].Value.Replace('/', '\\')}";
			}
			else
			{
				var m = System.Text.RegularExpressions.Regex.Match(path, @"^([a-zA-Z]):[\\/](.*)$");
				if (m.Success)
					return $"/mnt/{char.ToLowerInvariant(m.Groups[1].Value[0])}/{m.Groups[2].Value.Replace('\\', '/')}";
			}
			return path;
		}

		private static bool IsWindowsHost() => Environment.OSVersion.Platform == PlatformID.Win32NT;

		private string LuaExec(JsonElement? args)
		{
			var a = Required(args);
			string code = RequireString(a, "code");
			var lua = ResolveLua();
			try
			{
				var results = lua.ExecuteString(code);
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["executed"] = true,
					["result"] = results.Select(LuaValueText).ToArray(),
				});
			}
			catch (Exception e)
			{
				// a Lua error (syntax/runtime) is a script outcome, not a server fault
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["executed"] = false,
					["error"] = e.Message,
				});
			}
		}

		private LuaFile? FindLuaFile(LuaLibraries lua, string path)
		{
			string absolute = System.IO.Path.GetFullPath(path);
			return lua.ScriptList.FirstOrDefault(f => !f.IsSeparator && System.IO.Path.GetFullPath(f.Path).Equals(absolute, StringComparison.OrdinalIgnoreCase));
		}

		private string LuaLoad(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			if (!System.IO.File.Exists(path))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"script file not found: {path}");
			var lua = ResolveLua();
			var existing = FindLuaFile(lua, path);
			if (existing != null)
			{
				if (!existing.Enabled)
					existing.Start(lua.SpawnCoroutineAndSandbox(path));
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["path"] = path,
					["loaded"] = true,
					["enabled"] = existing.Enabled,
				});
			}
			var file = new LuaFile(path, () => { });
			lua.ScriptList.Add(file);
			file.Start(lua.SpawnCoroutineAndSandbox(path));
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["path"] = path,
				["loaded"] = true,
				["enabled"] = true,
				["scripts"] = lua.ScriptList.Count,
			});
		}

		private string LuaUnload(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			var lua = ResolveLua();
			var file = FindLuaFile(lua, path);
			if (file == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"script not loaded: {path}");
			file.Stop();
			lua.ScriptList.Remove(file);
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["removed"] = path });
		}

		private string LuaEnable(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			var lua = ResolveLua();
			var file = FindLuaFile(lua, path);
			if (file == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"script not loaded: {path}");
			if (!file.Enabled) file.Start(lua.SpawnCoroutineAndSandbox(path));
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["path"] = path, ["enabled"] = file.Enabled });
		}

		private string LuaDisable(JsonElement? args)
		{
			var a = Required(args);
			string path = NormalizeHostPath(RequireString(a, "path"), IsWindowsHost())!;
			var lua = ResolveLua();
			var file = FindLuaFile(lua, path);
			if (file == null)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"script not loaded: {path}");
			file.Stop();
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["path"] = path, ["enabled"] = file.Enabled });
		}

		private string LuaList()
		{
			var lua = ResolveLua();
			var list = new List<object?>();
			foreach (var file in lua.ScriptList)
			{
				if (file.IsSeparator) continue;
				list.Add(new Dictionary<string, object?>
				{
					["path"] = file.Path,
					["enabled"] = file.Enabled,
					["paused"] = file.Paused,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["scripts"] = list, ["count"] = list.Count });
		}

		// Agent-friendly dump of the Lua API docs: the same chain that generates
		// the tasvideos.org LuaFunctions page ([LuaMethod] attributes →
		// LuaLibraries.Docs), serialized as JSON with signatures and examples.
		// Served live from the running emulator, so it matches the installed
		// build exactly (and includes Example, which the wiki page omits).
		private string LuaDocsJson(string? library)
		{
			var lua = ResolveLua();
			var groups = lua.Docs
				.Where(f => library == null || f.Library.Equals(library, StringComparison.OrdinalIgnoreCase))
				.GroupBy(f => f.Library)
				.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
			var result = new List<object?>();
			int count = 0;
			foreach (var g in groups)
			{
				var functions = g.OrderBy(f => f.Name).Select(f =>
				{
					count++;
					return (object?)new Dictionary<string, object?>
					{
						["name"] = f.Name,
						["signature"] = $"{f.ReturnType} {f.Library}.{f.Name}{f.ParameterList}",
						["description"] = f.Description,
						["example"] = f.Example,
						["deprecated"] = f.IsDeprecated,
					};
				}).ToList();
				result.Add(new Dictionary<string, object?>
				{
					["library"] = g.Key,
					["description"] = g.First().LibraryDescription,
					["functions"] = functions,
				});
			}
			if (library != null && result.Count == 0)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown Lua library: {library} (available: {string.Join(", ", lua.Docs.Select(d => d.Library).Distinct().OrderBy(n => n))})");
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["count"] = count,
				["libraries"] = result,
			});
		}

		private string LuaDocs(JsonElement? args)
		{
			string? library = null;
			if (args is { } a && a.ValueKind == JsonValueKind.Object) library = OptionalString(a, "library");
			return LuaDocsJson(library);
		}

		private string OverlayText(JsonElement? args)
		{
			var a = Required(args);
			int x = RequireInt(a, "x", 0);
			int y = RequireInt(a, "y", 0);
			string text = RequireString(a, "text");
			var color = ParseColor(a);
			int? fontSize = a.TryGetProperty("fontsize", out var fs) && fs.ValueKind == JsonValueKind.Number ? fs.GetInt32() : null;
			// draw on the Client (video overlay) surface — EmuCore draws into the
			// core framebuffer, which is not visible in the EmuHawk window.
			_overlays.Add(() =>
			{
				_tool.Gui!.WithSurface(DisplaySurfaceID.Client, gui =>
					gui.DrawString(x, y, text, color, null, fontSize, null, null, "Left", "Top"));
			});
			RedrawOverlays();
			return $"overlay text drawn (overlay count: {_overlays.Count})";
		}

		private string OverlayRect(JsonElement? args)
		{
			var a = Required(args);
			// single rect, or a list of rects in one call: {rects: [{x,y,width,height,color,fill}...]}
			if (a.TryGetProperty("rects", out var rects) && rects.ValueKind == JsonValueKind.Array)
			{
				foreach (var r in rects.EnumerateArray())
				{
					int x = RequireInt(r, "x", 0);
					int y = RequireInt(r, "y", 0);
					int width = RequireInt(r, "width", 0);
					int height = RequireInt(r, "height", 0);
					var line = ParseColor(r);
					var fill = ParseColorArg(r, "fill");
					AddOverlayRect(x, y, width, height, line, fill);
				}
			}
			else
			{
				int x = RequireInt(a, "x", 0);
				int y = RequireInt(a, "y", 0);
				int width = RequireInt(a, "width", 0);
				int height = RequireInt(a, "height", 0);
				var line = ParseColor(a);
				var fill = ParseColorArg(a, "fill");
				AddOverlayRect(x, y, width, height, line, fill);
			}
			RedrawOverlays();
			return $"overlay rect drawn (overlay count: {_overlays.Count})";
		}

		private void AddOverlayRect(int x, int y, int width, int height, System.Drawing.Color? line, System.Drawing.Color? fill)
		{
			_overlays.Add(() =>
			{
				_tool.Gui!.WithSurface(DisplaySurfaceID.Client, gui =>
					gui.DrawRectangle(x, y, width, height, line, fill));
			});
		}

		private string OverlayLine(JsonElement? args)
		{
			var a = Required(args);
			// single line, or a list in one call: {lines: [{x1,y1,x2,y2,color}...]}
			if (a.TryGetProperty("lines", out var lines) && lines.ValueKind == JsonValueKind.Array)
			{
				foreach (var l in lines.EnumerateArray())
				{
					int x1 = RequireInt(l, "x1", 0);
					int y1 = RequireInt(l, "y1", 0);
					int x2 = RequireInt(l, "x2", 0);
					int y2 = RequireInt(l, "y2", 0);
					var color = ParseColor(l);
					AddOverlayLine(x1, y1, x2, y2, color);
				}
			}
			else
			{
				int x1 = RequireInt(a, "x1", 0);
				int y1 = RequireInt(a, "y1", 0);
				int x2 = RequireInt(a, "x2", 0);
				int y2 = RequireInt(a, "y2", 0);
				var color = ParseColor(a);
				AddOverlayLine(x1, y1, x2, y2, color);
			}
			RedrawOverlays();
			return $"overlay line drawn (overlay count: {_overlays.Count})";
		}

		private void AddOverlayLine(int x1, int y1, int x2, int y2, System.Drawing.Color? color)
		{
			_overlays.Add(() =>
			{
				_tool.Gui!.WithSurface(DisplaySurfaceID.Client, gui =>
					gui.DrawLine(x1, y1, x2, y2, color));
			});
		}

		private string ClearOverlay()
		{
			_overlays.Clear();
			_tool.Gui!.WithSurface(DisplaySurfaceID.Client, gui => gui.ClearGraphics());
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

		private string MovieStart(JsonElement? args)
		{
			// with a path: load that movie file and start playback from frame 0;
			// without: start recording a new movie for the currently loaded ROM.
			string path = "";
			if (args is { } a && a.ValueKind == JsonValueKind.Object) path = NormalizeHostPath(OptionalString(a, "path"), IsWindowsHost()) ?? "";
			bool ok = _tool.Movie!.PlayFromStart(path);
			return ok
				? (string.IsNullOrEmpty(path) ? "movie started (recording)" : $"movie loaded and playing: {path}")
				: $"failed to start movie{(string.IsNullOrEmpty(path) ? "" : $": {path}")}";
		}

		private string MovieSave(JsonElement? args)
		{
			string path = "";
			if (args is { } a && a.ValueKind == JsonValueKind.Object) path = NormalizeHostPath(OptionalString(a, "path"), IsWindowsHost()) ?? "";
			_tool.Movie!.Save(path);
			return string.IsNullOrEmpty(path) ? "movie saved" : $"movie saved: {path}";
		}

		private string MovieStop(JsonElement? args)
		{
			_tool.Movie!.Stop();
			return "movie stopped";
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

		private sealed class Watcher
		{
			public string Name = "";
			public long Address;
			public int Width;
			public string? Domain;
			public bool BigEndian;
			public ulong? Last;
		}

		private readonly List<Watcher> _watches = new();

		// ── symbols ────────────────────────────────────────────────────────────
		// name → (address, width, domain). Lets agents use names from Ghidra /
		// fixtures in read_memory/write_memory/read_many instead of raw addresses.

		private sealed class Symbol
		{
			public long Address;
			public int Width;
			public string? Domain;
			public string Namespace = DefaultNamespace;
		}

		private readonly Dictionary<string, Symbol> _symbols = new(StringComparer.OrdinalIgnoreCase);

		private string SymbolsSet(JsonElement? args)
		{
			var a = Required(args);
			if (!a.TryGetProperty("symbols", out var syms) || syms.ValueKind != JsonValueKind.Array)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "symbols must be an array");
			if (syms.GetArrayLength() is < 1 or > 4096)
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "symbols must contain 1..4096 entries");

			string ns = a.TryGetProperty("namespace", out var nsEl) && nsEl.ValueKind == JsonValueKind.String ? nsEl.GetString()! : DefaultNamespace;
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
				_symbols[name] = new Symbol { Address = address, Width = width, Domain = domain, Namespace = ns };
				added++;
			}
			SaveSymbols();
			return $"registered {added} symbol(s) in \"{ns}\" (persisted)";
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
					["namespace"] = s.Namespace,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["symbols"] = list });
		}

		private string SymbolsClear(JsonElement? args)
		{
			int n = 0;
			if (args is { } a && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("namespace", out var nsEl) && nsEl.ValueKind == JsonValueKind.String)
			{
				string ns = nsEl.GetString()!;
				var doomed = new List<string>();
				foreach (var kv in _symbols) if (kv.Value.Namespace == ns) doomed.Add(kv.Key);
				n = doomed.Count;
				foreach (var k in doomed) _symbols.Remove(k);
				SaveSymbols();
				return $"cleared {n} symbol(s) from \"{ns}\" (persisted)";
			}
			n = _symbols.Count;
			_symbols.Clear();
			SaveSymbols();
			return $"cleared {n} symbol(s) (persisted)";
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
			bool bigEndian = ResolveBigEndian(a, domain);
			_watches.Add(new Watcher { Name = name, Address = address, Width = width, Domain = domain, BigEndian = bigEndian });
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
					["endianness"] = EndianName(w.BigEndian),
					["value"] = ReadWatchValue(w),
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["watchers"] = watches });
		}

		private string WatchRead(JsonElement? args)
		{
			bool compact = args is { } a && a.TryGetProperty("compact", out var c) && c.ValueKind == JsonValueKind.True;
			var watches = new List<object?>();
			var names = new List<object?>();
			var values = new List<object?>();
			var changed = new List<object?>();
			foreach (var w in _watches)
			{
				ulong value = ReadWatchValue(w);
				bool isChanged = w.Last != null && w.Last != value;
				w.Last = value;
				names.Add(w.Name);
				values.Add(value);
				changed.Add(isChanged);
				watches.Add(new Dictionary<string, object?>
				{
					["name"] = w.Name,
					["value"] = value,
					["endianness"] = EndianName(w.BigEndian),
					["changed"] = isChanged,
				});
			}
			if (compact)
			{
				// three aligned arrays (names, values, changed) — the caller
				// knows the watchers; avoids per-entry object overhead
				return JsonRpc.Pretty(new Dictionary<string, object?>
				{
					["names"] = names,
					["values"] = values,
					["changed"] = changed,
				});
			}
			return JsonRpc.Pretty(new Dictionary<string, object?> { ["watchers"] = watches });
		}

		private ulong ReadWatchValue(Watcher w)
		{
			return w.Width switch
			{
				8 => _tool.Memory!.ReadByte(w.Address, w.Domain),
				16 => ReadValue(w.Address, 16, w.Domain, w.BigEndian),
				_ => ReadValue(w.Address, 32, w.Domain, w.BigEndian),
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
			int contextBytes = 0;
			if (args is { } a && a.ValueKind == JsonValueKind.Object)
			{
				timeout = RequireInt(a, "timeout_frames", 600);
				contextBytes = RequireInt(a, "context_bytes", 0);
			}
			if (timeout is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "timeout_frames must be 1..600");
			if (contextBytes is < 0 or > 512) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "context_bytes must be 0..512");
			if (_watchpoints.Count == 0) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "no watchpoints registered; add one with bizhawk_watchpoint_add first");

			_wpFired = false;
			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			int frames = 0;
			try
			{
				for (; frames < timeout; frames++)
				{
					AdvanceFrame();
					if (_wpFired) break;
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			bool matched = _wpFired;
			var result = new Dictionary<string, object?>
			{
				["matched"] = matched,
				["frames"] = matched ? frames + 1 : frames,
				["watchpoint"] = _wpName,
				["type"] = matched ? _watchpoints.Find(w => w.Name == _wpName)?.Type.ToString().ToLowerInvariant() : null,
				["address"] = _wpAddr,
				["value"] = _wpValue,
				["framecount"] = _tool.Emulation!.FrameCount(),
			};

			if (matched && contextBytes > 0)
			{
				result["context_bytes"] = contextBytes;
				result["registers"] = _tool.Emulation!.GetRegisters();
				var regs = _tool.Emulation!.GetRegisters();
				uint pc = (uint)FindRegister(regs, "PC");
				try
				{
					var (disasm, _) = _tool.Emulation!.Disassemble(pc);
					result["pc"] = pc;
					result["instruction"] = disasm;
				}
				catch
				{
					// disasm at an odd address can fail; don't fail the whole wait
					result["pc"] = pc;
					result["instruction"] = null;
				}

				// dump bytes around the hit address on the watchpoint's scope
				var wp = _watchpoints.Find(w => w.Name == _wpName);
				if (wp != null)
				{
					long start = (long)Math.Max(0, (long)_wpAddr - contextBytes / 2);
					int half = contextBytes / 2;
					var raw = _tool.Memory!.ReadByteRange(start, Math.Min(contextBytes, 512), wp.Scope);
					var sb = new System.Text.StringBuilder();
					for (var i = 0; i < raw.Count; i++) sb.Append(raw[i].ToString("X2")).Append(' ');
					result["context"] = new Dictionary<string, object?>
					{
						["start"] = start,
						["bytes"] = sb.ToString().TrimEnd(),
						["hit_offset"] = (long)_wpAddr - start,
					};
				}
			}

			return JsonRpc.Pretty(result);
		}

		private sealed class MemoryCallbackImpl : IMemoryCallback
		{
			public MemoryCallbackType Type { get; init; }
			public string Name { get; init; } = "";
			public MemoryCallbackDelegate Callback { get; init; } = (_, _, _) => null;
			public uint? Address { get; init; }
			// must be non-null, or the core's Call() match
			// (cb.Address == (addr & cb.AddressMask)) never fires for
			// address-specific watchpoints. BizHawk's MemoryCallback ctor
			// defaults this to 0xFFFFFFFF.
			public uint? AddressMask => 0xFFFFFFFF;
			public string Scope { get; init; } = "";
		}

		private string WaitUntil(JsonElement? args)
		{
			var a = Required(args);
			int timeout = RequireInt(a, "timeout_frames", 600);
			if (timeout is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "timeout_frames must be 1..600");

			// multi-condition mode: wait until ALL conditions hold on the same
			// frame (AND), so nested single waits are no longer needed
			if (a.TryGetProperty("conditions", out var condEl) && condEl.ValueKind == JsonValueKind.Array)
				return WaitUntilConditions(a, condEl, timeout);

			// single-condition mode (unchanged semantics)
			var (address, width, domain) = ResolveTarget(a);
			string op = a.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "eq";
			if (op is not ("eq" or "ne" or "lt" or "gt" or "le" or "ge")) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown op: {op}");
			ulong value = RequireULong(a, "value");

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			bool bigEndian = ResolveBigEndian(a, domain);
			ulong current = 0;
			int frames = 0;
			try
			{
				for (; frames < timeout; frames++)
				{
					AdvanceFrame();
					current = width switch
					{
						8 => _tool.Memory!.ReadByte(address, domain),
						16 => ReadValue(address, 16, domain, bigEndian),
						_ => ReadValue(address, 32, domain, bigEndian),
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
				["endianness"] = EndianName(bigEndian),
				["conditions"] = new[]
				{
					new Dictionary<string, object?>
					{
						["address"] = address,
						["op"] = op,
						["value"] = value,
						["current"] = current,
						["matched"] = Compare(op, current, value),
						["endianness"] = EndianName(bigEndian),
					},
				},
				["framecount"] = _tool.Emulation!.FrameCount(),
			});
		}

		private string WaitUntilConditions(JsonElement a, JsonElement condEl, int timeout)
		{
			int n = condEl.GetArrayLength();
			if (n is < 1 or > 32) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "conditions must contain 1..32 entries");
			var conds = new List<(long Address, int Width, string? Domain, bool BigEndian, string Op, ulong Value, ulong Current)>(n);
			foreach (var el in condEl.EnumerateArray())
			{
				if (el.ValueKind != JsonValueKind.Object)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "each condition must be an object with address/name + value");
				var (address, width, domain) = ResolveTarget(el);
				string op = el.TryGetProperty("op", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "eq";
				if (op is not ("eq" or "ne" or "lt" or "gt" or "le" or "ge")) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown op: {op}");
				ulong value = RequireULong(el, "value");
				conds.Add((address, width, domain, ResolveBigEndian(el, domain), op, value, 0));
			}

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			int frames = 0;
			try
			{
				for (; frames < timeout; frames++)
				{
					AdvanceFrame();
					bool all = true;
					for (var i = 0; i < conds.Count; i++)
					{
						var c = conds[i];
						ulong cur = c.Width switch
						{
							8 => _tool.Memory!.ReadByte(c.Address, c.Domain),
							16 => ReadValue(c.Address, 16, c.Domain, c.BigEndian),
							_ => ReadValue(c.Address, 32, c.Domain, c.BigEndian),
						};
						conds[i] = (c.Address, c.Width, c.Domain, c.BigEndian, c.Op, c.Value, cur);
						if (!Compare(c.Op, cur, c.Value)) all = false;
					}
					if (all) break;
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			bool matched = frames < timeout;
			var results = new List<object?>(conds.Count);
			foreach (var c in conds)
			{
				results.Add(new Dictionary<string, object?>
				{
					["address"] = c.Address,
					["op"] = c.Op,
					["value"] = c.Value,
					["current"] = c.Current,
					["matched"] = Compare(c.Op, c.Current, c.Value),
					["endianness"] = EndianName(c.BigEndian),
					["domain"] = c.Domain ?? _tool.Memory!.GetCurrentMemoryDomain(),
				});
			}

			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["matched"] = matched,
				["frames"] = matched ? frames + 1 : frames,
				["conditions"] = results,
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

		// Same polling loop as WaitUntil, but with "first change frame"
		// semantics: baseline = the value at call time, no target value needed.
		// Catches dynamic structures (framecounters, state flags) without
		// knowing what they'll become.
		private string WatchChange(JsonElement? args)
		{
			var a = Required(args);
			var (address, width, domain) = ResolveTarget(a);
			int timeout = RequireInt(a, "timeout_frames", 600);
			if (timeout is < 1 or > 600) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "timeout_frames must be 1..600");

			bool wasPaused = _tool.EmuClient!.IsPaused();
			if (wasPaused) _tool.EmuClient!.Unpause();

			bool bigEndian = ResolveBigEndian(a, domain);
			ulong initial = width switch
			{
				8 => _tool.Memory!.ReadByte(address, domain),
				16 => ReadValue(address, 16, domain, bigEndian),
				_ => ReadValue(address, 32, domain, bigEndian),
			};
			ulong current = initial;
			int frames = 0;
			try
			{
				for (; frames < timeout; frames++)
				{
					AdvanceFrame();
					current = width switch
					{
						8 => _tool.Memory!.ReadByte(address, domain),
						16 => ReadValue(address, 16, domain, bigEndian),
						_ => ReadValue(address, 32, domain, bigEndian),
					};
					if (current != initial) break;
				}
			}
			finally
			{
				if (wasPaused) _tool.EmuClient!.Pause();
			}

			bool changed = current != initial;
			return JsonRpc.Pretty(new Dictionary<string, object?>
			{
				["changed"] = changed,
				["frames"] = changed ? frames + 1 : frames,
				["initial"] = initial,
				["value"] = current,
				["address"] = address,
				["endianness"] = EndianName(bigEndian),
				["framecount"] = _tool.Emulation!.FrameCount(),
			});
		}

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
					AdvanceFrame();
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

		// Endianness resolution for a call, in precedence order:
		//   1. explicit "endianness": "big" | "little" | "auto" param
		//   2. a global bizhawk_set_big_endian override
		//   3. the DOMAIN's native endianness (e.g. 68K RAM big vs Z80 RAM
		//      little on Genesis — both live in the same system)
		private bool ResolveBigEndian(JsonElement? args, string? domain)
		{
			if (args is { } a && a.TryGetProperty("endianness", out var v) && v.ValueKind == JsonValueKind.String)
			{
				switch (v.GetString())
				{
					case "big": return true;
					case "little": return false;
					case "auto": break;
					default: throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, "endianness must be \"big\", \"little\" or \"auto\"");
				}
			}
			if (_bigEndianOverride is { } o) return o;
			return DomainIsBigEndian(domain);
		}

		private bool DomainIsBigEndian(string? domain)
		{
			// Z80-family memory is little-endian even on big-endian systems
			// (Genesis: sound CPU). Everything else follows the main CPU.
			if (domain != null && domain.IndexOf("Z80", StringComparison.OrdinalIgnoreCase) >= 0) return false;
			return SystemIsBigEndian(_tool.Emulation!.GetSystemId());
		}

		private static string EndianName(bool bigEndian) => bigEndian ? "big" : "little";

		// Read/write multi-byte values deterministically (raw bytes + explicit
		// endianness) so results never depend on ApiHawk's global SetBigEndian
		// state — a per-call "endianness" always wins.
		private ulong ReadValue(long address, int width, string? domain, bool bigEndian)
		{
			var raw = _tool.Memory!.ReadByteRange(address, width / 8, domain);
			return BytesToValue(raw, 0, width / 8, bigEndian);
		}

		private void WriteValue(long address, int width, string? domain, ulong value, bool bigEndian)
		{
			_tool.Memory!.WriteByteRange(address, ValueToBytes(value, width / 8, bigEndian), domain);
		}

		private static byte[] ValueToBytes(ulong value, int bytesPer, bool bigEndian)
		{
			var b = new byte[bytesPer];
			for (int i = 0; i < bytesPer; i++)
			{
				int shift = bigEndian ? (bytesPer - 1 - i) * 8 : i * 8;
				b[i] = (byte)(value >> shift);
			}
			return b;
		}

		private static long SignExtend(ulong v, int bytesPer)
		{
			int bits = bytesPer * 8;
			ulong sign = 1UL << (bits - 1);
			if ((v & sign) != 0) v |= ulong.MaxValue << bits;
			return (long)v;
		}

		private float ReadFloatRaw(long address, string? domain, bool bigEndian)
		{
			var raw = _tool.Memory!.ReadByteRange(address, 4, domain);
			var bytes = new byte[] { raw[0], raw[1], raw[2], raw[3] };
			if (bigEndian) Array.Reverse(bytes);
			return BitConverter.ToSingle(bytes, 0);
		}

		private void WriteFloatRaw(long address, string? domain, float value, bool bigEndian)
		{
			var bytes = BitConverter.GetBytes(value);
			if (bigEndian) Array.Reverse(bytes);
			_tool.Memory!.WriteByteRange(address, bytes, domain);
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
				["path"] = a.Path,
			});
			}
			resources.Add(new Dictionary<string, object?>
			{
				["uri"] = "bizhawk://lua-docs",
				["name"] = "Lua API documentation (agent-friendly JSON)",
				["mimeType"] = "application/json",
			});
			return new Dictionary<string, object?> { ["resources"] = resources };
		}

		public Dictionary<string, object?> ReadResource(string uri)
		{
			// bizhawk://lua-docs and bizhawk://lua-docs/{library} — live dump of
			// the Lua API docs as JSON (the tasvideos LuaFunctions source chain)
			if (uri.StartsWith("bizhawk://lua-docs", StringComparison.Ordinal))
			{
				string? library = null;
				if (uri.Length > "bizhawk://lua-docs".Length)
				{
					library = Uri.UnescapeDataString(uri.Substring("bizhawk://lua-docs/".Length));
				}
				string json = _ui.Invoke(() => LuaDocsJson(library));
				return new Dictionary<string, object?>
				{
					["contents"] = new List<object?>
					{
						new Dictionary<string, object?> { ["uri"] = uri, ["mimeType"] = "application/json", ["blob"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json)) },
					},
				};
			}

			// bizhawk://read/{domain}/{start}:{end} — live read of a memory region
			// (bytes raw, no endianness interpretation). Domain is URL-decoded.
			if (uri.StartsWith("bizhawk://read/", StringComparison.Ordinal))
			{
				return ReadResourceTemplate(uri);
			}

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

		private const long MaxTemplateBytes = 256 * 1024;

		private Dictionary<string, object?> ReadResourceTemplate(string uri)
		{
			byte[] bytes = ReadRangeBytes(uri);
			return new Dictionary<string, object?>
			{
				["contents"] = new List<object?>
				{
					new Dictionary<string, object?>
					{
						["uri"] = uri,
						["mimeType"] = "application/octet-stream",
						["blob"] = Convert.ToBase64String(bytes),
					},
				},
			};
		}

		// Shared by resources/read (base64 JSON) and the raw GET
		// /mcp/read/{domain}/{start}:{end} endpoint (octet-stream bytes).
		public byte[] ReadRangeRaw(string uri) => ReadRangeBytes(uri);

		private byte[] ReadRangeBytes(string uri)
		{
			// uri = bizhawk://read/{domain}/{start}:{end}
			string rest = uri.Substring("bizhawk://read/".Length);
			int slash = rest.IndexOf('/');
			if (slash < 0) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"malformed read URI: {uri} (expected bizhawk://read/<domain>/<start>:<end>)");
			string domain = Uri.UnescapeDataString(rest.Substring(0, slash));
			string range = rest.Substring(slash + 1);
			int colon = range.LastIndexOf(':');
			if (colon < 0) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"malformed read URI: {uri} (expected <start>:<end>)");
			string startPart = range.Substring(0, colon);
			string endPart = range.Substring(colon + 1);
			if (!long.TryParse(startPart, System.Globalization.NumberStyles.HexNumber, null, out long start)
				|| !long.TryParse(endPart, System.Globalization.NumberStyles.HexNumber, null, out long end)
				|| end <= start)
			{
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"malformed read URI: {uri} (start/end must be hex, end > start)");
			}
			long len = end - start;
			if (len > MaxTemplateBytes) throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"read URI too large ({len} bytes; max {MaxTemplateBytes})");

			// read via the UI thread (memory API is not thread-safe off it)
			return _ui.Invoke(() =>
			{
				EnsureEndianness();
				start = ValidateAddress(start, 1, domain);
				long size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
				if (start + len > size)
					throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"range {start:X}:{start + len:X} outside domain \"{domain}\" (size {size})");
				var raw = _tool.Memory!.ReadByteRange(start, (int)len, domain);
				var buf = new byte[len];
				for (var i = 0; i < len; i++) buf[i] = raw[i];
				return buf;
			});
		}

		// Artifact bytes + mime for the raw GET /mcp/artifacts/{id} endpoint;
		// null when the URI isn't a registered artifact (404 upstream).
		public (byte[] Bytes, string Mime, string Name)? ReadArtifactFile(string uri)
		{
			var artifact = _artifacts.Find(a => a.Uri == uri);
			if (artifact == null) return null;
			try
			{
				return (System.IO.File.ReadAllBytes(artifact.Path), artifact.Mime, artifact.Name);
			}
			catch (Exception e)
			{
				throw new JsonRpc.Error(JsonRpc.Error.INTERNAL_ERROR, $"cannot read resource: {e.Message}");
			}
		}

		public Dictionary<string, object?> ListResourceTemplates() =>
			new Dictionary<string, object?>
			{
				["resourceTemplates"] = new List<object?>
				{
					new Dictionary<string, object?>
					{
						["uriTemplate"] = "bizhawk://read/{domain}/{range}",
						["name"] = "Read memory region (binary)",
						["description"] = "Raw bytes from a memory domain, offsets given as a hex range. Example: bizhawk://read/68K%20RAM/ffbc8:ffbd0. No endianness applied.",
					},
					new Dictionary<string, object?>
					{
						["uriTemplate"] = "bizhawk://lua-docs/{library}",
						["name"] = "Lua API documentation for one library (agent-friendly JSON)",
						["description"] = "Functions of one Lua library with signatures + examples, e.g. bizhawk://lua-docs/memory.",
					},
				},
			};

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
			EnsureKnownDomain(domain);
			uint size = _tool.Memory!.GetMemoryDomainSize(domain ?? "");
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

		// ApiHawk's NamedDomainOrCurrent SILENTLY falls back to the current
		// domain when the requested name doesn't exist (a catch that ignores
		// the miss), so a typo'd domain reads the WRONG memory and labels it
		// with the wrong endianness. Reject unknown names up front instead.
		private void EnsureKnownDomain(string? domain)
		{
			if (string.IsNullOrEmpty(domain)) return;
			if (!_tool.Memory!.GetMemoryDomainList().Contains(domain))
				throw new JsonRpc.Error(JsonRpc.Error.INVALID_PARAMS, $"unknown domain: {domain}");
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
