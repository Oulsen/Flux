using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Threading.Channels;

namespace Flux.App;

/// <summary>
/// Prints one line per beat, so the Link grid can be watched without a browser.
///
/// This was Phase 1's stand-in for the visualizer; the web layer has taken that
/// job, so it is registered only when Console:BeatLog is set and stays as a
/// console-side sanity check for debugging the grid itself.
/// </summary>
public sealed class ConsoleBeatMonitor: IHostedService, IDisposable {
  /// <summary>
  /// Beats are dropped rather than queued without limit. Falling behind by more
  /// than a couple of bars means the console is the bottleneck, and stale beat
  /// lines are worth less than a responsive scheduler.
  /// </summary>
  private const int QueueCapacity = 16;

  private readonly BeatScheduler scheduler;
  private readonly BeatGrid grid;
  private readonly int quantum;
  private readonly Channel<BeatEvent> queue = Channel.CreateBounded<BeatEvent>(
    new BoundedChannelOptions(QueueCapacity) {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = true,
    });

  private Task? printer;
  private CancellationTokenSource? stopping;

  public ConsoleBeatMonitor(BeatScheduler scheduler, BeatGrid grid, IOptions<LinkOptions> options) {
    ArgumentNullException.ThrowIfNull(options);

    this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
    this.quantum = options.Value.Quantum;
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    this.stopping = new CancellationTokenSource();
    this.printer = this.PrintAsync(this.stopping.Token);
    this.scheduler.Beat += this.Enqueue;

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    // The channel is never completed. Unsubscribing does not wait for an
    // in-flight Enqueue on the scheduler thread, so a TryComplete here could run
    // concurrently with its TryWrite - the very thing SingleWriter promises the
    // channel won't see. Cancellation ends the printer instead.
    this.scheduler.Beat -= this.Enqueue;

    if(this.stopping is not null) {
      await this.stopping.CancelAsync().ConfigureAwait(false);
    }

    if(this.printer is not null) {
      await this.printer.ConfigureAwait(false);
    }
  }

  public void Dispose() {
    this.stopping?.Dispose();
  }

  /// <summary>
  /// Hands the beat off and returns immediately: this runs on the scheduler
  /// thread, where a blocking console write would delay the next beat.
  /// </summary>
  private void Enqueue(BeatEvent occurred) {
    this.queue.Writer.TryWrite(occurred);
  }

  private async Task PrintAsync(CancellationToken stoppingToken) {
    try {
      await foreach(BeatEvent occurred in this.queue.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false)) {
        this.grid.TryGetSnapshot(out BeatGridSnapshot snapshot);
        Console.WriteLine(Format(occurred, snapshot, this.quantum));
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }

  /// <summary>
  /// One line per beat.
  ///
  /// The bar position is drawn as a moving dot rather than printed as "BAR",
  /// because it is <em>not</em> anchored to the music yet - Link carries no
  /// downbeat, and Phase 2 supplies one from a cue press or a tap. A line
  /// reading "BAR" would be a claim this source cannot make.
  ///
  /// The readout is as wide as the configured quantum, not four slots. Hardcoding
  /// four made a quantum of eight print half a bar with the dot nowhere in it -
  /// silently wrong, which is worse than visibly unsupported.
  /// </summary>
  private static string Format(BeatEvent occurred, BeatGridSnapshot snapshot, int quantum) {
    char[] bar = new char[quantum];
    Array.Fill(bar, '.');

    if(occurred.PositionInBar >= 1 && occurred.PositionInBar <= bar.Length) {
      bar[occurred.PositionInBar - 1] = '#';
    }

    string alignment = occurred.IsBarAligned ? "bar aligned" : "bar unaligned";
    string freshness = snapshot.IsStale ? "  LINK QUIET" : string.Empty;

    return string.Create(
      CultureInfo.InvariantCulture,
      $"beat {occurred.Index,10}  [{new string(bar)}]  {occurred.Bpm,7:F2} BPM  {alignment}  "
        + $"packets {snapshot.Packets,5}  re-anchor {snapshot.MaxReanchorJumpBeats:F4} beats{freshness}");
  }
}
