using System;
using System.Collections.Generic;
using System.Linq;
using BizHawk.Client.Common;
using BizHawk.Emulation.Common;
using BizHawkMcp;

namespace BizHawkMcp.Tests
{
	/// <summary>Runs handlers on the calling thread.</summary>
	public sealed class InlineDispatcher : IUiDispatcher
	{
		public bool IsUiThread => true;

		public void Invoke(Action action) => action();

		public T Invoke<T>(Func<T> func) => func();
	}

	public sealed class FakeMemoryApi : IMemoryApi
	{
		public readonly Dictionary<long, byte> Bytes = new();
		public bool BigEndian;
		public string CurrentDomain = "68K RAM";
		public int SetBigEndianCalls;

		public void SetBigEndian(bool enabled = true) { BigEndian = enabled; SetBigEndianCalls++; }

		public IReadOnlyCollection<string> GetMemoryDomainList() => ["68K RAM", "Z80 RAM", "M68K BUS", "VRAM", "CRAM"];

		public uint GetMemoryDomainSize(string name = "")
		{
			if (string.IsNullOrEmpty(name) || name == "68K RAM") return 65536u;
			if (name == "Z80 RAM") return 8192u;
			if (name == "VRAM") return 65536u;
			if (name == "CRAM") return 128u;
			return 16u * 1024 * 1024;
		}

		public string GetCurrentMemoryDomain() => CurrentDomain;

		public uint GetCurrentMemoryDomainSize() => 65536u;

		public bool UseMemoryDomain(string domain)
		{
			if (domain != "68K RAM" && domain != "M68K BUS") return false;
			CurrentDomain = domain;
			return true;
		}

		public string HashRegion(long addr, int count, string domain = null) => "deadbeef";

		public uint ReadByte(long addr, string domain = null) => Bytes.TryGetValue(addr + DomainBase(domain), out var b) ? b : (uint)0;

		public IReadOnlyList<byte> ReadByteRange(long addr, int length, string domain = null)
		{
			var list = new byte[length];
			for (var i = 0; i < length; i++) list[i] = (byte)ReadByte(addr + i, domain);
			return list;
		}

		// Domain spaces are offset so VRAM/CRAM don't collide with each other
		// or with RAM in the shared byte map (the real core uses separate
		// domains). Base offsets chosen well above any RAM test address.
		private static long DomainBase(string? domain) => domain switch
		{
			"VRAM" => 0x10_0000L,
			"CRAM" or "CGRAM" => 0x20_0000L,
			_ => 0L,
		};

		public float ReadFloat(long addr, string domain = null) => BitConverter.ToSingle(new[] { (byte)ReadByte(addr), (byte)ReadByte(addr + 1), (byte)ReadByte(addr + 2), (byte)ReadByte(addr + 3) }, 0);

		public int ReadS8(long addr, string domain = null) => (sbyte)ReadByte(addr);

		public int ReadS16(long addr, string domain = null) => (short)ReadU16(addr, domain);

		public int ReadS24(long addr, string domain = null)
		{
			uint v = ReadU8(addr, domain) | (ReadU8(addr + 1, domain) << 8) | (ReadU8(addr + 2, domain) << 16);
			return (int)(v & 0x800000) == 0 ? (int)v : (int)(v | 0xFF000000);
		}

		public int ReadS32(long addr, string domain = null) => (int)ReadU32(addr, domain);

		public uint ReadU8(long addr, string domain = null) => ReadByte(addr, domain);

		public uint ReadU16(long addr, string domain = null)
		{
			byte a = (byte)ReadByte(addr), b = (byte)ReadByte(addr + 1);
			return BigEndian ? (uint)((a << 8) | b) : (uint)(a | (b << 8));
		}

		public uint ReadU24(long addr, string domain = null)
		{
			byte a = (byte)ReadByte(addr), b = (byte)ReadByte(addr + 1), c = (byte)ReadByte(addr + 2);
			return BigEndian ? (uint)((a << 16) | (b << 8) | c) : (uint)(a | (b << 8) | (c << 16));
		}

