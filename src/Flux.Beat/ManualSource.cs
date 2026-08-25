namespace Flux.Beat;

/// <summary>
/// The tempo a human supplies: tapped in time with the music, or typed as a
/// number.
///
/// It exists for the nights when nothing else works. No Link peer, no usable
/// audio, a laptop playing through the wrong output - the show still has to run,
/// and a DJ who can count can still drive it. It is also the fallback the risks
/// table names for a machine whose MIDI stack will not share the controller.
///
/// The two inputs are not equivalent, and the difference is phase. A tap says
/// <em>when</em> as well as how fast, so it can set the downbeat. A typed BPM says
/// only how fast, so it deliberately leaves the bar alone rather than guessing.
/// </summary>
public sealed class ManualSource: ISyncSource {
  /// <summary>This source's name in the protocol.</summary>
  public const string SourceName = "manual";

  /// <summary>
  /// The slowest tempo accepted. Also exactly the slowest
  /// <see cref="BeatTimeline"/> considers plausible, which is not a coincidence
  /// worth removing: a value this side of the bound is guaranteed to survive
  /// publication.
  /// </summary>
  public const double MinBpm = 20;

  /// <summary>
  /// The fastest tempo accepted, deliberately far tighter than
  /// <see cref="BeatTimeline"/>'s own 999 BPM ceiling. That ceiling exists to
  /// reject a misparsed packet, where anything might arrive. This one exists to
  /// catch a typo, where the plausible mistakes are the ones near the edge of
  /// real music.
  /// </summary>
  public const double MaxBpm = 200;

  /// <summary>
  /// How many taps the estimate is drawn from. Enough for the median to ignore a
  /// stray, few enough that a tempo change is followed within about two bars
  /// rather than averaged away.
  /// </summary>
  private const int MaxTaps = 8;

  /// <summary>
  /// A gap longer than this starts a new tap sequence rather than extending the
  /// current one.
  ///
  /// Derived from <see cref="MinBpm"/> rather than picked: an interval slower than
  /// the slowest tempo Flux will accept cannot be part of a tempo, so it can only
  /// be someone starting again. A hardcoded two seconds would have quietly made
  /// tapping anything under 30 BPM impossible.
  /// </summary>
  private const double NewSequenceAfterMicros = 60_000_000.0 / MinBpm;

  private readonly IMonotonicClock clock;
  private readonly ITimelineSink sink;
  private readonly object gate = new object();
  private readonly List<double> taps = new List<double>(MaxTaps);

  private double? bpm;

