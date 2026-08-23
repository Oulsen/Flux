using System.Reflection;

namespace Flux.Web;

/// <summary>
/// The running build's version, as a string fit for a screen.
///
/// It lives beside the web layer because that is its main consumer: the page
/// footer exists to answer "which build is this phone actually talking to?"
/// without anyone inspecting files on the PC. The console banner reads the same
/// value, so the two can never disagree.
/// </summary>
public static class FluxVersion {
  /// <summary>The version of the host executable, with Source Link's commit suffix removed.</summary>
  public static string Current { get; } = Read();

  private static string Read() {
    string? informational = Assembly.GetEntryAssembly()?
      .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
      .InformationalVersion;

    if(informational is null) {
      return "unknown";
    }

    // Source Link appends "+<commit sha>", which is noise on a banner and in a
    // footer alike.
    int build = informational.IndexOf('+', StringComparison.Ordinal);
    return build < 0 ? informational : informational[..build];
  }
}