		public uint ReadU32(long addr, string domain = null)
		{
			byte a = (byte)ReadByte(addr), b = (byte)ReadByte(addr + 1), c = (byte)ReadByte(addr + 2), d = (byte)ReadByte(addr + 3);
			return BigEndian ? (uint)((a << 24) | (b << 16) | (c << 8) | d) : (uint)(a | (b << 8) | (c << 16) | (d << 24));
		}

		public void WriteByte(long addr, uint value, string domain = null) => Bytes[addr + DomainBase(domain)] = (byte)value;

		public void WriteByteRange(long addr, IReadOnlyList<byte> memoryblock, string domain = null)
		{
			for (var i = 0; i < memoryblock.Count; i++) Bytes[addr + DomainBase(domain) + i] = memoryblock[i];
		}

		public void WriteFloat(long addr, float value, string domain = null)
		{
			var bytes = BitConverter.GetBytes(value);
			for (var i = 0; i < 4; i++) Bytes[addr + DomainBase(domain) + i] = bytes[i];
		}

		public void WriteS8(long addr, int value, string domain = null) => WriteByte(addr, (uint)(sbyte)value);

		public void WriteS16(long addr, int value, string domain = null)
		{
			if (BigEndian) { WriteByte(addr, (uint)((short)value >> 8)); WriteByte(addr + 1, (uint)(short)value); }
			else { WriteByte(addr, (uint)(short)value); WriteByte(addr + 1, (uint)((short)value >> 8)); }
		}

		public void WriteS24(long addr, int value, string domain = null)
		{
			if (BigEndian) { WriteByte(addr, (uint)(value >> 16)); WriteByte(addr + 1, (uint)(value >> 8)); WriteByte(addr + 2, (uint)value); }
			else { WriteByte(addr, (uint)value); WriteByte(addr + 1, (uint)(value >> 8)); WriteByte(addr + 2, (uint)(value >> 16)); }
		}

		public void WriteS32(long addr, int value, string domain = null)
		{
			if (BigEndian) { WriteByte(addr, (uint)(value >> 24)); WriteByte(addr + 1, (uint)(value >> 16)); WriteByte(addr + 2, (uint)(value >> 8)); WriteByte(addr + 3, (uint)value); }
			else { WriteByte(addr, (uint)value); WriteByte(addr + 1, (uint)(value >> 8)); WriteByte(addr + 2, (uint)(value >> 16)); WriteByte(addr + 3, (uint)(value >> 24)); }
		}

		public void WriteU8(long addr, uint value, string domain = null) => WriteByte(addr, value);

		public void WriteU16(long addr, uint value, string domain = null)
		{
			if (BigEndian) { WriteByte(addr, value >> 8); WriteByte(addr + 1, value); }
			else { WriteByte(addr, value); WriteByte(addr + 1, value >> 8); }
		}

		public void WriteU24(long addr, uint value, string domain = null)
		{
			if (BigEndian) { WriteByte(addr, value >> 16); WriteByte(addr + 1, value >> 8); WriteByte(addr + 2, value); }
			else { WriteByte(addr, value); WriteByte(addr + 1, value >> 8); WriteByte(addr + 2, value >> 16); }
		}

		public void WriteU32(long addr, uint value, string domain = null)
		{
			if (BigEndian) { WriteByte(addr, value >> 24); WriteByte(addr + 1, value >> 16); WriteByte(addr + 2, value >> 8); WriteByte(addr + 3, value); }
			else { WriteByte(addr, value); WriteByte(addr + 1, value >> 8); WriteByte(addr + 2, value >> 16); WriteByte(addr + 3, value >> 24); }
		}
	}

