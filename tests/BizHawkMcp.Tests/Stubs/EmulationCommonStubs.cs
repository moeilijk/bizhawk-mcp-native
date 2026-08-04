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

	// ── code/data logger (ICodeDataLogger via Emulator.ServiceProvider) ─────
	// Mirrors the real BizHawk.Emulation.Common API: the log is a per-domain
	// bitmap (1 byte per address) the core ORs access flags into.
	public interface ICodeDataLogger : IEmulatorService
	{
		void SetCDL(ICodeDataLog? cdl);
		void NewCDL(ICodeDataLog cdl);
		void DisassembleCDL(System.IO.Stream s, ICodeDataLog cdl);
	}

	public interface ICodeDataLog : IDictionary<string, byte[]>
	{
		void Pin();
		void Unpin();
		IntPtr GetPin(string key);
		bool Has(string blockName);
		bool Active { get; set; }
		string? SubType { get; set; }
		int SubVer { get; set; }
		bool Check(ICodeDataLog other);
		void LogicalOrFrom(ICodeDataLog other);
		void ClearData();
		void Save(System.IO.Stream s);
	}

	// Base implementation, same shape as BizHawk.Emulation.Common.CodeDataLog
	// (including the BIZHAWK-CDL-2 Save layout so export tests can check it).
	public class CodeDataLog : Dictionary<string, byte[]>, ICodeDataLog
	{
		public CodeDataLog() => Active = true;

		public bool Active { get; set; }
		public string? SubType { get; set; }
		public int SubVer { get; set; }

		public void Pin() { }
		public void Unpin() { }
		public IntPtr GetPin(string key) => IntPtr.Zero;
		public bool Has(string blockName) => ContainsKey(blockName);
		public bool Check(ICodeDataLog other) => true;
		public void LogicalOrFrom(ICodeDataLog other) { }

		public void ClearData()
		{
			foreach (var v in Values) Array.Clear(v, 0, v.Length);
		}

		public void Save(System.IO.Stream s)
		{
			var w = new System.IO.BinaryWriter(s);
			w.Write("BIZHAWK-CDL-2");
			w.Write((SubType ?? "").PadRight(15));
			w.Write(Count);
			foreach (var kv in this)
			{
				w.Write(kv.Key);
				w.Write(kv.Value.Length);
				w.Write(kv.Value);
			}
			w.Flush();
		}
	}
}
