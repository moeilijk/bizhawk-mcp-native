using System.Text.Json;
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
		public void List_memory_domains_returns_names_and_sizes()
		{
			var res = Parse(_ts.Call("bizhawk_list_memory_domains", null));
			var domains = res.GetProperty("domains");
			Assert.Equal(65536u, domains.GetProperty("68K RAM").GetUInt64());
			Assert.Equal("68K RAM", res.GetProperty("current").GetString());
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
			Assert.Equal((ulong)0xFFFBCA, res.GetProperty("registers").GetProperty("PC").GetUInt64());
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
		public void Overlay_text_draws_and_clears()
		{
			_ts.Call("bizhawk_overlay_text", TestHelpers.Js("{\"x\":1,\"y\":2,\"text\":\"hi\",\"fontsize\":12}"));
			Assert.Equal((1, 2, "hi", (int?)12), _apis.GuiApi.LastDraw);
			_ts.Call("bizhawk_clear_overlay", null);
			Assert.Equal(1, _apis.GuiApi.ClearTextCalls);
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
