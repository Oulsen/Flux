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
}
