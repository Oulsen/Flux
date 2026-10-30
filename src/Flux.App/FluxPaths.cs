namespace Flux.App;

/// <summary>
/// Where the desktop app keeps things, named after <see cref="FluxEdition"/>.
///
/// Settings go in the roaming folder and the rest in local application data. A log and a
/// browser profile belong to this machine and are nobody's preference, and roaming them
/// would copy megabytes of cache between machines to carry nothing worth keeping.
/// </summary>
internal static class FluxPaths {
  /// <summary>
  /// What is tuned on the page and the bridge key: %APPDATA%\Flux, or Flux-dev. Handed to
  /// the other projects through configuration, since they cannot see this class.
  /// </summary>
  internal static string Settings { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
    FluxEdition.Name);

  /// <summary>The folder itself, for what belongs to this machine and needs no folder of its own.</summary>
  internal static string Root { get; } = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    FluxEdition.Name);

  /// <summary>The rolling log, one file per day.</summary>
  internal static string Logs { get; } = Path.Combine(Root, "logs");

  /// <summary>
  /// The window's browser profile. Its own folder, because WebView2 would otherwise put
  /// it beside the executable, which an installed app cannot write to.
  /// </summary>
  internal static string WebView2 { get; } = Path.Combine(Root, "WebView2");
}
