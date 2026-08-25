using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Beat.Tests;

public class ManualSourceTests {
  [Fact]
  public void KnowsNothingUntilSomethingIsTappedOrTyped() {
    (ManualSource manual, BeatGrid grid, _) = NewSource();

    Assert.Null(manual.Bpm);
    Assert.Equal(SyncSourceState.Waiting, manual.Status().State);
    Assert.Equal(0, manual.Status().Confidence, precision: 6);
    Assert.False(grid.TryGetSnapshot(out _));
  }

  /// <summary>
  /// One tap carries no interval, so it cannot produce a tempo. It must not
  /// invent one either: a single tap has to leave the source exactly as
  /// uninformed as it was.
  /// </summary>
  [Fact]
  public void ASingleTapYieldsNoTempo() {
    (ManualSource manual, BeatGrid grid, _) = NewSource();

    manual.Tap();

    Assert.Null(manual.Bpm);
    Assert.False(grid.TryGetSnapshot(out _));
  }

  [Fact]
  public void DerivesTheTempoFromTheIntervalBetweenTaps() {
    (ManualSource manual, BeatGrid grid, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    Assert.Equal(120, manual.Bpm!.Value, precision: 6);
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(120, snapshot.Bpm, precision: 6);
  }

  /// <summary>
  /// The tap sets phase as well as tempo, which is the whole reason it is timed
  /// on the server: beat 0 falls at the instant of the tap, so the grid then
  /// counts forward from there rather than from whenever the source happened to
  /// start.
  /// </summary>
  [Fact]
  public void AnchorsTheBeatAtTheInstantOfTheTap() {
    (ManualSource manual, BeatGrid grid, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot atTap));
    Assert.Equal(0, atTap.Beat, precision: 6);

    clock.Advance(TimeSpan.FromMilliseconds(250));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot halfABeatLater));
    Assert.Equal(0.5, halfABeatLater.Beat, precision: 6);
  }

  /// <summary>
  /// The error a human makes is one late tap, not a general wobble. The median
  /// ignores that tap outright; a mean would spread it over the whole estimate,
  /// so a single fumble in a run of four would move the tempo by a quarter of its
  /// size.
  /// </summary>
  [Fact]
  public void UsesTheMedianSoOneLateTapDoesNotDragTheTempo() {
    (ManualSource manual, _, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    // 400 ms late. Intervals are 500, 500, 900: the median is still 500, while
    // the mean would be 633 and the tempo would read about 95 BPM.
    clock.Advance(TimeSpan.FromMilliseconds(900));
    manual.Tap();

    Assert.Equal(120, manual.Bpm!.Value, precision: 6);
  }

  /// <summary>
  /// A gap slower than the slowest accepted tempo cannot be part of a tempo, so
  /// it is someone starting again rather than a very slow beat. Deriving the
  /// threshold from the minimum is what keeps tapping near 20 BPM possible at all.
  /// </summary>
  [Fact]
  public void StartsAFreshSequenceAfterALongPause() {
    (ManualSource manual, _, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();
    Assert.Equal(120, manual.Bpm!.Value, precision: 6);

    // Longer than 60/20 s, so the previous run is abandoned rather than averaged
    // in. The tempo stands until two new taps replace it.
    clock.Advance(TimeSpan.FromSeconds(5));
    manual.Tap();
    Assert.Equal(120, manual.Bpm!.Value, precision: 6);

    clock.Advance(TimeSpan.FromMilliseconds(300));
    manual.Tap();
    Assert.Equal(200, manual.Bpm!.Value, precision: 6);
  }

  /// <summary>
  /// Every tap states where the downbeat is, including the first of a run. With
  /// Link already supplying the tempo, one tap on the one is the entire gesture,
  /// and waiting for a second before honouring it would feel broken.
  /// </summary>
  [Fact]
  public void EveryTapAlignsTheBar() {
    (ManualSource manual, BeatGrid grid, FakeClock clock) = NewSource();

    Assert.True(manual.TrySetBpm(120));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot typed));
    Assert.False(typed.IsBarAligned);

    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot tapped));
    Assert.True(tapped.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, tapped.AlignmentMethod);
    Assert.Equal(1, tapped.PositionInBar);
  }

  /// <summary>
  /// A typed number says how fast, never where. It sets no downbeat, and it does
  /// not preserve one either: anchoring the new tempo at the moment of typing
  /// rebases the numbering, so the beat index the old anchor named now points
  /// somewhere else in the music and has to be re-armed.
  ///
  /// A known trade rather than an oversight. Carrying the phase across a tempo
  /// edit would mean anchoring the new tempo to the beat currently sounding
  /// instead of to beat 0, which is worth doing and is not what this does today.
  /// </summary>
  [Fact]
  public void ATypedTempoSetsNoDownbeatAndRetiresTheOldOne() {
    (ManualSource manual, BeatGrid grid, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterTaps));
    Assert.True(afterTaps.IsBarAligned);

    clock.Advance(TimeSpan.FromMilliseconds(1500));
    Assert.True(manual.TrySetBpm(128));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot afterTyping));
    Assert.Equal(128, afterTyping.Bpm, precision: 6);
    Assert.False(afterTyping.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.None, afterTyping.AlignmentMethod);
  }

  /// <summary>
  /// The other side of that rule, and the reason the threshold is a shift rather
  /// than "any new timeline": correcting the tempo the instant after tapping
  /// anchors beat 0 at the same moment the tap did, so nothing was renumbered and
  /// the downbeat the user just set survives.
  /// </summary>
  [Fact]
  public void CorrectingTheTempoImmediatelyKeepsTheDownbeat() {
    (ManualSource manual, BeatGrid grid, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    Assert.True(manual.TrySetBpm(128));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(128, snapshot.Bpm, precision: 6);
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, snapshot.AlignmentMethod);
  }

  [Theory]
  [InlineData(20)]
  [InlineData(128)]
  [InlineData(200)]
  public void AcceptsAnyTempoInsideTheAllowedRange(double bpm) {
    (ManualSource manual, BeatGrid grid, _) = NewSource();

    Assert.True(manual.TrySetBpm(bpm));
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(bpm, snapshot.Bpm, precision: 1);
    Assert.Equal(SyncSourceState.Live, manual.Status().State);
  }

  [Theory]
  [InlineData(19.9)]
  [InlineData(200.1)]
  [InlineData(0)]
  [InlineData(-120)]
  [InlineData(double.NaN)]
  [InlineData(double.PositiveInfinity)]
  public void RefusesATempoOutsideTheAllowedRange(double bpm) {
    (ManualSource manual, BeatGrid grid, _) = NewSource();

    Assert.False(manual.TrySetBpm(bpm));
    Assert.Null(manual.Bpm);
    Assert.False(grid.TryGetSnapshot(out _));
  }

  /// <summary>
  /// The taps described a different tempo, so keeping them would let the next
  /// single tap resurrect it by median and silently undo what was typed.
  /// </summary>
  [Fact]
  public void TypingATempoForgetsTheTapsBeforeIt() {
    (ManualSource manual, _, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    Assert.True(manual.TrySetBpm(150));

    clock.Advance(TimeSpan.FromMilliseconds(100));
    manual.Tap();

    Assert.Equal(150, manual.Bpm!.Value, precision: 6);
  }

  /// <summary>
  /// A tapped tempo does not decay, which is the substantive difference from every
  /// other source. Silence from Link means its session is gone and the tempo is a
  /// worsening guess; silence here means nobody changed their mind.
  /// </summary>
  [Fact]
  public void NeverGoesQuiet() {
    (ManualSource manual, _, FakeClock clock) = NewSource();

    manual.Tap();
    clock.Advance(TimeSpan.FromMilliseconds(500));
    manual.Tap();

    clock.Advance(TimeSpan.FromMinutes(10));

    Assert.Equal(SyncSourceState.Live, manual.Status().State);
    Assert.Equal(1, manual.Status().Confidence, precision: 6);
  }

  /// <summary>
  /// Manual is active in these tests so its timelines reach the grid, which is
  /// what makes the tap arithmetic observable.
  /// </summary>
  private static (ManualSource Manual, BeatGrid Grid, FakeClock Clock) NewSource() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = new BeatGrid(clock, quantum: 4, TimeSpan.FromSeconds(2.5));

    SyncSourceManager manager = new SyncSourceManager(
      grid,
      Options.Create(new BeatOptions { DefaultSource = ManualSource.SourceName }),
      NullLogger<SyncSourceManager>.Instance);

    return (new ManualSource(clock, manager), grid, clock);
  }
}
