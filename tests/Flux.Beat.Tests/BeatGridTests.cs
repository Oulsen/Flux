using Xunit;

namespace Flux.Beat.Tests;

public class BeatGridTests {
  /// <summary>120 BPM anchored at beat 0, time 0 - round numbers so the arithmetic is checkable by eye.</summary>
  private static readonly BeatTimeline AtOneTwenty = new BeatTimeline(
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
    List<TempoChange> announced = new List<TempoChange>();
    grid.TempoChanged += change => announced.Add(change);

    grid.Apply(AtOneTwenty);
    grid.Apply(AtOneTwenty);
    grid.Apply(AtOneTwenty with { BeatOrigin = 4 });
    grid.Apply(AtOneTwenty with { MicrosPerBeat = 342_857 });

    Assert.Equal(2, announced.Count);
    Assert.Equal(120, announced[0].Timeline.Bpm, precision: 2);
    Assert.Equal(175, announced[1].Timeline.Bpm, precision: 2);
  }

  /// <summary>
  /// Measured against rekordbox: a tempo change renumbers the Link timeline
  /// wholesale - 176.06 to 172.55 BPM moved the beat number by 2354 beats. The
  /// announcement carries that shift because anything anchored to a beat index,
  /// bar alignment above all, is invalidated by it.
  /// </summary>
  [Fact]
  public void ReportsHowFarATempoChangeMovedTheBeatNumbering() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    List<TempoChange> announced = new List<TempoChange>();
    grid.TempoChanged += change => announced.Add(change);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { MicrosPerBeat = 342_857 });

    Assert.Equal(2, announced.Count);
    Assert.Equal(0, announced[0].BeatNumberingShift, precision: 9);

    // Beat 0.5 at 120 BPM becomes beat 0.729 at 175 BPM, against a shared anchor.
    Assert.Equal(0.229, announced[1].BeatNumberingShift, precision: 3);
  }

  /// <summary>
  /// Regression: the drift metric is a running maximum, so counting a tempo
  /// change would pin it at thousands of beats for the rest of the session and
  /// destroy the one signal it exists to give - whether extrapolation between
  /// packets is still sound.
  /// </summary>
  [Fact]
  public void DoesNotCountATempoChangeAsExtrapolationDrift() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { MicrosPerBeat = 342_857 });

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterChange));
    Assert.Equal(0, afterChange.MaxReanchorJumpBeats, precision: 9);

    // And it still reports drift once the tempo settles again.
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { MicrosPerBeat = 342_857, BeatOrigin = 2 });

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterDrift));
    Assert.Equal(2, afterDrift.MaxReanchorJumpBeats, precision: 6);
  }

  /// <summary>
  /// Regression: switching LINK off and on starts a new Link session whose
  /// timeline shares the tempo but not the beat numbering - observed against
  /// rekordbox at ~710 beats. Counting that as drift would pin the running
  /// maximum at a huge value forever, exactly like counting a tempo change.
  /// </summary>
  [Fact]
  public void DoesNotCountASessionSwitchAsExtrapolationDrift() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = 710 }, newSession: true);

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(0, snapshot.MaxReanchorJumpBeats, precision: 9);

    // The new session's own republications are claims about the same thing
    // again, so the metric resumes counting from there.
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = 710.25 });

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterDrift));
    Assert.Equal(0.25, afterDrift.MaxReanchorJumpBeats, precision: 6);
  }

  /// <summary>
  /// A same-tempo session switch renumbers the beat exactly as a tempo change
  /// does, so it is announced the same way: anything anchored to a beat index,
  /// bar alignment above all, has to be moved by the shift.
  /// </summary>
  [Fact]
  public void AnnouncesASessionSwitchWithItsNumberingShift() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    List<TempoChange> announced = new List<TempoChange>();
    grid.TempoChanged += change => announced.Add(change);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = 710 }, newSession: true);

    Assert.Equal(2, announced.Count);
    Assert.Equal(710, announced[1].BeatNumberingShift, precision: 6);
  }

  /// <summary>
  /// No source transmits a downbeat, so a fresh grid is counting from an
  /// arbitrary starting point. Reporting that honestly is the point; claiming
  /// alignment would make bar-scoped effects fire confidently on the wrong beat.
  /// </summary>
  [Fact]
  public void StartsUnalignedBecauseNoSourceTransmitsADownbeat() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.False(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.None, snapshot.AlignmentMethod);
  }

  [Fact]
  public void CountsTheBarFromTheAnchorOnceAligned() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);

    // 2.5 s at 120 BPM is exactly beat 5, so the anchor lands there.
    clock.Advance(TimeSpan.FromSeconds(2.5));
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(5, snapshot.Beat, precision: 6);
    Assert.Equal(1, snapshot.PositionInBar);
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, snapshot.AlignmentMethod);

    // And the count runs on from there rather than from beat 0.
    Assert.Equal(1, grid.PositionOf(5).PositionInBar);
    Assert.Equal(2, grid.PositionOf(6).PositionInBar);
    Assert.Equal(4, grid.PositionOf(8).PositionInBar);
    Assert.Equal(1, grid.PositionOf(9).PositionInBar);
    Assert.Equal(4, grid.PositionOf(4).PositionInBar);

    // Bars too, floored, so the beats just before the anchor are the bar before it.
    Assert.Equal(0, grid.PositionOf(5).Bar);
    Assert.Equal(0, grid.PositionOf(8).Bar);
    Assert.Equal(1, grid.PositionOf(9).Bar);
    Assert.Equal(-1, grid.PositionOf(4).Bar);
    Assert.Equal(-1, grid.PositionOf(1).Bar);
    Assert.Equal(-2, grid.PositionOf(0).Bar);
  }

  /// <summary>
  /// The light engine answers a press on the beat it was meant for, and a press usually
  /// lands after that beat has fired, so the grid says where the new downbeat falls
  /// rather than leaving it to be noticed on the next one.
  /// </summary>
  [Fact]
  public void AnAlignmentSaysWhereTheNewDownbeatFallsAndOnlyWhenItMoved() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    List<double> reported = [];
    grid.BarAligned += reported.Add;

    grid.Apply(AtOneTwenty);

    // 2.5 s at 120 BPM is exactly beat 5, and a press 100 ms later still rounds to it.
    clock.Advance(TimeSpan.FromSeconds(2.5));
    double beatFive = clock.NowMicros;
    clock.Advance(TimeSpan.FromMilliseconds(100));
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    Assert.Equal([beatFive], reported);

    // The same beat again moves nothing, so there is nothing to answer.
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));
    Assert.Single(reported);
  }

  /// <summary>
  /// A drop within half a bar of the anchor only confirms it, which is not reported; one
  /// a whole bar on moves bar one by that bar without rotating it, which is, because it
  /// restarts the bar count the light engine's colour cycle runs on.
  /// </summary>
  [Fact]
  public void ADropIsReportedOnlyWhenItMovesBarOne() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    List<double> reported = [];

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));
    grid.BarAligned += reported.Add;

    clock.Advance(TimeSpan.FromMilliseconds(500));
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Drop, snapToBar: true));
    Assert.Empty(reported);

    // Beat 4, a bar after the tap.
    clock.Advance(TimeSpan.FromMilliseconds(1500));
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Drop, snapToBar: true));
    Assert.Equal([clock.NowMicros], reported);
  }

  /// <summary>
  /// A human tapping along lands either side of the beat they mean, so the anchor
  /// is the <em>nearest</em> beat rather than the one currently sounding.
  /// Flooring would put every slightly early tap a whole beat out, which is the
  /// one error that makes the feature useless.
  /// </summary>
  [Theory]
  [InlineData(2.4)]
  [InlineData(2.6)]
  public void AnchorsTheNearestBeatRatherThanTheCurrentOne(double tappedAtSeconds) {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromSeconds(tappedAtSeconds));
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    // Either tap meant beat 5, so beat 5 is the downbeat in both cases.
    Assert.Equal(1, grid.PositionOf(5).PositionInBar);
  }

  [Fact]
  public void CannotAlignTheBarBeforeAnyTimelineIsKnown() {
    BeatGrid grid = NewGrid(new FakeClock());

    Assert.False(grid.TryAlignBar(BarAlignmentMethod.Tap));
    Assert.False(grid.PositionOf(0).IsAligned);
  }

  /// <summary>
  /// A source republishing a smoothed tempo moves the numbering by a rounding
  /// error. Discarding a hand-made alignment over a wobble nobody can hear would
  /// mean asking the user to tap again every few seconds.
  /// </summary>
  [Fact]
  public void KeepsTheBarAnchorThroughASubBeatReanchor() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Cue));

    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = 0.1 });

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Cue, snapshot.AlignmentMethod);
  }

  /// <summary>
  /// <b>An anchor is the user's statement and it survives a renumbering.</b>
  ///
  /// Until 2026-09-11 a rebase past half a beat threw the anchor away, on the reasoning
  /// that its beat index now names a different moment in the music. The index does - and
  /// the shift that made it wrong is measured on the same line, so the number can be
  /// corrected rather than forgotten. What the discarded version looked like in the room
  /// was reported the same day: a tap held until audio detection next rebased the
  /// timeline, which it does on drops and on strong beats, and the page went back to
  /// "counting beats, bar not anchored yet" with nothing the user did to explain it.
  /// </summary>
  [Fact]
  public void KeepsTheBarAnchorWhenTheNumberingIsRebased() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { MicrosPerBeat = 342_857, BeatOrigin = 100 });

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, snapshot.AlignmentMethod);
  }

  [Fact]
  public void KeepsTheBarAnchorOnASessionSwitch() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = 710 }, newSession: true);

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, snapshot.AlignmentMethod);
  }

  /// <summary>
  /// <b>A drop is a guess and does not overrule a bar that is already known.</b>
  ///
  /// Reported from the room on 2026-09-11: anchor by hand, and the next drop declares
  /// whatever beat it landed on to be position one and rotates the bar out from under it.
  /// Two things make that likely rather than rare - detection fires on an energy jump a
  /// frame or two after the transient, and a drop is not reliably on a downbeat at all.
  ///
  /// Snapping to the nearest downbeat of the existing grid is phase-preserving by
  /// construction, so what this asserts is that the bar position does not move. That is
  /// the intended reading of "a guess should not overrule a measurement", and its cost is
  /// stated where the flag is declared: an anchor that is genuinely wrong stays wrong
  /// until somebody taps again.
  /// </summary>
  [Fact]
  public void ADropDoesNotRotateABarThatIsAlreadyAnchored() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot tapped));

    // A beat and a half later, which is where a late drop detection lands and is exactly
    // what used to move the bar.
    clock.Advance(TimeSpan.FromMilliseconds(750));
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Drop, snapToBar: true));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterDrop));
    Assert.True(afterDrop.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, afterDrop.AlignmentMethod);

    // Three beats on from the tap, so the bar position has advanced by three and not been
    // reset to one.
    clock.Advance(TimeSpan.FromMilliseconds(750));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot later));
    Assert.Equal(PositionAfter(tapped.PositionInBar, beats: 3), later.PositionInBar);
  }

  /// <summary>
  /// With no bar known, a drop still sets one outright, which is the job it was added
  /// for: it is the only downbeat audio analysis can find without being told.
  /// </summary>
  [Fact]
  public void ADropStillAnchorsTheBarWhenNothingElseHas() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Drop, snapToBar: true));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Drop, snapshot.AlignmentMethod);
    Assert.Equal(1, snapshot.PositionInBar);
  }

  /// <summary>The 1-based bar position a given number of beats after another.</summary>
  private static int PositionAfter(int position, int beats) {
    return ((position - 1 + beats) % 4) + 1;
  }

  /// <summary>
  /// The anchor is carried by the shift rather than merely kept, so it goes on naming the
  /// same moment in the music. A kept-but-uncorrected index would claim alignment while
  /// putting the downbeat wherever the renumbering left it, which is the failure the old
  /// clearing behaviour was right to avoid and wrong about how to avoid.
  /// </summary>
  [Fact]
  public void TheKeptAnchorMovesWithTheNumberingRatherThanStayingPut() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot before));

    // A whole number of beats of renumbering at the same tempo, which is the case where
    // "the same moment" has an exact answer and the bar position must not move at all.
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = AtOneTwenty.BeatOrigin + 8 }, newSession: true);

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot after));
    Assert.True(after.IsBarAligned);
    Assert.Equal(before.PositionInBar, after.PositionInBar);
  }

  /// <summary>
  /// Reset is what a switch to a source that has never published uses. It has to
  /// leave nothing behind: a tempo surviving the switch would be shown under the
  /// arriving source's name, and a re-anchor maximum surviving it would blame the
  /// arriving source for the departing one's drift.
  /// </summary>
  [Fact]
  public void ResetLeavesNothingBehind() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);

    grid.Apply(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));
    grid.Apply(AtOneTwenty with { BeatOrigin = 2 });
    Assert.True(grid.TryAlignBar(BarAlignmentMethod.Tap));

    grid.Reset();

    Assert.False(grid.TryGetSnapshot(out _));
    Assert.Null(grid.Timeline);

    grid.Apply(AtOneTwenty);
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterReset));
    Assert.Equal(1, afterReset.Packets);
    Assert.Equal(0, afterReset.MaxReanchorJumpBeats, precision: 9);
    Assert.False(afterReset.IsBarAligned);
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
