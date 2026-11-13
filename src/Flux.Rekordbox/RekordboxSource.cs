using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text;

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
  private readonly int quantum;
  private readonly ILogger<RekordboxSource> log;
  private readonly RekordboxFollower follower;

  /// <summary>
  /// How long a track's id is trusted before the files' write times are looked at again.
  /// Flux's own number: rekordbox can analyse a loaded track, and a page should get the
  /// new waveform within a moment of it, without the disk being asked 20 times a second.
  /// </summary>
  private static readonly TimeSpan TrackIdFor = TimeSpan.FromSeconds(2);

  /// <summary>
  /// Part of every track id, raised whenever what <see cref="Track"/> answers changes
  /// shape. The page keeps an answer for good under its id, so a new build that answered
  /// the same id differently would be read with the old answer: a page that had cached a
  /// track before the colour waveform was added drew no waveform at all after it.
  /// </summary>
  private const int TrackFormat = 2;

  /// <summary>The last sentence logged, so a state that lasts is logged once rather than on every read.</summary>
  private string? logged;

  private readonly object trackGate = new object();

  /// <summary>The followed track's analysis path, its id, and when the id was worked out.</summary>
  private (string Path, string Id, double AtMicros)? named;

  /// <summary>The last track handed to the page, kept because each page that opens asks for it.</summary>
  private RekordboxTrack? served;

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
    this.quantum = beat.Value.Quantum;
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

  /// <summary>
  /// The deck followed and where its playhead is now, or null while no analysed track is
  /// followed. Its <see cref="RekordboxDeck.TrackId"/> is what <see cref="Track"/> takes.
  /// </summary>
  public RekordboxDeck? Deck() {
    if(this.follower.Playhead(this.clock.NowMicros) is not { } playhead) {
      return null;
    }

    return new RekordboxDeck(playhead.Deck, this.IdOf(playhead.TrackPath), playhead.PositionMs, playhead.Rate, playhead.Playing);
  }

  /// <summary>
  /// The followed track's waveform and grid, when <paramref name="trackId"/> names the
  /// track followed now; null for any other. Only the track on the deck can be asked for,
  /// so an id is never a way to have Flux open a file of the caller's choosing.
  /// </summary>
  public RekordboxTrack? Track(string trackId) {
    ArgumentNullException.ThrowIfNull(trackId);
    if(this.follower.Playhead(this.clock.NowMicros) is not { } playhead || this.IdOf(playhead.TrackPath) != trackId) {
      return null;
    }

    lock(this.trackGate) {
      if(this.served is { } kept && kept.TrackId == trackId) {
        return kept;
      }
    }

    // Both waveforms are 150 columns a second - each reader refuses any other rate - so
    // one rate describes whichever of them the track has.
    AnlzWaveform? threeBand = AnlzWaveformReader.Read(AnlzFile.ThreeBandPath(playhead.TrackPath));
    AnlzColorWaveform? rgb = AnlzColorWaveformReader.Read(AnlzFile.ExtendedPath(playhead.TrackPath));
    var track = new RekordboxTrack(
      trackId,
      threeBand?.EntriesPerSecond ?? rgb?.EntriesPerSecond ?? 0,
      threeBand?.LowMidHigh,
      rgb?.HeightRgb,
      [.. playhead.Grid.Select(beat => beat.TimeMs)],
      [.. playhead.Grid.Select(beat => beat.BeatInBar)]);

    lock(this.trackGate) {
      this.served = track;
    }

    return track;
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

    // Said once rather than refused: the tempo and the beat are right at any quantum, and
    // only the downbeat and the phrases, which count rekordbox's bars of four, are not.
    if(this.quantum != BeatgridPosition.BeatsPerBar) {
      this.log.LogWarning(
        "Beat:Quantum is {Quantum}, but rekordbox counts bars of {BeatsPerBar}; the downbeat and phrases it sets, and any bar shift of them, will not line up.",
        this.quantum,
        BeatgridPosition.BeatsPerBar);
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

  /// <summary>
  /// An id for the track whose analysis is at <paramref name="path"/>: a hash of the path,
  /// of when its three analysis files were last written and of <see cref="TrackFormat"/>,
  /// so the page can keep a track's data for good and still fetch it again once rekordbox
  /// has re-analysed it or Flux answers in a new shape.
  /// Hashed rather than the path itself, which names the user's folders and has no
  /// business in a URL.
  /// </summary>
  private string IdOf(string path) {
    double now = this.clock.NowMicros;
    lock(this.trackGate) {
      if(this.named is { } known && known.Path == path && now - known.AtMicros < TrackIdFor.TotalMicroseconds) {
        return known.Id;
      }
    }

    string stamp = $"{TrackFormat}|{path}|{WrittenTicks(path)}|{WrittenTicks(AnlzFile.ExtendedPath(path))}|{WrittenTicks(AnlzFile.ThreeBandPath(path))}";
    string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp)))[..16];
    lock(this.trackGate) {
      this.named = (path, id, now);
    }

    return id;

    static long WrittenTicks(string file) {
      try {
        return File.GetLastWriteTimeUtc(file).Ticks;
      } catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
        return 0;
      }
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

/// <summary>
/// The deck rekordbox is followed on, for the page to draw.
/// </summary>
/// <param name="Deck">The deck, counted from 1 as rekordbox shows it.</param>
/// <param name="TrackId">Names the track for <see cref="RekordboxSource.Track"/>; changes when the track does, or is analysed again.</param>
/// <param name="PositionMs">Where the playhead is, in milliseconds into the track at its normal speed.</param>
/// <param name="Rate">Track milliseconds per real millisecond: 1 at normal speed, more with the pitch up.</param>
/// <param name="Playing">Whether the playhead is moving.</param>
public sealed record RekordboxDeck(int Deck, string TrackId, double PositionMs, double Rate, bool Playing);

/// <summary>
/// What the page draws of a track: rekordbox's two detail waveforms and its beatgrid.
/// </summary>
/// <param name="TrackId">The id it was asked for by.</param>
/// <param name="EntriesPerSecond">Waveform columns per second of track, for both waveforms; 0 when there is neither.</param>
/// <param name="LowMidHigh">Three heights per column, low, mid and high, each 0 to 127; null when the track has no 3-band analysis.</param>
/// <param name="HeightRgb">Four bytes per column, height 0 to 31 then red, green, blue each 0 to 7; null when the track has no colour waveform.</param>
/// <param name="BeatTimesMs">Each beat of the grid, in milliseconds into the track.</param>
/// <param name="BeatsInBar">Each beat's place in its bar, 1 to 4, as rekordbox's grid says.</param>
public sealed record RekordboxTrack(string TrackId, int EntriesPerSecond, byte[]? LowMidHigh, byte[]? HeightRgb, IReadOnlyList<int> BeatTimesMs, IReadOnlyList<int> BeatsInBar);
