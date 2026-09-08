using Flux.Audio;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Flux.Web.Server;

/// <summary>
/// Sends the analyzer's picture to the browsers that asked for it, thirty times a
/// second.
///
/// It samples rather than subscribes, and that is the design rather than a
/// shortcut. The analyzer produces sixty frames a second into a slot holding the
/// newest one; this reads that slot at the rate the protocol advertises. So
/// downsampling is free, a subscriber that cannot keep up sees fewer frames and
/// never an older one, and no browser's ability to draw can ever slow down beat
/// detection - which matters because the same detector is driving the lights.
///
/// Nothing is composed at all while nobody is subscribed. The phone in the booth
/// never asks for a spectrum, and on a normal night that is the only client there
/// is.
/// </summary>
public sealed class SpectrumBroadcaster: IHostedService, IDisposable {
  private readonly SocketHub hub;

  /// <summary>
  /// Null in a build with no audio support, which is a real configuration rather
  /// than a defensive nullable: <c>Flux.Web.Server</c> is platform-neutral and the
  /// audio stack is Windows-only, so a host that never registers one still has to
  /// serve a page. Nothing here starts in that case.
  /// </summary>
  private readonly IAudioSpectrum? audio;

  private readonly WebOptions options;

  private CancellationTokenSource? stopping;
  private Task? sampling;

  public SpectrumBroadcaster(SocketHub hub, IAudioSpectrum? audio, IOptions<WebOptions> options) {
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
  /// Composes one frame and sends it, or declines to.
  ///
  /// Internal and separate from the timer so the tests can assert on the two
  /// reasons it declines - nobody subscribed, and nothing being heard - without
  /// waiting on real thirtieths of a second.
  /// </summary>
  /// <returns>False when there was nothing to send, or nobody to send it to.</returns>
  internal bool TryBroadcast() {
    if(this.audio is null || !this.hub.HasSpectrumSubscribers) {
      return false;
    }

    if(this.audio.Spectrum() is not SpectrumSnapshot snapshot) {
      // Nothing is being heard. Deliberately sending no frame rather than a frame
      // of zeroes: an empty spectrum drawn at 30 Hz is a picture of silence, which
      // is indistinguishable from a picture of a bug in the analyzer.
      return false;
    }

    this.hub.BroadcastSpectrum(Frames.Spectrum(new SpectrumData(
      snapshot.Bins,
      snapshot.Kick,
      snapshot.Mids,
      snapshot.Highs,
      snapshot.Flux,
      BeatName(snapshot.Strength))));

    return true;
  }

  private async Task SampleAsync(CancellationToken token) {
    using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / this.options.SpectrumHz));

    try {
      while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
        this.TryBroadcast();
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }

  /// <summary>
  /// The protocol's name for the detector's verdict.
  ///
  /// A switch rather than <c>ToString().ToLowerInvariant()</c>, so renaming the
  /// enum is a compile error here instead of a silently changed wire format that
  /// only a browser ever notices.
  /// </summary>
  private static string BeatName(BeatStrength strength) {
    return strength switch {
      BeatStrength.Beat => "beat",
      BeatStrength.Strong => "strong",
      _ => "none",
    };
  }
}
