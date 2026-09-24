using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// Flux's mark, from the multi-size icon beside the executable.
///
/// Loaded from the file rather than taken from the executable's embedded icon, which
/// hands back one size: the file carries a cut drawn for each size band, and loading it
/// lets Windows pick the 16 px one for the title bar and the tray instead of shrinking
/// the 32 px one. The mark itself lives in <c>assets/branding</c>, with how it is built.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FluxIcon {
  private static readonly string IconPath = Path.Combine(AppContext.BaseDirectory, "flux.ico");

  /// <summary>The icon at the size Windows asks for, or the generic one if the file is missing.</summary>
  internal static Icon Load(Size size) {
    try {
      return new Icon(IconPath, size);
    } catch(Exception e) when(e is IOException or ArgumentException or UnauthorizedAccessException) {
      return (Icon)SystemIcons.Application.Clone();
    }
  }
}
