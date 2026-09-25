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
    if(reading.MasterDeck is not { } deck) {
      this.Lose("rekordbox's master deck could not be read");
      return;
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
      } else if(observed.MicrosPerBeat != current.MicrosPerBeat || Math.Abs(off) * current.MicrosPerBeat > DriftToleranceMicros) {
        this.Publish(observed, now, newSession: false);
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
}
