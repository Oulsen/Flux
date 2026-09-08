namespace Flux.Midi;

/// <summary>Whether a deck can be heard, as far as MIDI can tell.</summary>
internal enum DeckAudibility {
  /// <summary>
  /// Nothing has been heard from this deck's fader yet, so there is no answer.
  /// Distinguished from <see cref="Silent"/> deliberately: they are both refusals
  /// but only one of them is worth telling somebody about.
  /// </summary>
  Unknown,

  /// <summary>Its channel fader is down. Normally a deck being previewed in headphones.</summary>
  Silent,

  /// <summary>Its channel fader is open.</summary>
  Audible,
}

/// <summary>
/// What the four decks are currently doing, rebuilt from the messages that have
/// gone past.
///
/// It exists to answer one question - may this deck's cue press move the downbeat -
/// and it is a separate class from the observer so that question can be tested. The
/// answer is a policy with a real failure mode behind it, not plumbing: a DJ
/// previewing the incoming track presses cue repeatedly on a deck nobody can hear,
/// and treating those as downbeats would drag the light grid onto the wrong track's
/// phase during a mix.
///
/// State rather than events, throughout. Every field here is the last value seen for
/// a control that sends its complete position, so a dropped message costs accuracy
/// until the next one and never leaves this class believing something that has
/// stopped being true.
///
/// Not thread-safe; its caller holds a lock.
/// </summary>
internal sealed class FlxDeckState {
  private readonly int?[] fader = new int?[FlxConstants.DeckCount];
  private readonly bool?[] onDisplayedLayer = new bool?[FlxConstants.DeckCount];

  /// <summary>
  /// Folds one event into the picture. Events this class holds no state for -
  /// a cue press - are accepted and ignored, so a caller can hand it everything the
  /// parser produced without sorting them first.
  /// </summary>
  public void Apply(FlxEvent evt) {
    int index = evt.Deck - 1;
    if(index < 0 || index >= FlxConstants.DeckCount) {
      return;
    }

    switch(evt.Kind) {
      case FlxEventKind.FaderMoved:
        this.fader[index] = evt.Value;
        break;

      case FlxEventKind.DeckActive:
        this.onDisplayedLayer[index] = evt.IsActive;
        break;

      default:
        break;
    }
  }

  /// <summary>
  /// Whether <paramref name="deck"/> can be heard.
  ///
  /// The channel fader is the whole model, and the gap in it is worth stating: the
  /// crossfader's controller number was never identified, so a deck with its channel
  /// fader open but crossfaded away reads as <see cref="DeckAudibility.Audible"/>
  /// here. That is a false positive, which is the direction this is meant to avoid -
  /// it is accepted only because a four-channel controller like this one is
  /// commonly mixed on the channel faders with the crossfader parked, and because
  /// the honest fix is to measure the missing CC rather than to guess at it.
  /// </summary>
  public DeckAudibility Audibility(int deck) {
    int index = deck - 1;
    if(index < 0 || index >= FlxConstants.DeckCount) {
      return DeckAudibility.Unknown;
    }

    return this.fader[index] switch {
      null => DeckAudibility.Unknown,
      int position when position >= FlxConstants.AudibleFaderThreshold => DeckAudibility.Audible,
      _ => DeckAudibility.Silent,
    };
  }

  /// <summary>
  /// Whether <paramref name="deck"/> is the layer its section currently shows, or
  /// null before the controller has said.
  ///
  /// Diagnostics only, and it is worth being clear that it is not part of
  /// <see cref="Audibility"/>. It looks like it should be, and the measurement says
  /// otherwise: this is a true four-channel mixer, so all four decks have their own
  /// fader whichever layer is on display, and a deck on the hidden layer can be
  /// playing and audible while its buttons are unreachable. Gating on the layer
  /// would therefore reject cue presses from decks that are perfectly audible - and
  /// it could not gain anything either, because a hidden deck emits nothing at all,
  /// so a cue press proves the layer by arriving.
  /// </summary>
  public bool? IsOnDisplayedLayer(int deck) {
    int index = deck - 1;
    return index < 0 || index >= FlxConstants.DeckCount ? null : this.onDisplayedLayer[index];
  }
}
