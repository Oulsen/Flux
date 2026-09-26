using Flux.Beat;

namespace Flux.Rekordbox;

/// <summary>
/// Turns reads of rekordbox into what a sync source says: a timeline for the master
/// deck, the downbeat from its beatgrid, and silence when there is nothing playing to
/// follow.
///
/// Kept apart from the process and the poll loop so the decisions can be tested on
/// readings laid out by the test, as the chain walker is; <see cref="RekordboxSource"/>
/// only feeds it reads and the time they were taken.
///
/// <b>What a read is worth</b> was measured at the desk on 2026-09-25, on rekordbox's own
/// audio device with no controller: the playhead moves one audio buffer at a time, about
/// 5 ms of track, delivered in pairs about 9 ms apart, so a read is up to 10 ms old; the
/// tempo read is the pitched one, following the fader on every read; and the playhead
/// counts the track's own time, so it maps straight onto the grid. A controller's ASIO
/// buffer can be larger, and the numbers below leave room for one of 20 ms or more.
///
/// A timeline is therefore anchored only on a read the playhead has just moved in, and
/// republished only when the one in force is out by more than a read can explain. In
/// between it is repeated unchanged, as Link repeats its own, so the grid sees a source
/// that is alive and a drift of exactly zero rather than a stream of 5 ms corrections.
///
/// <b>The downbeat is rekordbox's, always.</b> It is set when there is a new reason to
/// read it - a new track on the master, a different master, playback starting, a jump in
/// the track - and at no other time, with the user's whole-beat correction for this track
/// added on. A tap is not overruled between those events, but the next one sets the bar
/// from the grid again; the correction for a grid that is a beat off is
/// <see cref="Shift"/>, which lasts until the track changes.
///
/// <b>Without a master deck</b> - the byte that names it holds no deck, as the first
/// 7.2.19 chain's did with LINK on, or is unreadable when its chain has moved - the deck
/// playing is followed instead: the one already followed while it plays, else the one
/// playing longest, which is the one on air while the next is started.
///
/// <b>Nothing playing means nothing published.</b> A stopped master withdraws its tempo
/// rather than letting it run on, so while rekordbox is the source the lights settle at
/// their floor instead of pulsing to a track that is not playing.
/// </summary>
internal sealed class RekordboxFollower {
  /// <summary>
  /// How long the master's playhead may stand still before the deck counts as stopped.
  /// Flux's own number, reasoned rather than tuned: ten times the 20 ms or more a slow
  /// audio buffer can hold a playhead still while it plays, and short enough that
  /// pressing pause dims the room within a beat at any dance tempo.
  /// </summary>
  internal static readonly double StoppedAfterMicros = 250_000;

  /// <summary>
  /// How far the timeline in force may be from a fresh read before it is re-anchored.
  /// Flux's own number, reasoned: above the 10 ms old a read can be, so an honest
  /// timeline is not republished on every read, and below the 20 ms or so at which light
  /// against music starts to look early or late.
  /// </summary>
  internal static readonly double DriftToleranceMicros = 15_000;

  /// <summary>
  /// How far the tempo read may move from the one in force before it is published as a
  /// change, as a fraction: 0.05 per cent, 0.09 BPM at 175. Flux's own number, reasoned
  /// after watching a deck under LINK read 176.00, 176.02 and back from one read to the
  /// next, each published as a tempo change and re-anchored on a read up to 10 ms old -
  /// beats that wandered by milliseconds, and once by 124, which the room showed as
  /// restless flashing. A pitch fader moving at all passes this within a few reads, and
  /// what a smaller difference does to the phase the drift tolerance catches.
  /// </summary>
  internal static readonly double TempoToleranceFraction = 0.0005;

  /// <summary>
  /// How far out, in beats, a read has to be for the playhead to have been moved rather
  /// than to have drifted - a seek, a loop, a beat jump, a cue. Flux's own number: half a
  /// beat is the most a timeline can be wrong and still point at the same beat.
  /// </summary>
  internal static readonly double JumpBeats = 0.5;

  /// <summary>
  /// How often an unchanged timeline is repeated, so the source reads as alive to the
  /// grid and the page. Flux's own number, borrowed in spirit from Link, which repeats
  /// its timeline about four times a second with nothing new in it.
  /// </summary>
  internal static readonly double HeartbeatMicros = 250_000;

  /// <summary>Playhead units per millisecond of track: 44.1 kHz whatever the file's own rate, measured, see <c>docs/rekordbox-chains.md</c>.</summary>
  private const double SamplesPerMs = 44.1;

  private readonly ITimelineSink sink;
  private readonly Func<string, IReadOnlyList<AnlzBeat>?> loadGrid;
  private readonly object gate = new object();

