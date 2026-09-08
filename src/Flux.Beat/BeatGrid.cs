namespace Flux.Beat;

/// <summary>
/// What put the bar where it is.
///
/// Worth carrying beside the alignment itself rather than just a boolean, because
/// the three sources of a downbeat are not equally trustworthy and the user is
/// the only one who can judge which applies: a cue press is the DJ's own
/// statement about a deck, a tap is a human ear, and a drop is a guess made by
/// the detector. Showing which one aligned the bar is what lets a wrong one be
/// recognised as wrong.
/// </summary>
public enum BarAlignmentMethod {
  /// <summary>The bar position is a correct count from an arbitrary starting point.</summary>
  None,

  /// <summary>A cue press on an audible deck.</summary>
  Cue,

  /// <summary>A tap, from the web UI.</summary>
  Tap,

  /// <summary>Audio detection deciding a drop landed here.</summary>
  Drop,
}

/// <summary>
/// Everything the rest of the app needs to know about the beat at one instant.
/// A value type, read without a lock once handed out.
/// </summary>
/// <param name="Bpm">Tempo, as published by the active source.</param>
/// <param name="Beat">The fractional beat number; the integer part is the current beat.</param>
/// <param name="PositionInBar">1-based position within the quantum, counted from the bar anchor when there is one.</param>
/// <param name="IsBarAligned">Whether <paramref name="PositionInBar"/> means anything musically.</param>
/// <param name="AlignmentMethod">What supplied the downbeat, or <see cref="BarAlignmentMethod.None"/> while unaligned.</param>
/// <param name="IsStale">True once the active source stops publishing.</param>
/// <param name="Packets">Timelines applied so far.</param>
/// <param name="MaxReanchorJumpBeats">Largest drift seen between an old timeline's prediction and its replacement, counting only same-session updates that did not change tempo.</param>
public readonly record struct BeatGridSnapshot(
  double Bpm,
  double Beat,
  int PositionInBar,
  bool IsBarAligned,
  BarAlignmentMethod AlignmentMethod,
  bool IsStale,
  long Packets,
  double MaxReanchorJumpBeats) {
  /// <summary>The beat currently sounding.</summary>
  public long BeatIndex => BeatGrid.IndexOf(Beat);
}

/// <summary>
/// Where a beat falls in the bar, and whether that is a claim worth making.
/// </summary>
/// <param name="PositionInBar">1-based position within the quantum.</param>
/// <param name="IsAligned">False while the count has an arbitrary starting point.</param>
public readonly record struct BarPosition(int PositionInBar, bool IsAligned);

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
/// Holds the current timeline and answers "where is the beat right now?".
///
/// There is one of these for the whole app, whichever source is active. That is
/// the point: sources disagree about how to find a tempo, and none of them get to
/// disagree about how a beat is numbered, positioned in the bar or reported.
///
/// The design rule it exists to enforce: <b>re-anchor, never integrate</b>. Every
/// published timeline replaces the previous one outright instead of nudging a
/// running estimate, so error cannot accumulate. Phase 0 measured the cost of
/// that replacement at 0.0000 beats of discontinuity over ~2.5 minutes -
/// rekordbox republishes an unchanged timeline, so the "correction" is normally
/// exactly nothing.
///
/// The corollary is worth stating because it is easy to get backwards: <b>an
/// update is not a beat</b>. Link's arrive at ~4 Hz carrying no new information
/// between tempo changes. Beats come from extrapolating the timeline against the
/// clock, which is what lets them be scheduled ahead of time rather than merely
/// reacted to.
/// </summary>
public sealed class BeatGrid {
  /// <summary>
  /// How far the beat numbering may move while keeping the bar anchor.
  ///
  /// The two cases this separates are far apart in practice, which is what makes
  /// a single threshold workable: a Link tempo change renumbers by thousands of
  /// beats and a session switch by hundreds, while a source re-publishing a
  /// smoothed tempo moves it by a rounding error. Below half a beat the anchor is
  /// worth keeping even though it may now be marginally off, because the
  /// alternative is throwing away an alignment the user had to perform by hand
  /// and asking them to do it again over a wobble they cannot hear.
  /// </summary>
  private const double MaxShiftKeepingBarAnchor = 0.5;

  private readonly IMonotonicClock clock;
  private readonly int quantum;
  private readonly double staleAfterMicros;
  private readonly object gate = new object();

