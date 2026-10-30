using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;

namespace Flux.Web.Server;

/// <summary>
/// Tells every browser when a newer Flux has been found, or a check has ended.
///
/// Pushed on change like <see cref="StageBroadcaster"/>, since the state moves a few
/// times a day at most. Starts nothing in a host with no updater: the page then never
/// hears of updates and shows no control for them.
/// </summary>
public sealed class UpdateBroadcaster: IHostedService {
  private readonly SocketHub hub;
  private readonly IAppUpdates? updates;

  public UpdateBroadcaster(SocketHub hub, IAppUpdates? updates) {
    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.updates = updates;
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    if(this.updates is not null) {
      this.updates.Changed += this.Broadcast;
    }

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    if(this.updates is not null) {
      this.updates.Changed -= this.Broadcast;
    }

    return Task.CompletedTask;
  }

  /// <summary>
  /// Encodes one state and hands it to every client.
  ///
  /// Internal so a test can drive it without an updater raising the event.
  /// </summary>
  /// <returns>False when nobody is watching and the frame was not built.</returns>
  internal bool TryBroadcast(UpdateState state) {
    if(!this.hub.HasClients) {
      return false;
    }

    this.hub.Broadcast(UpdateFrames.Snapshot(state));

    return true;
  }

  private void Broadcast(UpdateState state) {
    this.TryBroadcast(state);
  }
}
