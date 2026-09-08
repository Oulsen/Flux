using Microsoft.Extensions.Options;

namespace Flux.Midi;

/// <summary>
/// Checks the MIDI settings before anything opens a port.
///
/// There is one mistake here worth refusing to start over, and it is the same one
/// the audio settings guard: a <see cref="MidiOptions.PreferDeviceNamed"/> made
/// only of spaces. It is not empty, so it counts as a real preference, and almost
/// every device name contains a space - so it silently watches whichever input port
/// enumerates first. On the audio side that costs a wrong level meter; here it hands
/// an unknown device the authority to move the downbeat.
/// </summary>
internal sealed class MidiOptionsValidator: IValidateOptions<MidiOptions> {
  public ValidateOptionsResult Validate(string? name, MidiOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if(options.PreferDeviceNamed is { Length: > 0 } preferred && string.IsNullOrWhiteSpace(preferred)) {
      return ValidateOptionsResult.Fail(
        "Midi:PreferDeviceNamed must name part of a MIDI input, or be empty to watch nothing. "
        + "Whitespace matches nearly every device name.");
    }

    return ValidateOptionsResult.Success;
  }
}
