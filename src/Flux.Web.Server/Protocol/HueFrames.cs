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
      connection.Message);
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
