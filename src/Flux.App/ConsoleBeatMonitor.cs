using Flux.Beat;
using Microsoft.Extensions.Hosting;
using System.Globalization;
using System.Threading.Channels;

namespace Flux.App;

/// <summary>
/// Prints one line per beat, so the Link grid can be watched before there is a
/// browser to watch it in.
///
/// This is the Phase 1 stand-in for the visualizer, not a feature: once the web
/// layer lands, the same beat events go out over the WebSocket instead and this
/// stays only as a console-side sanity check.
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
  private readonly Channel<BeatEvent> queue = Channel.CreateBounded<BeatEvent>(
    new BoundedChannelOptions(QueueCapacity) {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = true,
    });

  private Task? printer;
  private CancellationTokenSource? stopping;

  public ConsoleBeatMonitor(BeatScheduler scheduler, BeatGrid grid) {
    this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    this.stopping = new CancellationTokenSource();
    this.printer = this.PrintAsync(this.stopping.Token);
    this.scheduler.Beat += this.Enqueue;

    Console.WriteLine("Waiting for Ableton Link. Enable LINK in rekordbox and play a track. Ctrl+C to stop.");
    Console.WriteLine();
    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    this.scheduler.Beat -= this.Enqueue;
    this.queue.Writer.TryComplete();

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
        Console.WriteLine(Format(occurred, snapshot));
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
  /// </summary>
  private static string Format(BeatEvent occurred, BeatGridSnapshot snapshot) {
    char[] bar = ['.', '.', '.', '.'];
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
