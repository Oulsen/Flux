using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Hue;

/// <summary>
/// Turns beats into one colour, evaluated fresh on every frame.
///
/// Two layers at two rates, which is the whole design. A hue is chosen every few bars
/// and slides toward its new value; brightness is knocked to full on every beat and
/// fades. Separating them is what stops the lights either sitting still for eight
/// seconds between colour changes or strobing through the palette four times a bar -
/// both of which are what happens when one setting has to do both jobs.
///
/// The tuned numbers come from the SignalRGB scene this phase reimplements and are
/// copied rather than re-derived, on the standing rule for that material: structure
/// gets rewritten, values that somebody arrived at by listening get carried across.
/// Where this file departs from the scene it says so and says why.
///
/// <b>The latency offset is applied here and nowhere else.</b> That is not tidiness.
/// The offset shifts when light happens relative to the beat, and there are two things
/// it has to shift - the pulse and the colour change - so applying it at the one point
/// where both are decided is the only arrangement in which they cannot drift apart.
/// Every other layer treats it as a number to store, validate and display.
/// </summary>
internal sealed class LightEngine: ILightSource, IHostedService {
  private const double MicrosPerMilli = 1000.0;
  private const double MicrosPerSecond = 1_000_000.0;

  /// <summary>
  /// How far a new hue must sit from the one it replaces, on a wheel of 1.
  ///
  /// The scene's value, and its purpose is to stop a "change" that nobody can see.
  /// It is deliberately not always satisfiable: 0.12 of the wheel is 43 degrees, and
  /// <c>Ocean</c> spans fifty in total, so only its two extremes - 170 and 220 - are
  /// that far apart. From the other six of its hues no candidate can qualify and every
  /// attempt fails. That is the intended behaviour of a tight palette rather than a bug
  /// in the threshold - see <see cref="PickHue"/> for what happens when the attempts
  /// run out.
  /// </summary>
  private const double MinHueSeparation = 0.12;

  /// <summary>How many hues to try before accepting the best of a bad set. The scene's number.</summary>
  private const int MaxHueAttempts = 50;

  /// <summary>
  /// How fast the displayed hue closes on its target, as a fraction of the remaining
  /// distance per second.
  ///
  /// The scene's transition speed defaulted to 50 on a 0-100 dial and its rate was
  /// <c>(speed / 100) * 10</c> per second, so 5. Fixed here rather than exposed:
  /// three settings is what somebody standing in a room can hold in their head, and
  /// this is the one of the four that changes the look least.
  /// </summary>
  private const double TransitionRatePerSecond = 5.0;

  /// <summary>
  /// Where a pulse is considered spent, as a fraction of its height.
  ///
  /// A tenth, which is what turns an exponential decay into a duration somebody can
  /// set. An exponential never reaches its floor, so "how long is the pulse" has no
  /// answer until a threshold is named, and naming one here means the number on the
  /// slider is the number of milliseconds the flash visibly lasts.
  /// </summary>
  private const double PulseSpentAt = 0.1;

  /// <summary>
  /// How many un-rendered beats to hold before dropping the oldest.
  ///
  /// Beats keep arriving while the stream is stopped and nothing is draining them, so
  /// this is bounded on principle rather than in expectation. Dropping is safe here in
  /// a way it would not be with a running count: the colour-change interval is decided
  /// from <see cref="BeatEvent.Index"/> and <see cref="BeatEvent.PositionInBar"/>,
  /// which are absolute, so a lost beat cannot shift the phase of the changes.
  /// </summary>
  private const int MaxPendingBeats = 8;

  private readonly BeatScheduler scheduler;
  private readonly HueBridgeService bridge;
  private readonly IMonotonicClock clock;
  private readonly ILogger<LightEngine> logger;
  private readonly Random random;
  private readonly object gate = new();
  private readonly Queue<BeatEvent> pending = new(MaxPendingBeats);

  /// <summary>
  /// Beats to the bar, taken from the grid's own setting rather than assumed.
  ///
  /// The scene's interval is counted in bars and four is still the default, so nothing
  /// about the tuned behaviour moves. What this closes is a divisor that was written as
  /// a constant while <see cref="BeatOptions.Quantum"/> was bindable: at a quantum of
  /// eight the downbeats arrive eight indices apart, dividing by four steps the bar
  /// count by two, and "every four bars" silently becomes every eight - or never,
  /// depending on where the grid's anchor happened to fall.
  ///
  /// Read once. The quantum is bound at startup and validated there, and this is
  /// consulted on the frame path.
  /// </summary>
  private readonly int beatsPerBar;

