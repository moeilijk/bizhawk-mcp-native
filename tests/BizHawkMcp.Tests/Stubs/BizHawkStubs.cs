using System;
using System.Collections.Generic;
using BizHawk.Emulation.Common;

// Stubs of the BizHawk ApiHawk interfaces used by McpToolset — only the
// members the linked-in source actually calls. Do NOT extend these unless
// McpToolset starts using more API surface (keep in sync with the pinned
// BizHawk commit's Api/Interfaces/).

namespace BizHawk.Client.Common
{
	public interface IGameInfo
	{
		string? Name { get; }
		string? Hash { get; }
		string System { get; }
	}

	public interface IMemoryApi
	{
		void SetBigEndian(bool enabled = true);
		IReadOnlyCollection<string> GetMemoryDomainList();
		uint GetMemoryDomainSize(string name = "");
		string GetCurrentMemoryDomain();
		uint GetCurrentMemoryDomainSize();
		bool UseMemoryDomain(string domain);
		string HashRegion(long addr, int count, string? domain = null);
		uint ReadByte(long addr, string? domain = null);
		IReadOnlyList<byte> ReadByteRange(long addr, int length, string? domain = null);
		float ReadFloat(long addr, string? domain = null);
		int ReadS8(long addr, string? domain = null);
		int ReadS16(long addr, string? domain = null);
		int ReadS24(long addr, string? domain = null);
		int ReadS32(long addr, string? domain = null);
		uint ReadU8(long addr, string? domain = null);
		uint ReadU16(long addr, string? domain = null);
		uint ReadU32(long addr, string? domain = null);
		void WriteByte(long addr, uint value, string? domain = null);
		void WriteByteRange(long addr, IReadOnlyList<byte> memoryblock, string? domain = null);
		void WriteFloat(long addr, float value, string? domain = null);
		void WriteS8(long addr, int value, string? domain = null);
		void WriteS16(long addr, int value, string? domain = null);
		void WriteS24(long addr, int value, string? domain = null);
		void WriteS32(long addr, int value, string? domain = null);
		void WriteU8(long addr, uint value, string? domain = null);
		void WriteU16(long addr, uint value, string? domain = null);
		void WriteU24(long addr, uint value, string? domain = null);
		void WriteU32(long addr, uint value, string? domain = null);
	}

	public interface IEmulationApi
	{
		int FrameCount();
		(string Disasm, int Length) Disassemble(uint pc, string? name = null);
		ulong? GetRegister(string name);
		IReadOnlyDictionary<string, ulong> GetRegisters();
		void SetRegister(string register, int value);
		long TotalExecutedCycles();
		string GetSystemId();
		bool IsLagged();
		void SetIsLagged(bool value = true);
		int LagCount();
		void SetLagCount(int count);
		IGameInfo? GetGameInfo();
		string GetBoardName();
		string GetDisplayType();
		IReadOnlyDictionary<string, string?> GetGameOptions();
		void LimitFramerate(bool enabled);
	}

	public interface IEmuClientApi
	{
		void DoFrameAdvance();
		void DoFrameAdvanceAndUnpause();
		bool IsPaused();
		void Pause();
		void Unpause();
		void TogglePause();
		void SpeedMode(int percent);
		void Screenshot(string? path = null);
		void SetScreenshotOSD(bool value);
		void EnableRewind(bool enabled);
		void FrameSkip(int numFrames);
		bool GetSoundOn();
		void SetSoundOn(bool enable);
		bool OpenRom(string path);
		void CloseRom();
		void RebootCore();
	}

	public interface IJoypadApi
	{
		IReadOnlyDictionary<string, object> Get(int? controller = null);
		void Set(IReadOnlyDictionary<string, bool> buttons, int? controller = null);
		void Set(string button, bool? state = null, int? controller = null);
	}

	public interface ISaveStateApi
	{
		void Save(string path);
		bool Load(string path);
		void SaveSlot(int slotNum);
		bool LoadSlot(int slotNum);
	}

	public enum DisplaySurfaceID { Client = 1, EmuCore = 0 }

