using Flux.Beat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Rekordbox.Tests;

/// <summary>
/// The follower against a real manager and grid, so each test asserts what the lights
/// would see rather than what the follower meant to say.
///
/// Every desk fact is laid out as measured: a playhead that moves in 5 ms steps, in
/// 44.1 kHz units of the track's own time, and a tempo that is the pitched one.
/// </summary>
public class RekordboxFollowerTests {
  private const string TrackA = "C:/rb/USBANLZ/a/ANLZ0000.DAT";
  private const string TrackB = "C:/rb/USBANLZ/b/ANLZ0000.DAT";

  [Fact]
  public void PublishesThePlayingMastersTimelineWithTheBarFromItsGrid() {
    // A track that opens on beat 3, which a count from its first beat would get wrong.
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 3);

    rig.Play(deck: 0, fromMs: 1000, forMs: 50);

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(120, snapshot.Bpm, precision: 3);
    Assert.Equal(BarAlignmentMethod.Rekordbox, snapshot.AlignmentMethod);
    Assert.Equal(1, rig.Grid.PositionOf(2).PositionInBar);
    Assert.Equal(1, rig.Grid.PositionOf(6).PositionInBar);
    Assert.Null(rig.Follower.Describe().Detail);
  }

  [Fact]
  public void SetsTheDownbeatWhileAnotherSourceKeepsTheTempo() {
    using var rig = new Rig(active: "link", firstBeatInBar: 1);

    // Link at 120 and numbered from somewhere else entirely, its beats 8 ms after the
    // track's - the few milliseconds Link and a deck really are apart. rekordbox's own
    // reading of the tempo is a hair off, and must not reach the grid while Link is selected.
    rig.Clock.Set(1_000_000);
    rig.Link.Publish(BeatTimeline.FromBpm(120, beat: 1001, atMicros: rig.Clock.NowMicros + 8_000));
    rig.Play(deck: 0, fromMs: 500, forMs: 50, bpm: 120.4f);

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(120, snapshot.Bpm, precision: 3);
    Assert.Equal(BarAlignmentMethod.Rekordbox, snapshot.AlignmentMethod);

    // Track beat 0, a downbeat, played at 0.5 s on the clock: Link's beat 1000, 8 ms on.
    Assert.Equal(1, rig.Grid.PositionOf(1000).PositionInBar);
    Assert.Equal(1, rig.Grid.PositionOf(1004).PositionInBar);
    Assert.Equal(2, rig.Grid.PositionOf(1001).PositionInBar);
  }

  [Fact]
  public void RepeatsASteadyTimelineRatherThanCorrectingItOnEveryRead() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);

    rig.Play(deck: 0, fromMs: 0, forMs: 1000);

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(0, snapshot.MaxReanchorJumpBeats);
    Assert.InRange(snapshot.Packets, 4, 6);
  }

  [Fact]
  public void ReanchorsWhenTheTimelineInForceDriftsPastTheTolerance() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);
    int tempoChanges = rig.TempoChanges;

    // 20 ms ahead of where the timeline says: more than a read can explain, less than a jump.
    rig.Play(deck: 0, fromMs: 120, forMs: 10);

    Assert.Equal(tempoChanges, rig.TempoChanges);
    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.InRange(snapshot.MaxReanchorJumpBeats, 0.03, 0.05);
  }

  [Fact]
  public void StartsANewSessionAndReadsTheBarAgainAfterASeek() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 2);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);
    int tempoChanges = rig.TempoChanges;

    rig.Play(deck: 0, fromMs: 7_250, forMs: 50);

    Assert.Equal(tempoChanges + 1, rig.TempoChanges);
    double beat = rig.Grid.Timeline!.Value.BeatAt(rig.Clock.NowMicros);
    Assert.Equal(14.6, beat, precision: 1);
    Assert.Equal(1, rig.Grid.PositionOf(15).PositionInBar);
  }

  [Fact]
  public void WithdrawsWhenTheMasterStopsAndComesBackWhenItPlays() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);

    rig.Hold(deck: 0, atMs: 100, forMs: 300);

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Equal("deck 1 is stopped", rig.Follower.Describe().Detail);

    rig.Play(deck: 0, fromMs: 100, forMs: 50);

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(BarAlignmentMethod.Rekordbox, snapshot.AlignmentMethod);
  }

  [Fact]
  public void FollowsThePitchedTempoWithoutRenumberingTheBeat() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);
    double before = rig.Grid.Timeline!.Value.BeatAt(rig.Clock.NowMicros);

    rig.Play(deck: 0, fromMs: 100, forMs: 20, bpm: 124.8f);

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(124.8, snapshot.Bpm, precision: 1);
    Assert.Equal(before, rig.Grid.Timeline!.Value.BeatAt(rig.Clock.NowMicros - 20_000), precision: 1);
  }

  /// <summary>
  /// A deck under LINK reads its tempo a hair either side of the session's from one read
  /// to the next. Each of those published as a change re-anchored the beat on a stale
  /// read, and the room showed the wander as restless flashing.
  /// </summary>
  [Fact]
  public void DoesNotPublishATempoThatOnlyWavers() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);
    int tempoChanges = rig.TempoChanges;

    for(int i = 0; i < 40; i++) {
      rig.Play(deck: 0, fromMs: 105 + (i * 10), forMs: 5, bpm: i % 2 == 0 ? 120.02f : 119.98f);
    }

    Assert.Equal(tempoChanges, rig.TempoChanges);
    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(120, snapshot.Bpm, precision: 3);
  }

  [Fact]
  public void SaysSoWhenTheTrackHasNoBeatgrid() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1, withGrid: false);

    rig.Play(deck: 0, fromMs: 0, forMs: 50);

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Contains("no beatgrid", rig.Follower.Describe().Detail);
  }

  [Fact]
  public void KeepsTheUsersShiftWhenTheBarIsReadAgainAfterASeek() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);

    Assert.True(rig.Manager.ShiftBar(1));
    Assert.Equal(1, rig.Grid.PositionOf(1).PositionInBar);

    rig.Play(deck: 0, fromMs: 4_000, forMs: 50);

    Assert.Equal(1, rig.Grid.PositionOf(9).PositionInBar);
  }

  [Fact]
  public void ForgetsTheShiftWithTheTrack() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);
    Assert.True(rig.Manager.ShiftBar(1));

    rig.Play(deck: 0, fromMs: 4_000, forMs: 50, path: TrackB);

    Assert.Equal(1, rig.Grid.PositionOf(8).PositionInBar);
  }

  [Fact]
  public void HandsOverToANewMasterWithoutGoingDarkInBetween() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);

    rig.Read(deck: 1, positionMs: 2_000);
    Assert.True(rig.Grid.TryGetSnapshot(out _));

    rig.Play(deck: 1, fromMs: 2_005, forMs: 50);
    Assert.Equal(2, rig.Follower.Describe().Deck);
    Assert.Equal(4.1, rig.Grid.Timeline!.Value.BeatAt(rig.Clock.NowMicros), precision: 1);
  }

  /// <summary>
  /// A new master whose playhead happens to sit where the old timeline predicted is not
  /// a jump, and without being told the track changed the follower would keep the old
  /// track's bar. Its grid opens on beat 3, so its downbeats are two beats from the old ones.
  /// </summary>
  [Fact]
  public void ReadsTheBarAgainForANewMasterEvenWhenItsPlayheadLinesUp() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1, trackBFirstBeatInBar: 3);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);
    double predictedMs = rig.Grid.Timeline!.Value.BeatAt(rig.Clock.NowMicros) * 500;

    rig.Read(deck: 1, positionMs: predictedMs, path: TrackB);
    rig.Clock.Advance(5_000);
    rig.Play(deck: 1, fromMs: predictedMs + 5, forMs: 20, path: TrackB);

    // Track B's beat 0 is on 3 of its bar and beat 2 is its downbeat. Kept from track A,
    // the bar would still start on beat 0.
    Assert.Equal(3, rig.Grid.PositionOf(0).PositionInBar);
    Assert.Equal(1, rig.Grid.PositionOf(2).PositionInBar);
  }

  /// <summary>
  /// The master byte names no deck, as the first 7.2.19 chain's did with LINK on. The deck
  /// that is playing is then the only music there is, and it is followed as the master.
  /// </summary>
  [Fact]
  public void FollowsThePlayingDeckWhenLinkIsTheMaster() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);

    rig.Steps(forMs: 50, deck1FromMs: null, deck2FromMs: 1_000);

    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot snapshot));
    Assert.Equal(BarAlignmentMethod.Rekordbox, snapshot.AlignmentMethod);
    Assert.Equal(2, rig.Follower.Describe().Deck);
    Assert.Null(rig.Follower.Describe().Detail);
  }

  [Fact]
  public void StaysOnTheFollowedDeckWhenASecondStartsWithoutAMaster() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Steps(forMs: 100, deck1FromMs: 0, deck2FromMs: null);

    // The next track started under it: the one on air stays followed.
    rig.Steps(forMs: 100, deck1FromMs: 105, deck2FromMs: 3_000);
    Assert.Equal(1, rig.Follower.Describe().Deck);

    // The first stops; the second is the music now.
    rig.Steps(forMs: 400, deck1FromMs: null, deck2FromMs: 3_105, deck1HeldAtMs: 210);
    Assert.Equal(2, rig.Follower.Describe().Deck);
    Assert.True(rig.Grid.TryGetSnapshot(out _));
  }

  [Fact]
  public void SaysWhyWhenThereIsNoMasterAndNothingPlays() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);

    rig.Steps(forMs: 50, deck1FromMs: null, deck2FromMs: null, deck1HeldAtMs: 0, deck2HeldAtMs: 0);

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Equal("no deck is playing", rig.Follower.Describe().Detail);
  }

  [Fact]
  public void KeepsTheShiftThroughAPauseWithoutAMaster() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Steps(forMs: 100, deck1FromMs: 0, deck2FromMs: null);
    Assert.True(rig.Manager.ShiftBar(1));

    rig.Steps(forMs: 400, deck1FromMs: null, deck2FromMs: null, deck1HeldAtMs: 105);
    Assert.False(rig.Grid.TryGetSnapshot(out _));

    rig.Steps(forMs: 50, deck1FromMs: 4_000, deck2FromMs: null);
    Assert.Equal(1, rig.Grid.PositionOf(9).PositionInBar);
  }

  [Fact]
  public void OffersThePhrasesAsMomentsOnTheTimelineItPublishes() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1, phrases: Song(endBeat: 400));

    rig.Play(deck: 0, fromMs: 1000, forMs: 50);

    PhraseMap map = Assert.IsType<PhraseMap>(rig.Grid.Phrases);
    Assert.Equal([PhraseKind.Intro, PhraseKind.Verse, PhraseKind.Chorus], map.Phrases.Select(phrase => phrase.Kind));

    // rekordbox's beat 17 is the grid's beat 16, which is where the grid itself puts it.
    Assert.True(rig.Grid.TryGetSnapshot(out BeatGridSnapshot now));
    double expected = rig.Clock.NowMicros + ((16 - now.Beat) * 500_000);
    Assert.Equal(expected, map.Phrases[1].StartMicros, tolerance: 10);
    Assert.Equal(399 * 500_000, map.EndMicros - map.Phrases[0].StartMicros, precision: 0);

    // At 1 s the playhead is on the grid's beat 2, rekordbox's 3: the intro.
    Assert.Equal("Intro 1", rig.Follower.Describe().Phrase);
  }

  [Fact]
  public void OffersThePhrasesWhileAnotherSourceKeepsTheTempo() {
    using var rig = new Rig(active: "link", firstBeatInBar: 1, phrases: Song(endBeat: 400));

    rig.Play(deck: 0, fromMs: 9000, forMs: 50);

    Assert.NotNull(rig.Grid.Phrases);
    Assert.Equal("Verse 1", rig.Follower.Describe().Phrase);
  }

  [Theory]
  [InlineData(800)]
  [InlineData(200)]
  public void OffersNoPhrasesThatDoNotFitTheGrid(int endBeat) {
    // The grid has 400 beats: phrases running twice its length, or half of it, are laid
    // out on a grid at a different tempo from this one.
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1, phrases: Song(endBeat));

    rig.Play(deck: 0, fromMs: 1000, forMs: 50);

    Assert.Null(rig.Grid.Phrases);
    Assert.Equal("none: the phrases do not fit the beatgrid", rig.Follower.Describe().Phrase);
  }

  [Fact]
  public void SaysSoWhenTheTrackHasNoPhraseAnalysis() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);

    rig.Play(deck: 0, fromMs: 1000, forMs: 50);

    Assert.Null(rig.Grid.Phrases);
    Assert.Equal("none: rekordbox has no phrase analysis for this track", rig.Follower.Describe().Phrase);
  }

  [Fact]
  public void MovesThePhrasesWithTheUsersShift() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1, phrases: Song(endBeat: 400));
    rig.Play(deck: 0, fromMs: 1000, forMs: 50);
    double before = rig.Grid.Phrases!.Phrases[1].StartMicros;

    Assert.True(rig.Manager.ShiftBar(1));
    rig.Play(deck: 0, fromMs: 1055, forMs: 300);

    Assert.Equal(before + 500_000, rig.Grid.Phrases!.Phrases[1].StartMicros, tolerance: 10);
  }

  [Fact]
  public void WithdrawsThePhrasesWhenTheDeckStops() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1, phrases: Song(endBeat: 400));
    rig.Play(deck: 0, fromMs: 1000, forMs: 50);

    rig.Hold(deck: 0, atMs: 1050, forMs: 400);

    Assert.Null(rig.Grid.Phrases);
    Assert.Null(rig.Follower.Describe().Phrase);
  }

  [Theory]
  [InlineData(401, true)]
  [InlineData(402, false)]
  [InlineData(267, true)]
  [InlineData(266, false)]
  public void PhrasesFitAGridTheyEndWithinABeatOfAndCoverTwoThirdsOf(int endBeat, bool fits) {
    Assert.Equal(fits, RekordboxFollower.Fits(Song(endBeat), gridBeats: 400));
  }

  [Fact]
  public void WithdrawsAndSaysWhyWhenRekordboxIsLost() {
    using var rig = new Rig(active: "rekordbox", firstBeatInBar: 1);
    rig.Play(deck: 0, fromMs: 0, forMs: 100);

    rig.Follower.Lose("rekordbox closed");

    Assert.False(rig.Grid.TryGetSnapshot(out _));
    Assert.Equal("rekordbox closed", rig.Follower.Describe().Detail);
    Assert.True(rig.Follower.Published().Ever);
  }

  /// <summary>A manager with a Link stand-in and the follower registered, a clock, and a 120 BPM grid per track.</summary>
  private sealed class Rig: IDisposable {
    private readonly int firstBeatInBar;
    private readonly int trackBFirstBeatInBar;
    private readonly bool withGrid;
    private readonly AnlzPhraseList? phrases;

    internal Rig(string active, int firstBeatInBar, bool withGrid = true, int? trackBFirstBeatInBar = null, AnlzPhraseList? phrases = null) {
      this.firstBeatInBar = firstBeatInBar;
      this.phrases = phrases;
      this.trackBFirstBeatInBar = trackBFirstBeatInBar ?? firstBeatInBar;
      this.withGrid = withGrid;
      this.Grid = new BeatGrid(this.Clock, quantum: 4, TimeSpan.FromSeconds(2.5));
      this.Grid.TempoChanged += _ => this.TempoChanges++;
      this.Manager = new SyncSourceManager(
        this.Grid,
        Options.Create(new BeatOptions { DefaultSource = active }),
        settings: null,
        NullLogger<SyncSourceManager>.Instance);
      this.Link = this.Manager.Register(new NamedSource("link"));
      this.Follower = new RekordboxFollower(this.Manager.Register(new NamedSource("rekordbox")), this.Load, _ => this.phrases);
      this.Manager.BarShifted += this.Follower.Shift;
    }

    internal FakeClock Clock { get; } = new FakeClock();

    internal BeatGrid Grid { get; }

    internal SyncSourceManager Manager { get; }

    internal ITimelineSink Link { get; }

    internal RekordboxFollower Follower { get; }

    internal int TempoChanges { get; private set; }

    /// <summary>The deck playing: a read every 5 ms, the playhead moving with the clock.</summary>
    internal void Play(int deck, double fromMs, double forMs, float bpm = 120f, string path = TrackA) {
      for(double ms = 0; ms <= forMs; ms += 5) {
        this.Read(deck, fromMs + ms, bpm, path);
        this.Clock.Advance(5_000);
      }
    }

    /// <summary>The deck standing still at one place.</summary>
    internal void Hold(int deck, double atMs, double forMs) {
      for(double ms = 0; ms <= forMs; ms += 5) {
        this.Read(deck, atMs);
        this.Clock.Advance(5_000);
      }
    }

    /// <summary>
    /// Both decks at once with no master named: a deck
    /// given a start plays from there, one given a hold stands still, and one given
    /// neither has no track.
    /// </summary>
    internal void Steps(double forMs, double? deck1FromMs, double? deck2FromMs, double? deck1HeldAtMs = null, double? deck2HeldAtMs = null) {
      for(double ms = 0; ms <= forMs; ms += 5) {
        var decks = new DeckReading[2];
        decks[0] = Deck(deck1FromMs is { } a ? a + ms : deck1HeldAtMs, TrackA);
        decks[1] = Deck(deck2FromMs is { } b ? b + ms : deck2HeldAtMs, TrackB);
        this.Follower.Follow(new RekordboxReading(decks, MasterDeck: null), this.Clock.NowMicros);
        this.Clock.Advance(5_000);
      }

      static DeckReading Deck(double? atMs, string path) =>
        atMs is { } at ? new DeckReading(120f, (long)Math.Round(at * 44.1), path) : new DeckReading(0, 0, null);
    }

    internal void Read(int deck, double positionMs, float bpm = 120f, string path = TrackA) {
      var decks = new DeckReading[2];
      decks[deck] = new DeckReading(bpm, (long)Math.Round(positionMs * 44.1), path);
      decks[1 - deck] = new DeckReading(0, 0, null);
      this.Follower.Follow(new RekordboxReading(decks, deck), this.Clock.NowMicros);
    }

    public void Dispose() => this.Manager.BarShifted -= this.Follower.Shift;

    /// <summary>A long steady 120 BPM grid from 0 ms: beat n at n * 500 ms, the first on the track's own first beat in bar.</summary>
    private IReadOnlyList<AnlzBeat>? Load(string path) {
      int first = path == TrackB ? this.trackBFirstBeatInBar : this.firstBeatInBar;
      return this.withGrid
        ? [.. Enumerable.Range(0, 400).Select(n => new AnlzBeat(((first - 1 + n) % 4) + 1, 120f, n * 500))]
        : null;
    }
  }

  /// <summary>An intro, a verse and a chorus of 16 beats each, from rekordbox's beat 1, ending where it is told.</summary>
  private static AnlzPhraseList Song(int endBeat) {
    return new AnlzPhraseList(PhraseMood.High, endBeat, [
      new AnlzPhrase(1, PhraseKind.Intro, 1, FillBeat: null),
      new AnlzPhrase(17, PhraseKind.Verse, 1, FillBeat: null),
      new AnlzPhrase(33, PhraseKind.Chorus, 1, FillBeat: null),
    ]);
  }

  private sealed class NamedSource(string name): ISyncSource {
    public string Name => name;

    public SyncSourceStatus Status() => new(SyncSourceState.Waiting, Confidence: 0);
  }

  internal sealed class FakeClock: IMonotonicClock {
    public double NowMicros { get; private set; } = 10_000_000;

    public void Advance(double micros) => this.NowMicros += micros;

    public void Set(double micros) => this.NowMicros = micros;
  }
}
