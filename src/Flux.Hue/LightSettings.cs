namespace Flux.Hue;

/// <summary>
/// How the light engine turns beats into colour, as the user has it set.
///
/// Several numbers rather than one, because they answer different questions a person
/// asks while standing in the room: which colours, how many at once, how often they
/// change, and how hard each beat hits. All of them are owned by the page and survive a
/// restart, and none has a configuration key - unlike
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
/// a fault. How long a bar is comes from <see cref="Flux.Beat.BeatOptions.Quantum"/>,
/// which is what the beat grid counts in and what
/// <see cref="Flux.Beat.BeatEvent.PositionInBar"/> reports against - four by default,
/// and nothing here restates that number.
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
/// <param name="ColorCount">
/// How many colours the room is in at once, from one to
/// <see cref="MaxColorCount"/>.
///
/// One is the room in a single colour, which is what the engine did before this setting
/// existed and is still the default. Above one the lights are split into that many
/// groups by where they listen on the frequency axis - with two, the bass end of the
/// room takes one hue and the top end another; with three, inner, middle and outer.
///
/// Grouped by frequency rather than by channel order, so that the colour split says the
/// same thing as the brightness split already does and the room reads as one idea. The
/// alternative - alternating hues around the room - is bolder and was considered and
/// rejected on 2026-09-11: it discards the spatial mapping entirely, so a light's colour
/// would stop having anything to do with where it is or what it is listening to.
///
/// The hues are drawn from the same palette and held apart from each other by the same
/// separation a single colour is held from the one it replaces, so two colours at once
/// can never be two shades of the one colour. A palette too tight to supply that many
/// distinct hues gets the farthest apart it can manage rather than a failure, on the
/// same argument <c>PickHue</c> already makes for one.
/// </param>
/// <param name="ColorLayout">
/// How the room is divided between those colours, and a no-op at one.
///
/// Radial by default and by argument; the alternatives are there because a look is
/// settled by standing in front of it rather than by reasoning about it. See
/// <see cref="LightColorLayout"/> for what each does.
/// </param>
/// <param name="Strobe">
/// Whether the room strobes white on the beat grid until somebody switches it off.
///
/// The scene's toggle, and off by default for the reason the scene had it off: a
/// strobe is a photosensitivity hazard, and nobody should meet one because a room
/// started with a setting they never chose. It is one of two ways to strobe - the
/// other is holding the strobe button, which is never saved and ends when the finger
/// lifts, see <see cref="StrobeHolds"/>. Both run the same strobe at the same
/// <paramref name="StrobeCycle"/>.
/// </param>
/// <param name="StrobeCycle">
/// How long one strobe cycle is, as a percentage of a beat.
///
/// The scene's value and range - 10 to 100, default 50 - under a new name. The scene
/// called it speed, which read backwards: a lower number meant faster flashing, because
/// the number is how long a cycle lasts and not how many of them there are. At 50 the
/// strobe fires twice a beat, at 100 once, on the beat.
///
/// Worth knowing at the fast end, on the same hardware limit <paramref name="PulseMs"/>
/// documents: each flash is a quarter of a cycle, so at 128 BPM and the default a flash
/// lasts about 59 ms, which the bridge forwards to a bulb as one or two Zigbee frames.
/// On Hue that reads as energetic pulsing rather than as a club strobe. The value is
/// honoured regardless; the bulbs simply stop resolving it.
/// </param>
/// <param name="FollowPhrases">
/// Whether a phrase starting changes the colour and restarts the
/// <paramref name="ColorChangeBars"/> count, when the track playing has phrases.
///
/// Flux's own, since the scene had no phrases to follow, and on by default because the
/// count it corrects is usually wrong without it: the count runs from the bar anchor,
/// which rekordbox sets at the downbeat nearest the playhead whenever a track loads, the
/// master changes or the playhead jumps, so it rarely starts where a phrase does, and a
/// chorus gets its colour a bar or three late. Inside a phrase the count runs as before.
/// Off, or with nothing offering phrases - Link or audio alone, a track whose phrases do
/// not fit its grid - the colours change exactly as they did before phrases existed.
/// </param>
public sealed record LightSettings(
  LightPalette Palette,
  int ColorChangeBars,
  int PulseMs,
  int Brightness = 100,
  int PulseDepth = 85,
  int ColorCount = 1,
  LightColorLayout ColorLayout = LightColorLayout.Radial,
  bool Strobe = false,
  int StrobeCycle = 50,
  bool FollowPhrases = true) {
  /// <summary>
  /// The most colours the room can be in at once.
  ///
  /// Three, and the bound is about the room rather than about the arithmetic. Every hue
  /// added takes lights away from the others, and past three a nine-light area is
  /// splitting into groups of two or three where a group stops reading as a part of the
  /// room and starts reading as individual bulbs disagreeing. It is also the point where
  /// the palette separation stops being satisfiable for anything but the widest
  /// palettes.
  /// </summary>
  public const int MaxColorCount = 3;

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
  /// The shortest strobe cycle, as a percentage of a beat. The scene's bound.
  ///
  /// A tenth of a beat is 47 ms at 128 BPM, so its flash is about 12 ms - well under a
  /// single bridge frame. The scene allowed it and so does this; it is the end of the
  /// range where the screen still flashes and the bulbs mostly do not.
  /// </summary>
  public const int MinStrobeCycle = 10;

  /// <summary>The longest strobe cycle: one flash per beat. The scene's bound.</summary>
  public const int MaxStrobeCycle = 100;

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
      && PulseDepth <= MaxPercent
      && ColorCount >= 1
      && ColorCount <= MaxColorCount
      && Enum.IsDefined(ColorLayout)
      && StrobeCycle >= MinStrobeCycle
      && StrobeCycle <= MaxStrobeCycle;
  }
}
