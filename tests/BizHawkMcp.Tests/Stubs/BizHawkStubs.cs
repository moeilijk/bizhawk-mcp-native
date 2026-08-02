using System.Collections.Generic;

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
		string HashRegion(long addr, int count, string domain = null);
		uint ReadByte(long addr, string domain = null);
		IReadOnlyList<byte> ReadByteRange(long addr, int length, string domain = null);
		float ReadFloat(long addr, string domain = null);
		int ReadS8(long addr, string domain = null);
		int ReadS16(long addr, string domain = null);
		int ReadS24(long addr, string domain = null);
		int ReadS32(long addr, string domain = null);
		uint ReadU8(long addr, string domain = null);
		uint ReadU16(long addr, string domain = null);
		uint ReadU32(long addr, string domain = null);
		void WriteByte(long addr, uint value, string domain = null);
		void WriteByteRange(long addr, IReadOnlyList<byte> memoryblock, string domain = null);
		void WriteFloat(long addr, float value, string domain = null);
		void WriteS8(long addr, int value, string domain = null);
		void WriteS16(long addr, int value, string domain = null);
		void WriteS24(long addr, int value, string domain = null);
		void WriteS32(long addr, int value, string domain = null);
		void WriteU8(long addr, uint value, string domain = null);
		void WriteU16(long addr, uint value, string domain = null);
		void WriteU24(long addr, uint value, string domain = null);
		void WriteU32(long addr, uint value, string domain = null);
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
		void Screenshot(string path = null);
		void SetScreenshotOSD(bool value);
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
	}

	public enum DisplaySurfaceID { }

	public interface IGuiApi
	{
		void AddMessage(string message, int? duration = null);
		void ClearText();
		void DrawString(int x, int y, string message, System.Drawing.Color? forecolor = null, System.Drawing.Color? backcolor = null, int? fontsize = null, string fontfamily = null, string fontstyle = null, string horizalign = null, string vertalign = null, DisplaySurfaceID? surfaceID = null);
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
	}

	public interface IUserDataApi
	{
		void Set(string name, object value);
		object Get(string key);
		void Clear();
		bool Remove(string key);
	}
}
