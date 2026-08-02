using BizHawk.Client.Common;

namespace BizHawkMcp
{
	/// <summary>
	/// The subset of <see cref="ExternalToolEntry"/> that <see cref="McpToolset"/>
	/// and <see cref="Mcp.McpHttpServer"/> depend on. Extracted so the tool logic
	/// can be unit-tested with fakes (the real implementation is a WinForms form
	/// with ApiHawk interfaces injected by EmuHawk at runtime).
	/// </summary>
	public interface IHostApis
	{
		IMemoryApi? Memory { get; }
		IEmulationApi? Emulation { get; }
		IEmuClientApi? EmuClient { get; }
		IJoypadApi? Joypad { get; }
		ISaveStateApi? SaveState { get; }
		IGuiApi? Gui { get; }
		IInputApi? Input { get; }
		IMovieApi? Movie { get; }
		IUserDataApi? UserData { get; }

		string? ServerUrl { get; }

		void Log(string line);

		void StopServer();
	}
}
