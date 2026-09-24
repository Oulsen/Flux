using Flux.Beat;
using Flux.Beat.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Audio.Tests;

/// <summary>
/// What audio detection does to the beat grid, and what it says about itself while
/// doing it.
///
/// <see cref="AudioBeatDetectorTests"/> covers the arithmetic of finding a beat.
/// These cover the join: that a found beat becomes a timeline the shared grid
/// adopts, that a drop anchors the bar, that the source describes its own health
/// honestly enough for the selector to be trusted, and that audio which stops
/// arriving takes its tempo with it rather than leaving one on stage.
///
/// The frames are handed over directly rather than produced by a sound card, which
/// is the same seam <see cref="IAudioSamples"/> opens one layer down and the only
/// reason any of this is testable on a machine with no audio hardware.
/// </summary>
public sealed class AudioSyncSourceTests {
  private const double Bpm = 128;

  [Fact]
  public void PublishesTheTempoItFindsToTheSharedGrid() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.InRange(snapshot.Bpm, 125, 131);

    SyncSourceStatus status = rig.Source.Status();
    Assert.Equal(SyncSourceState.Live, status.State);
    Assert.True(status.Confidence > 0.6, $"A locked tempo reported a confidence of {status.Confidence}.");
  }

  /// <summary>
  /// Waiting is the truth about a source that has heard audio and found no beat in
  /// it, and the grid has to stay empty for it: a tempo of zero rendered as
  /// 0.00 BPM is the one reading a beat display must never invent.
  /// </summary>
  [Fact]
  public void SaysWaitingAndPublishesNothingUntilItFindsABeat() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Quiet(2_000));

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Equal(SyncSourceState.Waiting, rig.Source.Status().State);
    Assert.Equal(0, rig.Source.Bpm);
  }

  /// <summary>
  /// Liveness is measured from the last onset and never from the last frame, and
  /// this is the case that forces it: a stopped deck still delivers audio, so the
  /// analyzer keeps producing frames out of bit-exact zeros. A source calling itself
  /// live on that basis would be claiming a tempo nobody in the room can hear.
  /// </summary>
  [Fact]
  public void GoesQuietWhenTheBeatsStopEvenThoughFramesKeepArriving() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));
    Assert.Equal(SyncSourceState.Live, rig.Source.Status().State);

    rig.Play(new FrameTrack().Quiet(4_000));

    Assert.Equal(SyncSourceState.Quiet, rig.Source.Status().State);

    // Quiet is not gone: the grid keeps extrapolating a tempo that was real a
    // moment ago, and says so, rather than going dark.
    Assert.True(rig.Grid.TryGetSnapshot(out _));
  }

  [Fact]
  public void AnchorsTheBarToADropAndLabelsItAsOne() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));

    // No source transmits a downbeat, so a correct count from an arbitrary
    // starting point is the most the grid can have until something anchors it.
    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot beforeDrop));
    Assert.False(beforeDrop.IsBarAligned);

    rig.Play(new FrameTrack().Quiet(1_500).Beats(Bpm, 4));

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot afterDrop));

    // Anchored, and labelled by what anchored it. A drop is a guess the detector
    // made, so showing which method placed the downbeat is what lets a wrong one be
    // recognised as wrong instead of as a bar that mysteriously moved.
    Assert.True(afterDrop.IsBarAligned);
    Assert.Equal(BarAlignmentMethod.Drop, afterDrop.AlignmentMethod);
  }

  [Fact]
  public void OffersTheNewestFrameForDrawingAndThenNothingAtAll() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));

    SpectrumSnapshot? drawn = rig.Source.Spectrum();
    Assert.NotNull(drawn);
    Assert.Equal(AudioConstants.DisplayBins, drawn.Bins.Count);

    rig.Clock.Advance(TimeSpan.FromMilliseconds(400));

    // Null and not the last frame before the audio stopped. A spectrum frozen at
    // whatever was playing is the most misleading picture the instrument could
    // draw, because it looks exactly like music.
    Assert.Null(rig.Source.Spectrum());
  }

  /// <summary>
  /// A verdict lives on exactly one analyzer frame, and the visualizer samples at
  /// half the frame rate, so a verdict published only on its own frame is sampled
  /// away about half the time - a beat the detector accepted then never reaches the
  /// screen, and the instrument reports a miss that did not happen. The strongest
  /// verdict is therefore latched until a read consumes it, and drawn exactly once.
  /// </summary>
  [Fact]
  public void LatchesTheBeatVerdictUntilARedraw() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));

    // The track ends between beats, so the newest frame's own verdict is None. The
    // latch is what carries the accepted beat across to the read.
    SpectrumSnapshot? drawn = rig.Source.Spectrum();
    Assert.NotNull(drawn);
    Assert.NotEqual(BeatStrength.None, drawn.Strength);

    // Consumed: the same beat must not flash twice.
    Assert.Equal(BeatStrength.None, rig.Source.Spectrum()?.Strength);
  }

  /// <summary>
  /// A gap in the frames means the device closed or changed, not that a frame ran
  /// late, and everything derived from the audio before it is dropped: spectral flux
  /// across such a gap measures the join between two unrelated pieces of audio, and
  /// a discontinuity is precisely what this detector calls a beat.
  ///
  /// The tempo goes with it. Unlike a Link session falling silent, there is no
  /// estimate left standing behind the number, so leaving it on screen would be
  /// reporting a tempo nothing in the room supports.
  /// </summary>
  [Fact]
  public void RetractsItsTempoWhenTheAudioStopsArriving() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));
    Assert.True(rig.Grid.TryGetSnapshot(out _));

    rig.PlayAfterSilence(TimeSpan.FromSeconds(1));

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Equal(SyncSourceState.Waiting, rig.Source.Status().State);
    Assert.Equal(0, rig.Source.Bpm);

    // The frame that arrived after the gap is still worth drawing: audio is back,
    // and the picture is of the audio rather than of the tempo. What it carries no
    // opinion about yet is the beat, which is the whole point of the reset.
    Assert.Equal(BeatStrength.None, rig.Source.Spectrum()?.Strength);
  }

  /// <summary>
  /// A device switch is faster than the gap test can see: the capture buffer
  /// refills in about 43 ms, so the frames resume within a tick or two while the
  /// analyzer's smoothing restarts from silence. The frame's generation is what
  /// carries the news, and everything derived from the old device has to go with
  /// it - the climb out of a cleared spectrum measured against the old device's
  /// flux history is a phantom onset, arriving at the exact moment the user
  /// switched devices.
  /// </summary>
  [Fact]
  public void RetractsItsTempoWhenTheDeviceChangesWithoutALongGap() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));
    Assert.True(rig.Grid.TryGetSnapshot(out _));

    rig.PlayAfterDeviceSwitch();

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Equal(SyncSourceState.Waiting, rig.Source.Status().State);
    Assert.Equal(0, rig.Source.Bpm);
  }

  /// <summary>
  /// A breakdown is not a gap in the frames. It is 1.5 seconds of quiet audio,
  /// which the detector reads as a breakdown and this class must not read as the
  /// device going away: resetting there would throw away the tempo the drop is
  /// about to re-anchor.
  /// </summary>
  [Fact]
  public void KeepsItsTempoThroughABreakdown() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));
    rig.Play(new FrameTrack().Quiet(1_500));

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.InRange(snapshot.Bpm, 125, 131);
  }

  [Fact]
  public void ReportsTheCountersTheDiagnosticsRowShows() {
    Rig rig = new Rig();

    rig.Play(new FrameTrack().Warmup().Beats(Bpm, 16));

    SyncSourceDiagnostics counters = rig.Source.Status().Diagnostics;

    Assert.Equal(16, counters.BeatsDetected);
    Assert.True(counters.BpmLocked);
    Assert.NotNull(counters.LastBeatAgoMs);
    Assert.NotNull(counters.Fps);

    // The Link counter belongs to Link. A source reporting another source's
    // counters would put a number in the diagnostics row that nothing measured.
    Assert.Null(counters.Packets);
    Assert.Null(counters.Taps);
  }

  /// <summary>
  /// The source under test, wired to a real grid and manager so the assertions are
  /// about what the rest of the app would see rather than about what this class
  /// meant to say.
  /// </summary>
  private sealed class Rig {
    private const double FramePeriodMicros = 1_000_000.0 / AudioConstants.FrameRateHz;

    private readonly AudioSyncSource source;

    public Rig() {
      BeatOptions beat = new BeatOptions();

      this.Clock = new FakeClock();
      this.Grid = new BeatGrid(this.Clock, beat.Quantum, beat.StaleAfter);

      SyncSourceManager sources = new SyncSourceManager(
        this.Grid,
        Options.Create(beat),
        settings: null,
        NullLogger<SyncSourceManager>.Instance);

      // Never started: the analyzer is here for its frame-rate reading and as the
      // event this source subscribes to in a real host. The frames are handed over
      // directly instead, which is what makes the timing in these tests exact.
      SpectrumAnalyzer analyzer = new SpectrumAnalyzer(
        new NoSamples(),
        this.Clock,
        NullLogger<SpectrumAnalyzer>.Instance,
        bands: null);

      this.source = new AudioSyncSource(
        analyzer,
        sources,
        this.Clock,
        Options.Create(new AudioOptions()),
        Options.Create(beat),
        NullLogger<AudioSyncSource>.Instance);
    }

    public FakeClock Clock { get; }

    public BeatGrid Grid { get; }

    public AudioSyncSource Source => this.source;

    /// <summary>
    /// Feeds a run of frames, keeping the clock in step with their timestamps.
    ///
    /// The clock matters as much as the frames: the detector times everything from
    /// the timestamp it is handed, while the source reports its own liveness against
    /// the clock, and a test where those two disagreed would prove nothing about
    /// either.
    ///
    /// Every track numbers its frames from the same origin, so they are rebased onto
    /// the clock as they are played. That is what lets a test play two tracks in a
    /// row and have the second one continue the first, rather than handing the
    /// detector a run of frames whose timestamps go backwards.
    /// </summary>
    public void Play(FrameTrack track) {
      if(track.Frames.Count == 0) {
        return;
      }

      double offset = this.Clock.NowMicros + FramePeriodMicros - track.Frames[0].TimestampMicros;

      foreach(SpectrumFrame frame in track.Frames) {
        SpectrumFrame continued = frame with { TimestampMicros = frame.TimestampMicros + offset };

        this.AdvanceTo(continued.TimestampMicros);
        this.source.Observe(continued);
      }
    }

    /// <summary>
    /// Delivers one frame after a gap, which is what a closed or swapped device
    /// looks like from here: the frames simply stop and then resume.
    /// </summary>
    public void PlayAfterSilence(TimeSpan gap) {
      this.Clock.Advance(gap);

      this.source.Observe(new SpectrumFrame(
        this.Clock.NowMicros,
        48_000,
        new int[AudioConstants.DisplayBins],
        Kick: 0.5,
        Mids: 0.05,
        Highs: 0.01));
    }

    /// <summary>
    /// Delivers one frame from a new capture generation only two frame periods
    /// later, which is what a fast device switch looks like: a gap far too short
    /// for the timestamps to distinguish from a frame that ran late, so the
    /// generation is the only thing announcing the change.
    /// </summary>
    public void PlayAfterDeviceSwitch() {
      this.Clock.Advance(TimeSpan.FromMilliseconds(2 * FramePeriodMicros / 1000.0));

      this.source.Observe(new SpectrumFrame(
        this.Clock.NowMicros,
        48_000,
        new int[AudioConstants.DisplayBins],
        Kick: 0.5,
        Mids: 0.05,
        Highs: 0.01,
        Generation: 1));
    }

    private void AdvanceTo(double micros) {
      double ahead = micros - this.Clock.NowMicros;
      if(ahead > 0) {
        this.Clock.Advance(TimeSpan.FromMilliseconds(ahead / 1000.0));
      }
    }
  }

  /// <summary>
  /// A sample source with nothing in it. The analyzer in these tests never runs its
  /// thread, so this exists only to satisfy the constructor.
  /// </summary>
  private sealed class NoSamples: IAudioSamples {
    public int SampleRate => 0;

    public bool TryReadLatest(Span<float> destination) {
      return false;
    }
  }
}
