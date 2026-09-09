using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Flux.Hue;

/// <summary>What the stream is doing, as one reading.</summary>
/// <param name="Running">Whether a session is open and the loop is sending.</param>
/// <param name="Fps">
/// Frames a second actually reaching the bridge, measured over the last second.
/// Zero until a second has been measured, which is why <paramref name="Running"/> is a
/// separate field: a rate of zero on a stream that just started is honest, and a page
/// that inferred "not streaming" from it would be wrong for one second every time.
/// </param>
public readonly record struct HueStreamStatus(bool Running, double Fps);

/// <summary>
/// Sends a frame to the bridge fifty times a second, and says how often it managed.
///
/// The loop is a dedicated thread rather than a timer, and it aims at absolute targets
/// rather than sleeping "one period" each pass, because both of those lessons were
/// paid for by <c>BeatScheduler</c> in Phase 1: sleeping a duration accumulates every
/// scheduling error, and a loop that re-aims at the next deadline the instant it
/// misses one produces nothing at all with no error anywhere.
///
/// The frame rate is <em>measured</em> and not the configured rate echoed back, which
/// is the whole of acceptance criterion 5. A stream quietly achieving half its rate
/// looks, from outside, exactly like lights that are less smooth than somebody
/// expected, on a night when everything else is also a suspect.
/// </summary>
public sealed class HueStreamService: IDisposable {
  /// <summary>
  /// The longest single sleep, so a stop is noticed promptly rather than at the end of
  /// whatever the loop was waiting out.
  /// </summary>
  private const double MaxSleepMillis = 20.0;

  /// <summary>
  /// How far behind schedule the loop may fall before it stops trying to catch up.
  ///
  /// Below this, a late frame is sent late and the next deadline still advances by one
  /// period, so a single hiccup does not shift every frame after it. Past it, catching
  /// up would mean firing a burst of frames the bridge would drop anyway, so the
  /// schedule is re-based on now and the frames in between are given up honestly.
  /// </summary>
  private const int MaxFramesBehind = 5;

  /// <summary>
  /// How long to wait for the DTLS handshake before giving up on it.
  ///
  /// Needed because the handshake is <b>not cancellable</b>: the library's connect
  /// takes no token, so nothing above it can interrupt one that will never complete.
  /// A bridge whose one session is already taken does not refuse - it simply never
  /// answers, and without a bound here that wait is permanent.
  /// </summary>
  private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(10);

  private readonly IHueStreamFactory factory;
  private readonly HueBridgeService bridge;
  private readonly ICredentialStore credentials;
  private readonly IOptions<HueOptions> options;
  private readonly ILogger<HueStreamService> logger;

  /// <summary>
  /// What decides the colour, asked once per frame.
  ///
  /// Optional, and null leaves the stream sending <see cref="Color"/>. That is the
  /// beat lab's arrangement rather than a missing feature: a session opened only to
  /// prove the bridge is reachable wants a colour that holds still while somebody
  /// looks at it.
  /// </summary>
  private readonly ILightSource? lights;
  private readonly object gate = new();
  private readonly object colorGate = new();

  private CancellationTokenSource? stopping;
  private Thread? thread;

  /// <summary>
  /// What an open stream sends until something has an opinion about colour.
  ///
  /// A dim white rather than <see cref="HueColor.Black"/>, and the difference is not
  /// cosmetic: black is indistinguishable from a stream that never opened, so the one
  /// thing a person can check by eye - are the lights under Flux's control - would be
  /// unanswerable exactly when it is being asked. Dim rather than full because this is
  /// a held colour and somebody is standing in the room. The light engine replaces it.
  /// </summary>
  private static readonly HueColor Holding = new HueColor(0.35, 0.35, 0.35);

  private HueColor color = Holding;
  private long frames;
  private double fps;
  private volatile bool running;
  private bool starting;

  public HueStreamService(
    IHueStreamFactory factory,
    HueBridgeService bridge,
    ICredentialStore credentials,
    IOptions<HueOptions> options,
    ILogger<HueStreamService> logger,
    ILightSource? lights = null) {
    ArgumentNullException.ThrowIfNull(options);

    this.factory = factory ?? throw new ArgumentNullException(nameof(factory));
    this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    this.credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
    this.options = options;
    this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    this.lights = lights;
  }

  /// <summary>Raised when the stream starts or stops, including when it stops by itself.</summary>
  public event Action? Changed;

  /// <summary>
  /// The colour every channel is sent when no <see cref="ILightSource"/> is behind the
  /// stream.
  ///
  /// This was going to be the light engine's seam and is not, because a colour that
  /// only changes when something writes it cannot express a brightness pulse that
  /// fades or a hue that slides - both of those are functions of the moment the frame
  /// is sent, so the loop asks rather than reads. What survives here is the holding
  /// colour and the way tests pin one.
  /// </summary>
  public HueColor Color {
    // Locked rather than volatile, because a colour is three doubles and nothing
    // reads three doubles atomically. The failure that avoids is not theoretical: a
    // torn read between a write and a frame gives one frame the red of the new colour
    // and the blue of the old one, on one beat, once, never reproducibly.
    //
    // Its own lock and not the one guarding start and stop: this is taken fifty times
    // a second on the thread whose entire job is punctuality, and it must never wait
    // behind a DTLS handshake.
    get { lock(this.colorGate) { return this.color; } }
    set { lock(this.colorGate) { this.color = value.Clamped(); } }
  }

