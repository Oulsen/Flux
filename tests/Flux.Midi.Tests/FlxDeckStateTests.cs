using Xunit;

namespace Flux.Midi.Tests;

/// <summary>
/// The audibility model, which is the only policy this project has. It decides
/// whether a cue press is allowed to move the downbeat, and the failure it exists to
/// prevent is specific: a DJ previewing the incoming track in headphones presses cue
/// repeatedly on a deck nobody can hear, and taking those as downbeats would drag
/// the light grid onto the wrong track's phase in the middle of a mix.
/// </summary>
public class FlxDeckStateTests {
  [Fact]
  public void KnowsNothingBeforeAFaderHasMoved() {
    FlxDeckState decks = new FlxDeckState();

    // Distinct from Silent on purpose. Both refuse the cue press, but this one is
    // worth telling somebody about: the fader may well be up, Flux has simply never
    // seen it and will not assume.
    Assert.Equal(DeckAudibility.Unknown, decks.Audibility(1));
  }

  [Fact]
  public void CallsAClosedFaderSilent() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(1, 0));

    Assert.Equal(DeckAudibility.Silent, decks.Audibility(1));
  }

  [Fact]
  public void CallsAnOpenFaderAudible() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(1, FlxConstants.FaderMax));

    Assert.Equal(DeckAudibility.Audible, decks.Audibility(1));
  }

  [Fact]
  public void PutsTheThresholdWhereTheConstantSaysItIs() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(1, FlxConstants.AudibleFaderThreshold - 1));
    Assert.Equal(DeckAudibility.Silent, decks.Audibility(1));

    decks.Apply(FlxEvent.FaderMoved(1, FlxConstants.AudibleFaderThreshold));
    Assert.Equal(DeckAudibility.Audible, decks.Audibility(1));
  }

  [Fact]
  public void FollowsAFaderBackDown() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(4, FlxConstants.FaderMax));
    decks.Apply(FlxEvent.FaderMoved(4, 0));

    // The last value wins, always. Everything this class holds is a complete
    // position rather than a delta, so a dropped message costs accuracy until the
    // next one and never leaves a belief behind that has stopped being true.
    Assert.Equal(DeckAudibility.Silent, decks.Audibility(4));
  }

  [Fact]
  public void JudgesEachDeckOnItsOwnFader() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(1, FlxConstants.FaderMax));
    decks.Apply(FlxEvent.FaderMoved(2, 0));

    Assert.Equal(DeckAudibility.Audible, decks.Audibility(1));
    Assert.Equal(DeckAudibility.Silent, decks.Audibility(2));

    // Untouched, and therefore still unanswered rather than inheriting either.
    Assert.Equal(DeckAudibility.Unknown, decks.Audibility(3));
    Assert.Equal(DeckAudibility.Unknown, decks.Audibility(4));
  }

  [Fact]
  public void TracksTheDisplayedLayerWithoutLettingItDecideAudibility() {
    FlxDeckState decks = new FlxDeckState();

    Assert.Null(decks.IsOnDisplayedLayer(3));

    decks.Apply(FlxEvent.DeckActive(1, active: false));
    decks.Apply(FlxEvent.DeckActive(3, active: true));

    Assert.False(decks.IsOnDisplayedLayer(1));
    Assert.True(decks.IsOnDisplayedLayer(3));

    // The decision this pins is that the layer is diagnostics and nothing more. It
    // looks like it belongs in the audibility test and the measurement says
    // otherwise: this is a true four-channel mixer, so a deck on the hidden layer
    // has its own fader and can be perfectly audible. Gating on the layer would
    // reject cue presses from decks anyone in the room can hear.
    decks.Apply(FlxEvent.FaderMoved(1, FlxConstants.FaderMax));

    Assert.Equal(DeckAudibility.Audible, decks.Audibility(1));
  }

  [Fact]
  public void IgnoresEventsItHoldsNoStateFor() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(2, FlxConstants.FaderMax));
    decks.Apply(FlxEvent.Cue(2));

    // A caller can hand this everything the parser produced without sorting it
    // first, and a cue press is not evidence about a fader.
    Assert.Equal(DeckAudibility.Audible, decks.Audibility(2));
  }

  [Fact]
  public void RefusesToAnswerForADeckThatDoesNotExist() {
    FlxDeckState decks = new FlxDeckState();

    decks.Apply(FlxEvent.FaderMoved(9, FlxConstants.FaderMax));

    Assert.Equal(DeckAudibility.Unknown, decks.Audibility(9));
    Assert.Equal(DeckAudibility.Unknown, decks.Audibility(0));
    Assert.Null(decks.IsOnDisplayedLayer(9));
  }
}
