using Velopack;
using Velopack.Sources;

namespace Flux.App;

/// <summary>
/// Whether this copy is the installed Flux or a build run from source, and what that
/// changes.
///
/// The two keep entirely apart: their own folders under both application data roots,
/// their own single-instance session and their own port. A build run from source is
/// where settings get experimented with, and an experiment must not reach the copy the
/// lights run on at night. Apart is also what lets both run at once, the installed one
/// on 5050 and the development one on 5051. Only one of them can stream to the bridge,
/// which has a single entertainment slot, and the second to ask is refused.
///
/// The folder name decides it rather than anything about the build. Setup.exe installs
/// into %LOCALAPPDATA%\Flux and refuses a folder that is already there, so a development
/// build writing its log into that folder made the installer believe Flux was installed.
///
/// Decided by Velopack's own record of the install rather than by a setting, so a copy
/// cannot be told it is the other edition and quietly pick up the other's bridge key.
/// </summary>
internal static class FluxEdition {
  /// <summary>True for a copy that Setup.exe installed.</summary>
  internal static bool Installed { get; } = IsInstalled();

  /// <summary>
  /// The name the folders and the session are called by: Flux when installed, Flux-dev
  /// when run from source.
  /// </summary>
  internal static string Name { get; } = Installed ? "Flux" : "Flux-dev";

  /// <summary>What the window and the tray say, so the two are told apart at a glance.</summary>
  internal static string Title { get; } = Installed ? "Flux" : "Flux (dev)";

  /// <summary>
  /// The development build's port, one above the installed default, so both can run.
  /// appsettings.local.json and the command line still override it.
  /// </summary>
  internal const int DevelopmentPort = 5051;

  /// <summary>
  /// Asks Velopack, which looks for its own files beside the executable and reaches
  /// nothing on the network. A copy it cannot place is a development build.
  /// </summary>
  private static bool IsInstalled() {
    try {
      return new UpdateManager(new GithubSource(UpdateService.Repository, accessToken: null, prerelease: false)).IsInstalled;
    } catch(Exception) {
      return false;
    }
  }
}
