using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Rekordbox;

/// <summary>
/// rekordbox as a sync source: tempo, beat and bar from the master deck and the track's
/// own beatgrid, read from rekordbox's memory and its analysis files.
///
/// Two jobs in one source, and both are always on. As the active source it drives the
/// grid. Whichever source is active, it sets the downbeat from the grid, since a
/// downbeat is a statement about the track that is true whoever keeps time - with Link
/// selected, Link keeps the tempo and rekordbox says where bar 1 is, which no other
/// source can.
///
/// This class is the loop and the process; what a read means is
/// <see cref="RekordboxFollower"/>'s. Every way of not reading rekordbox ends in a
/// sentence on the page, because each has a different fix: start rekordbox, start it
/// without administrator rights, or find the new version's chains with the tool.
/// </summary>
public sealed class RekordboxSource: BackgroundService, ISyncSource {
  public const string SourceName = "rekordbox";

  /// <summary>
  /// How often rekordbox is read. Flux's own number, reasoned: a read costs well under a
  /// millisecond, and the playhead moves every 5 to 10 ms, so reading about as often
  /// as it moves catches each move while it is fresh. Timer resolution makes the real
  /// interval somewhat longer, which the follower's tolerances allow for.
  /// </summary>
  private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

  /// <summary>How often to look for rekordbox while it is not open, or not readable. Flux's own number: soon enough after a launch to not be noticed.</summary>
  private static readonly TimeSpan AttachInterval = TimeSpan.FromSeconds(2);

  private readonly RekordboxOptions options;
  private readonly IMonotonicClock clock;
  private readonly SyncSourceManager sources;
  private readonly double staleAfterMicros;
  private readonly ILogger<RekordboxSource> log;
  private readonly RekordboxFollower follower;

  /// <summary>The last sentence logged, so a state that lasts is logged once rather than on every read.</summary>
  private string? logged;

  public RekordboxSource(
    SyncSourceManager sources,
    IMonotonicClock clock,
    IOptions<RekordboxOptions> options,
    IOptions<BeatOptions> beat,
    ILogger<RekordboxSource> log) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(beat);

    this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.options = options.Value;
    this.staleAfterMicros = beat.Value.StaleAfter.TotalMilliseconds * 1000.0;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    // Registered last, as every source is, so Status() finds a complete object.
    ITimelineSink sink = sources.Register(this);
    this.follower = new RekordboxFollower(sink, AnlzBeatGrid.Read, dat => AnlzPhrases.Read(AnlzFile.ExtendedPath(dat)));
    sources.BarShifted += this.follower.Shift;
  }

  /// <inheritdoc/>
  public string Name => SourceName;

  /// <summary>
  /// Live while a playing master is being followed, quiet once it has stopped being,
  /// waiting before it ever has been. Confidence is 1 whenever there is a tempo: it is
  /// read, not estimated.
  /// </summary>
  public SyncSourceStatus Status() {
    (int? deck, string? detail, string? phrase) = this.follower.Describe();
    var diagnostics = new SyncSourceDiagnostics(Deck: deck, Detail: this.options.Enabled ? detail : "turned off by Rekordbox:Enabled", Phrase: phrase);

    (bool ever, double? last) = this.follower.Published();
    if(last is not { } at) {
      return new SyncSourceStatus(ever ? SyncSourceState.Quiet : SyncSourceState.Waiting, Confidence: 0, diagnostics);
    }

    bool quiet = this.clock.NowMicros - at > this.staleAfterMicros;
    return new SyncSourceStatus(quiet ? SyncSourceState.Quiet : SyncSourceState.Live, Confidence: 1, diagnostics);
  }

  public override void Dispose() {
    this.sources.BarShifted -= this.follower.Shift;
    base.Dispose();
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Nothing above the first await may throw, for the reason the Link listener gives:
    // a fault here would stop Flux from starting, over a source that is optional.
    await Task.Yield();
    if(!this.options.Enabled) {
      this.log.LogInformation("rekordbox source is off (Rekordbox:Enabled).");
      return;
    }

    while(!stoppingToken.IsCancellationRequested) {
      try {
        await this.AttachAndFollowAsync(stoppingToken).ConfigureAwait(false);
      } catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested) {
        return;
      } catch(Exception error) {
        this.log.LogWarning(error, "Reading rekordbox failed; trying again.");
        this.Lose("reading rekordbox failed; see the log");
      }

      try {
        await Task.Delay(AttachInterval, stoppingToken).ConfigureAwait(false);
      } catch(OperationCanceledException) {
        return;
      }
    }
  }

  /// <summary>One attach, and following it until rekordbox closes or cannot be read.</summary>
  private async Task AttachAndFollowAsync(CancellationToken stoppingToken) {
    RekordboxAttachResult attach = RekordboxProcess.Attach();
    if(attach.Process is not { } rekordbox) {
      this.Lose(attach.State switch {
        RekordboxAttachState.NotRunning => "rekordbox is not running",
        RekordboxAttachState.Elevated => "rekordbox runs as administrator and Flux does not, so it cannot be read; start rekordbox normally",
        RekordboxAttachState.Unsupported => "this build of Flux cannot read rekordbox (it needs 64-bit)",
        _ => "Windows refused to let Flux read rekordbox",
      });
      return;
    }

    using(rekordbox) {
      if(!RekordboxChains.TryGet(rekordbox.Version, out RekordboxChains chains)) {
        // Named in full, because the fix is a tool somebody has to run, not a setting.
        this.Lose($"rekordbox {rekordbox.Version.ToString(3)} is a version Flux has no chains for; find them with tools/Flux.Rekordbox.ChainFinder");
        while(!rekordbox.HasExited) {
          await Task.Delay(AttachInterval, stoppingToken).ConfigureAwait(false);
        }

        return;
      }

      this.log.LogInformation("Reading rekordbox {Version} (process {Id}).", rekordbox.Version.ToString(3), rekordbox.ProcessId);
      while(!rekordbox.HasExited) {
        RekordboxReading reading = chains.Read(rekordbox, rekordbox.ModuleBase);
        this.follower.Follow(reading, this.clock.NowMicros);
        this.LogDetail();
        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
      }

      this.Lose("rekordbox closed");
    }
  }

  private void Lose(string why) {
    this.follower.Lose(why);
    this.LogDetail();
  }

  private void LogDetail() {
    string? detail = this.follower.Describe().Detail;
    if(detail == this.logged) {
      return;
    }

    this.logged = detail;
    if(detail is null) {
      this.log.LogInformation("Following rekordbox deck {Deck}.", this.follower.Describe().Deck);
    } else {
      this.log.LogInformation("rekordbox source: {Detail}.", detail);
    }
  }
}
