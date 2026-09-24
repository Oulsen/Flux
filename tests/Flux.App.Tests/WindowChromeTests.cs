using System.Drawing;
using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// The frame's colours as DWM receives them. A byte order the wrong way round still
/// paints a colour, just the wrong one, and it looks like a deliberate choice.
/// </summary>
public sealed class WindowChromeTests {
  [Fact]
  public void AColourReachesDwmWithItsBytesReversed() {
    Assert.Equal(0x00563412, WindowChrome.ColorRef(Color.FromArgb(0x12, 0x34, 0x56)));
  }

  /// <summary>The title bar is the page's own surface, <c>#0b0d12</c>.</summary>
  [Fact]
  public void TheTitleBarIsThePageSurface() {
    Assert.Equal(0x00120D0B, WindowChrome.ColorRef(WindowChrome.Surface));
  }
}
