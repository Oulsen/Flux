namespace Flux.Beat;

/// <summary>
/// An Ableton Link timeline: the mapping between session beats and local
/// microseconds, exactly as it arrives in the <c>tmln</c> chunk of an ALIVE
/// packet.
///
/// The three fields are Link's own struct order, which Phase 0 verified by ear
/// rather than by inspection - the alternative order yields an identical tempo
/// while silently corrupting phase, so a BPM cross-check alone proves nothing.
///
/// Both mappings are pure functions of a timestamp. Nothing here integrates or
/// accumulates, which is what lets the grid re-anchor on every packet instead of
/// tracking drift.
/// </summary>
/// <param name="MicrosPerBeat">Beat duration in microseconds; the wire tempo.</param>
/// <param name="BeatOrigin">The beat number that sits at <paramref name="TimeOriginMicros"/>.</param>
/// <param name="TimeOriginMicros">Anchor timestamp on the local monotonic clock.</param>
public readonly record struct LinkTimeline(long MicrosPerBeat, double BeatOrigin, long TimeOriginMicros) {
  /// <summary>
  /// The slowest and fastest tempi treated as real. A misparse produces values
  /// orders of magnitude outside this range, so the bounds exist to stop a bad
  /// packet driving the light show rather than to police unusual music: 20 BPM
  /// to 999 BPM already spans well beyond anything a DJ will play.
  /// </summary>
  public const long MinMicrosPerBeat = 60_060;

  /// <inheritdoc cref="MinMicrosPerBeat"/>
  public const long MaxMicrosPerBeat = 3_000_000;

  /// <summary>
  /// The widest anchor treated as real, either side of zero. Link's host time is
  /// microseconds since boot, so 1e15 is about 31 years of uptime - beyond any
  /// machine, and small enough that a garbage anchor cannot survive.
  ///
  /// The tempo bound alone does not cover this. Tempo is one of three wire
  /// fields, and a plausible tempo beside a wild anchor still puts the beat
  /// number somewhere around 1e13, which rotates the bar readout at random and
  /// looks like a Link fault rather than a bad packet.
  /// </summary>
  public const long MaxAnchorMagnitudeMicros = 1_000_000_000_000_000;

  /// <summary>
  /// The widest beat origin treated as real. Generous on purpose: it only has to
  /// exclude the magnitudes a misparse produces, not police a long session.
  /// </summary>
  public const double MaxBeatOriginMagnitude = 1e12;

  public double Bpm => 60_000_000.0 / MicrosPerBeat;

  /// <summary>
  /// True when all three wire fields are inside their plausible bands: the tempo
  /// within <see cref="MinMicrosPerBeat"/>..<see cref="MaxMicrosPerBeat"/>, and
  /// both halves of the anchor within their magnitude bounds.
  /// </summary>
  public bool IsPlausible =>
    MicrosPerBeat is >= MinMicrosPerBeat and <= MaxMicrosPerBeat
    && TimeOriginMicros is >= -MaxAnchorMagnitudeMicros and <= MaxAnchorMagnitudeMicros
    && Math.Abs(BeatOrigin) <= MaxBeatOriginMagnitude;

  /// <summary>The fractional beat number at a local timestamp.</summary>
  public double BeatAt(double micros) {
    return BeatOrigin + ((micros - TimeOriginMicros) / MicrosPerBeat);
  }

  /// <summary>The local timestamp at which a beat number falls.</summary>
  public double MicrosOfBeat(double beat) {
    return TimeOriginMicros + ((beat - BeatOrigin) * MicrosPerBeat);
  }
}
