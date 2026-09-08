namespace Flux.Beat;

/// <summary>
/// The mapping between beat numbers and local microseconds: a tempo and one
/// anchor point.
///
/// This is the currency every sync source deals in. Link reads it off the wire,
/// audio detection derives it from onsets, a tap builds it from two timestamps -
/// and <see cref="BeatGrid"/> knows nothing about which. That is the whole reason
/// the type is named for the beat rather than for Link: a source publishes a
/// timeline and stops there, so adding a source cannot change how beats are
/// counted or fired.
///
/// Both mappings are pure functions of a timestamp. Nothing here integrates or
/// accumulates, which is what lets the grid re-anchor on every update instead of
/// tracking drift.
/// </summary>
/// <param name="MicrosPerBeat">Beat duration in microseconds.</param>
/// <param name="BeatOrigin">The beat number that sits at <paramref name="TimeOriginMicros"/>.</param>
/// <param name="TimeOriginMicros">Anchor timestamp on the local monotonic clock.</param>
public readonly record struct BeatTimeline(long MicrosPerBeat, double BeatOrigin, long TimeOriginMicros) {
  /// <summary>
  /// The slowest and fastest tempi treated as real. A misparsed packet or a
  /// runaway estimate produces values orders of magnitude outside this range, so
  /// the bounds exist to stop a bad timeline driving the light show rather than
  /// to police unusual music: 20 BPM to 999 BPM already spans well beyond
  /// anything a DJ will play.
  /// </summary>
  public const long MinMicrosPerBeat = 60_060;

  /// <inheritdoc cref="MinMicrosPerBeat"/>
  public const long MaxMicrosPerBeat = 3_000_000;

  /// <summary>
  /// The widest anchor treated as real, either side of zero. The clock counts
  /// microseconds since boot, so 1e15 is about 31 years of uptime - beyond any
  /// machine, and small enough that a garbage anchor cannot survive.
  ///
  /// The tempo bound alone does not cover this. Tempo is one of three fields, and
  /// a plausible tempo beside a wild anchor still puts the beat number somewhere
  /// around 1e13, which rotates the bar readout at random and looks like a source
  /// fault rather than a bad timeline.
  /// </summary>
  public const long MaxAnchorMagnitudeMicros = 1_000_000_000_000_000;

  /// <summary>
  /// The widest beat origin treated as real. Generous on purpose: it only has to
  /// exclude the magnitudes a misparse produces, not police a long session.
  /// </summary>
  public const double MaxBeatOriginMagnitude = 1e12;

  public double Bpm => 60_000_000.0 / MicrosPerBeat;

  /// <summary>
  /// True when all three fields are inside their plausible bands: the tempo
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

  /// <summary>
  /// A timeline at <paramref name="bpm"/> whose beat numbering is anchored so
  /// that <paramref name="beat"/> falls at <paramref name="atMicros"/>.
  ///
  /// Built for the sources that have a tempo and an instant rather than a wire
  /// format: a tap knows when the user tapped, audio detection knows when an
  /// onset landed. Both need the beat to fall <em>there</em>, which is the part
  /// a BPM alone cannot say.
  /// </summary>
  public static BeatTimeline FromBpm(double bpm, double beat, double atMicros) {
    return new BeatTimeline(
      (long)Math.Round(60_000_000.0 / bpm),
      beat,
      (long)Math.Round(atMicros));
  }
}
