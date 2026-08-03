using System;
using System.Collections.Generic;

// Stubs of the BizHawk.Emulation.Common service interfaces used by the
// linked-in McpToolset.cs (watchpoints path). Keep in sync with the pinned
// commit's src/BizHawk.Emulation.Common/Interfaces/.

namespace BizHawk.Emulation.Common
{
	public delegate uint? MemoryCallbackDelegate(uint address, uint value, uint flags);

	public enum MemoryCallbackType
	{
		Read,
		Write,
		Execute,
	}

	public interface IMemoryCallback
	{
		MemoryCallbackType Type { get; }
		string Name { get; }
		MemoryCallbackDelegate Callback { get; }
		uint? Address { get; }
		uint? AddressMask { get; }
		string Scope { get; }
	}

	public interface IMemoryCallbackSystem : IEnumerable<IMemoryCallback>
	{
		bool ExecuteCallbacksAvailable { get; }
		bool HasReads { get; }
		bool HasWrites { get; }
		bool HasExecutes { get; }
		void Add(IMemoryCallback callback);
		void Remove(MemoryCallbackDelegate action);
		string[] AvailableScopes { get; }
	}

	public interface IDebuggable
	{
		IMemoryCallbackSystem MemoryCallbacks { get; }
	}

	// ── in-memory savestates (IStatable via Emulator.ServiceProvider) ────────
	public interface IEmulatorService
	{
	}

	public interface IEmulator : IEmulatorService, IDisposable
	{
		IEmulatorServiceProvider ServiceProvider { get; }
	}

	public interface IEmulatorServiceProvider
	{
		T GetService<T>() where T : IEmulatorService;
		object? GetService(Type t);
	}

	public interface IStatable : IEmulatorService
	{
		bool AvoidRewind { get; }
		void SaveStateBinary(System.IO.BinaryWriter writer);
		void LoadStateBinary(System.IO.BinaryReader reader);
	}

	// Minimal MemoryDomain — only the members the freeze (cheat) path touches:
	// Name/Writable for the guard, plus a test seam for the Watch reads/writes.
	public class MemoryDomain
	{
		public string Name = "";
		public long Size;
		public bool Writable = true;

		public Func<long, byte>? PeekByteFn;
		public Action<long, byte>? PokeByteFn;

		public virtual byte PeekByte(long addr) => PeekByteFn != null ? PeekByteFn(addr) : (byte)0;

		public virtual void PokeByte(long addr, byte val) => PokeByteFn?.Invoke(addr, val);
	}
}
