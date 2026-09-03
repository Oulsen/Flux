using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Audio;

/// <summary>
/// The tempo taken from the music itself: the analyzer's frames run through the
/// beat detector, and the result published as the <c>audio</c> sync source.
///
/// It is the join between two halves that are deliberately ignorant of each other.
/// <see cref="SpectrumAnalyzer"/> knows about audio and nothing about beats;
/// <see cref="AudioBeatDetector"/> is a pure function over frames and knows
/// nothing about sinks, threads or which source happens to be selected. This class
/// is the only place that holds both, which is also why it is the only place that
/// can answer the visualizer: <c>flux</c> and <c>beat</c> are the detector's
/// verdict, not the analyzer's measurement, and a picture carrying one without the
/// other cannot show whether the detector agreed with the music.
///
/// Nothing here decides when a beat fires. The detector hands back a timeline and
/// this publishes it, exactly as the Link listener publishes one off the wire -
/// which is what makes an audio tempo interchangeable with a Link tempo rather
/// than a second opinion running beside it.
/// </summary>
internal sealed class AudioSyncSource: ISyncSource, IAudioSpectrum, IHostedService {
  /// <summary>This source's name in the protocol.</summary>
  public const string SourceName = "audio";

  /// <summary>
  /// A gap between frames longer than this means capture stopped rather than ran
  /// late, and everything derived from the audio before it is dropped.
  ///
  /// Thirty frames, where a frame the analyzer fires late is one or two. The
  /// distinction is not tidiness: spectral flux is a difference between
  /// consecutive frames, so carrying state across a gap of unknown length measures
  /// the join between two unrelated pieces of audio, and a discontinuity is
  /// precisely what this detector calls a beat.
  /// </summary>
  private const double ResetAfterGapMillis = 500;

  /// <summary>
  /// How long the last frame stays worth drawing.
  ///
  /// Short, because the visualizer is an instrument: a spectrum held on screen
  /// after the audio stopped is the most misleading thing it could show, since it
  /// looks like music. Longer than a few frames, because a frame arriving late must
  /// not make the picture flicker.
  /// </summary>
  private const double SpectrumStaleMillis = 250;

  private readonly SpectrumAnalyzer analyzer;
  private readonly AudioBeatDetector detector;
  private readonly IMonotonicClock clock;
  private readonly ITimelineSink sink;
  private readonly double staleAfterMicros;
  private readonly ILogger<AudioSyncSource> log;

  /// <summary>
  /// Guards what the analyzer thread publishes and the web layer reads. Sixty
  /// writes a second against twenty or thirty reads, both holding it for a handful
  /// of field assignments, so it is never meaningfully contended - and the
  /// alternative, a scatter of volatile doubles that have to agree with each other,
  /// would only be harder to read.
  /// </summary>
  private readonly object statusGate = new object();

  /// <summary>
  /// The previous frame's timestamp, touched only on the analyzer thread and
  /// therefore deliberately outside the lock: it is how this class recognises a gap
  /// in the audio, which is a question only the thread reading the audio can ask.
  /// </summary>
  private double previousFrameMicros;

  private SpectrumSnapshot? latest;
  private double lastFrameMicros;
  private double? lastBeatMicros;
  private double bpm;
  private double confidence;
  private long beatsDetected;
  private bool locked;
  private bool published;

  public AudioSyncSource(
    SpectrumAnalyzer analyzer,
    SyncSourceManager sources,
    IMonotonicClock clock,
    IOptions<AudioOptions> audio,
    IOptions<BeatOptions> beat,
    ILogger<AudioSyncSource> log) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(audio);
    ArgumentNullException.ThrowIfNull(beat);

