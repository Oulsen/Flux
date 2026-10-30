using Flux.Web.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// Finding, downloading and applying a release, against a fake Velopack. The claims
/// worth holding: nothing reaches the network when it cannot or must not, nothing is
/// applied while Flux runs, and a release that was downloaded is never lost on the way out.
/// </summary>
public sealed class UpdateServiceTests {
  [Fact]
  public async Task ADevelopmentBuildNeverChecks() {
    FakeBackend backend = new FakeBackend { IsInstalled = false };
    UpdateService updates = NewService(backend);

    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(UpdateStatus.Unavailable, updates.State.Status);
    Assert.Equal(0, backend.Checks);
  }

  [Fact]
  public async Task SwitchedOffItNeverChecks() {
    FakeBackend backend = new FakeBackend();
    UpdateService updates = NewService(backend, enabled: false);

    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(UpdateStatus.Unavailable, updates.State.Status);
    Assert.Equal(0, backend.Checks);
  }

  [Fact]
  public async Task NothingNewerLeavesItCurrent() {
    FakeBackend backend = new FakeBackend();
    UpdateService updates = NewService(backend);

    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(UpdateStatus.Current, updates.State.Status);
    Assert.Equal(0, backend.Downloads);
  }

  [Fact]
  public async Task ANewerReleaseIsDownloadedAndReady() {
    FakeBackend backend = new FakeBackend { Release = new StagedUpdate("0.7.0", new object()) };
    UpdateService updates = NewService(backend);
    List<UpdateStatus> seen = new List<UpdateStatus>();
    updates.Changed += state => seen.Add(state.Status);

    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(new UpdateState(UpdateStatus.Ready, "0.7.0"), updates.State);
    Assert.Equal([UpdateStatus.Checking, UpdateStatus.Ready], seen);
    Assert.Equal(1, backend.Downloads);
  }

  /// <summary>Once ready it stays ready: checking again would only download it twice.</summary>
  [Fact]
  public async Task AReadyReleaseIsNotFetchedAgain() {
    FakeBackend backend = new FakeBackend { Release = new StagedUpdate("0.7.0", new object()) };
    UpdateService updates = NewService(backend);

    await updates.CheckAsync(TestContext.Current.CancellationToken);
    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(1, backend.Checks);
    Assert.Equal(1, backend.Downloads);
  }

  [Fact]
  public async Task AFailedCheckIsReportedAndNotThrown() {
    FakeBackend backend = new FakeBackend { Failure = new HttpRequestException("offline") };
    UpdateService updates = NewService(backend);

    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(UpdateStatus.Failed, updates.State.Status);
  }

  [Fact]
  public async Task ACheckThatHangsTimesOut() {
    FakeBackend backend = new FakeBackend { Hang = true };
    UpdateService updates = NewService(backend, checkTimeout: TimeSpan.FromMilliseconds(50));

    await updates.CheckAsync(TestContext.Current.CancellationToken);

    Assert.Equal(UpdateStatus.Failed, updates.State.Status);
  }

  /// <summary>The background check and a click on the page at the same moment are one check.</summary>
  [Fact]
  public async Task OverlappingChecksAreOne() {
    TaskCompletionSource<StagedUpdate?> answer = new TaskCompletionSource<StagedUpdate?>();
    FakeBackend backend = new FakeBackend { Answer = answer.Task };
    UpdateService updates = NewService(backend);

    Task first = updates.CheckAsync(TestContext.Current.CancellationToken);
    Task second = updates.CheckAsync(TestContext.Current.CancellationToken);
    answer.SetResult(null);
    await Task.WhenAll(first, second);

    Assert.Equal(1, backend.Checks);
  }

  [Fact]
  public async Task RestartStopsFluxAndAppliesWithARestartOnTheWayOut() {
    FakeBackend backend = new FakeBackend { Release = new StagedUpdate("0.7.0", new object()) };
    StoppingLifetime lifetime = new StoppingLifetime();
    UpdateService updates = NewService(backend, lifetime: lifetime);
    await updates.CheckAsync(TestContext.Current.CancellationToken);

    updates.RestartToUpdate();

    Assert.True(lifetime.Stopped);
    Assert.Empty(backend.Applied);

    updates.ApplyOnExit();

    Assert.Equal([("0.7.0", true)], backend.Applied);
  }

  /// <summary>Quitting with a release ready applies it quietly, so the next start is the new version.</summary>
  [Fact]
  public async Task QuittingAppliesAReadyReleaseWithoutARestart() {
    FakeBackend backend = new FakeBackend { Release = new StagedUpdate("0.7.0", new object()) };
    UpdateService updates = NewService(backend);
    await updates.CheckAsync(TestContext.Current.CancellationToken);

    updates.ApplyOnExit();
    updates.ApplyOnExit();

    Assert.Equal([("0.7.0", false)], backend.Applied);
  }

  [Fact]
  public void RestartWithNothingReadyDoesNotStopFlux() {
    StoppingLifetime lifetime = new StoppingLifetime();
    UpdateService updates = NewService(new FakeBackend(), lifetime: lifetime);

    updates.RestartToUpdate();
    updates.ApplyOnExit();

    Assert.False(lifetime.Stopped);
  }

  private static UpdateService NewService(
    FakeBackend backend,
    bool enabled = true,
    StoppingLifetime? lifetime = null,
    TimeSpan? checkTimeout = null) {
    return new UpdateService(
      backend,
      enabled,
      lifetime ?? new StoppingLifetime(),
      NullLogger<UpdateService>.Instance,
      checkTimeout);
  }

  private sealed class FakeBackend: IUpdateBackend {
    public bool IsInstalled { get; set; } = true;

    public StagedUpdate? Release { get; set; }

    public Exception? Failure { get; set; }

    public bool Hang { get; set; }

    public Task<StagedUpdate?>? Answer { get; set; }

    public int Checks { get; private set; }

    public int Downloads { get; private set; }

    public List<(string Version, bool Restart)> Applied { get; } = new List<(string Version, bool Restart)>();

    public Task<StagedUpdate?> CheckAsync(CancellationToken cancellationToken) {
      this.Checks++;

      if(this.Failure is not null) {
        return Task.FromException<StagedUpdate?>(this.Failure);
      }

      if(this.Hang) {
        return new TaskCompletionSource<StagedUpdate?>().Task;
      }

      return this.Answer ?? Task.FromResult(this.Release);
    }

    public Task DownloadAsync(StagedUpdate update, CancellationToken cancellationToken) {
      this.Downloads++;
      return Task.CompletedTask;
    }

    public void ApplyAfterExit(StagedUpdate update, bool restart) {
      this.Applied.Add((update.Version, restart));
    }
  }

  private sealed class StoppingLifetime: IHostApplicationLifetime {
    public bool Stopped { get; private set; }

    public CancellationToken ApplicationStarted => CancellationToken.None;

    public CancellationToken ApplicationStopping => CancellationToken.None;

    public CancellationToken ApplicationStopped => CancellationToken.None;

    public void StopApplication() {
      this.Stopped = true;
    }
  }
}