  private double targetHue;
  private double displayHue;
  private double? pulseStartedMicros;
  private double? renderedAtMicros;

  public LightEngine(
    BeatScheduler scheduler,
    HueBridgeService bridge,
    IMonotonicClock clock,
    IOptions<BeatOptions> beat,
    ILogger<LightEngine> logger)
    : this(scheduler, bridge, clock, beat, logger, new Random()) {
  }

  /// <summary>
  /// The seeded form, for tests.
  ///
  /// The engine's whole output is a colour it chose at random, so a test that cannot
  /// fix the seed can only assert that something changed. With one, the exact hue a
  /// given beat produces is a fact.
  /// </summary>
  internal LightEngine(
    BeatScheduler scheduler,
    HueBridgeService bridge,
    IMonotonicClock clock,
    IOptions<BeatOptions> beat,
    ILogger<LightEngine> logger,
    Random random) {
    ArgumentNullException.ThrowIfNull(beat);

    this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    this.random = random ?? throw new ArgumentNullException(nameof(random));
    this.beatsPerBar = Math.Max(1, beat.Value.Quantum);

    this.targetHue = this.random.Next(360) / 360.0;
    this.displayHue = this.targetHue;
  }

  /// <summary>
  /// Starts listening for beats.
  ///
  /// Subscribed here rather than in the constructor, which is the pattern
  /// <c>BeatBroadcaster</c> set and which is worth keeping for two reasons: handing
  /// <c>this</c> to an event source before construction has finished is a hazard in
  /// general, and a beat handler attached by a constructor is a beat handler no test
  /// can get in front of.
  ///
  /// Subscribing happens even in a build with lights switched off, where nothing will
  /// ever render. The cost is one bounded queue taking beats and dropping them a
  /// couple of times a second, which is cheaper than a second registration path.
  /// </summary>
  public Task StartAsync(CancellationToken cancellationToken) {
    this.scheduler.Beat += this.OnBeat;

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    this.scheduler.Beat -= this.OnBeat;

    return Task.CompletedTask;
  }

  /// <summary>
  /// The colour for this instant.
  ///
  /// Called from the stream loop's thread and from nowhere else, which is why the
  /// render state below is unsynchronised: only the queue of arriving beats is shared,
  /// and only that is locked. A second concurrent renderer would corrupt the
  /// transition, and there is deliberately no lock making that safe - the fix for two
  /// renderers is one renderer.
  /// </summary>
  public HueColor Render() {
    HueConnection connection = this.bridge.Connection;
    LightSettings settings = connection.Light;

    // Positive is early, which is the documented direction: the light path runs behind
    // the music, so the engine is asked to live slightly in the future.
    double now = this.clock.NowMicros + (connection.LatencyOffsetMs * MicrosPerMilli);

    this.Consume(now, settings);
    this.Slide(now);

    return FromHue(this.displayHue, this.Brightness(now, settings));
  }

  /// <summary>
  /// Takes a beat off the scheduler's thread and gets out of the way.
  ///
  /// Nothing is decided here. The beat carries the moment it was aimed at, and every
  /// question about it - has it happened yet under the current offset, does it change
  /// the colour - is answered on the frame that renders it. That is what lets the
  /// offset slider move mid-beat without the engine holding a stale decision.
  ///
  /// Internal rather than private so the tests can hand the engine a beat directly.
  /// The alternative is running a real scheduler, which means a real thread and real
  /// tempos, to exercise arithmetic that has neither.
  /// </summary>
  internal void OnBeat(BeatEvent beat) {
    lock(this.gate) {
      if(this.pending.Count >= MaxPendingBeats) {
        this.pending.Dequeue();
      }

      this.pending.Enqueue(beat);
    }
  }

