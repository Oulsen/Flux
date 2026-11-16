namespace Flux.Audio;

/// <summary>
/// How eagerly the audio source hears a beat, set from the page.
///
/// The one tuning knob audio detection has, on the scene's own 1 to 100 scale. It was a
/// configuration key only, which put the one setting meant for a booth - turned up for
/// a soft kick, down for a detector finding beats in the hi-hats - in a file nobody at
/// the booth opens. A contract of its own rather than a member of <see cref="IAudioDevices"/>,
/// because choosing what to listen to and how hard to listen are separate questions, and
/// the web server should not have to reach into the detector to answer the second.
/// </summary>
public interface IAudioSensitivity {
  /// <summary>The lowest sensitivity: an onset needs nearly four times the recent average.</summary>
  public const int Min = 1;

  /// <summary>The highest sensitivity: an onset needs one and a half times the recent average.</summary>
  public const int Max = 100;

  /// <summary>The sensitivity the detector is running at now.</summary>
  int Sensitivity { get; }

  /// <summary>Raised after the sensitivity changed, with the new value.</summary>
  event Action<int>? SensitivityChanged;

  /// <summary>
  /// Sets the sensitivity and remembers it. False, and nothing changed, for a value
  /// outside <see cref="Min"/> to <see cref="Max"/>.
  /// </summary>
  bool TrySetSensitivity(int sensitivity);
}
