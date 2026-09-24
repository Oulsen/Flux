using Microsoft.Extensions.Logging.Abstractions;
using System.Drawing;
using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// Whether a remembered place is still a place. The failure guarded against is the
/// window reopening on a monitor that is no longer plugged in, where nobody can reach
/// it to drag it back.
/// </summary>
public sealed class WindowPlacementTests {
  private static readonly Rectangle[] OneScreen = [new Rectangle(0, 0, 1920, 1040)];

  [Fact]
  public void AWindowOnTheScreenIsRestored() {
    Assert.True(new WindowPlacement(100, 100, 1280, 800, Maximized: false).IsUsableOn(OneScreen));
  }

  /// <summary>Left of the only screen: where a second monitor used to be.</summary>
  [Fact]
  public void AWindowOnAScreenThatIsGoneIsNot() {
    Assert.False(new WindowPlacement(-2000, 100, 1280, 800, Maximized: false).IsUsableOn(OneScreen));
  }

  /// <summary>
  /// Mostly off the edge is still fine while enough of the title bar is showing to
  /// grab; a sliver is not.
  /// </summary>
  [Theory]
  [InlineData(1920 - 200, true)]
  [InlineData(1920 - 60, false)]
  public void EnoughTitleBarToGrabIsWhatCounts(int x, bool usable) {
    Assert.Equal(usable, new WindowPlacement(x, 100, 1280, 800, Maximized: false).IsUsableOn(OneScreen));
  }

  [Theory]
  [InlineData(100, 800)]
  [InlineData(1280, 100)]
  public void ASizeTooSmallToUseIsNotRestored(int width, int height) {
    Assert.False(new WindowPlacement(100, 100, width, height, Maximized: false).IsUsableOn(OneScreen));
  }

  [Fact]
  public void APlacementSurvivesTheFileAndAMissingFileIsNone() {
    string folder = Path.Combine(Path.GetTempPath(), "flux-placement-test-" + Guid.NewGuid().ToString("N"));

    try {
      WindowPlacementStore store = new WindowPlacementStore(folder, NullLogger.Instance);
      Assert.Null(store.Load());

      WindowPlacement saved = new WindowPlacement(10, 20, 1300, 820, Maximized: true);
      store.Save(saved);

      Assert.Equal(saved, store.Load());
    } finally {
      if(Directory.Exists(folder)) {
        Directory.Delete(folder, recursive: true);
      }
    }
  }
}
