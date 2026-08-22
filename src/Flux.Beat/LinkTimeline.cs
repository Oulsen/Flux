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

  public double Bpm => 60_000_000.0 / MicrosPerBeat;

  /// <summary>True when the tempo is inside <see cref="MinMicrosPerBeat"/>..<see cref="MaxMicrosPerBeat"/>.</summary>
  public bool IsPlausible => MicrosPerBeat is >= MinMicrosPerBeat and <= MaxMicrosPerBeat;

  /// <summary>The fractional beat number at a local timestamp.</summary>
  public double BeatAt(double micros) {
    return BeatOrigin + ((micros - TimeOriginMicros) / MicrosPerBeat);
  }

  /// <summary>The local timestamp at which a beat number falls.</summary>
  public double MicrosOfBeat(double beat) {
    return TimeOriginMicros + ((beat - BeatOrigin) * MicrosPerBeat);
  }
}
