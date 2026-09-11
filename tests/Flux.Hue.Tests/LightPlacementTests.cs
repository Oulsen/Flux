using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// The room-to-frequency mapping, which is arithmetic and needs no bridge.
///
/// The interesting cases are all about rooms that are not tidy, because a tidy room is
/// the one arrangement every candidate mapping handles. The two that earned their place
/// here are a real area whose lights cluster at one radius, and a pair placed at the
/// same point.
/// </summary>
public class LightPlacementTests {
  [Fact]
  public void TheLightNearestTheCentreFollowsTheKickAndTheFurthestFollowsTheTop() {
    IReadOnlyList<LightPlacement> spread = LightPlacements.Spread([
      new HueChannel(0, new LightPosition(1, 0, 0)),
      new HueChannel(1, new LightPosition(0.2, 0, 0)),
      new HueChannel(2, new LightPosition(0.6, 0, 0)),
    ]);

    Assert.Equal([1, 2, 0], spread.Select(placement => placement.ChannelId));
    Assert.Equal([0, 0.5, 1], spread.Select(placement => placement.At));
  }

  /// <summary>
  /// The regression the ranking exists for, measured off this rig's Woonkamer area on
  /// 2026-09-10: four of its seven lights sit within 0.07 of each other. Scaling raw
  /// distance would give those four almost the same frequency and leave the rest of the
  /// spectrum to the other three.
  /// </summary>
  [Fact]
  public void ARoomWhoseLightsClusterAtOneRadiusStillCoversTheWholeSpectrum() {
    IReadOnlyList<LightPlacement> spread = LightPlacements.Spread([
      new HueChannel(0, new LightPosition(0, 0, 0.467)),
      new HueChannel(1, new LightPosition(-0.184, 0.168, 1)),
      new HueChannel(2, new LightPosition(-1, -0.085, 0)),
      new HueChannel(3, new LightPosition(0.654, 1, 0)),
      new HueChannel(4, new LightPosition(-1, 0.373, 0)),
      new HueChannel(5, new LightPosition(-0.161, -1, 0)),
      new HueChannel(6, new LightPosition(-1, 1, 0)),
    ]);

    Assert.Equal([0, 2, 5, 1, 4, 3, 6], spread.Select(placement => placement.ChannelId));

    double[] expected = [0, 1 / 6.0, 2 / 6.0, 3 / 6.0, 4 / 6.0, 5 / 6.0, 1];
    Assert.Equal(expected.Length, spread.Count);
    for(int index = 0; index < expected.Length; index++) {
      Assert.Equal(expected[index], spread[index].At, 9);
    }
  }

  /// <summary>
  /// The property that makes this a mapping of the room rather than of the list. The
  /// bridge lists channels consistently today, and a mapping that depended on it would
  /// fail on the day that stops being true, silently, as lights following the wrong
  /// part of the music.
  /// </summary>
  [Fact]
  public void TheOrderChannelsArriveInDoesNotChangeTheMapping() {
    HueChannel[] room = [
      new HueChannel(7, new LightPosition(-0.741, 1, -1)),
      new HueChannel(3, new LightPosition(0, 0, 0)),
      new HueChannel(5, new LightPosition(-0.314, 0.305, 1)),
    ];

    IReadOnlyList<LightPlacement> forwards = LightPlacements.Spread(room);
    IReadOnlyList<LightPlacement> backwards = LightPlacements.Spread([.. room.Reverse()]);

    Assert.Equal(forwards, backwards);
  }

  /// <summary>
  /// Two bulbs flanking a booth are placed as a pair and should behave as one. The
  /// alternative has them drifting apart for a reason invisible from the floor.
  /// </summary>
  [Fact]
  public void LightsTheSameDistanceFromTheCentreShareAFrequency() {
    IReadOnlyList<LightPlacement> spread = LightPlacements.Spread([
      new HueChannel(0, new LightPosition(0, 0, 0)),
      new HueChannel(1, new LightPosition(1, 0, 0)),
      new HueChannel(2, new LightPosition(-1, 0, 0)),
    ]);

    Assert.Equal(0, spread[0].At);

    // The middle of the two ranks the tie spans, so the pair sits where a single light
    // between them would have, rather than one of them taking the top of the spectrum.
    Assert.Equal(0.75, spread[1].At);
    Assert.Equal(0.75, spread[2].At);
  }

  [Fact]
  public void ASingleLightFollowsTheKick() {
    IReadOnlyList<LightPlacement> spread = LightPlacements.Spread([
      new HueChannel(4, new LightPosition(0.5, 0.5, 0.5)),
    ]);

    Assert.Equal(4, Assert.Single(spread).ChannelId);
    Assert.Equal(0, spread[0].At);
  }

  [Fact]
  public void AnAreaWithNoChannelsPlacesNothing() {
    Assert.Empty(LightPlacements.Spread([]));
  }

  /// <summary>
  /// An unplaced light reports the origin, which the bridge does hand out: this rig's
  /// Blokhut area has one. It is the centre by the only definition available here, so
  /// it takes the kick, and the point of the test is that it is not treated as missing.
  /// </summary>
  [Fact]
  public void ALightAtTheOriginIsPlacedRatherThanSkipped() {
    IReadOnlyList<LightPlacement> spread = LightPlacements.Spread([
      new HueChannel(0, new LightPosition(0.8, 0, 0)),
      new HueChannel(1, LightPosition.Centre),
    ]);

    Assert.Equal(2, spread.Count);
    Assert.Equal(1, spread[0].ChannelId);
    Assert.Equal(0, spread[0].At);
    Assert.Equal(1, spread[1].At);
  }
}
