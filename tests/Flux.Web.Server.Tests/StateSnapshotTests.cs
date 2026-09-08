using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// What a state frame says about the sources behind it.
///
/// These read the composed <see cref="StateData"/> rather than the encoded
/// bytes, which is the opposite of <see cref="WireFormatTests"/> and deliberate:
/// the wire tests guard the field names a browser depends on, and these guard
/// the two facts that span the grid and the sources - that a null grid means
/// waiting, and that the counters on show belong to the source on show.
/// </summary>
public sealed class StateSnapshotTests {
  private static readonly BeatTimeline AtOneTwenty = new BeatTimeline(
    MicrosPerBeat: 500_000,
    BeatOrigin: 0,
    TimeOriginMicros: 0);

  [Fact]
  public void ReportsEveryRegisteredSourceAndNamesTheActiveOne() {
    Rig rig = NewRig(defaultSource: "link");
    ITimelineSink link = rig.Register(new FakeSource("link", SyncSourceState.Live, confidence: 1));
    rig.Register(new FakeSource("audio", SyncSourceState.Waiting, confidence: 0.4));

    link.Publish(AtOneTwenty);

    StateData state = rig.Broadcaster.Sample();

    Assert.Equal("link", state.Source);
    Assert.Equal("live", state.SourceState);
    Assert.Equal(2, state.Sources.Count);

    SourceReportData audio = state.Sources.Single(source => source.Name == "audio");
    Assert.Equal("waiting", audio.State);
    Assert.Equal(0.4, audio.Confidence, precision: 6);
  }

  /// <summary>
  /// The null grid is the whole reason this state exists: an absent tempo sent
  /// as zero renders as a confident 0.00 BPM.
  /// </summary>
  [Fact]
  public void SaysWaitingWithNoGridUntilTheActiveSourcePublishes() {
    Rig rig = NewRig(defaultSource: "link");
    rig.Register(new FakeSource("link", SyncSourceState.Waiting, confidence: 0));

    StateData state = rig.Broadcaster.Sample();

    Assert.Equal("waiting", state.SourceState);
    Assert.Null(state.Grid);
    Assert.False(state.Alignment.Aligned);
    Assert.Null(state.Alignment.Method);
  }

  /// <summary>
  /// Only the source knows whether its silence means anything: a Link session
  /// gone quiet is a worsening guess, while a typed tempo left alone is exactly
  /// as true as when it was typed. The grid cannot tell those apart, so it does
  /// not get to decide this.
  /// </summary>
  [Fact]
  public void SaysQuietOnlyWhenTheActiveSourceDoes() {
    Rig quiet = NewRig(defaultSource: "link");
    quiet.Register(new FakeSource("link", SyncSourceState.Quiet, confidence: 1)).Publish(AtOneTwenty);

    Assert.Equal("quiet", quiet.Broadcaster.Sample().SourceState);

    Rig live = NewRig(defaultSource: "typed");
    live.Register(new FakeSource("typed", SyncSourceState.Live, confidence: 1)).Publish(AtOneTwenty);

    Assert.Equal("live", live.Broadcaster.Sample().SourceState);
  }

  [Fact]
  public void CarriesTheCountersOfTheActiveSourceAndNoOthers() {
    Rig rig = NewRig(defaultSource: "link");

    ITimelineSink link = rig.Register(new FakeSource("link", SyncSourceState.Live, confidence: 1) {
      Counters = new SyncSourceDiagnostics(Packets: 4821),
    });

    ITimelineSink audio = rig.Register(new FakeSource("audio", SyncSourceState.Live, confidence: 0.8) {
      Counters = new SyncSourceDiagnostics(Fps: 59.4, BeatsDetected: 212, BpmLocked: true),
    });

    link.Publish(AtOneTwenty);

    DiagnosticsData onLink = rig.Broadcaster.Sample().Diagnostics;
    Assert.Equal(4821, onLink.Packets);
    Assert.Null(onLink.Fps);
    Assert.Null(onLink.BpmLocked);

    audio.Publish(AtOneTwenty);
    Assert.True(rig.Sources.TrySelect("audio"));

    DiagnosticsData onAudio = rig.Broadcaster.Sample().Diagnostics;
    Assert.Equal(59.4, onAudio.Fps);
    Assert.Equal(212, onAudio.BeatsDetected);
    Assert.True(onAudio.BpmLocked);
    Assert.Null(onAudio.Packets);
  }

