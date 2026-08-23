using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Beat;

/// <summary>One beat, fired at the instant it lands.</summary>
/// <param name="Index">The Link session beat number.</param>
/// <param name="PositionInBar">1-based position in the bar; meaningless while <paramref name="IsBarAligned"/> is false.</param>
/// <param name="IsBarAligned">Whether the bar position has been anchored by a cue press or a tap.</param>
/// <param name="Bpm">Tempo at the moment of the beat.</param>
/// <param name="ScheduledMicros">The local timestamp the beat was aimed at, not the one it fired at.</param>
public readonly record struct BeatEvent(
  long Index,
  int PositionInBar,
  bool IsBarAligned,
  double Bpm,
  double ScheduledMicros);

/// <summary>
/// Turns the beat grid into beat events on a dedicated thread.
///
/// Three decisions here come straight from Phase 0 measurements and should not
/// be revisited casually:
///
/// <list type="bullet">
/// <item><description><b>Sleep, do not spin.</b> A sleep-then-spin hybrid measured
/// 14.4% of a core for a <em>worse</em> 1.52 ms tail than plain sleep. Against a
/// 60-140 ms light path, sub-millisecond precision buys nothing worth a burning
/// core.</description></item>
/// <item><description><b>Aim at absolute targets.</b> Sleeping "one beat" at a
/// time accumulates every scheduling error; computing each beat's timestamp from
/// the timeline does not.</description></item>
/// <item><description><b>Re-read the timeline mid-wait.</b> The target moves when
/// the DJ moves the tempo fader, so the wait happens in short hops and the
/// destination is recalculated after each one.</description></item>
/// </list>
///
/// Note the timing requirement this class does not meet on its own: it needs
/// <c>timeBeginPeriod(1)</c> raised process-wide, without which every sleep here
/// is quantised to ~15.6 ms. The host owns that.
/// </summary>
public sealed class BeatScheduler: IHostedService, IDisposable {
  /// <summary>Longest single sleep, so a tempo change is picked up within one hop.</summary>
  private const double MaxSleepMillis = 20.0;

  /// <summary>Closer than this to the target counts as arrived; sleeping again would overshoot.</summary>
  private const double ArrivalToleranceMillis = 1.0;

  /// <summary>How long to wait for a timeline before looking again.</summary>
  private const int IdlePollMillis = 100;

  /// <summary>
  /// How late a beat may be and still be worth firing, as a fraction of a beat.
  /// A quarter beat is 86 ms at 175 BPM - past the point where a flash reads as
  /// belonging to the previous kick rather than this one.
  /// </summary>
  private const double LateToleranceBeats = 0.25;

  /// <summary>
  /// How far ahead a target may sit before it is treated as left over from a
  /// timeline that has since been renumbered.
  /// </summary>
  private const double StaleTargetBeats = 2.0;

  /// <summary>How long to wait for the beat thread on shutdown: the longest sleep it can be inside, plus slack.</summary>
  private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(500);

  private readonly BeatGrid grid;
  private readonly IMonotonicClock clock;
  private readonly int quantum;
  private readonly ILogger<BeatScheduler> log;
  private readonly CancellationTokenSource stopping = new CancellationTokenSource();

  private Thread? thread;

  public BeatScheduler(BeatGrid grid, IMonotonicClock clock, IOptions<LinkOptions> options, ILogger<BeatScheduler> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.quantum = options.Value.Quantum;
    this.log = log ?? throw new ArgumentNullException(nameof(log));
  }

  /// <summary>
  /// Raised once per beat, on the scheduler thread. Handlers must return
  /// promptly - anything slow here delays the next beat.
  /// </summary>
  public event Action<BeatEvent>? Beat;

  public Task StartAsync(CancellationToken cancellationToken) {
    this.thread = new Thread(this.Run) {
      Name = "Flux beat scheduler",
      IsBackground = true,

      // Above normal, not highest: the thread must beat a busy UI without ever
      // competing with the audio stack it is listening to.
      Priority = ThreadPriority.AboveNormal,
    };

    this.thread.Start();
    return Task.CompletedTask;
  }

