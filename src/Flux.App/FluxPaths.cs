namespace Flux.App;

/// <summary>
/// Where the desktop app keeps what is not a setting.
///
/// Local application data rather than the roaming folder the settings live in. A log
/// and a browser profile are this machine's and nobody's preference: roaming them
/// would copy megabytes of cache between machines to carry nothing worth keeping.
/// </summary>
internal static class FluxPaths {
  /// <summary>The folder itself, for what belongs to this machine and needs no folder of its own.</summary>
  internal static string Root { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Flux");

  /// <summary>The rolling log, one file per day.</summary>
  internal static string Logs { get; } = Path.Combine(Root, "logs");

  /// <summary>
  /// The window's browser profile. Its own folder, because WebView2 would otherwise put
  /// it beside the executable, which an installed app cannot write to.
  /// </summary>
  internal static string WebView2 { get; } = Path.Combine(Root, "WebView2");
}
