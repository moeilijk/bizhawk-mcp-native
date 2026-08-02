using System;
using System.Drawing;
using System.Windows.Forms;

using BizHawk.Client.Common;

using BizHawkMcp.Mcp;

namespace BizHawkMcp
{
	/// <summary>
	/// Entry point of the external tool. EmuHawk discovers this type via the
	/// <see cref="ExternalToolAttribute"/> on a class implementing
	/// <see cref="IExternalToolForm"/>, then injects the API properties below
	/// (see ApiInjector in BizHawk.Client.Common).
	/// </summary>
	[ExternalTool(
		"BizHawk MCP Server",
		Description = "Exposes a Streamable HTTP MCP endpoint to drive EmuHawk: memory read/write, joypad, frame advance, screenshots, savestates.")]
	public sealed class ExternalToolEntry : Form, IExternalToolForm
	{
		private readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9f) };
		private readonly Label _url = new() { Dock = DockStyle.Top, AutoSize = false, Height = 20 };
		private readonly Button _stop = new() { Text = "Stop server", Dock = DockStyle.Top, Height = 28 };

		private UiDispatcher? _ui;
		private McpHttpServer? _server;

		// ── ApiHawk injection ──────────────────────────────────────────────────
		// RequiredApi: tool fails to load if the provider can't supply it.
		// These five are registered unconditionally by EmuHawk.
		// NOTE: do NOT put [RequiredApi] on ApiContainer — the provider only
		// registers the IExternalApi interfaces, and a miss makes the tool
		// fail to load (ApiInjector.UpdateApis returns false).

		public ApiContainer? Api { get; set; }

		[RequiredApi] public IMemoryApi? Memory { get; set; }

		[RequiredApi] public IEmulationApi? Emulation { get; set; }

		[RequiredApi] public IEmuClientApi? EmuClient { get; set; }

		[RequiredApi] public IJoypadApi? Joypad { get; set; }

		[RequiredApi] public ISaveStateApi? SaveState { get; set; }

		[RequiredApi] public IGuiApi? Gui { get; set; }

		[RequiredApi] public IInputApi? Input { get; set; }

		[RequiredApi] public IMovieApi? Movie { get; set; }

		[RequiredApi] public IUserDataApi? UserData { get; set; }

		public ExternalToolEntry()
		{
			Text = "BizHawk MCP Server";
			ClientSize = new Size(560, 240);
			Controls.Add(_log);
			Controls.Add(_url);
			Controls.Add(_stop);
			_stop.Click += (_, _) => StopServer();
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);
			_ui = new UiDispatcher(this);
			StartServer();
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			StopServer();
			base.OnFormClosing(e);
		}

		private void StartServer()
		{
			if (_server != null) return;
			try
			{
				_server = new McpHttpServer(this, _ui!, Log);
				_server.Start();
				_url.Text = $"Listening on {_server.BaseUrl}  (env: BIZHAWK_MCP_HOST / BIZHAWK_MCP_PORT)";
				Log($"MCP server started at {_server.BaseUrl}");
			}
			catch (Exception ex)
			{
				_url.Text = "Failed to start server";
				Log($"ERROR: {ex}");
			}
		}

		internal void StopServer()
		{
			if (_server == null) return;
			try
			{
				_server.Stop();
				Log("MCP server stopped");
			}
			finally
			{
				_server = null;
				_url.Text = "Server stopped";
			}
		}

		internal string? ServerUrl => _server?.BaseUrl;

		internal void Log(string line)
		{
			if (_log.IsDisposed) return;
			if (InvokeRequired)
			{
				BeginInvoke(new Action(() => Log(line)));
				return;
			}

			_log.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
		}

		// ── IToolForm ──────────────────────────────────────────────────────────

		public void UpdateValues(ToolFormUpdateType type)
		{
		}

		public void Restart()
		{
		}

		public bool AskSaveChanges() => true;

		public bool IsActive => !IsDisposed;

		public bool IsLoaded => Visible;
	}
}
