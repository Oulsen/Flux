namespace Flux.Hue;

/// <summary>
/// How the room is divided between the hues, when there is more than one.
///
/// Only <see cref="LightColorLayout.Radial"/> was built, on the argument that the colour
/// split should say the same thing as the brightness split already does. The other two
/// exist because that argument is worth testing against the room rather than only against
/// itself: this is a look, and a look is settled by standing in front of it.
///
/// Every one of these is a no-op at one colour. The setting divides a room between hues,
/// and a room in one colour has nothing to divide.
/// </summary>
public enum LightColorLayout {
  /// <summary>
  /// Contiguous groups along the frequency axis: the lights nearest the centre of the
  /// room take the first hue and the outer ones the next.
  ///
  /// The default, and the one with an argument behind it. A light's place on that axis
  /// already decides which band it follows, so using it for colour as well means a
  /// light's colour says the same thing about where it is as its brightness does, and
  /// the room reads as one idea rather than two overlaid.
  /// </summary>
  Radial,

  /// <summary>
  /// Every other light takes the next hue, in order of distance from the centre.
  ///
  /// Bolder, and it discards the spatial mapping: neighbouring lights disagree by
  /// design, so colour stops carrying any information about position. That is a real
  /// look rather than a broken one - it is closer to what a club rig does - and whether
  /// it beats the radial split in this room is exactly the question this setting exists
  /// to answer.
  /// </summary>
  Alternating,

  /// <summary>
  /// Each light takes one of the hues at random, re-drawn on every colour change.
  ///
  /// Stable between changes rather than per frame, which is the difference between a
  /// room with a shape that keeps changing and a room full of flickering. The assignment
  /// comes from the light's own place on the axis mixed with a number re-drawn at each
  /// change, so it needs no per-room state and a room that gains a bulb does not reshuffle
  /// the others.
  /// </summary>
  Random,
}
