using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// The tray icon and the window, and how they end.
///
/// The message loop runs this rather than the window, so the window can come and go
/// while the process stays - which is the whole of close-to-tray. What ends the loop
/// is the host stopping, and nothing else: the tray's Quit, Ctrl+C in a console, and
/// Windows ending the session all ask the host to stop, and the loop follows it. One
/// way out means there is no path that quits the window and leaves the lights running,
/// or the other way round.
///
/// Windows ending the session is the one way out that waits for the host rather than
/// asking it. Once Windows is told the session may end it can end the process at any
/// point, and a stop still running on the thread pool then would cut the stream to the
/// bridge mid-frame. So the stop happens while Windows is still waiting for the answer.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ShellContext: ApplicationContext {
  private readonly Uri page;
  private readonly IHostApplicationLifetime lifetime;
  private readonly Action stopHost;
  private readonly ILogger log;
  private readonly WindowsFormsSynchronizationContext ui;
  private readonly ContextMenuStrip menu;
  private readonly Icon trayIcon;
  private readonly NotifyIcon tray;
  private readonly CancellationTokenRegistration stopping;

  private MainWindow? window;
  private bool hiddenOnce;
  private bool watchingSession;

  /// <param name="page">The host's own page, for the window and for the browser fallback.</param>
  /// <param name="profileFolder">Where the window's browser profile lives.</param>
  /// <param name="lifetime">The host's, which is what quitting stops and what ends the loop.</param>
  /// <param name="stopHost">
  /// Stops the host and returns once it has, for a session that is ending. Called on
  /// the loop's thread, and blocks it until the host has stopped.
  /// </param>
  /// <param name="log">Where the shell reports what it could not do.</param>
  internal ShellContext(Uri page, string profileFolder, IHostApplicationLifetime lifetime, Action stopHost, ILogger log) {
    this.page = page;
    this.lifetime = lifetime;
    this.stopHost = stopHost;
    this.log = log;

    // Created on this thread, which is the one the loop will run on, so work posted
    // from the host's threads lands where the controls live.
    this.ui = new WindowsFormsSynchronizationContext();

    // Everything from here on is undone if any of it fails. A constructor that throws
    // hands nobody an object to dispose, and a tray icon that is never disposed stays
    // in the tray after the process has gone, until the pointer passes over it.
    try {
      ToolStripMenuItem open = new ToolStripMenuItem("Open Flux", image: null, (_, _) => this.Open());
      open.Font = new Font(open.Font, FontStyle.Bold);

      this.menu = new ContextMenuStrip();
      this.menu.Items.Add(open);
      this.menu.Items.Add(new ToolStripSeparator());
      this.menu.Items.Add(new ToolStripMenuItem("Quit", image: null, (_, _) => this.Quit()));

      // The small cut, which the icon file carries drawn for 16 px. Left to scale the
      // window's icon down, the tray would show a blur of four bars.
      this.trayIcon = FluxIcon.Load(SystemInformation.SmallIconSize);

      this.tray = new NotifyIcon {
        Icon = this.trayIcon,
        Text = FluxEdition.Title,
        ContextMenuStrip = this.menu,
        Visible = true,
      };

      this.tray.DoubleClick += (_, _) => this.Open();

      this.window = new MainWindow(page, profileFolder, new WindowPlacementStore(FluxPaths.Root, log), log);
      this.window.HiddenByClose += this.OnHiddenByClose;

      // Only the host is stopped here: the window is already closing, and the loop's
      // exit closes anything that is left. Waited for, because what closes a window for
      // good is usually Windows ending the session.
      this.window.ClosingForGood += stopHost;
      this.window.Unavailable += this.OnWindowUnavailable;
      this.window.Show();

      this.stopping = lifetime.ApplicationStopping.Register(() => this.ui.Post(_ => this.ExitThread(), null));
    } catch {
      this.Dispose();
      throw;
    }
  }

  /// <summary>Shows the window from any thread, for a second copy of Flux asking for it.</summary>
  internal void OpenFromAnyThread() {
    this.ui.Post(_ => this.Open(), null);
  }

  protected override void ExitThreadCore() {
    this.tray.Visible = false;
    this.window?.CloseForGood();

    base.ExitThreadCore();
  }

  protected override void Dispose(bool disposing) {
    if(disposing) {
      // A static event, so a handler left on it would outlive the shell.
      if(this.watchingSession) {
        SystemEvents.SessionEnding -= this.OnSessionEnding;
      }

      // Null-checked for a constructor that failed partway and disposed what it had.
      this.stopping.Dispose();
      this.tray?.Dispose();
      this.trayIcon?.Dispose();
      this.menu?.Dispose();
      this.window?.Dispose();
      this.ui.Dispose();
    }

    base.Dispose(disposing);
  }

  private void Open() {
    if(this.window is not { IsDisposed: false } shown) {
      MainWindow.OpenInBrowser(this.page.AbsoluteUri, this.log);
      return;
    }

    shown.Show();

    if(shown.WindowState == FormWindowState.Minimized) {
      shown.WindowState = shown.RestoredState;
    }

    shown.Activate();
  }

  private void Quit() {
    this.log.LogInformation("Quit from the tray.");
    this.window?.CloseForGood();
    this.lifetime.StopApplication();
  }

  /// <summary>
  /// Says once where Flux went. The first close is the one that reads as "it quit",
  /// and a lighting app that seems to have quit while the lights keep going is
  /// confusing in a way that is worth one balloon per run.
  /// </summary>
  private void OnHiddenByClose() {
    if(this.hiddenOnce) {
      return;
    }

    this.hiddenOnce = true;
    this.tray.ShowBalloonTip(
      3000,
      "Flux is still running",
      "The lights keep going. Double-click the tray icon to open Flux again, or right-click it to quit.",
      ToolTipIcon.None);
  }

  /// <summary>
  /// Falls back to the default browser, with the tray still in charge of quitting.
  /// The page is the same page either way, which is why this is a fallback and not an
  /// error.
  /// </summary>
  private void OnWindowUnavailable(Exception reason) {
    if(this.window is { } broken) {
      broken.CloseForGood();
      broken.Dispose();
      this.window = null;
    }

    // The window was what heard Windows ending the session, and it is gone. Without
    // this, signing out would end the process with the stream to the bridge cut
    // mid-frame, rather than stopped the way every other way out stops it.
    if(!this.watchingSession) {
      SystemEvents.SessionEnding += this.OnSessionEnding;
      this.watchingSession = true;
    }

    MainWindow.OpenInBrowser(this.page.AbsoluteUri, this.log);

    this.tray.ShowBalloonTip(
      5000,
      "Flux opened in your browser",
      reason is WebView2RuntimeNotFoundException
        ? "The Microsoft Edge WebView2 Runtime is not installed, so Flux runs from the tray and uses your browser for its window."
        : "Flux's window could not start, so it runs from the tray and uses your browser instead. The log says why.",
      ToolTipIcon.Warning);
  }

  private void OnSessionEnding(object? sender, SessionEndingEventArgs e) {
    this.log.LogInformation("Windows is ending the session ({Reason}), so Flux is stopping.", e.Reason);
    this.stopHost();
  }
}