  /// <summary>The current reading, complete on every read.</summary>
  public HueStreamStatus Status() {
    return new HueStreamStatus(this.running, this.running ? Volatile.Read(ref this.fps) : 0);
  }

  /// <summary>
  /// Opens a session and starts sending.
  ///
  /// Awaits the handshake rather than starting the loop hopefully, which is the
  /// difference between "Flux is streaming" meaning something and meaning "somebody
  /// pressed a button". Everything a caller needs to know is in the answer: false is a
  /// refusal it can put on screen, and true means light is already coming out.
  /// </summary>
  /// <returns>
  /// False when there is nothing to stream to - no bridge, no area chosen, lights
  /// switched off - or when the bridge would not grant the session, which almost
  /// always means another application is holding its only one.
  /// </returns>
  public async Task<bool> StartAsync(CancellationToken cancellationToken) {
    HueBridge located;
    HueCredentials keys;
    Guid areaId;
    CancellationToken token;

    lock(this.gate) {
      if(this.running || this.starting || !this.options.Value.Enabled) {
        return this.running;
      }

      HueConnection connection = this.bridge.Connection;
      if(connection.Bridge is not { } bridgeNow
        || connection.SelectedAreaId is not { } chosen
        || this.credentials.Load() is not { } keysNow) {
        return false;
      }

      located = bridgeNow;
      keys = keysNow;
      areaId = chosen;

      this.stopping?.Dispose();
      this.stopping = new CancellationTokenSource();
      token = this.stopping.Token;

      // Not `running`. That flag is what the page reads as "Flux is streaming", and a
      // handshake in progress is not streaming - saying otherwise is how a stream that
      // never opened ends up displayed as one sending zero frames a second.
      this.starting = true;
    }

    try {
      using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, token);

      if(!await this.IsAreaFreeAsync(areaId, linked.Token).ConfigureAwait(false)) {
        return false;
      }

      IHueStream stream = await this.OpenAsync(located, keys, areaId, linked.Token).ConfigureAwait(false);

      lock(this.gate) {
        if(token.IsCancellationRequested) {
          stream.Dispose();
          return false;
        }

        this.running = true;
        this.thread = new Thread(() => this.Run(stream, token)) {
          Name = "Flux Hue stream",
          IsBackground = true,

          // The same priority the beat scheduler runs at, and for the same reason: this
          // thread's only job is to be somewhere at a particular moment, and it does
          // almost nothing when it gets there.
          Priority = ThreadPriority.AboveNormal,
        };

        this.thread.Start();
      }

      // Whatever was said about the last attempt no longer applies.
      this.bridge.Note(null);
      this.Changed?.Invoke();
      return true;
    } catch(OperationCanceledException) {
      return false;
    } catch(HueBridgeException e) {
      this.logger.LogWarning(e, "The light stream could not be opened.");
      return false;
    } finally {
      lock(this.gate) {
        this.starting = false;
      }
    }
  }

  /// <summary>
  /// The busy pre-flight, asked again at the last possible moment.
  ///
  /// The area list a page is looking at can be a whole set old, and this is the one
  /// fact on it with a lifetime of seconds. Re-reading costs one HTTP request; not
  /// re-reading costs a DTLS handshake that never answers, which is exactly the
  /// failure the pre-flight exists to turn into a sentence.
  /// </summary>
  private async Task<bool> IsAreaFreeAsync(Guid areaId, CancellationToken cancellationToken) {
    // The cheap refresh, not the full one. A pre-flight that re-discovered the bridge
    // would cost a multi-second mDNS sweep and would blank the bridge screen while it
    // ran, which is a large price for reading one boolean off a known address.
    await this.bridge.TryRefreshAreasAsync(cancellationToken).ConfigureAwait(false);

    if(this.bridge.Connection.Areas.FirstOrDefault(area => area.Id == areaId) is not { } area) {
      const string gone = "That room is no longer on the bridge. Choose another one.";

      this.logger.LogWarning("{Message}", gone);
      this.bridge.Note(gone);
      return false;
    }

    if(area.Streaming) {
      // Said on screen and not only to the console. A refusal nobody can see is barely
      // an improvement on the hang it replaced: the user presses a button, nothing
      // happens, and the reason is in a log they are not reading.
      string busy = $"'{area.Name.Trim()}' is already being streamed to by another application - Hue Sync, most likely. Stop that first, then refresh the rooms.";

      this.logger.LogWarning("{Message}", busy);
      this.bridge.Note(busy);
      return false;
    }

    return true;
  }

  /// <summary>
  /// Opens the session, and refuses to wait forever for one that is not coming.
  /// </summary>
  private async Task<IHueStream> OpenAsync(HueBridge located, HueCredentials keys, Guid areaId, CancellationToken cancellationToken) {
    Task<IHueStream> opening = this.factory.OpenAsync(located, keys, areaId, cancellationToken);

    try {
      return await opening.WaitAsync(OpenTimeout, cancellationToken).ConfigureAwait(false);
    } catch(TimeoutException e) {
      // Abandoned rather than cancelled, because the handshake underneath cannot be
      // cancelled. The continuation is not tidiness: a session that completes after
      // this point belongs to nobody, and an undisposed one holds the bridge's only
      // slot until Flux exits.
      _ = opening.ContinueWith(
        finished => {
          if(finished.IsCompletedSuccessfully) {
            finished.Result.Dispose();
          } else {
            _ = finished.Exception;
          }
        },
        CancellationToken.None,
        TaskContinuationOptions.ExecuteSynchronously,
        TaskScheduler.Default);

      throw new HueBridgeException(
        $"The bridge did not open a light stream within {OpenTimeout.TotalSeconds:F0} seconds. The usual cause is another application holding its one entertainment session.",
        e);
    }
  }

  /// <summary>
  /// Closes the session.
  ///
  /// Never fails and never rejects: stopping a stream that is not running has got
  /// exactly what it asked for.
  /// </summary>
  public void Stop() {
    Thread? leaving;

    lock(this.gate) {
      this.stopping?.Cancel();
      leaving = this.thread;
      this.thread = null;
    }

    leaving?.Join(TimeSpan.FromSeconds(2));

    // Cleared here and not only in the loop's own finally, which is the fix for a real
    // lock-up: a thread stuck somewhere uncancellable never reaches that finally, the
    // join times out, and the flag stays true forever - leaving a page showing a
    // stream that is not running and a stop button that cannot end it.
    this.running = false;
    Volatile.Write(ref this.fps, 0);
    this.Changed?.Invoke();
  }

  public void Dispose() {
    this.Stop();
    this.stopping?.Dispose();
  }

  private void Run(IHueStream stream, CancellationToken token) {
    try {
      this.logger.LogInformation(
        "Streaming to {Channels} channel(s) at {Hz} Hz.",
        stream.ChannelCount,
        this.options.Value.StreamHz);

      this.Pump(stream, token);
    } catch(OperationCanceledException) {
      // An ordinary stop, or the host going down.
    } catch(Exception e) {
      // A catch-all, deliberately, and the narrowest place in Flux where one is right.
      //
      // This runs on a dedicated thread, so anything that escapes it is an unhandled
      // exception and the process is gone - taking the beat grid, the web server and
      // every other page down with a light stream. Phase 1 recorded the same lesson for
      // the Link listener: a peripheral that cannot be reached must never be able to
      // stop Flux. It cost a crash here before it was applied.
      this.logger.LogError(e, "The light stream ended with an error.");
    } finally {
      try {
        stream.Dispose();
      } catch(Exception e) {
        // Closing a session that has already gone is not worth the process.
        this.logger.LogWarning(e, "The light stream did not close cleanly.");
      }

      this.running = false;
      Volatile.Write(ref this.fps, 0);

      this.logger.LogInformation("The light stream has stopped.");
      this.Changed?.Invoke();
    }
  }

  private void Pump(IHueStream stream, CancellationToken token) {
    long period = Stopwatch.Frequency / Math.Max(1, this.options.Value.StreamHz);
    long next = Stopwatch.GetTimestamp() + period;
    long measuredAt = Stopwatch.GetTimestamp();

    this.frames = 0;

    while(!token.IsCancellationRequested) {
      long now = Stopwatch.GetTimestamp();
      double remainingMillis = (next - now) * 1000.0 / Stopwatch.Frequency;

      if(remainingMillis > 0) {
        // Sleeping rather than spinning, which Phase 0 measured: a hybrid burned a
        // whole core to buy an accuracy nothing downstream can use. The cap is so a
        // cancellation is noticed inside a frame rather than at the end of one.
        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(remainingMillis, MaxSleepMillis)));
        continue;
      }

      // Clamped on the way out rather than trusted, which is what the engine's
      // envelope makes worth doing: an overshoot reaching the bridge as a wrapped or
      // negative channel is one wrong light for one frame, and never reproducible.
      stream.SetAll((this.lights?.Render() ?? this.Color).Clamped());
      stream.Send();

      this.frames++;

      // Advanced by exactly one period rather than re-based on now. Re-basing is the
      // trap: every frame would then start its wait from wherever the last one
      // finished, so the rate drifts downward by the cost of the send and never
      // recovers.
      next += period;

      if(next < now - (period * MaxFramesBehind)) {
        this.logger.LogWarning("The light stream fell more than {Frames} frames behind and re-based its schedule.", MaxFramesBehind);
        next = Stopwatch.GetTimestamp() + period;
      }

      long since = now - measuredAt;
      if(since >= Stopwatch.Frequency) {
        Volatile.Write(ref this.fps, this.frames * (double)Stopwatch.Frequency / since);
        this.frames = 0;
        measuredAt = now;
      }
    }
  }
}
