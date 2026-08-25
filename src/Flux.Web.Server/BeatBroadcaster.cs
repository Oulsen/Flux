using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace Flux.Web.Server;

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

  /// <summary>
  /// What <c>state.source</c> says when the build has no sync sources at all.
  ///
  /// Not reachable through the standard registrations, which always include the
  /// manual source, but the state frame has to say something and every other
  /// answer would name a source that is not there.
  /// </summary>
  private const string NoSource = "none";

  private readonly SocketHub hub;
  private readonly BeatScheduler scheduler;
  private readonly BeatGrid grid;
  private readonly SyncSourceManager sources;
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

  public BeatBroadcaster(
    SocketHub hub,
    BeatScheduler scheduler,
    BeatGrid grid,
    SyncSourceManager sources,
    IOptions<WebOptions> options) {
    ArgumentNullException.ThrowIfNull(options);

    this.hub = hub ?? throw new ArgumentNullException(nameof(hub));
    this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
    this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
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
  /// Reads the grid and the sources into the shape the protocol describes.
  ///
  /// The three states are kept distinct rather than collapsed into a boolean
  /// because they call for three different things on screen: nothing known yet,
  /// a live tempo, and a tempo that is still being extrapolated after the source
  /// went silent. Sending a null grid for the first is what stops a client
  /// rendering an absent tempo as a confident 0.00 BPM.
  ///
  /// Internal rather than private so the tests can pin the frame this composes.
  /// It is the one place where the grid and the sources are read together, so
  /// the invariants that span them - a null grid exactly when waiting, counters
  /// belonging to the active source and nobody else - are only observable here.
  /// </summary>
  internal StateData Sample() {
    bool known = this.grid.TryGetSnapshot(out BeatGridSnapshot snapshot);

    IReadOnlyList<SyncSourceReport> registered = this.sources.Sources();
    SyncSourceStatus active = default;
    string activeName = NoSource;

    foreach(SyncSourceReport report in registered) {
      if(report.IsActive) {
        active = report.Status;
        activeName = report.Name;
        break;
      }
    }

    // The grid decides "waiting" and the source decides between "live" and
    // "quiet". Split that way because each knows one half: only the grid knows
    // whether a timeline exists at all, and only the source knows whether
    // silence means anything - a Link session gone quiet is a worsening guess,
    // a typed tempo left alone is exactly as true as when it was typed.
    string state = !known ? "waiting"
      : active.State == SyncSourceState.Quiet ? "quiet"
      : "live";

    GridData? reading = known
      ? new GridData(
        snapshot.Bpm,
        snapshot.Beat,
        snapshot.BeatIndex,
        snapshot.PositionInBar,
        snapshot.IsBarAligned)
      : null;

    return new StateData(
      activeName,
      state,
      [.. registered.Select(report => new SourceReportData(
        report.Name,
        StateName(report.Status.State),
        report.Status.Confidence))],
      reading,
      new AlignmentData(snapshot.IsBarAligned, MethodName(snapshot.AlignmentMethod)),
      Counters(active.Diagnostics, snapshot.MaxReanchorJumpBeats));
  }

  /// <summary>
  /// Merges the active source's own counters with the grid's drift metric.
  ///
  /// The two come from different places on purpose. A source's counters describe
  /// how that source is doing, so they follow the selection; the drift metric
  /// describes whether extrapolating the current timeline still agrees with what
  /// arrives next, which is worth asking of whichever source is driving.
  /// </summary>
  private static DiagnosticsData Counters(SyncSourceDiagnostics source, double reanchorMaxBeats) {
    return new DiagnosticsData(
      reanchorMaxBeats,
      source.Packets,
      source.Fps,
      source.BeatsDetected,
      source.BpmLocked,
      source.LastBeatAgoMs,
      source.Taps,
      source.LastTapAgoMs);
  }

  /// <summary>
  /// The protocol's name for a source state.
  ///
  /// A switch rather than <c>ToString().ToLowerInvariant()</c>, so that renaming
  /// the enum is a compile error here instead of a silently changed wire format
  /// that only a browser ever notices.
  /// </summary>
  private static string StateName(SyncSourceState state) {
    return state switch {
      SyncSourceState.Live => "live",
      SyncSourceState.Quiet => "quiet",
      _ => "waiting",
    };
  }

  /// <summary>The protocol's name for what aligned the bar, or null while it is only a count.</summary>
  private static string? MethodName(BarAlignmentMethod method) {
    return method switch {
      BarAlignmentMethod.Cue => "cue",
      BarAlignmentMethod.Tap => "tap",
      BarAlignmentMethod.Drop => "drop",
      _ => null,
    };
  }
}