	public interface IGuiApi
	{
		void AddMessage(string message, int? duration = null);
		void ClearText();
		void WithSurface(DisplaySurfaceID surfaceID, Action<IGuiApi> drawingCallsFunc);
		void WithSurface(DisplaySurfaceID surfaceID, Action drawingCallsFunc);
		void ClearGraphics(DisplaySurfaceID? surfaceID = null);
		void DrawString(int x, int y, string message, System.Drawing.Color? forecolor = null, System.Drawing.Color? backcolor = null, int? fontsize = null, string? fontfamily = null, string? fontstyle = null, string? horizalign = null, string? vertalign = null, DisplaySurfaceID? surfaceID = null);
		void DrawRectangle(int x, int y, int width, int height, System.Drawing.Color? line = null, System.Drawing.Color? background = null, DisplaySurfaceID? surfaceID = null);
		void DrawLine(int x1, int y1, int x2, int y2, System.Drawing.Color? color = null, DisplaySurfaceID? surfaceID = null);
	}

	public interface IInputApi
	{
		IReadOnlyDictionary<string, object> GetMouse();
		IReadOnlyList<string> GetPressedButtons();
	}

	public interface IMovieApi
	{
		bool IsLoaded();
		string Filename();
		string GetInputAsMnemonic(int frame);
		bool GetReadOnly();
		ulong GetRerecordCount();
		int Length();
		string Mode();
		double GetFps();
		IReadOnlyDictionary<string, string> GetHeader();
		bool PlayFromStart(string path = "");
		void Save(string filename = "");
		void Stop(bool saveChanges = true);
	}

