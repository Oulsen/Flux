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
/// <param name="Color">
/// The colour the room is on, or null when no frame has gone out.
///
/// This is what the bridge was <em>sent</em>, read back after the fact, and never a
/// second opinion about what it should have been: <see cref="ILightSource.Render"/> is
/// documented as having exactly one caller, so a page wanting to know what the room
/// looks like has to be told rather than allowed to ask.
///
/// The shared colour before any one light's own band is taken into account, which is a
/// value the engine computes rather than an average of <paramref name="Channels"/>: the
/// mean of nine bulbs is a colour nothing in the room is showing.
/// </param>
/// <param name="Channels">
/// What each light was sent, and where it listens on the frequency axis. Empty until a
/// frame has gone out.
/// </param>
public readonly record struct HueStreamStatus(
  bool Running,
  double Fps,
  HueColor? Color = null,
  IReadOnlyList<LightChannel>? Channels = null);

/// <summary>One light, as a page is shown it.</summary>
/// <param name="At">Where it listens on the frequency axis, 0 for the kick and 1 for the top.</param>
/// <param name="Color">What it was sent on the most recent frame.</param>
public readonly record struct LightChannel(double At, HueColor Color);

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
public sealed class HueStreamService: IDisposable, IStreamActivity {
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

  /// <summary>
  /// How long a stop waits for the pump to notice before giving up on it.
  ///
  /// Bounded because the pump can be inside a send the transport will never finish, and
  /// a stop that waited for one would be as stuck as the thing it was ending. What
  /// happens after the wait is the interesting part - see <see cref="Session"/>.
  /// </summary>
  private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

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

  /// <summary>
  /// The attempt that owns the service's state, or null when nothing does.
  /// </summary>
  private Session? current;

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
  private bool running;
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

    // Subscribed from the constructor, which this project otherwise avoids, because
    // this service deliberately has no start hook: nothing may open a session merely
    // because Flux launched. The usual hazard - an event reaching a half-built object -
    // does not apply, since the handler reads only the fields assigned above it.
    this.bridge.Changed += this.OnBridgeChanged;
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
    lock(this.gate) {
      // The rate and the colour are read off the session rather than off the service,
      // so a pump that outlived the stop that abandoned it cannot contribute to what a
      // page is shown: it is measuring and painting into a session nothing reads any
      // more.
      return this.running && this.current is { } session
        ? new HueStreamStatus(Running: true, session.Fps, session.Rendered, session.Lights())
        : new HueStreamStatus(Running: false, Fps: 0, Color: null);
    }
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
    Session session;
    CancellationToken token;

