using Flux.Audio;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Flux.Web.Server;

/// <summary>
/// Sends the audio device list to the browsers that asked for it, ten times a
/// second.
///
/// Ten and not thirty because the list itself changes when somebody plugs in a
/// cable. What moves at this rate is the level meter beside the selected device,
/// and that meter is the reason the feed exists at all: on this rig the endpoint
/// carrying the master mix is called <c>Microfoon (2- DDJ-FLX10)</c> and thirteen
/// permanently silent inputs sit beside it, so a name cannot identify the right
/// device and only a level that moves when a physical fader does can.
///
/// The frames go through the ordinary client queue rather than the spectrum's
/// single-frame slot, because a device list is small, is rare, and must not be
/// dropped: a picker showing a device that has been unplugged is worse than a
/// picker that updates a tenth of a second late.
/// </summary>
public sealed class DevicesBroadcaster: IHostedService, IDisposable {
  private readonly SocketHub hub;

  /// <summary>
  /// Null in a build with no audio support, which is a real configuration:
  /// <c>Flux.Web.Server</c> is platform-neutral and the audio stack is
  /// Windows-only. Nothing here starts in that case, and the protocol's answer to a
  /// device selection stays <c>rejected</c>.
  /// </summary>
  private readonly IAudioDevices? audio;

  private readonly WebOptions options;

  private CancellationTokenSource? stopping;
  private Task? sampling;

  public DevicesBroadcaster(SocketHub hub, IAudioDevices? audio, IOptions<WebOptions> options) {
    ArgumentNullException.ThrowIfNull(options);

    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.audio = audio;
    this.options = options.Value;
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    if(this.audio is null) {
      return Task.CompletedTask;
    }

    this.stopping = new CancellationTokenSource();
    this.sampling = this.SampleAsync(this.stopping.Token);

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    if(this.stopping is not null) {
      await this.stopping.CancelAsync().ConfigureAwait(false);
    }

    if(this.sampling is not null) {
      await this.sampling.ConfigureAwait(false);
    }
  }

  public void Dispose() {
    this.stopping?.Dispose();
  }

  /// <summary>
  /// Composes the list and sends it, or declines to.
  ///
  /// Internal and separate from the timer so a test can assert the subscription
  /// gate without waiting on real tenths of a second.
  /// </summary>
  /// <returns>False when nobody has asked for the list.</returns>
  internal bool TryBroadcast() {
    if(this.audio is null || !this.hub.HasDeviceSubscribers) {
      return false;
    }

    this.hub.BroadcastDevices(DeviceFrames.Snapshot(this.audio));

    return true;
  }

  private async Task SampleAsync(CancellationToken token) {
    using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / this.options.DevicesHz));

    try {
      while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
        this.TryBroadcast();
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }
}
