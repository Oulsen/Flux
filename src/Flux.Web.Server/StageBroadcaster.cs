using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;

namespace Flux.Web.Server;

/// <summary>
/// Sends the stage's look to every browser, whenever it changes.
///
/// The second broadcaster here with no timer, and for the reason
/// <see cref="HueBroadcaster"/> has none: a look has not moved since somebody last
/// tapped a chip, and re-sending it twenty times a second to say so would be waste with
/// no reader.
///
/// Pushing on change means a dropped frame is not replaced by the next one, which is
/// accepted rather than overlooked - see "Backpressure" in the protocol document - and
/// it is why <see cref="SocketHub"/> sends a frame of its own to every client the moment
/// it connects.
/// </summary>
public sealed class StageBroadcaster: IHostedService {
  private readonly SocketHub hub;
  private readonly StageService stage;

  public StageBroadcaster(SocketHub hub, StageService stage) {
    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.stage = stage ?? throw new ArgumentNullException(nameof(stage));
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    this.stage.Changed += this.Broadcast;

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    this.stage.Changed -= this.Broadcast;

    return Task.CompletedTask;
  }

  /// <summary>
  /// Encodes one look and hands it to every client.
  ///
  /// Internal so a test can drive it without the service raising the event.
  /// </summary>
  /// <returns>False when nobody is watching and the frame was not built.</returns>
  internal bool TryBroadcast(StageSettings settings) {
    if(!this.hub.HasClients) {
      return false;
    }

    this.hub.Broadcast(StageFrames.Snapshot(settings));

    return true;
  }

  /// <summary>
  /// Runs on whichever thread changed the setting, which is a socket's receive loop.
  ///
  /// Inline rather than handed to a pump, on the argument the bridge broadcaster makes:
  /// encoding a hundred bytes into bounded queues cannot block, and the rule the hub
  /// exists to enforce still holds - nothing waits for a client to receive anything.
  /// </summary>
  private void Broadcast(StageSettings settings) {
    this.TryBroadcast(settings);
  }
}
