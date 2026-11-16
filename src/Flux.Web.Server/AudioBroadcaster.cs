using Flux.Audio;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;

namespace Flux.Web.Server;

/// <summary>
/// Sends the audio source's beat sensitivity to every browser, whenever it changes.
///
/// No timer, for the reason <see cref="StageBroadcaster"/> has none: a setting has not
/// moved since somebody last touched it. Optional the way audio is: a build with no
/// audio source has no sensitivity, and this then starts nothing.
/// </summary>
public sealed class AudioBroadcaster: IHostedService {
  private readonly SocketHub hub;
  private readonly IAudioSensitivity? sensitivity;

  public AudioBroadcaster(SocketHub hub, IAudioSensitivity? sensitivity) {
    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.sensitivity = sensitivity;
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    if(this.sensitivity is not null) {
      this.sensitivity.SensitivityChanged += this.Broadcast;
    }

    return Task.CompletedTask;
  }

  public Task StopAsync(CancellationToken cancellationToken) {
    if(this.sensitivity is not null) {
      this.sensitivity.SensitivityChanged -= this.Broadcast;
    }

    return Task.CompletedTask;
  }

  private void Broadcast(int value) {
    if(this.hub.HasClients) {
      this.hub.Broadcast(Frames.Audio(new AudioData(value)));
    }
  }
}
