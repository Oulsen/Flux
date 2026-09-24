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
/// </summary>
internal sealed class SingleInstance: IDisposable {
  private const string MutexName = @"Local\Flux.Running";
  private const string SignalName = @"Local\Flux.Show";

  private readonly Mutex running;
  private readonly EventWaitHandle signal;
  private RegisteredWaitHandle? listening;

  internal SingleInstance() {
    this.running = new Mutex(initiallyOwned: true, MutexName, out bool created);
    this.IsFirst = created;
    this.signal = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, SignalName);
  }

  /// <summary>Whether this is the copy that gets to run.</summary>
  internal bool IsFirst { get; }

  /// <summary>Asks the running copy to show its window.</summary>
  internal void ShowFirst() {
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

  /// <summary>Must be called on the thread that created this, which owns the mutex.</summary>
  public void Dispose() {
    this.listening?.Unregister(null);
    this.signal.Dispose();

    if(this.IsFirst) {
      this.running.ReleaseMutex();
    }

    this.running.Dispose();
  }
}
