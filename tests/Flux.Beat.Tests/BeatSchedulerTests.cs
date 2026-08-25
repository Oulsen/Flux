using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flux.Beat.Tests;

public class BeatSchedulerTests {
  [Fact]
  public void AimsAtTheNextWholeBeatWhenStartingCold() {
    Assert.Equal(1, BeatScheduler.NextBeatAfter(scheduled: null, currentBeat: 0.0));
    Assert.Equal(4, BeatScheduler.NextBeatAfter(scheduled: null, currentBeat: 3.4));
    Assert.Equal(-3, BeatScheduler.NextBeatAfter(scheduled: null, currentBeat: -3.2));
  }

  [Fact]
  public void KeepsAimingAtATargetThatHasNotArrivedYet() {
    Assert.Equal(1, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 0.5));
    Assert.Equal(1, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 0.999));
  }

  /// <summary>
  /// Regression: treating any overshoot as a miss meant the loop resynced to the
  /// following beat every single time it woke, because <c>Thread.Sleep</c>
  /// quantises to ~15.6 ms until the timer resolution is raised. The scheduler
  /// chased the next beat forever and fired nothing at all.
  /// </summary>
  [Fact]
  public void FiresABeatThatIsSlightlyLateRatherThanDroppingIt() {
    Assert.Equal(1, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 1.0));
    Assert.Equal(1, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 1.05));
    Assert.Equal(1, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 1.24));
  }

  /// <summary>
  /// Past a quarter beat the flash belongs to the previous kick, so the beat is
  /// abandoned. Oversleeping several beats resyncs rather than firing the missed
  /// ones back to back - a burst of catch-up flashes reads far worse on lights
  /// than one that never happened.
  /// </summary>
  [Fact]
  public void SkipsABeatThatIsTooLateToLookRight() {
    Assert.Equal(2, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 1.5));
    Assert.Equal(6, BeatScheduler.NextBeatAfter(scheduled: 1, currentBeat: 5.2));
  }

  /// <summary>
  /// A replacement timeline can move the beat numbering backwards - a target far
  /// in the future is a stale one, not something to wait out.
  /// </summary>
  [Fact]
  public void ResyncsWhenTheTargetIsImplausiblyFarAhead() {
    Assert.Equal(6, BeatScheduler.NextBeatAfter(scheduled: 100, currentBeat: 5.2));
  }

  /// <summary>
  /// The one thing the pure arithmetic cannot show: that the thread actually
  /// runs and fires. Deliberately loose bounds - this asserts the scheduler is
  /// alive and roughly on tempo, and leaves precision to the Phase 0 timing
  /// measurements.
  /// </summary>
  [Fact]
  public async Task FiresBeatsOnItsOwnThread() {
    MonotonicClock clock = new MonotonicClock();
    BeatGrid grid = new BeatGrid(clock, quantum: 4, TimeSpan.FromSeconds(2.5));

    // 300 BPM: fast enough to count several beats without a slow test.
    grid.Apply(new BeatTimeline(MicrosPerBeat: 200_000, BeatOrigin: 0, TimeOriginMicros: (long)clock.NowMicros));

    using BeatScheduler scheduler = new BeatScheduler(grid, clock, NullLogger<BeatScheduler>.Instance);

    List<BeatEvent> fired = new List<BeatEvent>();
    scheduler.Beat += occurred => {
      lock(fired) {
        fired.Add(occurred);
      }
    };

    await scheduler.StartAsync(TestContext.Current.CancellationToken);
    await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
    await scheduler.StopAsync(TestContext.Current.CancellationToken);

    BeatEvent[] observed;
    lock(fired) {
      observed = [.. fired];
    }

    Assert.InRange(observed.Length, 3, 7);

    // Consecutive, never repeated: the loop advances its target after firing.
    for(int index = 1; index < observed.Length; index++) {
      Assert.Equal(observed[index - 1].Index + 1, observed[index].Index);
    }

    Assert.All(observed, occurred => Assert.Equal(300, occurred.Bpm, precision: 2));
    Assert.All(observed, occurred => Assert.False(occurred.IsBarAligned));
    Assert.All(observed, occurred => Assert.InRange(occurred.PositionInBar, 1, 4));
  }
}