  /// <summary>
  /// Stops the beat thread, and says so when it does not stop.
  ///
  /// The wait is bounded here rather than by <paramref name="cancellationToken"/>
  /// on purpose: <see cref="ShutdownTimeout"/> is already far inside any host
  /// shutdown budget, and the thread is a background thread, so a hung one cannot
  /// hold the process open either way. What it can do is hide the reason, which
  /// is what the warning is for.
  /// </summary>
  public async Task StopAsync(CancellationToken cancellationToken) {
    await this.stopping.CancelAsync().ConfigureAwait(false);

    Thread? running = this.thread;
    if(running is not null && !running.Join(ShutdownTimeout)) {
      this.log.LogWarning(
        "The beat scheduler thread was still running {Timeout} ms after being asked to stop.",
        ShutdownTimeout.TotalMilliseconds);
    }
  }

  public void Dispose() {
    this.stopping.Dispose();
  }

  private void Run() {
    CancellationToken stop = this.stopping.Token;
    double? scheduled = null;

    while(!stop.IsCancellationRequested) {
      if(this.grid.Timeline is not LinkTimeline timeline) {
        scheduled = null;
        Thread.Sleep(IdlePollMillis);
        continue;
      }

      double nowMicros = this.clock.NowMicros;
      scheduled = NextBeatAfter(scheduled, timeline.BeatAt(nowMicros));

      double remainingMillis = (timeline.MicrosOfBeat(scheduled.Value) - nowMicros) / 1000.0;
      if(remainingMillis > ArrivalToleranceMillis) {
        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(remainingMillis, MaxSleepMillis)));

        // The timeline may have been replaced while this thread slept, which
        // moves the target. Going back round re-measures rather than firing at
        // a timestamp that is no longer the beat.
        continue;
      }

      this.Fire(timeline, scheduled.Value);
      scheduled += 1;
    }
  }

  private void Fire(LinkTimeline timeline, double beat) {
    Action<BeatEvent>? handler = this.Beat;
    if(handler is null) {
      return;
    }

    long index = BeatGrid.IndexOf(beat);

    BeatEvent occurred = new BeatEvent(
      index,
      BeatGrid.PositionInBar(index, this.quantum),

      // Phase 2 anchors this from a cue press or a tap. Until then the bar
      // position is an arbitrary rotation and says so.
      IsBarAligned: false,
      timeline.Bpm,
      timeline.MicrosOfBeat(beat));

    try {
      handler(occurred);
    } catch(Exception ex) {
      // One misbehaving subscriber must not silently stop the light show.
      this.log.LogError(ex, "A beat handler threw at beat {Beat}.", occurred.Index);
    }
  }

  /// <summary>
  /// Chooses the beat to aim at next.
  ///
  /// The distinction that matters is between <em>slightly late</em> and
  /// <em>missed</em>. Waking a few milliseconds past the target is the normal
  /// case, not an exception: <c>Thread.Sleep</c> quantises to ~15.6 ms whenever
  /// the timer resolution has not been raised. Treating any overshoot as a miss
  /// makes the loop resync to the <em>following</em> beat every time it wakes,
  /// and a scheduler that perpetually chases the next beat never fires a single
  /// one. So a late beat still fires, and only <see cref="LateToleranceBeats"/>
  /// decides when one is too late to be worth it.
  ///
  /// The upper guard catches the opposite case - a replacement timeline that
  /// renumbers the beats and leaves the target stranded in the future.
  /// </summary>
  /// <param name="scheduled">The beat previously aimed at, or null on the first pass.</param>
  /// <param name="currentBeat">The fractional beat happening now.</param>
  public static double NextBeatAfter(double? scheduled, double currentBeat) {
    if(scheduled is not double next
      || next < currentBeat - LateToleranceBeats
      || next > currentBeat + StaleTargetBeats) {
      return Math.Floor(currentBeat) + 1;
    }

    return next;
  }
}
