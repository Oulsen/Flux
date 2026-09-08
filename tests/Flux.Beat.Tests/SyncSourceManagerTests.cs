using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Beat.Tests;

public class SyncSourceManagerTests {
  private static readonly BeatTimeline AtOneTwenty = new BeatTimeline(
    MicrosPerBeat: 500_000,
    BeatOrigin: 0,
    TimeOriginMicros: 0);

  private static readonly BeatTimeline AtOneSeventyFive = new BeatTimeline(
    MicrosPerBeat: 342_857,
    BeatOrigin: 0,
    TimeOriginMicros: 0);

  /// <summary>
  /// The rule the class exists for: every source publishes all the time, and only
  /// the active one's timelines reach the grid. Without it, two sources with
  /// different ideas of the tempo would fight over the light show at whatever
  /// rate they each publish.
  /// </summary>
  [Fact]
  public void ForwardsOnlyTheActiveSourcesTimelines() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    ITimelineSink audio = manager.Register(new FakeSource("audio"));

    audio.Publish(AtOneSeventyFive);
    Assert.False(grid.TryGetSnapshot(out _));

    link.Publish(AtOneTwenty);
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(120, snapshot.Bpm, precision: 2);

    // And the inactive source still cannot get a word in.
    audio.Publish(AtOneSeventyFive);
    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot unchanged));
    Assert.Equal(120, unchanged.Bpm, precision: 2);
  }

  /// <summary>
  /// The configured default wins whenever it registers, because registration
  /// order is an accident of dependency-injection wiring: Manual is registered
  /// before Link today, and a rule of "first one wins" would silently make Manual
  /// the startup source.
  /// </summary>
  [Fact]
  public void MakesTheConfiguredDefaultActiveWhateverOrderSourcesRegister() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    manager.Register(new FakeSource("manual"));
    Assert.Equal("manual", manager.ActiveSource);

    manager.Register(new FakeSource("link"));
    Assert.Equal("link", manager.ActiveSource);
  }

  /// <summary>
  /// A typo in the setting costs the user their preference, not their light show.
  /// </summary>
  [Fact]
  public void FallsBackToTheFirstSourceWhenTheDefaultIsNotRegistered() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "telepathy");

    manager.Register(new FakeSource("manual"));
    manager.Register(new FakeSource("link"));

    Assert.Equal("manual", manager.ActiveSource);
  }

  /// <summary>
  /// Why the last timeline is cached for every source, active or not: selecting a
  /// source has to take effect now, not once that source next works out a tempo.
  /// A manual tempo set two minutes ago is still the right answer.
  /// </summary>
  [Fact]
  public void SwitchingAppliesTheCachedTimelineOfAWarmSource() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    ITimelineSink manual = manager.Register(new FakeSource("manual"));

    link.Publish(AtOneTwenty);
    manual.Publish(AtOneSeventyFive);

    Assert.True(manager.TrySelect("manual"));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(175, snapshot.Bpm, precision: 2);
  }

  /// <summary>
  /// A source switch renumbers the beat wholesale even when the two tempi match,
  /// so it is announced as a rebasing and never counted as extrapolation drift.
  /// Counting it would pin the one metric that says whether the grid is sound.
  /// </summary>
  [Fact]
  public void DoesNotCountASourceSwitchAsExtrapolationDrift() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    ITimelineSink manual = manager.Register(new FakeSource("manual"));

    link.Publish(AtOneTwenty);
    clock.Advance(TimeSpan.FromSeconds(30));
    manual.Publish(AtOneTwenty with { BeatOrigin = 500 });

    Assert.True(manager.TrySelect("manual"));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(0, snapshot.MaxReanchorJumpBeats, precision: 9);
  }

  /// <summary>
  /// Selecting a source that has never published must leave the grid reporting
  /// nothing. Keeping the outgoing source's timeline would show its tempo on
  /// screen under the new source's name, and a confidently wrong BPM is the one
  /// reading a beat display must never invent.
  /// </summary>
  [Fact]
  public void SwitchingToASourceThatHasNeverPublishedReportsNothing() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    manager.Register(new FakeSource("audio"));

    link.Publish(AtOneTwenty);
    Assert.True(grid.TryGetSnapshot(out _));

    Assert.True(manager.TrySelect("audio"));

    Assert.False(grid.TryGetSnapshot(out _));
    Assert.Null(grid.Timeline);
  }

  [Fact]
  public void RefusesToSelectASourceThatIsNotRegistered() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");
    manager.Register(new FakeSource("link"));

    Assert.False(manager.TrySelect("audio"));
    Assert.False(manager.TrySelect(""));
    Assert.False(manager.TrySelect("  "));
    Assert.Equal("link", manager.ActiveSource);
  }

  [Fact]
  public void SelectingTheAlreadyActiveSourceIsAcceptedAndChangesNothing() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    link.Publish(AtOneTwenty);
    clock.Advance(TimeSpan.FromMilliseconds(250));

    Assert.True(manager.TrySelect("link"));

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(1, snapshot.Packets);
    Assert.Equal(0.5, snapshot.Beat, precision: 6);
  }

  /// <summary>
  /// Source names are the protocol's own, so two sources answering to one name
  /// would make the selector ambiguous on the wire. Louder as a startup failure
  /// than as a mystery on stage.
  /// </summary>
  [Fact]
  public void RefusesTwoSourcesWithTheSameName() {
    SyncSourceManager manager = NewManager(NewGrid(new FakeClock()), defaultSource: "link");
    manager.Register(new FakeSource("link"));

    Assert.Throws<InvalidOperationException>(() => manager.Register(new FakeSource("LINK")));
  }

  /// <summary>
  /// Every source reports, not just the active one. This is the whole input to
  /// the source selector: choosing between Link and audio means seeing which of
  /// them currently has a signal, without having to switch and watch the lights
  /// die to find out.
  /// </summary>
  [Fact]
  public void ReportsTheHealthOfInactiveSourcesToo() {
    SyncSourceManager manager = NewManager(NewGrid(new FakeClock()), defaultSource: "link");

    manager.Register(new FakeSource("link") {
      Reported = new SyncSourceStatus(SyncSourceState.Quiet, Confidence: 1),
    });

    manager.Register(new FakeSource("audio") {
      Reported = new SyncSourceStatus(SyncSourceState.Live, Confidence: 0.8),
    });

    IReadOnlyList<SyncSourceReport> reports = manager.Sources();
    Assert.Equal(2, reports.Count);

    SyncSourceReport link = reports.Single(report => report.Name == "link");
    Assert.Equal(SyncSourceState.Quiet, link.Status.State);
    Assert.True(link.IsActive);

    SyncSourceReport audio = reports.Single(report => report.Name == "audio");
    Assert.Equal(SyncSourceState.Live, audio.Status.State);
    Assert.Equal(0.8, audio.Status.Confidence, precision: 6);
    Assert.False(audio.IsActive);
  }

  /// <summary>
  /// A downbeat is a statement about the music in the room, not one source's
  /// opinion about the tempo, so a tap aligns the bar while Link is still timing
  /// it. This is the only way a bar ever gets aligned, since no source transmits
  /// a downbeat at all.
  /// </summary>
  [Fact]
  public void AligningTheBarWorksFromAnInactiveSourcesSink() {
    FakeClock clock = new FakeClock();
    BeatGrid grid = NewGrid(clock);
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    ITimelineSink manual = manager.Register(new FakeSource("manual"));

    link.Publish(AtOneTwenty);
    clock.Advance(TimeSpan.FromSeconds(2.5));

    manual.AlignBar(BarAlignmentMethod.Tap);

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.True(snapshot.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Tap, snapshot.AlignmentMethod);
    Assert.Equal(1, snapshot.PositionInBar);
  }

  /// <summary>
  /// Withdrawing is the opposite of publishing rather than a way of reporting
  /// silence. A quiet source still has a last tempo worth extrapolating; a
  /// source that has retracted its tempo has nothing, and extrapolating it
  /// anyway would put a number on stage that nothing stands behind.
  /// </summary>
  [Fact]
  public void WithdrawingLeavesTheActiveSourceWithNothingToReport() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "manual");

    ITimelineSink manual = manager.Register(new FakeSource("manual"));

    manual.Publish(AtOneTwenty);
    Assert.True(grid.TryGetSnapshot(out _));

    manual.Withdraw();

    Assert.False(grid.TryGetSnapshot(out _));
  }

  [Fact]
  public void WithdrawingAnInactiveSourceLeavesTheGridAlone() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    ITimelineSink manual = manager.Register(new FakeSource("manual"));

    manual.Publish(AtOneSeventyFive);
    link.Publish(AtOneTwenty);

    manual.Withdraw();

    Assert.True(grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(120, snapshot.Bpm, precision: 2);
  }

  /// <summary>
  /// The cached timeline goes with the withdrawal, so selecting that source
  /// later finds nothing rather than resurrecting the tempo the user retracted.
  /// </summary>
  [Fact]
  public void SelectingASourceThatWithdrewFindsNothing() {
    BeatGrid grid = NewGrid(new FakeClock());
    SyncSourceManager manager = NewManager(grid, defaultSource: "link");

    ITimelineSink link = manager.Register(new FakeSource("link"));
    ITimelineSink manual = manager.Register(new FakeSource("manual"));

    manual.Publish(AtOneSeventyFive);
    manual.Withdraw();
    link.Publish(AtOneTwenty);

    Assert.True(manager.TrySelect("manual"));
    Assert.False(grid.TryGetSnapshot(out _));
  }

  [Fact]
  public void HasNoActiveSourceUntilOneRegisters() {
    SyncSourceManager manager = NewManager(NewGrid(new FakeClock()), defaultSource: "link");

    Assert.Null(manager.ActiveSource);
    Assert.Empty(manager.Sources());
  }

  private static BeatGrid NewGrid(IMonotonicClock clock) {
    return new BeatGrid(clock, quantum: 4, TimeSpan.FromSeconds(2.5));
  }

  private static SyncSourceManager NewManager(BeatGrid grid, string defaultSource) {
    return new SyncSourceManager(
      grid,
      Options.Create(new BeatOptions { DefaultSource = defaultSource }),
      NullLogger<SyncSourceManager>.Instance);
  }

  /// <summary>
  /// A source that publishes nothing on its own, so a test can drive the sink by
  /// hand and state exactly what health it is reporting.
  /// </summary>
  private sealed class FakeSource(string name): ISyncSource {
    public string Name => name;

    public SyncSourceStatus Reported { get; init; } =
      new SyncSourceStatus(SyncSourceState.Waiting, Confidence: 0);

    public SyncSourceStatus Status() {
      return this.Reported;
    }
  }
}
