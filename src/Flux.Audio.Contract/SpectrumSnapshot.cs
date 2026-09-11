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
/// The three band energies of the newest frame, each 0 to 1.
///
/// Deliberately smaller than <see cref="SpectrumSnapshot"/> and deliberately without a
/// verdict, because it exists for a reader that wants the sound and not the decision -
/// see <see cref="IAudioSpectrum.Levels"/>.
/// </summary>
/// <param name="Kick">Bass band energy. The band the detector triggers on.</param>
/// <param name="Mids">Mid band energy.</param>
/// <param name="Highs">High band energy.</param>
public readonly record struct SpectrumLevels(double Kick, double Mids, double Highs) {
  /// <summary>
  /// The band at a point on the frequency axis, 0 for the kick and 1 for the top.
  ///
  /// Three bands rather than the display columns, and that is a decision rather than a
  /// convenience: the column count is a slider somebody drags to change how the picture
  /// looks, and sampling it here would mean a display setting silently retuning the
  /// room. These three have derivations in <c>docs/audio-constants.md</c> and move only
  /// when the audio engine does.
  ///
  /// Linear between the three, which is coarse and is the right kind of coarse: what it
  /// has to express is that lights near the centre follow the low end and the outer ones
  /// follow the top, and no amount of resolution between three real measurements makes
  /// that statement more true.
  /// </summary>
  public double At(double position) {
    double clamped = Math.Clamp(position, 0, 1);

    return clamped <= 0.5
      ? Lerp(this.Kick, this.Mids, clamped * 2)
      : Lerp(this.Mids, this.Highs, (clamped - 0.5) * 2);
  }

  /// <summary>The loudest of the three, which is what the others are read against.</summary>
  public double Peak => Math.Max(this.Kick, Math.Max(this.Mids, this.Highs));

  private static double Lerp(double from, double to, double amount) {
    return from + ((to - from) * amount);
  }
}

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

  /// <summary>
  /// The newest frame's band energies, or null when nothing is being heard.
  ///
  /// <b>This exists because <see cref="Spectrum"/> may only ever have one caller.</b>
  /// That method consumes the latched verdict, so a second reader takes roughly half
  /// the beats away from the first - and since the second reader here is the light
  /// engine at fifty frames a second, the symptom would be beat markers vanishing from
  /// the visualizer only while the lights are streaming. That is a bug nobody would
  /// find from the outside, so the two readers are given two methods instead of a
  /// convention about who is allowed to call which.
  ///
  /// Same staleness rule as <see cref="Spectrum"/>: null rather than the last frame
  /// before the audio stopped, so a room with no music falls back to lights that behave
  /// identically rather than to a picture frozen mid-track.
  /// </summary>
  SpectrumLevels? Levels();
}
