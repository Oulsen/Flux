namespace Flux.Hue;

/// <summary>
/// How the light engine turns beats into colour, as the user has it set.
///
/// Three numbers rather than one, because they answer three different questions a
/// person asks while standing in the room: which colours, how often they change, and
/// how hard each beat hits. All three are owned by the page and survive a restart, and
/// none of them has a configuration key - unlike
/// <see cref="HueOptions.LatencyOffsetMs"/>, which does, because light latency is a
/// property of an installation that somebody may want pinned for a rig. A palette is a
/// taste that changes between sets.
/// </summary>
/// <param name="Palette">Which colours the engine draws from.</param>
/// <param name="ColorChangeBars">
/// Bars between colour changes, or zero for a new colour on every beat.
///
/// Zero is the reference scene's "Off", and the name is worth explaining because it
/// reads backwards: switching the interval off does not stop the colours changing, it
/// removes the interval, so they change as often as they possibly can. The wire keeps
/// the number rather than the word.
///
/// Counted in bars and not in beats, because a colour that changes on a downbeat is
/// heard as part of the music and one that changes three beats into a bar is heard as
/// a fault. Four beats to the bar is assumed throughout: it is what the beat grid
/// counts in and what <see cref="Flux.Beat.BeatEvent.PositionInBar"/> reports against.
/// </param>
/// <param name="PulseMs">
/// How long a beat's brightness pulse takes to fade out, or zero for no pulse at all.
///
/// Zero means a steady colour rather than an instantaneous flash, which is the one
/// reading of "instant" that is useful: a one-frame pulse at 50 Hz is a 20 ms strobe
/// nobody asked for, while a steady light is a real thing to want - it is what the
/// colour-change interval sounds like on its own.
///
/// Worth knowing at the short end: the bridge forwards to a bulb at roughly 25 updates
/// a second, so a pulse under about 80 ms is one or two Zigbee frames and the bulb may
/// never render it. That is a property of the hardware rather than a bound to enforce -
/// the value is still honoured, it simply stops being visible.
/// </param>
/// <param name="Brightness">
/// How bright the lights get at the top of a beat, as a percentage.
///
/// The ceiling for everything the engine sends, so this is "how bright is the room"
/// and the one control somebody reaches for first. Zero is a blackout, which is a real
/// thing to ask for and is deliberately allowed - the page says "off" at that end
/// rather than leaving somebody wondering whether the stream died.
/// </param>
/// <param name="PulseDepth">
/// How far the light drops between beats, as a percentage of <paramref name="Brightness"/>.
///
/// Depth rather than a floor of its own, because the two numbers people actually want
/// are "how bright" and "how much does the beat stand out", and a floor expressed
/// absolutely lets somebody set one above the ceiling. It also makes a short pulse
/// usable: at a low depth a quick flash sits on top of a bright room instead of leaving
/// a dark one, which is what a fixed floor could not express.
/// </param>
public sealed record LightSettings(
  LightPalette Palette,
  int ColorChangeBars,
  int PulseMs,
  int Brightness = 100,
  int PulseDepth = 85) {
  /// <summary>Beats to the bar, which is what the beat grid counts in.</summary>
  public const int BeatsPerBar = 4;

  /// <summary>
  /// The longest interval between colour changes that is still a colour change.
  ///
  /// Sixteen bars is about half a minute at 120 BPM, and the reference scene stopped
  /// there for the same reason: past it, a light that has held one colour since before
  /// the current track started is indistinguishable from one that is stuck.
  /// </summary>
  public const int MaxColorChangeBars = 16;

  /// <summary>
  /// The slowest pulse, in milliseconds.
  ///
  /// Two seconds is four beats at 120 BPM, so a pulse longer than this has not
  /// finished fading when the beat after next arrives and the envelope stops being a
  /// pulse at all. Past that the setting would only be able to make the lights dimmer,
  /// which is not what the person moving the slider is asking for.
  /// </summary>
  public const int MaxPulseMs = 2000;

  /// <summary>
  /// The top of the range for both percentages, which share one bound because they are
  /// both fractions of the same thing.
  /// </summary>
  public const int MaxPercent = 100;

  /// <summary>
  /// The scene's own defaults, carried across rather than chosen.
  ///
  /// <c>Random</c> and four bars are what the reference scene shipped. The pulse has
  /// no direct equivalent there - the scene's lights rendered a spectrum and its one
  /// decay constant governed an on-screen indicator - so 240 ms is that constant
  /// converted honestly: 0.85 per frame at 60 frames a second reaches a tenth of its
  /// height in 14.2 frames, which is 236 ms. It is the starting point for the tuning
  /// that happens by eye with music playing, not a measured value.
  ///
  /// Full brightness at a depth of 85 is not a new guess either: it is arithmetically
  /// the behaviour the engine had before either setting existed, when the floor was a
  /// fixed 0.15 and the peak a fixed 1.0. Adding the controls therefore changed nothing
  /// about how it looks until somebody moves one, which is the only honest way to
  /// introduce a knob to something already being tuned by eye.
  /// </summary>
  public static LightSettings Default { get; } = new LightSettings(LightPalette.Random, ColorChangeBars: 4, PulseMs: 240);

  /// <summary>Whether every field is inside its documented range.</summary>
  public bool IsValid() {
    return Enum.IsDefined(Palette)
      && ColorChangeBars >= 0
      && ColorChangeBars <= MaxColorChangeBars
      && PulseMs >= 0
      && PulseMs <= MaxPulseMs
      && Brightness >= 0
      && Brightness <= MaxPercent
      && PulseDepth >= 0
      && PulseDepth <= MaxPercent;
  }
}
