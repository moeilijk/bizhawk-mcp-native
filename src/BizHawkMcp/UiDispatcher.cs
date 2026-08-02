using System;
using System.Windows.Forms;

namespace BizHawkMcp
{
	/// <summary>
	/// Marshals every emulator API call onto EmuHawk's UI thread.
	/// BizHawk's ApiHawk implementations assume they run on the WinForms
	/// thread (frame stepping, screenshots, joypad), so the HTTP listener's
	/// worker thread must never touch them directly.
	/// </summary>
	public sealed class UiDispatcher
	{
		private readonly Control _control;

		public UiDispatcher(Control control)
		{
			_control = control;
		}

		public bool IsUiThread => !_control.InvokeRequired;

		public void Invoke(Action action)
		{
			if (IsUiThread)
			{
				action();
				return;
			}

			_control.Invoke(new Action(() =>
			{
				try
				{
					action();
				}
				catch (Exception e)
				{
					_lastError = e;
				}
			}));
		}

		public T Invoke<T>(Func<T> func)
		{
			if (IsUiThread) return func();
			T result = default!;
			_lastError = null;
			_control.Invoke(new Action(() =>
			{
				try
				{
					result = func();
				}
				catch (Exception e)
				{
					_lastError = e;
				}
			}));
			if (_lastError != null) throw _lastError;
			return result;
		}

		private volatile Exception? _lastError;
	}
}
