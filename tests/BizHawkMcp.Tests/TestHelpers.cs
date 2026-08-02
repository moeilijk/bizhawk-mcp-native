using System.Text.Json;

namespace BizHawkMcp.Tests
{
	public static class TestHelpers
	{
		public static JsonElement Js(string json) => JsonDocument.Parse(json).RootElement.Clone();
	}
}
