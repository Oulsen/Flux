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
///
/// <b>The phrases go out with the timeline.</b> They are read with the grid when the
/// track changes, laid out on each timeline as it is published, with the user's shift
/// on them as on the downbeat, and withdrawn with the tempo. A track whose phrases do not
/// fit its grid offers none rather than phrases in the wrong places.
/// </summary>
internal sealed class RekordboxFollower {
  /// <summary>
  /// How far past the grid a track's phrases may end and still fit it, in beats: one,
  /// since the end is the beat after the last phrase's final one. Measured across all
  /// 5,433 analysed tracks of the library on 2026-09-27: the phrases of every track
  /// either end at most one beat past the grid or run about twice its length (11 tracks,
  /// a grid set to half tempo after the phrases were found), with nothing in between.
  /// See <c>docs/rekordbox-anlz.md</c>.
  /// </summary>
  internal const int PhraseOverrunBeats = 1;

  /// <summary>
  /// How much of the grid a track's phrases have to cover to fit it. The same survey:
  /// every track's phrases cover 0.85 of its grid or more, except 21 that cover half of
  /// it or less, the opposite case of a grid set to double tempo. Two thirds sits in the
  /// empty middle, and is Flux's own choice of where in it.
  /// </summary>
  internal const double PhraseCoverage = 2.0 / 3.0;

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
  private readonly Func<string, AnlzPhraseList?> loadPhrases;
  private readonly object gate = new object();

  private int? master;
  private string? trackPath;
  private IReadOnlyList<AnlzBeat>? grid;

  /// <summary>The track's phrases, when it has some that fit its grid.</summary>
  private AnlzPhraseList? phrases;

  /// <summary>Why the track has no phrases to offer, when it has none; for the diagnostics.</summary>
  private string? phraseNote;

  /// <summary>Where the playhead was in the grid at the last fresh read, for naming the phrase playing.</summary>
  private double? lastBeat;

  /// <summary>What was last handed to the sink as the phrases, and the timeline and shift they were laid out on.</summary>
  private PhraseMap? offered;
  private BeatTimeline offeredOn;
  private int offeredShift;

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

  /// <param name="sink">Where the timelines, the downbeat and the phrases go.</param>
  /// <param name="loadGrid">Reads the beatgrid from a deck's <c>.DAT</c> path.</param>
  /// <param name="loadPhrases">Reads the phrases for a deck's <c>.DAT</c> path, which live in the file beside it.</param>
  internal RekordboxFollower(
    ITimelineSink sink,
    Func<string, IReadOnlyList<AnlzBeat>?> loadGrid,
    Func<string, AnlzPhraseList?> loadPhrases) {
    this.sink = sink ?? throw new ArgumentNullException(nameof(sink));
    this.loadGrid = loadGrid ?? throw new ArgumentNullException(nameof(loadGrid));
    this.loadPhrases = loadPhrases ?? throw new ArgumentNullException(nameof(loadPhrases));
  }

  /// <summary>Whether a timeline has ever been published, and when the one in force last was - null while none is.</summary>
  internal (bool Ever, double? LastMicros) Published() {
    lock(this.gate) {
      return (this.everPublished, this.held is null ? null : this.lastPublishedMicros);
    }
  }

  /// <summary>
  /// The master deck from 1, why nothing is being followed when nothing is, and the
  /// phrase playing - or why there is none - while a track is.
  /// </summary>
  internal (int? Deck, string? Detail, string? Phrase) Describe() {
    lock(this.gate) {
      return (this.master + 1, this.detail, this.DescribePhrase());
    }
  }

  /// <summary>The phrase <paramref name="phrase"/> as rekordbox labels it: "Chorus 2", or "Down" for one without a number.</summary>
  internal static string Label(AnlzPhrase phrase) {
    return phrase.Variant > 0 ? $"{phrase.Kind} {phrase.Variant}" : phrase.Kind.ToString();
  }

