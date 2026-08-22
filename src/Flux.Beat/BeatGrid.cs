namespace Flux.Beat;

/// <summary>
/// Everything the rest of the app needs to know about the beat at one instant.
/// A value type, read without a lock once handed out.
/// </summary>
/// <param name="Bpm">Tempo, as published by Link.</param>
/// <param name="Beat">The fractional beat number; the integer part is the current beat.</param>
/// <param name="PositionInBar">1-based position within the quantum.</param>
/// <param name="IsBarAligned">Whether <paramref name="PositionInBar"/> means anything musically.</param>
/// <param name="IsStale">True once ALIVE packets stop arriving.</param>
/// <param name="Packets">ALIVE packets accepted so far.</param>
/// <param name="MaxReanchorJumpBeats">Largest disagreement seen between an old timeline's prediction and its replacement.</param>
public readonly record struct BeatGridSnapshot(
  double Bpm,
  double Beat,
  int PositionInBar,
  bool IsBarAligned,
  bool IsStale,
  long Packets,
  double MaxReanchorJumpBeats) {
  /// <summary>The beat currently sounding.</summary>
  public long BeatIndex => (long)Math.Floor(Beat);
}

/// <summary>
/// Holds the current Link timeline and answers "where is the beat right now?".
///
/// The design rule it exists to enforce: <b>re-anchor, never integrate</b>. Every
/// ALIVE packet replaces the timeline outright instead of nudging a running
/// estimate, so error cannot accumulate. Phase 0 measured the cost of that
/// replacement at 0.0000 beats of discontinuity over ~2.5 minutes - rekordbox
/// republishes an unchanged timeline, so the "correction" is normally exactly
/// nothing.
///
/// The corollary is worth stating because it is easy to get backwards: <b>a
/// packet is not a beat</b>. Packets arrive at ~4 Hz carrying no new information
/// between tempo changes. Beats come from extrapolating the timeline against the
/// clock, which is what lets them be scheduled ahead of time rather than merely
/// reacted to.
/// </summary>
public sealed class BeatGrid {
  private readonly IMonotonicClock clock;
  private readonly int quantum;
  private readonly double staleAfterMicros;
  private readonly object gate = new object();

  private LinkTimeline? timeline;
  private double lastPacketMicros;
  private long packets;
  private double maxReanchorJumpBeats;

  public BeatGrid(IMonotonicClock clock, int quantum, TimeSpan staleAfter) {
    ArgumentNullException.ThrowIfNull(clock);
    ArgumentOutOfRangeException.ThrowIfLessThan(quantum, 1);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(staleAfter, TimeSpan.Zero);

    this.clock = clock;
    this.quantum = quantum;
    this.staleAfterMicros = staleAfter.TotalMilliseconds * 1000.0;
  }

  /// <summary>Raised on the first accepted packet, and again after every tempo change.</summary>
  public event Action<LinkTimeline>? TimelineChanged;

  /// <summary>
  /// Adopts a freshly received timeline.
  ///
  /// The discontinuity against the outgoing timeline is measured before the swap
  /// and kept as the running maximum. It is the one number that says whether
  /// extrapolation is still sound: it should stay near zero forever, and a
  /// growing value means the grid is being corrected rather than confirmed.
  /// </summary>
  public void Apply(LinkTimeline received) {
    LinkTimeline? changed = null;

    lock(this.gate) {
      double nowMicros = this.clock.NowMicros;

      if(this.timeline is LinkTimeline previous) {
        double jump = Math.Abs(received.BeatAt(nowMicros) - previous.BeatAt(nowMicros));
        if(jump > this.maxReanchorJumpBeats) {
          this.maxReanchorJumpBeats = jump;
        }

        if(previous.MicrosPerBeat != received.MicrosPerBeat) {
          changed = received;
        }
      } else {
        changed = received;
      }

      this.timeline = received;
      this.lastPacketMicros = nowMicros;
      this.packets++;
    }

    if(changed is LinkTimeline notify) {
      this.TimelineChanged?.Invoke(notify);
    }
  }

  /// <summary>Forgets the timeline entirely, so the grid reports no source until packets resume.</summary>
  public void Reset() {
    lock(this.gate) {
      this.timeline = null;
      this.lastPacketMicros = 0;
      this.packets = 0;
      this.maxReanchorJumpBeats = 0;
    }
  }

  /// <summary>The current timeline, or null before the first packet arrives.</summary>
  public LinkTimeline? Timeline {
    get {
      lock(this.gate) {
        return this.timeline;
      }
    }
  }

  /// <summary>
  /// Where the beat is right now.
  ///
  /// Returns false only before the first packet. Once a timeline is known it
  /// keeps extrapolating even when packets stop, reporting
  /// <see cref="BeatGridSnapshot.IsStale"/> instead of going silent - a known
  /// tempo that has gone quiet is still better than darkness, and the UI can say
  /// so.
  /// </summary>
  public bool TryGetSnapshot(out BeatGridSnapshot snapshot) {
    lock(this.gate) {
      if(this.timeline is not LinkTimeline known) {
        snapshot = default;
        return false;
      }

      double nowMicros = this.clock.NowMicros;
      double beat = known.BeatAt(nowMicros);

      snapshot = new BeatGridSnapshot(
        known.Bpm,
        beat,
        PositionInBar((long)Math.Floor(beat), this.quantum),

        // Link carries no meter and no quantum, so bar 1 is not recoverable from
        // the wire at any amount of parsing effort. Phase 2 supplies it from a
        // cue press or a tap; until then this stays honestly false and
        // bar-scoped behaviour degrades to beat-scoped.
        IsBarAligned: false,
        nowMicros - this.lastPacketMicros > this.staleAfterMicros,
        this.packets,
        this.maxReanchorJumpBeats);

      return true;
    }
  }

  /// <summary>
  /// Turns a beat number into a 1-based position in the bar.
  ///
  /// Beat numbers go negative when the session anchor sits ahead of now, and C#
  /// modulo follows the sign of the dividend, so the naive expression yields
  /// positions like -2. Normalising is not pedantry: it is the difference
  /// between a beat indicator and an array index that throws.
  /// </summary>
  public static int PositionInBar(long beat, int quantum) {
    long position = beat % quantum;
    if(position < 0) {
      position += quantum;
    }

    return (int)position + 1;
  }
}