    lock(this.gate) {
      if(this.running || this.starting || !this.options.Value.Enabled) {
        // A handshake in flight answers yes, even though nothing is streaming yet. The
        // second client pressing start is watching the first attempt succeed, and false
        // here reaches them as the refusal text - choose a reachable room, stop whatever
        // else is streaming - a moment before the lights come on.
        return this.running || this.starting;
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

      session = new Session(areaId);
      this.current = session;
      token = session.Cancellation.Token;

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
        if(token.IsCancellationRequested || !ReferenceEquals(this.current, session)) {
          stream.Dispose();
          return false;
        }

        this.running = true;
        session.Thread = new Thread(() => this.Run(stream, session)) {
          Name = "Flux Hue stream",
          IsBackground = true,

          // The same priority the beat scheduler runs at, and for the same reason: this
          // thread's only job is to be somewhere at a particular moment, and it does
          // almost nothing when it gets there.
          Priority = ThreadPriority.AboveNormal,
        };

        session.Thread.Start();
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
      Session? abandoned = null;

      lock(this.gate) {
        this.starting = false;

        // A session whose handshake never produced a pump owns nothing, and leaving it
        // in place would have the next stop waiting on a thread that was never created
        // and the next start refused by a token already cancelled.
        if(ReferenceEquals(this.current, session) && session.Thread is null) {
          this.current = null;
          abandoned = session;
        }
      }

      // Outside the lock, and only for a session no pump ever took: the thread owns the
      // token while it lives, and disposing one underneath a wedged pump turns a stream
      // that will not stop into a process that will not survive.
      abandoned?.Dispose();
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
    Session? leaving;

    lock(this.gate) {
      leaving = this.current;
      leaving?.Cancellation.Cancel();
    }

    leaving?.Thread?.Join(StopTimeout);

    bool changed = false;

    lock(this.gate) {
      // Cleared here and not only in the pump's own finally, which is the fix for a real
      // lock-up: a thread stuck somewhere uncancellable never reaches that finally, the
      // join times out, and the flag stays true forever - leaving a page showing a
      // stream that is not running and a stop button that cannot end it.
      //
      // A session still opening is left alone beyond the cancellation above, because
      // it has no pump to abandon and its own start is about to tidy it away.
      if(ReferenceEquals(this.current, leaving) && leaving is { Thread: not null }) {
        this.current = null;
        changed = this.running;
        this.running = false;
      }
    }

    if(changed) {
      this.Changed?.Invoke();
    }
  }

  /// <summary>
  /// Closes the session without making the caller wait for the pump to notice.
  ///
  /// <see cref="Stop"/> can spend <see cref="StopTimeout"/> inside its join, and the two
  /// callers that reach it from somewhere shared must not: a web receive loop blocked
  /// for two seconds delays every tap and tempo queued behind the stop, and the bridge's
  /// change notification is raised while it holds the lock every page reads through.
  /// </summary>
  public Task StopAsync() {
    return Task.Run(this.Stop);
  }

  public void Dispose() {
    this.bridge.Changed -= this.OnBridgeChanged;
    this.Stop();
  }

  /// <summary>
  /// Ends a session whose target has moved out from under it.
  ///
  /// Choosing another room, or forgetting the bridge, leaves the pump sending to the
  /// room it opened against: the page shows the new room selected and a stop button,
  /// while the lights in the old one keep running. A forgotten bridge is the worse half
  /// - its one entertainment slot stays held by a session nothing on screen admits to,
  /// so the next pairing cannot stream and nothing says why.
  /// </summary>
  private void OnBridgeChanged(HueConnection connection) {
    Guid target;

    lock(this.gate) {
      if(!this.running || this.current is not { Thread: not null } session) {
        return;
      }

      target = session.AreaId;
    }

    if(connection.Bridge is not null && connection.SelectedAreaId == target) {
      return;
    }

    this.logger.LogInformation("The light stream's target changed, so its session is being closed.");

    // Off this thread, because this runs inside the bridge's own notification: a stop
    // that joined for two seconds here would hold every page's picture of the bridge
    // still for as long as it took.
    _ = this.StopAsync();
  }

  private void Run(IHueStream stream, Session session) {
    CancellationToken token = session.Cancellation.Token;

    try {
      this.logger.LogInformation(
        "Streaming to {Channels} channel(s) at {Hz} Hz.",
        stream.ChannelCount,
        this.options.Value.StreamHz);

      this.Pump(stream, session, token);
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

      bool mine;

      lock(this.gate) {
        // Only the session that still owns the service may clear its state. A pump that
        // a stop gave up waiting for arrives here long after somebody started another
        // one, and clearing then reports a live stream as stopped: the page offers the
        // start button again, and the press behind it opens a second DTLS session
        // against a bridge that grants exactly one.
        mine = ReferenceEquals(this.current, session);

        if(mine) {
          this.current = null;
          this.running = false;
        }
      }

      session.Dispose();

      this.logger.LogInformation("The light stream has stopped.");

      if(mine) {
        this.Changed?.Invoke();
      }
    }
  }

  private void Pump(IHueStream stream, Session session, CancellationToken token) {
    // The room's geometry, resolved once. It cannot change under an open session: the
    // channels belong to the entertainment configuration this session opened against,
    // and choosing a different area closes the session rather than re-shaping it.
    LightPlacement[] lights = InChannelOrder(stream.Channels);
    HueColor[] colors = new HueColor[lights.Length];

    session.Place(lights);

    long period = Stopwatch.Frequency / Math.Max(1, this.options.Value.StreamHz);
    long next = Stopwatch.GetTimestamp() + period;
    long measuredAt = Stopwatch.GetTimestamp();
    long frames = 0;

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
      HueColor sent = (this.lights?.Render(lights, colors) ?? this.Color).Clamped();

      if(this.lights is null) {
        // No engine, so every channel holds the same colour - the beat lab's
        // arrangement, where a session exists to prove the bridge is reachable.
        colors.AsSpan().Fill(sent);
      }

      for(int index = 0; index < colors.Length; index++) {
        colors[index] = colors[index].Clamped();
      }

      stream.Set(colors);
      stream.Send();

      // Published after the send and not before it, so what a page draws is a colour
      // the bridge was given rather than one Flux was about to give it. The difference
      // shows up on the frame a send throws on, which is the frame where a preview
      // claiming the room changed colour would be exactly wrong.
      session.Publish(sent, colors);

      frames++;

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
        session.Fps = frames * (double)Stopwatch.Frequency / since;
        frames = 0;
        measuredAt = now;
      }
    }
  }

  bool IStreamActivity.IsStreaming {
    get {
      lock(this.gate) {
        return this.running;
      }
    }
  }

  /// <summary>
  /// The channels' frequencies, in the order the stream writes colours.
  ///
  /// <see cref="LightPlacements.Spread"/> answers in frequency order, because that is
  /// the order the mapping is about; the stream writes by channel index. Paired here,
  /// once per session, rather than either side bending to the other: the placement
  /// arithmetic should be readable as arithmetic, and the stream should not have to
  /// know what a frequency is.
  /// </summary>
  private static LightPlacement[] InChannelOrder(IReadOnlyList<HueChannel> channels) {
    IReadOnlyList<LightPlacement> spread = LightPlacements.Spread(channels);
    LightPlacement[] ordered = new LightPlacement[channels.Count];

    for(int index = 0; index < channels.Count; index++) {
      int id = channels[index].Id;

      // Looked up rather than assumed to be at the same index, which is the whole
      // reason this method exists. A linear scan over a room's worth of lights, once
      // when a session opens, is not worth a dictionary.
      ordered[index] = spread.FirstOrDefault(
        placement => placement.ChannelId == id,
        new LightPlacement(id, 0));
    }

    return ordered;
  }

  /// <summary>
  /// One attempt at streaming, and the identity that decides whether it still owns the
  /// service.
  ///
  /// It exists because <see cref="Stop"/> gives up on a pump it cannot join. Without an
  /// identity that abandoned pump eventually reaches its own finally and clears the
  /// running flag and the frame rate - which by then belong to a session somebody
  /// started afterwards. Everything a session can write is either kept here or written
  /// only after a check that it is still the current one.
  /// </summary>
  private sealed class Session: IDisposable {
    private readonly object paint = new();
    private double fps;
    private HueColor? rendered;

    /// <summary>Where each channel listens, fixed for the life of the session.</summary>
    private double[]? ats;

    /// <summary>What each channel was last sent, rewritten in place by the pump.</summary>
    private HueColor[]? colors;

    /// <summary>Whether a frame has been published, so an unlit buffer is not read as black.</summary>
    private bool painted;

    public Session(Guid areaId) {
      this.AreaId = areaId;
    }

    /// <summary>The area this session opened against, so a change of target is noticeable.</summary>
    public Guid AreaId { get; }

    public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

    /// <summary>The pump, once a handshake produced one. Null while the session is still opening.</summary>
    public Thread? Thread { get; set; }

    /// <summary>Frames a second this session measured, written by its pump and read by anyone.</summary>
    public double Fps {
      get => Volatile.Read(ref this.fps);
      set => Volatile.Write(ref this.fps, value);
    }

    /// <summary>
    /// The colour this session's last frame carried, written by its pump and read by
    /// anyone. Null until one has gone out.
    ///
    /// Locked where <see cref="Fps"/> is volatile, and the asymmetry is the point: a
    /// rate is one double and reads atomically, while a colour is three and does not.
    /// The failure a lock avoids here is the same one
    /// <see cref="HueStreamService.Color"/> documents from the other direction - a torn
    /// read handing a page the red of one frame beside the blue of the last, on one
    /// beat, once, never reproducibly. Its own lock and not the service's, which a
    /// fifty-times-a-second write must never queue behind a DTLS handshake.
    /// </summary>
    public HueColor? Rendered {
      get { lock(this.paint) { return this.rendered; } }
    }

    /// <summary>
    /// Records the room this session is lighting, before the first frame.
    /// </summary>
    public void Place(ReadOnlySpan<LightPlacement> lights) {
      lock(this.paint) {
        this.ats = new double[lights.Length];
        this.colors = new HueColor[lights.Length];

        for(int index = 0; index < lights.Length; index++) {
          this.ats[index] = lights[index].At;
        }
      }
    }

    /// <summary>
    /// Records what the bridge was just sent.
    ///
    /// Copied into a buffer this session owns rather than published as the pump's own
    /// array, and both halves of that matter. Handing the array over would let the next
    /// frame rewrite a page's picture from under it; allocating a fresh one each time
    /// would put fifty allocations a second on the one thread whose contract says not
    /// to. The reader pays instead, twenty times a second, which is the cheaper side.
    /// </summary>
    public void Publish(HueColor shared, ReadOnlySpan<HueColor> colors) {
      lock(this.paint) {
        this.rendered = shared;

        if(this.colors is { } buffer && colors.Length >= buffer.Length) {
          colors[..buffer.Length].CopyTo(buffer);
          this.painted = true;
        }
      }
    }

    /// <summary>
    /// What each light was sent, or empty before the first frame.
    ///
    /// A copy, made under the lock, because the pump keeps writing into the buffer
    /// behind it. Empty rather than null before a frame has gone out, so a caller has
    /// one shape to handle rather than two.
    /// </summary>
    public IReadOnlyList<LightChannel> Lights() {
      lock(this.paint) {
        if(!this.painted || this.colors is not { } buffer || this.ats is not { } at) {
          return [];
        }

        LightChannel[] lights = new LightChannel[buffer.Length];
        for(int index = 0; index < buffer.Length; index++) {
          lights[index] = new LightChannel(at[index], buffer[index]);
        }

        return lights;
      }
    }

    public void Dispose() {
      this.Cancellation.Dispose();
    }
  }
}
