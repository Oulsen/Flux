using Flux.Web.Server;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Flux.App;

/// <summary>
/// A release found and downloaded, waiting to be applied. The payload is whatever the
/// backend needs to apply it, typed as object so <see cref="UpdateService"/> never sees
/// a Velopack type and can be tested against a fake.
/// </summary>
internal sealed record StagedUpdate(string Version, object Payload);

/// <summary>What <see cref="UpdateService"/> asks of Velopack, behind a seam for the tests.</summary>
internal interface IUpdateBackend {
  /// <summary>
  /// True only for a copy that Setup.exe installed. A development build has nothing to
  /// update and must never reach the network for it.
  /// </summary>
  bool IsInstalled { get; }

  /// <summary>The newest release when it is newer than this copy, otherwise null.</summary>
  Task<StagedUpdate?> CheckAsync(CancellationToken cancellationToken);

  /// <summary>Downloads a release into the install's package folder.</summary>
  Task DownloadAsync(StagedUpdate update, CancellationToken cancellationToken);

  /// <summary>
  /// Hands the release to Velopack's updater, which waits for this process to end,
  /// swaps the app folder and, if asked, starts the new version.
  /// </summary>
  void ApplyAfterExit(StagedUpdate update, bool restart);
}

/// <summary>
/// Finds, downloads and applies a newer Flux.
///
/// A release is found and downloaded in the background, the page says it is ready, and
/// it is applied when somebody chooses to restart - or, if nobody does, when Flux next
/// quits, so the following start is the new version. Nothing is ever applied while Flux
/// runs, because applying means stopping, and stopping cuts the lights.
///
/// A restart stops Flux the ordinary way and applies on the way out, rather than letting
/// Velopack end the process at once, which is its own restart's way. The host has a Hue
/// stream to end properly and settings to finish writing, and a process ended under them
/// does neither.
/// </summary>
internal sealed class UpdateService: IAppUpdates {
  /// <summary>Where releases are published. Public, so no token is needed to read it.</summary>
  internal const string Repository = "https://github.com/Oulsen/Flux";

  // A download is the whole release, so it gets far longer than the question.
  private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

  private readonly IUpdateBackend backend;
  private readonly IHostApplicationLifetime lifetime;
  private readonly ILogger<UpdateService> log;
  private readonly TimeSpan checkTimeout;
  private readonly object gate = new object();
  private Task? running;
  private StagedUpdate? staged;
  private bool restart;
  private bool applied;

  /// <param name="backend">Velopack, or a fake in the tests.</param>
  /// <param name="enabled">False switches updating off: the state stays unavailable and nothing reaches the network.</param>
  /// <param name="lifetime">Stopped by a restart, which is how the update gets applied on the way out.</param>
  /// <param name="log">Where checks that failed are recorded.</param>
  /// <param name="checkTimeout">How long asking for a release may take before the check counts as failed. 30 seconds by default.</param>
  public UpdateService(
    IUpdateBackend backend,
    bool enabled,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateService> log,
    TimeSpan? checkTimeout = null) {
    this.backend = backend ?? throw new ArgumentNullException(nameof(backend));
    this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
    this.log = log ?? throw new ArgumentNullException(nameof(log));
    this.checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(30);

    this.State = enabled && backend.IsInstalled
      ? new UpdateState(UpdateStatus.Current, null)
      : new UpdateState(UpdateStatus.Unavailable, null);
  }

  public UpdateState State { get; private set; }

  public event Action<UpdateState>? Changed;

  /// <summary>
  /// Checks now. Joins a check already running, and does nothing once a release is
  /// ready: it stays ready until it is applied, and checking again would only download
  /// it twice.
  /// </summary>
  public void Check() {
    _ = this.CheckAsync();
  }

  /// <summary>
  /// <see cref="Check"/> with the check to wait on, for the background timer and the tests.
  /// </summary>
  internal Task CheckAsync(CancellationToken cancellationToken = default) {
    lock(this.gate) {
      if(this.State.Status is UpdateStatus.Unavailable or UpdateStatus.Ready) {
        return Task.CompletedTask;
      }

      return this.running ??= this.RunCheckAsync(cancellationToken);
    }
  }

  public void RestartToUpdate() {
    lock(this.gate) {
      if(this.staged is null) {
        return;
      }

      this.restart = true;
    }

    this.log.LogInformation("Restarting to update to Flux {Version}.", this.staged.Version);
    this.lifetime.StopApplication();
  }

