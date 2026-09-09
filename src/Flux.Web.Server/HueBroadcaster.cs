using Flux.Hue;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;

namespace Flux.Web.Server;

/// <summary>
/// Sends the bridge picture to every browser, whenever it changes.
///
/// The only broadcaster here with no timer, and the difference is the point. A state
/// frame is worth re-sending at 20 Hz because the beat has moved; a list of
/// entertainment areas has not moved since somebody last opened the Hue app, and
/// sending it twelve hundred times a minute to say so would be the one genuinely
/// wasteful thing on this socket.
///
/// The cost of pushing on change is that a dropped frame is not replaced by the next
/// one. That is accepted rather than overlooked - see "Backpressure" in the protocol
/// document - and it is why <see cref="SocketHub"/> sends a frame of its own to every
/// client the moment it connects.
/// </summary>
public sealed class HueBroadcaster: IHostedService {
  private readonly SocketHub hub;

  /// <summary>
  /// Null in a build with no Hue support. Nothing here starts in that case, and the
  /// page is told so by the <c>disabled</c> state rather than by silence.
  /// </summary>
  private readonly HueBridgeService? bridge;

  public HueBroadcaster(SocketHub hub, HueBridgeService? bridge) {
    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.bridge = bridge;
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    if(this.bridge is not null) {
      this.bridge.Changed += this.Broadcast;
    }

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    if(this.bridge is not null) {
      this.bridge.Changed -= this.Broadcast;
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Encodes one picture and hands it to every client.
  ///
  /// Internal so a test can drive it without a bridge service raising the event.
  /// </summary>
  /// <returns>False when nobody is watching and the frame was not built.</returns>
  internal bool TryBroadcast(HueConnection connection) {
    if(!this.hub.HasClients) {
      return false;
    }

    this.hub.Broadcast(HueFrames.Snapshot(connection));

    return true;
  }

  /// <summary>
  /// Runs on whichever thread changed the connection, which during pairing is the
  /// polling loop once a second.
  ///
  /// Doing the work inline rather than handing it to a pump is deliberate: encoding a
  /// few hundred bytes and writing them into bounded queues cannot block, and a change
  /// here is a handful of events an hour rather than a stream. The rule the hub exists
  /// to enforce still holds - nothing waits for a client to receive anything.
  /// </summary>
  private void Broadcast(HueConnection connection) {
    this.TryBroadcast(connection);
  }
}
