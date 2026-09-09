using Flux.Beat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// The light engine, with a fake clock in front of it and no bridge anywhere.
///
/// Every assertion here reads a colour, because a colour is the engine's entire
/// output - so the two things it decides are recovered from one: the largest channel
/// of a fully saturated colour is its brightness, and the ratio between the channels
/// is its hue. That is not a trick, it is the property that makes the engine testable
/// at all, and it is why <see cref="ILightSource.Render"/> takes no arguments and
/// returns a value instead of writing one somewhere.
/// </summary>
public class LightEngineTests {
  /// <summary>How close two hues have to be to count as the same one. Wide enough for the arithmetic, far narrower than anything visible.</summary>
  private const double HueTolerance = 1e-6;

  /// <summary>Where a beat's pulse has fallen to by the time the pulse is spent: the rest level plus a tenth of what was above it.</summary>
  private const double SpentBrightness = 0.15 + (0.85 * 0.1);

  [Fact]
  public void ABeatDrivesTheBrightnessToFullAndItFadesFromThere() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.BeatNow(index: 0);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 3);

    harness.Clock.Advance(TimeSpan.FromMilliseconds(240));
    Assert.Equal(SpentBrightness, Brightness(harness.Engine.Render()), 3);
  }

  /// <summary>
  /// The floor exists for the reason the stream's holding colour is a dim white rather
  /// than black: a light that goes out between beats looks exactly like a light that
  /// has stopped receiving anything, and between beats is most of the time.
  /// </summary>
  [Fact]
  public void ThePulseFadesToADimRoomRatherThanToDarkness() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.BeatNow(index: 0);
    harness.Engine.Render();

    harness.Clock.Advance(TimeSpan.FromSeconds(10));

    double brightness = Brightness(harness.Engine.Render());
    Assert.True(brightness > 0.14, $"A spent pulse left the lights at {brightness:F3}, which is off rather than dim.");
    Assert.True(brightness < 0.16, $"A pulse ten seconds old is still at {brightness:F3} and has not faded.");
  }

  /// <summary>
  /// Zero is a steady colour and not a one-frame flash. Both readings of "instant" are
  /// defensible and only one is useful: a 20 ms strobe at 50 Hz is nobody's setting,
  /// while holding the colour is what the change interval sounds like on its own.
  /// </summary>
  [Fact]
  public void NoPulseMeansASteadyColourRatherThanAFlash() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0 });

    harness.BeatNow(index: 0);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);

    harness.Clock.Advance(TimeSpan.FromSeconds(3));
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);
  }

  /// <summary>
  /// Full and not resting before the first beat, on the argument that chose the
  /// stream's holding colour: "no music yet" is exactly when somebody is standing in
  /// the room asking whether Flux has the lights, and a light at its floor cannot
  /// answer them.
  /// </summary>
  [Fact]
  public void TheLightsAreOnBeforeAnyBeatHasArrived() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);
  }

  /// <summary>
  /// The assertion behind the claim that adding two controls changed nothing about how
  /// the engine looks. Before they existed the envelope ran between a fixed floor of
  /// 0.15 and a fixed peak of 1.0, and full brightness at a depth of 85 is those two
  /// numbers arrived at from the other direction. If this ever fails, a default moved
  /// and somebody's tuned room moved with it.
  /// </summary>
  [Fact]
  public void TheDefaultsReproduceTheFixedEnvelopeFromBeforeTheControlsExisted() {
    Harness harness = new Harness(LightSettings.Default);

    harness.BeatNow(index: 0);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);

    harness.Clock.Advance(TimeSpan.FromSeconds(10));
    Assert.Equal(0.15, Brightness(harness.Engine.Render()), 6);
  }

  /// <summary>
  /// Brightness is the ceiling for everything sent, so a beat cannot drive the lights
  /// past it. Zero is a blackout and is allowed on purpose: it is a real thing to ask
  /// for, and the page says "off" at that end rather than leaving somebody wondering
  /// whether the stream died.
  /// </summary>
  [Theory]
  [InlineData(100, 1.0)]
  [InlineData(60, 0.6)]
  [InlineData(20, 0.2)]
  [InlineData(0, 0.0)]
  public void NothingTheEngineSendsIsBrighterThanTheBrightnessCeiling(int brightness, double ceiling) {
    Harness harness = new Harness(LightSettings.Default with { Brightness = brightness });

    harness.BeatNow(index: 0);
    Assert.Equal(ceiling, Brightness(harness.Engine.Render()), 6);

    // And nowhere in the fade either, which is the half a ceiling has to also cover.
    for(int frame = 0; frame < 30; frame++) {
      harness.Clock.Advance(TimeSpan.FromMilliseconds(20));

      Assert.True(Brightness(harness.Engine.Render()) <= ceiling + 1e-9);
    }
  }

  [Fact]
  public void ADepthOfNothingHoldsTheLightSteadyThroughTheBeat() {
    Harness harness = new Harness(LightSettings.Default with { PulseDepth = 0 });

    harness.BeatNow(index: 0);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);

    harness.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);
  }

  [Fact]
  public void AFullDepthFadesAllTheWayToNothing() {
    Harness harness = new Harness(LightSettings.Default with { PulseDepth = 100 });

    harness.BeatNow(index: 0);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);

    harness.Clock.Advance(TimeSpan.FromSeconds(10));
    Assert.Equal(0.0, Brightness(harness.Engine.Render()), 6);
  }

  /// <summary>
  /// The reason depth exists rather than a fixed floor, stated as a measurement. A
  /// 40 ms pulse is spent in two frames at 50 Hz, and under the old fixed floor that
  /// left the room sitting at 15 per cent almost all of the time - which reads as "the
  /// pulse does not do much brightness" rather than as a short pulse. A shallow depth
  /// puts the same quick flash on top of a bright room.
  /// </summary>
  [Fact]
  public void AShallowPulsePutsAQuickFlashOnABrightRoomRatherThanADarkOne() {
    Harness deep = new Harness(LightSettings.Default with { PulseMs = 40, PulseDepth = 85 });
    Harness shallow = new Harness(LightSettings.Default with { PulseMs = 40, PulseDepth = 20 });

    deep.BeatNow(index: 0);
    shallow.BeatNow(index: 0);
    deep.Engine.Render();
    shallow.Engine.Render();

    // A third of a beat at 130 BPM: long past a 40 ms pulse, and where the eye spends
    // almost all of its time.
    deep.Clock.Advance(TimeSpan.FromMilliseconds(150));
    shallow.Clock.Advance(TimeSpan.FromMilliseconds(150));

    Assert.Equal(0.15, Brightness(deep.Engine.Render()), 3);
    Assert.Equal(0.80, Brightness(shallow.Engine.Render()), 3);
  }

  [Fact]
  public void TheColourChangesOnTheDownbeatOfEveryNthBarAndNowhereElse() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    List<double> hues = [];
    for(long index = 0; index < 16; index++) {
      hues.Add(harness.SettledHue(index));
    }

    // Bars of four beats, changing on every second bar: beats 0 and 8 are downbeats of
    // bar 0 and bar 2. Everything else holds the colour it was given.
    for(long index = 1; index < 16; index++) {
      bool changed = Separation(hues[(int)index], hues[(int)index - 1]) > HueTolerance;

      Assert.Equal(index == 8, changed);
    }
  }

  /// <summary>
  /// "Off" removes the interval rather than the changes, which reads backwards and is
  /// the reference scene's own meaning: with no interval the colour changes as often
  /// as it can, which is every beat.
  /// </summary>
  [Fact]
  public void NoIntervalMeansANewColourOnEveryBeat() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 0, PulseMs: 0));

    double previous = harness.SettledHue(index: 0);

    for(long index = 1; index < 12; index++) {
      double hue = harness.SettledHue(index);

      Assert.True(Separation(hue, previous) > HueTolerance, $"Beat {index} did not change the colour.");
      previous = hue;
    }
  }

  /// <summary>
  /// The phase of the changes comes from the grid's absolute beat index and not from a
  /// count the engine keeps, which is what makes a dropped beat harmless: beats are
  /// dropped, by the bounded queue here and by a stream that was stopped when they
  /// arrived, and a running count would leave every colour change after one landing
  /// off the bar for the rest of the night.
  /// </summary>
  [Theory]
  [InlineData(0L, true)]
  [InlineData(4L, false)]
  [InlineData(8L, true)]
  [InlineData(12L, false)]
  [InlineData(16L, true)]
  public void WhichBeatChangesTheColourIsDecidedByItsIndexAndNotByHowManyCameBefore(long index, bool changes) {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    double before = HueOf(harness.Engine.Render());
    double after = harness.SettledHue(index);

    Assert.Equal(changes, Separation(after, before) > HueTolerance);
  }

  /// <summary>
  /// With no downbeat known there is no bar to land on, so the fallback is the scene's
  /// raw form - every n-th beat from wherever the numbering starts. Changing on a
  /// guessed phase is worth more than never changing, and an alignment arriving later
  /// takes the decision back.
  /// </summary>
  [Fact]
  public void WithoutAKnownDownbeatTheColourStillChangesOnEveryNthBeat() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 1, PulseMs: 0));

    List<double> hues = [];
    for(long index = 0; index < 9; index++) {
      hues.Add(harness.SettledHue(index, barAligned: false));
    }

    for(long index = 1; index < 9; index++) {
      bool changed = Separation(hues[(int)index], hues[(int)index - 1]) > HueTolerance;

      Assert.Equal(index % 4 == 0, changed);
    }
  }

  /// <summary>
  /// A palette is a constraint on the colour and not a suggestion, and this is the
  /// assertion that would catch a hue picked from the wrong table or divided by the
  /// wrong number.
  /// </summary>
  [Theory]
  [InlineData(LightPalette.Warm)]
  [InlineData(LightPalette.Cool)]
  [InlineData(LightPalette.Neon)]
  [InlineData(LightPalette.Sunset)]
  [InlineData(LightPalette.Ocean)]
  [InlineData(LightPalette.Forest)]
  [InlineData(LightPalette.Party)]
  public void EveryColourAPaletteProducesIsOneOfItsOwn(LightPalette palette) {
    Harness harness = new Harness(new LightSettings(palette, ColorChangeBars: 0, PulseMs: 0));
    IReadOnlyList<int> allowed = LightPalettes.HuesOf(palette)!;

    for(long index = 0; index < 40; index++) {
      double hue = harness.SettledHue(index);

      Assert.Contains(allowed, degrees => Separation(degrees / 360.0, hue) <= HueTolerance);
    }
  }

  /// <summary>
  /// The tight palettes are the interesting case and the reason the engine keeps the
  /// farthest candidate rather than the last one. <c>Ocean</c> spans fifty degrees in
  /// total, so no two of its hues are the required 0.12 apart and every one of the
  /// fifty attempts fails - taking whichever was tried last can then hand back the
  /// colour already on screen, and a change nobody can see is the one thing the
  /// separation rule exists to prevent.
  /// </summary>
  [Fact]
  public void ATightPaletteStillChangesColourEvenThoughItCannotSatisfyTheSeparation() {
    Harness harness = new Harness(new LightSettings(LightPalette.Ocean, ColorChangeBars: 0, PulseMs: 0));

    double previous = harness.SettledHue(index: 0);

    for(long index = 1; index < 20; index++) {
      double hue = harness.SettledHue(index);

      Assert.True(Separation(hue, previous) > HueTolerance, $"Ocean repeated its colour on beat {index}.");
      previous = hue;
    }
  }

  /// <summary>
  /// A colour slides to its target rather than jumping, so a frame taken immediately
  /// after a change is between the two rather than at either.
  /// </summary>
  [Fact]
  public void TheColourSlidesTowardItsTargetInsteadOfJumping() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 0, PulseMs: 0));

    double first = harness.SettledHue(index: 0);

    harness.BeatNow(index: 1);
    harness.Clock.Advance(TimeSpan.FromMilliseconds(20));
    double moving = HueOf(harness.Engine.Render());

    harness.Clock.Advance(TimeSpan.FromSeconds(2));
    double settled = HueOf(harness.Engine.Render());

    Assert.True(Separation(moving, first) > HueTolerance, "The colour had not started moving one frame after the beat.");
    Assert.True(Separation(moving, settled) > HueTolerance, "The colour arrived instantly rather than sliding.");
  }

  /// <summary>
  /// The offset's whole job, and the direction the document promises: positive sends
  /// light early, because the bridge, the Zigbee hop and the bulb each take their
  /// share and a change sent the instant a beat lands is seen late.
  ///
  /// Asserted against a decayed pulse rather than from a standing start, because a
  /// fresh engine renders at full brightness by design - so "the beat has not landed
  /// yet" is only distinguishable once there is an envelope to be low in.
  /// </summary>
  [Fact]
  public void APositiveOffsetLandsTheBeatEarly() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 }, offsetMs: 100);

    harness.BeatNow(index: 0);
    harness.Engine.Render();
    harness.Clock.Advance(TimeSpan.FromSeconds(2));

    double due = harness.Clock.NowMicros + (1000.0 * 1000.0);
    harness.Engine.OnBeat(Beat(index: 4, scheduledMicros: due));

    // 150 ms before the beat is due, which a 100 ms offset does not reach.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(850));
    AssertSpent(harness.Engine.Render());

    // 90 ms before, which it does.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(60));
    AssertJustFired(harness.Engine.Render());
  }

  [Fact]
  public void ANegativeOffsetHoldsTheBeatBack() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 }, offsetMs: -100);

    harness.BeatNow(index: 0);
    harness.Engine.Render();
    harness.Clock.Advance(TimeSpan.FromSeconds(2));

    double due = harness.Clock.NowMicros + (1000.0 * 1000.0);
    harness.Engine.OnBeat(Beat(index: 4, scheduledMicros: due));

    // The moment the beat was aimed at, which a negative offset has not reached.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(1000));
    AssertSpent(harness.Engine.Render());

    harness.Clock.Advance(TimeSpan.FromMilliseconds(110));
    AssertJustFired(harness.Engine.Render());
  }

  /// <summary>
  /// The engine reads the settings on every frame rather than caching them, which is
  /// what a slider being tuned by eye with music playing actually requires.
  /// </summary>
  [Fact]
  public void ChangingTheSettingsTakesEffectOnTheNextFrame() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.BeatNow(index: 0);
    harness.Engine.Render();
    harness.Clock.Advance(TimeSpan.FromSeconds(2));

    Assert.Equal(0.15, Brightness(harness.Engine.Render()), 2);

    Assert.True(harness.Bridge.TrySetLightSettings(LightSettings.Default with { PulseMs = 0 }));

    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);
  }

  /// <summary>
  /// Beats keep arriving while nothing is draining them, which is the ordinary state
  /// of a stream somebody has stopped. The queue is bounded for that, and what matters
  /// is that overflowing it costs frames rather than correctness.
  /// </summary>
  [Fact]
  public void BeatsArrivingWithNothingRenderingThemAreDroppedRatherThanHoarded() {
    Harness harness = new Harness(new LightSettings(LightPalette.Party, ColorChangeBars: 0, PulseMs: 240));

    for(long index = 0; index < 200; index++) {
      harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
      harness.Engine.OnBeat(Beat(index, harness.Clock.NowMicros));
    }

    HueColor color = harness.Engine.Render();

    Assert.Equal(1.0, Brightness(color), 2);
    Assert.Contains(LightPalettes.HuesOf(LightPalette.Party)!, degrees => Separation(degrees / 360.0, HueOf(color)) <= HueTolerance);
  }

  /// <summary>
  /// The bridge is sent three channels and every one of them has to be a real number
  /// inside its range, whatever the envelope did on the way. This is the assertion that
  /// would catch a wrapped hue or a negative brightness, which reach a room as one
  /// wrong light for one frame and never reproduce.
  /// </summary>
  [Fact]
  public void EveryColourItEverSendsIsInsideItsRange() {
    Harness harness = new Harness(new LightSettings(LightPalette.Party, ColorChangeBars: 1, PulseMs: 240));

    for(long index = 0; index < 400; index++) {
      harness.Engine.OnBeat(Beat(index, harness.Clock.NowMicros, positionInBar: (int)(index % 4) + 1));

      for(int frame = 0; frame < 5; frame++) {
        harness.Clock.Advance(TimeSpan.FromMilliseconds(20));

        HueColor color = harness.Engine.Render();

        Assert.InRange(color.R, 0, 1);
        Assert.InRange(color.G, 0, 1);
        Assert.InRange(color.B, 0, 1);
      }
    }
  }

  /// <summary>
  /// Started and stopped without a scheduler thread anywhere, which is all the hosted
  /// service has to do: it holds no device and opens nothing, and the subscription is
  /// the entire reason it is one.
  /// </summary>
  [Fact]
  public async Task ItSubscribesOnStartAndComesOffOnStop() {
    Harness harness = new Harness();

    await harness.Engine.StartAsync(TestContext.Current.CancellationToken);
    await harness.Engine.StopAsync(TestContext.Current.CancellationToken);
  }

  /// <summary>
  /// That a pulse has just started, asserted as a threshold rather than as a value.
  ///
  /// The exact brightness depends on how many milliseconds past the trigger the frame
  /// landed, which is the thing under test in the offset cases - so pinning it would
  /// mean asserting the answer to the question being asked.
  /// </summary>
  private static void AssertJustFired(HueColor color) {
    double brightness = Brightness(color);

    Assert.True(brightness > 0.9, $"The pulse should have just fired and the lights are at {brightness:F3}.");
  }

  /// <summary>That no pulse is in flight, so the lights are back at their resting level.</summary>
  private static void AssertSpent(HueColor color) {
    double brightness = Brightness(color);

    Assert.True(brightness < 0.2, $"No pulse was due and the lights are at {brightness:F3}.");
  }

  private static BeatEvent Beat(long index, double scheduledMicros, int positionInBar = 1, bool barAligned = true) {
    return new BeatEvent(index, positionInBar, barAligned, Bpm: 120, scheduledMicros);
  }

  /// <summary>The largest channel of a fully saturated colour, which is its brightness.</summary>
  private static double Brightness(HueColor color) {
    return Math.Max(color.R, Math.Max(color.G, color.B));
  }

  /// <summary>The hue a colour represents, recovered from the ratio between its channels.</summary>
  private static double HueOf(HueColor color) {
    double max = Math.Max(color.R, Math.Max(color.G, color.B));
    double min = Math.Min(color.R, Math.Min(color.G, color.B));
    double range = max - min;

    if(range <= 0) {
      return 0;
    }

    double sixth = max == color.R
      ? (((color.G - color.B) / range) + 6) % 6
      : max == color.G
        ? ((color.B - color.R) / range) + 2
        : ((color.R - color.G) / range) + 4;

    return sixth / 6.0;
  }

  /// <summary>
  /// The shorter of the two ways round the wheel, which is the only comparison that
  /// works here: 0.999 and 0.001 are neighbours and both are red, and subtracting them
  /// says they are almost a full turn apart.
  /// </summary>
  private static double Separation(double first, double second) {
    double difference = Math.Abs(first - second);

    return Math.Min(difference, 1 - difference);
  }

  private sealed class Harness {
    internal Harness(LightSettings? light = null, int offsetMs = 0, int seed = 20260909) {
      Settings.Stored = HueSettings.Empty with { Light = light, LatencyOffsetMs = offsetMs };

      Bridge = new HueBridgeService(
        new FakeHueBridgeClient(),
        new InMemoryCredentialStore(),
        Settings,
        Options.Create(new HueOptions()),
        NullLogger<HueBridgeService>.Instance);

      // A real scheduler, never started. The engine needs one to subscribe to and the
      // tests hand it beats directly, so nothing here runs a thread or invents a tempo.
      BeatScheduler scheduler = new BeatScheduler(
        new BeatGrid(Clock, quantum: 4, TimeSpan.FromSeconds(2)),
        Clock,
        NullLogger<BeatScheduler>.Instance);

      Engine = new LightEngine(scheduler, Bridge, Clock, NullLogger<LightEngine>.Instance, new Random(seed));
    }

    internal FakeClock Clock { get; } = new FakeClock();

    internal InMemorySettingsStore Settings { get; } = new InMemorySettingsStore();

    internal HueBridgeService Bridge { get; }

    internal LightEngine Engine { get; }

    /// <summary>Delivers a beat aimed at this instant.</summary>
    internal void BeatNow(long index, int positionInBar = 1, bool barAligned = true) {
      Engine.OnBeat(Beat(index, Clock.NowMicros, positionInBar, barAligned));
    }

    /// <summary>
    /// The hue after a beat has been rendered and the transition has finished.
    ///
    /// Two seconds is well past the point where the slide clamps and lands exactly on
    /// its target, which is what makes "did the colour change" a question with a yes or
    /// no answer rather than one about how far it had got.
    /// </summary>
    internal double SettledHue(long index, bool barAligned = true) {
      BeatNow(index, positionInBar: barAligned ? (int)FloorMod(index, 4) + 1 : 1, barAligned);
      Engine.Render();

      Clock.Advance(TimeSpan.FromSeconds(2));

      return HueOf(Engine.Render());
    }

    private static long FloorMod(long value, long divisor) {
      long remainder = value % divisor;

      return remainder < 0 ? remainder + divisor : remainder;
    }
  }
}
