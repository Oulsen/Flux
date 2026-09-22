namespace Flux.Beat;

/// <summary>
/// Settings for the beat grid itself, independent of where the beat comes from.
///
/// Split out of the Link settings because neither value is about Link. Both
/// describe how Flux counts and reports a beat, so an audio source or a tap has
/// exactly the same need of them, and leaving them under <c>Link:</c> would have
/// meant a user disabling Link losing the meter along with it.
/// </summary>
public sealed class BeatOptions {
  public const string SectionName = "Beat";

  /// <summary>
  /// Beats per bar: what turns a beat number into a 1-2-3-4 readout, and how long
  /// a bar is when the light engine changes colour every few bars.
  ///
  /// This is Flux's own interpretation in the strict sense: Link transmits no
  /// meter, and audio detection cannot infer one from onsets, so no source either
  /// confirms or contradicts it. The bar <em>position</em> can be anchored by a
  /// cue press or a tap; the bar <em>length</em> is only ever this setting.
  /// </summary>
  public int Quantum { get; set; } = 4;

  /// <summary>
  /// How long without a new timeline before the grid reports itself stale.
  ///
  /// Sized for the fastest-publishing source, which is Link at roughly every
  /// 250 ms: ten missed updates is long enough not to flicker on a scheduling
  /// hiccup, and short enough that switching a source off shows up while the user
  /// is still looking at the screen. Going stale never stops the grid
  /// extrapolating, it only stops it claiming the tempo is confirmed.
  /// </summary>
  public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(2.5);

  /// <summary>
  /// Which sync source is active when Flux starts.
  ///
  /// Named rather than left to whichever source happens to be constructed first,
  /// because that order is an accident of dependency-injection registration and
  /// would silently change the moment a source was added. An unrecognised name
  /// falls back to the first source registered instead of failing to start: a
  /// typo here should cost the user their preferred default, not their light show.
  /// </summary>
  public string DefaultSource { get; set; } = "link";
}
