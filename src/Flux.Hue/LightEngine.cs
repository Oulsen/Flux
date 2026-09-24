using Flux.Audio;
using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
  /// The most of a beat's pulse that a light's own band can reach between beats.
  ///
  /// Flux's own, with no counterpart in the scene, and the number is doing one job: it
  /// keeps the shimmer under the punch. Each light follows its own band continuously so
  /// that hats lift the outer lights in the gaps where nothing is hitting the kick -
  /// without it the room is only ever a set of simultaneous flashes at different heights,
  /// which is what looking at nine channels of the live feed showed on 2026-09-11.
  ///
  /// Seven tenths, raised from a half on 2026-09-11 after the room still read as every
  /// light doing the same thing: at a half the beat pulse was so much the larger of the
  /// two that the between-beat movement was there in the numbers and not on the wall.
  /// It stays below one on purpose - the beat has to remain the loudest thing in the room
  /// or the pulse stops being legible as a beat at all, and a response allowed to reach
  /// full would do exactly that on dense material where something is always hitting
  /// somewhere.
  /// </summary>
  private const double ShimmerShare = 0.7;

  /// <summary>
  /// The most of the range above the floor that the room's own energy can claim
  /// between beats.
  ///
  /// Flux's own, like <see cref="ShimmerShare"/> above it, and it exists for the
  /// complaint the gates cannot answer: the loudness gate saturates so that ordinary
  /// playing counts as playing, which deliberately discards the difference between a
  /// verse and a drop - so the room answered both identically, reported on 2026-09-11.
  /// This is that difference, spent on the resting level rather than on the pulse: a
  /// section running hotter than the room has been lifts what the lights fall back to
  /// between beats, and a steady passage rests on the floor exactly as it always did.
  ///
  /// Below <see cref="ShimmerShare"/> on purpose, which keeps the room legible as a
  /// hierarchy: the beat is the loudest thing in the room, a band doing something right
  /// now reads above the room's general body, and the body sits above the floor. First
  /// to move if the room breathes too much or too little - alongside the two constants
  /// in <see cref="BandDynamics"/> that decide when and how easily it breathes at all.
  /// </summary>
  private const double EnergyShare = 0.4;

  /// <summary>
  /// The dimmest a light is allowed to sit while a stream is open, as a fraction of the
  /// room's own ceiling.
  ///
  /// Asked for on 2026-09-11, after watching it: the lights should never go fully out,
  /// only down to their lowest setting. That was already true at the default depth of 85,
  /// where a spent pulse leaves a light at 15 per cent - but a depth of 100 is documented
  /// as fading to nothing, and the spatial share can take a light on a dead band to the
  /// floor whatever the depth. A light that is off is indistinguishable from a light that
  /// has stopped receiving anything, and between beats is most of the time.
  ///
  /// A fraction of the ceiling rather than an absolute value, so that turning the room
  /// down turns this down with it: a 5 per cent floor under a room set to 20 per cent
  /// would otherwise be a quarter of the brightness somebody asked for.
  ///
  /// <b>A brightness of zero is still black.</b> That is a blackout, which is somebody
  /// asking for no light rather than for a little, and the two should not be confused by
  /// a floor that ignores the request.
  /// </summary>
  private const double MinimumLitFraction = 0.05;

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
  /// How long a pulse holds its peak before the fade begins, in milliseconds.
  ///
  /// A departure from the scene, stated as one: the scene's decay constant drove an
  /// on-screen indicator at 60 frames a second, where the peak frame is guaranteed to
  /// be displayed. A bulb is not a monitor. The bridge forwards to it at roughly 25
  /// updates a second - the figure <see cref="LightSettings.PulseMs"/> already
  /// documents - so the top of a decay that starts falling the instant the beat lands
  /// exists for one stream frame and is resampled away before any bulb renders it.
  /// The room then never actually shows the brightness the beat was worth, which is
  /// what watching it beside the scene showed on 2026-09-11. Two bridge frames is the
  /// shortest hold that guarantees the peak is forwarded.
  ///
  /// Capped at half the pulse in <see cref="EnvelopeOf"/>, so a short pulse stays
  /// mostly a fade, and the fall runs over what the hold leaves: hold plus fall still
  /// totals <see cref="LightSettings.PulseMs"/>, so the number on the slider remains
  /// the number of milliseconds the flash visibly lasts.
  /// </summary>
  private const double PulseHoldMs = 80;

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

  /// <summary>
  /// How much of each strobe cycle is the white flash. The scene's number.
  ///
  /// A quarter, which the scene chose so that the lights have time to react even at
  /// fast settings: most of every cycle is the ordinary room, and the flash is the
  /// short part.
  /// </summary>
  private const double StrobeDuty = 0.25;

  /// <summary>
  /// How many beat lengths after the last beat the strobe keeps running before it
  /// stops.
  ///
  /// Flux's own. The scene's grid ran on time alone and never stopped once it had a
  /// tempo, but Flux's beats come from a source that can go away - Link closing, a
  /// tempo cleared - and a strobe extrapolating a grid that no longer exists is a room
  /// flashing to nothing. Two is the scheduler's own figure for when a timeline has
  /// gone stale, so the strobe gives up at the same point the beats themselves do; a
  /// single late beat is well inside it.
  /// </summary>
  private const double StrobeStaleBeats = 2.0;

  private readonly BeatScheduler scheduler;
  private readonly HueBridgeService bridge;
  private readonly IMonotonicClock clock;

  /// <summary>
  /// What the room sounds like, or null in a build with no audio at all.
  ///
  /// Null is an ordinary state and not a degraded one: Link supplies a tempo with no
  /// microphone anywhere near it, and that configuration lights every channel alike.
  /// Read through <see cref="IAudioSpectrum.Levels"/> and never
  /// <see cref="IAudioSpectrum.Spectrum"/>, which consumes the beat verdict the
  /// visualizer needs - see the argument on that method.
  /// </summary>
  private readonly IAudioSpectrum? spectrum;
  private readonly ILogger<LightEngine> logger;
  private readonly Random random;
  private readonly object gate = new();

  /// <summary>
  /// Held for the whole of a render, so the stream and the preview take turns. Its own
  /// lock rather than <see cref="gate"/>, which the beat handler takes on the scheduler's
  /// thread and must never wait behind a render.
  /// </summary>
  private readonly object renderGate = new();
  private readonly Queue<BeatEvent> pending = new(MaxPendingBeats);

  /// <summary>
  /// Each band as a fraction of its own recent loudest, which is the only form in which
  /// the three can be compared - see <see cref="BandDynamics"/> for why the raw levels
  /// cannot be.
  /// </summary>
  private readonly BandDynamics dynamics = new();

  /// <summary>
  /// The hues in play, of which the first <see cref="activeColorCount"/> are drawn.
  ///
  /// Fixed at the maximum rather than resized with the setting, so that changing the
  /// number of colours mid-set cannot allocate on the frame path and cannot leave a
  /// half-built array visible to a render. The entries past the count are stale on
  /// purpose: they are what the room was last split into, and they are overwritten
  /// before they are ever drawn again.
  /// </summary>
  private readonly double[] targetHues = new double[LightSettings.MaxColorCount];
  private readonly double[] displayHues = new double[LightSettings.MaxColorCount];

  /// <summary>How many of the hues above are currently being drawn.</summary>
  private int activeColorCount = 1;

  /// <summary>
  /// The number the random layout mixes into each light's rank, re-drawn whenever the
  /// colours change.
  ///
  /// Re-drawn on the change rather than per frame, which is the difference between a
  /// room whose shape changes with the music and a room full of flickering. Held even
  /// while the layout is not random, because the alternative is a draw on the frame path
  /// the first time somebody selects it.
  /// </summary>
  private uint colorRoll;

  /// <summary>
  /// The timestamp of a downbeat just set by an alignment, handed over by
  /// <see cref="OnBarAligned"/> and not yet taken by the frame path. Guarded by
  /// <see cref="gate"/>, since the alignment arrives on whatever thread pressed it.
  /// </summary>
  private double? alignedAtMicros;

  /// <summary>
  /// The newly anchored downbeat the frame path still owes a colour change, or null.
  ///
  /// This is what makes the anchor button answer on the beat it was pressed for. The bar
  /// count itself comes from the grid on every beat, so the cycle restarts from the
  /// anchor without help; what the count cannot do is change the colour on a beat that
  /// fired before the press, and a press usually lands a hair after the kick it means.
  /// Held by time rather than by index because the grid renumbers beats on a tempo
  /// change, and a moment in the music survives that where an index does not.
  /// </summary>
  private double? colorRestartMicros;

  private double? pulseStartedMicros;

  /// <summary>
  /// When the last beat landed and how long a beat was then, or null before the first.
  ///
  /// The strobe's clock. Kept apart from <see cref="pulseStartedMicros"/> although the
  /// two are written together, because they answer different questions: that one is
  /// where a fade began, and this is where the grid is.
  /// </summary>
  private double? lastBeatMicros;

  private double? beatPeriodMicros;

  /// <summary>
  /// How hard the kick was hitting when the last beat landed, 0 to 1, and 1 when there is
  /// nothing to listen to.
  ///
  /// <b>This is what stops the room announcing a beat nobody can hear.</b> A grid keeps
  /// publishing beats through a breakdown - Link is a clock and the detector holds a
  /// timeline - so before this the lights fired a synchronous pulse right through a
  /// passage with no kick in it. Scaling that pulse by how loud the room was made it
  /// quieter and left it synchronous, which is the part that reads as wrong: it is the
  /// unison rather than the brightness that gives it away.
  ///
  /// Captured at the beat and then only ever raised while the pulse is alive, never
  /// followed downward - the opposite choice to <c>Loudness</c> beside it, and the two
  /// answer different questions. How hard this beat hit should hold for the whole fade,
  /// or a pulse gets cut off by the gap after the kick that caused it. Whether anything
  /// is playing at all is a fact about now, so a track that stops mid-pulse still kills
  /// the tail.
  ///
  /// Raised rather than fixed because of when the capture happens: the frame that
  /// consumes a beat runs ahead of the audio - the latency offset shifts it early on
  /// purpose, and capture adds its own lag - so the kick this beat announces has
  /// usually not reached the analyzer yet, and a strength read at that one moment is a
  /// reading of the gap before the kick. That gated ordinary beats to a fraction of
  /// their height, differently on every beat, which no slider could correct. Letting
  /// the hit count when it lands fixes that without touching the breakdown behaviour:
  /// in a break no hit ever arrives, so there is nothing to ratchet.
  ///
  /// One with no audio, so a build driven by a clock alone pulses on every beat exactly
  /// as it always has.
  /// </summary>
  private double pulseStrength = 1;

  private double? renderedAtMicros;

  /// <summary>
  /// The ordinary form.
  ///
  /// <paramref name="spectrum"/> is nullable and deliberately <b>not optional</b>. A
  /// default here is what let the band count reach the analyzer as a ceiling in step 3:
  /// the registration built its analyzer with an explicit argument list, the new
  /// parameter had a default, and every symptom said the wiring was correct except the
  /// picture. Without a default, a construction site that forgets the room's sound does
  /// not compile.
  /// </summary>
  public LightEngine(
    BeatScheduler scheduler,
    HueBridgeService bridge,
    IMonotonicClock clock,
    IAudioSpectrum? spectrum,
    ILogger<LightEngine> logger)
    : this(scheduler, bridge, clock, spectrum, logger, new Random()) {
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
    IAudioSpectrum? spectrum,
    ILogger<LightEngine> logger,
    Random random) {
    this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.spectrum = spectrum;
    this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    this.random = random ?? throw new ArgumentNullException(nameof(random));

    // One draw and not one per slot, which keeps a seeded engine's first colour the
    // number it has always been. The other slots are not colours yet: they become them
    // when somebody asks for a second or third, and until then drawing for them would
    // only move every later draw along by two.
    this.targetHues[0] = this.random.Next(360) / 360.0;
    Array.Fill(this.targetHues, this.targetHues[0]);
    Array.Copy(this.targetHues, this.displayHues, this.targetHues.Length);
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
    this.scheduler.BarAligned += this.OnBarAligned;

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    this.scheduler.Beat -= this.OnBeat;
    this.scheduler.BarAligned -= this.OnBarAligned;

    return Task.CompletedTask;
  }

  /// <summary>
  /// The colours for this instant.
  ///
  /// Called by the stream loop while a session is open and by <see cref="LightPreview"/>
  /// while none is, and serialised by <see cref="renderGate"/> so the two can never run
  /// at once - which matters only at the handover, since the preview stands down the
  /// moment a session starts rendering. Taking turns is safe where running together is
  /// not: every piece of state below integrates over the time since the last render,
  /// whoever made it, so two callers split an interval between them rather than counting
  /// it twice. What they must not do is interleave inside one render, and the lock is
  /// what stops that.
  ///
  /// <b>Where a light is decides both how hard the beat hits it and, above one colour,
  /// which colour it is in.</b> A light near the centre of the room follows the kick,
  /// one out at the edge follows the top, and the band each listens to is decided by
  /// <see cref="LightPlacements"/> from coordinates the bridge reports. With
  /// <see cref="LightSettings.ColorCount"/> above one the same axis splits the room into
  /// that many hues, so the colour split and the brightness split are the same statement
  /// made twice rather than two unrelated ones.
  /// </summary>
  public HueColor Render(ReadOnlySpan<LightPlacement> lights, Span<HueColor> colors) {
    lock(this.renderGate) {
      return this.RenderLocked(lights, colors);
    }
  }

  private HueColor RenderLocked(ReadOnlySpan<LightPlacement> lights, Span<HueColor> colors) {
    if(colors.Length < lights.Length) {
      throw new ArgumentException($"{lights.Length} light(s) need {lights.Length} colours, and {colors.Length} were offered.", nameof(colors));
    }

    HueConnection connection = this.bridge.Connection;
    LightSettings settings = connection.Light;
    int wanted = Math.Clamp(settings.ColorCount, 1, LightSettings.MaxColorCount);

    // Positive is early, which is the documented direction: the light path runs behind
    // the music, so the engine is asked to live slightly in the future.
    double now = this.clock.NowMicros + (connection.LatencyOffsetMs * MicrosPerMilli);

    // The frame's own length, taken once. The hue transition and the band references are
    // both integrating over it, and two readings of one interval is how a frame ends up
    // half a frame long in one place and a whole one in another.
    double? lastFrame = this.renderedAtMicros;
    double elapsedSeconds = lastFrame is { } previous ? Math.Max(0, now - previous) / MicrosPerSecond : 0;
    this.renderedAtMicros = now;

    // Read once for the whole frame rather than per light, which is not only cheaper:
    // sampling it nine times would let the analyzer publish a new frame midway through
    // and leave one half of the room a frame ahead of the other. Read before the early
    // return below as well, so that the references inside BandDynamics keep tracking
    // whether or not there is a room to paint - a stream opening should find them where
    // the music has been, not starting from nothing.
    //
    // Before the beats are applied rather than after, because a beat's pulse is now sized
    // from what the kick is doing at the moment it lands.
    SpectrumLevels? heard = this.spectrum?.Levels();
    RoomSound? sound = heard is { } bands ? this.dynamics.Observe(bands, elapsedSeconds) : null;
    SpectrumLevels? drive = sound?.Drive;
    SpectrumLevels? transient = sound?.Transient;

    this.Consume(now, settings, sound?.Transient.Kick);
    this.Split(wanted, settings.Palette);
    this.Slide(elapsedSeconds, lastFrame is null, wanted);

    Envelope envelope = this.EnvelopeOf(now, settings, sound?.Loudness ?? 1, sound?.Energy ?? 0);

    // After everything above rather than instead of it, so the hue keeps sliding and
    // the bands keep being heard through a flash: the room between flashes is the
    // ordinary room, and it has to be where it would have been.
    if(this.IsStrobeFlash(now, settings)) {
      return Flash(settings, lights.Length, colors);
    }

    // The first hue and not an average of them, because an average of two hues is a
    // third colour that no light in the room is showing. What this field has meant since
    // the per-light feed arrived is what the engine chose, and with the room split it is
    // what the engine chose first.
    HueColor shared = FromHue(this.displayHues[0], envelope.Level);

    if(lights.IsEmpty) {
      return shared;
    }

    for(int index = 0; index < lights.Length; index++) {
      double at = lights[index].At;
      double hue = this.displayHues[GroupOf(at, lights.Length, wanted, this.colorRoll, settings.ColorLayout)];

      colors[index] = FromHue(hue, envelope.LevelAt(Share(drive, at), Shimmer(transient, at)));
    }

    return shared;
  }

  /// <summary>
  /// Whether this instant falls inside a strobe flash.
  ///
  /// The scene's rule on Flux's grid: a cycle of <see cref="LightSettings.StrobeCycle"/>
  /// per cent of a beat, counted from the last beat, and white for the first
  /// <see cref="StrobeDuty"/> of each. Counted from the beat's own scheduled moment, on
  /// the argument <see cref="Consume"/> makes for the pulse, so the flashes and the
  /// beats are one grid rather than two drifting ones. With no beat yet there is no grid
  /// to flash on and nothing happens, which is also what the scene did before it had a
  /// tempo.
  ///
  /// On when the setting is on or when anybody is holding the button, and those are the
  /// same strobe: holding it while it is switched on changes nothing.
  /// </summary>
  private bool IsStrobeFlash(double now, LightSettings settings) {
    if(!settings.Strobe && !this.bridge.Strobe.IsHeld) {
      return false;
    }

    if(this.lastBeatMicros is not { } beat || this.beatPeriodMicros is not { } period) {
      return false;
    }

    double since = now - beat;

    if(since < 0 || since > period * StrobeStaleBeats) {
      return false;
    }

    double cycle = period * settings.StrobeCycle / LightSettings.MaxPercent;

    return (since % cycle) / cycle < StrobeDuty;
  }

  /// <summary>
  /// Every light white, at the room's ceiling.
  ///
  /// The scene's flash was white at full, drawn over everything. White is kept; full is
  /// not, and that is a departure worth its reason: <see cref="LightSettings.Brightness"/>
  /// is how bright the room is allowed to get, and a strobe ignoring it would make the
  /// one control somebody reaches for first stop working at the one moment it matters
  /// most. It also keeps a blackout black - somebody who asked for no light does not get
  /// a strobe instead.
  /// </summary>
  private static HueColor Flash(LightSettings settings, int lights, Span<HueColor> colors) {
    double ceiling = settings.Brightness / (double)LightSettings.MaxPercent;
    HueColor white = new HueColor(ceiling, ceiling, ceiling);

    colors[..lights].Fill(white);

    return white;
  }

  /// <summary>
  /// The room's one colour, with no geometry involved.
  ///
  /// For the tests, and for reasoning about the engine in isolation: everything the
  /// engine decides is visible in this one value, which is what makes an envelope and a
  /// palette assertable without a room to put them in.
  /// </summary>
  internal HueColor Render() {
    return this.Render([], []);
  }

  /// <summary>
  /// How much of this frame's pulse a light at a given frequency takes, 0 to 1.
  ///
  /// Relative to whichever band is leading and not absolute, which is the decision that
  /// keeps this out of the beat-energy envelope's territory. Absolute energy would make
  /// the whole room dim during a quiet passage, and how hard the room hits overall is
  /// the next step's question - deciding it here as well would leave two answers to one.
  /// Relative says only which lights lead.
  ///
  /// <b>What leads is decided from the bands measured against themselves</b> and not
  /// from their raw levels, which is the correction made on 2026-09-11: raw, the kick
  /// band outweighs the others on essentially every frame of every track, so "which
  /// lights lead" had exactly one answer and the room was a fixed gradient. See
  /// <see cref="BandDynamics"/>, which is where the comparison is made possible.
  ///
  /// One, meaning the room's own envelope unchanged, whenever there is nothing to go
  /// on: no audio project in the build, no device open, silence, or the improbable
  /// frame where all three bands read zero. Every one of those means "no opinion", and
  /// a room lighting evenly is what Flux did for the whole of Phase 3.
  /// </summary>
  private static double Share(SpectrumLevels? drive, double at) {
    if(drive is not { } bands) {
      return 1;
    }

    double peak = bands.Peak;

    return peak > 0 ? Math.Clamp(bands.At(at) / peak, 0, 1) : 1;
  }

  /// <summary>
  /// How far a light's own band lifts it between beats, 0 to
  /// <see cref="ShimmerShare"/>.
  ///
  /// Fed by the transient measure and not the level one, which is the correction made
  /// after the room still read as every light doing the same thing: a band's level
  /// changes slowly, so lifting a light by it moved every light together and only the
  /// heights differed. What each light needs in the gaps between beats is whether its own
  /// band is doing something *now*, which is a fast question with a different answer per
  /// band at any given instant. That is what a hat pattern under a held chord looks like
  /// from the far side of a room, and it is the difference between a room that flashes
  /// and a room the music moves through.
  ///
  /// Zero with nothing to go on, which leaves the pulse as the only thing driving the
  /// lights - exactly the behaviour before this existed.
  /// </summary>
  private static double Shimmer(SpectrumLevels? transient, double at) {
    return transient is { } bands ? Math.Clamp(bands.At(at), 0, 1) * ShimmerShare : 0;
  }

  /// <summary>
  /// Which colour group a light belongs to, under the chosen layout.
  ///
  /// Everything here is a function of where the light sits on the frequency axis and of
  /// nothing else - not of its channel id, not of its position in the array it arrived
  /// in. That is what keeps the three layouts comparable: they divide the same room the
  /// same way round, and they differ only in how the divisions are handed out.
  /// </summary>
  /// <param name="at">Where the light listens, 0 to 1.</param>
  /// <param name="lights">How many lights the room has, which turns <paramref name="at"/> back into a rank.</param>
  /// <param name="count">How many colours to divide between.</param>
  /// <param name="roll">The number re-drawn at each colour change, for <see cref="LightColorLayout.Random"/>.</param>
  /// <param name="layout">How to divide them.</param>
  private static int GroupOf(double at, int lights, int count, uint roll, LightColorLayout layout) {
    double position = Math.Clamp(at, 0, 1);

    if(count <= 1) {
      return 0;
    }

    // The rank the placement was built from, recovered rather than passed in: the engine
    // is handed a frequency per light and the placements are spread evenly over the
    // ranks, so this inverts that exactly. It is the honest way to ask "which light is
    // this, counting outward" without the engine taking a second opinion on the ordering.
    int rank = lights > 1 ? (int)Math.Round(position * (lights - 1)) : 0;

    return layout switch {
      // Equal spans of the axis rather than equal counts of lights, because the axis is
      // what the placements already spread evenly by rank: splitting it in two puts the
      // lower half of the spectrum in one colour and the upper half in the other whatever
      // the room's shape, while splitting by count would move the boundary every time an
      // area gained a bulb.
      LightColorLayout.Radial => Math.Clamp((int)(position * count), 0, count - 1),

      LightColorLayout.Alternating => rank % count,

      // A cheap integer hash rather than a stored assignment. Knuth's multiplicative
      // constant mixes the low bits of the rank up into the high ones, which matters at
      // two colours: without it the low bit survives into the result and "random" is
      // alternating with extra steps.
      LightColorLayout.Random => (int)((((uint)rank * 2654435761u) ^ roll) % (uint)count),

      // No default arm, deliberately: a layout added later must fail to compile here
      // rather than silently ship as whichever one the fallback happened to be.
      _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "No grouping for this layout."),
    };
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

  /// <summary>
  /// Takes note that the downbeat moved, so the frame path can give the anchored beat
  /// its colour change even when that beat has already been rendered.
  ///
  /// Internal for the same reason as <see cref="OnBeat"/>.
  /// </summary>
  /// <param name="downbeatMicros">The local timestamp of the beat that is now position 1.</param>
  internal void OnBarAligned(double downbeatMicros) {
    lock(this.gate) {
      this.alignedAtMicros = downbeatMicros;
    }
  }

  /// <summary>Applies every beat whose moment has arrived.</summary>
  /// <param name="now">The instant being rendered, already shifted by the latency offset.</param>
  /// <param name="settings">The look in force, which decides whether this beat changes the colour.</param>
  /// <param name="kickHit">
  /// How hard the kick is hitting right now, or null with nothing to listen to.
  ///
  /// The transient and not the level, which is the distinction the whole thing turns on:
  /// a breakdown often still has bass in it and what it does not have is a kick landing.
  /// A band sitting at a steady level, however loud, is not a hit.
  /// </param>
  private void Consume(double now, LightSettings settings, double? kickHit) {
    lock(this.gate) {
      if(this.alignedAtMicros is { } aligned) {
        this.colorRestartMicros = aligned;
        this.alignedAtMicros = null;
      }
    }

    int colorCount = Math.Clamp(settings.ColorCount, 1, LightSettings.MaxColorCount);

    // The anchored beat was rendered before the press was heard, carrying the old count.
    // Answering now is a frame or so after the beat the user meant, against a bar later
    // if the change waited for the count. Judged by the last beat rendered and not by
    // the clock, because the latency offset runs this path up to half a second ahead and
    // the clock passing the downbeat says nothing about whether its beat has arrived -
    // answering early would change the colour and then change it again when it did.
    //
    // Bounded above as well, by the same half beat. A drop snaps to the nearest downbeat
    // it already knows, which can be a beat or two behind the last one rendered, and
    // answering then would change the colour partway through the bar. That restart is
    // dropped rather than paid: the beats already carry the new count, so the next
    // change lands on a downbeat of its own.
    if(this.colorRestartMicros is { } owed && this.lastBeatMicros is { } last) {
      double sameBeat = SameBeatMicros(this.beatPeriodMicros);

      if(last > owed + sameBeat) {
        this.colorRestartMicros = null;
      } else if(last >= owed - sameBeat) {
        this.colorRestartMicros = null;
        this.PickHues(settings.Palette, colorCount);
      }
    }

    while(true) {
      BeatEvent beat;

      lock(this.gate) {
        if(this.pending.Count == 0 || this.pending.Peek().ScheduledMicros > now) {
          break;
        }

        beat = this.pending.Dequeue();
      }

      // The beat's own scheduled moment and not `now`, so a frame that lands 8 ms after
      // a beat renders a pulse that is 8 ms old rather than one starting late. Over a
      // fade of a couple of hundred milliseconds that is the difference between an
      // envelope and a staircase.
      this.pulseStartedMicros = beat.ScheduledMicros;
      this.lastBeatMicros = beat.ScheduledMicros;
      this.beatPeriodMicros = beat.Bpm > 0 ? 60 * MicrosPerSecond / beat.Bpm : null;
      this.pulseStrength = kickHit is { } hit ? Math.Clamp(hit, 0, 1) : 1;

      // The anchored beat takes a colour whatever bar it was stamped with, since one that
      // fired just before the press still carries the old count. Earlier beats are left
      // to the old phase, which was still the right one for them.
      bool anchored = this.colorRestartMicros is { } restart
        && beat.ScheduledMicros >= restart - SameBeatMicros(this.beatPeriodMicros);
      if(anchored) {
        this.colorRestartMicros = null;
      }

      if(anchored || this.ShouldChangeColor(beat, settings.ColorChangeBars)) {
        this.PickHues(settings.Palette, colorCount);
      }
    }

    // The ratchet the field's own comment argues for. Bounded by the pulse's remaining
    // life rather than by a second window constant, because the decay already prices
    // lateness: a hit arriving with the pulse nearly spent raises a strength that
    // multiplies almost nothing, so the bound never needs to be exact - it only stops
    // a spent pulse being re-judged by hits that belong to no beat at all.
    if(kickHit is { } landing
      && this.pulseStartedMicros is { } started
      && now - started <= settings.PulseMs * MicrosPerMilli) {
      this.pulseStrength = Math.Max(this.pulseStrength, Math.Clamp(landing, 0, 1));
    }
  }

  /// <summary>
  /// Whether this beat is one of the ones that changes the colour.
  ///
  /// The scene's rule, in the scene's unit: colours change on the downbeat of every
  /// n-th bar, and zero bars means every beat instead. What is rewritten is how the
  /// position is worked out - the scene counted beats it had detected, while this reads
  /// the grid's bar number, so a dropped beat cannot leave the changes landing off the
  /// bar for the rest of the night.
  ///
  /// <b>The bar number is the grid's, counted from the anchor.</b> Until 2026-09-24 the
  /// engine kept an anchor of its own and inferred a new one whenever a downbeat arrived
  /// out of phase within the bar. That caught a press one beat off the old phase and
  /// missed the commoner one: a press on a beat that was already a downbeat, but in the
  /// second, third or fourth bar of the cycle, moved the readout's bar one and left the
  /// colours counting from the old one. Anchoring means "this is bar one", and it has to
  /// mean that for everything the bar count drives. Before any anchor the grid counts
  /// from its own beat 0, which is the scene's raw form - every n-th bar from wherever
  /// the numbering started - and worth more than not changing at all.
  /// </summary>
  private bool ShouldChangeColor(BeatEvent beat, int bars) {
    if(bars <= 0) {
      return true;
    }

    return beat.PositionInBar == 1 && FloorMod(beat.Bar, bars) == 0;
  }

  /// <summary>
  /// How close a beat's moment has to be to a downbeat's to be that beat: half a beat,
  /// which is the same nearest-beat rounding the alignment itself used to pick it. The
  /// timeline can be rebased between the press and the beat, so the two moments are not
  /// always equal, but the beats either side are a whole beat away. Zero before any beat
  /// has given a tempo, when there is nothing to be near.
  /// </summary>
  private static double SameBeatMicros(double? beatPeriodMicros) {
    return (beatPeriodMicros ?? 0) / 2;
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
  private double PickHue(LightPalette palette, ReadOnlySpan<double> away) {
    IReadOnlyList<int>? hues = LightPalettes.HuesOf(palette);

    double best = away.IsEmpty ? this.targetHues[0] : away[0];
    double bestDistance = -1;

    for(int attempt = 0; attempt < MaxHueAttempts; attempt++) {
      double candidate = hues is null
        ? this.random.Next(360) / 360.0
        : hues[this.random.Next(hues.Count)] / 360.0;

      // The nearest of the hues to stay away from, because a candidate is only as good
      // as its worst neighbour: one that sits comfortably opposite the first colour and
      // right on top of the second is not a third colour at all.
      double distance = double.PositiveInfinity;
      foreach(double other in away) {
        distance = Math.Min(distance, Separation(candidate, other));
      }

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

  /// <summary>
  /// Chooses the whole set of hues the room is about to be in.
  ///
  /// The first is chosen against the one it replaces, which is the single-colour rule
  /// unchanged. Each one after it is chosen against the ones already picked in this same
  /// round and not against what those slots held before, because what makes two colours
  /// read as two colours is that they differ from each other now - how either of them
  /// relates to the colour it replaced is a question about the transition, and the first
  /// hue already carries that.
  /// </summary>
  private void PickHues(LightPalette palette, int count) {
    this.targetHues[0] = this.PickHue(palette, [this.targetHues[0]]);

    for(int index = 1; index < count; index++) {
      this.targetHues[index] = this.PickHue(palette, this.targetHues.AsSpan(0, index));
    }

    // Derived from the hue that was just chosen rather than drawn separately, which is
    // not thrift: a seeded engine's colours are a fact its tests assert, and an extra
    // draw here would move every later one along for a value nothing about the colours
    // depends on. The hue is uniform over the wheel, so this is as well spread as a draw
    // would have been, and it changes exactly when the colours do - which is when the
    // random layout is supposed to reshuffle.
    this.colorRoll = (uint)(this.targetHues[0] * uint.MaxValue);
  }

  /// <summary>
  /// Brings the set of hues up to date with a changed colour count.
  ///
  /// Without this a room split from one colour into two shows one colour until the next
  /// scheduled change, which at sixteen bars is half a minute of a setting looking
  /// broken. The new groups start from the colour they are splitting out of and slide to
  /// their own, so the split is something the room does rather than something that has
  /// already happened by the time anyone looks up.
  /// </summary>
  private void Split(int count, LightPalette palette) {
    if(count == this.activeColorCount) {
      return;
    }

    for(int index = this.activeColorCount; index < count; index++) {
      this.targetHues[index] = this.PickHue(palette, this.targetHues.AsSpan(0, index));
      this.displayHues[index] = this.displayHues[0];
    }

    this.activeColorCount = count;
  }

  /// <summary>Moves each displayed hue toward its target by this frame's share.</summary>
  private void Slide(double elapsedSeconds, bool firstFrame, int count) {
    if(firstFrame) {
      // Nothing to interpolate from on the first frame. Starting at the target rather
      // than sliding to it from an arbitrary hue keeps the first colour the one that
      // was chosen.
      Array.Copy(this.targetHues, this.displayHues, this.targetHues.Length);
      return;
    }

    // Clamped, which the scene had no need to be at a steady 60 frames a second. A
    // frame that arrives late enough to make the rate exceed 1 would otherwise
    // overshoot the target and slide back, so a hiccup would read as a colour wobble.
    double rate = Math.Clamp(TransitionRatePerSecond * elapsedSeconds, 0, 1);

    for(int index = 0; index < count; index++) {
      // The short way round: a target 0.05 above a display of 0.98 is 0.07 away going
      // up through red, not 0.93 going down through everything else.
      double difference = this.targetHues[index] - this.displayHues[index];
      if(difference > 0.5) {
        difference -= 1;
      } else if(difference < -0.5) {
        difference += 1;
      }

      this.displayHues[index] = Wrap(this.displayHues[index] + (difference * rate));
    }
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
  ///
  /// Returned in parts rather than as one number, and that is what makes the spatial
  /// behaviour possible without a new setting: a light's own band scales the pulse above
  /// the floor and never the floor itself, so a light listening to a silent band drops
  /// back to the room's resting level rather than going out. A light that goes out looks
  /// exactly like a light that has stopped receiving anything, which is the argument the
  /// floor already existed for.
  ///
  /// <b><paramref name="loudness"/> is what makes the pulse answer the music and not
  /// only the grid.</b> A beat during a breakdown is still a beat, and a grid driven by
  /// a clock goes on publishing beats after the track has finished, so a pulse of a
  /// fixed height answers a break and a drop and a silent room identically - which is
  /// what was seen on 2026-09-11 and is what this scales. One means "no opinion" and
  /// reproduces the old behaviour exactly, which is what a build with no audio gets.
  ///
  /// Applied continuously rather than captured at the beat, which is a departure from
  /// the wording this step was booked under. Capturing it makes a loud beat stay loud
  /// through its whole decay and is the more musical of the two - but it also means a
  /// track that stops mid-pulse finishes the pulse at the volume of the track that is no
  /// longer playing, and the complaint that prompted this was about exactly that moment.
  /// Worth revisiting by ear: the two differ only in the couple of hundred milliseconds
  /// after a beat.
  /// </summary>
  private Envelope EnvelopeOf(double now, LightSettings settings, double loudness, double energy) {
    double ceiling = settings.Brightness / (double)LightSettings.MaxPercent;

    if(settings.PulseMs <= 0 || this.pulseStartedMicros is not { } started) {
      // Two situations, one answer, and the answer is the ceiling in both. No pulse
      // means a steady colour: whoever turned it off asked for the colour, not for a
      // night light. And before the first beat there is no envelope to be anywhere in,
      // where full brightness is the argument that chose the stream's dim white over
      // black - a light sitting at its resting level looks exactly like a stream that
      // never opened, and "no music yet" is precisely when somebody is standing in the
      // room asking which of those they are looking at.
      //
      // A floor equal to the ceiling also makes the spatial scaling vanish rather than
      // needing a special case, which is right: with the pulse switched off there is no
      // pulse to divide between the lights, and the person who turned it off asked for
      // one steady colour rather than for a room that shimmers by position.
      return new Envelope(ceiling, ceiling, 1, 1, 1, 0);
    }

    double floor = Math.Max(
      ceiling * (1.0 - (settings.PulseDepth / (double)LightSettings.MaxPercent)),
      ceiling * MinimumLitFraction);
    double age = Math.Max(0, now - started);

    // Hold, then fall, together spanning exactly PulseMs - see PulseHoldMs for why the
    // peak needs width at all. The cap keeps a short pulse mostly a fade, and it keeps
    // the fall's length positive without a special case.
    double pulseMicros = settings.PulseMs * MicrosPerMilli;
    double holdMicros = Math.Min(PulseHoldMs * MicrosPerMilli, pulseMicros / 2);
    double decay = age <= holdMicros
      ? 1
      : Math.Exp(Math.Log(PulseSpentAt) * (age - holdMicros) / (pulseMicros - holdMicros));

    return new Envelope(floor, ceiling, decay, Math.Clamp(loudness, 0, 1), this.pulseStrength, Math.Clamp(energy, 0, 1));
  }

  /// <summary>Where this frame sits between the resting level and full.</summary>
  /// <param name="Floor">The resting level, which a light never drops below while a stream is open.</param>
  /// <param name="Ceiling">The brightest this room is allowed to get.</param>
  /// <param name="Decay">How much of the last beat's pulse is left, 1 at the beat and falling.</param>
  /// <param name="Loudness">
  /// How much of the range above the floor the music is currently entitled to.
  ///
  /// Applied to everything above the floor and never to the floor itself, which is the
  /// same rule the spatial share already follows and for the same reason: quiet has to
  /// mean a smaller pulse rather than a lower resting level. A silent room that fades
  /// out altogether is indistinguishable from a stream that has dropped, and telling
  /// those two apart is the one thing the floor has always existed for.
  /// </param>
  /// <param name="Strength">
  /// How hard the kick hit on the beat this pulse belongs to.
  ///
  /// Reaches the pulse and deliberately not the shimmer, which is what carries a break:
  /// with no kick the lights are left following their own bands and nothing else, and
  /// that is the whole point of sizing a pulse this way.
  /// </param>
  /// <param name="Energy">
  /// How hot the room is running against its own recent typical, which is what lets the
  /// resting level breathe with the track: a drop lifts what the lights fall back to
  /// between beats, and a steady passage rests on the floor as it always did.
  ///
  /// Deliberately gated by nothing - not the kick, not the loudness. It is derived from
  /// the same hearing they are and goes to zero with silence on its own, and a swell
  /// with no kick in it is exactly the thing it exists to show. Uniform across the room
  /// on purpose, where the shimmer is per band: which lights lead is already said twice,
  /// and how loud this section is happens to every light at once.
  /// </param>
  private readonly record struct Envelope(double Floor, double Ceiling, double Decay, double Loudness, double Strength, double Energy) {
    /// <summary>
    /// <b>Two gates compose by taking the smaller, not by multiplying.</b>
    ///
    /// <see cref="Loudness"/> and <see cref="Strength"/> are both answering one question -
    /// should this beat count for anything - from two directions, so multiplying them
    /// charges the pulse twice for one doubt. Each was individually defensible and
    /// together they were a chain of dimmers: sixty frames off the live socket on
    /// 2026-09-11 never reached half the brightness the settings allowed, on a room whose
    /// ceiling was a full hundred per cent.
    ///
    /// The smaller of the two keeps both behaviours whole. A break still kills the pulse
    /// because the kick gate goes to nothing; a finished track still kills it because the
    /// loudness gate does; and ordinary playing, where both are merely high rather than
    /// perfect, gets the higher of the two readings rather than their product.
    /// </summary>
    private double PulseGate => Math.Min(this.Loudness, this.Strength);

    /// <summary>The level the room as a whole is at, which is what a page is shown.</summary>
    internal double Level => this.Above(Math.Max(this.Decay * this.PulseGate, this.Energy * EnergyShare));

    /// <summary>
    /// The level for one light, given its cut of the beat and its own band's shimmer.
    ///
    /// The largest of the three rather than their sum, because they are claims about
    /// the same light and not three lights: adding them would push a light whose band is
    /// busy on the beat past the ceiling, and clamping that away would silently flatten
    /// exactly the moments worth seeing. The beat wins wherever it is bigger, which is
    /// every beat; the shimmer fills the gaps between them; and the room's own energy is
    /// what everything falls back to when neither has anything to say.
    ///
    /// The shimmer is gated by loudness alone. It is what a passage with no kick in it
    /// has left, so gating it by how hard the kick hit would take away the one thing
    /// carrying the break.
    /// </summary>
    internal double LevelAt(double share, double shimmer) {
      return this.Above(Math.Max(
        Math.Max(this.Decay * share * this.PulseGate, shimmer * this.Loudness),
        this.Energy * EnergyShare));
    }

    /// <summary>
    /// A drive turned into a level, which is the one place the floor is applied.
    ///
    /// Loudness is already folded into whatever reaches here, because the pulse and the
    /// shimmer are gated differently and this cannot tell them apart.
    /// </summary>
    private double Above(double drive) {
      return this.Floor + ((this.Ceiling - this.Floor) * Math.Clamp(drive, 0, 1));
    }
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
  /// Needed because C# rounds toward zero, and the grid's bar number can be negative:
  /// it counts from the anchor, so the bars just before a fresh alignment are numbered
  /// below zero. With the language's own operators the colour cycle would reverse across
  /// that boundary.
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
