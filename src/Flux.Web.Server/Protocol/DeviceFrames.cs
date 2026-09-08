using Flux.Audio;

namespace Flux.Web.Server.Protocol;

/// <summary>
/// Composes the <c>devices</c> frame from what the audio layer can see.
///
/// Here rather than in <see cref="DevicesBroadcaster"/> because it has two callers
/// and they must not be able to disagree. The broadcaster sends the list on a
/// timer; <see cref="SocketHub"/> sends one immediately to a client that has just
/// subscribed, so a picker fills in at once instead of after a blank tenth of a
/// second. Two compositions of the same frame would eventually differ in exactly
/// the field that mattered.
/// </summary>
internal static class DeviceFrames {
  /// <summary>The device list, encoded and ready to send.</summary>
  internal static ReadOnlyMemory<byte> Snapshot(IAudioDevices audio) {
    return Frames.Devices(Compose(audio));
  }

  /// <summary>
  /// The device list as the protocol describes it.
  ///
  /// The status is read once and applied to every device, rather than asked for per
  /// device: it names one selection, and re-reading it mid-list could produce a
  /// frame in which a selection changed halfway - two devices selected, or none.
  ///
  /// Internal rather than private so the tests can assert on the composed frame,
  /// which is where the "measured" rule actually lives.
  /// </summary>
  internal static DevicesData Compose(IAudioDevices audio) {
    ArgumentNullException.ThrowIfNull(audio);

    AudioCaptureStatus status = audio.Status();

    return new DevicesData([.. audio.Devices().Select(device => Describe(device, status))]);
  }

  /// <summary>
  /// One device, said of it only what is known.
  ///
  /// The selected device is the <em>wanted</em> one and not necessarily the open
  /// one, which is the whole reason a failed device is worth a row at all: the
  /// endpoint somebody picked, sitting there labelled "failed", is the answer to
  /// why there is no audio. Its meters are still omitted, because a device that
  /// never opened delivered no samples to measure.
  /// </summary>
  private static DeviceData Describe(AudioDevice device, AudioCaptureStatus status) {
    if(device.Id != status.Device?.Id) {
      return new DeviceData(
        device.Id,
        device.Name,
        KindName(device.Kind),
        State: null,
        Selected: false,
        Rms: null,
        Peak: null);
    }

    // Digital silence counts as measured, and deliberately: bit-exact zeros are a
    // real reading of a stopped deck, and a meter pinned at the bottom is the
    // difference between "the cable is fine and nothing is playing" and "Flux is
    // not listening to this".
    bool measured = status.State is AudioDeviceState.Signal or AudioDeviceState.DigitalSilence;

    return new DeviceData(
      device.Id,
      device.Name,
      KindName(device.Kind),
      StateName(status.State),
      Selected: true,
      measured ? status.Levels.Rms : null,
      measured ? status.Levels.Peak : null);
  }

  /// <summary>
  /// The protocol's name for a capture path.
  ///
  /// A switch rather than <c>ToString().ToLowerInvariant()</c>, so renaming the
  /// enum is a compile error here instead of a silently changed wire format that
  /// only a browser ever notices.
  /// </summary>
  private static string KindName(AudioDeviceKind kind) {
    return kind switch {
      AudioDeviceKind.Loopback => "loopback",
      _ => "capture",
    };
  }

  /// <summary>The protocol's name for what a device is doing.</summary>
  private static string StateName(AudioDeviceState state) {
    return state switch {
      AudioDeviceState.Signal => "signal",
      AudioDeviceState.DigitalSilence => "digitalSilence",
      AudioDeviceState.Failed => "failed",
      _ => "noData",
    };
  }
}
