using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Flux.App;

/// <summary>
/// Where the window was, so it opens there again.
///
/// The bounds are the restored ones, in screen pixels, even when the window was
/// maximized or fullscreen: un-maximizing should give back the size somebody chose,
/// not the size of the screen. <see cref="Maximized"/> rides on top.
/// </summary>
internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized) {
  /// <summary>
  /// The smallest size worth restoring, and how much of the title bar has to be on a
  /// screen to count. Carried from Clowder, where the same check guards the same
  /// failure: a window restored onto a monitor that has since been unplugged, off
  /// every screen and out of reach.
  /// </summary>
  private const int MinWidth = 320;

  /// <inheritdoc cref="MinWidth"/>
  private const int MinHeight = 240;

  /// <inheritdoc cref="MinWidth"/>
  private const int TitleBarHeight = 40;

  /// <inheritdoc cref="MinWidth"/>
  private const int GripWidth = 120;

  /// <inheritdoc cref="MinWidth"/>
  private const int GripHeight = 20;

  /// <summary>The bounds as a rectangle.</summary>
  internal Rectangle Bounds => new Rectangle(this.X, this.Y, this.Width, this.Height);

  /// <summary>
  /// Whether this can be restored onto these screens: a sane size, and enough of the
  /// title bar on one of them that the window can be grabbed and dragged by hand.
  /// </summary>
  internal bool IsUsableOn(IEnumerable<Rectangle> workingAreas) {
    ArgumentNullException.ThrowIfNull(workingAreas);

    if(this.Width < MinWidth || this.Height < MinHeight) {
      return false;
    }

    Rectangle titleBar = new Rectangle(this.X, this.Y, this.Width, TitleBarHeight);

    return workingAreas.Any(area => {
      Rectangle visible = Rectangle.Intersect(area, titleBar);
      return visible.Width >= GripWidth && visible.Height >= GripHeight;
    });
  }
}

/// <summary>
/// The placement's file, beside the log rather than beside the settings: which
/// monitor the window was on is a fact about this machine and nobody's preference.
///
/// Failures are swallowed both ways, as the other stores do. A placement that cannot
/// be read opens the window centred, and one that cannot be written is forgotten -
/// neither is worth a message to anybody.
/// </summary>
internal sealed class WindowPlacementStore {
  private const string FileName = "window.json";

  private readonly string path;
  private readonly ILogger log;

  internal WindowPlacementStore(string directory, ILogger log) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);

    this.path = Path.Combine(directory, FileName);
    this.log = log;
  }

  internal WindowPlacement? Load() {
    if(!File.Exists(this.path)) {
      return null;
    }

    try {
      return JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(this.path));
    } catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException) {
      this.log.LogDebug(e, "The window placement in {Path} could not be read.", this.path);
      return null;
    }
  }

  internal void Save(WindowPlacement placement) {
    try {
      Directory.CreateDirectory(Path.GetDirectoryName(this.path)!);
      File.WriteAllText(this.path, JsonSerializer.Serialize(placement));
    } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
      this.log.LogDebug(e, "The window placement could not be saved to {Path}.", this.path);
    }
  }
}
