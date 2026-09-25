using System.Runtime.InteropServices;

namespace Flux.App;

/// <summary>
/// One Flux per Windows session, and a second launch brings the first one forward.
///
/// Two copies cannot both work - the second cannot have the port, the audio device or
/// the bridge's one entertainment stream - so the only question is how the second one
/// fails. Without this it failed on the port, with a message about another program,
/// while the first copy sat hidden in the tray: exactly the situation somebody
/// double-clicking the shortcut again is in. Now it shows them the copy they already
/// have and leaves.
///
/// A named event rather than a pipe, because the only thing ever said is "show
/// yourself". An auto-reset event holds that until the first copy is listening, so a
/// second launch during the first one's startup is not lost.
///
/// The copy that runs is the one that owns the mutex, not the one that created it. A
/// copy on its way out keeps its handle open until it has stopped, so the mutex still
/// exists for a launch in that window: asking who created it would call that launch a
/// second copy, and it would leave without starting.
/// </summary>
internal sealed class SingleInstance: IDisposable {
  private const string SessionPrefix = @"Local\Flux";

  /// <summary><c>ASFW_ANY</c>: any process may take the foreground next.</summary>
  private const int AnyProcess = -1;

  private readonly Mutex running;
  private readonly EventWaitHandle signal;
  private RegisteredWaitHandle? listening;
  private bool held;

  internal SingleInstance()
    : this(SessionPrefix) {
  }

  /// <param name="prefix">What the named objects are called, which tests vary so they
  /// cannot meet a Flux that is running.</param>
  internal SingleInstance(string prefix) {
    this.running = new Mutex(initiallyOwned: false, $"{prefix}.Running");

    try {
      this.held = this.running.WaitOne(TimeSpan.Zero);
    } catch(AbandonedMutexException) {
      // A copy that ended without releasing it, which is a copy that crashed. The
      // mutex is this one's now, and there is nobody left to show.
      this.held = true;
    }

    this.IsFirst = this.held;
    this.signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, $"{prefix}.Show");
  }

  /// <summary>Whether this is the copy that gets to run.</summary>
  internal bool IsFirst { get; }

  /// <summary>
  /// Asks the running copy to show its window.
  ///
  /// The foreground is handed over first. Windows only lets the process that has it
  /// give it away, and that is this one, which the user just launched: without the
  /// hand-over the running copy's window comes back behind whatever was in front, and
  /// all that shows is a taskbar button flashing.
  /// </summary>
  internal void ShowFirst() {
    _ = AllowSetForegroundWindow(AnyProcess);
    this.signal.Set();
  }

  /// <summary>
  /// Calls <paramref name="show"/> whenever a later copy asks, on a thread-pool thread.
  /// </summary>
  internal void OnShowRequested(Action show) {
    ArgumentNullException.ThrowIfNull(show);

    this.listening = ThreadPool.RegisterWaitForSingleObject(
      this.signal,
      (_, _) => show(),
      state: null,
      Timeout.Infinite,
      executeOnlyOnce: false);
  }

  /// <summary>
  /// Stops answering later copies and lets the next launch be a real start, for a copy
  /// that has begun to stop.
  ///
  /// Called as soon as the shell's loop ends rather than when this is disposed, because
  /// stopping the host takes a while - the stream to the bridge is closed properly - and
  /// a launch in that window would otherwise ask a copy on its way out to show itself,
  /// then leave, and there would be no Flux at all. Released now, that launch starts,
  /// and if the old copy still has the port it says so. Waits for a request already
  /// being answered, so nothing calls into the shell once it is gone.
  ///
  /// Must be called on the thread that created this, which owns the mutex.
  /// </summary>
  internal void Leave() {
    if(this.listening is { } registered) {
      using ManualResetEvent unregistered = new ManualResetEvent(initialState: false);

      if(registered.Unregister(unregistered)) {
        unregistered.WaitOne();
      }

      this.listening = null;
    }

    if(this.held) {
      this.running.ReleaseMutex();
      this.held = false;
    }
  }

  /// <inheritdoc cref="Leave"/>
  public void Dispose() {
    this.Leave();
    this.signal.Dispose();
    this.running.Dispose();
  }

  [DllImport("user32.dll")]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static extern bool AllowSetForegroundWindow(int processId);
}
