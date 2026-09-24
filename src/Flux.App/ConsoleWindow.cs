using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// Gives a window application a console, for <c>--console</c>.
///
/// The console it was started from when there is one, which is what makes
/// <c>dotnet run -- --console</c> print where it was typed and stop on Ctrl+C there.
/// A new console window otherwise, which is the case for a shortcut carrying the flag.
/// Called before anything touches <see cref="Console"/>, since .NET binds the standard
/// streams on first use and would otherwise keep writing to nothing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleWindow {
  /// <summary>ATTACH_PARENT_PROCESS.</summary>
  private const int AttachParentProcess = -1;

  internal static void Open() {
    if(!AttachConsole(AttachParentProcess)) {
      AllocConsole();
    }
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AttachConsole(int processId);

  [DllImport("kernel32.dll", SetLastError = true)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AllocConsole();
}
