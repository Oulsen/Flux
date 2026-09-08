namespace Flux.Midi;

/// <summary>
/// Turns raw MIDI bytes from the FLX-10 into the three things Flux cares about,
/// and discards everything else.
///
/// This is the whole of the project that can be tested, and it is deliberately the
/// whole of the project that decides anything. Nothing here touches WinRT, a
/// device, or a clock: bytes in, an event or nothing out. The observer above it is
/// then thin enough that "it needs a controller on the desk" is an acceptable
/// answer for the little that is left.
///
/// Not static, which is a deviation from the plan and forced by the wire format
/// rather than chosen. A 14-bit control arrives as two messages, so the coarse half
/// has to be remembered while the fine half is in flight - and a latch is state, so
/// a static method could only have made it shared between callers. One instance per
/// open port keeps two controllers from overwriting each other's faders.
///
/// The instance is not thread-safe. Its caller holds a lock, because the messages
/// of one port are not documented to be raised in order on one thread.
/// </summary>
internal sealed class FlxMessageParser {
  private const int NoteOffStatus = 0x80;
  private const int NoteOnStatus = 0x90;
  private const int ControlChangeStatus = 0xB0;

  /// <summary>
  /// The velocity at which a switch-like note counts as on. The device only ever
  /// sends 0 or 127 for these, so the midpoint is a formality rather than a
  /// threshold with a judgement in it.
  /// </summary>
  private const int SwitchOnVelocity = 64;

  /// <summary>
  /// The two halves of each deck's channel fader, indexed by deck - 1.
  ///
  /// Both start at zero, which is the fader's own rest position, so an
  /// unsynchronised start reads as closed rather than as some arbitrary value. That
  /// matters because a fader nobody has touched must never look open: audibility is
  /// what gates a cue press onto the light grid.
  /// </summary>
  private readonly int[] faderMsb = new int[FlxConstants.DeckCount];
  private readonly int[] faderLsb = new int[FlxConstants.DeckCount];

  /// <summary>
  /// Reads one MIDI message.
  /// </summary>
  /// <param name="message">The raw bytes, status byte first.</param>
  /// <returns>What the controller said, or null for the vast majority of messages, which is not something Flux watches.</returns>
  public FlxEvent? Parse(ReadOnlySpan<byte> message) {
    // Every message this project reads is a three-byte channel message. Shorter is
    // a running-status continuation or a system message, and neither is something
    // this device uses for the controls below.
    if(message.Length < 3) {
      return null;
    }

    int status = message[0];

    // A data byte where a status byte belongs means the stream was joined
    // mid-message. Guessing at the status would invent a control that was never
    // touched, and on this device the reward for guessing right is a moved
    // downbeat.
    if(status < 0x80) {
      return null;
    }

    int deck = (status & 0x0F) + 1;
    int data1 = message[1];
    int data2 = message[2];

    // Data bytes have their high bit clear by definition, so one that does not is
    // a truncated message followed by the start of the next.
    if(data1 > 0x7F || data2 > 0x7F) {
      return null;
    }

    // The controller puts plenty on channels 5 and up - pads on 10, the jog wheel,
    // loop buttons and the crossfader on 7 - and none of it identifies a deck.
    if(deck < FlxConstants.FirstDeckChannel || deck > FlxConstants.LastDeckChannel) {
      return null;
    }

    return (status & 0xF0) switch {
      NoteOnStatus => this.ReadNoteOn(deck, data1, data2),
      NoteOffStatus => this.ReadNoteOff(deck, data1),
      ControlChangeStatus => this.ReadControlChange(deck, data1, data2),
      _ => null,
    };
  }

  private FlxEvent? ReadNoteOn(int deck, int note, int velocity) {
    // The layer indicator is checked before the zero-velocity convention below,
    // and that ordering is load-bearing. A note-on at velocity 0 conventionally
    // means a release, but this device uses exactly that message to say a deck
    // stopped being the displayed one - so treating it as a release would silently
    // drop half of the only message that carries layer state.
    if(note == FlxConstants.DeckActiveNote) {
      return FlxEvent.DeckActive(deck, velocity >= SwitchOnVelocity);
    }

    // The press, not the release. Anchoring on both would place the bar twice for
    // one gesture, and the second one lands wherever the finger happened to leave.
    if(note == FlxConstants.CueNote && velocity > 0) {
      return FlxEvent.Cue(deck);
    }

    return null;
  }

  private FlxEvent? ReadNoteOff(int deck, int note) {
    // Not observed from this device, which sends note-on at velocity 0 instead.
    // Handled anyway because the cost is one line and the alternative is a layer
    // state stuck on a deck that has since been switched away.
    return note == FlxConstants.DeckActiveNote
      ? FlxEvent.DeckActive(deck, active: false)
      : null;
  }

  private FlxEvent? ReadControlChange(int deck, int controller, int value) {
    int index = deck - 1;

    // Either half emits an event carrying the best current answer, rather than
    // waiting for the pair to complete. On this device the two always arrive
    // together, so the wait would work - but a control that only ever sent its
    // coarse half would then produce no fader events at all, which reads as a fader
    // nobody has touched, which silently disables the cue press. Emitting twice per
    // move costs an event; waiting costs the feature.
    if(controller == FlxConstants.ChannelFaderMsbController) {
      this.faderMsb[index] = value;
    } else if(controller == FlxConstants.ChannelFaderLsbController) {
      this.faderLsb[index] = value;
    } else {
      return null;
    }

    return FlxEvent.FaderMoved(deck, (this.faderMsb[index] << 7) | this.faderLsb[index]);
  }
}