  /// <summary>
  /// Whether a track's phrases can be laid on its grid: they end no more than
  /// <see cref="PhraseOverrunBeats"/> past it, and cover at least
  /// <see cref="PhraseCoverage"/> of it.
  /// </summary>
  internal static bool Fits(AnlzPhraseList phrases, int gridBeats) {
    return phrases.EndBeat <= gridBeats + PhraseOverrunBeats
      && phrases.EndBeat >= gridBeats * PhraseCoverage;
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
      this.ForgetTrack();
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
      this.LoadPhrases(path);
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
    this.lastBeat = beat;
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
    this.OfferPhrases(timeline);
  }

  /// <summary>
  /// Lays the track's phrases on <paramref name="timeline"/> and hands them over, unless
  /// the ones last handed over were laid on the same timeline and shift - a heartbeat
  /// repeats the timeline, and the phrases on it have not moved either.
  /// </summary>
  private void OfferPhrases(BeatTimeline timeline) {
    if(this.phrases is not { } track) {
      this.WithdrawPhrases();
      return;
    }

    if(this.offered is not null && this.offeredOn == timeline && this.offeredShift == this.shift) {
      return;
    }

    // Start beats count from 1 and the grid from 0, so rekordbox's beat n is the
    // source's beat n - 1. The shift moves them with the downbeat, since a grid a beat
    // off has its phrases a beat off with it.
    var marks = new PhraseMark[track.Phrases.Count];
    for(int i = 0; i < marks.Length; i++) {
      AnlzPhrase phrase = track.Phrases[i];
      marks[i] = new PhraseMark(phrase.Kind, phrase.Variant, timeline.MicrosOfBeat(phrase.StartBeat - 1 + this.shift));
    }

    this.offered = new PhraseMap(marks, timeline.MicrosOfBeat(track.EndBeat - 1 + this.shift));
    this.offeredOn = timeline;
    this.offeredShift = this.shift;
    this.sink.PublishPhrases(this.offered);
  }

  private void WithdrawPhrases() {
    if(this.offered is not null) {
      this.offered = null;
      this.sink.PublishPhrases(null);
    }
  }

  /// <summary>Reads the phrases for the track at <paramref name="path"/>, keeping them only when they fit its grid.</summary>
  private void LoadPhrases(string path) {
    AnlzPhraseList? read = this.loadPhrases(path);
    if(read is null) {
      this.phrases = null;
      this.phraseNote = "none: rekordbox has no phrase analysis for this track";
    } else if(this.grid is not { Count: > 0 } beats || !Fits(read, beats.Count)) {
      this.phrases = null;
      this.phraseNote = "none: the phrases do not fit the beatgrid";
    } else {
      this.phrases = read;
      this.phraseNote = null;
    }

    // The ones on offer belong to the track before, and the next publish lays these.
    this.WithdrawPhrases();
  }

  /// <summary>The phrase at the last fresh read, the reason there is none, or null while no track is followed.</summary>
  private string? DescribePhrase() {
    if(this.trackPath is null || this.held is null) {
      return null;
    }

    if(this.phrases is not { } track) {
      return this.phraseNote;
    }

    if(this.lastBeat is not { } beat) {
      return null;
    }

    // In rekordbox's own count, from 1, with the shift taken back off.
    double at = beat + 1 - this.shift;
    if(at >= track.EndBeat) {
      return "none: past the last phrase";
    }

    AnlzPhrase? playing = null;
    foreach(AnlzPhrase phrase in track.Phrases) {
      if(phrase.StartBeat > at) {
        break;
      }

      playing = phrase;
    }

    return playing is { } found ? Label(found) : "none: before the first phrase";
  }

  /// <summary>The deck is readable but has nothing to follow: stop publishing, and remember it as the master.</summary>
  private void Forget(int deck, string why) {
    this.master = deck;
    this.ForgetTrack();
    this.Silence(why);
  }

  private void ForgetTrack() {
    this.trackPath = null;
    this.grid = null;
    this.phrases = null;
    this.phraseNote = null;
    this.lastBeat = null;
    this.lastPosition = null;
  }

  private void Silence(string why) {
    this.detail = why;
    this.alignPending = false;
    if(this.held is not null) {
      this.held = null;
      this.sink.Withdraw();
    }

    this.WithdrawPhrases();
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
