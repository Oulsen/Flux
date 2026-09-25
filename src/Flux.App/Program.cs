using Flux.Audio;
using Flux.Beat;
using Flux.Hue;
using Flux.Midi;
using Flux.Web.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;

namespace Flux.App;

/// <summary>
/// The host process, from Phase 1 to release, and since Phase 5 the desktop shell
/// around it: later phases change what this process hosts, never which executable
/// runs.
///
/// The host is started and not run. The lights, the audio and the web server all live
/// in it and none of them need a window, so the process waits on the shell's message
/// loop instead, and the loop ends when the host is asked to stop. That order is what
/// lets the window close to the tray without taking the lights with it.
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class Program {
  [STAThread]
  private static int Main(string[] args) {
    LaunchOptions launch = LaunchOptions.Parse(args);

    using SingleInstance instance = new SingleInstance();
    if(!instance.IsFirst) {
      instance.ShowFirst();
      return 0;
    }

    if(launch.Console) {
      ConsoleWindow.Open();
    }

    using FileLoggerProvider fileLog = new FileLoggerProvider(FluxPaths.Logs, DateTime.Now);
    ILogger startup = fileLog.CreateLogger("Flux.App");

    // Anything that escapes a thread-pool thread ends the process, and a window
    // application has nowhere to print the reason. The log is the one record left, and
    // it is flushed here: logging only queues the line, and the process ends as soon as
    // this handler returns, long before the writer would have got to it.
    AppDomain.CurrentDomain.UnhandledException += (_, e) => {
      startup.LogCritical(e.ExceptionObject as Exception, "Flux stopped on an unhandled exception.");
      fileLog.Dispose();
    };

    // The same for the shell's own thread - a tray click, the window closing - where
    // WinForms would otherwise show its stock dialog with a Continue button and log
    // nothing. Logged and carried on from, as that dialog's Continue would: the lights
    // run on the host's threads, and a tray menu that threw once is no reason to cut them.
    Application.ThreadException += (_, e) =>
      startup.LogError(e.Exception, "The window or the tray failed on something it was doing.");

    ApplicationConfiguration.Initialize();

    WebApplication? app = null;

    try {
      app = BuildHost(launch, fileLog);

      // On the thread pool rather than this thread. Nothing has installed a UI
      // synchronization context yet, but blocking the one thread a later context
      // would post back to is how a startup deadlocks, and this costs nothing.
      Task.Run(app.StartFluxAsync).GetAwaiter().GetResult();
    } catch(Exception ex) {
      startup.LogCritical(ex, "Flux could not start.");

      // Disposed before the message is shown, not after. Whatever did start - the audio
      // capture, the Link listener - would otherwise keep running behind a box nobody
      // has read yet, and a relaunch in the meantime would find this copy still holding
      // the session and quietly leave.
      if(app is not null) {
        Task.Run(() => app.DisposeAsync().AsTask()).GetAwaiter().GetResult();
      }

      instance.Leave();
      ReportStartupFailure(ex, fileLog.Path, launch.Console);

      return 1;
    }

    IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    Uri page = app.Services.GetRequiredService<IOptions<WebOptions>>().Value.LocalPageUri();
    ILogger shellLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Flux.App.Shell");

    using(ShellContext shell = new ShellContext(page, FluxPaths.WebView2, lifetime, shellLog)) {
      instance.OnShowRequested(shell.OpenFromAnyThread);
      Application.Run(shell);

      // Before the shell is disposed, so no request from a later copy can reach it, and
      // before the host stops, so a launch while it does is a real start.
      instance.Leave();
    }

    // Off this thread for the reason the start was: the shell's synchronization
    // context is still installed here, and a continuation posted to a loop that has
    // ended would never run.
    Task.Run(async () => {
      await app.StopAsync().ConfigureAwait(false);
      await app.DisposeAsync().ConfigureAwait(false);
    }).GetAwaiter().GetResult();

    startup.LogInformation("Flux stopped.");

    return 0;
  }

  private static WebApplication BuildHost(LaunchOptions launch, FileLoggerProvider fileLog) {
    // The content root is pinned to the executable's own folder rather than left to
    // the working directory. An installed app is launched from a shortcut or the
    // tray, where the working directory is whatever Windows felt like - and a Flux
    // that silently ignores its own appsettings.json depending on how it was started
    // is a bug that only shows up after packaging. It decides where wwwroot is
    // found, too, which turns the same trap into a blank page.
    WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions {
      Args = launch.HostArguments,
      ContentRootPath = AppContext.BaseDirectory,
    });

    builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);

    // The file always, the console only when there is one. The provider is owned by
    // Main rather than by the container, so the last lines of a shutdown are flushed
    // after everything that could log them has stopped.
    builder.Logging.ClearProviders();
    builder.Logging.AddProvider(fileLog);
    builder.Logging.AddDebug();

    if(launch.Console) {
      builder.Logging.AddConsole();

      Console.WriteLine($"Flux {FluxVersion.Current} - music in, light out.");
      Console.WriteLine("Ctrl+C here, or Quit in the tray, stops it.");
      Console.WriteLine();
    }

    // Registration order is start order, and the timer resolution has to be up
    // before the beat scheduler starts sleeping against it. The grid and scheduler
    // come next, then the sources that feed them.
    builder.Services.AddHostedService<TimerResolutionService>();
    builder.Services.AddBeatCore(builder.Configuration);
    builder.Services.AddAbletonLink(builder.Configuration);

    // Capture and analysis run whichever source is driving the beat, because the
    // spectrum they feed is the instrument every later phase is checked against. A
    // spectrum that went dark because the DJ selected Link would switch itself off
    // exactly when it was being used to check the thing under test. Detection is
    // registered on top of them and publishes unconditionally: an unselected source
    // stays warm, so choosing audio takes effect on the next frame rather than after
    // however long the detector needs to find the tempo again.
    builder.Services.AddAudioCapture(builder.Configuration);
    builder.Services.AddAudioAnalysis();
    builder.Services.AddAudioSource();

    // The controller's cue button, read-only, as a downbeat marker. Not a source: it
    // registers nothing with the manager and has no opinion about tempo, it only says
    // where a bar starts - so it goes after the sources rather than among them.
    builder.Services.AddFlxObserver(builder.Configuration);

    // The lights. Settings and the credential store only, for now: nothing here starts
    // a bridge conversation, and registering it this early is what makes a bad Hue
    // setting fail at startup with a message naming the key, rather than inside a
    // background loop where it would look like a bridge nobody switched on.
    builder.Services.AddHue(builder.Configuration);

    // The per-beat console line was Phase 1's stand-in for the visualizer; the web
    // app has taken that job, so it is opt-in diagnostics now - three lines a
    // second, for nobody, on a default night.
    bool beatLog = builder.Configuration.GetValue("Console:BeatLog", defaultValue: false);

    // The same arrangement for the capture level, and for a sharper reason: a device
    // list can only show names, and on this rig the endpoint carrying the master mix
    // is called "Microfoon". Only a level that falls when the channel fader does can
    // tell the right device from the thirteen silent ones beside it.
    bool audioMeter = builder.Configuration.GetValue("Console:AudioMeter", defaultValue: false);

    if(launch.Console) {
      if(beatLog) {
        builder.Services.AddHostedService<ConsoleBeatMonitor>();
      }

      if(audioMeter) {
        builder.Services.AddHostedService<ConsoleAudioMonitor>();
      }
    } else if(beatLog || audioMeter) {
      // Said rather than silently skipped: a setting that was switched on and does
      // nothing reads as a broken setting.
      fileLog.CreateLogger("Flux.App").LogWarning(
        "Console:BeatLog and Console:AudioMeter only print with a console. Start Flux with {Flag} to see them.",
        LaunchOptions.ConsoleFlag);
    }

    builder.AddFluxWeb();

    WebApplication app = builder.Build();
    app.MapFluxWeb();

    return app;
  }

  /// <summary>
  /// Tells somebody why Flux did not start, somewhere they will see it.
  ///
  /// A message box, because a window application that fails before its window exists
  /// otherwise just does not appear. An explained failure is shown as it is; anything
  /// else is shown by its message, with the log that holds the rest.
  /// </summary>
  private static void ReportStartupFailure(Exception ex, string logPath, bool console) {
    string reason = ex is FluxStartupException or OptionsValidationException
      ? ex.Message
      : $"Flux could not start: {ex.Message}";

    string text = $"{reason}{Environment.NewLine}{Environment.NewLine}The log is in {logPath}.";

    if(console) {
      Console.Error.WriteLine();
      Console.Error.WriteLine(text);
    }

    MessageBox.Show(text, "Flux", MessageBoxButtons.OK, MessageBoxIcon.Error);
  }
}