    this.analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.detector = new AudioBeatDetector(audio.Value.Sensitivity);
    this.staleAfterMicros = beat.Value.StaleAfter.TotalMilliseconds * 1000.0;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    // Registered last, so every field is set before the manager can hand this
    // reference to anyone. Status() reads only fields and answers Waiting from
    // their defaults, which is the truth about a source that has heard nothing.
    this.sink = sources.Register(this);
  }

  /// <inheritdoc/>
  public string Name => SourceName;

  /// <summary>
  /// What this source knows, whether or not it is the active one.
  ///
  /// Liveness is measured in beats rather than in frames, and that is the one
  /// non-obvious decision here. Frames keep arriving through a stopped deck: the
  /// endpoint delivers bit-exact zeros, the analyzer dutifully transforms them
  /// sixty times a second, and a source reporting itself live on that basis would
  /// be claiming a tempo nobody can hear. An onset is the only evidence this source
  /// ever has, so silence is measured from the last one - which makes its
  /// <see cref="SyncSourceState.Quiet"/> mean the same thing as the Link
  /// listener's: the tempo on screen is now an extrapolation, and it is getting
  /// worse.
  /// </summary>
  public SyncSourceStatus Status() {
    lock(this.statusGate) {
      SyncSourceDiagnostics diagnostics = new SyncSourceDiagnostics(
        Fps: this.analyzer.Fps,
        BeatsDetected: this.beatsDetected,
        BpmLocked: this.locked,
        LastBeatAgoMs: this.lastBeatMicros is double at ? (this.clock.NowMicros - at) / 1000.0 : null);

      if(!this.published) {
        return new SyncSourceStatus(SyncSourceState.Waiting, Confidence: 0, diagnostics);
      }

      bool quiet = this.lastBeatMicros is not double beatAt
        || this.clock.NowMicros - beatAt > this.staleAfterMicros;

      return new SyncSourceStatus(
        quiet ? SyncSourceState.Quiet : SyncSourceState.Live,
        this.confidence,
        diagnostics);
    }
  }

  /// <inheritdoc/>
  public SpectrumSnapshot? Spectrum() {
    lock(this.statusGate) {
      return this.clock.NowMicros - this.lastFrameMicros > SpectrumStaleMillis * 1000.0
        ? null
        : this.latest;
    }
  }

  /// <summary>
  /// The tempo estimate, or zero before there is one. For the console readout and
  /// the tests; the grid learns it through the sink.
  /// </summary>
  public double Bpm {
    get {
      lock(this.statusGate) {
        return this.bpm;
      }
    }
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    // Subscribed here rather than in the constructor, following the recorder: the
    // analyzer raises this on its own thread, and a handler attached from a
    // half-built object is a race nobody needs. A few frames analyzed before this
    // runs cost nothing, since the detector discards its own first twenty anyway.
    this.analyzer.Analyzed += this.Observe;

    this.log.LogInformation("Audio beat detection is running as the '{Source}' sync source.", SourceName);

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    this.analyzer.Analyzed -= this.Observe;

    return Task.CompletedTask;
  }

  /// <summary>
  /// Judges one frame and publishes what follows from it.
  ///
  /// Runs on the analyzer thread, once per frame, and does nothing that can wait:
  /// this method's own duration is subtracted from the next frame's budget, and the
  /// frame rate is what every history window in
  /// <see cref="AudioConstants"/> is measured in.
  ///
  /// Internal so the tests can drive it with frames they built. That is the only
  /// way to test this class at all - the alternative is a sound card, a track and a
  /// stopwatch - and it is the same seam <see cref="IAudioSamples"/> opens one layer
  /// down.
  /// </summary>
  internal void Observe(SpectrumFrame frame) {
    ArgumentNullException.ThrowIfNull(frame);

    if(this.previousFrameMicros > 0
      && (frame.TimestampMicros - this.previousFrameMicros) / 1000.0 > ResetAfterGapMillis) {
      this.Forget();
    }

    this.previousFrameMicros = frame.TimestampMicros;

    BeatDecision decision = this.detector.Observe(frame);

    lock(this.statusGate) {
      this.latest = new SpectrumSnapshot(
        frame.Bins,
        frame.Kick,
        frame.Mids,
        frame.Highs,
        decision.Flux,
        decision.Strength);

      this.lastFrameMicros = frame.TimestampMicros;
      this.lastBeatMicros = decision.LastBeatMicros;
      this.bpm = decision.Bpm;
      this.confidence = decision.Confidence;
      this.beatsDetected = decision.BeatsDetected;
      this.locked = decision.Locked;
      this.published |= decision.Timeline is not null;
    }

    // Published before the bar is aligned, never after, which is the ordering a
    // tapped tempo needs for the same reason: publishing a rebased timeline clears
    // any existing anchor, so aligning first would set an anchor and then
    // immediately throw it away.
    if(decision.Timeline is BeatTimeline timeline) {
      this.sink.Publish(timeline, decision.Rebase);
    }

    if(decision.Drop) {
      // The one downbeat audio analysis can find without being told. It is a guess
      // and is labelled as one: BarAlignmentMethod.Drop travels with the anchor, so
      // a wrong drop is visible on screen as a wrong drop rather than as a bar that
      // mysteriously moved.
      this.sink.AlignBar(BarAlignmentMethod.Drop);
      this.log.LogInformation("Audio detection placed a drop, and the bar is anchored to it.");
    }
  }

  /// <summary>
  /// Drops everything derived from audio that has stopped arriving, and retracts
  /// the tempo with it.
  ///
  /// Withdrawing rather than falling quiet, which is the opposite of what a Link
  /// session going silent does, and the difference is what the two silences mean. A
  /// Link session that stops publishing still had a real tempo a moment ago and
  /// extrapolating it is the best available answer. Audio that stops arriving has
  /// had its device closed or changed underneath it: the detector is being reset to
  /// zero, so there is no estimate left to stand behind the number on stage, and
  /// leaving it there would put a tempo on screen that nothing in the room supports.
  /// </summary>
  private void Forget() {
    this.detector.Reset();

    lock(this.statusGate) {
      this.latest = null;
      this.lastFrameMicros = 0;
      this.lastBeatMicros = null;
      this.bpm = 0;
      this.confidence = 0;
      this.beatsDetected = 0;
      this.locked = false;
      this.published = false;
    }

    // A no-op unless this source has published, and harmless unless it is the
    // active one: the manager drops the cached timeline either way and only clears
    // the grid when audio is the thing driving it.
    this.sink.Withdraw();
  }
}
