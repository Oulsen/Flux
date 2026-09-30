using Xunit;

namespace Flux.Rekordbox.Tests;

public class BeatgridPositionTests {
  /// <summary>Four beats at 120 BPM, starting 100 ms in, on beat 3 of a bar.</summary>
  private static readonly AnlzBeat[] Grid = [new(3, 120f, 100), new(4, 120f, 600), new(1, 120f, 1100), new(2, 120f, 1600)];

  [Theory]
  [InlineData(100, 0)]
  [InlineData(350, 0.5)]
  [InlineData(1100, 2)]
  [InlineData(1475, 2.75)]
  public void InterpolatesBetweenTheGridsBeats(double trackMs, double beat) {
    Assert.Equal(beat, BeatgridPosition.BeatAt(Grid, trackMs), precision: 9);
  }

  [Fact]
  public void CountsOnAtTheNearestBeatsTempoOutsideTheGrid() {
    Assert.Equal(-0.2, BeatgridPosition.BeatAt(Grid, 0), precision: 9);
    Assert.Equal(5, BeatgridPosition.BeatAt(Grid, 2600), precision: 9);
  }

  [Fact]
  public void DoesNotDivideByBeatsPiledOnOneMillisecond() {
    AnlzBeat[] piled = [new(1, 120f, 0), new(2, 120f, 500), new(3, 120f, 500)];

    Assert.Equal(2.5, BeatgridPosition.BeatAt(piled, 750), precision: 9);
  }

  [Theory]
  [InlineData(2.3, 2)]
  [InlineData(3.6, 2)]
  [InlineData(4.1, 6)]
  [InlineData(-0.4, -2)]
  [InlineData(9.9, 10)]
  public void FindsTheNearestDownbeatInsideAndPastTheGrid(double beat, long downbeat) {
    Assert.Equal(downbeat, BeatgridPosition.NearestDownbeat(Grid, beat));
  }
}
