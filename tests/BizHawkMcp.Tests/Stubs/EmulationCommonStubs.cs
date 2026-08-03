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
}
