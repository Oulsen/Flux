using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// Which launch gets to run. Each copy is made on a thread of its own, because a mutex
/// belongs to a thread and a second copy on the same one would simply own it again. The
/// names are unique per test, so a Flux running on this machine is never in the way.
/// </summary>
public sealed class SingleInstanceTests {
  private readonly string prefix = $@"Local\Flux.Tests.{Guid.NewGuid():N}";

  [Fact]
  public void ALaunchWhileFluxRunsIsNotTheFirst() {
    using SingleInstance running = new SingleInstance(this.prefix);

    Assert.True(running.IsFirst);
    Assert.False(this.LaunchOnItsOwnThread());
  }

  /// <summary>
  /// The copy that is stopping still has its handle open, so the mutex still exists. A
  /// launch in that window has to start rather than ask a copy on its way out to show
  /// itself, or there is no Flux at all.
  /// </summary>
  [Fact]
  public void ALaunchWhileFluxIsStoppingIsTheFirst() {
    using SingleInstance stopping = new SingleInstance(this.prefix);
    stopping.Leave();

    Assert.True(this.LaunchOnItsOwnThread());
  }

  /// <summary>A copy that crashed never released the mutex, and must not block the next one.</summary>
  [Fact]
  public void ALaunchAfterACrashIsTheFirst() {
    // Never disposed, as a crashed copy never is: its thread ends holding the mutex.
    Thread crashed = new Thread(() => _ = new SingleInstance(this.prefix));
    crashed.Start();
    crashed.Join();

    Assert.True(this.LaunchOnItsOwnThread());
  }

  private bool LaunchOnItsOwnThread() {
    bool first = false;

    Thread launch = new Thread(() => {
      using SingleInstance instance = new SingleInstance(this.prefix);
      first = instance.IsFirst;
    });

    launch.Start();
    launch.Join();

    return first;
  }
}
