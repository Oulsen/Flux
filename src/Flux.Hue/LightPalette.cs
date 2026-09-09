namespace Flux.Hue;

/// <summary>
/// Which colours the light engine is allowed to choose from.
///
/// The word "hue" does two jobs in this project and this is where they meet: a Hue
/// bridge is a product, a hue is a position on the colour wheel. Everything in this
/// file means the second one.
/// </summary>
public enum LightPalette {
  /// <summary>Any hue on the wheel, which is the reference scene's own default.</summary>
  Random,

  /// <summary>Reds, oranges, yellows.</summary>
  Warm,

  /// <summary>Blues, cyans, purples.</summary>
  Cool,

  /// <summary>Bright magentas, cyans, greens.</summary>
  Neon,

  /// <summary>Oranges, pinks, purples.</summary>
  Sunset,

  /// <summary>Teals and blues.</summary>
  Ocean,

  /// <summary>Greens.</summary>
  Forest,

  /// <summary>A full rainbow spread.</summary>
  Party,
}

/// <summary>
/// The hues each palette draws from, in degrees.
///
/// These numbers are data and not design, so they are copied rather than derived.
/// They were tuned by eye against real music in the scene this phase reimplements,
/// and the rule for that material is that structure gets rewritten while tuned values
/// get carried across unchanged: a palette re-invented from colour theory would look
/// entirely plausible and would quietly regress something somebody already got right.
///
/// Eight hues each, which is not a coincidence either - the scene drove up to eight
/// independent colour sections and needed one candidate per section. Flux sends a
/// single colour across the whole area in this phase, so the list is a bag to draw
/// from rather than a layout, and per-light spatial colour is Phase 4's problem.
/// </summary>
internal static class LightPalettes {
  private static readonly int[] WarmHues = [0, 15, 30, 45, 350, 335, 320, 20];
  private static readonly int[] CoolHues = [180, 200, 220, 240, 260, 190, 210, 230];
  private static readonly int[] NeonHues = [300, 180, 120, 60, 330, 150, 90, 270];
  private static readonly int[] SunsetHues = [0, 20, 40, 280, 300, 320, 340, 10];
  private static readonly int[] OceanHues = [180, 190, 200, 210, 220, 170, 195, 205];
  private static readonly int[] ForestHues = [80, 100, 120, 140, 60, 90, 110, 130];
  private static readonly int[] PartyHues = [0, 60, 120, 180, 240, 300, 30, 270];

  /// <summary>
  /// The hues a palette offers, or null for <see cref="LightPalette.Random"/>.
  ///
  /// Null rather than a list of all 360 degrees, because the two are different
  /// instructions: a curated palette is a choice between eight named colours, and
  /// Random is the absence of any such constraint. Flattening one into the other
  /// would make "no palette" indistinguishable from a palette that happens to be
  /// large.
  /// </summary>
  internal static IReadOnlyList<int>? HuesOf(LightPalette palette) {
    return palette switch {
      LightPalette.Random => null,
      LightPalette.Warm => WarmHues,
      LightPalette.Cool => CoolHues,
      LightPalette.Neon => NeonHues,
      LightPalette.Sunset => SunsetHues,
      LightPalette.Ocean => OceanHues,
      LightPalette.Forest => ForestHues,
      LightPalette.Party => PartyHues,

      // No default arm, deliberately: a palette added later must fail to compile here
      // rather than silently ship as whatever the fallback happened to be.
      _ => throw new ArgumentOutOfRangeException(nameof(palette), palette, "No hues for this palette."),
    };
  }
}
