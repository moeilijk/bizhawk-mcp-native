using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BizHawk.Emulation.Common;
using BizHawkMcp;
using BizHawkMcp.Mcp;
using Xunit;

namespace BizHawkMcp.Tests
{
	public class MemoryToolTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public MemoryToolTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Write_then_read_roundtrip()
		{
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":8,\"value\":165}"));
			var res = Parse(_ts.Call("read_memory", TestHelpers.Js("{\"address\":100,\"width\":8}")));
			Assert.Equal((ulong)0xA5, res.GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Write_u16_respects_big_endian_default_on_genesis()
		{
			_ts.Call("get_info", null); // triggers EnsureEndianness for GEN
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827}"));
			// big-endian: high byte first
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Write_u16_little_endian_on_nes()
		{
			_apis.EmulationApi.SystemId = "NES";
			_ts.Call("get_info", null);
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Write_u16_respects_explicit_big_endian_param_on_nes()
		{
			// NES defaults little, but an explicit param flips it for this call
			_apis.EmulationApi.SystemId = "NES";
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"endianness\":\"big\"}"));
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Domain_default_is_little_for_z80_ram()
		{
			// Genesis: the Z80 sound CPU memory is little-endian even though
			// the 68K main memory is big-endian — the domain decides.
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"domain\":\"Z80 RAM\"}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Domain_default_is_big_for_68k_ram()
		{
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"domain\":\"68K RAM\"}"));
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Explicit_little_endian_param_on_z80_sticks_for_call()
		{
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"domain\":\"Z80 RAM\",\"endianness\":\"little\"}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Read_memory_reports_used_endianness()
		{
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("read_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"domain\":\"68K RAM\"}")));
			Assert.Equal((ulong)8, res.GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("endianness").GetString());
			res = Parse(_ts.Call("read_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"domain\":\"Z80 RAM\"}")));
			Assert.Equal((ulong)2048, res.GetProperty("value").GetUInt64());
			Assert.Equal("little", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Invalid_endianness_param_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"address\":0,\"endianness\":\"sideways\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Use_memory_domain_valid_switches()
		{
			var res = _ts.Call("use_memory_domain", TestHelpers.Js("{\"domain\":\"M68K BUS\"}"));
			Assert.Contains("M68K BUS", res);
			Assert.Equal("M68K BUS", _apis.MemoryApi.CurrentDomain);
		}

		[Fact]
		public void Use_memory_domain_unknown_throws_invalid_params()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("use_memory_domain", TestHelpers.Js("{\"domain\":\"NOPE\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("known domains", ex.Message);
			Assert.Contains("68K RAM", ex.Message);
		}

		[Fact]
		public void Set_big_endian_overrides_core_default()
		{
			_ts.Call("get_info", null); // GEN → BE
			_ts.Call("set_big_endian", TestHelpers.Js("{\"enabled\":false}"));
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]); // LE now
			_ts.Call("get_info", null);
			// override sticks: still LE
			_ts.Call("write_memory", TestHelpers.Js("{\"address\":200,\"width\":16,\"value\":24827}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[200]);
		}

		[Fact]
		public void Get_info_reports_endianness_and_pause()
		{
			_apis.EmulationApi.SystemId = "GEN";
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("get_info", null));
			Assert.Equal("big", res.GetProperty("endianness").GetString());
			Assert.True(res.GetProperty("paused").GetBoolean());
			Assert.Equal("GEN", res.GetProperty("system_id").GetString());
		}

		[Fact]
		public void Get_board_info_reports_identifiers()
		{
			var res = Parse(_ts.Call("get_board_info", null));
			Assert.Equal("Genesis", res.GetProperty("board_name").GetString());
			Assert.Equal("NTSC", res.GetProperty("display_type").GetString());
			Assert.Equal("USA", res.GetProperty("game_options").GetProperty("region").GetString());
		}

		[Fact]
		public void Get_info_reports_host_paths()
		{
			var res = Parse(_ts.Call("get_info", null));
			var paths = res.GetProperty("paths");
			Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, paths.GetProperty("install_dir").GetString());
			Assert.Equal(Environment.CurrentDirectory, paths.GetProperty("working_dir").GetString());
			Assert.Equal(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp"), paths.GetProperty("temp_dir").GetString());
			Assert.Equal(Environment.OSVersion.Platform == PlatformID.Win32NT, paths.GetProperty("host_is_windows").GetBoolean());
			Assert.Equal(JsonValueKind.Null, paths.GetProperty("rom_path").ValueKind); // no MainForm in tests
			Assert.Equal(JsonValueKind.Null, paths.GetProperty("rom_dir").ValueKind);
		}

		[Fact]
		public void Get_info_reports_little_on_gb()
		{
			_apis.EmulationApi.SystemId = "GB";
			var res = Parse(_ts.Call("get_info", null));
			Assert.Equal("little", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Invalid_width_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"address\":0,\"width\":7}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Missing_args_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_memory", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Value_out_of_width_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_memory", TestHelpers.Js("{\"address\":0,\"width\":8,\"value\":300}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Address_outside_domain_rejected()
		{
			// 68K RAM fake has size 65536
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"address\":65536,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_memory", TestHelpers.Js("{\"address\":65535,\"width\":16,\"value\":1}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Address_in_domain_accepted()
		{
			_ts.Call("read_memory", TestHelpers.Js("{\"address\":65534,\"width\":16}"));
		}

		[Fact]
		public void Bus_domain_masks_32bit_address_like_hardware()
		{
			// Games (e.g. Kid Chameleon) reference RAM as 0xFFFFxxxx in the
			// disassembly; the 68K has a 24-bit bus so 0xFFFFF832 == 0xFFF832.
			_apis.MemoryApi.Bytes[0xFFF832] = 0xAB;
			var res = Parse(_ts.Call("read_memory", TestHelpers.Js("{\"address\":4294965298,\"domain\":\"M68K BUS\",\"width\":8}")));
			// 4294965298 = 0xFFFFF832
			Assert.Equal((ulong)0xAB, res.GetProperty("value").GetUInt64());

			_ts.Call("write_memory", TestHelpers.Js("{\"address\":4294965298,\"domain\":\"M68K BUS\",\"width\":8,\"value\":205}"));
			Assert.Equal((byte)0xCD, _apis.MemoryApi.Bytes[0xFFF832]);
		}

		[Fact]
		public void Linear_domain_rejects_address_beyond_size_even_with_high_bits()
		{
			// 68K RAM is a linear 64KB domain: 0x10001 is NOT the same as 0x0001
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"address\":65537,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Non_68k_core_keeps_strict_bus_check()
		{
			// Only 68000-family cores (24-bit bus) mask 32-bit addresses; a
			// PSX/NES-style core must keep rejecting out-of-range bus addresses.
			_apis.EmulationApi.SystemId = "PSX";
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"address\":4294965298,\"domain\":\"M68K BUS\",\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void List_memory_domains_returns_names_and_sizes()
		{
			var res = Parse(_ts.Call("list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.Equal((ulong)65536, domains.GetProperty("68K RAM").GetProperty("size").GetUInt64());
			Assert.Equal("68K RAM", res.GetProperty("current").GetString());
		}

		[Fact]
		public void List_memory_domains_reports_bus_base_for_genesis()
		{
			var res = Parse(_ts.Call("list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.Equal((long)0xFF0000, domains.GetProperty("68K RAM").GetProperty("bus_base").GetInt64());
			Assert.Equal((long)0xA00000, domains.GetProperty("Z80 RAM").GetProperty("bus_base").GetInt64());
		}

		[Fact]
		public void List_memory_domains_omits_bus_base_when_unknown()
		{
			_apis.EmulationApi.SystemId = "PSX";
			var res = Parse(_ts.Call("list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.False(domains.GetProperty("68K RAM").TryGetProperty("bus_base", out _));
		}

		[Fact]
		public void Search_memory_finds_exact_byte()
		{
			_apis.MemoryApi.Bytes[10] = 0x42;
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"max_results\":10}")));
			Assert.Equal(2, res.GetProperty("count").GetInt32());
			Assert.Equal((long)10, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
			Assert.Equal((long)100, res.GetProperty("matches")[1].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_respects_max_results()
		{
			for (int i = 0; i < 50; i++) _apis.MemoryApi.Bytes[i] = 0x42;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"max_results\":10}")));
			Assert.Equal(10, res.GetProperty("count").GetInt32());
		}

		[Fact]
		public void Search_memory_restricts_to_addresses()
		{
			_apis.MemoryApi.Bytes[10] = 0x42;
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"addresses\":[10]}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
		}

		[Fact]
		public void Search_memory_u16_respects_big_endian_on_genesis()
		{
			// bytes 00 08 at 100 = 8 in BE, 2048 in LE (the mainFunction regression)
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)100, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_u32_respects_big_endian_on_genesis()
		{
			// bytes 12 34 56 78 at 100 = 0x12345678 in BE, 0x78563412 in LE
			_apis.MemoryApi.Bytes[100] = 0x12;
			_apis.MemoryApi.Bytes[101] = 0x34;
			_apis.MemoryApi.Bytes[102] = 0x56;
			_apis.MemoryApi.Bytes[103] = 0x78;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":305419896,\"width\":32,\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)100, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_respects_little_endian_override()
		{
			// on GEN the override to LE must make 08 00 match value 8
			_apis.MemoryApi.Bytes[100] = 0x08;
			_apis.MemoryApi.Bytes[101] = 0x00;
			_ts.Call("set_big_endian", TestHelpers.Js("{\"enabled\":false}"));
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)100, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_addresses_scan_respects_big_endian()
		{
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"addresses\":[100]}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
		}

		[Fact]
		public void Search_memory_uses_domain_endianness_and_reports_it()
		{
			// same bytes 00 08: big on 68K RAM (=8), little on Z80 RAM (=2048)
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"domain\":\"68K RAM\",\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal("big", res.GetProperty("endianness").GetString());
			res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":2048,\"width\":16,\"domain\":\"Z80 RAM\",\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal("little", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Search_stateful_baseline_then_finds_increases()
		{
			// first stateful call takes the baseline (count 0, baseline true)
			_apis.MemoryApi.Bytes[100] = 5;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"op\":\"gt\"}")));
			Assert.True(res.GetProperty("baseline").GetBoolean());
			Assert.Equal(0, res.GetProperty("count").GetInt32());

			// value increased at 100 → found; the rest stayed 0 → not > 0
			_apis.MemoryApi.Bytes[100] = 9;
			res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"op\":\"gt\"}")));
			Assert.False(res.GetProperty("baseline").GetBoolean());
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)100, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
			Assert.Equal("gt", res.GetProperty("op").GetString());
		}

		[Fact]
		public void Search_stateful_changed_narrows_and_updates_reference()
		{
			_ts.Call("search_memory", TestHelpers.Js("{\"op\":\"changed\"}"));
			_apis.MemoryApi.Bytes[100] = 1;
			_apis.MemoryApi.Bytes[200] = 2;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"op\":\"changed\",\"addresses\":[100,200]}")));
			Assert.Equal(2, res.GetProperty("count").GetInt32());

			// the reference was updated to the state just scanned → no more changes
			res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"op\":\"changed\",\"addresses\":[100,200]}")));
			Assert.Equal(0, res.GetProperty("count").GetInt32());

			// a still-zero address is "unchanged" against the same reference
			res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"op\":\"unchanged\",\"addresses\":[50]}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)50, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_op_compares_against_value_constant()
		{
			_apis.MemoryApi.Bytes[10] = 3;
			_apis.MemoryApi.Bytes[11] = 9;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":8,\"op\":\"lt\",\"range_start\":10,\"range_length\":2}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)10, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_rejects_bad_op_combinations()
		{
			// eq needs a value
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("search_memory", TestHelpers.Js("{\"op\":\"eq\"}")));
			// changed/unchanged need the previous state (no value)
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("search_memory", TestHelpers.Js("{\"value\":1,\"op\":\"changed\"}")));
			// unknown op
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("search_memory", TestHelpers.Js("{\"op\":\"bogus\"}")));
		}

		[Fact]
		public void Hash_region_returns_hash()
		{
			var res = Parse(_ts.Call("hash_region", TestHelpers.Js("{\"address\":0,\"length\":64}")));
			Assert.Equal("deadbeef", res.GetProperty("hash").GetString());
		}

		[Fact]
		public void Signed_read_write_roundtrip()
		{
			_ts.Call("write_signed", TestHelpers.Js("{\"address\":10,\"width\":16,\"value\":-1234}"));
			var res = Parse(_ts.Call("read_signed", TestHelpers.Js("{\"address\":10,\"width\":16}")));
			Assert.Equal(-1234L, res.GetProperty("value").GetInt64());
		}

		[Fact]
		public void Float_read_write_roundtrip()
		{
			_ts.Call("write_float", TestHelpers.Js("{\"address\":20,\"value\":3.5}"));
			var res = Parse(_ts.Call("read_float", TestHelpers.Js("{\"address\":20}")));
			Assert.Equal(3.5f, res.GetProperty("value").GetSingle());
		}

		[Fact]
		public void Read_many_reads_all_items()
		{
			_apis.MemoryApi.Bytes[10] = 0x11;
			_apis.MemoryApi.Bytes[20] = 0x22;
			_apis.MemoryApi.Bytes[30] = 0x33;
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":10,\"width\":8},{\"address\":20,\"width\":8},{\"address\":30,\"width\":8}]}")));
			var reads = res.GetProperty("reads");
			Assert.Equal(3, reads.GetArrayLength());
			Assert.Equal((ulong)0x11, reads[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x33, reads[2].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Read_many_reports_bad_item_without_killing_the_batch()
		{
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":10,\"width\":7},{\"address\":20,\"width\":8}]}")));
			Assert.Equal(1, res.GetProperty("read").GetInt32());
			Assert.Equal(1, res.GetProperty("failed").GetInt32());
			var reads = res.GetProperty("reads");
			Assert.Equal(2, reads.GetArrayLength());
			Assert.Equal(0, reads[0].GetProperty("index").GetInt32());
			Assert.Contains("width", reads[0].GetProperty("error").GetString());
			Assert.Equal((ulong)0, reads[1].GetProperty("value").GetUInt64());
			Assert.Equal(1, reads[1].GetProperty("index").GetInt32());
		}

		[Fact]
		public void Read_many_unknown_symbol_fails_only_that_item()
		{
			// B2 regression: one unknown symbol used to abort the whole batch
			// (INVALID_PARAMS) and lose the valid items.
			_apis.MemoryApi.Bytes[10] = 0x11;
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":10,\"width\":8},{\"name\":\"playerSprPtr\",\"width\":16},{\"address\":30,\"width\":8}]}")));
			Assert.Equal(2, res.GetProperty("read").GetInt32());
			Assert.Equal(1, res.GetProperty("failed").GetInt32());
			var reads = res.GetProperty("reads");
			Assert.Equal("unknown symbol: playerSprPtr", reads[1].GetProperty("error").GetString());
			Assert.Equal((ulong)0x11, reads[0].GetProperty("value").GetUInt64());
			Assert.Equal(JsonValueKind.Null, reads[1].GetProperty("address").ValueKind);
		}

		[Fact]
		public void Read_many_echoes_requested_address_before_bus_masking()
		{
			// B4 regression: request 0x1002024 (not on the 24-bit bus) →
			// response must show both the original and the effective address.
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":16785444,\"width\":16,\"domain\":\"M68K BUS\"}]}")));
			var item = res.GetProperty("reads")[0];
			Assert.Equal(16785444L, item.GetProperty("requested").GetInt64());
			Assert.Equal(8228L, item.GetProperty("address").GetInt64());
		}

		[Fact]
		public void Read_memory_echoes_requested_address_before_bus_masking()
		{
			var res = Parse(_ts.Call("read_memory", TestHelpers.Js("{\"address\":16785444,\"width\":16,\"domain\":\"M68K BUS\"}")));
			Assert.Equal(16785444L, res.GetProperty("requested").GetInt64());
			Assert.Equal(8228L, res.GetProperty("address").GetInt64());
		}

		[Fact]
		public void Read_many_u16_respects_big_endian_on_genesis()
		{
			// mainFunction regression: bytes 00 08 = 8 in BE, 2048 in LE
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":16}]}")));
			Assert.Equal((ulong)8, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Read_many_respects_little_endian_override()
		{
			_apis.MemoryApi.Bytes[100] = 0x08;
			_apis.MemoryApi.Bytes[101] = 0x00;
			_ts.Call("set_big_endian", TestHelpers.Js("{\"enabled\":false}"));
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":16}]}")));
			Assert.Equal((ulong)8, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Read_many_mixes_domains_with_own_endianness()
		{
			// same bytes 00 08: 8 on big 68K RAM, 2048 on little Z80 RAM
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":16,\"domain\":\"68K RAM\"},{\"address\":100,\"width\":16,\"domain\":\"Z80 RAM\"}]}")));
			Assert.Equal((ulong)8, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("reads")[0].GetProperty("endianness").GetString());
			Assert.Equal((ulong)2048, res.GetProperty("reads")[1].GetProperty("value").GetUInt64());
			Assert.Equal("little", res.GetProperty("reads")[1].GetProperty("endianness").GetString());
		}

		[Fact]
		public void Write_range_writes_bytes_in_order()
		{
			_ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"values\":[1,2,3,4]}"));
			Assert.Equal((byte)1, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)4, _apis.MemoryApi.Bytes[103]);
		}

		[Fact]
		public void Write_range_uses_bulk_path_when_domain_is_pointer_backed()
		{
			_apis.MemoryApi.DomainList = new FakeMemoryApi.FakeDomainList(_apis.MemoryApi.Bytes);
			FakeMemoryApi.FakeMemoryDomain.BulkWriteUsed = false;
			_ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"values\":[1,2,3,4,5,6,7,8]}"));
			Assert.True(FakeMemoryApi.FakeMemoryDomain.BulkWriteUsed, "bulk path not taken; logs: " + string.Join(" | ", _apis.Logged));
			Assert.False(_apis.MemoryApi.Bytes.ContainsKey(100));
		}

		[Fact]
		public void Write_range_rejects_out_of_byte_values()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"values\":[1,300]}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Write_many_writes_non_contiguous_values()
		{
			_ts.Call("write_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":8,\"value\":1},{\"address\":200,\"width\":16,\"value\":513},{\"address\":300,\"width\":32,\"value\":65537}]}"));
			// domain default on GEN = big-endian (68K RAM)
			Assert.Equal((byte)1, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x02, _apis.MemoryApi.Bytes[200]);
			Assert.Equal((byte)0x01, _apis.MemoryApi.Bytes[201]);
			Assert.Equal((byte)0x00, _apis.MemoryApi.Bytes[300]);
			Assert.Equal((byte)0x01, _apis.MemoryApi.Bytes[301]);
			Assert.Equal((byte)0x00, _apis.MemoryApi.Bytes[302]);
			Assert.Equal((byte)0x01, _apis.MemoryApi.Bytes[303]);
		}

		[Fact]
		public void Write_many_respects_explicit_little_endian()
		{
			_ts.Call("write_many", TestHelpers.Js("{\"items\":[{\"address\":200,\"width\":16,\"value\":513,\"endianness\":\"little\"},{\"address\":300,\"width\":32,\"value\":65537,\"endianness\":\"little\"}]}"));
			Assert.Equal((byte)0x01, _apis.MemoryApi.Bytes[200]);
			Assert.Equal((byte)0x02, _apis.MemoryApi.Bytes[201]);
			Assert.Equal((byte)0x01, _apis.MemoryApi.Bytes[300]);
			Assert.Equal((byte)0x00, _apis.MemoryApi.Bytes[301]);
			Assert.Equal((byte)0x01, _apis.MemoryApi.Bytes[302]);
			Assert.Equal((byte)0x00, _apis.MemoryApi.Bytes[303]);
		}

		[Fact]
		public void Write_many_accepts_symbol_names()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":50,\"width\":8}]}"));
			_ts.Call("write_many", TestHelpers.Js("{\"items\":[{\"name\":\"hp\",\"value\":99}]}"));
			Assert.Equal((byte)99, _apis.MemoryApi.Bytes[50]);
		}

		[Fact]
		public void Read_range_dumps_hex()
		{
			_apis.MemoryApi.Bytes[100] = 0xAB;
			_apis.MemoryApi.Bytes[101] = 0xCD;
			var res = _ts.Call("read_range", TestHelpers.Js("{\"address\":100,\"length\":2}"));
			Assert.Equal("AB CD", res);
		}

		[Fact]
		public void Read_bulk_returns_base64_of_range()
		{
			for (var i = 0; i < 5; i++) _apis.MemoryApi.Bytes[100 + i] = (byte)(0x10 + i);
			var res = Parse(_ts.Call("read_bulk", TestHelpers.Js("{\"address\":100,\"length\":5,\"domain\":\"68K RAM\"}")));
			Assert.Equal(100L, res.GetProperty("address").GetInt64());
			Assert.Equal(5, res.GetProperty("length").GetInt32());
			byte[] decoded = Convert.FromBase64String(res.GetProperty("base64").GetString()!);
			Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14 }, decoded);
		}

		[Fact]
		public void Read_bulk_accepts_symbol_name()
		{
			_apis.MemoryApi.Bytes[50] = 0x7F;
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":50,\"width\":8}]}"));
			var res = Parse(_ts.Call("read_bulk", TestHelpers.Js("{\"name\":\"hp\",\"length\":1}")));
			Assert.Equal(new byte[] { 0x7F }, Convert.FromBase64String(res.GetProperty("base64").GetString()!));
		}

		[Fact]
		public void Read_bulk_rejects_bad_lengths_and_out_of_domain()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_bulk", TestHelpers.Js("{\"address\":0,\"length\":65537}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			// 64KiB domain: start + length beyond it
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_bulk", TestHelpers.Js("{\"address\":60000,\"length\":10000,\"domain\":\"68K RAM\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("outside domain", ex.Message);
		}

		[Fact]
		public void Write_many_rejects_value_out_of_width_per_item()
		{
			// F1: a bad item must fail only itself — valid items still write.
			var res = Parse(_ts.Call("write_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":8,\"value\":300},{\"address\":101,\"width\":8,\"value\":7}]}")));
			Assert.Equal(1, res.GetProperty("wrote").GetInt32());
			Assert.Equal(1, res.GetProperty("failed").GetInt32());
			var failure = res.GetProperty("failures")[0];
			Assert.Equal(0, failure.GetProperty("index").GetInt32());
			Assert.Equal(100L, failure.GetProperty("address").GetInt64());
			Assert.Contains("does not fit width", failure.GetProperty("reason").GetString());
			Assert.Equal((byte)7, _apis.MemoryApi.Bytes[101]); // valid item still wrote
		}

		[Fact]
		public void Write_many_out_of_range_address_fails_only_that_item()
		{
			var res = Parse(_ts.Call("write_many", TestHelpers.Js("{\"items\":[{\"address\":70000,\"width\":8,\"value\":1},{\"address\":50,\"width\":8,\"value\":2}]}")));
			Assert.Equal(1, res.GetProperty("wrote").GetInt32());
			Assert.Equal(1, res.GetProperty("failed").GetInt32());
			Assert.Contains("outside domain", res.GetProperty("failures")[0].GetProperty("reason").GetString());
			Assert.Equal((byte)2, _apis.MemoryApi.Bytes[50]);
		}

		[Fact]
		public void Write_range_fill_mode_writes_repeated_byte()
		{
			// B1: fill+length clears large regions with a tiny payload
			// (the values-array path aborts in some MCP clients ~1-2 KB).
			var res = Parse(_ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"fill\":0,\"length\":1440}")));
			Assert.Equal(1440, res.GetProperty("wrote").GetInt32());
			Assert.Equal(100L, res.GetProperty("address").GetInt64());
			Assert.Equal(0L, res.GetProperty("fill").GetInt64());
			Assert.Equal((byte)0, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0, _apis.MemoryApi.Bytes[1539]);
			Assert.True(_apis.MemoryApi.Bytes.TryGetValue(1539, out _));
		}

		[Fact]
		public void Write_range_fill_rejects_bad_fill_value()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"fill\":256,\"length\":10}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"fill\":0}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Ram_snapshot_then_diff_finds_changes()
		{
			_apis.MemoryApi.Bytes[10] = 0x01;
			_apis.MemoryApi.Bytes[11] = 0x02;
			_ts.Call("ram_snapshot", TestHelpers.Js("{\"domain\":\"68K RAM\",\"label\":\"t1\"}"));

			_apis.MemoryApi.Bytes[10] = 0xFF;
			_apis.MemoryApi.Bytes[11] = 0xFE;
			_apis.MemoryApi.Bytes[500] = 0xAA;

			var res = Parse(_ts.Call("ram_diff", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
			Assert.Equal("t1", res.GetProperty("label").GetString());
			Assert.Equal(2, res.GetProperty("count").GetInt32());
			// run at 10 (old 0102, new FFFE) coalesced
			var changes = res.GetProperty("changes");
			Assert.Equal((long)10, changes[0].GetProperty("start").GetInt64());
			Assert.Equal(2, changes[0].GetProperty("length").GetInt32());
			Assert.Equal("0102", changes[0].GetProperty("old").GetString());
			Assert.Equal("FFFE", changes[0].GetProperty("new").GetString());
			// single change at 500
			Assert.Equal((long)500, changes[1].GetProperty("start").GetInt64());
		}

		[Fact]
		public void Ram_diff_without_snapshot_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("ram_diff", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Palette_genesis_parses_bgr_to_rgb()
		{
			// CRAM hardware format 0x0RRR0GGG0BBB: R at bits 1-3. R=7 → 0x000E
			// (stored big-endian: 00 0E) → #FF0000
			_apis.MemoryApi.WriteByte(0, 0x00, "CRAM");
			_apis.MemoryApi.WriteByte(1, 0x0E, "CRAM");
			var res = Parse(_ts.Call("read_palette", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal("GEN", res.GetProperty("system").GetString());
			Assert.Equal("#FF0000", res.GetProperty("colors")[0].GetString());
		}

		[Fact]
		public void Palette_genesis_blue_entry_maps_to_blue()
		{
			// B at bits 9-11: B=7 → 0x0E00 (big-endian stored)
			_apis.MemoryApi.WriteByte(0, 0x0E, "CRAM");
			_apis.MemoryApi.WriteByte(1, 0x00, "CRAM");
			var res = Parse(_ts.Call("read_palette", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal("#0000FF", res.GetProperty("colors")[0].GetString());
		}

		[Fact]
		public void Palette_snes_parses_bgr555()
		{
			// CGRAM entry: 0x001F = R=31, G=0, B=0 → #FF0000 (little-endian stored)
			_apis.EmulationApi.SystemId = "SNES";
			_apis.MemoryApi.WriteByte(0, 0x1F, "CGRAM");
			_apis.MemoryApi.WriteByte(1, 0x00, "CGRAM");
			var res = Parse(_ts.Call("read_palette", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal("#FF0000", res.GetProperty("colors")[0].GetString());
		}

		[Fact]
		public void Palette_unsupported_system_rejected()
		{
			_apis.EmulationApi.SystemId = "NES";
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_palette", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Read_plane_renders_tiles_to_png()
		{
			var mem = _apis.MemoryApi;
			// CRAM color 0 = black (0x0000), color 1 = white (0x000E).
			mem.WriteByte(0, 0x00, "CRAM"); mem.WriteByte(1, 0x00, "CRAM");
			mem.WriteByte(2, 0x00, "CRAM"); mem.WriteByte(3, 0x0E, "CRAM");

			// Tile 0 at VRAM 0x0000: every row byte = 0x11 → all pixels = color 1.
			// Tile 1 at VRAM 0x20: all black.
			for (var i = 0; i < 32; i++) mem.WriteByte(i, 0x11, "VRAM");
			for (var i = 0; i < 32; i++) mem.WriteByte(0x20 + i, 0x00, "VRAM");

			// Nametable at 0xC000: entry 0 = tile 0, entry 1 = tile 1 (16-bit BE).
			mem.WriteByte(0xC000, 0x00, "VRAM"); mem.WriteByte(0xC001, 0x00, "VRAM");
			mem.WriteByte(0xC002, 0x00, "VRAM"); mem.WriteByte(0xC003, 0x01, "VRAM");

			var res = Parse(_ts.Call("genesis_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"columns\":2,\"rows\":1}")));
			Assert.Equal((long)0xC000, res.GetProperty("base").GetInt64());
			Assert.Equal(16, res.GetProperty("width").GetInt32());
			Assert.Equal(8, res.GetProperty("height").GetInt32());
			var path = res.GetProperty("path").GetString();
			var uri = res.GetProperty("resource").GetString();
			Assert.StartsWith("bizhawk://", uri);
			Assert.True(System.IO.File.Exists(path));

			// verify the PNG is decodable: signature + IHDR dims + it's a resource
			var bytes = System.IO.File.ReadAllBytes(path);
			Assert.Equal(0x89, bytes[0]);
			Assert.Equal(0x50, bytes[1]);
			Assert.Equal(0x4E, bytes[2]);
			Assert.Equal(0x47, bytes[3]);
			int ihdrW = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
			int ihdrH = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
			Assert.Equal(16, ihdrW);
			Assert.Equal(8, ihdrH);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Read_plane_h_flip_and_palette_select()
		{
			var mem = _apis.MemoryApi;
			// Block 1 (CRAM index 16+): color 1 = red (0x000E).
			mem.WriteByte(32, 0x00, "CRAM"); mem.WriteByte(33, 0x00, "CRAM"); // index 16 = black
			mem.WriteByte(34, 0x00, "CRAM"); mem.WriteByte(35, 0x0E, "CRAM"); // index 17 = red

			// Tile 0: row0 = 0x10 0x00 0x00 0x00 → only leftmost pixel (col 0) = color 1
			mem.WriteByte(0, 0x10, "VRAM"); mem.WriteByte(1, 0x00, "VRAM");
			mem.WriteByte(2, 0x00, "VRAM"); mem.WriteByte(3, 0x00, "VRAM");
			for (var i = 4; i < 32; i++) mem.WriteByte(i, 0x00, "VRAM");

			// nametable entry 0: tile 0, H-flip (bit 11 = 0x800), palette block 1 (bits 13-14 = 0x2000)
			int attr = 0x0000 | 0x0800 | 0x2000;
			mem.WriteByte(0xC000, (byte)(attr >> 8), "VRAM");
			mem.WriteByte(0xC001, (byte)attr, "VRAM");

			var res = Parse(_ts.Call("genesis_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"columns\":1,\"rows\":1}")));
			var path = res.GetProperty("path").GetString();
			Assert.True(System.IO.File.Exists(path));
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Read_plane_rejects_out_of_vram()
		{
			// 0xF100 (61696) + 64*32*2 nametable exceeds 64KB VRAM
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("genesis_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"base\":61696,\"columns\":64,\"rows\":32}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Get_vdp_view_returns_plane_bases()
		{
			var dbg = _apis.EnableWatchpoints();
			dbg.PlaneABase = 0x0000;
			dbg.PlaneBBase = 0xE000;
			var res = Parse(_ts.Call("genesis_get_vdp_view", null));
			Assert.Equal((long)0x0000, res.GetProperty("planeA").GetProperty("base").GetInt64());
			Assert.Equal((long)0xE000, res.GetProperty("planeB").GetProperty("base").GetInt64());
			Assert.Equal(64, res.GetProperty("planeA").GetProperty("width").GetInt32());
		}

		[Fact]
		public void Get_vdp_view_errors_without_core()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("genesis_get_vdp_view", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Read_plane_auto_detects_base_from_core()
		{
			// Kid Chameleon uses plane A at 0x0000 (not the typical 0xC000).
			var dbg = _apis.EnableWatchpoints();
			dbg.PlaneABase = 0x0000;
			var mem = _apis.MemoryApi;
			mem.WriteByte(2, 0x00, "CRAM"); mem.WriteByte(3, 0x0E, "CRAM");
			for (var i = 0; i < 32; i++) mem.WriteByte(i, 0x11, "VRAM");
			mem.WriteByte(0x0000, 0x00, "VRAM"); mem.WriteByte(0x0001, 0x00, "VRAM");

			var res = Parse(_ts.Call("genesis_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"columns\":1,\"rows\":1}")));
			// no explicit base → the core's plane A base (0x0000) is used
			Assert.Equal((long)0x0000, res.GetProperty("base").GetInt64());
			var path = res.GetProperty("path").GetString();
			Assert.True(System.IO.File.Exists(path));
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Read_plane_window_offset_crops_camera()
		{
			var mem = _apis.MemoryApi;
			mem.WriteByte(2, 0x00, "CRAM"); mem.WriteByte(3, 0x0E, "CRAM");
			for (var i = 0; i < 32; i++) mem.WriteByte(i, 0x11, "VRAM");
			// entry at base + offset_x*2 (tile 0 at column 40): tile 0, white
			mem.WriteByte(0xC000 + 40 * 2, 0x00, "VRAM");
			mem.WriteByte(0xC000 + 40 * 2 + 1, 0x00, "VRAM");

			var res = Parse(_ts.Call("genesis_read_plane", TestHelpers.Js("{\"plane\":\"B\",\"columns\":1,\"rows\":1,\"offset_x\":40}")));
			Assert.Equal((long)0xE000, res.GetProperty("base").GetInt64());
			Assert.Equal(8, res.GetProperty("width").GetInt32());
			var path = res.GetProperty("path").GetString();
			Assert.True(System.IO.File.Exists(path));
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Screenshot_toggles_osd_off_and_back_on()
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test-osd.png");
			_ts.Call("screenshot", TestHelpers.Js($"{{\"path\":\"{path}\"}}"));
			// default: overlay off, then restored off (no getter)
			Assert.Equal(new[] { false, false }, _apis.EmuClientApi.OsdChanges.ToArray());
			Assert.False(_apis.EmuClientApi.OsdEnabled);
		}

		[Fact]
		public void Screenshot_include_overlays_sets_osd()
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test-osd-overlays.png");
			var res = Parse(_ts.Call("screenshot", TestHelpers.Js($"{{\"path\":\"{path}\",\"include_overlays\":true}}")));
			Assert.True(res.GetProperty("include_overlays").GetBoolean());
			Assert.Equal(new[] { true, false }, _apis.EmuClientApi.OsdChanges.ToArray());
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Frame_hash_is_deterministic_and_tracks_pixels()
		{
			var r1 = Parse(_ts.Call("frame_hash", null));
			var r2 = Parse(_ts.Call("frame_hash", null));
			Assert.Equal(r1.GetProperty("sha1").GetString(), r2.GetProperty("sha1").GetString());
			Assert.Equal(1000, r1.GetProperty("frame").GetInt32());
			var path = r1.GetProperty("path").GetString()!;
			Assert.False(string.IsNullOrEmpty(path));
			System.IO.File.Delete(path);

			// a different rendered frame → different hash
			_apis.EmuClientApi.ScreenshotPayload = new byte[] { 0x00, 0xFF, 0x11 };
			var r3 = Parse(_ts.Call("frame_hash", null));
			Assert.NotEqual(r1.GetProperty("sha1").GetString(), r3.GetProperty("sha1").GetString());
			System.IO.File.Delete(r3.GetProperty("path").GetString()!);
		}

		[Fact]
		public void Genesis_z80_registers_filters_z80_keys()
		{
			_apis.EmulationApi.Registers = new Dictionary<string, ulong> { ["M68K PC"] = 1, ["Z80 PC"] = 0x1234, ["Z80 SP"] = 0xFFFF };
			var res = Parse(_ts.Call("genesis_get_z80_registers", null));
			var regs = res.GetProperty("registers");
			Assert.Equal((ulong)0x1234, regs.GetProperty("Z80 PC").GetUInt64());
			Assert.Equal((ulong)0xFFFF, regs.GetProperty("Z80 SP").GetUInt64());
			Assert.False(regs.TryGetProperty("M68K PC", out _));
		}

		[Fact]
		public void Genesis_z80_registers_errors_without_z80_cpu()
		{
			_apis.EmulationApi.Registers = new Dictionary<string, ulong> { ["M68K PC"] = 1 };
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("genesis_get_z80_registers", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Genesis_z80_disassemble_walks_instructions()
		{
			// fake disassembler: each byte = one 1-byte instruction → 0x2000, 0x2001, ...
			_apis.MemoryApi.Bytes[0x2000] = 0x3E; // LD A,n
			_apis.MemoryApi.Bytes[0x2001] = 0x7C;
			var res = Parse(_ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":8192,\"count\":2}")));
			var insns = res.GetProperty("instructions");
			Assert.Equal(2, insns.GetArrayLength());
			Assert.Equal((long)0x2000, insns[0].GetProperty("address").GetInt64());
			Assert.Equal("op 3E", insns[0].GetProperty("instruction").GetString());
			Assert.Equal("3E", insns[0].GetProperty("bytes").GetString());
			Assert.Equal((long)0x2001, insns[1].GetProperty("address").GetInt64());
			Assert.Equal("op 7C", insns[1].GetProperty("instruction").GetString());
			Assert.Equal("7C", insns[1].GetProperty("bytes").GetString());
		}

		[Fact]
		public void Genesis_z80_disassemble_rejects_bad_address()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":65536}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":0,\"count\":65}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Genesis_z80_synthesizes_bus_without_bus_domain()
		{
			// the real GEN gpgx has NO "Z80 BUS" domain (only SMS/GG do) — the
			// tool must synthesize from Z80 RAM: 0x0000-0x1FFF = RAM,
			// 0x2000-0x3FFF = aliased, 0x4000+ = open bus (live-QA mapping)
			_apis.MemoryApi.Domains.Remove("Z80 BUS");
			_apis.MemoryApi.Bytes[0x0000] = 0xC3;
			_apis.MemoryApi.Bytes[0x1000] = 0xED;

			// RAM at bus 0x0000
			var res = Parse(_ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":0,\"count\":1}")));
			Assert.Equal("op C3", res.GetProperty("instructions")[0].GetProperty("instruction").GetString());
			// RAM at bus 0x1000
			res = Parse(_ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":4096,\"count\":1}")));
			Assert.Equal("op ED", res.GetProperty("instructions")[0].GetProperty("instruction").GetString());
			// alias: bus 0x3000 = 12288 reads RAM offset 0x1000
			res = Parse(_ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":12288,\"count\":1}")));
			Assert.Equal("op ED", res.GetProperty("instructions")[0].GetProperty("instruction").GetString());
			// sound I/O / open bus 0x4000+ reads 0xFF
			res = Parse(_ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":16384,\"count\":1}")));
			Assert.Equal("op FF", res.GetProperty("instructions")[0].GetProperty("instruction").GetString());
			res = Parse(_ts.Call("genesis_disassemble_z80", TestHelpers.Js("{\"address\":24576,\"count\":1}")));
			Assert.Equal("op FF", res.GetProperty("instructions")[0].GetProperty("instruction").GetString());
		}

		[Fact]
		public void Read_memory_rejects_unknown_domain()
		{
			// ApiHawk silently falls back to the current domain on a miss —
			// the plugin must reject instead of reading mislabeled data
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"address\":0,\"domain\":\"NOPE\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_range", TestHelpers.Js("{\"address\":0,\"domain\":\"NOPE\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("search_memory", TestHelpers.Js("{\"value\":1,\"domain\":\"NOPE\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Genesis_z80_trace_samples_pc_sp_and_stack()
		{
			// Z80 pc counts up each frame (registers refreshed per frame like
			// the real core); Z80 sp fixed; stack lives in Z80 RAM
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				frames++;
				_apis.EmulationApi.Registers = new Dictionary<string, ulong>
				{
					["M68K PC"] = 0xFF0000,
					["Z80 pc"] = (ulong)(0x2000 + frames),
					["Z80 sp"] = 0x3FFC,
				};
			};
			_apis.EmulationApi.Registers = new Dictionary<string, ulong>
			{
				["M68K PC"] = 0xFF0000,
				["Z80 pc"] = 0x2000,
				["Z80 sp"] = 0x3FFC,
			};
			_apis.MemoryApi.Bytes[0x3FFC] = 0x34;
			_apis.MemoryApi.Bytes[0x3FFD] = 0x12;
			_apis.MemoryApi.Bytes[0x2001] = 0x3E; // sampled at pc 0x2001 (frame 1)
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("genesis_trace_z80", TestHelpers.Js("{\"count\":2,\"step\":1,\"stack_words\":2}")));
			var samples = res.GetProperty("samples");
			Assert.Equal(2, samples.GetArrayLength());
			Assert.Equal((ulong)0x2001, samples[0].GetProperty("pc").GetUInt64());
			Assert.Equal((ulong)0x3FFC, samples[0].GetProperty("sp").GetUInt64());
			Assert.Equal("op 3E", samples[0].GetProperty("instruction").GetString());
			// stack: little-endian 16-bit at SP (0x1234) and SP+2 (0x0000)
			Assert.Equal((ulong)0x1234, samples[0].GetProperty("stack")[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x0000, samples[0].GetProperty("stack")[1].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x2002, samples[1].GetProperty("pc").GetUInt64());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Genesis_z80_trace_errors_without_z80_cpu()
		{
			_apis.EmulationApi.Registers = new Dictionary<string, ulong> { ["M68K PC"] = 1 };
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("genesis_trace_z80", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Symbols_set_then_read_and_write_by_name()
		{
			_apis.MemoryApi.Bytes[0xFFFBCA] = 0x12;
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"mainFunction\",\"address\":16776138,\"width\":8,\"domain\":\"M68K BUS\"}]}"));
			// 16776138 = 0xFFFBCA
			var res = Parse(_ts.Call("read_memory", TestHelpers.Js("{\"name\":\"mainFunction\"}")));
			Assert.Equal((ulong)0x12, res.GetProperty("value").GetUInt64());

			_ts.Call("write_memory", TestHelpers.Js("{\"name\":\"mainFunction\",\"value\":153}"));
			Assert.Equal((byte)0x99, _apis.MemoryApi.Bytes[0xFFFBCA]);
		}

		[Fact]
		public void Symbols_read_many_accepts_names()
		{
			_apis.MemoryApi.Bytes[10] = 0x11;
			_apis.MemoryApi.Bytes[20] = 0x22;
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"a\",\"address\":10},{\"name\":\"b\",\"address\":20}]}"));
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"name\":\"a\"},{\"name\":\"b\"}]}")));
			Assert.Equal((ulong)0x11, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x22, res.GetProperty("reads")[1].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Symbols_unknown_name_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("read_memory", TestHelpers.Js("{\"name\":\"nope\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Symbols_list_and_clear()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"a\",\"address\":10}]}"));
			var listed = Parse(_ts.Call("symbols_list", null));
			Assert.Equal("a", listed.GetProperty("symbols")[0].GetProperty("name").GetString());
			_ts.Call("symbols_clear", null);
			var cleared = Parse(_ts.Call("symbols_list", null));
			Assert.Empty(cleared.GetProperty("symbols").EnumerateArray());
		}

		[Fact]
		public void Symbols_persist_across_toolset_restarts()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"persisted\",\"address\":42,\"width\":16,\"domain\":\"68K RAM\"}]}"));
			// a brand-new toolset sharing the same UserData store must reload them
			var fresh = new McpToolset(_apis, new InlineDispatcher());
			var res = Parse(fresh.Call("symbols_list", null));
			var sym = res.GetProperty("symbols")[0];
			Assert.Equal("persisted", sym.GetProperty("name").GetString());
			Assert.Equal((long)42, sym.GetProperty("address").GetInt64());
			Assert.Equal(16, sym.GetProperty("width").GetInt32());
			Assert.Equal("68K RAM", sym.GetProperty("domain").GetString());
		}

		[Fact]
		public void Symbols_clear_persists_empty()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"x\",\"address\":1}]}"));
			_ts.Call("symbols_clear", null);
			var fresh = new McpToolset(_apis, new InlineDispatcher());
			var res = Parse(fresh.Call("symbols_list", null));
			Assert.Empty(res.GetProperty("symbols").EnumerateArray());
		}

		[Fact]
		public void Symbols_namespaces_are_isolated_and_clearable()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"namespace\":\"ghidra\",\"symbols\":[{\"name\":\"mainFunction\",\"address\":100}]}"));
			_ts.Call("symbols_set", TestHelpers.Js("{\"namespace\":\"fixture\",\"symbols\":[{\"name\":\"hp\",\"address\":200}]}"));

			var res = Parse(_ts.Call("symbols_list", null));
			Assert.Equal(2, res.GetProperty("symbols").GetArrayLength());
			// namespaces survive a reload
			var fresh = new McpToolset(_apis, new InlineDispatcher());
			var reloaded = Parse(fresh.Call("symbols_list", null));
			Assert.Equal(2, reloaded.GetProperty("symbols").GetArrayLength());

			// clearing just one namespace keeps the other
			fresh.Call("symbols_clear", TestHelpers.Js("{\"namespace\":\"fixture\"}"));
			var after = Parse(fresh.Call("symbols_list", null));
			Assert.Single(after.GetProperty("symbols").EnumerateArray());
			Assert.Equal("ghidra", after.GetProperty("symbols")[0].GetProperty("namespace").GetString());
		}

		[Fact]
		public void Symbols_switch_when_rom_changes()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"onlyInRom1\",\"address\":11}]}"));
			// same toolset, different ROM loaded → get_info swaps the symbol set
			_apis.EmulationApi.RomHash = "rom2";
			var info = Parse(_ts.Call("get_info", null));
			var after = Parse(_ts.Call("symbols_list", null));
			Assert.Empty(after.GetProperty("symbols").EnumerateArray());
		}

		[Fact]
		public void Symbols_rom2_persists_separately()
		{
			// default ROM hash is "abcd"
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"rom1sym\",\"address\":11}]}"));
			_apis.EmulationApi.RomHash = "rom2";
			_ts.Call("get_info", null);
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"rom2sym\",\"address\":22}]}"));

			// back to the original ROM restores its own set
			_apis.EmulationApi.RomHash = "abcd";
			_ts.Call("get_info", null);
			var listed = Parse(_ts.Call("symbols_list", null));
			Assert.Single(listed.GetProperty("symbols").EnumerateArray());
			Assert.Equal("rom1sym", listed.GetProperty("symbols")[0].GetProperty("name").GetString());
		}

		[Fact]
		public void Dump_memory_writes_file_and_resource()
		{
			_apis.MemoryApi.Bytes[0] = 0xDE;
			_apis.MemoryApi.Bytes[1] = 0xAD;
			var res = Parse(_ts.Call("dump_memory", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
			var path = res.GetProperty("path").GetString();
			Assert.Contains("bizhawk-mcp", path);
			Assert.Equal(65536L, res.GetProperty("size").GetInt64());
			var fileBytes = System.IO.File.ReadAllBytes(path!);
			Assert.Equal((byte)0xDE, fileBytes[0]);
			Assert.Equal((byte)0xAD, fileBytes[1]);

			var listed = _ts.ListResources();
			var listDoc = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(listed));
			Assert.Contains(listDoc.RootElement.GetProperty("resources").EnumerateArray(), r => r.GetProperty("mimeType").GetString() == "application/octet-stream");
		}

		[Fact]
		public void Dump_memory_range_writes_subrange_file()
		{
			_apis.MemoryApi.Bytes[100] = 0xAA;
			_apis.MemoryApi.Bytes[199] = 0xBB;
			var res = Parse(_ts.Call("dump_memory", TestHelpers.Js("{\"domain\":\"68K RAM\",\"range_start\":100,\"range_length\":100}")));
			Assert.Equal(100L, res.GetProperty("size").GetInt64());
			Assert.Equal(100L, res.GetProperty("range_start").GetInt64());
			var fileBytes = System.IO.File.ReadAllBytes(res.GetProperty("path").GetString()!);
			Assert.Equal(100, fileBytes.Length);
			Assert.Equal((byte)0xAA, fileBytes[0]);
			Assert.Equal((byte)0xBB, fileBytes[99]);
			System.IO.File.Delete(res.GetProperty("path").GetString()!);
		}

		[Fact]
		public void Dump_memory_rejects_out_of_range()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("dump_memory", TestHelpers.Js("{\"domain\":\"68K RAM\",\"range_start\":65500,\"range_length\":100}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Resources_list_reports_host_path()
		{
			var res = Parse(_ts.Call("dump_memory", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
			var uri = res.GetProperty("resource").GetString();
			var listed = _ts.ListResources();
			var listDoc = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(listed));
			var entry = listDoc.RootElement.GetProperty("resources").EnumerateArray().First(r => r.GetProperty("uri").GetString() == uri);
			var path = entry.GetProperty("path").GetString();
			Assert.False(string.IsNullOrEmpty(path));
			Assert.True(System.IO.File.Exists(path));
			System.IO.File.Delete(path!);
		}

		[Fact]
		public void Read_many_compact_returns_aligned_values()
		{
			_apis.MemoryApi.Bytes[100] = 0x42;
			_apis.MemoryApi.Bytes[101] = 0x24;
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":100},{\"address\":101},{\"address\":999999}],\"compact\":true}")));
			Assert.Equal(2, res.GetProperty("read").GetInt32());
			Assert.Equal(1, res.GetProperty("failed").GetInt32());
			// aligned with items: 0x42, 0x24, null (failed)
			Assert.Equal((ulong)0x42, res.GetProperty("values")[0].GetUInt64());
			Assert.Equal((ulong)0x24, res.GetProperty("values")[1].GetUInt64());
			Assert.Equal(JsonValueKind.Null, res.GetProperty("values")[2].ValueKind);
			Assert.Equal(2, res.GetProperty("failures")[0].GetProperty("index").GetInt32());
		}

		[Fact]
		public void Search_compact_returns_addresses_only()
		{
			_apis.MemoryApi.Bytes[10] = 0x42;
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"compact\":true}")));
			Assert.Equal(2, res.GetProperty("count").GetInt32());
			var addresses = res.GetProperty("addresses");
			Assert.Equal(2, addresses.GetArrayLength());
			Assert.Equal((long)10, addresses[0].GetInt64());
			Assert.Equal((long)100, addresses[1].GetInt64());
			Assert.False(res.TryGetProperty("matches", out _));
		}

		[Fact]
		public void Read_many_consistent_pauses_and_resumes()
		{
			_apis.EmuClientApi.Paused = false;
			var res = Parse(_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":10}],\"consistent\":true}")));
			Assert.Equal(1, res.GetProperty("reads").GetArrayLength());
			// paused during the batch (Pause called), resumed after (Unpause called)
			Assert.Equal(1, _apis.EmuClientApi.PauseCalls);
			Assert.Equal(1, _apis.EmuClientApi.UnpauseCalls);
			Assert.False(_apis.EmuClientApi.Paused);
		}

		[Fact]
		public void Read_many_consistent_keeps_pause_when_already_paused()
		{
			_apis.EmuClientApi.Paused = true;
			_ts.Call("read_many", TestHelpers.Js("{\"items\":[{\"address\":10}],\"consistent\":true}"));
			Assert.Equal(0, _apis.EmuClientApi.PauseCalls);
			Assert.Equal(0, _apis.EmuClientApi.UnpauseCalls);
			Assert.True(_apis.EmuClientApi.Paused);
		}

		[Fact]
		public void Start_fixture_writes_csv_with_samples_per_frame()
		{
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = (byte)frames; };
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.EmuClientApi.Paused = true;
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":100,\"width\":8}]}"));
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp-test-fixture.csv");

			var res = Parse(_ts.Call("start_fixture", TestHelpers.Js($"{{\"frames\":5,\"samples\":[{{\"name\":\"hp\"}}],\"path\":\"{path}\"}}")));
			Assert.Equal(5, res.GetProperty("frames").GetInt32());
			Assert.Equal(1, res.GetProperty("samples").GetInt32());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored

			var lines = System.IO.File.ReadAllLines(path);
			Assert.Equal(6, lines.Length); // header + 5 rows
			Assert.StartsWith("frame,", lines[0]);
			Assert.EndsWith("hp", lines[0].TrimEnd());
			// memory goes 1,2,3,4,5 across the 5 frames
			Assert.Equal("0,1", lines[1]);
			Assert.Equal("4,5", lines[5]);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Start_fixture_applies_input_timeline()
		{
			_apis.EmuClientApi.Paused = true;
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp-test-fixture2.csv");
			_ts.Call("start_fixture", TestHelpers.Js($"{{\"frames\":2,\"samples\":[{{\"address\":0,\"width\":8}}],\"inputs\":[{{\"frame\":1,\"buttons\":{{\"A\":true,\"Right\":true}}}}],\"path\":\"{path}\"}}"));
			Assert.NotNull(_apis.JoypadApi.LastSet);
			Assert.True(_apis.JoypadApi.LastSet!["A"]);
			Assert.True(_apis.JoypadApi.LastSet!["Right"]);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Start_fixture_hold_mode_keeps_buttons_after_timeline_end()
		{
			// B3 default: a timeline ending in {Right: true} keeps Right held.
			_apis.EmuClientApi.Paused = true;
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp-test-fixture3.csv");
			_apis.JoypadApi.Calls.Clear();
			_ts.Call("start_fixture", TestHelpers.Js($"{{\"frames\":3,\"samples\":[{{\"address\":0,\"width\":8}}],\"inputs\":[{{\"frame\":0,\"buttons\":{{\"Right\":true}}}}],\"path\":\"{path}\"}}"));
			Assert.Single(_apis.JoypadApi.Calls); // only frame 0 sets — Right stays held on 1,2
			Assert.True(_apis.JoypadApi.Calls[0].buttons["Right"]);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Start_fixture_explicit_mode_releases_buttons_on_absent_frames()
		{
			// B3 fix: "explicit" = absent timeline frames mean no buttons
			// (per-frame like Lua joypad.set), so Right must be released on
			// frames 1 and 2.
			_apis.EmuClientApi.Paused = true;
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp-test-fixture4.csv");
			_apis.JoypadApi.Calls.Clear();
			_ts.Call("start_fixture", TestHelpers.Js($"{{\"frames\":3,\"samples\":[{{\"address\":0,\"width\":8}}],\"inputs\":[{{\"frame\":0,\"buttons\":{{\"Right\":true}}}}],\"input_mode\":\"explicit\",\"path\":\"{path}\"}}"));
			Assert.Equal(3, _apis.JoypadApi.Calls.Count);
			Assert.True(_apis.JoypadApi.Calls[0].buttons["Right"]);
			Assert.Empty(_apis.JoypadApi.Calls[1].buttons);
			Assert.Null(_apis.JoypadApi.Calls[1].controller); // release scope = all controllers
			Assert.Empty(_apis.JoypadApi.Calls[2].buttons);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Start_fixture_empty_buttons_releases_in_hold_mode()
		{
			// B3 fix: {"frame": N, "buttons": {}} releases the controller's
			// buttons mid-timeline even in hold mode (JoypadApi.Set un-sets
			// every button not present in the dict).
			_apis.EmuClientApi.Paused = true;
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp-test-fixture5.csv");
			_apis.JoypadApi.Calls.Clear();
			_ts.Call("start_fixture", TestHelpers.Js($"{{\"frames\":3,\"samples\":[{{\"address\":0,\"width\":8}}],\"inputs\":[{{\"frame\":0,\"buttons\":{{\"Right\":true}}}},{{\"frame\":1,\"buttons\":{{}}}}],\"path\":\"{path}\"}}"));
			Assert.Equal(2, _apis.JoypadApi.Calls.Count);
			Assert.True(_apis.JoypadApi.Calls[0].buttons["Right"]);
			Assert.Empty(_apis.JoypadApi.Calls[1].buttons);
			Assert.Equal(1, _apis.JoypadApi.Calls[1].controller); // releases P1 only
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Start_fixture_rejects_bad_input_mode()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("start_fixture", TestHelpers.Js("{\"frames\":1,\"samples\":[{\"address\":0}],\"input_mode\":\"sticky\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Start_fixture_rejects_bad_frames()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("start_fixture", TestHelpers.Js("{\"frames\":0,\"samples\":[{\"address\":0}]}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Read_struct_returns_relative_offsets()
		{
			// playerSprPtr scenario: base 0xFFF85E + 0x1A / 0x1E → X/Y (16.16 fixed)
			_apis.MemoryApi.Bytes[0xF85E + 0x1A] = 0x00;
			_apis.MemoryApi.Bytes[0xF85E + 0x1B] = 0x20;
			_apis.MemoryApi.Bytes[0xF85E + 0x1C] = 0x00;
			_apis.MemoryApi.Bytes[0xF85E + 0x1D] = 0x00;
			_apis.MemoryApi.Bytes[0xF85E + 0x1E] = 0x00;
			_apis.MemoryApi.Bytes[0xF85E + 0x1F] = 0x10;
			_apis.MemoryApi.Bytes[0xF85E + 0x20] = 0xF0;
			_apis.MemoryApi.Bytes[0xF85E + 0x21] = 0x00;

			var res = Parse(_ts.Call("read_struct", TestHelpers.Js("{\"address\":63582,\"domain\":\"68K RAM\",\"fields\":[{\"name\":\"x\",\"offset\":26,\"width\":32},{\"name\":\"y\",\"offset\":30,\"width\":32}]}")));
			Assert.Equal((ulong)0x00200000, res.GetProperty("fields")[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x0010F000, res.GetProperty("fields")[1].GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("fields")[0].GetProperty("endianness").GetString());
		}

		[Fact]
		public void Read_struct_accepts_symbol_base()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"player\",\"address\":100,\"width\":8,\"domain\":\"68K RAM\"}]}"));
			_apis.MemoryApi.Bytes[100] = 0xAA;
			_apis.MemoryApi.Bytes[101] = 0xBB;
			var res = Parse(_ts.Call("read_struct", TestHelpers.Js("{\"name\":\"player\",\"fields\":[{\"name\":\"b0\",\"offset\":0,\"width\":8},{\"name\":\"b1\",\"offset\":1,\"width\":8}]}")));
			Assert.Equal((ulong)0xAA, res.GetProperty("fields")[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0xBB, res.GetProperty("fields")[1].GetProperty("value").GetUInt64());
		}

	}

	public class WatcherToolTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public WatcherToolTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Watch_add_list_read_roundtrip()
		{
			_apis.MemoryApi.Bytes[100] = 0x42;
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			var listed = Parse(_ts.Call("watch_list", null));
			Assert.Equal((ulong)0x42, listed.GetProperty("watchers")[0].GetProperty("value").GetUInt64());

			var read = Parse(_ts.Call("watch_read", null));
			Assert.Equal((ulong)0x42, read.GetProperty("watchers")[0].GetProperty("value").GetUInt64());
			Assert.False(read.GetProperty("watchers")[0].GetProperty("changed").GetBoolean());
		}

		[Fact]
		public void Watch_read_reports_changed()
		{
			_apis.MemoryApi.Bytes[100] = 0x42;
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			_ts.Call("watch_read", null);
			_apis.MemoryApi.Bytes[100] = 0x99;
			var read = Parse(_ts.Call("watch_read", null));
			Assert.Equal((ulong)0x99, read.GetProperty("watchers")[0].GetProperty("value").GetUInt64());
			Assert.True(read.GetProperty("watchers")[0].GetProperty("changed").GetBoolean());
		}

		[Fact]
		public void Watch_read_compact_returns_aligned_arrays()
		{
			_apis.MemoryApi.Bytes[100] = 0x42;
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"lives\",\"address\":200,\"width\":8}"));
			var read = Parse(_ts.Call("watch_read", TestHelpers.Js("{\"compact\":true}")));
			Assert.Equal(new[] { "hp", "lives" }, read.GetProperty("names").EnumerateArray().Select(e => e.GetString()).ToArray());
			Assert.Equal((ulong)0x42, read.GetProperty("values")[0].GetUInt64());
			Assert.Equal((ulong)0, read.GetProperty("values")[1].GetUInt64());
			Assert.False(read.GetProperty("changed")[0].GetBoolean());
		}

		[Fact]
		public void Watch_reads_with_per_watcher_endianness()
		{
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"main\",\"address\":100,\"width\":16,\"domain\":\"68K RAM\"}"));
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"sound\",\"address\":100,\"width\":16,\"domain\":\"Z80 RAM\"}"));
			var listed = Parse(_ts.Call("watch_list", null));
			Assert.Equal((ulong)8, listed.GetProperty("watchers")[0].GetProperty("value").GetUInt64());
			Assert.Equal("big", listed.GetProperty("watchers")[0].GetProperty("endianness").GetString());
			Assert.Equal((ulong)2048, listed.GetProperty("watchers")[1].GetProperty("value").GetUInt64());
			Assert.Equal("little", listed.GetProperty("watchers")[1].GetProperty("endianness").GetString());
		}

		[Fact]
		public void Watch_add_duplicate_rejected()
		{
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":200,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watch_remove_works()
		{
			_ts.Call("watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			var res = _ts.Call("watch_remove", TestHelpers.Js("{\"name\":\"hp\"}"));
			Assert.Contains("removed", res);
			var listed = Parse(_ts.Call("watch_list", null));
			Assert.Empty(listed.GetProperty("watchers").EnumerateArray());
		}

		[Fact]
		public void Watch_add_outside_domain_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watch_add", TestHelpers.Js("{\"name\":\"x\",\"address\":70000,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Wait_until_matches_when_value_reached()
		{
			// memory starts at 0; every frame advance bumps it by 1 → reaches 5 on the 5th frame
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = (byte)frames; };
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"eq\",\"value\":5,\"width\":8}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal(5, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)5, res.GetProperty("value").GetUInt64());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Wait_until_accepts_symbol_name()
		{
			// same as above but the target is addressed by symbol, not raw offset
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":100,\"width\":8}]}"));
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = (byte)frames; };
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"name\":\"hp\",\"op\":\"eq\",\"value\":3}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal(3, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)3, res.GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Wait_until_times_out()
		{
			// value never changes → no match within 600 frames
			_apis.MemoryApi.Bytes[100] = 0;
			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"eq\",\"value\":9,\"width\":8}")));
			Assert.False(res.GetProperty("matched").GetBoolean());
			Assert.Equal(600, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Wait_until_rejects_bad_op()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"==\",\"value\":1}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Wait_until_u16_uses_domain_endianness()
		{
			// big-endian 68K RAM: bytes 00 01 = 1; wait until it reaches 1
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = 0x00; _apis.MemoryApi.Bytes[101] = (byte)frames; };
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x00;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"eq\",\"value\":1,\"width\":16,\"domain\":\"68K RAM\"}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal((ulong)1, res.GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Wait_until_multi_condition_waits_for_all_on_same_frame()
		{
			// addr 100 counts up, addr 200 counts down — both reach their
			// targets on frame 3 (3 and 7); single-mode waits would need nesting
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				frames++;
				_apis.MemoryApi.Bytes[100] = (byte)frames;
				_apis.MemoryApi.Bytes[200] = (byte)(10 - frames);
			};
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.MemoryApi.Bytes[200] = 10;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"conditions\":[{\"address\":100,\"op\":\"eq\",\"value\":3,\"width\":8},{\"address\":200,\"op\":\"eq\",\"value\":7,\"width\":8}]}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal(3, res.GetProperty("frames").GetInt32());
			var conds = res.GetProperty("conditions");
			Assert.Equal(2, conds.GetArrayLength());
			Assert.True(conds[0].GetProperty("matched").GetBoolean());
			Assert.Equal((ulong)3, conds[0].GetProperty("current").GetUInt64());
			Assert.True(conds[1].GetProperty("matched").GetBoolean());
			Assert.Equal((ulong)7, conds[1].GetProperty("current").GetUInt64());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Wait_until_multi_condition_supports_symbols_and_mixed_ops()
		{
			// symbol "timer" counts up by 2/frame (>= 8 on frame 4); addr 300
			// stays 0 so its "lt 5" condition already holds
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"timer\",\"address\":100,\"width\":8}]}"));
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = (byte)(frames * 2); };
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"conditions\":[{\"name\":\"timer\",\"op\":\"ge\",\"value\":8},{\"address\":300,\"op\":\"lt\",\"value\":5}]}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal(4, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Wait_until_multi_condition_times_out()
		{
			// addr 200 never reaches 99 → timeout without match
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("wait_until", TestHelpers.Js("{\"timeout_frames\":10,\"conditions\":[{\"address\":100,\"op\":\"eq\",\"value\":5},{\"address\":200,\"op\":\"eq\",\"value\":99}]}")));
			Assert.False(res.GetProperty("matched").GetBoolean());
			Assert.Equal(10, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Wait_until_rejects_bad_conditions()
		{
			// empty array
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("wait_until", TestHelpers.Js("{\"conditions\":[]}")));
			// condition missing its value
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("wait_until", TestHelpers.Js("{\"conditions\":[{\"address\":100,\"op\":\"eq\"}]}")));
			// condition with a bad op
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("wait_until", TestHelpers.Js("{\"conditions\":[{\"address\":100,\"op\":\"==\",\"value\":1}]}")));
		}

		[Fact]
		public void Watch_change_reports_first_change_frame()
		{
			// memory starts at 0; bumps on the 3rd frame advance → changed at frame 3
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				frames++;
				if (frames == 3) _apis.MemoryApi.Bytes[100] = 0xAA;
			};
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("watch_change", TestHelpers.Js("{\"address\":100,\"width\":8}")));
			Assert.True(res.GetProperty("changed").GetBoolean());
			Assert.Equal(3, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)0, res.GetProperty("initial").GetUInt64());
			Assert.Equal((ulong)0xAA, res.GetProperty("value").GetUInt64());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Watch_change_accepts_symbol_name()
		{
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"camX\",\"address\":100,\"width\":32}]}"));
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = (byte)frames; };
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("watch_change", TestHelpers.Js("{\"name\":\"camX\"}")));
			Assert.True(res.GetProperty("changed").GetBoolean());
			Assert.Equal(1, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Watch_change_times_out_when_value_is_stable()
		{
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("watch_change", TestHelpers.Js("{\"address\":100,\"width\":8,\"timeout_frames\":5}")));
			Assert.False(res.GetProperty("changed").GetBoolean());
			Assert.Equal(5, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)0x42, res.GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Watch_change_rejects_bad_timeout()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watch_change", TestHelpers.Js("{\"address\":100,\"timeout_frames\":0}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Trace_samples_pc_and_disasm()
		{
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("trace", TestHelpers.Js("{\"count\":10,\"step\":5}")));
			var samples = res.GetProperty("samples");
			// 10 frames, sampled every 5 → frames 0, 5 (i % step == 0)
			Assert.Equal(2, samples.GetArrayLength());
			Assert.Equal((ulong)0xFFFBCA, samples[0].GetProperty("pc").GetUInt64());
			Assert.Equal("MOVE.L D0,D1", samples[0].GetProperty("disasm").GetString());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
			Assert.Equal(10, _apis.EmuClientApi.FramesAdvanced);
		}

		[Fact]
		public void Trace_rejects_large_count()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("trace", TestHelpers.Js("{\"count\":601}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}
	}

	public class WatchpointToolTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public WatchpointToolTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Watchpoint_add_registers_in_core()
		{
			var dbg = _apis.EnableWatchpoints();
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":16776136}"));
			Assert.Single(dbg.Callbacks.Registered);
			Assert.Equal(MemoryCallbackType.Write, dbg.Callbacks.Registered[0].Type);
			Assert.Equal((uint)16776136, dbg.Callbacks.Registered[0].Address);
		}

		[Fact]
		public void Watchpoint_add_without_core_support_errors()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("unsupported", ex.Message);
		}

		[Fact]
		public void Watchpoint_execute_requires_address()
		{
			_apis.EnableWatchpoints();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"execute\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_execute_unavailable_errors()
		{
			var dbg = _apis.EnableWatchpoints();
			dbg.Callbacks.ExecuteCallbacksAvailableValue = false;
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"execute\",\"address\":16776136}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_bad_scope_errors()
		{
			_apis.EnableWatchpoints();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"domain\":\"NOPE\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_wait_detects_fire()
		{
			var dbg = _apis.EnableWatchpoints();
			_apis.EmuClientApi.Paused = true;
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":16776136}"));
			// fire the callback on the 3rd frame advance
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 3) dbg.Callbacks.Fire(16776136, 0x77);
			};

			var res = Parse(_ts.Call("watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":10}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal("wp1", res.GetProperty("watchpoint").GetString());
			Assert.Equal("write", res.GetProperty("type").GetString());
			Assert.Equal((ulong)16776136, res.GetProperty("address").GetUInt64());
			Assert.Equal((ulong)0x77, res.GetProperty("value").GetUInt64());
			Assert.Equal(3, res.GetProperty("frames").GetInt32());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Watchpoint_wait_context_dump_on_hit()
		{
			var dbg = _apis.EnableWatchpoints();
			_apis.EmuClientApi.Paused = true;
			_apis.MemoryApi.Bytes[0x24D0] = 0xAA;
			_apis.MemoryApi.Bytes[0x24D1] = 0xBB;
			_apis.MemoryApi.Bytes[0x24D2] = 0xCC;
			_apis.MemoryApi.Bytes[0x24D3] = 0xDD;
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":16776200}"));
			// hit address 16776200 (= 0x1000208); scope M68K BUS (16 MiB)
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 2) dbg.Callbacks.Fire(16776200, 0x42);
			};

			var res = Parse(_ts.Call("watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":10,\"context_bytes\":64}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			// registers captured on the hit
			Assert.Equal((ulong)0xFFFBCA, res.GetProperty("registers").GetProperty("M68K PC").GetUInt64());
			// PC + disassembly of the current instruction
			Assert.Equal((ulong)0xFFFBCA, res.GetProperty("pc").GetUInt64());
			Assert.Equal("MOVE.L D0,D1", res.GetProperty("instruction").GetString());
			// raw bytes around the hit
			var ctx = res.GetProperty("context");
			Assert.Equal(64, ctx.GetProperty("bytes").GetString()!.Split(' ').Length);
			Assert.True(ctx.TryGetProperty("start", out _));
			Assert.True(ctx.TryGetProperty("hit_offset", out _));
		}

		[Fact]
		public void Watchpoint_wait_context_off_by_default()
		{
			var dbg = _apis.EnableWatchpoints();
			_apis.EmuClientApi.Paused = true;
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\"}"));
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 1) dbg.Callbacks.Fire(100, 0x01);
			};

			var res = Parse(_ts.Call("watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":10}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.False(res.TryGetProperty("registers", out _));
			Assert.False(res.TryGetProperty("context", out _));
		}

		[Fact]
		public void Watchpoint_wait_times_out()
		{
			_apis.EnableWatchpoints();
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"read\"}"));
			var res = Parse(_ts.Call("watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":5}")));
			Assert.False(res.GetProperty("matched").GetBoolean());
			Assert.Equal(5, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Run_to_advances_until_target_executes_and_cleans_up()
		{
			var dbg = _apis.EnableWatchpoints();
			_apis.EmuClientApi.Paused = true;
			// fake PC is 0xFFFBCA; target 0xFFFBCE (16776142) executes on frame 2
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 2) dbg.Callbacks.Fire(0xFFFBCE, 0x60);
			};

			var res = Parse(_ts.Call("run_to", TestHelpers.Js("{\"address\":16776142}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.False(res.GetProperty("timed_out").GetBoolean());
			Assert.Equal(2, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)0xFFFBCE, res.GetProperty("address").GetUInt64());
			Assert.Equal((ulong)0x60, res.GetProperty("value").GetUInt64());
			Assert.True(res.TryGetProperty("pc", out _)); // state at END of the frame
			Assert.True(res.TryGetProperty("pc_instruction", out _));
			Assert.True(res.TryGetProperty("instruction", out _)); // disasm at the target
			// the one-shot watchpoint was removed — nothing lingers
			Assert.Empty(dbg.Callbacks.Registered);
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Run_to_masks_bus_addresses_and_translates_symbols()
		{
			var dbg = _apis.EnableWatchpoints();
			_apis.EmuClientApi.Paused = true;
			// 32-bit disassembly form masks down to the 24-bit bus
			var res = Parse(_ts.Call("run_to", TestHelpers.Js("{\"address\":4294966218}"))); // 0xFFFFFBCA
			Assert.Equal((ulong)0xFFFBCA, res.GetProperty("address").GetUInt64());
			Assert.Equal((ulong)4294966218, res.GetProperty("requested").GetUInt64());
			Assert.Equal(0, res.GetProperty("frames").GetInt32()); // 0xFFFBCA == fake PC: already there
			Assert.True(res.GetProperty("already_at_target").GetBoolean());

			// a symbol registered on the 68K RAM domain (0-based) translates
			// by the domain's bus base: 0xFBCA → 0xFFFBCA
			_ts.Call("symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"snd_main\",\"address\":64458,\"domain\":\"68K RAM\",\"width\":8}]}")); // 0xFBCA
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 1) dbg.Callbacks.Fire(0xFFFBCA, 0x4E);
			};
			var res2 = Parse(_ts.Call("run_to", TestHelpers.Js("{\"name\":\"snd_main\"}")));
			Assert.True(res2.GetProperty("matched").GetBoolean());
			Assert.Equal((ulong)0xFFFBCA, res2.GetProperty("address").GetUInt64());
			Assert.Equal((ulong)64458, res2.GetProperty("requested").GetUInt64());
			Assert.Empty(dbg.Callbacks.Registered);
		}

		[Fact]
		public void Run_to_times_out_and_errors_without_callbacks()
		{
			_apis.EnableWatchpoints();
			var res = Parse(_ts.Call("run_to", TestHelpers.Js("{\"address\":16776142,\"timeout_frames\":3}")));
			Assert.False(res.GetProperty("matched").GetBoolean());
			Assert.True(res.GetProperty("timed_out").GetBoolean());
			Assert.Equal(3, res.GetProperty("frames").GetInt32());

			// non-gpgx cores (no memory callbacks) get a clear error
			var noWp = new FakeApis();
			var ex = Assert.Throws<JsonRpc.Error>(() => noWp.Toolset().Call("run_to", TestHelpers.Js("{\"address\":16776142}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_wait_without_any_registered_errors()
		{
			_apis.EnableWatchpoints();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("watchpoint_wait", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_remove_unregisters()
		{
			var dbg = _apis.EnableWatchpoints();
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":100}"));
			var res = _ts.Call("watchpoint_remove", TestHelpers.Js("{\"name\":\"wp1\"}"));
			Assert.Contains("removed", res);
			Assert.Empty(dbg.Callbacks.Registered);
		}

		[Fact]
		public void Watchpoint_list_reports()
		{
			_apis.EnableWatchpoints();
			_ts.Call("watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"execute\",\"address\":2370}"));
			var res = Parse(_ts.Call("watchpoint_list", null));
			Assert.Equal("wp1", res.GetProperty("watchpoints")[0].GetProperty("name").GetString());
			Assert.Equal("execute", res.GetProperty("watchpoints")[0].GetProperty("type").GetString());
			Assert.Equal((ulong)2370, res.GetProperty("watchpoints")[0].GetProperty("address").GetUInt64());
		}
	}

	public class EmulationToolTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public EmulationToolTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Frame_advance_runs_frames_and_restores_pause()
		{
			_apis.EmuClientApi.Paused = true;
			var res = _ts.Call("frame_advance", TestHelpers.Js("{\"count\":3}"));
			Assert.Equal(3, _apis.EmuClientApi.FramesAdvanced);
			Assert.Equal(1, _apis.EmuClientApi.UnpauseCalls);
			Assert.Equal(1, _apis.EmuClientApi.PauseCalls);
			Assert.True(_apis.EmuClientApi.Paused); // restored
			Assert.Contains("was paused", res);
		}

		[Fact]
		public void Frame_advance_running_stays_running()
		{
			_apis.EmuClientApi.Paused = false;
			_ts.Call("frame_advance", TestHelpers.Js("{\"count\":2}"));
			Assert.Equal(2, _apis.EmuClientApi.FramesAdvanced);
			Assert.Equal(0, _apis.EmuClientApi.UnpauseCalls);
			Assert.Equal(0, _apis.EmuClientApi.PauseCalls);
			Assert.False(_apis.EmuClientApi.Paused);
		}

		[Fact]
		public void Pause_unpause_toggle_work()
		{
			_apis.EmuClientApi.Paused = false;
			var res = Parse(_ts.Call("pause", null));
			Assert.True(res.GetProperty("paused").GetBoolean());
			res = Parse(_ts.Call("unpause", null));
			Assert.False(res.GetProperty("paused").GetBoolean());
			res = Parse(_ts.Call("toggle_pause", null));
			Assert.True(res.GetProperty("paused").GetBoolean());
		}

		[Fact]
		public void Speed_mode_sets_percent()
		{
			_ts.Call("speed_mode", TestHelpers.Js("{\"percent\":400}"));
			Assert.Equal(400, _apis.EmuClientApi.SpeedModePercent);
		}

		[Fact]
		public void Sound_get_set_work()
		{
			var res = Parse(_ts.Call("get_sound", null));
			Assert.True(res.GetProperty("sound_on").GetBoolean());
			_ts.Call("set_sound", TestHelpers.Js("{\"enabled\":false}"));
			Assert.False(_apis.EmuClientApi.SoundOn);
			res = Parse(_ts.Call("get_sound", null));
			Assert.False(res.GetProperty("sound_on").GetBoolean());
			_ts.Call("set_sound", null);
			Assert.True(_apis.EmuClientApi.SoundOn); // default enables
		}

		[Fact]
		public void Enable_rewind_toggles()
		{
			_ts.Call("enable_rewind", TestHelpers.Js("{\"enabled\":true}"));
			Assert.True(_apis.EmuClientApi.RewindEnabled);
			Assert.Equal(1, _apis.EmuClientApi.RewindCalls);
			_ts.Call("enable_rewind", TestHelpers.Js("{\"enabled\":false}"));
			Assert.False(_apis.EmuClientApi.RewindEnabled);
		}

		[Fact]
		public void Frame_skip_sets_count()
		{
			_ts.Call("frameskip", TestHelpers.Js("{\"count\":3}"));
			Assert.Equal(3, _apis.EmuClientApi.FrameSkipValue);
			_ts.Call("frameskip", TestHelpers.Js("{\"count\":0}"));
			Assert.Equal(0, _apis.EmuClientApi.FrameSkipValue);
		}

		[Fact]
		public void Limit_framerate_toggles()
		{
			_ts.Call("limit_framerate", TestHelpers.Js("{\"enabled\":false}"));
			Assert.False(_apis.EmulationApi.LimitFramerateValue);
			_ts.Call("limit_framerate", null);
			Assert.True(_apis.EmulationApi.LimitFramerateValue);
		}

		[Fact]
		public void Rom_open_close_reboot_work()
		{
			var res = Parse(_ts.Call("open_rom", TestHelpers.Js("{\"path\":\"F:/roms/game.md\"}")));
			Assert.True(res.GetProperty("loaded").GetBoolean());
			Assert.Single(_apis.EmuClientApi.OpenedRoms);
			Assert.Equal("/mnt/f/roms/game.md", _apis.EmuClientApi.OpenedRoms[0]);
			_ts.Call("close_rom", null);
			Assert.Equal(1, _apis.EmuClientApi.CloseRomCalls);
			_ts.Call("reboot", null);
			Assert.Equal(1, _apis.EmuClientApi.RebootCalls);
		}

		[Fact]
		public void Open_rom_requires_path()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("open_rom", TestHelpers.Js("{}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Get_registers_returns_map()
		{
			var res = Parse(_ts.Call("get_registers", null));
			Assert.Equal((ulong)0xFFFBCA, res.GetProperty("registers").GetProperty("M68K PC").GetUInt64());
		}

		[Fact]
		public void Trace_finds_prefixed_core_register_names()
		{
			// gpgx names registers "M68K PC" etc.; the trace must match the
			// suffix, not just the bare "PC" key (regression: PC sampled as 0).
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("trace", TestHelpers.Js("{\"count\":2,\"step\":1}")));
			var sample = res.GetProperty("samples")[0];
			Assert.Equal((ulong)0xFFFBCA, sample.GetProperty("pc").GetUInt64());
			Assert.Equal("MOVE.L D0,D1", sample.GetProperty("disasm").GetString());
			Assert.Equal((ulong)0xFFFFFDFA, sample.GetProperty("sp").GetUInt64());
			Assert.Equal((ulong)0x2000, sample.GetProperty("sr").GetUInt64());
		}

		[Fact]
		public void Set_register_forwards()
		{
			_ts.Call("set_register", TestHelpers.Js("{\"register\":\"A0\",\"value\":39321}"));
			Assert.Equal("A0", _apis.EmulationApi.RegisterToSet);
			Assert.Equal(0x9999, _apis.EmulationApi.RegisterValue);
		}

		[Fact]
		public void Disassemble_returns_asm()
		{
			var res = _ts.Call("disassemble", TestHelpers.Js("{\"pc\":4194304}"));
			Assert.Equal("MOVE.L D0,D1", res);
		}

		[Fact]
		public void Lag_count_reports_state()
		{
			_apis.EmulationApi.Lagged = true;
			_apis.EmulationApi.LagCountValue = 7;
			var res = Parse(_ts.Call("lag_count", null));
			Assert.True(res.GetProperty("is_lagged").GetBoolean());
			Assert.Equal(7, res.GetProperty("lag_count").GetInt32());
		}
	}

	public class ArtifactResourceTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public ArtifactResourceTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Screenshot_without_path_returns_temp_path_and_resource()
		{
			var res = Parse(_ts.Call("screenshot", null));
			var path = res.GetProperty("path").GetString();
			Assert.Contains("bizhawk-mcp", path);
			Assert.StartsWith("bizhawk://", res.GetProperty("resource").GetString());
			// wsl_path only exists on Windows hosts — the test host is Linux
			Assert.False(res.TryGetProperty("wsl_path", out _));
			Assert.Single(_apis.EmuClientApi.Screenshots);
		}

		[Fact]
		public void Fixture_csv_is_registered_as_an_artifact()
		{
			_apis.EmuClientApi.Paused = true;
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[0] = (byte)frames; };
			var res = Parse(_ts.Call("start_fixture", TestHelpers.Js("{\"frames\":3,\"samples\":[{\"address\":0,\"width\":8}]}")));
			var uri = res.GetProperty("resource").GetString()!;
			Assert.StartsWith("bizhawk://", uri);
			Assert.False(res.TryGetProperty("wsl_path", out _)); // Windows-host only
			Assert.True(res.TryGetProperty("size", out _));
			Assert.Equal(3, res.GetProperty("row_count").GetInt32());

			// listed with host path, readable back as CSV
			var listDoc = JsonDocument.Parse(JsonSerializer.Serialize(_ts.ListResources()));
			var artifacts = listDoc.RootElement.GetProperty("resources");
			var entry = artifacts.EnumerateArray().First(r => r.GetProperty("uri").GetString() == uri);
			Assert.Equal("text/csv", entry.GetProperty("mimeType").GetString());
			Assert.True(entry.TryGetProperty("path", out _));
			Assert.False(entry.TryGetProperty("wsl_path", out _)); // Windows-host only

			var readDoc = JsonDocument.Parse(JsonSerializer.Serialize(_ts.ReadResource(uri)));
			var contents = readDoc.RootElement.GetProperty("contents")[0];
			var csv = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(contents.GetProperty("blob").GetString()!));
			Assert.StartsWith("frame,", csv);
			Assert.Equal("2,3", csv.Split('\n')[3]);
		}

		[Fact]
		public void Screenshot_with_path_uses_it()
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test-shot.png");
			var res = Parse(_ts.Call("screenshot", TestHelpers.Js($"{{\"path\":\"{path}\"}}")));
			Assert.Equal(path, res.GetProperty("path").GetString());
		}

		[Fact]
		public void Resources_list_and_read_roundtrip()
		{
			var res = Parse(_ts.Call("screenshot", null));
			var uri = res.GetProperty("resource").GetString()!;

			var listed = _ts.ListResources();
			var listDoc = JsonDocument.Parse(JsonSerializer.Serialize(listed));
			// 1 artifact + the static lua-docs resource
			Assert.Equal(2, listDoc.RootElement.GetProperty("resources").GetArrayLength());
			Assert.Equal("image/png", listDoc.RootElement.GetProperty("resources")[0].GetProperty("mimeType").GetString());

			var readDoc = JsonDocument.Parse(JsonSerializer.Serialize(_ts.ReadResource(uri)));
			var contents = readDoc.RootElement.GetProperty("contents")[0];
			Assert.Equal(uri, contents.GetProperty("uri").GetString());
			Assert.Equal("image/png", contents.GetProperty("mimeType").GetString());
			var bytes = System.Convert.FromBase64String(contents.GetProperty("blob").GetString()!);
			Assert.Equal(0x89, bytes[0]); // PNG magic
		}

		[Fact]
		public void Resources_read_unknown_uri_errors()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.ReadResource("bizhawk://doesnotexist"));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Resource_template_read_returns_raw_bytes()
		{
			_apis.MemoryApi.Bytes[0xFBCA] = 0x00;
			_apis.MemoryApi.Bytes[0xFBCB] = 0x08;
			_apis.MemoryApi.Bytes[0xFBCC] = 0xFF;
			var readDoc = JsonDocument.Parse(JsonSerializer.Serialize(_ts.ReadResource("bizhawk://read/68K%20RAM/fbca:fbcd")));
			var contents = readDoc.RootElement.GetProperty("contents")[0];
			Assert.Equal("bizhawk://read/68K%20RAM/fbca:fbcd", contents.GetProperty("uri").GetString());
			Assert.Equal("application/octet-stream", contents.GetProperty("mimeType").GetString());
			var bytes = System.Convert.FromBase64String(contents.GetProperty("blob").GetString()!);
			Assert.Equal(3, bytes.Length);
			Assert.Equal(new byte[] { 0x00, 0x08, 0xFF }, bytes);
		}

		[Fact]
		public void Resource_template_respects_domain_size_and_hex_range()
		{
			// M68K BUS takes raw bus addresses; read 0xFF0000..0xFF0002
			_apis.MemoryApi.Bytes[0xFF0000] = 0xAA;
			var readDoc = JsonDocument.Parse(JsonSerializer.Serialize(_ts.ReadResource("bizhawk://read/M68K%20BUS/FF0000:FF0001")));
			var contents = readDoc.RootElement.GetProperty("contents")[0];
			var bytes = System.Convert.FromBase64String(contents.GetProperty("blob").GetString()!);
			Assert.Single(bytes);
			Assert.Equal(0xAA, bytes[0]);
		}

		[Fact]
		public void Resource_template_errors_on_bad_range()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.ReadResource("bizhawk://read/68K%20RAM/zz:10"));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Resource_template_errors_on_empty_or_inverted_range()
		{
			Assert.Throws<JsonRpc.Error>(() => _ts.ReadResource("bizhawk://read/68K%20RAM/10:10"));
			Assert.Throws<JsonRpc.Error>(() => _ts.ReadResource("bizhawk://read/68K%20RAM/20:10"));
		}

		[Fact]
		public void Resource_template_rejects_oversized_range()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.ReadResource("bizhawk://read/68K%20RAM/0:10001"));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Resource_template_list_lists_read_template()
		{
			var listed = _ts.ListResourceTemplates();
			var doc = JsonDocument.Parse(JsonSerializer.Serialize(listed));
			var templates = doc.RootElement.GetProperty("resourceTemplates");
			Assert.Equal(2, templates.GetArrayLength());
			Assert.Equal("bizhawk://read/{domain}/{range}", templates[0].GetProperty("uriTemplate").GetString());
		}
	}

	public class MiscToolTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public MiscToolTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Get_joypad_returns_buttons()
		{
			var res = Parse(_ts.Call("get_joypad", null));
			Assert.True(res.GetProperty("buttons").GetProperty("A").GetBoolean());
		}

		[Fact]
		public void Press_buttons_forwards_to_joypad()
		{
			_ts.Call("press_buttons", TestHelpers.Js("{\"buttons\":{\"A\":true,\"Right\":true},\"controller\":2}"));
			Assert.True(_apis.JoypadApi.LastSet!["A"]);
			Assert.Equal(2, _apis.JoypadApi.LastController);
		}

		[Fact]
		public void Save_load_state_forward()
		{
			_ts.Call("save_state", TestHelpers.Js("{\"path\":\"C:/x.State\"}"));
			Assert.Equal("/mnt/c/x.State", _apis.SaveStateApi.SavedTo);
			var res = _ts.Call("load_state", TestHelpers.Js("{\"path\":\"C:/x.State\"}"));
			Assert.Contains("loaded", res);
		}

		[Fact]
		public void Save_load_slot_forward()
		{
			_ts.Call("save_slot", TestHelpers.Js("{\"slot\":3}"));
			Assert.Equal(3, _apis.SaveStateApi.SavedSlot);
			var res = _ts.Call("load_slot", TestHelpers.Js("{\"slot\":3}"));
			Assert.Equal(3, _apis.SaveStateApi.LoadedSlot);
			Assert.Contains("loaded", res);
		}

		[Fact]
		public void Save_slot_rejects_out_of_range()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("save_slot", TestHelpers.Js("{\"slot\":0}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("load_slot", TestHelpers.Js("{\"slot\":11}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Mem_state_save_then_load_restores_memory()
		{
			_apis.EnableMemStates();
			_apis.MemoryApi.Bytes[100] = 0x11;
			_apis.MemoryApi.Bytes[200] = 0x22;

			var res = Parse(_ts.Call("memstate_save", TestHelpers.Js("{\"slot\":\"pre-jump\"}")));
			Assert.Equal("pre-jump", res.GetProperty("slot").GetString());
			Assert.True(res.GetProperty("size").GetInt32() > 0);

			// trash the memory, then restore
			_apis.MemoryApi.Bytes[100] = 0x99;
			_apis.MemoryApi.Bytes[200] = 0x77;
			_apis.MemoryApi.Bytes[300] = 0x55;
			var load = Parse(_ts.Call("memstate_load", TestHelpers.Js("{\"slot\":\"pre-jump\"}")));
			Assert.Equal("pre-jump", load.GetProperty("slot").GetString());

			Assert.Equal((byte)0x11, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x22, _apis.MemoryApi.Bytes[200]);
			Assert.False(_apis.MemoryApi.Bytes.ContainsKey(300)); // post-save writes are gone
		}

		[Fact]
		public void Mem_state_slots_are_independent()
		{
			_apis.EnableMemStates();
			_apis.MemoryApi.Bytes[100] = 1;
			_ts.Call("memstate_save", TestHelpers.Js("{\"slot\":\"a\"}"));
			_apis.MemoryApi.Bytes[100] = 2;
			_ts.Call("memstate_save", TestHelpers.Js("{\"slot\":\"b\"}"));

			_apis.MemoryApi.Bytes[100] = 99;
			_ts.Call("memstate_load", TestHelpers.Js("{\"slot\":\"a\"}"));
			Assert.Equal((byte)1, _apis.MemoryApi.Bytes[100]);
			_ts.Call("memstate_load", TestHelpers.Js("{\"slot\":\"b\"}"));
			Assert.Equal((byte)2, _apis.MemoryApi.Bytes[100]);

			var list = Parse(_ts.Call("memstate_list", null));
			Assert.Equal(2, list.GetProperty("states").GetArrayLength());
		}

		[Fact]
		public void Mem_state_load_unknown_slot_errors()
		{
			_apis.EnableMemStates();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("memstate_load", TestHelpers.Js("{\"slot\":\"nope\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Mem_state_unsupported_core_errors_clearly()
		{
			// no Emulator wired → same shape as watchpoints on non-gpgx cores
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("memstate_save", TestHelpers.Js("{\"slot\":\"a\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("IEmulator", ex.Message);
		}

		[Fact]
		public void Mem_state_rejects_empty_slot_name()
		{
			_apis.EnableMemStates();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("memstate_save", TestHelpers.Js("{\"slot\":\"\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("memstate_save", TestHelpers.Js("{\"slot\":\"  \"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Freeze_add_snapshots_current_value()
		{
			_apis.EnableCheats();
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"width\":8,\"domain\":\"68K RAM\"}")));
			Assert.Equal(100L, res.GetProperty("address").GetInt64());
			Assert.Equal(0x42, res.GetProperty("value").GetInt32());
			var cheat = _apis.Cheats!.Single();
			Assert.Equal(100L, cheat.Address);
			Assert.Equal(0x42, cheat.Value);
			Assert.Equal("68K RAM", cheat.Domain.Name);
		}

		[Fact]
		public void Freeze_add_with_explicit_value_and_note()
		{
			_apis.EnableCheats();
			var res = Parse(_ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":513,\"domain\":\"68K RAM\",\"note\":\"lives\"}")));
			Assert.Equal("lives", res.GetProperty("note").GetString());
			var cheat = _apis.Cheats!.Single();
			Assert.Equal(513, cheat.Value);
			Assert.Equal("lives", cheat.Name);
			Assert.Equal(WatchSize.Word, cheat.Size);
			Assert.True(cheat.BigEndian == true);
		}

		[Fact]
		public void Freeze_add_range_snapshot_and_fill()
		{
			_apis.EnableCheats();
			for (var i = 0; i < 5; i++) _apis.MemoryApi.Bytes[100 + i] = (byte)(i + 1);

			var res = Parse(_ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"length\":5,\"domain\":\"68K RAM\"}")));
			Assert.Equal("snapshot", res.GetProperty("mode").GetString());
			Assert.Equal(5, res.GetProperty("frozen").GetInt32());
			Assert.Equal(5, _apis.Cheats!.Count);
			Assert.Equal(3, _apis.Cheats.ElementAt(2).Value); // byte 3 of the snapshot

			var fill = Parse(_ts.Call("freeze_add", TestHelpers.Js("{\"address\":200,\"length\":4,\"value\":0,\"domain\":\"68K RAM\"}")));
			Assert.Equal("fill", fill.GetProperty("mode").GetString());
			Assert.Equal(9, _apis.Cheats!.Count);
			Assert.All(_apis.Cheats!.Skip(5), c => Assert.Equal(0, c.Value));
		}

		[Fact]
		public void Freeze_add_rejects_bad_inputs()
		{
			_apis.EnableCheats();
			// range with width 16
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"length\":2,\"width\":16}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			// range fill value not a byte
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"length\":2,\"value\":256}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			// single value not fitting the width
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"width\":8,\"value\":999}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			// non-writable domain (VRAM in the fake)
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("freeze_add", TestHelpers.Js("{\"address\":0,\"width\":8,\"domain\":\"VRAM\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("not writable", ex.Message);
		}

		[Fact]
		public void Freeze_remove_by_note_and_by_range()
		{
			_apis.EnableCheats();
			_ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"note\":\"lives\"}"));
			_ts.Call("freeze_add", TestHelpers.Js("{\"address\":200,\"note\":\"timer\"}"));
			_ts.Call("freeze_add", TestHelpers.Js("{\"address\":300,\"length\":4}"));

			var byNote = Parse(_ts.Call("freeze_remove", TestHelpers.Js("{\"note\":\"lives\"}")));
			Assert.Equal(1, byNote.GetProperty("removed").GetInt32());
			Assert.Equal(5, _apis.Cheats!.Count);

			var byRange = Parse(_ts.Call("freeze_remove", TestHelpers.Js("{\"address\":301,\"length\":2,\"domain\":\"68K RAM\"}")));
			Assert.Equal(2, byRange.GetProperty("removed").GetInt32());
			Assert.Equal(3, _apis.Cheats!.Count);
			Assert.Equal(200L, _apis.Cheats!.First().Address); // timer survived
		}

		[Fact]
		public void Freeze_list_and_clear()
		{
			_apis.EnableCheats();
			_ts.Call("freeze_add", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":513,\"note\":\"hp\"}"));
			_ts.Call("freeze_add", TestHelpers.Js("{\"address\":200,\"value\":7}"));

			var list = Parse(_ts.Call("freeze_list", null));
			Assert.Equal(2, list.GetProperty("count").GetInt32());
			var first = list.GetProperty("freezes")[0];
			Assert.Equal("hp", first.GetProperty("name").GetString());
			Assert.Equal(16, first.GetProperty("width").GetInt32());
			Assert.Equal("big", first.GetProperty("endianness").GetString());
			Assert.True(first.GetProperty("enabled").GetBoolean());

			var clear = Parse(_ts.Call("freeze_clear", null));
			Assert.Equal(2, clear.GetProperty("cleared").GetInt32());
			Assert.Empty(_apis.Cheats!);
		}

		[Fact]
		public void Freeze_unsupported_errors_clearly()
		{
			// domain resolvable, but no cheat list wired → like a host where
			// MainForm.CheatList is unreachable
			_apis.MemoryApi.DomainList = new FakeMemoryApi.FakeDomainList(_apis.MemoryApi.Bytes);
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("freeze_add", TestHelpers.Js("{\"address\":100}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("cheat list", ex.Message);
		}

		[Fact]
		public void Write_memory_with_freeze_registers_cheat()
		{
			_apis.EnableCheats();
			var res = Parse(_ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":513,\"freeze\":true,\"domain\":\"68K RAM\"}")));
			Assert.True(res.GetProperty("frozen").GetBoolean());
			Assert.Equal((byte)0x02, _apis.MemoryApi.Bytes[100]); // big-endian write
			var cheat = _apis.Cheats!.Single();
			Assert.Equal(100L, cheat.Address);
			Assert.Equal(513, cheat.Value);
			Assert.Equal(WatchSize.Word, cheat.Size);
		}

		[Fact]
		public void Write_memory_freeze_failure_does_not_write()
		{
			// cheat list unreachable: the call must error BEFORE the write
			// happens (2026-08-03 QA finding: write-then-error inconsistency)
			_apis.MemoryApi.DomainList = new FakeMemoryApi.FakeDomainList(_apis.MemoryApi.Bytes);
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("write_memory", TestHelpers.Js("{\"address\":100,\"width\":8,\"value\":7,\"freeze\":true,\"domain\":\"68K RAM\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.False(_apis.MemoryApi.Bytes.ContainsKey(100));
		}

		[Fact]
		public void Write_many_freezes_only_marked_items()
		{
			_apis.EnableCheats();
			_ts.Call("write_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":8,\"value\":1,\"freeze\":true},{\"address\":200,\"width\":8,\"value\":2}]}"));
			Assert.Single(_apis.Cheats!);
			Assert.Equal(100L, _apis.Cheats!.Single().Address);
		}

		[Fact]
		public void Write_range_with_freeze_registers_whole_range()
		{
			_apis.EnableCheats();
			_ts.Call("write_range", TestHelpers.Js("{\"address\":100,\"values\":[1,2,3],\"freeze\":true,\"domain\":\"68K RAM\"}"));
			Assert.Equal(3, _apis.Cheats!.Count);
			Assert.Equal(101L, _apis.Cheats!.ElementAt(1).Address);
			Assert.Equal(2, _apis.Cheats!.ElementAt(1).Value);
		}

		[Fact]
		public void Lua_exec_returns_results()
		{
			var lua = _apis.EnableLua();
			var res = Parse(_ts.Call("lua_exec", TestHelpers.Js("{\"code\":\"memory.read_u8(0xFF2506)\"}")));
			Assert.True(res.GetProperty("executed").GetBoolean());
			Assert.Equal("42", res.GetProperty("result")[0].GetString());
			Assert.Equal("memory.read_u8(0xFF2506)", lua.Executed.Single());
		}

		[Fact]
		public void Lua_exec_reports_script_errors_as_result_not_server_error()
		{
			var lua = _apis.EnableLua();
			lua.ExecuteError = new Exception("attempt to call a nil value (global 'nope')");
			var res = Parse(_ts.Call("lua_exec", TestHelpers.Js("{\"code\":\"nope()\"}")));
			Assert.False(res.GetProperty("executed").GetBoolean());
			Assert.Contains("nil value", res.GetProperty("error").GetString());
		}

		[Fact]
		public void Lua_unsupported_errors_clearly()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("lua_exec", TestHelpers.Js("{\"code\":\"1\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("Lua", ex.Message);
		}

		private string _luaTempScript(string name, string content)
		{
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"bizhawk-mcp-test-{name}.lua");
			System.IO.File.WriteAllText(path, content);
			return path;
		}

		[Fact]
		public void Lua_load_adds_and_starts_script()
		{
			var lua = _apis.EnableLua();
			string path = _luaTempScript("load", "emu.frameadvance()\n");
			var res = Parse(_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{path}\"}}")));
			Assert.True(res.GetProperty("enabled").GetBoolean());
			Assert.Single(lua.ScriptList);
			Assert.Equal(1, lua.SpawnCalls);
			Assert.True(lua.ScriptList[0].Enabled);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Lua_load_missing_file_errors()
		{
			_apis.EnableLua();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("lua_load", TestHelpers.Js("{\"path\":\"C:/nope/not-there.lua\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("not found", ex.Message);
		}

		[Fact]
		public void Lua_load_reloads_disabled_script()
		{
			var lua = _apis.EnableLua();
			string path = _luaTempScript("reload", "emu.frameadvance()\n");
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{path}\"}}"));
			_ts.Call("lua_disable", TestHelpers.Js($"{{\"path\":\"{path}\"}}"));
			Assert.False(lua.ScriptList[0].Enabled);
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{path}\"}}"));
			Assert.Equal(2, lua.SpawnCalls); // re-started, not duplicated
			Assert.Single(lua.ScriptList);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Lua_unload_removes_and_stops()
		{
			var lua = _apis.EnableLua();
			string p1 = _luaTempScript("u1", "emu.frameadvance()\n");
			string p2 = _luaTempScript("u2", "emu.frameadvance()\n");
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{p1}\"}}"));
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{p2}\"}}"));
			var res = Parse(_ts.Call("lua_unload", TestHelpers.Js($"{{\"path\":\"{p1}\"}}")));
			Assert.Equal(p1, res.GetProperty("removed").GetString());
			Assert.Single(lua.ScriptList);
			Assert.Equal(p2, lua.ScriptList[0].Path);
			System.IO.File.Delete(p1);
			System.IO.File.Delete(p2);
		}

		[Fact]
		public void Lua_enable_disable_transitions()
		{
			var lua = _apis.EnableLua();
			string path = _luaTempScript("ed", "emu.frameadvance()\n");
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{path}\"}}"));
			var dis = Parse(_ts.Call("lua_disable", TestHelpers.Js($"{{\"path\":\"{path}\"}}")));
			Assert.False(dis.GetProperty("enabled").GetBoolean());
			var en = Parse(_ts.Call("lua_enable", TestHelpers.Js($"{{\"path\":\"{path}\"}}")));
			Assert.True(en.GetProperty("enabled").GetBoolean());
			Assert.Equal(2, lua.SpawnCalls);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Lua_list_lists_scripts()
		{
			_apis.EnableLua();
			string p1 = _luaTempScript("l1", "emu.frameadvance()\n");
			string p2 = _luaTempScript("l2", "emu.frameadvance()\n");
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{p1}\"}}"));
			_ts.Call("lua_load", TestHelpers.Js($"{{\"path\":\"{p2}\"}}"));
			var res = Parse(_ts.Call("lua_list", null));
			Assert.Equal(2, res.GetProperty("count").GetInt32());
			Assert.Equal(p1, res.GetProperty("scripts")[0].GetProperty("path").GetString());
			Assert.True(res.GetProperty("scripts")[0].GetProperty("enabled").GetBoolean());
			Assert.False(res.GetProperty("scripts")[0].GetProperty("paused").GetBoolean());
			System.IO.File.Delete(p1);
			System.IO.File.Delete(p2);
		}

		[Fact]
		public void Lua_docs_dumps_all_libraries_with_signatures()
		{
			_apis.EnableLua();
			var res = Parse(_ts.Call("lua_docs", null));
			Assert.Equal(2, res.GetProperty("count").GetInt32());
			Assert.Equal(2, res.GetProperty("libraries").GetArrayLength());
			// alphabetical: gui < memory
			var gui = res.GetProperty("libraries")[0];
			Assert.Equal("gui", gui.GetProperty("library").GetString());
			var memory = res.GetProperty("libraries")[1];
			Assert.Equal("memory", memory.GetProperty("library").GetString());
			var fn = memory.GetProperty("functions")[0];
			Assert.Equal("read_u8", fn.GetProperty("name").GetString());
			Assert.Equal("uint memory.read_u8(long addr, [string domain = nil])", fn.GetProperty("signature").GetString());
			Assert.Equal("read unsigned byte", fn.GetProperty("description").GetString());
			Assert.Equal("local v = memory.read_u8(0xFF2506)", fn.GetProperty("example").GetString());
			Assert.False(fn.GetProperty("deprecated").GetBoolean());
		}

		[Fact]
		public void Lua_docs_filters_by_library()
		{
			_apis.EnableLua();
			var res = Parse(_ts.Call("lua_docs", TestHelpers.Js("{\"library\":\"gui\"}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal("gui", res.GetProperty("libraries")[0].GetProperty("library").GetString());
			Assert.Equal("addmessage", res.GetProperty("libraries")[0].GetProperty("functions")[0].GetProperty("name").GetString());
		}

		[Fact]
		public void Lua_docs_unknown_library_errors()
		{
			_apis.EnableLua();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("lua_docs", TestHelpers.Js("{\"library\":\"nope\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("memory", ex.Message);
		}

		[Fact]
		public void Lua_docs_resource_reads_as_json_blob()
		{
			_apis.EnableLua();
			var res = _ts.ReadResource("bizhawk://lua-docs/memory");
			var blob = res["contents"] as List<object?> ?? new List<object?>();
			var first = (Dictionary<string, object?>)blob[0]!;
			Assert.Equal("application/json", first["mimeType"]);
			var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String((string)first["blob"]!));
			using var doc = JsonDocument.Parse(json);
			Assert.Equal(1, doc.RootElement.GetProperty("libraries").GetArrayLength());
			Assert.Equal("memory", doc.RootElement.GetProperty("libraries")[0].GetProperty("library").GetString());
		}

		[Fact]
		public void Host_path_wsl_to_windows_conversion()
		{
			// agent drives from WSL, EmuHawk runs on Windows
			Assert.Equal(@"F:\roms\kid.md", McpToolset.NormalizeHostPath("/mnt/f/roms/kid.md", windowsHost: true));
			Assert.Equal(@"F:\roms\kid.md", McpToolset.NormalizeHostPath("/mnt/F/roms/kid.md", windowsHost: true));
			Assert.Equal(@"C:\temp\shot.png", McpToolset.NormalizeHostPath("/mnt/c/temp/shot.png", windowsHost: true));
			// already a Windows path — unchanged
			Assert.Equal(@"F:\roms\kid.md", McpToolset.NormalizeHostPath(@"F:\roms\kid.md", windowsHost: true));
		}

		[Fact]
		public void Host_path_windows_to_wsl_conversion()
		{
			// agent passes a Windows path while EmuHawk runs on Linux (Mono)
			Assert.Equal("/mnt/f/roms/kid.md", McpToolset.NormalizeHostPath(@"F:\roms\kid.md", windowsHost: false));
			Assert.Equal("/mnt/f/roms/kid.md", McpToolset.NormalizeHostPath(@"F:/roms/kid.md", windowsHost: false));
			// already a WSL path — unchanged
			Assert.Equal("/mnt/f/roms/kid.md", McpToolset.NormalizeHostPath("/mnt/f/roms/kid.md", windowsHost: false));
			// relative paths and non-mount paths untouched
			Assert.Equal("roms/kid.md", McpToolset.NormalizeHostPath("roms/kid.md", windowsHost: false));
			Assert.Equal("/tmp/x.bin", McpToolset.NormalizeHostPath("/tmp/x.bin", windowsHost: true));
			Assert.Null(McpToolset.NormalizeHostPath(null, windowsHost: true));
		}

		[Fact]
		public void Wsl_path_converts_windows_output_paths_to_mnt_form()
		{
			// EmuHawk wrote C:\Users\... on a Windows host — the WSL agent
			// gets the /mnt/... form to read the file directly
			Assert.Equal("/mnt/c/Users/stealthc/AppData/Local/Temp/bizhawk-mcp/fixture-1.csv", McpToolset.WslPath(@"C:\Users\stealthc\AppData\Local\Temp\bizhawk-mcp\fixture-1.csv"));
			Assert.Equal("/mnt/f/temp/shot.png", McpToolset.WslPath(@"F:/temp/shot.png"));
			// already an agent-form path (Linux host) — unchanged
			Assert.Equal("/tmp/bizhawk-mcp/dump.bin", McpToolset.WslPath("/tmp/bizhawk-mcp/dump.bin"));
			// UNC and relative paths untouched, null passes through
			Assert.Equal(@"\\server\share\x.bin", McpToolset.WslPath(@"\\server\share\x.bin"));
			Assert.Equal("roms/kid.md", McpToolset.WslPath("roms/kid.md"));
			Assert.Null(McpToolset.WslPath(null));
		}

		[Fact]
		public void Overlay_text_draws_and_clears()
		{
			_ts.Call("overlay_text", TestHelpers.Js("{\"x\":1,\"y\":2,\"text\":\"hi\",\"fontsize\":12}"));
			Assert.Equal((1, 2, "hi", (int?)12), _apis.GuiApi.LastDraw);
			_ts.Call("clear_overlay", null);
			// clears the Client graphics surface, the text layer, and the list
			Assert.Equal(3, _apis.GuiApi.ClearTextCalls);
		}

		[Fact]
		public void Overlays_accumulate_and_redraw_all()
		{
			// drawing a new shape must NOT wipe the previous ones: the toolset
			// keeps a list and re-renders everything on every mutation
			_ts.Call("overlay_rect", TestHelpers.Js("{\"x\":1,\"y\":2,\"width\":10,\"height\":20}"));
			Assert.Equal(1, _apis.GuiApi.DrawCount); // one rect
			_ts.Call("overlay_line", TestHelpers.Js("{\"x1\":0,\"y1\":0,\"x2\":5,\"y2\":5}"));
			// re-rendered the rect again + the new line (2 draws this mutation)
			Assert.Equal((1, 2, 10, 20), _apis.GuiApi.LastRect);
			Assert.Equal((0, 0, 5, 5), _apis.GuiApi.LastLine);
			Assert.Equal(3, _apis.GuiApi.DrawCount);
		}

		[Fact]
		public void Overlay_rects_list_in_one_call()
		{
			_ts.Call("overlay_rect", TestHelpers.Js("{\"rects\":[{\"x\":1,\"y\":2,\"width\":3,\"height\":4},{\"x\":5,\"y\":6,\"width\":7,\"height\":8}]}"));
			Assert.Equal((5, 6, 7, 8), _apis.GuiApi.LastRect);
		}

		[Fact]
		public void Overlay_rect_and_line_no_longer_throw()
		{
			// regression: DrawRectangle/DrawLine without a surface used to throw
			// (Get2DRenderer(null) threw); WithSurface(Client, ...) fixes it
			_ts.Call("overlay_rect", TestHelpers.Js("{\"x\":1,\"y\":2,\"width\":10,\"height\":20,\"color\":\"#FF0000\"}"));
			Assert.Equal((1, 2, 10, 20), _apis.GuiApi.LastRect);
			_ts.Call("overlay_line", TestHelpers.Js("{\"x1\":0,\"y1\":0,\"x2\":5,\"y2\":5}"));
			Assert.Equal((0, 0, 5, 5), _apis.GuiApi.LastLine);
		}

		[Fact]
		public void Osd_message_forwards()
		{
			_ts.Call("osd_message", TestHelpers.Js("{\"message\":\"hello\",\"duration\":500}"));
			Assert.Contains("hello", _apis.GuiApi.Messages);
		}

		[Fact]
		public void Movie_info_returns_json()
		{
			var res = Parse(_ts.Call("movie_info", null));
			Assert.True(res.GetProperty("loaded").GetBoolean());
			Assert.Equal("test.bk2", res.GetProperty("filename").GetString());
			Assert.Equal((ulong)42, res.GetProperty("rerecords").GetUInt64());
		}

		[Fact]
		public void Movie_info_without_movie_returns_empty_not_crash()
		{
			_apis.MovieApi.Loaded = false;
			var res = Parse(_ts.Call("movie_info", null));
			Assert.False(res.GetProperty("loaded").GetBoolean());
			Assert.Equal(JsonValueKind.Null, res.GetProperty("filename").ValueKind);
			Assert.Equal(0, res.GetProperty("length").GetInt32());
		}

		[Fact]
		public void Movie_input_without_movie_errors_cleanly()
		{
			_apis.MovieApi.Loaded = false;
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("movie_input", TestHelpers.Js("{\"frame\":0}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Movie_input_returns_mnemonic()
		{
			var res = _ts.Call("movie_input", TestHelpers.Js("{\"frame\":0}"));
			Assert.Equal("|..|..|", res);
		}

		[Fact]
		public void Movie_start_without_path_starts_recording()
		{
			var res = _ts.Call("movie_start", null);
			Assert.Contains("recording", res);
			Assert.Equal("", _apis.MovieApi.PlayedPath);
		}

		[Fact]
		public void Movie_start_with_path_loads_and_plays()
		{
			var res = _ts.Call("movie_start", TestHelpers.Js("{\"path\":\"C:/movies/run.bk2\"}"));
			Assert.Contains("playing", res);
			Assert.Equal("/mnt/c/movies/run.bk2", _apis.MovieApi.PlayedPath);
		}

		[Fact]
		public void Movie_start_failure_reports()
		{
			_apis.MovieApi.PlayResult = false;
			var res = _ts.Call("movie_start", TestHelpers.Js("{\"path\":\"C:/movies/nope.bk2\"}"));
			Assert.Contains("failed", res);
		}

		[Fact]
		public void Movie_save_and_stop_forward()
		{
			_ts.Call("movie_save", TestHelpers.Js("{\"path\":\"C:/movies/out.bk2\"}"));
			Assert.Equal("/mnt/c/movies/out.bk2", _apis.MovieApi.SavedPath);
			var res = _ts.Call("movie_stop", null);
			Assert.Contains("stopped", res);
			Assert.True(_apis.MovieApi.Stopped);
		}

		[Fact]
		public void Host_input_returns_pressed_and_mouse()
		{
			var res = Parse(_ts.Call("host_input", null));
			Assert.Equal("Shift+A", res.GetProperty("pressed")[0].GetString());
			Assert.Equal(10, res.GetProperty("mouse").GetProperty("X").GetInt32());
		}

		[Fact]
		public void Userdata_set_get_clear()
		{
			_ts.Call("userdata_set", TestHelpers.Js("{\"key\":\"k\",\"value\":\"v\"}"));
			var res = Parse(_ts.Call("userdata_get", TestHelpers.Js("{\"key\":\"k\"}")));
			Assert.Equal("v", res.GetProperty("value").GetString());
			_ts.Call("userdata_clear", null);
			Assert.Empty(_apis.UserDataApi.Data);
		}

		[Fact]
		public void Shutdown_stops_server()
		{
			_ts.Call("shutdown", null);
			Assert.Equal(1, _apis.StopServerCalls);
		}
	}

	// ── code/data logger (ICodeDataLogger service) ────────────────────────────
	public class CodeDataLoggerTests
	{
		private readonly FakeApis _apis = new();
		private readonly McpToolset _ts;

		public CodeDataLoggerTests() => _ts = _apis.Toolset();

		private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

		[Fact]
		public void Cdl_start_installs_log_and_lists_blocks()
		{
			var cdl = _apis.EnableCdl();
			var res = Parse(_ts.Call("cdl_start", null));
			Assert.True(res.GetProperty("active").GetBoolean());
			var blocks = res.GetProperty("blocks");
			Assert.Equal(3, blocks.GetArrayLength());
			Assert.Equal("MD CART", blocks[0].GetProperty("name").GetString());
			Assert.Equal(1024 * 1024, blocks[0].GetProperty("size").GetInt32());
			Assert.Equal("68K RAM", blocks[1].GetProperty("name").GetString());
			Assert.Equal(65536, blocks[1].GetProperty("size").GetInt32());
			Assert.Equal("Z80 RAM", blocks[2].GetProperty("name").GetString());
			Assert.NotNull(cdl.Installed);
		}

		[Fact]
		public void Cdl_get_reports_ranges_counts_flags_and_coverage()
		{
			var cdl = _apis.EnableCdl();
			_ts.Call("cdl_start", null);
			cdl.Exec("68K RAM", 0x100, 0x01);
			cdl.Exec("68K RAM", 0x101, 0x01);
			cdl.Exec("68K RAM", 0x102, 0x05); // Exec68k | Data68k
			cdl.Exec("68K RAM", 0x200, 0x01);
			cdl.Exec("68K RAM", 0x300, 0x04); // data only

			var res = Parse(_ts.Call("cdl_get", null));
			Assert.True(res.GetProperty("active").GetBoolean());
			var ram = res.GetProperty("blocks")[1];
			Assert.Equal("68K RAM", ram.GetProperty("name").GetString());
			Assert.Equal(4, ram.GetProperty("exec_bytes").GetInt32()); // 0x100..0x102 + 0x200
			Assert.Equal(5, ram.GetProperty("touched_bytes").GetInt32());
			Assert.Equal(0.01, ram.GetProperty("exec_pct").GetDouble()); // 4/65536

			var ranges = ram.GetProperty("ranges");
			Assert.Equal(2, ranges.GetArrayLength());
			Assert.Equal(0x100, ranges[0].GetProperty("start").GetInt32());
			Assert.Equal(0x103, ranges[0].GetProperty("end").GetInt32());
			Assert.Equal(0x200, ranges[1].GetProperty("start").GetInt32());
			Assert.Equal(0x201, ranges[1].GetProperty("end").GetInt32());

			var fc = ram.GetProperty("flag_counts");
			Assert.Equal(4, fc.GetProperty("Exec68k").GetInt32());
			Assert.Equal(2, fc.GetProperty("Data68k").GetInt32()); // 0x102 (Exec|Data) + 0x300 (data only)
			Assert.Equal(0, fc.GetProperty("DMASource").GetInt32());
		}

		[Fact]
		public void Cdl_get_mask_any_counts_data_only_bytes_and_block_filter_works()
		{
			var cdl = _apis.EnableCdl();
			_ts.Call("cdl_start", null);
			cdl.Exec("68K RAM", 0x300, 0x04); // data only

			// default mask "exec" ignores the data-only byte
			var exec = Parse(_ts.Call("cdl_get", null));
			var ramExec = exec.GetProperty("blocks")[1];
			Assert.Equal(0, ramExec.GetProperty("exec_bytes").GetInt32());
			Assert.Equal(1, ramExec.GetProperty("touched_bytes").GetInt32());
			Assert.Equal(0, ramExec.GetProperty("ranges").GetArrayLength());

			// "any" mask includes it
			var any = Parse(_ts.Call("cdl_get", TestHelpers.Js("{\"mask\":\"any\"}")));
			var ramAny = any.GetProperty("blocks")[1];
			Assert.Equal(1, ramAny.GetProperty("exec_bytes").GetInt32());
			Assert.Equal(1, ramAny.GetProperty("ranges").GetArrayLength());
			Assert.Equal(0x300, ramAny.GetProperty("ranges")[0].GetProperty("start").GetInt32());
			Assert.Equal(0x301, ramAny.GetProperty("ranges")[0].GetProperty("end").GetInt32());

			// block filter
			var filtered = Parse(_ts.Call("cdl_get", TestHelpers.Js("{\"block\":\"MD CART\"}")));
			Assert.Equal(1, filtered.GetProperty("blocks").GetArrayLength());
			Assert.Equal("MD CART", filtered.GetProperty("blocks")[0].GetProperty("name").GetString());

			// unknown block → clear error
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("cdl_get", TestHelpers.Js("{\"block\":\"nope\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Cdl_stop_uninstalls_but_keeps_data()
		{
			var cdl = _apis.EnableCdl();
			_ts.Call("cdl_start", null);
			cdl.Exec("68K RAM", 0x100, 0x01);

			var stop = Parse(_ts.Call("cdl_stop", null));
			Assert.False(stop.GetProperty("active").GetBoolean());
			Assert.Null(cdl.Installed);

			// data retained after stop
			var res = Parse(_ts.Call("cdl_get", null));
			Assert.False(res.GetProperty("active").GetBoolean());
			Assert.Equal(1, res.GetProperty("blocks")[1].GetProperty("exec_bytes").GetInt32());
		}

		[Fact]
		public void Cdl_get_and_export_before_start_error()
		{
			_apis.EnableCdl();
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("cdl_get", null));
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("cdl_stop", null));
			Assert.Throws<JsonRpc.Error>(() => _ts.Call("cdl_export", null));
		}

		[Fact]
		public void Cdl_export_writes_cdl_file_and_registers_artifact()
		{
			_apis.EnableCdl();
			_ts.Call("cdl_start", null);
			string tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cdl-test-{Guid.NewGuid():N}.cdl");
			var res = Parse(_ts.Call("cdl_export", TestHelpers.Js("{\"path\":\"" + tmp + "\"}")));
			Assert.Equal("cdl", res.GetProperty("format").GetString());
			Assert.StartsWith("bizhawk://", res.GetProperty("resource").GetString());
			Assert.True(System.IO.File.Exists(tmp));
			var bytes = System.IO.File.ReadAllBytes(tmp);
			Assert.True(bytes.Length > 13);
			// BinaryWriter writes a 7-bit length prefix before the string
			Assert.Equal(13, bytes[0]);
			Assert.Equal("BIZHAWK-CDL-2", System.Text.Encoding.ASCII.GetString(bytes, 1, 13));
			System.IO.File.Delete(tmp);
		}

		[Fact]
		public void Cdl_export_text_format_lists_blocks()
		{
			_apis.EnableCdl();
			_ts.Call("cdl_start", null);
			var res = Parse(_ts.Call("cdl_export", TestHelpers.Js("{\"format\":\"text\"}")));
			string path = res.GetProperty("path").GetString()!;
			var text = System.IO.File.ReadAllText(path);
			Assert.Contains("BLOCK 68K RAM", text);
			Assert.Contains("BLOCK MD CART", text);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Cdl_unsupported_core_errors_clearly()
		{
			// no Emulator wired → same shape as watchpoints on unsupported cores
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("cdl_start", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("IEmulator", ex.Message);
		}
	}
}
