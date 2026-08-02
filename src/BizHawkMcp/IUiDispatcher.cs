using System;

namespace BizHawkMcp
{
	/// <summary>
	/// Marshals every emulator API call onto EmuHawk's UI thread (see
	/// <see cref="UiDispatcher"/>). Abstracted so tests can run handlers
	/// inline without a WinForms control.
	/// </summary>
	public interface IUiDispatcher
	{
		bool IsUiThread { get; }

		void Invoke(Action action);

		T Invoke<T>(Func<T> func);
	}
}
