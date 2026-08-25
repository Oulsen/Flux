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
/// <param name="MaxReanchorJumpBeats">Largest drift seen between an old timeline's prediction and its replacement, counting only same-session packets that did not change tempo.</param>
public readonly record struct BeatGridSnapshot(
  double Bpm,
  double Beat,
  int PositionInBar,
  bool IsBarAligned,
  bool IsStale,
  long Packets,
  double MaxReanchorJumpBeats) {
  /// <summary>The beat currently sounding.</summary>
  public long BeatIndex => BeatGrid.IndexOf(Beat);
}

/// <summary>
/// A newly published tempo, and what adopting it did to the beat numbering.
///
/// The shift is worth carrying rather than discarding, because it is large and
/// surprising: rekordbox does not preserve beat continuity across a tempo
/// change, so the absolute Link beat number is not a stable identity. Anything
/// anchored to a beat index - bar alignment above all - has to re-arm when this
/// fires.
/// </summary>
/// <param name="Timeline">The timeline now in force.</param>
/// <param name="BeatNumberingShift">How far the beat number moved at the instant of the change; zero for the first timeline.</param>
public readonly record struct TempoChange(BeatTimeline Timeline, double BeatNumberingShift);

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

  private BeatTimeline? timeline;
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
  public event Action<TempoChange>? TempoChanged;

  /// <summary>
  /// Adopts a freshly received timeline.
  ///
  /// The discontinuity against the outgoing timeline is measured before the swap
  /// and kept as the running maximum. It is the one number that says whether
  /// extrapolation is still sound: it should stay near zero forever, and a
  /// growing value means the grid is being corrected rather than confirmed.
  ///
  /// <paramref name="newSession"/> says the timeline belongs to a different Link
  /// session than the previous one. A new session renumbers the beat wholesale
  /// exactly as a tempo change does - the two timelines are not claims about the
  /// same thing - so it is announced for the same reason and never counted as
  /// drift, even when the tempi happen to match.
  /// </summary>
  public void Apply(BeatTimeline received, bool newSession = false) {
    TempoChange? announcement = null;

    lock(this.gate) {
      double nowMicros = this.clock.NowMicros;

      if(this.timeline is BeatTimeline previous) {
        double shift = received.BeatAt(nowMicros) - previous.BeatAt(nowMicros);

        if(!newSession && previous.MicrosPerBeat == received.MicrosPerBeat) {
          // Same tempo republished by the same session, so the two timelines
          // are claims about the same thing and any disagreement is real error.
          // This is the only case the health metric may count.
          if(Math.Abs(shift) > this.maxReanchorJumpBeats) {
            this.maxReanchorJumpBeats = Math.Abs(shift);
          }
        } else {
          // A tempo change or a session switch renumbers the timeline wholesale
          // - measured against rekordbox at thousands of beats - so folding it
          // into the drift metric would pin that metric at a huge value for the
          // rest of the session and destroy the one signal it exists to give.
          announcement = new TempoChange(received, shift);
        }
      } else {
        announcement = new TempoChange(received, BeatNumberingShift: 0);
      }

      this.timeline = received;
      this.lastPacketMicros = nowMicros;
      this.packets++;
    }

    if(announcement is TempoChange notify) {
      this.TempoChanged?.Invoke(notify);
    }
  }

  /// <summary>The current timeline, or null before the first packet arrives.</summary>
  public BeatTimeline? Timeline {
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
      if(this.timeline is not BeatTimeline known) {
        snapshot = default;
        return false;
      }

      double nowMicros = this.clock.NowMicros;
      double beat = known.BeatAt(nowMicros);

      snapshot = new BeatGridSnapshot(
        known.Bpm,
        beat,
        PositionInBar(IndexOf(beat), this.quantum),

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
  /// The integer beat a fractional beat number belongs to.
  ///
  /// One definition, called from everywhere: rounding and flooring disagree by a
  /// whole beat on anything fractional, and two readings in one codebase is how
  /// a beat index and the beat it names drift apart. Phase 2 hangs bar alignment
  /// on that index.
  /// </summary>
  public static long IndexOf(double beat) {
    return (long)Math.Floor(beat);
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