  private int? master;
  private string? trackPath;
  private IReadOnlyList<AnlzBeat>? grid;

  /// <summary>Whole beats the user has moved this track's downbeat by. Reset with the track.</summary>
  private int shift;

  private long? lastPosition;
  private double lastMovedMicros;
  /// <summary>The timeline last published and not withdrawn: what the manager holds for this source.</summary>
  private BeatTimeline? held;

  /// <summary>The master or its track changed, so the next fresh read starts a new session whatever the timeline in force says.</summary>
  private bool rebase;

  private double lastPublishedMicros;
  private bool everPublished;
  private bool alignPending;

  /// <summary>How each deck's playhead has been moving, for choosing one when rekordbox names no master.</summary>
  private DeckMotion[] decks = [];
  private string? detail = "rekordbox has not been read yet";

  internal RekordboxFollower(ITimelineSink sink, Func<string, IReadOnlyList<AnlzBeat>?> loadGrid) {
    this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
    this.loadGrid = loadGrid ?? throw new ArgumentNullException(nameof(loadGrid));
  }

  /// <summary>Whether a timeline has ever been published, and when the one in force last was - null while none is.</summary>
  internal (bool Ever, double? LastMicros) Published() {
    lock(this.gate) {
      return (this.everPublished, this.held is null ? null : this.lastPublishedMicros);
    }
  }

  /// <summary>The master deck from 1, and why nothing is being followed when nothing is.</summary>
  internal (int? Deck, string? Detail) Describe() {
    lock(this.gate) {
      return (this.master + 1, this.detail);
    }
  }

  /// <summary>One read of rekordbox, taken at <paramref name="nowMicros"/>.</summary>
  internal void Follow(RekordboxReading reading, double nowMicros) {
    ArgumentNullException.ThrowIfNull(reading);
    lock(this.gate) {
      this.FollowWhileLocked(reading, nowMicros);
    }
  }

  /// <summary>rekordbox cannot be read at all; <paramref name="why"/> says what to do about it.</summary>
  internal void Lose(string why) {
    lock(this.gate) {
      this.master = null;
      this.trackPath = null;
      this.grid = null;
      this.lastPosition = null;
      this.Silence(why);
    }
  }

  /// <summary>
  /// The user moved the downbeat by <paramref name="beats"/>. Kept for the track on the
  /// master, so the next time the bar is read from its grid - after a jump, say - it is
  /// read with the correction rather than without it.
  /// </summary>
  internal void Shift(int beats) {
    lock(this.gate) {
      if(this.trackPath is not null) {
        this.shift += beats;
      }
    }
  }