  /// <summary>Applies every beat whose moment has arrived.</summary>
  private void Consume(double now, LightSettings settings) {
    while(true) {
      BeatEvent beat;

      lock(this.gate) {
        if(this.pending.Count == 0 || this.pending.Peek().ScheduledMicros > now) {
          return;
        }

        beat = this.pending.Dequeue();
      }

      // The beat's own scheduled moment and not `now`, so a frame that lands 8 ms after
      // a beat renders a pulse that is 8 ms old rather than one starting late. Over a
      // fade of a couple of hundred milliseconds that is the difference between an
      // envelope and a staircase.
      this.pulseStartedMicros = beat.ScheduledMicros;

      if(this.ShouldChangeColor(beat, settings.ColorChangeBars)) {
        this.targetHue = this.PickHue(settings.Palette);
      }
    }
  }

  /// <summary>
  /// Whether this beat is one of the ones that changes the colour.
  ///
  /// The scene's rule, in the scene's unit: colours change on the downbeat of every
  /// n-th bar, and zero bars means every beat instead. What is rewritten is how the
  /// position is worked out - the scene counted beats it had detected, while this reads
  /// the grid's absolute index, so a dropped or re-anchored beat cannot leave the
  /// changes landing off the bar for the rest of the night.
  /// </summary>
  private bool ShouldChangeColor(BeatEvent beat, int bars) {
    if(bars <= 0) {
      return true;
    }

    if(!beat.IsBarAligned) {
      // No downbeat is known, so there is no bar to land on and the honest fallback is
      // the scene's raw form: every n-th beat from wherever the grid happens to start.
      // Changing on a guessed phase is worth more than not changing at all, and the
      // moment an alignment arrives the branch above takes over.
      return FloorMod(beat.Index, (long)this.beatsPerBar * bars) == 0;
    }

    if(beat.PositionInBar != 1) {
      return false;
    }

    // Counted in bars rather than in beats, because a bar's first beat is not
    // necessarily at an index divisible by the quantum: the grid's alignment can put
    // the downbeat anywhere in the numbering, and a modulo over beat indices would then
    // never come out at zero and the colour would never change again. Dividing is safe
    // from here whatever the phase - consecutive downbeats are a bar apart, so they
    // always land in consecutive bars.
    long bar = FloorDiv(beat.Index, this.beatsPerBar);

    return FloorMod(bar, bars) == 0;
  }

  /// <summary>
  /// Chooses the next hue, far enough from the last one to be seen as a change.
  ///
  /// The best of the attempts rather than the last of them, which the scene did not do
  /// and which matters wherever the threshold cannot be met. <c>Ocean</c> is the palette
  /// that shows it: from six of its eight hues no candidate is 0.12 away, so all fifty
  /// attempts fail, and taking whichever one happened to be tried fiftieth can hand back
  /// a hue indistinguishable from the current one. Keeping the farthest guarantees the
  /// largest change the palette is capable of. The wider palettes reach the threshold
  /// on an early attempt and never get here.
  ///
  /// Distance is measured around the wheel and not along the number line, which is the
  /// other departure. The scene subtracted the two values, so it read a hue at 0.98 as
  /// almost a full turn away from one at 0.02 rather than as its neighbour - and both
  /// of those are red.
  /// </summary>
  private double PickHue(LightPalette palette) {
    IReadOnlyList<int>? hues = LightPalettes.HuesOf(palette);

    double best = this.targetHue;
    double bestDistance = -1;

    for(int attempt = 0; attempt < MaxHueAttempts; attempt++) {
      double candidate = hues is null
        ? this.random.Next(360) / 360.0
        : hues[this.random.Next(hues.Count)] / 360.0;

      double distance = Separation(candidate, this.targetHue);
      if(distance >= MinHueSeparation) {
        return candidate;
      }

      if(distance > bestDistance) {
        best = candidate;
        bestDistance = distance;
      }
    }

    return best;
  }

  /// <summary>Moves the displayed hue toward its target by this frame's share.</summary>
  private void Slide(double now) {
    if(this.renderedAtMicros is not { } previous) {
      // Nothing to interpolate from on the first frame. Starting at the target rather
      // than sliding to it from an arbitrary hue keeps the first colour the one that
      // was chosen.
      this.displayHue = this.targetHue;
      this.renderedAtMicros = now;
      return;
    }

    double elapsedSeconds = Math.Max(0, now - previous) / MicrosPerSecond;
    this.renderedAtMicros = now;

    // Clamped, which the scene had no need to be at a steady 60 frames a second. A
    // frame that arrives late enough to make the rate exceed 1 would otherwise
    // overshoot the target and slide back, so a hiccup would read as a colour wobble.
    double rate = Math.Clamp(TransitionRatePerSecond * elapsedSeconds, 0, 1);

    // The short way round: a target 0.05 above a display of 0.98 is 0.07 away going
    // up through red, not 0.93 going down through everything else.
    double difference = this.targetHue - this.displayHue;
    if(difference > 0.5) {
      difference -= 1;
    } else if(difference < -0.5) {
      difference += 1;
    }

    this.displayHue = Wrap(this.displayHue + (difference * rate));
  }