  /// <summary>
  /// Called once Flux has stopped: hands a downloaded release to the updater, which
  /// applies it once this process has ended, and starts it if a restart was asked for.
  /// Does nothing when no release is ready.
  /// </summary>
  public void ApplyOnExit() {
    StagedUpdate update;
    bool restartAfter;

    lock(this.gate) {
      if(this.applied || this.staged is null) {
        return;
      }

      this.applied = true;
      update = this.staged;
      restartAfter = this.restart;
    }

    try {
      this.backend.ApplyAfterExit(update, restartAfter);
      this.log.LogInformation("Flux {Version} will be applied as this copy exits.", update.Version);
    } catch(Exception ex) {
      // On the way out with nothing left to fall back on: this copy still works, and the
      // release is found again by the next start.
      this.log.LogError(ex, "Flux {Version} could not be handed to the updater.", update.Version);
    }
  }

  private async Task RunCheckAsync(CancellationToken cancellationToken) {
    // Never completes synchronously, so running is assigned before the end below clears it.
    await Task.Yield();

    this.Publish(new UpdateState(UpdateStatus.Checking, null));

    UpdateState outcome;

    try {
      // Velopack's question takes no token, so the deadline is enforced around it.
      using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      deadline.CancelAfter(this.checkTimeout);

      StagedUpdate? found = await this.backend.CheckAsync(deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);

      if(found is null) {
        outcome = new UpdateState(UpdateStatus.Current, null);
      } else {
        deadline.CancelAfter(DownloadTimeout);
        await this.backend.DownloadAsync(found, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false);

        lock(this.gate) {
          this.staged = found;
        }

        this.log.LogInformation("Flux {Version} is downloaded and applies on the next restart.", found.Version);
        outcome = new UpdateState(UpdateStatus.Ready, found.Version);
      }
    } catch(Exception ex) {
      // A warning and not an error: offline is an ordinary state for a laptop at a gig.
      this.log.LogWarning(ex, "Checking for a newer Flux failed.");
      outcome = new UpdateState(UpdateStatus.Failed, null);
    }

    lock(this.gate) {
      this.running = null;
    }

    this.Publish(outcome);
  }

  private void Publish(UpdateState state) {
    lock(this.gate) {
      this.State = state;
    }

    this.Changed?.Invoke(state);
  }
}

/// <summary>Velopack's update manager, reading the project's GitHub releases.</summary>
internal sealed class VelopackUpdateBackend: IUpdateBackend {
  private readonly UpdateManager manager;

  // Stable releases only: a test tag makes a prerelease, which installed copies must never pick up.
  public VelopackUpdateBackend(string repository) {
    this.manager = new UpdateManager(new GithubSource(repository, accessToken: null, prerelease: false));
  }

  public bool IsInstalled => this.manager.IsInstalled;

  public async Task<StagedUpdate?> CheckAsync(CancellationToken cancellationToken) {
    UpdateInfo? info = await this.manager.CheckForUpdatesAsync().ConfigureAwait(false);

    return info is null ? null : new StagedUpdate(info.TargetFullRelease.Version.ToString(), info);
  }

  public Task DownloadAsync(StagedUpdate update, CancellationToken cancellationToken) {
    return this.manager.DownloadUpdatesAsync((UpdateInfo)update.Payload, progress: null, cancellationToken);
  }

  public void ApplyAfterExit(StagedUpdate update, bool restart) {
    this.manager.WaitExitThenApplyUpdates(((UpdateInfo)update.Payload).TargetFullRelease, silent: true, restart: restart);
  }
}

/// <summary>
/// Checks for a newer Flux shortly after start and then every two hours.
///
/// Two hours is Flux's own choice and was reasoned about, not measured: a check that
/// finds nothing is one small unauthenticated request, far inside GitHub's limit, and a
/// release is downloaded once however often it is asked about, so checking often costs
/// next to nothing and lets a copy left on hear of a release the same evening. The first
/// check waits a minute so it never competes with a start that is finding devices and a
/// bridge.
/// </summary>
internal sealed class UpdateCheckService: BackgroundService {
  private static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
  private static readonly TimeSpan Interval = TimeSpan.FromHours(2);

  private readonly UpdateService updates;

  public UpdateCheckService(UpdateService updates) {
    this.updates = updates ?? throw new ArgumentNullException(nameof(updates));
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if(this.updates.State.Status == UpdateStatus.Unavailable) {
      return;
    }

    try {
      await Task.Delay(FirstCheck, stoppingToken).ConfigureAwait(false);

      using PeriodicTimer timer = new PeriodicTimer(Interval);

      do {
        await this.updates.CheckAsync(stoppingToken).ConfigureAwait(false);
      } while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
      // Flux is stopping.
    }
  }
}
