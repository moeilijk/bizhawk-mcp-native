// Minimal stubs of the .NET Framework types the linked-in McpToolset.cs
// references (net48 product code) that aren't in .NET 8 base. DoEvents is
// a no-op; Color/ColorTranslator implement just enough for ParseColor.

namespace System.Windows.Forms
{
	public static class Application
	{
		public static void DoEvents()
		{
		}
	}
}

namespace System.Drawing
{
	public struct Color
	{
		public static Color Empty;

		public static Color FromArgb(int argb) => new();

		public static Color FromArgb(int a, int r, int g, int b) => new();
	}

	public static class ColorTranslator
	{
		public static Color FromHtml(string htmlColor) => new();
	}
}
