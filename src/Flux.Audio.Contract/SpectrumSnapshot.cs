namespace Flux.Audio;

/// <summary>What the beat detector made of one analyzer frame. The protocol's <c>beat</c> field.</summary>
public enum BeatStrength {
  /// <summary>Nothing.</summary>
  None,

  /// <summary>An onset that cleared the adaptive threshold.</summary>
  Beat,

  /// <summary>
  /// An onset that cleared it by a wide margin. These are the ones the beat grid is
  /// anchored to.
  /// </summary>
  Strong,
}

/// <summary>
/// One analyzed frame as the visualizer sees it: what was heard, and what was
/// decided about it.
///
/// The two halves travel together because the picture is a test instrument rather
/// than decoration. Its whole purpose is to show a beat marker against the sound
/// that produced it, and bands without <see cref="Flux"/> and
/// <see cref="Strength"/> would show what was heard while saying nothing about
/// whether the detector agreed - which is the only question the instrument exists
/// to answer.
///
/// Immutable, and handed across a thread boundary by reference: the analyzer
/// thread publishes a whole snapshot into a slot, so a reader gets either the old
/// one or the new one and never a half-written mixture.
/// </summary>
/// <param name="Bins">
/// The log-spaced display columns, low frequency first, each 0 to 255. For drawing
/// only: no decision is taken on these, which is why they can be reduced from a
/// thousand bins to sixty-four without anything being lost that matters.
/// </param>
/// <param name="Kick">Bass band energy, 0 to 1. The band the detector triggers on.</param>
/// <param name="Mids">Mid band energy, 0 to 1. Used only to tell a kick from a snare.</param>
/// <param name="Highs">High band energy, 0 to 1. Drawn, never decided on.</param>
/// <param name="Flux">
/// The rise in bass energy against the previous frame: the number the adaptive
/// threshold is compared against, and therefore the only one that explains why a
/// beat did or did not fire.
/// </param>
/// <param name="Strength">
/// What the detector made of this frame - except through
/// <see cref="IAudioSpectrum.Spectrum"/>, where it is the strongest verdict since
/// the previous read, for the reason argued there.
/// </param>
public sealed record SpectrumSnapshot(
  IReadOnlyList<int> Bins,
  double Kick,
  double Mids,
  double Highs,
  double Flux,
  BeatStrength Strength);

/// <summary>
/// Where the visualizer feed comes from.
///
/// Deliberately a pull rather than an event, because the two rates differ on
/// purpose: analysis runs at sixty frames a second and the browser is sent thirty,
/// so the reader samples the newest frame instead of being handed every one. A
/// subscriber that cannot keep up then sees fewer frames and never an older one,
/// and no browser's refresh rate can ever slow down beat detection.
/// </summary>
public interface IAudioSpectrum {
  /// <summary>
  /// The most recent analyzed frame, or null when nothing is being heard.
  ///
  /// Null rather than the last frame before the audio stopped, and this is the one
  /// contract in this file worth being strict about: a spectrum frozen at whatever
  /// was playing when the device closed is the most misleading picture the
  /// instrument could draw, because it looks exactly like music.
  ///
  /// The <see cref="SpectrumSnapshot.Strength"/> the frame carries is the strongest
  /// verdict since the previous read, and reading consumes it. Sampling makes the
  /// bands cheap to downsample, but a verdict lives on exactly one analyzer frame,
  /// and a sampled verdict is missed about half the time - so the one field whose
  /// absence would be read as the detector failing is latched instead.
  /// </summary>
  SpectrumSnapshot? Spectrum();
}