  private BeatTimeline? timeline;
  private double lastPacketMicros;
  private long packets;
  private double maxReanchorJumpBeats;

  /// <summary>The beat index that counts as position 1, or null while unaligned.</summary>
  private long? barAnchorBeat;

  private BarAlignmentMethod alignmentMethod;

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
  /// <paramref name="newSession"/> says the beat numbering was rebased rather
  /// than re-anchored: a different Link session, a different source, an audio
  /// drop, a tap setting a new phase. Any of those renumbers the beat wholesale
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

        // The bar anchor is a beat index, so a renumbering leaves it pointing at
        // a different moment in the music. Clearing it re-arms the alignment,
        // which is why a cue press or a tap has to be repeated after a tempo
        // change: the position was real, and the number that recorded it is not
        // any more.
        if(Math.Abs(shift) >= MaxShiftKeepingBarAnchor) {
          this.barAnchorBeat = null;
          this.alignmentMethod = BarAlignmentMethod.None;
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
  /// Where a given beat falls in the bar.
  ///
  /// Here rather than on the caller because the anchor and the quantum both live
  /// here, and two places computing a bar position from a beat index is precisely
  /// how a beat indicator and a beat event end up disagreeing about the downbeat.
  /// </summary>
  public BarPosition PositionOf(long beatIndex) {
    lock(this.gate) {
      return this.PositionWhileLocked(beatIndex);
    }
  }

  /// <summary>
  /// Marks the beat nearest to now as position 1 in the bar.
  ///
  /// Nearest, not the one currently sounding, and the difference is the whole
  /// point: someone tapping along lands a few tens of milliseconds either side of
  /// the kick they mean, so flooring would put every slightly-early tap on the
  /// beat before the one the user heard. This is deliberately the one place that
  /// rounds instead of using <see cref="IndexOf"/>.
  /// </summary>
  /// <param name="method">What supplied the downbeat, for the UI to show.</param>
  /// <returns>False when no timeline is known yet, so there is no beat to anchor to.</returns>
  public bool TryAlignBar(BarAlignmentMethod method) {
    lock(this.gate) {
      if(this.timeline is not BeatTimeline known) {
        return false;
      }

      this.barAnchorBeat = (long)Math.Round(known.BeatAt(this.clock.NowMicros));
      this.alignmentMethod = method;

      return true;
    }
  }

  /// <summary>
  /// Forgets everything, so the grid reports nothing until a source publishes.
  ///
  /// Used when the active source changes to one that has never published. The
  /// alternative - leaving the outgoing source's timeline in place - would show
  /// its tempo on screen labelled as the new source, and a confidently wrong BPM
  /// is the one reading a beat display must never invent.
  ///
  /// The health counters go with it. They describe one continuous run of
  /// extrapolation, and carrying a re-anchor maximum across a source change would
  /// blame the arriving source for the departing one's drift.
  /// </summary>
  public void Reset() {
    lock(this.gate) {
      this.timeline = null;
      this.barAnchorBeat = null;
      this.alignmentMethod = BarAlignmentMethod.None;
      this.packets = 0;
      this.maxReanchorJumpBeats = 0;
      this.lastPacketMicros = 0;
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

      BarPosition bar = this.PositionWhileLocked(IndexOf(beat));

      snapshot = new BeatGridSnapshot(
        known.Bpm,
        beat,
        bar.PositionInBar,
        bar.IsAligned,
        this.alignmentMethod,
        nowMicros - this.lastPacketMicros > this.staleAfterMicros,
        this.packets,
        this.maxReanchorJumpBeats);

      return true;
    }
  }

  /// <summary>
  /// The bar position of a beat index. The caller must already hold the lock.
  ///
  /// Counted from the anchor once one exists, so position 1 is the beat the user
  /// actually marked. With no anchor it counts from beat 0: a correct count from
  /// an arbitrary starting point, which is the most any source can give on its
  /// own - none of them transmit a downbeat, so bar 1 comes from a cue press or a
  /// tap or not at all.
  /// </summary>
  private BarPosition PositionWhileLocked(long beatIndex) {
    return new BarPosition(
      PositionInBar(beatIndex - (this.barAnchorBeat ?? 0), this.quantum),
      this.barAnchorBeat is not null);
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