  /// <summary>
  /// How bright the lights are, given how long ago the last beat was.
  ///
  /// A ceiling and a depth rather than a floor and a peak, which is the arrangement
  /// that cannot express nonsense: a floor given absolutely can be set above the
  /// ceiling, and there is no sensible thing for the engine to do with that. Depth is
  /// also the number somebody actually means - how much should a beat stand out - and
  /// it is what makes a short pulse usable at all, because a shallow one puts a quick
  /// flash on top of a bright room instead of leaving a dark one.
  /// </summary>
  private double Brightness(double now, LightSettings settings) {
    double ceiling = settings.Brightness / (double)LightSettings.MaxPercent;

    if(settings.PulseMs <= 0 || this.pulseStartedMicros is not { } started) {
      // Two situations, one answer, and the answer is the ceiling in both. No pulse
      // means a steady colour: whoever turned it off asked for the colour, not for a
      // night light. And before the first beat there is no envelope to be anywhere in,
      // where full brightness is the argument that chose the stream's dim white over
      // black - a light sitting at its resting level looks exactly like a stream that
      // never opened, and "no music yet" is precisely when somebody is standing in the
      // room asking which of those they are looking at.
      return ceiling;
    }

    double floor = ceiling * (1.0 - (settings.PulseDepth / (double)LightSettings.MaxPercent));
    double age = Math.Max(0, now - started);
    double decay = Math.Exp(Math.Log(PulseSpentAt) * age / (settings.PulseMs * MicrosPerMilli));

    return floor + ((ceiling - floor) * decay);
  }

  /// <summary>
  /// A fully saturated hue at a given brightness, as red, green and blue.
  ///
  /// Written out rather than taken from <c>HueApi.ColorConverters</c> on purpose: this
  /// is the same conversion the scene used, saturation pinned at 1 and brightness
  /// scaling the result linearly, so the colours match what was tuned. Gamma and the
  /// bridge's own colour space are Phase 4's subject, and a correction invented here
  /// would be the wrong one to inherit.
  /// </summary>
  private static HueColor FromHue(double hue, double brightness) {
    double position = Wrap(hue) * 6.0;
    int sector = (int)position;
    double offset = position - sector;

    double rising = brightness * offset;
    double falling = brightness * (1 - offset);

    return sector switch {
      0 => new HueColor(brightness, rising, 0),
      1 => new HueColor(falling, brightness, 0),
      2 => new HueColor(0, brightness, rising),
      3 => new HueColor(0, falling, brightness),
      4 => new HueColor(rising, 0, brightness),
      _ => new HueColor(brightness, 0, falling),
    };
  }

  /// <summary>The shorter of the two ways round the wheel between two hues.</summary>
  private static double Separation(double first, double second) {
    double difference = Math.Abs(Wrap(first) - Wrap(second));

    return Math.Min(difference, 1 - difference);
  }

  /// <summary>A hue folded back into 0 to just under 1, from anywhere.</summary>
  private static double Wrap(double hue) {
    return hue - Math.Floor(hue);
  }

  /// <summary>
  /// Division that rounds toward negative infinity.
  ///
  /// Needed because C# rounds toward zero, and the grid's beat index can be negative:
  /// it extrapolates backwards from its anchor, so the beats just before a fresh
  /// alignment are numbered below zero. With the language's own operators the bar
  /// counting would reverse across that boundary.
  /// </summary>
  private static long FloorDiv(long value, long divisor) {
    long quotient = Math.DivRem(value, divisor, out long remainder);

    return remainder != 0 && ((remainder < 0) != (divisor < 0)) ? quotient - 1 : quotient;
  }

  /// <summary>Remainder that is never negative, for a positive divisor.</summary>
  private static long FloorMod(long value, long divisor) {
    return value - (FloorDiv(value, divisor) * divisor);
  }
}
