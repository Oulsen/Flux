using Xunit;

namespace Flux.Midi.Tests;

/// <summary>
/// The parser against the messages the FLX-10 was measured emitting. Every case
/// here is a shape from that capture rather than an invented one, which is the only
/// thing that makes a test of an undocumented MIDI map worth anything.
/// </summary>
public class FlxMessageParserTests {
  [Fact]
  public void ReadsACuePressOnEveryDeckChannel() {
    FlxMessageParser parser = new FlxMessageParser();

    // Deck identity is the channel: pressing deck-select moves the same physical
    // button from channel 1 to 3 and 2 to 4 with the note unchanged, so a parser
    // that read the note alone would report deck 1 for a press on deck 3.
    for(int deck = 1; deck <= 4; deck++) {
      FlxEvent? observed = parser.Parse(NoteOn(deck, FlxConstants.CueNote, velocity: 127));

      Assert.Equal(FlxEvent.Cue(deck), observed);
    }
  }

  [Fact]
  public void ExactlyOneOfTheTransportPairIsTheCueButton() {
    FlxMessageParser parser = new FlxMessageParser();

    // Notes 11 and 12 are cue and play/pause, and which is which is not settled -
    // it needs a controller and one glance at rekordbox's play state. What this
    // pins is the property that makes settling it cheap: one of the two is mapped,
    // the other is mapped to nothing at all, so the answer is a one-constant
    // change. Asserting on the literal 11 would be pinning the guess instead.
    FlxEvent? eleven = parser.Parse(NoteOn(deck: 1, note: 11, velocity: 127));
    FlxEvent? twelve = parser.Parse(NoteOn(deck: 1, note: 12, velocity: 127));

    Assert.True(eleven.HasValue ^ twelve.HasValue);
    Assert.Equal(FlxEvent.Cue(1), eleven ?? twelve);
  }

  [Fact]
  public void IgnoresTheCueRelease() {
    FlxMessageParser parser = new FlxMessageParser();

    // One press must place one downbeat. Anchoring on the release too would place a
    // second one wherever the finger happened to leave.
    Assert.Null(parser.Parse(NoteOn(deck: 1, FlxConstants.CueNote, velocity: 0)));
  }

  [Fact]
  public void IgnoresChannelsThatDoNotIdentifyADeck() {
    FlxMessageParser parser = new FlxMessageParser();

    // The pads sit on channel 10 with notes 0 to 7, and the loop and jog controls on
    // channels 2, 4 and 7. Channel 10 carrying the cue note is the case that matters:
    // nothing outside channels 1 to 4 says which deck it is about.
    Assert.Null(parser.Parse(NoteOn(deck: 10, FlxConstants.CueNote, velocity: 127)));
    Assert.Null(parser.Parse(NoteOn(deck: 5, FlxConstants.DeckActiveNote, velocity: 127)));
    Assert.Null(parser.Parse(ControlChange(deck: 7, FlxConstants.ChannelFaderMsbController, value: 64)));
  }

  [Fact]
  public void IgnoresControlsThisProjectDoesNotWatch() {
    FlxMessageParser parser = new FlxMessageParser();

    // The jog wheel is CC 31 and 63 and produces the bulk of the traffic. It has to
    // cost a comparison and nothing else.
    Assert.Null(parser.Parse(ControlChange(deck: 1, controller: 31, value: 65)));
    Assert.Null(parser.Parse(NoteOn(deck: 1, note: 114, velocity: 127)));
  }

  [Fact]
  public void PairsTheFourteenBitFader() {
    FlxMessageParser parser = new FlxMessageParser();

    parser.Parse(ControlChange(deck: 2, FlxConstants.ChannelFaderMsbController, value: 100));
    FlxEvent? observed = parser.Parse(ControlChange(deck: 2, FlxConstants.ChannelFaderLsbController, value: 5));

    // The standard convention: CC n carries the high seven bits, CC n + 32 the low
    // seven. A 7-bit reader sees these as two unrelated controls, which is what makes
    // a fader appear to jump around.
    Assert.Equal(FlxEvent.FaderMoved(2, (100 << 7) | 5), observed);
  }

  [Fact]
  public void ReportsACoarsePositionFromTheHighHalfAlone() {
    FlxMessageParser parser = new FlxMessageParser();

    FlxEvent? observed = parser.Parse(ControlChange(deck: 1, FlxConstants.ChannelFaderMsbController, value: 127));

    // Both halves emit, rather than the coarse one waiting for its partner. This
    // device always sends the pair, so waiting would work here - but a control that
    // only ever sent its high half would then produce no fader events at all, and a
    // fader that has never been seen silently disables the cue press.
    Assert.Equal(FlxEvent.FaderMoved(1, 127 << 7), observed);
  }

