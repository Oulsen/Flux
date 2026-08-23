using Xunit;

namespace Flux.Beat.Tests;

public class LinkTimelineTests {
  [Fact]
  public void MapsBeatsAndTimeInBothDirections() {
    LinkTimeline timeline = new LinkTimeline(MicrosPerBeat: 500_000, BeatOrigin: 4, TimeOriginMicros: 2_000_000);

    Assert.Equal(4, timeline.BeatAt(2_000_000), precision: 6);
    Assert.Equal(6, timeline.BeatAt(3_000_000), precision: 6);
    Assert.Equal(2_000_000, timeline.MicrosOfBeat(4), precision: 6);
    Assert.Equal(3_000_000, timeline.MicrosOfBeat(6), precision: 6);
  }

  [Fact]
  public void ExtrapolatesBackwardsPastTheAnchor() {
    LinkTimeline timeline = new LinkTimeline(MicrosPerBeat: 500_000, BeatOrigin: 0, TimeOriginMicros: 10_000_000);

    Assert.Equal(-4, timeline.BeatAt(8_000_000), precision: 6);
  }

  [Theory]
  [InlineData(1_000_000, 60.0)]
  [InlineData(500_000, 120.0)]
  [InlineData(342_857, 175.0)]
  public void ConvertsTempoToBpm(long microsPerBeat, double expectedBpm) {
    Assert.Equal(expectedBpm, new LinkTimeline(microsPerBeat, 0, 0).Bpm, precision: 2);
  }

  [Theory]
  [InlineData(LinkTimeline.MinMicrosPerBeat, true)]
  [InlineData(LinkTimeline.MaxMicrosPerBeat, true)]
  [InlineData(LinkTimeline.MinMicrosPerBeat - 1, false)]
  [InlineData(LinkTimeline.MaxMicrosPerBeat + 1, false)]
  [InlineData(0, false)]
  [InlineData(-1, false)]
  public void BoundsPlausibleTempi(long microsPerBeat, bool expected) {
    Assert.Equal(expected, new LinkTimeline(microsPerBeat, 0, 0).IsPlausible);
  }

  /// <summary>
  /// A plausible tempo beside a wild anchor is still a bad timeline: the tempo
  /// reads correctly on screen while the beat number sits around 1e13 and the bar
  /// readout rotates at random.
  /// </summary>
  [Theory]
  [InlineData(LinkTimeline.MaxAnchorMagnitudeMicros, true)]
  [InlineData(-LinkTimeline.MaxAnchorMagnitudeMicros, true)]
  [InlineData(LinkTimeline.MaxAnchorMagnitudeMicros + 1, false)]
  [InlineData(long.MinValue, false)]
  [InlineData(long.MaxValue, false)]
  public void BoundsPlausibleTimeOrigins(long timeOriginMicros, bool expected) {
    LinkTimeline timeline = new LinkTimeline(500_000, BeatOrigin: 0, timeOriginMicros);

    Assert.Equal(expected, timeline.IsPlausible);
  }

  [Theory]
  [InlineData(LinkTimeline.MaxBeatOriginMagnitude, true)]
  [InlineData(-LinkTimeline.MaxBeatOriginMagnitude, true)]
  [InlineData(LinkTimeline.MaxBeatOriginMagnitude * 10, false)]
  public void BoundsPlausibleBeatOrigins(double beatOrigin, bool expected) {
    LinkTimeline timeline = new LinkTimeline(500_000, beatOrigin, TimeOriginMicros: 0);

    Assert.Equal(expected, timeline.IsPlausible);
  }
}