	public sealed class FakeEmuClientApi : IEmuClientApi
	{
		public bool Paused = true;
		public int FramesAdvanced;
		public int UnpauseCalls;
		public int PauseCalls;
		public int SpeedModePercent = -1;
		public readonly List<string> Screenshots = new();
		// invoked after each DoFrameAdvance; lets tests simulate RAM changing
		public Action? OnFrameAdvance;

		public void DoFrameAdvance()
		{
			FramesAdvanced++;
			OnFrameAdvance?.Invoke();
		}

		public void DoFrameAdvanceAndUnpause() { UnpauseCalls++; FramesAdvanced++; }

		public bool IsPaused() => Paused;

		public void Pause() { Paused = true; PauseCalls++; }

		public void Unpause() { Paused = false; UnpauseCalls++; }

		public void TogglePause() => Paused = !Paused;

		public void SpeedMode(int percent) => SpeedModePercent = percent;

		public bool OsdEnabled = true;
		public readonly List<bool> OsdChanges = new();

		public void SetScreenshotOSD(bool value)
		{
			OsdEnabled = value;
			OsdChanges.Add(value);
		}

		public void Screenshot(string path = null)
		{
			Screenshots.Add(path);
			System.IO.File.WriteAllBytes(path, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A });
		}
	}

	public sealed class FakeEmulationApi : IEmulationApi
	{
		public string SystemId = "GEN";
		public string RomHash = "abcd";
		public int FrameCountValue = 1000;
		public bool Lagged;
		public int LagCountValue;
		public IReadOnlyDictionary<string, ulong> Registers = new Dictionary<string, ulong> { ["M68K PC"] = 0xFFFBCA, ["M68K A0"] = 0x1234, ["M68K SR"] = 0x2000, ["M68K SP"] = 0xFFFFFDFA };
		public string? RegisterToSet;
		public int RegisterValue;
		// watchpoints: exposed the same way EmulationApi exposes its private
		// DebuggableCore property (via reflection in McpToolset)
		public IDebuggable? DebuggableCore { get; set; }

		public int FrameCount() => FrameCountValue;

		public (string, int) Disassemble(uint pc, string? name = null) => ("MOVE.L D0,D1", 2);

		public ulong? GetRegister(string name) => Registers.TryGetValue(name, out var v) ? v : null;

		public IReadOnlyDictionary<string, ulong> GetRegisters() => Registers;

		public void SetRegister(string register, int value) { RegisterToSet = register; RegisterValue = value; }

		public long TotalExecutedCycles() => 123456;

		public string GetSystemId() => SystemId;

		public bool IsLagged() => Lagged;

		public void SetIsLagged(bool value = true) => Lagged = value;

		public int LagCount() => LagCountValue;

		public void SetLagCount(int count) => LagCountValue = count;

		public IGameInfo? GetGameInfo() => new FakeGameInfo { Name = "Test ROM", Hash = RomHash, System = "GEN" };
	}

	/// <summary>Fake IDebuggable whose MemoryCallbacks can be fired manually from a test.</summary>
	public sealed class FakeDebuggable : IDebuggable
	{
		public FakeMemoryCallbacks Callbacks { get; } = new();

		public IMemoryCallbackSystem MemoryCallbacks => Callbacks;

		// Simulates the gpgx core's VDP view (plane nametable bases/dims).
		public int PlaneABase = 0x0000;
		public int PlaneBBase = 0xE000;
		public int PlaneAWidth = 64, PlaneAHeight = 32;
		public int PlaneBWidth = 64, PlaneBHeight = 32;

		public FakeVdpView UpdateVDPViewContext()
		{
			return new FakeVdpView
			{
				NTA = new FakeNameTable { Baseaddr = PlaneABase, Width = PlaneAWidth, Height = PlaneAHeight },
				NTB = new FakeNameTable { Baseaddr = PlaneBBase, Width = PlaneBWidth, Height = PlaneBHeight },
			};
		}

		public sealed class FakeVdpView
		{
			public FakeNameTable NTA;
			public FakeNameTable NTB;
		}

		public sealed class FakeNameTable
		{
			public int Width;
			public int Height;
			public int Baseaddr;
		}
	}

	public sealed class FakeMemoryCallbacks : IMemoryCallbackSystem
	{
		public readonly List<IMemoryCallback> Registered = new();
		public bool ExecuteCallbacksAvailableValue = true;
		public string[] Scopes = new[] { "M68K BUS" };

		public bool ExecuteCallbacksAvailable => ExecuteCallbacksAvailableValue;
		public bool HasReads => Registered.Exists(c => c.Type == MemoryCallbackType.Read);
		public bool HasWrites => Registered.Exists(c => c.Type == MemoryCallbackType.Write);
		public bool HasExecutes => Registered.Exists(c => c.Type == MemoryCallbackType.Execute);
		public string[] AvailableScopes => Scopes;

		public void Add(IMemoryCallback callback) => Registered.Add(callback);

		public void Remove(BizHawk.Emulation.Common.MemoryCallbackDelegate action)
			=> Registered.RemoveAll(c => c.Callback == action);

		public System.Collections.Generic.IEnumerator<IMemoryCallback> GetEnumerator() => Registered.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

		/// <summary>Simulates the core firing a memory access at the given address,
		/// replicating MemoryCallbackSystem.Call(): only callbacks whose address
		/// matches (addr & AddressMask) are invoked.</summary>
		public void Fire(uint address, uint value = 0, uint flags = 0)
		{
			foreach (var cb in Registered)
			{
				if (!cb.Address.HasValue || cb.Address == (address & cb.AddressMask))
					cb.Callback(address, value, flags);
			}
		}
	}

	public sealed class FakeGameInfo : IGameInfo
	{
		public string? Name { get; set; }
		public string? Hash { get; set; }
		public string System { get; set; } = "";
	}

	public sealed class FakeJoypadApi : IJoypadApi
	{
		public IReadOnlyDictionary<string, object> Current = new Dictionary<string, object> { ["A"] = true, ["Up"] = false };
		public IReadOnlyDictionary<string, bool>? LastSet;
		public int? LastController;

		public IReadOnlyDictionary<string, object> Get(int? controller = null) => Current;

		public void Set(IReadOnlyDictionary<string, bool> buttons, int? controller = null)
		{
			LastSet = buttons;
			LastController = controller;
		}

		public void Set(string button, bool? state = null, int? controller = null) { }
	}

	public sealed class FakeSaveStateApi : ISaveStateApi
	{
		public string? SavedTo;
		public string? LoadedFrom;
		public bool LoadResult = true;
		public int? SavedSlot;
		public int? LoadedSlot;

		public void Save(string path) => SavedTo = path;

		public bool Load(string path) { LoadedFrom = path; return LoadResult; }

		public void SaveSlot(int slotNum) => SavedSlot = slotNum;

		public bool LoadSlot(int slotNum) { LoadedSlot = slotNum; return LoadResult; }
	}

	public sealed class FakeGuiApi : IGuiApi
	{
		public readonly List<string> Messages = new();
		public int ClearTextCalls;
		public int DrawCount;
		public (int x, int y, string text, int? fontsize)? LastDraw;
		public (int x, int y, int w, int h)? LastRect;
		public (int x1, int y1, int x2, int y2)? LastLine;

		public void AddMessage(string message, int? duration = null) => Messages.Add(message);

		public void ClearText() => ClearTextCalls++;

		public void WithSurface(DisplaySurfaceID surfaceID, Action drawingCallsFunc) => drawingCallsFunc();

		public void ClearGraphics(DisplaySurfaceID? surfaceID = null) => ClearTextCalls++;

		public void DrawString(int x, int y, string message, System.Drawing.Color? forecolor = null, System.Drawing.Color? backcolor = null, int? fontsize = null, string fontfamily = null, string fontstyle = null, string horizalign = null, string vertalign = null, DisplaySurfaceID? surfaceID = null)
		{
			DrawCount++;
			LastDraw = (x, y, message, fontsize);
		}

		public void DrawRectangle(int x, int y, int width, int height, System.Drawing.Color? line = null, System.Drawing.Color? background = null, DisplaySurfaceID? surfaceID = null)
		{
			DrawCount++;
			LastRect = (x, y, width, height);
		}

		public void DrawLine(int x1, int y1, int x2, int y2, System.Drawing.Color? color = null, DisplaySurfaceID? surfaceID = null)
		{
			DrawCount++;
			LastLine = (x1, y1, x2, y2);
		}
	}

	public sealed class FakeInputApi : IInputApi
	{
		public IReadOnlyList<string> Pressed = new List<string> { "Shift+A" };
		public IReadOnlyDictionary<string, object> Mouse = new Dictionary<string, object> { ["X"] = 10, ["Left"] = true };

		public IReadOnlyDictionary<string, object> GetMouse() => Mouse;

		public IReadOnlyList<string> GetPressedButtons() => Pressed;
	}

	public sealed class FakeMovieApi : IMovieApi
	{
		public bool Loaded = true;
		public string Name = "test.bk2";
		public int LengthValue = 500;
		public string ModeValue = "PLAY";
		public ulong Rerecords = 42;

		public bool IsLoaded() => Loaded;

		public string Filename() => Name;

		public string GetInputAsMnemonic(int frame) => frame == 0 ? "|..|..|" : "|.A|..|";

		public bool GetReadOnly() => false;

		public ulong GetRerecordCount() => Rerecords;

		public int Length() => LengthValue;

		public string Mode() => ModeValue;

		public double GetFps() => 60.0;

		public IReadOnlyDictionary<string, string> GetHeader() => new Dictionary<string, string> { ["Platform"] = "GEN" };
	}

	public sealed class FakeUserDataApi : IUserDataApi
	{
		public readonly Dictionary<string, object> Data = new();

		public void Set(string name, object value) => Data[name] = value;

		public object Get(string key) => Data.TryGetValue(key, out var v) ? v : null;

		public void Clear() => Data.Clear();

		public bool Remove(string key) => Data.Remove(key);
	}

	/// <summary>Wires the fakes into IHostApis.</summary>
	public sealed class FakeApis : IHostApis
	{
		public FakeMemoryApi MemoryApi = new();
		public FakeEmulationApi EmulationApi = new();
		public FakeEmuClientApi EmuClientApi = new();
		public FakeJoypadApi JoypadApi = new();
		public FakeSaveStateApi SaveStateApi = new();
		public FakeGuiApi GuiApi = new();
		public FakeInputApi InputApi = new();
		public FakeMovieApi MovieApi = new();
		public FakeUserDataApi UserDataApi = new();
		public readonly List<string> Logged = new();
		public int StopServerCalls;

		public IMemoryApi? Memory => MemoryApi;
		public IEmulationApi? Emulation => EmulationApi;
		public IEmuClientApi? EmuClient => EmuClientApi;
		public IJoypadApi? Joypad => JoypadApi;
		public ISaveStateApi? SaveState => SaveStateApi;
		public IGuiApi? Gui => GuiApi;
		public IInputApi? Input => InputApi;
		public IMovieApi? Movie => MovieApi;
		public IUserDataApi? UserData => UserDataApi;
		public string? ServerUrl => "http://127.0.0.1:8767/mcp/";

		public void Log(string line) => Logged.Add(line);

		public void StopServer() => StopServerCalls++;

		/// <summary>Wires up watchpoint support like the gpgx core would.</summary>
		public FakeDebuggable EnableWatchpoints()
		{
			var dbg = new FakeDebuggable();
			EmulationApi.DebuggableCore = dbg;
			return dbg;
		}

		public McpToolset Toolset() => new(this, new InlineDispatcher());
	}
}
