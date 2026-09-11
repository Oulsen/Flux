using Flux.Hue;

namespace Flux.Web.Server.Protocol;

/// <summary>
/// Composes the <c>hue</c> frame from what the bridge service knows.
///
/// Here rather than in <see cref="HueBroadcaster"/> for the reason
/// <see cref="DeviceFrames"/> exists: it has two callers that must not be able to
/// disagree. The broadcaster sends a frame whenever the picture changes;
/// <see cref="SocketHub"/> sends one to a client the moment it connects, because
/// <c>hue</c> is the only message that does not repeat and a page that waited for the
/// next change could sit on an empty pairing screen indefinitely.
/// </summary>
internal static class HueFrames {
  /// <summary>The bridge picture, encoded and ready to send.</summary>
  internal static ReadOnlyMemory<byte> Snapshot(HueConnection connection) {
    return Frames.Hue(Compose(connection));
  }

  /// <summary>
  /// The bridge picture as the protocol describes it.
  ///
  /// Internal rather than private so the tests can assert on the composed frame,
  /// which is where the countdown invariant actually lives.
  /// </summary>
  internal static HueData Compose(HueConnection connection) {
    ArgumentNullException.ThrowIfNull(connection);

    return new HueData(
      StateName(connection.State),
      connection.Bridge is { } bridge ? Describe(bridge) : null,
      [.. connection.Discovered.Select(Describe)],
      [.. connection.Areas.Select(Describe)],
      connection.SelectedAreaId,
      connection.PairingSecondsRemaining,
      connection.LatencyOffsetMs,
      Describe(connection.Light),
      connection.Message);
  }

  /// <summary>
  /// The protocol's name for a palette, and the parse of one.
  ///
  /// Paired in one method so the two directions cannot disagree: the page sends back
  /// exactly the names it was given, and a palette renamed in the enum breaks the
  /// build here rather than silently rejecting whatever the page still remembers.
  /// </summary>
  internal static bool TryParsePalette(string name, out LightPalette palette) {
    // Case-insensitively, because the wire name is lower case and the enum name is
    // not. Not Enum.Parse: that would accept "3" and the integer values are an
    // implementation detail no client should be able to reach.
    foreach(LightPalette candidate in Enum.GetValues<LightPalette>()) {
      if(string.Equals(PaletteName(candidate), name, StringComparison.OrdinalIgnoreCase)) {
        palette = candidate;
        return true;
      }
    }

    palette = LightPalette.Random;
    return false;
  }

  /// <summary>Reads a layout name off the wire, on the same terms as a palette.</summary>
  internal static bool TryParseColorLayout(string name, out LightColorLayout layout) {
    foreach(LightColorLayout candidate in Enum.GetValues<LightColorLayout>()) {
      if(string.Equals(LayoutName(candidate), name, StringComparison.OrdinalIgnoreCase)) {
        layout = candidate;
        return true;
      }
    }

    layout = LightColorLayout.Radial;
    return false;
  }

  private static HueEngineData Describe(LightSettings light) {
    return new HueEngineData(
      PaletteName(light.Palette),
      light.ColorChangeBars,
      light.PulseMs,
      light.Brightness,
      light.PulseDepth,
      light.ColorCount,
      LayoutName(light.ColorLayout));
  }

  /// <summary>A switch with no default arm, for the reason <see cref="PaletteName"/> is one.</summary>
  private static string LayoutName(LightColorLayout layout) {
    return layout switch {
      LightColorLayout.Radial => "radial",
      LightColorLayout.Alternating => "alternating",
      LightColorLayout.Random => "random",
      _ => throw new ArgumentOutOfRangeException(nameof(layout), layout, "No protocol name for this layout."),
    };
  }

  /// <summary>
  /// A switch and not <c>ToString().ToLowerInvariant()</c>, for the reason
  /// <see cref="StateName"/> is one: the wire format should not be a side effect of
  /// how an enum member happens to be spelled, and there is no default arm so a
  /// palette added later cannot ship as somebody else's colour.
  /// </summary>
  private static string PaletteName(LightPalette palette) {
    return palette switch {
      LightPalette.Random => "random",
      LightPalette.Warm => "warm",
      LightPalette.Cool => "cool",
      LightPalette.Neon => "neon",
      LightPalette.Sunset => "sunset",
      LightPalette.Ocean => "ocean",
      LightPalette.Forest => "forest",
      LightPalette.Party => "party",
      _ => throw new ArgumentOutOfRangeException(nameof(palette), palette, "No protocol name for this palette."),
    };
  }

  /// <summary>
  /// The protocol's name for where pairing has got to.
  ///
  /// A switch rather than <c>ToString().ToLowerInvariant()</c>, so renaming the enum
  /// is a compile error here instead of a silently changed wire format that only a
  /// browser ever notices. There is no default arm for the same reason: a state added
  /// later must not quietly ship as whatever the fallback happened to be.
  /// </summary>
  private static string StateName(HueConnectionState state) {
    return state switch {
      HueConnectionState.Disabled => "disabled",
      HueConnectionState.Unpaired => "unpaired",
      HueConnectionState.Discovering => "discovering",
      HueConnectionState.Pairing => "pairing",
      HueConnectionState.Connecting => "connecting",
      HueConnectionState.Ready => "ready",
      HueConnectionState.Failed => "failed",
      _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No protocol name for this bridge state."),
    };
  }

  private static HueBridgeData Describe(HueBridge bridge) {
    return new HueBridgeData(bridge.Id, bridge.Address);
  }

  private static HueAreaData Describe(HueArea area) {
    return new HueAreaData(area.Id, area.Name, area.ChannelCount, area.Streaming);
  }
}
