namespace Flux.Hue;

/// <summary>
/// One colour, as everything above the HueApi boundary talks about colour.
///
/// Linear 0 to 1 per channel, which is the same scale the spectrum bands and the
/// confidence figures already use, so nothing in Flux has to remember which numbers
/// are bytes. Gamma and colour spaces are Phase 4's problem deliberately: this phase
/// sends solid colours, and a conversion invented here would be the wrong one to
/// inherit.
/// </summary>
/// <param name="R">Red, 0 to 1.</param>
/// <param name="G">Green, 0 to 1.</param>
/// <param name="B">Blue, 0 to 1.</param>
public readonly record struct HueColor(double R, double G, double B) {
  /// <summary>Lights off, which is a colour rather than the absence of one while a stream is open.</summary>
  public static HueColor Black { get; } = new HueColor(0, 0, 0);

  /// <summary>
  /// The same colour with every channel inside 0 to 1.
  ///
  /// Applied at the boundary rather than trusted from the caller: the light engine
  /// arriving in Phase 4 will scale colours by a beat envelope, and an overshoot
  /// reaching the bridge as a wrapped or negative value is a class of bug that shows
  /// up as one wrong light for one frame and never reproduces.
  /// </summary>
  public HueColor Clamped() {
    return new HueColor(Clamp(R), Clamp(G), Clamp(B));
  }

  private static double Clamp(double value) {
    return double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);
  }
}
