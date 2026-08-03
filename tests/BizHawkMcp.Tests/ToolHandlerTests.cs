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
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":8,\"value\":165}"));
			var res = Parse(_ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":100,\"width\":8}")));
			Assert.Equal((ulong)0xA5, res.GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Write_u16_respects_big_endian_default_on_genesis()
		{
			_ts.Call("bizhawk_get_info", null); // triggers EnsureEndianness for GEN
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827}"));
			// big-endian: high byte first
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Write_u16_little_endian_on_nes()
		{
			_apis.EmulationApi.SystemId = "NES";
			_ts.Call("bizhawk_get_info", null);
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Write_u16_respects_explicit_big_endian_param_on_nes()
		{
			// NES defaults little, but an explicit param flips it for this call
			_apis.EmulationApi.SystemId = "NES";
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"endianness\":\"big\"}"));
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Domain_default_is_little_for_z80_ram()
		{
			// Genesis: the Z80 sound CPU memory is little-endian even though
			// the 68K main memory is big-endian — the domain decides.
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"domain\":\"Z80 RAM\"}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Domain_default_is_big_for_68k_ram()
		{
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"domain\":\"68K RAM\"}"));
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Explicit_little_endian_param_on_z80_sticks_for_call()
		{
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827,\"domain\":\"Z80 RAM\",\"endianness\":\"little\"}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)0x60, _apis.MemoryApi.Bytes[101]);
		}

		[Fact]
		public void Read_memory_reports_used_endianness()
		{
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"domain\":\"68K RAM\"}")));
			Assert.Equal((ulong)8, res.GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("endianness").GetString());
			res = Parse(_ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"domain\":\"Z80 RAM\"}")));
			Assert.Equal((ulong)2048, res.GetProperty("value").GetUInt64());
			Assert.Equal("little", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Invalid_endianness_param_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":0,\"endianness\":\"sideways\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Set_big_endian_overrides_core_default()
		{
			_ts.Call("bizhawk_get_info", null); // GEN → BE
			_ts.Call("bizhawk_set_big_endian", TestHelpers.Js("{\"enabled\":false}"));
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":100,\"width\":16,\"value\":24827}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[100]); // LE now
			_ts.Call("bizhawk_get_info", null);
			// override sticks: still LE
			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":200,\"width\":16,\"value\":24827}"));
			Assert.Equal((byte)0xFB, _apis.MemoryApi.Bytes[200]);
		}

		[Fact]
		public void Get_info_reports_endianness_and_pause()
		{
			_apis.EmulationApi.SystemId = "GEN";
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("bizhawk_get_info", null));
			Assert.Equal("big", res.GetProperty("endianness").GetString());
			Assert.True(res.GetProperty("paused").GetBoolean());
			Assert.Equal("GEN", res.GetProperty("system_id").GetString());
		}

		[Fact]
		public void Get_info_reports_little_on_gb()
		{
			_apis.EmulationApi.SystemId = "GB";
			var res = Parse(_ts.Call("bizhawk_get_info", null));
			Assert.Equal("little", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Invalid_width_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":0,\"width\":7}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Missing_args_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_write_memory", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Value_out_of_width_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":0,\"width\":8,\"value\":300}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Address_outside_domain_rejected()
		{
			// 68K RAM fake has size 65536
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":65536,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":65535,\"width\":16,\"value\":1}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Address_in_domain_accepted()
		{
			_ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":65534,\"width\":16}"));
		}

		[Fact]
		public void Bus_domain_masks_32bit_address_like_hardware()
		{
			// Games (e.g. Kid Chameleon) reference RAM as 0xFFFFxxxx in the
			// disassembly; the 68K has a 24-bit bus so 0xFFFFF832 == 0xFFF832.
			_apis.MemoryApi.Bytes[0xFFF832] = 0xAB;
			var res = Parse(_ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":4294965298,\"domain\":\"M68K BUS\",\"width\":8}")));
			// 4294965298 = 0xFFFFF832
			Assert.Equal((ulong)0xAB, res.GetProperty("value").GetUInt64());

			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"address\":4294965298,\"domain\":\"M68K BUS\",\"width\":8,\"value\":205}"));
			Assert.Equal((byte)0xCD, _apis.MemoryApi.Bytes[0xFFF832]);
		}

		[Fact]
		public void Linear_domain_rejects_address_beyond_size_even_with_high_bits()
		{
			// 68K RAM is a linear 64KB domain: 0x10001 is NOT the same as 0x0001
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":65537,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Non_68k_core_keeps_strict_bus_check()
		{
			// Only 68000-family cores (24-bit bus) mask 32-bit addresses; a
			// PSX/NES-style core must keep rejecting out-of-range bus addresses.
			_apis.EmulationApi.SystemId = "PSX";
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"address\":4294965298,\"domain\":\"M68K BUS\",\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void List_memory_domains_returns_names_and_sizes()
		{
			var res = Parse(_ts.Call("bizhawk_list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.Equal((ulong)65536, domains.GetProperty("68K RAM").GetProperty("size").GetUInt64());
			Assert.Equal("68K RAM", res.GetProperty("current").GetString());
		}

		[Fact]
		public void List_memory_domains_reports_bus_base_for_genesis()
		{
			var res = Parse(_ts.Call("bizhawk_list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.Equal((long)0xFF0000, domains.GetProperty("68K RAM").GetProperty("bus_base").GetInt64());
			Assert.Equal((long)0xA00000, domains.GetProperty("Z80 RAM").GetProperty("bus_base").GetInt64());
		}

		[Fact]
		public void List_memory_domains_omits_bus_base_when_unknown()
		{
			_apis.EmulationApi.SystemId = "PSX";
			var res = Parse(_ts.Call("bizhawk_list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.False(domains.GetProperty("68K RAM").TryGetProperty("bus_base", out _));
		}

		[Fact]
		public void Search_memory_finds_exact_byte()
		{
			_apis.MemoryApi.Bytes[10] = 0x42;
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"max_results\":10}")));
			Assert.Equal(2, res.GetProperty("count").GetInt32());
			Assert.Equal((long)10, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
			Assert.Equal((long)100, res.GetProperty("matches")[1].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_respects_max_results()
		{
			for (int i = 0; i < 50; i++) _apis.MemoryApi.Bytes[i] = 0x42;
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"max_results\":10}")));
			Assert.Equal(10, res.GetProperty("count").GetInt32());
		}

		[Fact]
		public void Search_memory_restricts_to_addresses()
		{
			_apis.MemoryApi.Bytes[10] = 0x42;
			_apis.MemoryApi.Bytes[100] = 0x42;
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":66,\"width\":8,\"addresses\":[10]}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
		}

		[Fact]
		public void Search_memory_u16_respects_big_endian_on_genesis()
		{
			// bytes 00 08 at 100 = 8 in BE, 2048 in LE (the mainFunction regression)
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"max_results\":10}")));
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
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":305419896,\"width\":32,\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)100, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_respects_little_endian_override()
		{
			// on GEN the override to LE must make 08 00 match value 8
			_apis.MemoryApi.Bytes[100] = 0x08;
			_apis.MemoryApi.Bytes[101] = 0x00;
			_ts.Call("bizhawk_set_big_endian", TestHelpers.Js("{\"enabled\":false}"));
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal((long)100, res.GetProperty("matches")[0].GetProperty("address").GetInt64());
		}

		[Fact]
		public void Search_memory_addresses_scan_respects_big_endian()
		{
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"addresses\":[100]}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
		}

		[Fact]
		public void Search_memory_uses_domain_endianness_and_reports_it()
		{
			// same bytes 00 08: big on 68K RAM (=8), little on Z80 RAM (=2048)
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":8,\"width\":16,\"domain\":\"68K RAM\",\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal("big", res.GetProperty("endianness").GetString());
			res = Parse(_ts.Call("bizhawk_search_memory", TestHelpers.Js("{\"value\":2048,\"width\":16,\"domain\":\"Z80 RAM\",\"max_results\":10}")));
			Assert.Equal(1, res.GetProperty("count").GetInt32());
			Assert.Equal("little", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Hash_region_returns_hash()
		{
			var res = Parse(_ts.Call("bizhawk_hash_region", TestHelpers.Js("{\"address\":0,\"length\":64}")));
			Assert.Equal("deadbeef", res.GetProperty("hash").GetString());
		}

		[Fact]
		public void Signed_read_write_roundtrip()
		{
			_ts.Call("bizhawk_write_signed", TestHelpers.Js("{\"address\":10,\"width\":16,\"value\":-1234}"));
			var res = Parse(_ts.Call("bizhawk_read_signed", TestHelpers.Js("{\"address\":10,\"width\":16}")));
			Assert.Equal(-1234L, res.GetProperty("value").GetInt64());
		}

		[Fact]
		public void Float_read_write_roundtrip()
		{
			_ts.Call("bizhawk_write_float", TestHelpers.Js("{\"address\":20,\"value\":3.5}"));
			var res = Parse(_ts.Call("bizhawk_read_float", TestHelpers.Js("{\"address\":20}")));
			Assert.Equal(3.5f, res.GetProperty("value").GetSingle());
		}

		[Fact]
		public void Read_many_reads_all_items()
		{
			_apis.MemoryApi.Bytes[10] = 0x11;
			_apis.MemoryApi.Bytes[20] = 0x22;
			_apis.MemoryApi.Bytes[30] = 0x33;
			var res = Parse(_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":10,\"width\":8},{\"address\":20,\"width\":8},{\"address\":30,\"width\":8}]}")));
			var reads = res.GetProperty("reads");
			Assert.Equal(3, reads.GetArrayLength());
			Assert.Equal((ulong)0x11, reads[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x33, reads[2].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Read_many_rejects_bad_item()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":10,\"width\":7}]}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Read_many_u16_respects_big_endian_on_genesis()
		{
			// mainFunction regression: bytes 00 08 = 8 in BE, 2048 in LE
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":16}]}")));
			Assert.Equal((ulong)8, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Read_many_respects_little_endian_override()
		{
			_apis.MemoryApi.Bytes[100] = 0x08;
			_apis.MemoryApi.Bytes[101] = 0x00;
			_ts.Call("bizhawk_set_big_endian", TestHelpers.Js("{\"enabled\":false}"));
			var res = Parse(_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":16}]}")));
			Assert.Equal((ulong)8, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Read_many_mixes_domains_with_own_endianness()
		{
			// same bytes 00 08: 8 on big 68K RAM, 2048 on little Z80 RAM
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			var res = Parse(_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":16,\"domain\":\"68K RAM\"},{\"address\":100,\"width\":16,\"domain\":\"Z80 RAM\"}]}")));
			Assert.Equal((ulong)8, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("reads")[0].GetProperty("endianness").GetString());
			Assert.Equal((ulong)2048, res.GetProperty("reads")[1].GetProperty("value").GetUInt64());
			Assert.Equal("little", res.GetProperty("reads")[1].GetProperty("endianness").GetString());
		}

		[Fact]
		public void Write_range_writes_bytes_in_order()
		{
			_ts.Call("bizhawk_write_range", TestHelpers.Js("{\"address\":100,\"values\":[1,2,3,4]}"));
			Assert.Equal((byte)1, _apis.MemoryApi.Bytes[100]);
			Assert.Equal((byte)4, _apis.MemoryApi.Bytes[103]);
		}

		[Fact]
		public void Write_range_rejects_out_of_byte_values()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_write_range", TestHelpers.Js("{\"address\":100,\"values\":[1,300]}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Write_many_writes_non_contiguous_values()
		{
			_ts.Call("bizhawk_write_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":8,\"value\":1},{\"address\":200,\"width\":16,\"value\":513},{\"address\":300,\"width\":32,\"value\":65537}]}"));
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
			_ts.Call("bizhawk_write_many", TestHelpers.Js("{\"items\":[{\"address\":200,\"width\":16,\"value\":513,\"endianness\":\"little\"},{\"address\":300,\"width\":32,\"value\":65537,\"endianness\":\"little\"}]}"));
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
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":50,\"width\":8}]}"));
			_ts.Call("bizhawk_write_many", TestHelpers.Js("{\"items\":[{\"name\":\"hp\",\"value\":99}]}"));
			Assert.Equal((byte)99, _apis.MemoryApi.Bytes[50]);
		}

		[Fact]
		public void Write_many_rejects_value_out_of_width()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_write_many", TestHelpers.Js("{\"items\":[{\"address\":100,\"width\":8,\"value\":300}]}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Ram_snapshot_then_diff_finds_changes()
		{
			_apis.MemoryApi.Bytes[10] = 0x01;
			_apis.MemoryApi.Bytes[11] = 0x02;
			_ts.Call("bizhawk_ram_snapshot", TestHelpers.Js("{\"domain\":\"68K RAM\",\"label\":\"t1\"}"));

			_apis.MemoryApi.Bytes[10] = 0xFF;
			_apis.MemoryApi.Bytes[11] = 0xFE;
			_apis.MemoryApi.Bytes[500] = 0xAA;

			var res = Parse(_ts.Call("bizhawk_ram_diff", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
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
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_ram_diff", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Palette_genesis_parses_bgr_to_rgb()
		{
			// CRAM hardware format 0x0RRR0GGG0BBB: R at bits 1-3. R=7 → 0x000E
			// (stored big-endian: 00 0E) → #FF0000
			_apis.MemoryApi.WriteByte(0, 0x00, "CRAM");
			_apis.MemoryApi.WriteByte(1, 0x0E, "CRAM");
			var res = Parse(_ts.Call("bizhawk_read_palette", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal("GEN", res.GetProperty("system").GetString());
			Assert.Equal("#FF0000", res.GetProperty("colors")[0].GetString());
		}

		[Fact]
		public void Palette_genesis_blue_entry_maps_to_blue()
		{
			// B at bits 9-11: B=7 → 0x0E00 (big-endian stored)
			_apis.MemoryApi.WriteByte(0, 0x0E, "CRAM");
			_apis.MemoryApi.WriteByte(1, 0x00, "CRAM");
			var res = Parse(_ts.Call("bizhawk_read_palette", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal("#0000FF", res.GetProperty("colors")[0].GetString());
		}

		[Fact]
		public void Palette_snes_parses_bgr555()
		{
			// CGRAM entry: 0x001F = R=31, G=0, B=0 → #FF0000 (little-endian stored)
			_apis.EmulationApi.SystemId = "SNES";
			_apis.MemoryApi.WriteByte(0, 0x1F, "CGRAM");
			_apis.MemoryApi.WriteByte(1, 0x00, "CGRAM");
			var res = Parse(_ts.Call("bizhawk_read_palette", TestHelpers.Js("{\"count\":1}")));
			Assert.Equal("#FF0000", res.GetProperty("colors")[0].GetString());
		}

		[Fact]
		public void Palette_unsupported_system_rejected()
		{
			_apis.EmulationApi.SystemId = "NES";
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_palette", null));
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

			var res = Parse(_ts.Call("bizhawk_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"columns\":2,\"rows\":1}")));
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

			var res = Parse(_ts.Call("bizhawk_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"columns\":1,\"rows\":1}")));
			var path = res.GetProperty("path").GetString();
			Assert.True(System.IO.File.Exists(path));
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Read_plane_rejects_out_of_vram()
		{
			// 0xF100 (61696) + 64*32*2 nametable exceeds 64KB VRAM
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"base\":61696,\"columns\":64,\"rows\":32}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Get_vdp_view_returns_plane_bases()
		{
			var dbg = _apis.EnableWatchpoints();
			dbg.PlaneABase = 0x0000;
			dbg.PlaneBBase = 0xE000;
			var res = Parse(_ts.Call("bizhawk_get_vdp_view", null));
			Assert.Equal((long)0x0000, res.GetProperty("planeA").GetProperty("base").GetInt64());
			Assert.Equal((long)0xE000, res.GetProperty("planeB").GetProperty("base").GetInt64());
			Assert.Equal(64, res.GetProperty("planeA").GetProperty("width").GetInt32());
		}

		[Fact]
		public void Get_vdp_view_errors_without_core()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_get_vdp_view", null));
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

			var res = Parse(_ts.Call("bizhawk_read_plane", TestHelpers.Js("{\"plane\":\"A\",\"columns\":1,\"rows\":1}")));
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

			var res = Parse(_ts.Call("bizhawk_read_plane", TestHelpers.Js("{\"plane\":\"B\",\"columns\":1,\"rows\":1,\"offset_x\":40}")));
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
			_ts.Call("bizhawk_screenshot", TestHelpers.Js($"{{\"path\":\"{path}\"}}"));
			// default: overlay off, then restored off (no getter)
			Assert.Equal(new[] { false, false }, _apis.EmuClientApi.OsdChanges.ToArray());
			Assert.False(_apis.EmuClientApi.OsdEnabled);
		}

		[Fact]
		public void Screenshot_include_overlays_sets_osd()
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test-osd-overlays.png");
			var res = Parse(_ts.Call("bizhawk_screenshot", TestHelpers.Js($"{{\"path\":\"{path}\",\"include_overlays\":true}}")));
			Assert.True(res.GetProperty("include_overlays").GetBoolean());
			Assert.Equal(new[] { true, false }, _apis.EmuClientApi.OsdChanges.ToArray());
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Symbols_set_then_read_and_write_by_name()
		{
			_apis.MemoryApi.Bytes[0xFFFBCA] = 0x12;
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"mainFunction\",\"address\":16776138,\"width\":8,\"domain\":\"M68K BUS\"}]}"));
			// 16776138 = 0xFFFBCA
			var res = Parse(_ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"name\":\"mainFunction\"}")));
			Assert.Equal((ulong)0x12, res.GetProperty("value").GetUInt64());

			_ts.Call("bizhawk_write_memory", TestHelpers.Js("{\"name\":\"mainFunction\",\"value\":153}"));
			Assert.Equal((byte)0x99, _apis.MemoryApi.Bytes[0xFFFBCA]);
		}

		[Fact]
		public void Symbols_read_many_accepts_names()
		{
			_apis.MemoryApi.Bytes[10] = 0x11;
			_apis.MemoryApi.Bytes[20] = 0x22;
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"a\",\"address\":10},{\"name\":\"b\",\"address\":20}]}"));
			var res = Parse(_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"name\":\"a\"},{\"name\":\"b\"}]}")));
			Assert.Equal((ulong)0x11, res.GetProperty("reads")[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x22, res.GetProperty("reads")[1].GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Symbols_unknown_name_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_read_memory", TestHelpers.Js("{\"name\":\"nope\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Symbols_list_and_clear()
		{
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"a\",\"address\":10}]}"));
			var listed = Parse(_ts.Call("bizhawk_symbols_list", null));
			Assert.Equal("a", listed.GetProperty("symbols")[0].GetProperty("name").GetString());
			_ts.Call("bizhawk_symbols_clear", null);
			var cleared = Parse(_ts.Call("bizhawk_symbols_list", null));
			Assert.Empty(cleared.GetProperty("symbols").EnumerateArray());
		}

		[Fact]
		public void Symbols_persist_across_toolset_restarts()
		{
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"persisted\",\"address\":42,\"width\":16,\"domain\":\"68K RAM\"}]}"));
			// a brand-new toolset sharing the same UserData store must reload them
			var fresh = new McpToolset(_apis, new InlineDispatcher());
			var res = Parse(fresh.Call("bizhawk_symbols_list", null));
			var sym = res.GetProperty("symbols")[0];
			Assert.Equal("persisted", sym.GetProperty("name").GetString());
			Assert.Equal((long)42, sym.GetProperty("address").GetInt64());
			Assert.Equal(16, sym.GetProperty("width").GetInt32());
			Assert.Equal("68K RAM", sym.GetProperty("domain").GetString());
		}

		[Fact]
		public void Symbols_clear_persists_empty()
		{
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"x\",\"address\":1}]}"));
			_ts.Call("bizhawk_symbols_clear", null);
			var fresh = new McpToolset(_apis, new InlineDispatcher());
			var res = Parse(fresh.Call("bizhawk_symbols_list", null));
			Assert.Empty(res.GetProperty("symbols").EnumerateArray());
		}

		[Fact]
		public void Symbols_namespaces_are_isolated_and_clearable()
		{
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"namespace\":\"ghidra\",\"symbols\":[{\"name\":\"mainFunction\",\"address\":100}]}"));
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"namespace\":\"fixture\",\"symbols\":[{\"name\":\"hp\",\"address\":200}]}"));

			var res = Parse(_ts.Call("bizhawk_symbols_list", null));
			Assert.Equal(2, res.GetProperty("symbols").GetArrayLength());
			// namespaces survive a reload
			var fresh = new McpToolset(_apis, new InlineDispatcher());
			var reloaded = Parse(fresh.Call("bizhawk_symbols_list", null));
			Assert.Equal(2, reloaded.GetProperty("symbols").GetArrayLength());

			// clearing just one namespace keeps the other
			fresh.Call("bizhawk_symbols_clear", TestHelpers.Js("{\"namespace\":\"fixture\"}"));
			var after = Parse(fresh.Call("bizhawk_symbols_list", null));
			Assert.Single(after.GetProperty("symbols").EnumerateArray());
			Assert.Equal("ghidra", after.GetProperty("symbols")[0].GetProperty("namespace").GetString());
		}

		[Fact]
		public void Symbols_switch_when_rom_changes()
		{
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"onlyInRom1\",\"address\":11}]}"));
			// same toolset, different ROM loaded → get_info swaps the symbol set
			_apis.EmulationApi.RomHash = "rom2";
			var info = Parse(_ts.Call("bizhawk_get_info", null));
			var after = Parse(_ts.Call("bizhawk_symbols_list", null));
			Assert.Empty(after.GetProperty("symbols").EnumerateArray());
		}

		[Fact]
		public void Symbols_rom2_persists_separately()
		{
			// default ROM hash is "abcd"
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"rom1sym\",\"address\":11}]}"));
			_apis.EmulationApi.RomHash = "rom2";
			_ts.Call("bizhawk_get_info", null);
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"rom2sym\",\"address\":22}]}"));

			// back to the original ROM restores its own set
			_apis.EmulationApi.RomHash = "abcd";
			_ts.Call("bizhawk_get_info", null);
			var listed = Parse(_ts.Call("bizhawk_symbols_list", null));
			Assert.Single(listed.GetProperty("symbols").EnumerateArray());
			Assert.Equal("rom1sym", listed.GetProperty("symbols")[0].GetProperty("name").GetString());
		}

		[Fact]
		public void Dump_memory_writes_file_and_resource()
		{
			_apis.MemoryApi.Bytes[0] = 0xDE;
			_apis.MemoryApi.Bytes[1] = 0xAD;
			var res = Parse(_ts.Call("bizhawk_dump_memory", TestHelpers.Js("{\"domain\":\"68K RAM\"}")));
			var path = res.GetProperty("path").GetString();
			Assert.Contains("bizhawk-mcp", path);
			Assert.Equal(65536L, res.GetProperty("size").GetInt64());
			var fileBytes = System.IO.File.ReadAllBytes(path);
			Assert.Equal((byte)0xDE, fileBytes[0]);
			Assert.Equal((byte)0xAD, fileBytes[1]);

			var listed = _ts.ListResources();
			var listDoc = JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(listed));
			Assert.Contains(listDoc.RootElement.GetProperty("resources").EnumerateArray(), r => r.GetProperty("mimeType").GetString() == "application/octet-stream");
		}

		[Fact]
		public void Read_many_consistent_pauses_and_resumes()
		{
			_apis.EmuClientApi.Paused = false;
			var res = Parse(_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":10}],\"consistent\":true}")));
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
			_ts.Call("bizhawk_read_many", TestHelpers.Js("{\"items\":[{\"address\":10}],\"consistent\":true}"));
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
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":100,\"width\":8}]}"));
			string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bizhawk-mcp-test-fixture.csv");

			var res = Parse(_ts.Call("bizhawk_start_fixture", TestHelpers.Js($"{{\"frames\":5,\"samples\":[{{\"name\":\"hp\"}}],\"path\":\"{path}\"}}")));
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
			_ts.Call("bizhawk_start_fixture", TestHelpers.Js($"{{\"frames\":2,\"samples\":[{{\"address\":0,\"width\":8}}],\"inputs\":[{{\"frame\":1,\"buttons\":{{\"A\":true,\"Right\":true}}}}],\"path\":\"{path}\"}}"));
			Assert.NotNull(_apis.JoypadApi.LastSet);
			Assert.True(_apis.JoypadApi.LastSet!["A"]);
			Assert.True(_apis.JoypadApi.LastSet!["Right"]);
			System.IO.File.Delete(path);
		}

		[Fact]
		public void Start_fixture_rejects_bad_frames()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_start_fixture", TestHelpers.Js("{\"frames\":0,\"samples\":[{\"address\":0}]}")));
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

			var res = Parse(_ts.Call("bizhawk_read_struct", TestHelpers.Js("{\"address\":63582,\"domain\":\"68K RAM\",\"fields\":[{\"name\":\"x\",\"offset\":26,\"width\":32},{\"name\":\"y\",\"offset\":30,\"width\":32}]}")));
			Assert.Equal((ulong)0x00200000, res.GetProperty("fields")[0].GetProperty("value").GetUInt64());
			Assert.Equal((ulong)0x0010F000, res.GetProperty("fields")[1].GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("fields")[0].GetProperty("endianness").GetString());
		}

		[Fact]
		public void Read_struct_accepts_symbol_base()
		{
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"player\",\"address\":100,\"width\":8,\"domain\":\"68K RAM\"}]}"));
			_apis.MemoryApi.Bytes[100] = 0xAA;
			_apis.MemoryApi.Bytes[101] = 0xBB;
			var res = Parse(_ts.Call("bizhawk_read_struct", TestHelpers.Js("{\"name\":\"player\",\"fields\":[{\"name\":\"b0\",\"offset\":0,\"width\":8},{\"name\":\"b1\",\"offset\":1,\"width\":8}]}")));
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
			_ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			var listed = Parse(_ts.Call("bizhawk_watch_list", null));
			Assert.Equal((ulong)0x42, listed.GetProperty("watchers")[0].GetProperty("value").GetUInt64());

			var read = Parse(_ts.Call("bizhawk_watch_read", null));
			Assert.Equal((ulong)0x42, read.GetProperty("watchers")[0].GetProperty("value").GetUInt64());
			Assert.False(read.GetProperty("watchers")[0].GetProperty("changed").GetBoolean());
		}

		[Fact]
		public void Watch_read_reports_changed()
		{
			_apis.MemoryApi.Bytes[100] = 0x42;
			_ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			_ts.Call("bizhawk_watch_read", null);
			_apis.MemoryApi.Bytes[100] = 0x99;
			var read = Parse(_ts.Call("bizhawk_watch_read", null));
			Assert.Equal((ulong)0x99, read.GetProperty("watchers")[0].GetProperty("value").GetUInt64());
			Assert.True(read.GetProperty("watchers")[0].GetProperty("changed").GetBoolean());
		}

		[Fact]
		public void Watch_reads_with_per_watcher_endianness()
		{
			_apis.MemoryApi.Bytes[100] = 0x00;
			_apis.MemoryApi.Bytes[101] = 0x08;
			_ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"main\",\"address\":100,\"width\":16,\"domain\":\"68K RAM\"}"));
			_ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"sound\",\"address\":100,\"width\":16,\"domain\":\"Z80 RAM\"}"));
			var listed = Parse(_ts.Call("bizhawk_watch_list", null));
			Assert.Equal((ulong)8, listed.GetProperty("watchers")[0].GetProperty("value").GetUInt64());
			Assert.Equal("big", listed.GetProperty("watchers")[0].GetProperty("endianness").GetString());
			Assert.Equal((ulong)2048, listed.GetProperty("watchers")[1].GetProperty("value").GetUInt64());
			Assert.Equal("little", listed.GetProperty("watchers")[1].GetProperty("endianness").GetString());
		}

		[Fact]
		public void Watch_add_duplicate_rejected()
		{
			_ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":200,\"width\":8}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watch_remove_works()
		{
			_ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"hp\",\"address\":100,\"width\":8}"));
			var res = _ts.Call("bizhawk_watch_remove", TestHelpers.Js("{\"name\":\"hp\"}"));
			Assert.Contains("removed", res);
			var listed = Parse(_ts.Call("bizhawk_watch_list", null));
			Assert.Empty(listed.GetProperty("watchers").EnumerateArray());
		}

		[Fact]
		public void Watch_add_outside_domain_rejected()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watch_add", TestHelpers.Js("{\"name\":\"x\",\"address\":70000,\"width\":8}")));
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

			var res = Parse(_ts.Call("bizhawk_wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"eq\",\"value\":5,\"width\":8}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal(5, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)5, res.GetProperty("value").GetUInt64());
			Assert.True(_apis.EmuClientApi.Paused); // pause restored
		}

		[Fact]
		public void Wait_until_accepts_symbol_name()
		{
			// same as above but the target is addressed by symbol, not raw offset
			_ts.Call("bizhawk_symbols_set", TestHelpers.Js("{\"symbols\":[{\"name\":\"hp\",\"address\":100,\"width\":8}]}"));
			var frames = 0;
			_apis.EmuClientApi.OnFrameAdvance = () => { frames++; _apis.MemoryApi.Bytes[100] = (byte)frames; };
			_apis.MemoryApi.Bytes[100] = 0;
			_apis.EmuClientApi.Paused = true;

			var res = Parse(_ts.Call("bizhawk_wait_until", TestHelpers.Js("{\"name\":\"hp\",\"op\":\"eq\",\"value\":3}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal(3, res.GetProperty("frames").GetInt32());
			Assert.Equal((ulong)3, res.GetProperty("value").GetUInt64());
		}

		[Fact]
		public void Wait_until_times_out()
		{
			// value never changes → no match within 600 frames
			_apis.MemoryApi.Bytes[100] = 0;
			var res = Parse(_ts.Call("bizhawk_wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"eq\",\"value\":9,\"width\":8}")));
			Assert.False(res.GetProperty("matched").GetBoolean());
			Assert.Equal(600, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Wait_until_rejects_bad_op()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"==\",\"value\":1}")));
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

			var res = Parse(_ts.Call("bizhawk_wait_until", TestHelpers.Js("{\"address\":100,\"op\":\"eq\",\"value\":1,\"width\":16,\"domain\":\"68K RAM\"}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.Equal((ulong)1, res.GetProperty("value").GetUInt64());
			Assert.Equal("big", res.GetProperty("endianness").GetString());
		}

		[Fact]
		public void Trace_samples_pc_and_disasm()
		{
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("bizhawk_trace", TestHelpers.Js("{\"count\":10,\"step\":5}")));
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
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_trace", TestHelpers.Js("{\"count\":601}")));
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
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":16776136}"));
			Assert.Single(dbg.Callbacks.Registered);
			Assert.Equal(MemoryCallbackType.Write, dbg.Callbacks.Registered[0].Type);
			Assert.Equal((uint)16776136, dbg.Callbacks.Registered[0].Address);
		}

		[Fact]
		public void Watchpoint_add_without_core_support_errors()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			Assert.Contains("unsupported", ex.Message);
		}

		[Fact]
		public void Watchpoint_execute_requires_address()
		{
			_apis.EnableWatchpoints();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"execute\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_execute_unavailable_errors()
		{
			var dbg = _apis.EnableWatchpoints();
			dbg.Callbacks.ExecuteCallbacksAvailableValue = false;
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"execute\",\"address\":16776136}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_bad_scope_errors()
		{
			_apis.EnableWatchpoints();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"domain\":\"NOPE\"}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_wait_detects_fire()
		{
			var dbg = _apis.EnableWatchpoints();
			_apis.EmuClientApi.Paused = true;
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":16776136}"));
			// fire the callback on the 3rd frame advance
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 3) dbg.Callbacks.Fire(16776136, 0x77);
			};

			var res = Parse(_ts.Call("bizhawk_watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":10}")));
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
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":16776200}"));
			// hit address 16776200 (= 0x1000208); scope M68K BUS (16 MiB)
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 2) dbg.Callbacks.Fire(16776200, 0x42);
			};

			var res = Parse(_ts.Call("bizhawk_watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":10,\"context_bytes\":64}")));
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
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\"}"));
			var frame = 0;
			_apis.EmuClientApi.OnFrameAdvance = () =>
			{
				if (++frame == 1) dbg.Callbacks.Fire(100, 0x01);
			};

			var res = Parse(_ts.Call("bizhawk_watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":10}")));
			Assert.True(res.GetProperty("matched").GetBoolean());
			Assert.False(res.TryGetProperty("registers", out _));
			Assert.False(res.TryGetProperty("context", out _));
		}

		[Fact]
		public void Watchpoint_wait_times_out()
		{
			_apis.EnableWatchpoints();
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"read\"}"));
			var res = Parse(_ts.Call("bizhawk_watchpoint_wait", TestHelpers.Js("{\"timeout_frames\":5}")));
			Assert.False(res.GetProperty("matched").GetBoolean());
			Assert.Equal(5, res.GetProperty("frames").GetInt32());
		}

		[Fact]
		public void Watchpoint_wait_without_any_registered_errors()
		{
			_apis.EnableWatchpoints();
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_watchpoint_wait", null));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Watchpoint_remove_unregisters()
		{
			var dbg = _apis.EnableWatchpoints();
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"write\",\"address\":100}"));
			var res = _ts.Call("bizhawk_watchpoint_remove", TestHelpers.Js("{\"name\":\"wp1\"}"));
			Assert.Contains("removed", res);
			Assert.Empty(dbg.Callbacks.Registered);
		}

		[Fact]
		public void Watchpoint_list_reports()
		{
			_apis.EnableWatchpoints();
			_ts.Call("bizhawk_watchpoint_add", TestHelpers.Js("{\"name\":\"wp1\",\"type\":\"execute\",\"address\":2370}"));
			var res = Parse(_ts.Call("bizhawk_watchpoint_list", null));
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
			var res = _ts.Call("bizhawk_frame_advance", TestHelpers.Js("{\"count\":3}"));
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
			_ts.Call("bizhawk_frame_advance", TestHelpers.Js("{\"count\":2}"));
			Assert.Equal(2, _apis.EmuClientApi.FramesAdvanced);
			Assert.Equal(0, _apis.EmuClientApi.UnpauseCalls);
			Assert.Equal(0, _apis.EmuClientApi.PauseCalls);
			Assert.False(_apis.EmuClientApi.Paused);
		}

		[Fact]
		public void Pause_unpause_toggle_work()
		{
			_apis.EmuClientApi.Paused = false;
			var res = Parse(_ts.Call("bizhawk_pause", null));
			Assert.True(res.GetProperty("paused").GetBoolean());
			res = Parse(_ts.Call("bizhawk_unpause", null));
			Assert.False(res.GetProperty("paused").GetBoolean());
			res = Parse(_ts.Call("bizhawk_toggle_pause", null));
			Assert.True(res.GetProperty("paused").GetBoolean());
		}

		[Fact]
		public void Speed_mode_sets_percent()
		{
			_ts.Call("bizhawk_speed_mode", TestHelpers.Js("{\"percent\":400}"));
			Assert.Equal(400, _apis.EmuClientApi.SpeedModePercent);
		}

		[Fact]
		public void Get_registers_returns_map()
		{
			var res = Parse(_ts.Call("bizhawk_get_registers", null));
			Assert.Equal((ulong)0xFFFBCA, res.GetProperty("registers").GetProperty("M68K PC").GetUInt64());
		}

		[Fact]
		public void Trace_finds_prefixed_core_register_names()
		{
			// gpgx names registers "M68K PC" etc.; the trace must match the
			// suffix, not just the bare "PC" key (regression: PC sampled as 0).
			_apis.EmuClientApi.Paused = true;
			var res = Parse(_ts.Call("bizhawk_trace", TestHelpers.Js("{\"count\":2,\"step\":1}")));
			var sample = res.GetProperty("samples")[0];
			Assert.Equal((ulong)0xFFFBCA, sample.GetProperty("pc").GetUInt64());
			Assert.Equal("MOVE.L D0,D1", sample.GetProperty("disasm").GetString());
			Assert.Equal((ulong)0xFFFFFDFA, sample.GetProperty("sp").GetUInt64());
			Assert.Equal((ulong)0x2000, sample.GetProperty("sr").GetUInt64());
		}

		[Fact]
		public void Set_register_forwards()
		{
			_ts.Call("bizhawk_set_register", TestHelpers.Js("{\"register\":\"A0\",\"value\":39321}"));
			Assert.Equal("A0", _apis.EmulationApi.RegisterToSet);
			Assert.Equal(0x9999, _apis.EmulationApi.RegisterValue);
		}

		[Fact]
		public void Disassemble_returns_asm()
		{
			var res = _ts.Call("bizhawk_disassemble", TestHelpers.Js("{\"pc\":4194304}"));
			Assert.Equal("MOVE.L D0,D1", res);
		}

		[Fact]
		public void Lag_count_reports_state()
		{
			_apis.EmulationApi.Lagged = true;
			_apis.EmulationApi.LagCountValue = 7;
			var res = Parse(_ts.Call("bizhawk_lag_count", null));
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
			var res = Parse(_ts.Call("bizhawk_screenshot", null));
			var path = res.GetProperty("path").GetString();
			Assert.Contains("bizhawk-mcp", path);
			Assert.StartsWith("bizhawk://", res.GetProperty("resource").GetString());
			Assert.Equal(1, _apis.EmuClientApi.Screenshots.Count);
		}

		[Fact]
		public void Screenshot_with_path_uses_it()
		{
			var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "test-shot.png");
			var res = Parse(_ts.Call("bizhawk_screenshot", TestHelpers.Js($"{{\"path\":\"{path}\"}}")));
			Assert.Equal(path, res.GetProperty("path").GetString());
		}

		[Fact]
		public void Resources_list_and_read_roundtrip()
		{
			var res = Parse(_ts.Call("bizhawk_screenshot", null));
			var uri = res.GetProperty("resource").GetString()!;

			var listed = _ts.ListResources();
			var listDoc = JsonDocument.Parse(JsonSerializer.Serialize(listed));
			Assert.Equal(1, listDoc.RootElement.GetProperty("resources").GetArrayLength());
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
			Assert.Equal(1, templates.GetArrayLength());
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
			var res = Parse(_ts.Call("bizhawk_get_joypad", null));
			Assert.True(res.GetProperty("buttons").GetProperty("A").GetBoolean());
		}

		[Fact]
		public void Press_buttons_forwards_to_joypad()
		{
			_ts.Call("bizhawk_press_buttons", TestHelpers.Js("{\"buttons\":{\"A\":true,\"Right\":true},\"controller\":2}"));
			Assert.True(_apis.JoypadApi.LastSet!["A"]);
			Assert.Equal(2, _apis.JoypadApi.LastController);
		}

		[Fact]
		public void Save_load_state_forward()
		{
			_ts.Call("bizhawk_save_state", TestHelpers.Js("{\"path\":\"C:/x.State\"}"));
			Assert.Equal("C:/x.State", _apis.SaveStateApi.SavedTo);
			var res = _ts.Call("bizhawk_load_state", TestHelpers.Js("{\"path\":\"C:/x.State\"}"));
			Assert.Contains("loaded", res);
		}

		[Fact]
		public void Save_load_slot_forward()
		{
			_ts.Call("bizhawk_save_slot", TestHelpers.Js("{\"slot\":3}"));
			Assert.Equal(3, _apis.SaveStateApi.SavedSlot);
			var res = _ts.Call("bizhawk_load_slot", TestHelpers.Js("{\"slot\":3}"));
			Assert.Equal(3, _apis.SaveStateApi.LoadedSlot);
			Assert.Contains("loaded", res);
		}

		[Fact]
		public void Save_slot_rejects_out_of_range()
		{
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_save_slot", TestHelpers.Js("{\"slot\":0}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
			ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_load_slot", TestHelpers.Js("{\"slot\":11}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Overlay_text_draws_and_clears()
		{
			_ts.Call("bizhawk_overlay_text", TestHelpers.Js("{\"x\":1,\"y\":2,\"text\":\"hi\",\"fontsize\":12}"));
			Assert.Equal((1, 2, "hi", (int?)12), _apis.GuiApi.LastDraw);
			_ts.Call("bizhawk_clear_overlay", null);
			// clears the Client graphics surface, the text layer, and the list
			Assert.Equal(3, _apis.GuiApi.ClearTextCalls);
		}

		[Fact]
		public void Overlays_accumulate_and_redraw_all()
		{
			// drawing a new shape must NOT wipe the previous ones: the toolset
			// keeps a list and re-renders everything on every mutation
			_ts.Call("bizhawk_overlay_rect", TestHelpers.Js("{\"x\":1,\"y\":2,\"width\":10,\"height\":20}"));
			Assert.Equal(1, _apis.GuiApi.DrawCount); // one rect
			_ts.Call("bizhawk_overlay_line", TestHelpers.Js("{\"x1\":0,\"y1\":0,\"x2\":5,\"y2\":5}"));
			// re-rendered the rect again + the new line (2 draws this mutation)
			Assert.Equal((1, 2, 10, 20), _apis.GuiApi.LastRect);
			Assert.Equal((0, 0, 5, 5), _apis.GuiApi.LastLine);
			Assert.Equal(3, _apis.GuiApi.DrawCount);
		}

		[Fact]
		public void Overlay_rects_list_in_one_call()
		{
			_ts.Call("bizhawk_overlay_rect", TestHelpers.Js("{\"rects\":[{\"x\":1,\"y\":2,\"width\":3,\"height\":4},{\"x\":5,\"y\":6,\"width\":7,\"height\":8}]}"));
			Assert.Equal((5, 6, 7, 8), _apis.GuiApi.LastRect);
		}

		[Fact]
		public void Overlay_rect_and_line_no_longer_throw()
		{
			// regression: DrawRectangle/DrawLine without a surface used to throw
			// (Get2DRenderer(null) threw); WithSurface(Client, ...) fixes it
			_ts.Call("bizhawk_overlay_rect", TestHelpers.Js("{\"x\":1,\"y\":2,\"width\":10,\"height\":20,\"color\":\"#FF0000\"}"));
			Assert.Equal((1, 2, 10, 20), _apis.GuiApi.LastRect);
			_ts.Call("bizhawk_overlay_line", TestHelpers.Js("{\"x1\":0,\"y1\":0,\"x2\":5,\"y2\":5}"));
			Assert.Equal((0, 0, 5, 5), _apis.GuiApi.LastLine);
		}

		[Fact]
		public void Osd_message_forwards()
		{
			_ts.Call("bizhawk_osd_message", TestHelpers.Js("{\"message\":\"hello\",\"duration\":500}"));
			Assert.Contains("hello", _apis.GuiApi.Messages);
		}

		[Fact]
		public void Movie_info_returns_json()
		{
			var res = Parse(_ts.Call("bizhawk_movie_info", null));
			Assert.True(res.GetProperty("loaded").GetBoolean());
			Assert.Equal("test.bk2", res.GetProperty("filename").GetString());
			Assert.Equal((ulong)42, res.GetProperty("rerecords").GetUInt64());
		}

		[Fact]
		public void Movie_info_without_movie_returns_empty_not_crash()
		{
			_apis.MovieApi.Loaded = false;
			var res = Parse(_ts.Call("bizhawk_movie_info", null));
			Assert.False(res.GetProperty("loaded").GetBoolean());
			Assert.Equal(JsonValueKind.Null, res.GetProperty("filename").ValueKind);
			Assert.Equal(0, res.GetProperty("length").GetInt32());
		}

		[Fact]
		public void Movie_input_without_movie_errors_cleanly()
		{
			_apis.MovieApi.Loaded = false;
			var ex = Assert.Throws<JsonRpc.Error>(() => _ts.Call("bizhawk_movie_input", TestHelpers.Js("{\"frame\":0}")));
			Assert.Equal(JsonRpc.Error.INVALID_PARAMS, ex.Code);
		}

		[Fact]
		public void Movie_input_returns_mnemonic()
		{
			var res = _ts.Call("bizhawk_movie_input", TestHelpers.Js("{\"frame\":0}"));
			Assert.Equal("|..|..|", res);
		}

		[Fact]
		public void Host_input_returns_pressed_and_mouse()
		{
			var res = Parse(_ts.Call("bizhawk_host_input", null));
			Assert.Equal("Shift+A", res.GetProperty("pressed")[0].GetString());
			Assert.Equal(10, res.GetProperty("mouse").GetProperty("X").GetInt32());
		}

		[Fact]
		public void Userdata_set_get_clear()
		{
			_ts.Call("bizhawk_userdata_set", TestHelpers.Js("{\"key\":\"k\",\"value\":\"v\"}"));
			var res = Parse(_ts.Call("bizhawk_userdata_get", TestHelpers.Js("{\"key\":\"k\"}")));
			Assert.Equal("v", res.GetProperty("value").GetString());
			_ts.Call("bizhawk_userdata_clear", null);
			Assert.Empty(_apis.UserDataApi.Data);
		}

		[Fact]
		public void Shutdown_stops_server()
		{
			_ts.Call("bizhawk_shutdown", null);
			Assert.Equal(1, _apis.StopServerCalls);
		}
	}
}
