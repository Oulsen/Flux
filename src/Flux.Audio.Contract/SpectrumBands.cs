namespace Flux.Audio;

/// <summary>
/// How many display columns the analyzer reduces its spectrum to, and the bounds a
/// request for a different number is judged against.
///
/// Declared here rather than in <c>AudioConstants</c> beside the rest of the audio
/// numbers, and the reason is the direction of the dependency: the web layer validates
/// an incoming request against these and cannot see that class. The derivation still
/// belongs with the audio numbers and is in <c>docs/audio-constants.md</c>, which is
/// where a reader should go for why the ceiling is where it is.
/// </summary>
public static class SpectrumBands {
  /// <summary>
  /// Fewer bars than this is a bar chart rather than a spectrum.
  ///
  /// Not a technical floor - the arithmetic works at two - but a picture this coarse
  /// stops being an instrument, and the display styles are all sized as a fraction of a
  /// bar's slot, so at very few bars they differ from each other by nothing.
  /// </summary>
  public const int Min = 16;

  /// <summary>
  /// The most the analyzer will produce.
  ///
  /// Past this the columns are asking for detail the transform does not have: at 48 kHz
  /// a 2048-sample window resolves 23.4 Hz per bin, and log spacing already makes these
  /// columns narrower than that below about 470 Hz. More of them would be the same bin
  /// drawn twice, presented as resolution.
  /// </summary>
  public const int Max = 128;

  /// <summary>
  /// Half the ceiling, which is where a picture reads best at a window's width.
  ///
  /// The ceiling is genuinely more detail on a wide monitor rather than more noise, so
  /// this is taste and not a limit - it is the default because a bar has to be wide
  /// enough to watch from across a booth.
  /// </summary>
  public const int Default = 64;

  /// <summary>Whether a requested count is one the analyzer will honour.</summary>
  public static bool IsValid(int count) {
    return count >= Min && count <= Max;
  }
}

/// <summary>
/// Where the analyzer learns how many display columns to produce.
///
/// A live reading rather than an option bound at startup, because this is a setting
/// somebody drags while music is playing. It is pulled once per frame on the analyzer
/// thread, so an implementation has to be safe to read from there and must not block:
/// anything slow here delays the next frame, and the frame rate is what the detector's
/// history windows are measured in.
///
/// The alternative was to keep producing the maximum and let the browser fold the
/// columns down, which needs no seam at all. It was rejected because folding is only
/// exact where the counts nest - 64 from 128 is the loudest of each pair, but 100 from
/// 128 leaves some bars covering two source columns and their neighbours one, which
/// reads as a faint comb across a smooth spectrum. Computing the columns here makes
/// every count exact and evenly spaced, and sends less over the wire at a low one.
///
/// Nothing the detector decides is affected. It reads band energies over the whole
/// transform and never these columns, which is why this is a resolution knob and not a
/// re-calibration.
/// </summary>
public interface ISpectrumBands {
  /// <summary>How many columns to produce, inside <see cref="SpectrumBands"/>' bounds.</summary>
  int Count { get; }
}
