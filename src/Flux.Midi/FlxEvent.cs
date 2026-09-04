namespace Flux.Midi;

/// <summary>Which of the controller's messages this is.</summary>
internal enum FlxEventKind {
  /// <summary>The cue button went down on a deck.</summary>
  CuePressed,

  /// <summary>A deck section announced which of its two layers it now shows.</summary>
  DeckActive,

  /// <summary>A deck's channel fader moved.</summary>
  FaderMoved,
}

/// <summary>
/// One thing the controller said, in Flux's terms rather than MIDI's.
///
/// A value type with a kind rather than a record per message, because the
/// alternative allocates on a thread that is handling every jog-wheel tick the
/// device emits - and the parser's whole job is to throw nearly all of those away.
/// Build these through the factory methods below, which is where
/// <see cref="Value"/> gets its meaning.
/// </summary>
/// <param name="Kind">Which message this is.</param>
/// <param name="Deck">The deck it came from, 1 to 4.</param>
/// <param name="Value">
/// The payload, read according to <paramref name="Kind"/>: the 14-bit fader
/// position for <see cref="FlxEventKind.FaderMoved"/>, 1 or 0 for
/// <see cref="FlxEventKind.DeckActive"/>, and unused for
/// <see cref="FlxEventKind.CuePressed"/>.
/// </param>
internal readonly record struct FlxEvent(FlxEventKind Kind, int Deck, int Value) {
  /// <summary>The cue button went down on <paramref name="deck"/>.</summary>
  public static FlxEvent Cue(int deck) {
    return new FlxEvent(FlxEventKind.CuePressed, deck, Value: 0);
  }

  /// <summary><paramref name="deck"/> is, or is no longer, the layer its section shows.</summary>
  public static FlxEvent DeckActive(int deck, bool active) {
    return new FlxEvent(FlxEventKind.DeckActive, deck, active ? 1 : 0);
  }

  /// <summary><paramref name="deck"/>'s channel fader is at <paramref name="position"/>.</summary>
  public static FlxEvent FaderMoved(int deck, int position) {
    return new FlxEvent(FlxEventKind.FaderMoved, deck, position);
  }

  /// <summary>Whether a <see cref="FlxEventKind.DeckActive"/> event means active.</summary>
  public bool IsActive => this.Value != 0;
}
