using Xunit;

namespace Flux.Beat.Tests;

public class BeatGridTests {
  /// <summary>120 BPM anchored at beat 0, time 0 - round numbers so the arithmetic is checkable by eye.</summary>
  private static readonly LinkTimeline AtOneTwenty = new LinkTimeline(
    MicrosPerBeat: 500_000,
    BeatOrigin: 0,
    TimeOriginMicros: 0);

  private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2.5);

  [Fact]
  public void ReportsNothingBeforeTheFirstPacket() {
    BeatGrid grid = NewGrid(new FakeClock());

    Assert.False(grid.TryGetSnapshot(out _));
    Assert.Null(grid.Timeline);
  }

  /// <summary>
  /// The distinction the whole design rests on: beats come from extrapolating
  /// the timeline against the clock, not from packet arrivals. One packet plus
  /// elapsed time is a moving beat.
  /// </summary>
  [Fact]
  public void ExtrapolatesBeatsBetweenPackets() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot atAnchor));
    Assert.Equal(0, atAnchor.Beat, precision: 6);

    clock.Advance(TimeSpan.FromSeconds(1));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot later));

    Assert.Equal(2, later.Beat, precision: 6);
    Assert.Equal(2, later.BeatIndex);
    Assert.Equal(1, later.Packets);
  }

  /// <summary>
  /// Phase 0 measured the re-anchor discontinuity at 0.0000 beats over ~2.5
  /// minutes, because rekordbox republishes an unchanged timeline. This pins
  /// that behaviour: adopting the same timeline again must be a no-op, not a
  /// nudge.
  /// </summary>
  [Fact]
  public void ReanchoringOnAnUnchangedTimelineMovesNothing() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    for(int packet = 0; packet < 10; packet++) {
      clock.Advance(TimeSpan.FromMilliseconds(250));
      grid.Apply(AtOneTwenty);
    }

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(0, snapshot.MaxReanchorJumpBeats, precision: 9);
    Assert.Equal(11, snapshot.Packets);
    Assert.Equal(5, snapshot.Beat, precision: 6);
  }

  [Fact]
  public void RecordsHowFarAReplacementTimelineMovedTheBeat() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));

    // The same tempo, shifted half a beat later.
    grid.Apply(AtOneTwenty with { BeatOrigin = -0.5 });

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(0.5, snapshot.MaxReanchorJumpBeats, precision: 6);
  }

  [Fact]
  public void GoesStaleOnceThePacketsStop() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);

    clock.Advance(TimeSpan.FromSeconds(1));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot fresh));
    Assert.False(fresh.IsStale);

    clock.Advance(TimeSpan.FromSeconds(2));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot quiet));
    Assert.True(quiet.IsStale);

    // Still extrapolating: a tempo that has gone quiet beats going dark.
    Assert.Equal(120, quiet.Bpm, precision: 6);
    Assert.Equal(6, quiet.Beat, precision: 6);
  }

  [Fact]
  public void AnnouncesTheFirstTimelineAndEveryTempoChange() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    List<double> announced = new List<double>();
    grid.TimelineChanged += timeline => announced.Add(timeline.Bpm);

    grid.Apply(AtOneTwenty);
    grid.Apply(AtOneTwenty);
    grid.Apply(AtOneTwenty with { BeatOrigin = 4 });
    grid.Apply(AtOneTwenty with { MicrosPerBeat = 342_857 });

    Assert.Equal(2, announced.Count);
    Assert.Equal(120, announced[0], precision: 2);
    Assert.Equal(175, announced[1], precision: 2);
  }

  [Fact]
  public void ForgetsEverythingOnReset() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    grid.Reset();

    Assert.False(grid.TryGetSnapshot(out _));
  }

  /// <summary>
  /// Phase 1 has no downbeat source, and Link carries none. Reporting the bar
  /// position as unanchored is the honest answer; claiming alignment would make
  /// bar-scoped effects fire confidently on the wrong beat.
  /// </summary>
  [Fact]
  public void NeverClaimsBarAlignment() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.False(snapshot.IsBarAligned);
  }

  [Fact]
  public void CyclesBarPositionsFromOne() {
    Assert.Equal(1, BeatGrid.PositionInBar(0, 4));
    Assert.Equal(2, BeatGrid.PositionInBar(1, 4));
    Assert.Equal(4, BeatGrid.PositionInBar(3, 4));
    Assert.Equal(1, BeatGrid.PositionInBar(4, 4));
    Assert.Equal(3, BeatGrid.PositionInBar(1002, 4));
  }

  /// <summary>
  /// Beat numbers go negative whenever the session anchor sits ahead of now, and
  /// C# modulo keeps the sign of the dividend. Left alone that yields positions
  /// like -2, which is not a bar position in any time signature.
  /// </summary>
  [Fact]
  public void NormalisesBarPositionsForNegativeBeats() {
    Assert.Equal(4, BeatGrid.PositionInBar(-1, 4));
    Assert.Equal(1, BeatGrid.PositionInBar(-4, 4));
    Assert.Equal(2, BeatGrid.PositionInBar(-7, 4));
  }

  [Fact]
  public void RejectsAnUnusableConfiguration() {
    Assert.Throws<ArgumentOutOfRangeException>(() => new BeatGrid(new FakeClock(), quantum: 0, StaleAfter));
    Assert.Throws<ArgumentOutOfRangeException>(() => new BeatGrid(new FakeClock(), quantum: 4, TimeSpan.Zero));
    Assert.Throws<ArgumentNullException>(() => new BeatGrid(null!, quantum: 4, StaleAfter));
  }

  private static BeatGrid NewGrid(IMonotonicClock clock) {
    return new BeatGrid(clock, quantum: 4, StaleAfter);
  }
}
