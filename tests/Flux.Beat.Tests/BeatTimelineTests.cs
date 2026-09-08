using Xunit;

namespace Flux.Beat.Tests;

public class BeatTimelineTests {
  [Fact]
  public void MapsBeatsAndTimeInBothDirections() {
    BeatTimeline timeline = new BeatTimeline(MicrosPerBeat: 500_000, BeatOrigin: 4, TimeOriginMicros: 2_000_000);

    Assert.Equal(4, timeline.BeatAt(2_000_000), precision: 6);
    Assert.Equal(6, timeline.BeatAt(3_000_000), precision: 6);
    Assert.Equal(2_000_000, timeline.MicrosOfBeat(4), precision: 6);
    Assert.Equal(3_000_000, timeline.MicrosOfBeat(6), precision: 6);
  }

  [Fact]
  public void ExtrapolatesBackwardsPastTheAnchor() {
    BeatTimeline timeline = new BeatTimeline(MicrosPerBeat: 500_000, BeatOrigin: 0, TimeOriginMicros: 10_000_000);

    Assert.Equal(-4, timeline.BeatAt(8_000_000), precision: 6);
  }

  [Theory]
  [InlineData(1_000_000, 60.0)]
  [InlineData(500_000, 120.0)]
  [InlineData(342_857, 175.0)]
  public void ConvertsTempoToBpm(long microsPerBeat, double expectedBpm) {
    Assert.Equal(expectedBpm, new BeatTimeline(microsPerBeat, 0, 0).Bpm, precision: 2);
  }

  [Theory]
  [InlineData(BeatTimeline.MinMicrosPerBeat, true)]
  [InlineData(BeatTimeline.MaxMicrosPerBeat, true)]
  [InlineData(BeatTimeline.MinMicrosPerBeat - 1, false)]
  [InlineData(BeatTimeline.MaxMicrosPerBeat + 1, false)]
  [InlineData(0, false)]
  [InlineData(-1, false)]
  public void BoundsPlausibleTempi(long microsPerBeat, bool expected) {
    Assert.Equal(expected, new BeatTimeline(microsPerBeat, 0, 0).IsPlausible);
  }

  /// <summary>
  /// A plausible tempo beside a wild anchor is still a bad timeline: the tempo
  /// reads correctly on screen while the beat number sits around 1e13 and the bar
  /// readout rotates at random.
  /// </summary>
  [Theory]
  [InlineData(BeatTimeline.MaxAnchorMagnitudeMicros, true)]
  [InlineData(-BeatTimeline.MaxAnchorMagnitudeMicros, true)]
  [InlineData(BeatTimeline.MaxAnchorMagnitudeMicros + 1, false)]
  [InlineData(long.MinValue, false)]
  [InlineData(long.MaxValue, false)]
  public void BoundsPlausibleTimeOrigins(long timeOriginMicros, bool expected) {
    BeatTimeline timeline = new BeatTimeline(500_000, BeatOrigin: 0, timeOriginMicros);

    Assert.Equal(expected, timeline.IsPlausible);
  }

  [Theory]
  [InlineData(BeatTimeline.MaxBeatOriginMagnitude, true)]
  [InlineData(-BeatTimeline.MaxBeatOriginMagnitude, true)]
  [InlineData(BeatTimeline.MaxBeatOriginMagnitude * 10, false)]
  public void BoundsPlausibleBeatOrigins(double beatOrigin, bool expected) {
    BeatTimeline timeline = new BeatTimeline(500_000, beatOrigin, TimeOriginMicros: 0);

    Assert.Equal(expected, timeline.IsPlausible);
  }

  /// <summary>
  /// The sources that have no wire format build their timeline from a tempo and
  /// an instant. The instant is the load-bearing half: a tap or an onset says
  /// when a beat <em>was</em>, and a factory that only took a BPM would anchor it
  /// wherever it happened to be called.
  /// </summary>
  [Fact]
  public void AnchorsABpmSoTheGivenBeatFallsAtTheGivenInstant() {
    BeatTimeline timeline = BeatTimeline.FromBpm(bpm: 120, beat: 7, atMicros: 5_000_000);

    Assert.Equal(120, timeline.Bpm, precision: 6);
    Assert.Equal(7, timeline.BeatAt(5_000_000), precision: 6);
    Assert.Equal(5_000_000, timeline.MicrosOfBeat(7), precision: 6);

    // And it keeps counting at the stated tempo either side of that anchor.
    Assert.Equal(9, timeline.BeatAt(6_000_000), precision: 6);
    Assert.Equal(5, timeline.BeatAt(4_000_000), precision: 6);
  }

  [Theory]
  [InlineData(60.0)]
  [InlineData(120.0)]
  [InlineData(175.0)]
  public void RoundTripsAnyPlausibleTempoThroughTheFactory(double bpm) {
    BeatTimeline timeline = BeatTimeline.FromBpm(bpm, beat: 0, atMicros: 0);

    Assert.True(timeline.IsPlausible);
    Assert.Equal(bpm, timeline.Bpm, precision: 2);
  }

  /// <summary>
  /// A tempo the factory cannot represent has to come back as an implausible
  /// timeline rather than as a plausible-looking one: every caller already checks
  /// <see cref="BeatTimeline.IsPlausible"/> before publishing, so that is the one
  /// gate a bad value must not slip through.
  /// </summary>
  [Theory]
  [InlineData(0.0)]
  [InlineData(-120.0)]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  public void RefusesToProduceAPlausibleTimelineFromAnImpossibleTempo(double bpm) {
    Assert.False(BeatTimeline.FromBpm(bpm, beat: 0, atMicros: 0).IsPlausible);
  }
}