  /// <summary>
  /// The bar anchor is a beat index, so a source switch renumbers it away. The
  /// method has to go with it: an alignment still labelled "tap" after the tap
  /// stopped meaning anything is worse than no label.
  /// </summary>
  [Fact]
  public void ForgetsTheAlignmentWhenTheSourceChanges() {
    Rig rig = NewRig(defaultSource: "link");
    ITimelineSink link = rig.Register(new FakeSource("link", SyncSourceState.Live, confidence: 1));
    ITimelineSink tapping = rig.Register(new FakeSource("typed", SyncSourceState.Live, confidence: 1));

    link.Publish(AtOneTwenty);
    tapping.AlignBar(BarAlignmentMethod.Tap);

    StateData aligned = rig.Broadcaster.Sample();
    Assert.True(aligned.Alignment.Aligned);
    Assert.Equal("tap", aligned.Alignment.Method);

    Assert.True(rig.Sources.TrySelect("typed"));

    StateData switched = rig.Broadcaster.Sample();
    Assert.False(switched.Alignment.Aligned);
    Assert.Null(switched.Alignment.Method);
  }

  /// <summary>
  /// A build with no sources at all cannot happen through the standard
  /// registrations, but the frame still has to say something rather than name a
  /// source that is not there.
  /// </summary>
  [Fact]
  public void NamesNoSourceWhenTheBuildHasNone() {
    StateData state = NewRig(defaultSource: "link").Broadcaster.Sample();

    Assert.Equal("none", state.Source);
    Assert.Empty(state.Sources);
    Assert.Equal("waiting", state.SourceState);
  }

  private static Rig NewRig(string defaultSource) {
    MonotonicClock clock = new MonotonicClock();
    BeatOptions beat = new BeatOptions { DefaultSource = defaultSource };
    BeatGrid grid = new BeatGrid(clock, beat.Quantum, beat.StaleAfter);

    SyncSourceManager sources = new SyncSourceManager(
      grid,
      Options.Create(beat),
      NullLogger<SyncSourceManager>.Instance);

    BeatBroadcaster broadcaster = new BeatBroadcaster(
      NewIdleHub(clock, beat),
      new BeatScheduler(grid, clock, NullLogger<BeatScheduler>.Instance),
      grid,
      sources,
      Options.Create(new WebOptions()));

    return new Rig(broadcaster, sources);
  }

  /// <summary>
  /// A hub with a beat pipeline of its own, purely because the broadcaster's
  /// constructor asks for one.
  ///
  /// Separate on purpose rather than out of laziness: the hub needs a
  /// <see cref="ManualSource"/>, which registers itself as "manual" the moment
  /// it is built, and a test source list is worth more when it holds exactly
  /// what the test put in it. Nothing here is ever sampled - with no client
  /// connected, the broadcaster never asks this hub for anything.
  /// </summary>
  private static SocketHub NewIdleHub(IMonotonicClock clock, BeatOptions beat) {
    SyncSourceManager spare = new SyncSourceManager(
      new BeatGrid(clock, beat.Quantum, beat.StaleAfter),
      Options.Create(new BeatOptions()),
      NullLogger<SyncSourceManager>.Instance);

    return new SocketHub(
      Options.Create(new WebOptions()),
      Options.Create(beat),
      spare,
      new ManualSource(clock, spare),
      audio: null,
      NullLogger<SocketHub>.Instance);
  }

  /// <summary>The broadcaster under test and the manager whose sources it reads.</summary>
  private sealed record Rig(BeatBroadcaster Broadcaster, SyncSourceManager Sources) {
    public ITimelineSink Register(ISyncSource source) {
      return this.Sources.Register(source);
    }
  }

  /// <summary>
  /// A source that reports whatever a test tells it to and publishes only when
  /// told, so the grid and the source can be put into states that disagree.
  /// </summary>
  private sealed class FakeSource(string name, SyncSourceState state, double confidence): ISyncSource {
    public string Name => name;

    public SyncSourceDiagnostics Counters { get; init; }

    public SyncSourceStatus Status() {
      return new SyncSourceStatus(state, confidence, this.Counters);
    }
  }
}
