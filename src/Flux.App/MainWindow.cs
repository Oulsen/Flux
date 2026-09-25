using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// The desktop window: the same page the phone opens, in a WebView2, and nothing else.
///
/// No bridge between the window and the host, which is the one way this departs from
/// the shell it is modelled on. The page already talks to the host over the WebSocket,
/// and a second channel only the desktop had would be a second protocol with one
/// client. So the window cannot do anything the phone cannot, which is the point. What
/// the window does add is its own: a frame in the page's colours, the place it was
/// left, and F11 for putting the stage on a screen by itself.
///
/// Closing it hides it. The lights are the host's and run on without a window, and the
/// tray is where Flux is actually quit.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MainWindow: Form {
  private readonly Uri page;
  private readonly string profileFolder;
  private readonly WindowPlacementStore placements;
  private readonly ILogger log;
  private readonly WebView2 view;

  private bool leaving;
  private bool fullscreen;
  private bool fullscreenByKey;
  private bool fullscreenByPage;
  private FormWindowState stateBeforeFullscreen;
  private Rectangle boundsBeforeFullscreen;

  /// <param name="page">The address to load, which is the host's own.</param>
  /// <param name="profileFolder">Where WebView2 keeps its browser profile.</param>
  /// <param name="placements">Where the window was last left.</param>
  /// <param name="log">Where a page that could not be shown is reported.</param>
  internal MainWindow(Uri page, string profileFolder, WindowPlacementStore placements, ILogger log) {
    this.page = page;
    this.profileFolder = profileFolder;
    this.placements = placements;
    this.log = log;

    // A development build says so, so a copy started from the repository cannot be
    // mistaken on the taskbar for the one that is running the room.
#if DEBUG
    this.Text = "Flux (DEV)";
#else
    this.Text = "Flux";
#endif

    this.Icon = FluxIcon.Load(SystemInformation.IconSize);
    this.BackColor = WindowChrome.Surface;
    this.StartPosition = FormStartPosition.CenterScreen;

    // The page's colour behind the page, and not WebView2's default white: a resize
    // uncovers new area before the renderer paints it, and on a dark page that shows
    // as a white strobe along the edge being dragged.
    this.view = new WebView2 {
      Dock = DockStyle.Fill,
      DefaultBackgroundColor = WindowChrome.Surface,
    };

    // On the view and not the form: the page has the keyboard, so the form never sees
    // a key while the window is in use. The view passes on the keys the page does not
    // consume as its own, function keys among them.
    this.view.KeyDown += this.OnViewKeyDown;

    this.Controls.Add(this.view);
  }

  /// <summary>The window was closed from its own close button and is now hidden.</summary>
  internal event Action? HiddenByClose;

  /// <summary>
  /// Something other than the close button is closing the window - Windows ending the
  /// session, most often - so Flux has to stop with it.
  /// </summary>
  internal event Action? ClosingForGood;

  /// <summary>WebView2 could not be started, so this window has nothing to show.</summary>
  internal event Action<Exception>? Unavailable;

  /// <summary>Closes the window for real, rather than hiding it.</summary>
  internal void CloseForGood() {
    this.leaving = true;

    if(!this.IsDisposed) {
      this.Close();
    }
  }

  protected override void OnHandleCreated(EventArgs e) {
    base.OnHandleCreated(e);
    WindowChrome.Apply(this.Handle);
  }

  protected override async void OnLoad(EventArgs e) {
    base.OnLoad(e);

    this.MinimumSize = this.LogicalToDeviceUnits(new Size(480, 360));
    this.Place();

    await this.ShowPageAsync().ConfigureAwait(true);
  }

  protected override void OnFormClosing(FormClosingEventArgs e) {
    ArgumentNullException.ThrowIfNull(e);

    // Saved on every way out, the hide included: a hidden window is usually the last
    // state it is ever in before somebody quits from the tray.
    this.placements.Save(this.CurrentPlacement());

    if(!this.leaving && e.CloseReason == CloseReason.UserClosing) {
      e.Cancel = true;
      this.Hide();
      this.HiddenByClose?.Invoke();
    } else if(!this.leaving) {
      // The X button is UserClosing and never gets here. Anything else - Windows
      // ending the session, End task in Task Manager - is somebody outside Flux
      // deciding it should stop, and the log is where that is worth recording.
      this.log.LogInformation("The window was closed by {Reason}, so Flux is stopping.", e.CloseReason);
      this.leaving = true;
      this.ClosingForGood?.Invoke();
    }

    base.OnFormClosing(e);
  }

  /// <summary>
  /// Opens where the window was left, or centred at a default size when that place is
  /// gone: a monitor unplugged, a resolution changed, a file somebody edited.
  /// </summary>
  private void Place() {
    if(this.placements.Load() is { } placement
      && placement.IsUsableOn(Screen.AllScreens.Select(screen => screen.WorkingArea))) {
      this.StartPosition = FormStartPosition.Manual;

      // Twice, and the second is the one that counts. The bounds are physical pixels,
      // and the first move takes a window created on the primary screen onto the saved
      // one; when that screen scales differently, Windows rescales the window on
      // arrival, and a size that was already physical would come out scaled a second
      // time - and be saved that way. The second move happens on the right screen, where
      // there is nothing left to rescale.
      this.Bounds = placement.Bounds;
      this.Bounds = placement.Bounds;

      if(placement.Maximized) {
        this.WindowState = FormWindowState.Maximized;
      }

      return;
    }

    // Sized here, where the window's own DPI is known, so it opens at the same
    // physical size on a 150% display as on a 100% one.
    this.Size = this.LogicalToDeviceUnits(new Size(1280, 800));
    this.CenterToScreen();
  }

  /// <summary>
  /// Where the window is, as it should be restored: the bounds it would un-maximize or
  /// leave fullscreen to, never the bounds of the screen it is filling.
  /// </summary>
  private WindowPlacement CurrentPlacement() {
    Rectangle bounds;
    bool maximized;

    if(this.fullscreen) {
      bounds = this.boundsBeforeFullscreen;
      maximized = this.stateBeforeFullscreen == FormWindowState.Maximized;
    } else {
      bounds = this.WindowState == FormWindowState.Normal ? this.Bounds : this.RestoreBounds;
      maximized = this.WindowState == FormWindowState.Maximized;
    }

    return new WindowPlacement(bounds.X, bounds.Y, bounds.Width, bounds.Height, maximized);
  }

  /// <summary>
  /// Borderless over the whole screen, taskbar included, or back to how it was.
  ///
  /// Out of Maximized before going borderless, because a borderless window that is
  /// already maximized keeps the working area and leaves the taskbar showing; maximizing
  /// again from Normal is what covers it.
  /// </summary>
  private void SetFullscreen(bool on) {
    if(on == this.fullscreen) {
      return;
    }

    if(on) {
      this.stateBeforeFullscreen = this.WindowState;
      this.boundsBeforeFullscreen = this.WindowState == FormWindowState.Normal ? this.Bounds : this.RestoreBounds;

      this.WindowState = FormWindowState.Normal;
      this.FormBorderStyle = FormBorderStyle.None;
      this.WindowState = FormWindowState.Maximized;
    } else {
      this.FormBorderStyle = FormBorderStyle.Sizable;
      this.WindowState = this.stateBeforeFullscreen;

      if(this.stateBeforeFullscreen == FormWindowState.Normal) {
        this.Bounds = this.boundsBeforeFullscreen;
      }
    }

    this.fullscreen = on;
  }

  private async Task ShowPageAsync() {
    // Out of OnLoad before anything can fail. Without WebView2 the environment fails
    // before it ever yields, and the fallback would then close and dispose this window
    // from inside the Show() that is still opening it, in the shell's constructor.
    // Yielded, the fallback runs from the loop, once Show() has returned.
    await Task.Yield();

    try {
      // Wheel scrolling animated rather than stepped, asked for explicitly: an embedded
      // Chromium does not always inherit the browser's default, and a list that jumps
      // three lines per notch is hard to keep your place in.
      CoreWebView2EnvironmentOptions options = new CoreWebView2EnvironmentOptions {
        AdditionalBrowserArguments = "--enable-smooth-scrolling",
      };

      CoreWebView2Environment environment = await CoreWebView2Environment
        .CreateAsync(browserExecutableFolder: null, userDataFolder: this.profileFolder, options)
        .ConfigureAwait(true);

      await this.view.EnsureCoreWebView2Async(environment).ConfigureAwait(true);
    } catch(Exception ex) {
      // Every failure here is the same failure from where the user sits - no runtime,
      // a profile folder another process has locked, a runtime too old - and it has
      // one answer: the page still exists, so open it somewhere that can show it. An
      // exception escaping this async void would take the whole process down with it,
      // lights and all, which is a worse answer to a missing browser than any.
      this.log.LogWarning(ex, "The window could not start WebView2.");
      this.Unavailable?.Invoke(ex);
      return;
    }

    CoreWebView2 core = this.view.CoreWebView2;
    core.Settings.IsStatusBarEnabled = false;

    // Developer tools in a development build only. In the one running the room they are
    // a second way into the page that nobody at the desk needs.
#if !DEBUG
    core.Settings.AreDevToolsEnabled = false;
#endif

    // The window shows Flux's page and nothing else. A link, a redirect or a script
    // that would take it anywhere else is sent to the default browser instead: this
    // window has no address bar and no back button, so a page from somewhere else
    // could only be left by quitting. Frames too, which raise their own event: a frame
    // from somewhere else would be the same page from somewhere else, only smaller.
    core.NavigationStarting += (_, args) => {
      if(!IsSameOrigin(this.page, args.Uri)) {
        args.Cancel = true;
        OpenInBrowser(args.Uri, this.log);
      }
    };

    core.FrameNavigationStarting += (_, args) => {
      if(!IsSameOrigin(this.page, args.Uri)) {
        args.Cancel = true;
        this.log.LogWarning("Refused to load {Address} in a frame: not Flux's own page.", args.Uri);
      }
    };

    // A link that would open a new window goes to the default browser instead. A
    // second WebView2 window would be a browser with no address bar and no way back.
    core.NewWindowRequested += (_, args) => {
      args.Handled = true;
      OpenInBrowser(args.Uri, this.log);
    };

    // The page asking for fullscreen is honoured the same way F11 is, so a fullscreen
    // button in the page would work here as it does in a browser tab.
    core.ContainsFullScreenElementChanged += (_, _) => {
      this.fullscreenByPage = core.ContainsFullScreenElement;
      this.SetFullscreen(this.fullscreenByKey || this.fullscreenByPage);
    };

    this.view.Source = this.page;
  }

  /// <summary>
  /// Whether an address is on the page's own origin: the same scheme, host and port.
  /// </summary>
  internal static bool IsSameOrigin(Uri page, string address) {
    ArgumentNullException.ThrowIfNull(page);

    return Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
      && string.Equals(
        uri.GetLeftPart(UriPartial.Authority),
        page.GetLeftPart(UriPartial.Authority),
        StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// F11 in and F11 out. Not Escape as well, as a browser would: the page takes
  /// Escape for itself, so it never reaches the window, and catching it first would
  /// mean a script in the page talking to the window - the channel this window is
  /// built not to have. A fullscreen the page asks for still leaves on Escape, because
  /// there the browser engine handles it.
  ///
  /// The key and the page are kept apart, so one leaving fullscreen does not take the
  /// other's with it. While the page holds fullscreen F11 does nothing: the window
  /// cannot end the page's fullscreen without scripting it, and Escape already does.
  /// </summary>
  private void OnViewKeyDown(object? sender, KeyEventArgs e) {
    if(e.KeyCode == Keys.F11 && e.Modifiers == Keys.None) {
      e.Handled = true;

      if(this.fullscreenByPage) {
        return;
      }

      this.fullscreenByKey = !this.fullscreenByKey;
      this.SetFullscreen(this.fullscreenByKey);
    }
  }

  /// <summary>
  /// Opens a web address in the default browser. Only web addresses, because a page
  /// asking for anything else to be opened is not asking for anything Flux should run.
  /// </summary>
  internal static void OpenInBrowser(string address, ILogger log) {
    if(!Uri.TryCreate(address, UriKind.Absolute, out Uri? uri)
      || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) {
      log.LogWarning("Refused to open {Address} outside the window: not a web address.", address);
      return;
    }

    try {
      using Process? _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    } catch(Exception ex) when(ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
      log.LogWarning(ex, "Could not open {Address} in the default browser.", uri);
    }
  }
}
