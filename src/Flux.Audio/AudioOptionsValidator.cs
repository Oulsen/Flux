using Microsoft.Extensions.Options;

namespace Flux.Audio;

/// <summary>
/// Checks the audio settings before anything opens a device.
///
/// There is one mistake here worth stopping the app for, and it does not look
/// like a mistake at all: a <see cref="AudioOptions.PreferDeviceNamed"/> made
/// only of spaces. It is not empty, so it is treated as a real preference, and
/// almost every endpoint name contains a space - so it silently selects whichever
/// device happens to be enumerated first. That is the failure Phase 0 spent an
/// evening on: a render endpoint that produced a plausible level from a browser
/// tab while rekordbox's audio was somewhere else entirely.
/// </summary>
internal sealed class AudioOptionsValidator: IValidateOptions<AudioOptions> {
  public ValidateOptionsResult Validate(string? name, AudioOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    List<string> failures = new List<string>();

    if(options.PreferDeviceNamed is { Length: > 0 } preferred && string.IsNullOrWhiteSpace(preferred)) {
      failures.Add("Audio:PreferDeviceNamed must name part of a device, or be empty for no preference. "
        + "Whitespace matches nearly every device name.");
    }

    // Distinguished from null on purpose. A null id means "go by name", which is
    // the normal case; an id that is present but blank means a setting was
    // written and left unfinished, and silently going by name would hide it.
    if(options.DeviceId is { Length: > 0 } id && string.IsNullOrWhiteSpace(id)) {
      failures.Add("Audio:DeviceId must be an endpoint id, or null to select by name.");
    }

    // Refused rather than clamped, because both ends of the range are already
    // extreme: a zero here reads as "off" and silently gets the most eager
    // detection there is, which is the opposite of what anyone typing it meant.
    if(options.Sensitivity < AudioConstants.MinSensitivity
      || options.Sensitivity > AudioConstants.MaxSensitivity) {
      failures.Add($"Audio:Sensitivity must be between {AudioConstants.MinSensitivity} and "
        + $"{AudioConstants.MaxSensitivity}. It was {options.Sensitivity}.");
    }

    if(options.RecordFramesPath is { Length: > 0 } path && string.IsNullOrWhiteSpace(path)) {
      failures.Add("Audio:RecordFramesPath must be a file path, or null to record nothing.");
    }

    if(options.RecordAudioPath is { Length: > 0 } audioPath && string.IsNullOrWhiteSpace(audioPath)) {
      failures.Add("Audio:RecordAudioPath must be a file path, or null to record nothing.");
    }

    return failures.Count == 0
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(failures);
  }
}