	public interface IUserDataApi
	{
		void Set(string name, object value);
		object? Get(string key);
		void Clear();
		bool Remove(string key);
	}
}

	// ── freeze (emulator cheat engine) stubs ─────────────────────────────────
	// Mirror the members of the real Cheat/Watch/CheatCollection the linked
	// McpToolset.cs uses. The real types are in BizHawk.Client.Common (see
	// tools/Cheat.cs, tools/Watch/Watch.cs, tools/CheatList.cs).

	public enum WatchSize : int
	{
		Byte = 1,
		Word = 2,
		DWord = 4,
		Separator = 0,
	}

	public enum WatchDisplayType
	{
		Hex,
	}

	public class Watch
	{
		public MemoryDomain Domain { get; }
		public long Address { get; }
		public WatchSize Size { get; }
		public bool BigEndian { get; set; }
		public string Notes { get; set; } = "";
		public int Value => (int)Domain.PeekByte(Address);

		public Watch(MemoryDomain domain, long address, WatchSize size, bool bigEndian, string note)
		{
			Domain = domain;
			Address = address;
			Size = size;
			BigEndian = bigEndian;
			Notes = note;
		}

		public static Watch GenerateWatch(MemoryDomain domain, long address, WatchSize size, WatchDisplayType type, bool bigEndian, string note = "", long value = 0, long prev = 0, int changeCount = 0)
		{
			return new Watch(domain, address, size, bigEndian, note);
		}

		public bool Contains(long addr) => Size switch
		{
			WatchSize.Word => addr == Address || addr == Address + 1,
			WatchSize.DWord => addr >= Address && addr <= Address + 3,
			_ => addr == Address,
		};
	}

	public class Cheat
	{
		private readonly Watch _watch;
		private readonly int _val;
		private readonly bool _enabled;

		public enum CompareType
		{
			None,
			Equal,
		}

		public Cheat(Watch watch, int value, int? compare = null, bool enabled = true, CompareType comparisonType = CompareType.None)
		{
			_watch = watch;
			_val = value;
			_enabled = enabled;
			// mirrors Cheat.Pulse(): poke per watch size/endianness
			if (_watch.Domain.PokeByteFn != null)
			{
				void Poke(long addr, byte b) => _watch.Domain.PokeByte(addr, b);
				switch (_watch.Size)
				{
					case WatchSize.Byte:
						Poke(_watch.Address, (byte)value);
						break;
					case WatchSize.Word:
						if (_watch.BigEndian) { Poke(_watch.Address, (byte)(value >> 8)); Poke(_watch.Address + 1, (byte)value); }
						else { Poke(_watch.Address, (byte)value); Poke(_watch.Address + 1, (byte)(value >> 8)); }
						break;
					case WatchSize.DWord:
						if (_watch.BigEndian)
						{
							Poke(_watch.Address, (byte)(value >> 24)); Poke(_watch.Address + 1, (byte)(value >> 16));
							Poke(_watch.Address + 2, (byte)(value >> 8)); Poke(_watch.Address + 3, (byte)value);
						}
						else
						{
							Poke(_watch.Address, (byte)value); Poke(_watch.Address + 1, (byte)(value >> 8));
							Poke(_watch.Address + 2, (byte)(value >> 16)); Poke(_watch.Address + 3, (byte)(value >> 24));
						}
						break;
				}
			}
		}

		public bool IsSeparator => false;
		public bool Enabled => _enabled;
		public long? Address => _watch.Address;
		public int? Value => _val;
		public bool? BigEndian => _watch.BigEndian;
		public MemoryDomain Domain => _watch.Domain;
		public WatchSize Size => _watch.Size;
		public string Name => _watch.Notes;

		public bool Contains(long addr) => _watch.Contains(addr);
	}

	public class CheatCollection : ICollection<Cheat>
	{
		private readonly List<Cheat> _cheats = new();

		public int Count => _cheats.Count;
		public bool IsReadOnly => false;

		public void Add(Cheat cheat) => _cheats.Add(cheat);

		public void AddRange(IEnumerable<Cheat> cheats) => _cheats.AddRange(cheats);

		public void RemoveRange(IEnumerable<Cheat> cheats)
		{
			foreach (var c in cheats) _cheats.Remove(c);
		}

		public void Clear() => _cheats.Clear();

		public bool Contains(Cheat cheat) => _cheats.Contains(cheat);

		public bool Remove(Cheat cheat) => _cheats.Remove(cheat);

		public bool IsActive(MemoryDomain domain, long address) => _cheats.Exists(c => !c.IsSeparator && c.Domain == domain && c.Address == address);

		public void CopyTo(Cheat[] array, int arrayIndex) => _cheats.CopyTo(array, arrayIndex);

		public IEnumerator<Cheat> GetEnumerator() => _cheats.GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	// ── Lua stubs ─────────────────────────────────────────────────────────────
	// Mirror the members of the real LuaFile/LuaLibraries the linked
	// McpToolset.cs uses (BizHawk.Client.Common/lua/). The real LuaThread is an
	// NLua type — stubbed in NLuaStubs.cs.

	public interface IToolForm
	{
	}

	public interface IToolApi
	{
		IToolForm GetTool(string name);
	}

	public class LuaFile
	{
		public LuaFile(string path, Action onFunctionListChange)
		{
			Path = path;
		}

		public string Path { get; }
		public bool IsSeparator => false;
		public bool Enabled { get; private set; }
		public bool Paused { get; private set; }
		public NLua.LuaThread? Thread { get; private set; }

		public void Start(NLua.LuaThread thread)
		{
			Thread = thread;
			Enabled = true;
			Paused = false;
		}

		public void Stop() => Enabled = false;

		public void TogglePause()
		{
			if (Enabled) Paused = !Paused;
		}
	}

	public class LuaLibraries
	{
		public List<LuaFile> ScriptList { get; } = new();
		public LuaDocumentation Docs { get; } = new();

		public virtual object[] ExecuteString(string command) => throw new NotImplementedException();

		public virtual NLua.LuaThread SpawnCoroutineAndSandbox(string file) => new();

		public virtual NLua.LuaThread SpawnBlankCoroutineAndSandbox(string directory) => new();

		public virtual void Close() { }
	}

	public class LibraryFunction
	{
		public string Library = "";
		public string LibraryDescription = "";
		public string Name = "";
		public string Description = "";
		public string? Example;
		public string ParameterList = "()";
		public string ReturnType = "void";
		public bool IsDeprecated;
		public bool SuggestInREPL = true;
	}

	public class LuaDocumentation : List<LibraryFunction>
	{
	}
