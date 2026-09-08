namespace Flux.Midi;

/// <summary>
/// Which controller Flux watches for cue presses.
///
/// Observation is read-only and costs the DJ nothing - Phase 0 confirmed Flux can
/// watch the FLX-10 while rekordbox reacts to every message at the same time - so
/// it is on by default. Both settings exist to point it at a machine that is not
/// this one, or to switch it off entirely.
/// </summary>
public sealed class MidiOptions {
  public const string SectionName = "Midi";

  /// <summary>
  /// Whether to watch a MIDI controller at all.
  ///
  /// Worth a setting rather than relying on the name preference finding nothing,
  /// because those two states are indistinguishable from the outside and a DJ whose
  /// bar keeps moving needs to be able to rule this out in one line.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// Part of the MIDI input's name to watch, matched case-insensitively.
  ///
  /// Empty means no preference, which observes nothing at all - the same semantics
  /// as the audio device preference, and for a sharper reason here. Every note
  /// number this project reads was measured on one controller, so opening every
  /// input port would let a keyboard's middle C or a foreign controller's button 11
  /// move the downbeat. Watching nothing is the right answer to "which device?"
  /// when nobody has said.
  /// </summary>
  public string PreferDeviceNamed { get; set; } = "DDJ-FLX10";
}
