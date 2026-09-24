namespace Flux.App;

/// <summary>
/// What the command line says about the process itself, as opposed to about its
/// configuration.
///
/// Taken off the arguments before the host sees them. The configuration's command-line
/// provider reads <c>--name value</c> pairs, so a bare <c>--console</c> would either
/// swallow the next argument as its value or be read as a key with none - a flag that
/// changes what the process is does not belong in the settings it loads.
/// </summary>
/// <param name="Console">
/// Whether to show a console beside the window. For development: the log goes to a
/// file either way, and the console diagnostics (<c>Console:BeatLog</c>,
/// <c>Console:AudioMeter</c>) have nowhere to print without one.
/// </param>
/// <param name="HostArguments">Everything else, for the host's configuration.</param>
internal sealed record LaunchOptions(bool Console, string[] HostArguments) {
  internal const string ConsoleFlag = "--console";

  internal static LaunchOptions Parse(string[] args) {
    ArgumentNullException.ThrowIfNull(args);

    bool console = args.Any(IsConsoleFlag);

    return new LaunchOptions(console, [.. args.Where(arg => !IsConsoleFlag(arg))]);
  }

  private static bool IsConsoleFlag(string arg) {
    return string.Equals(arg, ConsoleFlag, StringComparison.OrdinalIgnoreCase);
  }
}
