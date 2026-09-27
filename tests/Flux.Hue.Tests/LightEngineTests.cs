using Flux.Audio;
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
  /// The peak has width, because a bulb is not a monitor: the bridge forwards at about
  /// 25 updates a second, so a decay that starts falling the instant the beat lands has
  /// its top resampled away before any bulb renders it. The pulse holds for two bridge
  /// frames and falls over what is left - and the test above pins the other half of the
  /// arrangement, that hold plus fall still spans exactly the slider's number.
  /// </summary>
  [Fact]
  public void ThePulseHoldsItsPeakBeforeTheFadeBegins() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.BeatNow(index: 0);
    harness.Engine.Render();

    // Inside the 80 ms hold, where the old curve had already fallen to less than half.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(70));
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 3);

    // Well into the fall: below the peak, nowhere near spent.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(70));
    double falling = Brightness(harness.Engine.Render());
    Assert.True(falling < 0.99, $"The pulse is still at {falling:F3} past its hold, so it is not falling.");
    Assert.True(falling > SpentBrightness, $"The pulse fell to {falling:F3} in 140 ms, so the hold did not happen.");
  }

  /// <summary>
  /// The frame that consumes a beat runs ahead of the audio - the latency offset shifts
  /// it early on purpose - so the kick the beat announces has usually not reached the
  /// analyzer at the moment the strength is first read. The strength may rise while the
  /// pulse is alive, so the hit counts when it lands instead of being judged by the gap
  /// before it.
  /// </summary>
  [Fact]
  public void AKickThatLandsAfterTheBeatIsConsumedStillCountsForThePulse() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    // A background for the kick to stand out from, and a beat consumed on a frame where
    // the kick band is doing nothing unusual: the gate reads it as no hit at all.
    harness.Heard.Bands = new SpectrumLevels(Kick: 0.4, Mids: 0.3, Highs: 0.3);
    harness.BeatNow(index: 0);
    Assert.Equal(0.15, Brightness(harness.Engine.Render()), 3);

    // Two stream frames later the kick arrives, and the pulse takes its full height.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(40));
    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 0.3, Highs: 0.3);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 3);
  }

  /// <summary>
  /// The other half of the ratchet's contract: it can only pass a hit along, never
  /// invent one. A beat in a passage where the kick band sits flat stays gated shut
  /// however long the pulse waits, which is what keeps a breakdown dark.
  /// </summary>
  [Fact]
  public void AKickThatNeverArrivesLeavesThePulseGatedShut() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 0.4, Mids: 0.3, Highs: 0.3);
    harness.BeatNow(index: 0);
    harness.Engine.Render();

    harness.Clock.Advance(TimeSpan.FromMilliseconds(40));
    Assert.Equal(0.15, Brightness(harness.Engine.Render()), 3);
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

  /// <summary>
  /// A full depth fades to the lowest setting rather than to nothing, which is a change
  /// of mind made on 2026-09-11 while looking at the lights: they should never go out,
  /// only down. A light that is off is indistinguishable from a light that has stopped
  /// receiving anything, and between beats is most of the time.
  /// </summary>
  [Fact]
  public void AFullDepthFadesToTheLowestSettingRatherThanToNothing() {
    Harness harness = new Harness(LightSettings.Default with { PulseDepth = 100 });

    harness.BeatNow(index: 0);
    Assert.Equal(1.0, Brightness(harness.Engine.Render()), 6);

    harness.Clock.Advance(TimeSpan.FromSeconds(10));
    Assert.Equal(0.05, Brightness(harness.Engine.Render()), 6);
  }

  /// <summary>
  /// A blackout is still black. Somebody who sets the brightness to zero is asking for no
  /// light rather than for a little, and a floor that ignored that would be the setting
  /// refusing the request.
  /// </summary>
  [Fact]
  public void ABlackoutIsStillBlack() {
    Harness harness = new Harness(LightSettings.Default with { Brightness = 0, PulseDepth = 100 });

    harness.BeatNow(index: 0);
    Assert.Equal(0.0, Brightness(harness.Engine.Render()), 6);

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
  /// A phrase starting on a downbeat the bar count would pass over - beat 12 is bar 3,
  /// and the count changes on even bars - takes a colour, and the count runs on from it:
  /// two bars after the phrase, not two after the anchor.
  /// </summary>
  [Fact]
  public void APhraseStartChangesTheColourAndRestartsTheBarCount() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));
    harness.OfferPhrases(endIndex: 400, 0, 12);

    Assert.Equal([8, 12, 20, 28], harness.ChangesOnTime(32));
  }

  [Fact]
  public void WithPhrasesSwitchedOffTheBarCountRunsAsItAlwaysHas() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0, FollowPhrases: false));
    harness.OfferPhrases(endIndex: 400, 0, 12);

    Assert.Equal([8, 16, 24], harness.ChangesOnTime(32));
  }

  [Fact]
  public void PastTheLastPhraseTheBarCountTakesOverAgain() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));
    harness.OfferPhrases(endIndex: 20, 0, 12);

    Assert.Equal([8, 12, 24], harness.ChangesOnTime(32));
  }

  /// <summary>
  /// The phrases are rekordbox's and the beats may be Link's, a few milliseconds apart, so
  /// a phrase start either side of its beat has to be that beat's.
  /// </summary>
  [Theory]
  [InlineData(-0.06)]
  [InlineData(0.06)]
  public void APhraseStartAHairOffItsBeatStillBelongsToIt(double offBeats) {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));
    harness.OfferPhrases(endIndex: 400, 0, 12 + offBeats);

    Assert.Equal([8, 12, 20, 28], harness.ChangesOnTime(32));
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
  /// The phase of the changes comes from the bar number the grid stamps on each beat,
  /// and not from a count the engine keeps, which is what makes a dropped beat harmless:
  /// beats are dropped, by the bounded queue here and by a stream that was stopped when
  /// they arrived, and a running count would leave every colour change after one landing
  /// off the bar for the rest of the night.
  /// </summary>
  [Theory]
  [InlineData(4L, false)]
  [InlineData(8L, true)]
  [InlineData(12L, false)]
  [InlineData(16L, true)]
  public void WhichBeatChangesTheColourIsDecidedByItsIndexAndNotByHowManyCameBefore(long index, bool changes) {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    harness.SettledHue(0);

    double before = HueOf(harness.Engine.Render());
    double after = harness.SettledHue(index);

    Assert.Equal(changes, Separation(after, before) > HueTolerance);
  }

  /// <summary>
  /// Pressing anchor means "this is bar one", and it has to mean that for everything the
  /// bar count drives or it only half means it. This press is one beat off the previous
  /// phase, and early: the anchored beat has not arrived yet.
  /// </summary>
  [Fact]
  public void AnchoringRestartsTheColourCycleFromTheBarThatWasMarked() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    double before = harness.SettledHue(0);

    harness.Anchor(9, harness.Clock.NowMicros);

    double anchored = harness.SettledHue(9);
    Assert.True(Separation(anchored, before) > HueTolerance, "The anchored bar is bar one, so it takes a colour.");

    // Two bars on from the new anchor, and not two bars on from the old one. Index 13 is
    // one bar past 9 and must be quiet; 17 is two and must change.
    double oneBarOn = harness.SettledHue(13);
    Assert.True(Separation(oneBarOn, anchored) <= HueTolerance, "One bar after the anchor is not a change.");

    double twoBarsOn = harness.SettledHue(17);
    Assert.True(Separation(twoBarsOn, oneBarOn) > HueTolerance, "Two bars after the anchor is a change.");
  }

  /// <summary>
  /// The press that was missed until 2026-09-24: on a beat that is already a downbeat,
  /// but in a bar of the cycle that does not change colour. Nothing about the bar's
  /// phase moves, so an engine inferring the anchor from the downbeats it sees has no
  /// way to notice it - the readout said bar one and the colours kept the old count.
  /// </summary>
  [Fact]
  public void AnchoringOnADownbeatMidCycleRestartsTheCycleToo() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    harness.SettledHue(0);
    double quiet = harness.SettledHue(4);

    // Bar 3 of the old count, which would not have changed.
    harness.Anchor(12, harness.Clock.NowMicros);

    double anchored = harness.SettledHue(12);
    Assert.True(Separation(anchored, quiet) > HueTolerance, "The anchored bar takes a colour.");

    // Bar 4 of the old count would have changed; it is bar 1 of the new one.
    double oneBarOn = harness.SettledHue(16);
    Assert.True(Separation(oneBarOn, anchored) <= HueTolerance, "One bar after the anchor is not a change.");

    double twoBarsOn = harness.SettledHue(20);
    Assert.True(Separation(twoBarsOn, oneBarOn) > HueTolerance, "Two bars after the anchor is a change.");
  }

  /// <summary>
  /// A press a hair after the kick rounds to the beat that just played, and that beat
  /// has already been rendered carrying the old count. Waiting for the count would put
  /// the answer a bar later; the colour changes on the next frame instead.
  /// </summary>
  [Fact]
  public void APressJustAfterTheBeatChangesTheColourOnTheNextFrame() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    harness.SettledHue(0);
    double beat = harness.Clock.NowMicros;
    double quiet = harness.SettledHue(4);

    harness.Anchor(4, beat);

    double answered = harness.Settle();
    Assert.True(Separation(answered, quiet) > HueTolerance, "The press is answered without waiting for a beat.");

    double oneBarOn = harness.SettledHue(8);
    Assert.True(Separation(oneBarOn, answered) <= HueTolerance, "One bar after the anchor is not a change.");

    double twoBarsOn = harness.SettledHue(12);
    Assert.True(Separation(twoBarsOn, oneBarOn) > HueTolerance, "Two bars after the anchor is a change.");
  }

  /// <summary>
  /// A drop snaps to the downbeat it rounds to, which can be a beat or more behind the
  /// last one rendered. Answering that late would change the colour partway through the
  /// bar, so the restart is dropped and the count carries on from the new anchor.
  /// </summary>
  [Fact]
  public void ARestartOwedToABeatAlreadyPastDoesNotChangeTheColourMidBar() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    harness.SettledHue(0);
    double downbeat = harness.Clock.NowMicros;
    harness.SettledHue(4);
    double pastIt = harness.SettledHue(5);

    harness.Anchor(4, downbeat);

    double answered = harness.Settle();
    Assert.True(Separation(answered, pastIt) <= HueTolerance, "A restart a beat late is not answered mid-bar.");

    double oneBarOn = harness.SettledHue(8);
    Assert.True(Separation(oneBarOn, answered) <= HueTolerance, "One bar after the anchor is not a change.");

    double twoBarsOn = harness.SettledHue(12);
    Assert.True(Separation(twoBarsOn, oneBarOn) > HueTolerance, "Two bars after the anchor is a change.");
  }

  /// <summary>
  /// A positive offset runs the frame path ahead of the clock, so the anchored moment
  /// passes on the frame path before its beat has arrived. Answering on the clock would
  /// change the colour there and again when the beat landed; the answer waits for the beat.
  /// </summary>
  [Fact]
  public void AnEarlyPressUnderAPositiveOffsetWaitsForItsBeat() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0), offsetMs: 400);

    double before = harness.SettledHue(0);

    harness.Anchor(4, harness.Clock.NowMicros + (300 * 1000.0));

    double first = HueOf(harness.Engine.Render());
    harness.Clock.Advance(TimeSpan.FromMilliseconds(100));
    double second = HueOf(harness.Engine.Render());

    Assert.True(Separation(first, before) <= HueTolerance, "Nothing changes before the beat arrives.");
    Assert.True(Separation(second, first) <= HueTolerance, "Nothing changes before the beat arrives.");

    harness.Clock.Advance(TimeSpan.FromMilliseconds(200));
    double anchored = harness.SettledHue(4);

    Assert.True(Separation(anchored, before) > HueTolerance, "The anchored beat takes a colour when it lands.");
  }

  /// <summary>
  /// A source that goes quiet and comes back does not move the grid's anchor, and the
  /// beats carry the grid's bar, so the count carries on rather than restarting wherever
  /// the dropouts happened to fall.
  /// </summary>
  [Fact]
  public void AReAlignmentOnTheSamePhaseLeavesTheCountWhereItWas() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0));

    harness.SettledHue(0);

    double before = harness.SettledHue(4);

    Assert.True(Separation(before, HueOf(harness.Engine.Render())) <= HueTolerance, "One bar in is not a change.");
  }

  /// <summary>
  /// How long a bar is comes from the grid's own quantum rather than from a four
  /// written into the engine.
  ///
  /// At eight beats to the bar a downbeat lands every eight indices, so dividing by four
  /// steps the bar count twice per bar: "every two bars" becomes every four, or stops
  /// happening at all, depending on where the grid's anchor fell. Four is still the
  /// default and nothing about the tuned behaviour moves - this is the setting being
  /// honoured rather than a value being re-derived.
  /// </summary>
  [Theory]
  [InlineData(8L, false)]
  [InlineData(16L, true)]
  [InlineData(24L, false)]
  public void HowLongABarIsComesFromTheQuantumAndNotFromAFourInTheEngine(long index, bool changes) {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 2, PulseMs: 0), quantum: 8);

    harness.SettledHue(0);

    double before = HueOf(harness.Engine.Render());
    double after = harness.SettledHue(index);

    Assert.Equal(changes, Separation(after, before) > HueTolerance);
  }

  /// <summary>The fallback counts in the same unit, for the same reason.</summary>
  [Fact]
  public void TheFallbackWithoutADownbeatCountsTheQuantumToo() {
    Harness harness = new Harness(new LightSettings(LightPalette.Random, ColorChangeBars: 1, PulseMs: 0), quantum: 8);

    List<double> hues = [];
    for(long index = 0; index < 17; index++) {
      hues.Add(harness.SettledHue(index, barAligned: false));
    }

    for(long index = 1; index < 17; index++) {
      bool changed = Separation(hues[(int)index], hues[(int)index - 1]) > HueTolerance;

      Assert.Equal(index % 8 == 0, changed);
    }
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
  /// A tight palette is the interesting case and the reason the engine keeps the
  /// farthest candidate rather than the last one. <c>Ocean</c> spans fifty degrees in
  /// total, where the required 0.12 is forty-three, so only its two extremes clear the
  /// threshold and from the other six hues every one of the fifty attempts fails -
  /// taking whichever was tried last can then hand back the colour already on screen,
  /// and a change nobody can see is the one thing the separation rule exists to
  /// prevent.
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
    harness.Engine.OnBeat(harness.BeatAt(index: 4, scheduledMicros: due));

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
    harness.Engine.OnBeat(harness.BeatAt(index: 4, scheduledMicros: due));

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
      harness.Engine.OnBeat(harness.BeatAt(index, harness.Clock.NowMicros));
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
      harness.BeatNow(index);

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

  /// <summary>
  /// The scene's strobe on Flux's grid: at 120 BPM a beat is 500 ms, so a cycle of 50
  /// per cent is 250 ms and its flash the first quarter of that. The pulse is off so the
  /// room between flashes is one steady saturated colour, which no flash can be mistaken
  /// for.
  /// </summary>
  [Fact]
  public void TheStrobeFlashesWhiteForAQuarterOfEachCycleAndLeavesTheRoomBetween() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0, Strobe = true, StrobeCycle = 50 });

    harness.BeatNow(index: 0);
    Assert.True(IsWhite(harness.Engine.Render(), 1.0), "The beat itself is the start of a flash.");

    harness.Clock.Advance(TimeSpan.FromMilliseconds(50));
    Assert.True(IsWhite(harness.Engine.Render(), 1.0), "50 ms is inside the first 62.5 ms of the cycle.");

    harness.Clock.Advance(TimeSpan.FromMilliseconds(50));
    Assert.False(IsWhite(harness.Engine.Render(), 1.0), "100 ms is past the flash and before the next cycle.");

    harness.Clock.Advance(TimeSpan.FromMilliseconds(160));
    Assert.True(IsWhite(harness.Engine.Render(), 1.0), "260 ms is 10 ms into the second cycle of the beat.");

    harness.Clock.Advance(TimeSpan.FromMilliseconds(70));
    Assert.False(IsWhite(harness.Engine.Render(), 1.0), "330 ms is past the second flash.");
  }

  [Fact]
  public void TheStrobeIsOffUnlessSomebodyAsksForIt() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0 });

    harness.BeatNow(index: 0);

    Assert.False(IsWhite(harness.Engine.Render(), 1.0));
  }

  /// <summary>
  /// The hold and the setting are one strobe: holding runs the same cycle with the
  /// setting off, and letting go returns the room at once rather than at the end of a
  /// cycle.
  /// </summary>
  [Fact]
  public void HoldingTheStrobeFlashesWithTheSettingOffAndLettingGoStopsIt() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0 });
    object finger = new object();

    harness.Bridge.Strobe.Press(finger);
    harness.BeatNow(index: 0);
    Assert.True(IsWhite(harness.Engine.Render(), 1.0));

    harness.Bridge.Strobe.Release(finger);
    Assert.False(IsWhite(harness.Engine.Render(), 1.0));
  }

  /// <summary>
  /// The brightness ceiling holds through a flash - it is the control somebody reaches
  /// for first - and a blackout stays black rather than becoming a strobe.
  /// </summary>
  [Theory]
  [InlineData(40, 0.4)]
  [InlineData(0, 0.0)]
  public void AStrobeFlashIsNoBrighterThanTheRoomIsAllowedToGet(int brightness, double ceiling) {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0, Brightness = brightness, Strobe = true });

    harness.BeatNow(index: 0);

    Assert.True(IsWhite(harness.Engine.Render(), ceiling));
  }

  /// <summary>
  /// No grid, no strobe: before the first beat there is nothing to flash on, and once
  /// the beats stop for two beat lengths the strobe stops with them rather than
  /// extrapolating a grid that is no longer there. 1010 ms after a beat at 120 BPM is
  /// 10 ms into a cycle, so a strobe still running would be mid-flash.
  /// </summary>
  [Fact]
  public void TheStrobeNeedsABeatToFlashOnAndStopsWhenTheBeatsDo() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0, Strobe = true, StrobeCycle = 50 });

    Assert.False(IsWhite(harness.Engine.Render(), 1.0), "No beat has arrived, so there is no grid.");

    harness.BeatNow(index: 0);
    harness.Engine.Render();

    harness.Clock.Advance(TimeSpan.FromMilliseconds(1010));
    Assert.False(IsWhite(harness.Engine.Render(), 1.0), "Two beat lengths have passed with no beat.");
  }

  /// <summary>Whether a colour is white at the given level: every channel equal, and at it.</summary>
  private static bool IsWhite(HueColor color, double level) {
    return Math.Abs(color.R - level) < 1e-9
      && Math.Abs(color.G - level) < 1e-9
      && Math.Abs(color.B - level) < 1e-9;
  }

  /// <summary>The largest channel of a fully saturated colour, which is its brightness.</summary>
  /// <summary>
  /// The step's whole claim, in one assertion: on a kick-heavy frame the lights nearest
  /// the centre of the room take the beat and the outer ones sit still.
  ///
  /// The history is established first and that is not a formality. Each band is measured
  /// against its own recent loudest, so a bare frame with nothing before it reads every
  /// band as being at its peak and every light leads - see <see cref="BandDynamics"/>.
  /// What this sets up is the ordinary case: a track whose kick is habitually louder
  /// than its top end, on a frame where the kick is hitting and the top is not.
  /// </summary>
  [Fact]
  public void ALightNearTheCentreTakesTheKickWhileOneAtTheEdgeTakesTheTop() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 0.3, Mids: 0.5, Highs: 0.5);
    RenderRoom(harness, 0, 0.5, 1);

    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 0.25, Highs: 0.05);
    harness.BeatNow(index: 0);

    (HueColor shared, HueColor[] lights) = RenderRoom(harness, 0, 0.5, 1);

    Assert.Equal(1.0, Brightness(lights[0]), 3);
    Assert.Equal(0.575, Brightness(lights[1]), 3);

    // The edge light's band is nearly silent, so its cut of the beat is nothing - what
    // holds it up on a frame this much hotter than the history is the room's own energy,
    // which lifts every light alike and reads here as the resting level rather than as a
    // share of the pulse.
    Assert.Equal(0.49, Brightness(lights[2]), 3);

    // The room's own colour is the full pulse, not the average of the three: it is what
    // the engine decided this beat was worth before any light's position was consulted.
    Assert.Equal(1.0, Brightness(shared), 3);
  }

  /// <summary>
  /// <b>The ordering has to be able to invert, or there is no information in it.</b>
  ///
  /// This is the defect the per-band measurement was built for, found by reading sixty
  /// frames off the live socket on 2026-09-11: comparing the raw band levels made the
  /// kick the leader on essentially every frame of every track, so the room was a fixed
  /// bright-in-the-middle gradient that pulsed in unison and never rearranged. Here the
  /// kick is still four times louder than the top in absolute terms and the outer light
  /// leads anyway, because the top is at the top of its own range and the kick is not.
  /// </summary>
  [Fact]
  public void ABandAtTheTopOfItsOwnRangeLeadsEvenWhileAnotherIsLouder() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 0.5, Highs: 0.1);
    RenderRoom(harness, 0, 1);

    // A break: the kick has dropped to a fifth of what it does, and the hats are the only
    // thing still hitting.
    harness.Heard.Bands = new SpectrumLevels(Kick: 0.2, Mids: 0.1, Highs: 0.25);
    harness.BeatNow(index: 0);

    (_, HueColor[] lights) = RenderRoom(harness, 0, 1);

    Assert.True(
      Brightness(lights[1]) > Brightness(lights[0]),
      $"The outer light is at {Brightness(lights[1]):F3} and the centre at {Brightness(lights[0]):F3}, so the ordering did not invert.");
  }

  /// <summary>
  /// A light whose band is busy stays lifted between beats, which is the difference
  /// between a room the music moves through and a set of simultaneous flashes at
  /// different heights.
  ///
  /// Measured well after the pulse is spent, so nothing of the beat is left to account
  /// for it: whatever is holding the light up is its own band and nothing else.
  /// </summary>
  [Fact]
  public void ALightWhoseBandIsHittingStaysLiftedBetweenBeats() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 1, Highs: 0.2);
    harness.BeatNow(index: 0);
    RenderRoom(harness, 0, 1);

    // Past the pulse, and the top end does something the bottom end does not.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(500));
    harness.Heard.Bands = new SpectrumLevels(Kick: 0, Mids: 0, Highs: 0.6);

    (_, HueColor[] lights) = RenderRoom(harness, 0, 1);

    Assert.True(Brightness(lights[0]) < 0.2, $"The silent band should have fallen back to resting and is at {Brightness(lights[0]):F3}.");
    Assert.True(Brightness(lights[1]) > 0.3, $"The band that is hitting should still be lifted and is at {Brightness(lights[1]):F3}.");
  }

  /// <summary>
  /// <b>It is the hitting and not the loudness that lifts a light between beats.</b> That
  /// distinction is the second correction made to this on 2026-09-11: driving the lift
  /// from how loud a band is moves every light together, because a band's level changes
  /// slowly and all three change together, so the room went on reading as every light
  /// doing the same thing at different heights. A sustained tone contributes nothing here
  /// however loud it is, and what keeps those lights alive is the beat share and the
  /// floor.
  /// </summary>
  [Fact]
  public void ABandThatIsLoudButSteadyDoesNotLiftItsLightBetweenBeats() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 1, Highs: 1);
    harness.BeatNow(index: 0);
    RenderRoom(harness, 0, 1);

    // Same levels, still at full, and a whole second of nothing changing.
    harness.Clock.Advance(TimeSpan.FromMilliseconds(1000));

    (_, HueColor[] lights) = RenderRoom(harness, 0, 1);

    Assert.All(lights, light => Assert.True(
      Brightness(light) < 0.2,
      $"A band holding steady lifted its light to {Brightness(light):F3}, so the lift is reading loudness rather than movement."));
  }

  /// <summary>
  /// The floor is the reason a light's own band scales the pulse and never the resting
  /// level. A light that goes out looks exactly like a light that has stopped receiving
  /// anything, which is the argument the floor already existed for.
  /// </summary>
  [Fact]
  public void ALightWhoseBandIsSilentRestsRatherThanGoingOut() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });
    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 0, Highs: 0);

    harness.BeatNow(index: 0);

    (_, HueColor[] lights) = RenderRoom(harness, 1);

    double brightness = Brightness(lights[0]);
    Assert.True(brightness > 0.14, $"A light listening to a silent band is at {brightness:F3}, which is off rather than resting.");
    Assert.True(brightness < 0.16, $"A light listening to a silent band is at {brightness:F3} and took the beat anyway.");
  }

  /// <summary>
  /// One palette, one colour, and position decides only how hard the beat lands. Eight
  /// hues at once is a different product and is deliberately not this one.
  /// </summary>
  [Fact]
  public void EveryLightIsOnTheSameHueWhateverItListensTo() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });
    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 0.5, Highs: 0.2);

    harness.BeatNow(index: 0);

    (HueColor shared, HueColor[] lights) = RenderRoom(harness, 0, 0.5, 1);

    Assert.Equal(HueOf(shared), HueOf(lights[0]), HueTolerance);
    Assert.Equal(HueOf(shared), HueOf(lights[1]), HueTolerance);
    Assert.Equal(HueOf(shared), HueOf(lights[2]), HueTolerance);
  }

  /// <summary>
  /// A beat during a breakdown is still a beat, and a room that answers it as hard as it
  /// answers the drop is reacting to the grid rather than to the music.
  /// </summary>
  [Fact]
  public void AQuietPassagePulsesLessHardThanTheDropBeforeIt() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    // A background for the kick to stand out from, or nothing stands out from anything.
    harness.Heard.Bands = new SpectrumLevels(Kick: 0.1, Mids: 0.3, Highs: 0.3);
    harness.Engine.Render();

    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 1, Highs: 1);
    harness.BeatNow(index: 0);
    HueColor loud = harness.Engine.Render();

    // A kick still landing on its beat, in a passage a fraction of the volume. The kick
    // gate is wide open in both, so what separates them here is the loudness and nothing
    // else - which is the thing under test.
    harness.Heard.Bands = new SpectrumLevels(Kick: 0.15, Mids: 0.05, Highs: 0.05);
    harness.BeatNow(index: 1);
    HueColor quiet = harness.Engine.Render();

    Assert.Equal(1.0, Brightness(loud), 3);
    Assert.True(
      Brightness(quiet) < Brightness(loud) / 2,
      $"The drop was at {Brightness(loud):F3} and the break at {Brightness(quiet):F3}, which is not the room reacting to the music.");
  }

  /// <summary>
  /// A section running hotter than the room has been lifts the resting level between
  /// beats, which is the difference the loudness gate discards on purpose: it saturates
  /// so that ordinary playing counts as playing, so a verse and a drop pulse alike, and
  /// before this the room answered them identically everywhere else too.
  ///
  /// Measured a second after the beat so the pulse is spent and no kick is hitting:
  /// whatever holds the room up is its energy and nothing else. And the lift is not
  /// forever - held long enough, the drop is the room's own level and the rest returns
  /// to the floor, because a room merely at its own level is not a drop.
  /// </summary>
  [Fact]
  public void ASectionRunningHotterThanTheRoomHasBeenLiftsTheRestingLevel() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 0.3, Mids: 0.3, Highs: 0.3);
    harness.BeatNow(index: 0);
    Assert.Equal(0.15, Brightness(harness.Engine.Render()), 3);

    // The drop arrives: three times what the room has been. The pulse is long spent, so
    // the lift is the energy's claim on the resting level and nothing else.
    harness.Clock.Advance(TimeSpan.FromSeconds(1));
    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 1, Highs: 1);
    Assert.Equal(0.49, Brightness(harness.Engine.Render()), 3);

    // Two minutes into the same level it is the room's typical, and the rest settles.
    harness.Clock.Advance(TimeSpan.FromSeconds(120));
    double settled = Brightness(harness.Engine.Render());
    Assert.True(settled < 0.2, $"A level held for two minutes still lifts the rest to {settled:F3}, so the room never settles.");
  }

  /// <summary>
  /// The counterpart that keeps the energy honest: a room playing steadily at any level
  /// is its own typical, so the rest stays on the floor. Without this the measure would
  /// be reading loudness rather than change, which is the gate's job and already taken.
  /// </summary>
  [Fact]
  public void ARoomHoldingItsUsualLevelRestsOnTheFloor() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 0.5, Mids: 0.5, Highs: 0.5);
    harness.BeatNow(index: 0);
    harness.Engine.Render();

    harness.Clock.Advance(TimeSpan.FromSeconds(1));
    Assert.Equal(0.15, Brightness(harness.Engine.Render()), 3);
  }

  /// <summary>
  /// <b>A beat with no kick under it does not pulse the room.</b>
  ///
  /// A grid keeps publishing beats through a breakdown - Link is a clock, and the audio
  /// detector holds its timeline - so the lights were firing a synchronous pulse right
  /// through a passage with no kick in it. Scaling that by how loud the room was made it
  /// quieter and left it synchronous, and it is the unison rather than the brightness
  /// that gives it away: the room is announcing a beat nobody can hear.
  ///
  /// What carries the break is the other half of the same arrangement, and it is asserted
  /// here rather than left implied: with the pulse out of the way the lights follow their
  /// own bands, so the one whose band is hitting is lifted and the one whose band is idle
  /// is not. There is no break *mode* - this is what the room already does once the pulse
  /// stops sitting on top of it.
  /// </summary>
  [Fact]
  public void ABeatWithNoKickUnderItLeavesTheRoomToTheBandsThatAreHitting() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 0.5, Mids: 0.3, Highs: 0.1);
    RenderRoom(harness, 0, 1);

    // The break: the kick has gone, the hats have not.
    harness.Heard.Bands = new SpectrumLevels(Kick: 0.2, Mids: 0.3, Highs: 0.3);
    harness.BeatNow(index: 0);

    (_, HueColor[] lights) = RenderRoom(harness, 0, 1);

    Assert.True(
      Brightness(lights[0]) < 0.2,
      $"The beat pulsed a room with no kick in it: the kick light is at {Brightness(lights[0]):F3}.");
    Assert.True(
      Brightness(lights[1]) > Brightness(lights[0]),
      $"The band that is still hitting should carry the break, and is at {Brightness(lights[1]):F3} against {Brightness(lights[0]):F3}.");
  }

  /// <summary>
  /// A finished track leaves the lights at their resting level rather than going on
  /// flashing, which is the form the problem was first noticed in: a grid driven by a
  /// clock publishes beats whether or not anything is playing, and before this the room
  /// answered every one of them at full height.
  ///
  /// Resting and not off, on the argument the floor has always carried: a room that
  /// fades out altogether is indistinguishable from a stream that has dropped.
  /// </summary>
  [Fact]
  public void WhenTheMusicStopsTheRoomSettlesRatherThanGoingOnPulsing() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 1, Highs: 1);
    harness.BeatNow(index: 0);
    harness.Engine.Render();

    harness.Heard.Bands = new SpectrumLevels(Kick: 0, Mids: 0, Highs: 0);
    harness.Clock.Advance(TimeSpan.FromSeconds(1));
    harness.BeatNow(index: 2);

    (HueColor shared, HueColor[] lights) = RenderRoom(harness, 0, 0.5, 1);

    Assert.Equal(0.15, Brightness(shared), 3);
    Assert.All(lights, light => Assert.Equal(0.15, Brightness(light), 3));
  }

  /// <summary>
  /// Above one colour the room splits along the same axis that already decides how hard
  /// each light is hit, so the two say the same thing rather than two unrelated ones.
  /// </summary>
  [Fact]
  public void TwoColoursSplitTheRoomAtTheMiddleOfTheFrequencyAxis() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0, ColorCount = 2 });

    RenderRoom(harness, 0, 0.25, 0.75, 1);
    harness.Clock.Advance(TimeSpan.FromSeconds(2));

    (_, HueColor[] lights) = RenderRoom(harness, 0, 0.25, 0.75, 1);

    Assert.Equal(HueOf(lights[0]), HueOf(lights[1]), HueTolerance);
    Assert.Equal(HueOf(lights[2]), HueOf(lights[3]), HueTolerance);
    Assert.True(
      Separation(HueOf(lights[0]), HueOf(lights[2])) > HueTolerance,
      "The two halves of the room are on the same hue, so the split is not visible.");
  }

  /// <summary>
  /// The three layouts divide the same room the same way round and differ only in how the
  /// divisions are handed out, which is what makes them comparable by standing in front of
  /// them. Alternating is the one that says so most clearly: neighbours in space disagree
  /// by design, where radial groups them.
  /// </summary>
  [Fact]
  public void AlternatingHandsNeighbouringLightsDifferentColours() {
    Harness harness = new Harness(LightSettings.Default with {
      PulseMs = 0,
      ColorCount = 2,
      ColorLayout = LightColorLayout.Alternating,
    });

    RenderRoom(harness, 0, 0.25, 0.5, 0.75, 1);
    harness.Clock.Advance(TimeSpan.FromSeconds(2));

    (_, HueColor[] lights) = RenderRoom(harness, 0, 0.25, 0.5, 0.75, 1);

    // Ranks 0 to 4 over five lights, so the groups run A B A B A.
    Assert.Equal(HueOf(lights[0]), HueOf(lights[2]), HueTolerance);
    Assert.Equal(HueOf(lights[2]), HueOf(lights[4]), HueTolerance);
    Assert.Equal(HueOf(lights[1]), HueOf(lights[3]), HueTolerance);
    Assert.True(
      Separation(HueOf(lights[0]), HueOf(lights[1])) > HueTolerance,
      "Neighbouring lights are on the same hue, so the room is not alternating.");
  }

  /// <summary>
  /// Random is stable between colour changes rather than re-drawn per frame, which is the
  /// difference between a room whose shape changes with the music and a room full of
  /// flickering.
  /// </summary>
  [Fact]
  public void RandomHoldsItsAssignmentUntilTheColoursChange() {
    Harness harness = new Harness(new LightSettings(
      LightPalette.Party,
      ColorChangeBars: 16,
      PulseMs: 0,
      ColorCount: 3,
      ColorLayout: LightColorLayout.Random));

    RenderRoom(harness, 0, 0.25, 0.5, 0.75, 1);
    harness.Clock.Advance(TimeSpan.FromSeconds(2));

    (_, HueColor[] first) = RenderRoom(harness, 0, 0.25, 0.5, 0.75, 1);

    harness.Clock.Advance(TimeSpan.FromMilliseconds(100));

    (_, HueColor[] second) = RenderRoom(harness, 0, 0.25, 0.5, 0.75, 1);

    for(int index = 0; index < first.Length; index++) {
      Assert.Equal(HueOf(first[index]), HueOf(second[index]), HueTolerance);
    }
  }

  /// <summary>
  /// A build with no audio project, a closed device and a silent room all arrive here,
  /// and all three mean the same thing: no opinion about which lights should lead. What
  /// Flux did for the whole of Phase 3 is the right answer to that.
  /// </summary>
  [Fact]
  public void WithNothingBeingHeardEveryLightGetsTheRoomsOwnEnvelope() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });

    harness.BeatNow(index: 0);

    (HueColor shared, HueColor[] lights) = RenderRoom(harness, 0, 0.5, 1);

    Assert.All(lights, light => Assert.Equal(shared, light));
  }

  [Fact]
  public void AFrameWithNoEnergyInAnyBandLightsTheRoomEvenly() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 240 });
    harness.Heard.Bands = new SpectrumLevels(Kick: 0, Mids: 0, Highs: 0);

    harness.BeatNow(index: 0);

    (HueColor shared, HueColor[] lights) = RenderRoom(harness, 0, 1);

    Assert.All(lights, light => Assert.Equal(shared, light));
  }

  /// <summary>
  /// Somebody who turns the pulse off asked for one steady colour, not for a room that
  /// shimmers by position. The envelope collapsing to a point is what makes that fall
  /// out rather than needing a branch.
  /// </summary>
  [Fact]
  public void WithThePulseOffTheSpectrumChangesNothing() {
    Harness harness = new Harness(LightSettings.Default with { PulseMs = 0 });
    harness.Heard.Bands = new SpectrumLevels(Kick: 1, Mids: 0, Highs: 0);

    harness.BeatNow(index: 0);

    (HueColor shared, HueColor[] lights) = RenderRoom(harness, 0, 1);

    Assert.All(lights, light => Assert.Equal(shared, light));
  }

  /// <summary>
  /// A frame that lit some of the room and left the rest showing the previous beat
  /// would be a flicker nobody could trace back to the engine.
  /// </summary>
  [Fact]
  public void ARoomOfferedFewerColoursThanLightsIsRefusedRatherThanPartlyLit() {
    Harness harness = new Harness();

    Assert.Throws<ArgumentException>(() => {
      LightPlacement[] lights = [new LightPlacement(0, 0), new LightPlacement(1, 1)];

      harness.Engine.Render(lights, new HueColor[1]);
    });
  }

  /// <summary>
  /// Renders a room whose lights listen at the given points on the frequency axis.
  ///
  /// The channel ids are the array positions, which is not how a real room numbers
  /// them and does not matter here: the engine is handed placements and never looks an
  /// id up. What it means for the reader is that the colours come back in the order the
  /// frequencies were asked for.
  /// </summary>
  private static (HueColor Shared, HueColor[] Lights) RenderRoom(Harness harness, params double[] ats) {
    LightPlacement[] lights = new LightPlacement[ats.Length];
    for(int index = 0; index < ats.Length; index++) {
      lights[index] = new LightPlacement(index, ats[index]);
    }

    HueColor[] colors = new HueColor[ats.Length];

    return (harness.Engine.Render(lights, colors), colors);
  }

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

  /// <summary>
  /// A room the test decides the sound of.
  ///
  /// <see cref="Spectrum"/> throws rather than answering, and that is the point of the
  /// class rather than an oversight: it consumes the beat verdict the visualizer needs,
  /// so the engine reading it would take beat markers off the canvas whenever the
  /// lights were streaming. The contract is pinned here because no assertion about
  /// colour could ever catch it.
  /// </summary>
  private sealed class FakeSpectrum: IAudioSpectrum {
    internal SpectrumLevels? Bands { get; set; }

    public SpectrumSnapshot? Spectrum() {
      throw new InvalidOperationException("The light engine must read Levels(), which consumes nothing, and never Spectrum().");
    }

    public SpectrumLevels? Levels() {
      return this.Bands;
    }
  }

  private sealed class Harness {
    internal Harness(LightSettings? light = null, int offsetMs = 0, int seed = 20260909, int quantum = 4) {
      Settings.Stored = HueSettings.Empty with { Light = light, LatencyOffsetMs = offsetMs };

      Bridge = new HueBridgeService(
        new FakeHueBridgeClient(),
        new InMemoryCredentialStore(),
        Settings,
        Options.Create(new HueOptions()),
        Clock,
        NullLogger<HueBridgeService>.Instance);

      // A real scheduler, never started. The engine needs one to subscribe to and the
      // tests hand it beats directly, so nothing here runs a thread or invents a tempo.
      Grid = new BeatGrid(Clock, quantum, TimeSpan.FromSeconds(2));
      BeatScheduler scheduler = new BeatScheduler(Grid, Clock, NullLogger<BeatScheduler>.Instance);

      Quantum = quantum;
      Start = Clock.NowMicros;

      Engine = new LightEngine(
        scheduler,
        Bridge,
        Clock,
        Heard,
        NullLogger<LightEngine>.Instance,
        new Random(seed));
    }

    internal FakeClock Clock { get; } = new FakeClock();

    /// <summary>What the room sounds like. Silent by default, which is every test that predates the spectrum.</summary>
    internal FakeSpectrum Heard { get; } = new FakeSpectrum();

    internal InMemorySettingsStore Settings { get; } = new InMemorySettingsStore();

    internal HueBridgeService Bridge { get; }

    internal LightEngine Engine { get; }

    /// <summary>The grid behind the scheduler, which is where a source's phrases land.</summary>
    internal BeatGrid Grid { get; }

    /// <summary>When the harness was built, which is where <see cref="MomentOf"/> puts beat 0.</summary>
    internal double Start { get; }

    /// <summary>Beats to the bar, which the grid and the engine are both built on.</summary>
    internal int Quantum { get; }

    /// <summary>
    /// The downbeat the harness numbers bars from, as the grid would, or null before
    /// <see cref="Anchor"/> - when the count runs from beat 0, which is also what the grid
    /// does before anything anchors it.
    /// </summary>
    internal long? AnchorBeat { get; private set; }

    /// <summary>
    /// A beat as the grid would stamp it: position and bar both counted from the anchor.
    /// </summary>
    /// <param name="index">The grid's own beat index, which is absolute and not counted from the anchor.</param>
    /// <param name="scheduledMicros">The moment the beat is aimed at.</param>
    /// <param name="barAligned">Whether a downbeat is known at all.</param>
    internal BeatEvent BeatAt(long index, double scheduledMicros, bool barAligned = true) {
      long fromAnchor = index - (AnchorBeat ?? 0);

      return new BeatEvent(
        index,
        (int)FloorMod(fromAnchor, Quantum) + 1,
        barAligned,
        Bpm: 120,
        scheduledMicros,
        Bar: (fromAnchor - FloorMod(fromAnchor, Quantum)) / Quantum);
    }

    /// <summary>Delivers a beat aimed at this instant.</summary>
    internal void BeatNow(long index, bool barAligned = true) {
      Engine.OnBeat(BeatAt(index, Clock.NowMicros, barAligned));
    }

    /// <summary>
    /// Presses anchor: every later beat is stamped from this one, and the engine hears
    /// where the new downbeat falls, which is what <see cref="BeatGrid.BarAligned"/> does.
    /// </summary>
    /// <param name="index">The beat that becomes position 1.</param>
    /// <param name="downbeatMicros">That beat's moment, which may be past or still to come.</param>
    internal void Anchor(long index, double downbeatMicros) {
      AnchorBeat = index;
      Engine.OnBarAligned(downbeatMicros);
    }

    /// <summary>
    /// The hue after a beat has been rendered and the transition has finished.
    ///
    /// Two seconds is well past the point where the slide clamps and lands exactly on
    /// its target, which is what makes "did the colour change" a question with a yes or
    /// no answer rather than one about how far it had got.
    /// </summary>
    /// <param name="index">The grid's own beat index, which is absolute and not counted from the anchor.</param>
    /// <param name="barAligned">Whether a downbeat is known at all.</param>
    internal double SettledHue(long index, bool barAligned = true) {
      BeatNow(index, barAligned);

      return Settle();
    }

    /// <summary>Renders, lets the transition finish, and reads the hue it landed on.</summary>
    internal double Settle() {
      Engine.Render();

      Clock.Advance(TimeSpan.FromSeconds(2));

      return HueOf(Engine.Render());
    }

    /// <summary>
    /// The moment beat <paramref name="index"/> falls on at the 120 BPM every beat is
    /// stamped with, for tests where the beats' moments have to agree with their tempo -
    /// a phrase is counted from in time, and <see cref="SettledHue"/> spaces beats two
    /// seconds apart.
    /// </summary>
    internal double MomentOf(double index) => Start + (index * 500_000);

    /// <summary>The hue after beat <paramref name="index"/> arrives at <see cref="MomentOf"/> and settles.</summary>
    internal double SettledHueOnTime(long index) {
      Engine.OnBeat(BeatAt(index, MomentOf(index)));

      return Settle();
    }

    /// <summary>Phrases starting at the moments of the given beats, the last one ending at <paramref name="endIndex"/>'s.</summary>
    internal void OfferPhrases(double endIndex, params double[] starts) {
      Grid.SetPhrases(new PhraseMap(
        [.. starts.Select(start => new PhraseMark(PhraseKind.Verse, 0, MomentOf(start)))],
        MomentOf(endIndex)));
    }

    /// <summary>Which of beats 0 to <paramref name="count"/> - 1 changed the colour, each arriving on time.</summary>
    internal List<long> ChangesOnTime(int count) {
      List<long> changes = [];
      double previous = SettledHueOnTime(0);
      for(long index = 1; index < count; index++) {
        double hue = SettledHueOnTime(index);
        if(Separation(hue, previous) > HueTolerance) {
          changes.Add(index);
        }

        previous = hue;
      }

      return changes;
    }

    private static long FloorMod(long value, long divisor) {
      long remainder = value % divisor;

      return remainder < 0 ? remainder + divisor : remainder;
    }
  }
}