  public ManualSource(IMonotonicClock clock, SyncSourceManager sources) {
    ArgumentNullException.ThrowIfNull(sources);

    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));

    // Registered last, so the object is complete before the manager can hand the
    // reference on. Status() reads only fields, and reports Waiting from their
    // defaults, which is the truth about a source nobody has tapped yet.
    this.sink = sources.Register(this);
  }

  /// <inheritdoc/>
  public string Name => SourceName;

  /// <summary>
  /// What this source knows.
  ///
  /// It is never <see cref="SyncSourceState.Quiet"/>, and that is the substantive
  /// difference from every other source. Silence from Link means the session has
  /// gone and the tempo is now a guess that gets worse by the second. Silence here
  /// means nobody has changed their mind: the tempo is exactly as true as when it
  /// was set, so reporting it as decaying would be a lie about a human's
  /// intention.
  ///
  /// Confidence is 1 whenever a tempo exists, because this source does not
  /// estimate anything - it was told.
  /// </summary>
  public SyncSourceStatus Status() {
    lock(this.gate) {
      return this.bpm is null
        ? new SyncSourceStatus(SyncSourceState.Waiting, Confidence: 0)
        : new SyncSourceStatus(SyncSourceState.Live, Confidence: 1);
    }
  }

  /// <summary>
  /// The tempo currently held, or null before anything has been tapped or set.
  /// </summary>
  public double? Bpm {
    get {
      lock(this.gate) {
        return this.bpm;
      }
    }
  }

  /// <summary>
  /// Records a tap, timed on arrival.
  ///
  /// Timed here and never from a timestamp the client sent, because there is no
  /// clock synchronisation in this protocol: a browser's idea of "now" and the
  /// server's are unrelated numbers, so a client-supplied instant would place the
  /// beat somewhere arbitrary. The cost is the network delay between the finger
  /// and this line, which on a LAN is far inside the tolerance of a tap made by a
  /// human hand.
  ///
  /// Every tap sets the downbeat, including the first of a sequence, which
  /// carries no interval and therefore no tempo at all. That is the useful case
  /// more often than it looks: with Link already supplying the tempo, one tap on
  /// the one is the whole gesture, and waiting for a second tap before honouring
  /// it would feel broken.
  /// </summary>
  public void Tap() {
    double now = this.clock.NowMicros;
    double? tempo;

    lock(this.gate) {
      if(this.taps.Count > 0 && now - this.taps[^1] > NewSequenceAfterMicros) {
        this.taps.Clear();
      }

      this.taps.Add(now);
      if(this.taps.Count > MaxTaps) {
        this.taps.RemoveAt(0);
      }

      if(EstimateBpm(this.taps) is double estimated) {
        this.bpm = estimated;
      }

      tempo = this.bpm;
    }

    // Published before the bar is aligned, never after. Publishing rebases the
    // beat numbering, which clears any existing anchor - so aligning first would
    // set an anchor and then immediately throw it away.
    if(tempo is double known) {
      this.PublishTempo(known, now);
    }

    this.sink.AlignBar(BarAlignmentMethod.Tap);
  }

  /// <summary>
  /// Sets the tempo outright.
  ///
  /// The bar is deliberately left alone. A number says nothing about where the
  /// downbeat falls, and moving the bar to wherever the user happened to press
  /// Enter would be inventing a phase out of a keystroke.
  /// </summary>
  /// <returns>False when the tempo is outside <see cref="MinBpm"/>..<see cref="MaxBpm"/>.</returns>
  public bool TrySetBpm(double value) {
    if(!double.IsFinite(value) || value < MinBpm || value > MaxBpm) {
      return false;
    }

    double now = this.clock.NowMicros;

    lock(this.gate) {
      this.bpm = value;

      // The taps described a different tempo, so keeping them would let the next
      // single tap resurrect it by median.
      this.taps.Clear();
    }

    this.PublishTempo(value, now);

    return true;
  }

  /// <summary>
  /// The tempo implied by a run of taps, or null when there is not enough to say.
  ///
  /// The median of the intervals rather than the mean, because the error a human
  /// makes is one late tap rather than a general wobble. A mean spreads that one
  /// mistake across the whole estimate; the median ignores it outright, which is
  /// the behaviour someone correcting themselves mid-sequence expects.
  /// </summary>
  private static double? EstimateBpm(List<double> taps) {
    if(taps.Count < 2) {
      return null;
    }

    double[] intervals = new double[taps.Count - 1];
    for(int index = 1; index < taps.Count; index++) {
      intervals[index - 1] = taps[index] - taps[index - 1];
    }

    Array.Sort(intervals);

    int middle = intervals.Length / 2;
    double median = intervals.Length % 2 == 1
      ? intervals[middle]
      : (intervals[middle - 1] + intervals[middle]) / 2.0;

    if(median <= 0) {
      return null;
    }

    double estimated = 60_000_000.0 / median;

    return estimated is >= MinBpm and <= MaxBpm ? estimated : null;
  }

  /// <summary>
  /// Publishes a tempo anchored so that beat 0 falls at
  /// <paramref name="atMicros"/>.
  ///
  /// Always a rebasing, because the anchor moves every time: two taps in a row
  /// produce two timelines whose beat numbering shares nothing, and pretending
  /// otherwise would book the difference as extrapolation drift and ruin the one
  /// metric that says whether the grid is sound.
  /// </summary>
  private void PublishTempo(double tempo, double atMicros) {
    BeatTimeline timeline = BeatTimeline.FromBpm(tempo, beat: 0, atMicros);

    // Bounded inputs cannot reach here implausible, so this is a guard against a
    // future caller rather than against these two, and it fails by publishing
    // nothing instead of by putting a nonsense tempo on stage.
    if(timeline.IsPlausible) {
      this.sink.Publish(timeline, newSession: true);
    }
  }
}