  [Fact]
  public void ReadsALowHalfWithNoHighHalfAsNearlyClosed() {
    FlxMessageParser parser = new FlxMessageParser();

    FlxEvent? observed = parser.Parse(ControlChange(deck: 1, FlxConstants.ChannelFaderLsbController, value: 127));

    // Joining the stream between the two halves of one move is the only way to see
    // this, and the answer errs the safe way: 127 of 16383 reads as a closed fader,
    // so an unlucky start cannot make a silent deck look audible.
    Assert.Equal(FlxEvent.FaderMoved(1, 127), observed);
    Assert.True(127 < FlxConstants.AudibleFaderThreshold);
  }

  [Fact]
  public void KeepsEachDecksFaderSeparate() {
    FlxMessageParser parser = new FlxMessageParser();

    parser.Parse(ControlChange(deck: 1, FlxConstants.ChannelFaderMsbController, value: 127));
    parser.Parse(ControlChange(deck: 3, FlxConstants.ChannelFaderMsbController, value: 0));

    FlxEvent? first = parser.Parse(ControlChange(deck: 1, FlxConstants.ChannelFaderLsbController, value: 127));
    FlxEvent? third = parser.Parse(ControlChange(deck: 3, FlxConstants.ChannelFaderLsbController, value: 0));

    // Four independent faders interleave freely during a mix. One shared latch would
    // hand deck 3's coarse position to deck 1.
    Assert.Equal(FlxEvent.FaderMoved(1, FlxConstants.FaderMax), first);
    Assert.Equal(FlxEvent.FaderMoved(3, 0), third);
  }

  [Fact]
  public void ReadsZeroVelocityOnTheLayerNoteAsDeckInactive() {
    FlxMessageParser parser = new FlxMessageParser();

    // The load-bearing one. A note-on at velocity 0 conventionally means a release,
    // and this device uses exactly that message to say a deck stopped being the
    // displayed one - each deck-select press sends note 60 at velocity 0 on the
    // outgoing channel and 127 on the incoming one, in the same millisecond.
    // Applying the convention here would drop half of the only message carrying
    // layer state, and the remaining half never says a deck went away.
    Assert.Equal(
      FlxEvent.DeckActive(1, active: false),
      parser.Parse(NoteOn(deck: 1, FlxConstants.DeckActiveNote, velocity: 0)));

    Assert.Equal(
      FlxEvent.DeckActive(3, active: true),
      parser.Parse(NoteOn(deck: 3, FlxConstants.DeckActiveNote, velocity: 127)));
  }

  [Fact]
  public void ReadsANoteOffOnTheLayerNoteAsDeckInactive() {
    FlxMessageParser parser = new FlxMessageParser();

    // Not observed from this controller, which sends note-on at velocity 0 instead.
    // Handled because the cost is one line and the alternative is a layer state
    // stuck on a deck that has since been switched away.
    Assert.Equal(
      FlxEvent.DeckActive(2, active: false),
      parser.Parse(NoteOff(deck: 2, FlxConstants.DeckActiveNote)));
  }

  [Fact]
  public void RefusesMessagesItCannotTrust() {
    FlxMessageParser parser = new FlxMessageParser();

    // Every control this project reads is a three-byte channel message, so anything
    // else is a stream joined mid-message. Guessing at the missing status byte would
    // invent a control nobody touched, and here the reward for guessing right is a
    // moved downbeat.
    Assert.Null(parser.Parse([0x90, FlxConstants.CueNote]));
    Assert.Null(parser.Parse([]));

    // A data byte where the status belongs: running status, which this device does
    // not use.
    Assert.Null(parser.Parse([FlxConstants.CueNote, 127, 0]));

    // A data byte with its high bit set is the start of the next message showing
    // through a truncated one.
    Assert.Null(parser.Parse([0x90, 0x80 | FlxConstants.CueNote, 127]));
    Assert.Null(parser.Parse([0x90, FlxConstants.CueNote, 0xFF]));
  }

  private static byte[] NoteOn(int deck, int note, int velocity) {
    return [(byte)(0x90 | (deck - 1)), (byte)note, (byte)velocity];
  }

  private static byte[] NoteOff(int deck, int note) {
    return [(byte)(0x80 | (deck - 1)), (byte)note, 0];
  }

  private static byte[] ControlChange(int deck, int controller, int value) {
    return [(byte)(0xB0 | (deck - 1)), (byte)controller, (byte)value];
  }
}
