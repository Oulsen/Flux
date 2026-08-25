namespace Flux.Audio;

/// <summary>
/// Which device audio analysis listens to. Both settings are escape hatches for a
/// machine that is not this one, not knobs to tune.
/// </summary>
public sealed class AudioOptions {
  public const string SectionName = "Audio";

  /// <summary>
  /// The exact endpoint id to capture, or null to go by name.
  ///
  /// The only setting that can name a device unambiguously, and the only one
  /// worth writing down when two identical interfaces are plugged in. It is not
  /// portable between machines: the id encodes the endpoint, so a config file
  /// copied to another PC will name a device that does not exist there, and Flux
  /// falls back to the name preference rather than capturing nothing.
  /// </summary>
  public string? DeviceId { get; set; }

  /// <summary>
  /// Part of the device name to prefer when no id is given, matched
  /// case-insensitively.
  ///
  /// The controller rather than a generic input, because on this rig the endpoint
  /// carrying the master mix is the FLX-10's USB return - and it is called
  /// <c>Microfoon (2- DDJ-FLX10)</c>, so matching on "microphone" or "line in"
  /// would find exactly the wrong things. Inputs are searched before render
  /// endpoints, so a name that matches both picks the one that hears the mix
  /// while ASIO owns playback.
  ///
  /// Empty means no preference, which leaves nothing captured until a device is
  /// picked. That is deliberate: guessing costs the whole point of the source,
  /// since the default render endpoint is the one answer Phase 0 measured to be
  /// wrong while DJing.
  /// </summary>
  public string PreferDeviceNamed { get; set; } = "DDJ-FLX10";

  /// <summary>
  /// How eagerly beats are detected, from 1 to 100.
  ///
  /// The only tuning knob audio detection has, and deliberately the only one: every
  /// other threshold in <see cref="AudioConstants"/> was arrived at against real
  /// music and they interact, so a knob per threshold would be twenty ways to make
  /// detection worse. This one scales the bar an onset has to clear, from nearly
  /// four times the recent average flux at 1 down to one and a half at 100.
  ///
  /// Turn it up for music with a soft kick, down for a track where the detector
  /// finds beats in the hi-hats. Halving or doubling a reported tempo is a
  /// different problem and this is not the setting for it.
  /// </summary>
  public int Sensitivity { get; set; } = AudioConstants.DefaultSensitivity;

  /// <summary>
  /// A file to write analyzed frames to, or null for the normal case of not
  /// recording anything.
  ///
  /// It exists to make test fixtures out of real music, which is the only honest way
  /// to test a beat detector: synthetic clicks prove the arithmetic, and a recording
  /// of an actual track played through the actual controller proves the tuning. A
  /// run with this set produces a file that replays through the detector at any
  /// speed, so a defect found on stage becomes a test rather than a memory.
  ///
  /// Off by default because it writes continuously for as long as the app runs, and
  /// nobody wants to discover it during a set.
  /// </summary>
  public string? RecordFramesPath { get; set; }
}