  private void FollowWhileLocked(RekordboxReading reading, double now) {
    this.Watch(reading, now);

    // No master deck: the byte holds no deck, as the first 7.2.19 chain's did with LINK on,
    // or cannot be read at all when its chain has moved. Either way the deck playing stands
    // in for the master, and the message does not guess which cause it was. Silenced rather
    // than lost when nothing plays, so a paused track keeps its shift for when it plays again.
    if(reading.MasterDeck is not { } deck) {
      if(this.PlayingDeck(reading.Decks.Count, now) is not { } playing) {
        this.Silence("no deck is playing");
        return;
      }

      deck = playing;
    }

    DeckReading read = reading.Decks[deck];
    if(read.AnlzPath is not { Length: > 0 } path || read.Bpm is not > 0 || read.SamplePosition is not { } position) {
      this.Forget(deck, $"deck {deck + 1} has no track loaded");
      return;
    }

    if(deck != this.master || path != this.trackPath) {
      bool sameTrack = path == this.trackPath;
      this.master = deck;
      this.trackPath = path;
      this.grid = this.loadGrid(path);
      this.shift = sameTrack ? this.shift : 0;
      this.lastPosition = position;
      this.lastMovedMicros = now;

      // Whether it plays takes a second read to tell, and until then this is what is true.
      this.detail = $"waiting to see whether deck {deck + 1} is playing";

      // Not withdrawn: a switch to a deck already playing should hand over on its first
      // fresh read as a new session, which the grid carries across like any other,
      // rather than leave the room dark for a read in between. A deck that turns out
      // not to be playing is silenced by the stopped test like any other.
      this.rebase = true;
    }

    if(this.grid is not { Count: > 0 } beats) {
      this.Silence($"the track on deck {deck + 1} has no beatgrid; analyse it in rekordbox");
      return;
    }

    bool moved = position != this.lastPosition;
    this.lastPosition = position;
    if(moved) {
      this.lastMovedMicros = now;
    } else if(now - this.lastMovedMicros > StoppedAfterMicros) {
      this.Silence($"deck {deck + 1} is stopped");
      return;
    }

    if(!moved) {
      // Nothing new in this read. The timeline in force is still the best statement of
      // where the beat is; repeat it now and then so it does not read as gone.
      if(this.held is { } steady && now - this.lastPublishedMicros > HeartbeatMicros) {
        this.Publish(steady, now, newSession: false);
      }

      return;
    }

    double beat = BeatgridPosition.BeatAt(beats, position / SamplesPerMs);
    var observed = BeatTimeline.FromBpm(read.Bpm.Value, beat, now);
    if(!observed.IsPlausible) {
      this.Silence($"deck {deck + 1} reads a tempo of {read.Bpm.Value:F2} BPM, which cannot be right");
      return;
    }

    if(this.held is not { } current || this.rebase) {
      this.rebase = false;
      this.Publish(observed, now, newSession: true);
      this.alignPending = true;
    } else {
      double off = beat - current.BeatAt(now);
      if(Math.Abs(off) > JumpBeats) {
        this.Publish(observed, now, newSession: true);
        this.alignPending = true;
      } else if(Math.Abs(observed.MicrosPerBeat - current.MicrosPerBeat) > current.MicrosPerBeat * TempoToleranceFraction) {
        this.Publish(observed, now, newSession: false);
      } else if(Math.Abs(off) * current.MicrosPerBeat > DriftToleranceMicros) {
        // The tempo in force is still right, so the re-anchor keeps it: a drift is a
        // phase correction, and publishing the read's own hair-different tempo with it
        // would be announced as a tempo change it is not.
        this.Publish(new BeatTimeline(current.MicrosPerBeat, beat, (long)Math.Round(now)), now, newSession: false);
      } else if(now - this.lastPublishedMicros > HeartbeatMicros) {
        this.Publish(current, now, newSession: false);
      }
    }

    this.detail = null;
    if(this.alignPending) {
      long downbeat = BeatgridPosition.NearestDownbeat(beats, beat) + this.shift;
      this.alignPending = !this.sink.AlignBarAt(BarAlignmentMethod.Rekordbox, observed.MicrosOfBeat(downbeat));
    }
  }

  /// <summary>
  /// Notes which decks moved in this read, for when there is no master to follow. Every
  /// deck, every read, because which one is playing is only knowable from the one before.
  /// </summary>
  private void Watch(RekordboxReading reading, double now) {
    if(this.decks.Length != reading.Decks.Count) {
      this.decks = new DeckMotion[reading.Decks.Count];
    }

    for(int i = 0; i < this.decks.Length; i++) {
      long? position = reading.Decks[i].SamplePosition;
      ref DeckMotion motion = ref this.decks[i];
      if(position is { } at && motion.Position is { } before && at != before) {
        if(!motion.IsPlaying(now)) {
          motion.PlayingSince = now;
        }

        motion.MovedAt = now;
      }

      motion.Position = position;
    }
  }

  /// <summary>
  /// The deck to follow without a master: the one already followed while it plays, and
  /// otherwise the one that has played longest, which is the one on air while the next
  /// is started. Null when none is playing.
  /// </summary>
  private int? PlayingDeck(int count, double now) {
    if(this.master is { } current && current < count && this.decks[current].IsPlaying(now)) {
      return current;
    }

    int? longest = null;
    for(int i = 0; i < count; i++) {
      if(this.decks[i].IsPlaying(now) && (longest is not { } l || this.decks[i].PlayingSince < this.decks[l].PlayingSince)) {
        longest = i;
      }
    }

    return longest;
  }

  private void Publish(BeatTimeline timeline, double now, bool newSession) {
    this.everPublished = true;
    this.held = timeline;
    this.lastPublishedMicros = now;
    this.sink.Publish(timeline, newSession);
  }

  /// <summary>The deck is readable but has nothing to follow: stop publishing, and remember it as the master.</summary>
  private void Forget(int deck, string why) {
    this.master = deck;
    this.trackPath = null;
    this.grid = null;
    this.lastPosition = null;
    this.Silence(why);
  }

  private void Silence(string why) {
    this.detail = why;
    this.alignPending = false;
    if(this.held is not null) {
      this.held = null;
      this.sink.Withdraw();
    }
  }

  /// <summary>One deck's playhead over the last reads: where it was, and since when it has been moving.</summary>
  private struct DeckMotion {
    public DeckMotion() {
    }

    public long? Position { get; set; }

    public double MovedAt { get; set; } = double.NegativeInfinity;

    public double PlayingSince { get; set; }

    /// <summary>Moving recently enough to count as playing, by the same test the followed deck is held to.</summary>
    public readonly bool IsPlaying(double now) => now - this.MovedAt <= StoppedAfterMicros;
  }
}
