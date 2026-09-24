using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// The native frame, painted in the page's own colours.
///
/// Without this Windows draws a light title bar over a page that is dark to its edges,
/// which on a stage built to be looked at in a dark room is the brightest thing on the
/// screen. The colours are the page's, from app.css, so the frame reads as part of it.
///
/// Dark mode is honoured from Windows 10 2004, which is Flux's floor anyway. The colour
/// attributes need Windows 11 and fail on anything older, leaving the plain dark frame -
/// cosmetic either way, so every result is ignored.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowChrome {
  /// <summary><c>--surface</c>: the page's background, and so the title bar's.</summary>
  internal static readonly Color Surface = Color.FromArgb(0x0b, 0x0d, 0x12);

  /// <summary><c>--ink</c>: the page's text, and the title's.</summary>
  internal static readonly Color Ink = Color.FromArgb(0xe8, 0xec, 0xf4);

  /// <summary><c>--edge</c>: the outline the page's own panels carry.</summary>
  internal static readonly Color Edge = Color.FromArgb(0x23, 0x28, 0x38);

  private const int UseImmersiveDarkMode = 20;
  private const int BorderColor = 34;
  private const int CaptionColor = 35;
  private const int TextColor = 36;

  internal static void Apply(IntPtr window) {
    int dark = 1;
    _ = DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref dark, sizeof(int));

    int caption = ColorRef(Surface);
    _ = DwmSetWindowAttribute(window, CaptionColor, ref caption, sizeof(int));

    int text = ColorRef(Ink);
    _ = DwmSetWindowAttribute(window, TextColor, ref text, sizeof(int));

    int border = ColorRef(Edge);
    _ = DwmSetWindowAttribute(window, BorderColor, ref border, sizeof(int));
  }

  /// <summary>
  /// A colour as DWM takes it: a COLORREF, <c>0x00BBGGRR</c>, which is a CSS hex value
  /// with its bytes reversed. Computed rather than written as literals, because a
  /// reversed literal is easy to get wrong and looks right either way.
  /// </summary>
  internal static int ColorRef(Color color) {
    return color.R | (color.G << 8) | (color.B << 16);
  }

  [DllImport("dwmapi.dll")]
  private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
