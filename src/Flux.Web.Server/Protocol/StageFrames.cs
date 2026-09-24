namespace Flux.Web.Server.Protocol;

/// <summary>
/// How the stage draws, as the wire carries it.
///
/// Its own message rather than fields on <see cref="StateData"/>, on the argument that
/// gave <c>hue</c> its own: state goes out twenty times a second, and a look that
/// changes when somebody taps a chip would be twelve hundred identical copies a minute
/// of something nobody moved.
///
/// The three names travel as strings rather than as numbers for the reason
/// <c>hue.engine.palette</c> does: the integer values of an enum are an implementation
/// detail, and a client that had to know them would break the day one was inserted in
/// the middle.
/// </summary>
/// <param name="Layout">"upward" or "mirrored".</param>
/// <param name="Shape">"bars", "thinBars", "chonkerBars", "pixel", "smooth" or "smoothSimple".</param>
/// <param name="Color">"randomBeat", "hueCycle", "hueWave", "hueWaveVert" or "static".</param>
/// <param name="Bands">
/// How many display columns the spectrum feed carries, 16 to 128. Read the length of
/// <c>spectrum.bins</c> rather than this to size a drawing: the two agree, but the
/// array is the thing being drawn and a frame in flight when this changed carries the
/// old count.
/// </param>
/// <param name="TrailMs">How long the picture's afterimage lasts, 0 to 1000 milliseconds, where 0 is off. Screen only.</param>
public sealed record StageData(string Layout, string Shape, string Color, int Bands, int TrailMs);

/// <summary>
/// Composes the <c>stage</c> frame, and reads a request to change it.
///
/// Here rather than in <see cref="StageBroadcaster"/> for the reason
/// <see cref="HueFrames"/> exists: two callers that must not be able to disagree. The
/// broadcaster sends one when the look changes, and <see cref="SocketHub"/> sends one to
/// a client the moment it connects, because this message does not repeat and a page that
/// waited for the next change would draw the defaults over somebody's chosen look for
/// as long as nobody touched it.
/// </summary>
internal static class StageFrames {
  /// <summary>The look, encoded and ready to send.</summary>
  internal static ReadOnlyMemory<byte> Snapshot(StageSettings settings) {
    return Frames.Stage(Compose(settings));
  }

  /// <summary>
  /// The look as the protocol describes it.
  ///
  /// Internal rather than private so a test can assert on the composed frame, which is
  /// where the wire names actually live.
  /// </summary>
  internal static StageData Compose(StageSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);

    return new StageData(
      Name(settings.Layout),
      Name(settings.Shape),
      Name(settings.Color),
      settings.Bands,
      settings.TrailMs);
  }

  /// <summary>
  /// Turns a wire name back into an enum member.
  ///
  /// Not <c>Enum.Parse</c>, on the same argument <see cref="HueFrames.TryParsePalette"/>
  /// makes: it would accept "3", and the integer values are an implementation detail no
  /// client should be able to reach.
  /// </summary>
  internal static bool TryParse<TEnum>(string name, out TEnum parsed) where TEnum : struct, Enum {
    foreach(TEnum candidate in Enum.GetValues<TEnum>()) {
      if(string.Equals(Name(candidate), name, StringComparison.OrdinalIgnoreCase)) {
        parsed = candidate;
        return true;
      }
    }

    parsed = default;
    return false;
  }

  /// <summary>
  /// An enum member's wire name: its own name with a lower-case first letter.
  ///
  /// Derived rather than tabulated, which is the opposite of what <see cref="HueFrames"/>
  /// does for palettes, and the difference is worth stating. A palette's wire name is a
  /// word a user reads and could reasonably diverge from the code's name; these are
  /// mechanical camel-case spellings of the members themselves, and a table of eleven of
  /// them would be eleven chances to typo one into a name no client sends.
  /// </summary>
  private static string Name<TEnum>(TEnum value) where TEnum : struct, Enum {
    string name = value.ToString();

    return char.ToLowerInvariant(name[0]) + name[1..];
  }
}
