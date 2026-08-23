using Flux.Beat;
using Flux.Web.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace Flux.Web;

/// <summary>
/// Feeds the sync source to every connected browser: a state snapshot on a
/// timer, and a beat the moment one lands.
///
/// Two messages rather than one, because they answer different questions. The
/// snapshot is the readout - tempo, position, health - and is complete every
/// time, so a client that misses one loses nothing. The beat is an instant, and
/// no snapshot rate that is sane to send can place one: at 20 Hz a flash would
/// land up to 50 ms off, which at 128 BPM is a tenth of a beat and plainly
/// visible.
///
/// The beat handler is the reason for the queue between the two halves. It runs
/// on the scheduler thread, where anything slow delays the next beat, so it
/// hands the event over and returns - the same discipline the console monitor
/// uses, for the same reason.
/// </summary>
public sealed class BeatBroadcaster: IHostedService, IDisposable {
  /// <summary>
  /// Beats waiting to be encoded. Two bars at the outside: falling further
  /// behind than that means the process is in no state to be broadcasting old
  /// beats, and dropping them is the documented behaviour anyway.
  /// </summary>
  private const int BeatQueueCapacity = 16;

  /// <summary>The only sync source in version 1. Phase 2 makes this the active source's name.</summary>
  private const string SourceName = "link";

  private readonly SocketHub hub;
  private readonly BeatScheduler scheduler;
  private readonly BeatGrid grid;
  private readonly WebOptions options;

  private readonly Channel<BeatEvent> beats = Channel.CreateBounded<BeatEvent>(
    new BoundedChannelOptions(BeatQueueCapacity) {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = true,
    });

  private CancellationTokenSource? stopping;
  private Task? pumping;
  private Task? sampling;

  public BeatBroadcaster(SocketHub hub, BeatScheduler scheduler, BeatGrid grid, IOptions<WebOptions> options) {
    ArgumentNullException.ThrowIfNull(options);

    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
    this.options = options.Value;
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    this.stopping = new CancellationTokenSource();
    this.pumping = this.PumpBeatsAsync(this.stopping.Token);
    this.sampling = this.SampleStateAsync(this.stopping.Token);
    this.scheduler.Beat += this.Enqueue;

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    // The channel is never completed. Unsubscribing does not wait for an
    // in-flight Enqueue on the scheduler thread, so a TryComplete here could run
    // concurrently with its TryWrite - the very thing SingleWriter promises the
    // channel won't see. Cancellation ends the pump instead.
    this.scheduler.Beat -= this.Enqueue;

    if(this.stopping is not null) {
      await this.stopping.CancelAsync().ConfigureAwait(false);
    }

    if(this.pumping is not null) {
      await this.pumping.ConfigureAwait(false);
    }

    if(this.sampling is not null) {
      await this.sampling.ConfigureAwait(false);
    }
  }

  public void Dispose() {
    this.stopping?.Dispose();
  }

  /// <summary>
  /// Runs on the scheduler thread. Hands the beat off and returns immediately;
  /// the encoding and the fan-out happen elsewhere.
  /// </summary>
  private void Enqueue(BeatEvent occurred) {
    this.beats.Writer.TryWrite(occurred);
  }

  private async Task PumpBeatsAsync(CancellationToken token) {
    try {
      await foreach(BeatEvent occurred in this.beats.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
        if(!this.hub.HasClients) {
          continue;
        }

        this.hub.Broadcast(Frames.Beat(new BeatData(
          occurred.Index,
          occurred.PositionInBar,
          occurred.IsBarAligned,
          occurred.Bpm)));
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }

  private async Task SampleStateAsync(CancellationToken token) {
    using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / this.options.StateHz));

    try {
      while(await timer.WaitForNextTickAsync(token).ConfigureAwait(false)) {
        // With no browser open this is the only work an otherwise idle process
        // would be doing, twenty times a second, for nobody.
        if(this.hub.HasClients) {
          this.hub.Broadcast(Frames.State(this.Sample()));
        }
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }

  /// <summary>
  /// Reads the grid into the shape the protocol describes.
  ///
  /// The three states are kept distinct rather than collapsed into a boolean
  /// because they call for three different things on screen: nothing known yet,
  /// a live tempo, and a tempo that is still being extrapolated after the
  /// packets stopped. Sending a null grid for the first is what stops a client
  /// rendering an absent tempo as a confident 0.00 BPM.
  /// </summary>
  private StateData Sample() {
    bool known = this.grid.TryGetSnapshot(out BeatGridSnapshot snapshot);

    string state = !known ? "waiting"
      : snapshot.IsStale ? "quiet"
      : "live";

    GridData? reading = known
      ? new GridData(
        snapshot.Bpm,
        snapshot.Beat,
        snapshot.BeatIndex,
        snapshot.PositionInBar,
        snapshot.IsBarAligned)
      : null;

    // Zeroed before the first packet, which is exactly what the counters mean
    // at that point.
    DiagnosticsData diagnostics = new DiagnosticsData(snapshot.Packets, snapshot.MaxReanchorJumpBeats);

    return new StateData(SourceName, state, reading, diagnostics);
  }
}
