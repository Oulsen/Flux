namespace Flux.Midi;

/// <summary>
/// What the DDJ-FLX10 actually puts on the wire, as measured rather than as
/// documented.
///
/// Pioneer publishes no MIDI map for this controller, so every number here comes
/// from watching the device: 4702 messages from 30 controls with rekordbox closed,
/// then a second capture with rekordbox playing and deck-select pressed on both
/// sides. That is why the values are gathered in one file with their evidence
/// attached - a wrong constant here does not fail, it silently watches the wrong
/// button, and the only way to tell is to know what was seen.
/// </summary>
internal static class FlxConstants {
  /// <summary>
  /// Deck 1's MIDI channel. Deck identity <em>is</em> the channel: pressing
  /// deck-select moves the same physical transport buttons from channel 1 to 3 on
  /// the left side and 2 to 4 on the right, with the note numbers unchanged. An
  /// action map keyed on the note alone would therefore treat deck 1 and deck 3 as
  /// the same deck.
  /// </summary>
  public const int FirstDeckChannel = 1;

  /// <summary>Deck 4's MIDI channel; the four decks occupy channels 1 to 4.</summary>
  public const int LastDeckChannel = 4;

  /// <summary>How many decks the controller drives, over two physical sections.</summary>
  public const int DeckCount = LastDeckChannel - FirstDeckChannel + 1;

  /// <summary>
  /// The note the cue button sends, on its deck's own channel.
  ///
  /// UNVERIFIED, and it is the one number in this file that has not been pinned
  /// down. Notes 11 and 12 are the transport pair - cue and play/pause - and which
  /// is which was not established during the capture, because the press order
  /// varied and both produce identical MIDI. Settling it needs a controller and one
  /// glance at rekordbox's play state: press this button and see whether the deck
  /// starts playing. If it does, this constant becomes 12 and nothing else in the
  /// project changes, which is the reason the other note is deliberately not mapped
  /// to anything.
  /// </summary>
  public const int CueNote = 11;

  /// <summary>
  /// The note that announces which layer a deck section is showing, as velocity 127
  /// on the incoming deck's channel and velocity 0 on the outgoing one's, both in
  /// the same millisecond as the deck-select press.
  ///
  /// Worth having as state rather than as an event, and this is what makes it
  /// trustworthy: the <em>last</em> value seen per channel is the current answer, so
  /// four booleans stay correct through a dropped message where a tally of
  /// button presses would drift and never recover.
  /// </summary>
  public const int DeckActiveNote = 60;

  /// <summary>
  /// The high seven bits of a deck's channel fader.
  ///
  /// Every analog control on this device is 14-bit, sent as the standard pair of
  /// CC <c>n</c> and CC <c>n + 32</c>. The capture showed identical message counts
  /// on both, which is what a 7-bit reader experiences as double the traffic and a
  /// value that jumps around.
  /// </summary>
  public const int ChannelFaderMsbController = 19;

  /// <summary>The low seven bits of a deck's channel fader.</summary>
  public const int ChannelFaderLsbController = ChannelFaderMsbController + 32;

  /// <summary>A 14-bit control's maximum, for the fader's full-open position.</summary>
  public const int FaderMax = (1 << 14) - 1;

  /// <summary>
  /// How far a channel fader has to be open before the deck behind it counts as
  /// audible, out of <see cref="FaderMax"/>.
  ///
  /// One eighth, and it is a margin rather than a measurement of what is hearable.
  /// The case this exists to separate is a DJ previewing the incoming track in
  /// headphones, and on this mixer that deck's fader is at the bottom - the capture
  /// recorded an exact 0 at rest, so any threshold at all divides the two states.
  /// The eighth is there for a fader that does not quite bottom out mechanically
  /// and for a finger resting on one.
  ///
  /// Erring high is deliberate and follows from the two ways this can be wrong. A
  /// cue press wrongly accepted yanks the light grid onto the phase of a track
  /// nobody can hear, in the middle of a running mix, which is exactly when the DJ
  /// has least attention to spare. A cue press wrongly ignored leaves the bar where
  /// it was, which is invisible.
  /// </summary>
  public const int AudibleFaderThreshold = FaderMax / 8;
}
