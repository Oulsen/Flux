namespace Flux.Web.Server.Protocol;

/// <summary>
/// Whether a newer Flux is waiting, as the wire carries it.
/// </summary>
/// <param name="Status">"unavailable", "checking", "current", "ready" or "failed".</param>
/// <param name="Version">The downloaded version when the status is "ready", otherwise null.</param>
public sealed record UpdateData(string Status, string? Version);

/// <summary>
/// Composes the <c>update</c> frame.
///
/// Here rather than in <see cref="UpdateBroadcaster"/> for the reason
/// <see cref="StageFrames"/> exists: the broadcaster sends one when the state moves, and
/// <see cref="SocketHub"/> sends one to a client the moment it connects, because a
/// release found an hour ago does not announce itself again.
/// </summary>
internal static class UpdateFrames {
  /// <summary>The state, encoded and ready to send.</summary>
  internal static ReadOnlyMemory<byte> Snapshot(UpdateState state) {
    return Frames.Update(Compose(state));
  }

  /// <summary>
  /// The state as the protocol describes it.
  ///
  /// Spelled out rather than derived from the member names, unlike the stage's enums:
  /// there are five, a client branches on each of them, and a rename in C# should not
  /// be able to change what goes on the wire.
  /// </summary>
  internal static UpdateData Compose(UpdateState state) {
    ArgumentNullException.ThrowIfNull(state);

    string status = state.Status switch {
      UpdateStatus.Checking => "checking",
      UpdateStatus.Current => "current",
      UpdateStatus.Ready => "ready",
      UpdateStatus.Failed => "failed",
      _ => "unavailable",
    };

    return new UpdateData(status, state.Status == UpdateStatus.Ready ? state.Version : null);
  }
}
