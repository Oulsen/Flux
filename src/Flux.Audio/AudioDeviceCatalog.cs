using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace Flux.Audio;

/// <summary>
/// What Windows can hear, and which of it Flux should listen to.
///
/// Both halves of that are here because the second one is the part with an
/// opinion. Picking a device is not "find something with a signal": Phase 0 swept
/// every render endpoint on this machine while a track was audible and the only
/// one carrying audio was a browser playing Netflix. Finding audio is not finding
/// <em>the</em> audio, and defaulting to the default render endpoint is
/// confidently wrong in exactly the situation the app exists for.
/// </summary>
internal sealed class AudioDeviceCatalog {
  private readonly ILogger<AudioDeviceCatalog> log;

  public AudioDeviceCatalog(ILogger<AudioDeviceCatalog> log) {
    this.log = log ?? throw new ArgumentNullException(nameof(log));
  }

  /// <summary>
  /// Every active endpoint, inputs first.
  ///
  /// Inputs first because that ordering is the one useful default: it is what
  /// makes a name preference resolve to the controller's USB return rather than to
  /// a render endpoint with a similar name, and it puts the devices that can hear
  /// a DJ set above the ones that mostly cannot.
  ///
  /// Never throws. An endpoint can disappear between being enumerated and being
  /// asked its name, and a machine mid-driver-install can refuse the enumeration
  /// outright - neither is a reason for the audio source to end, so both come back
  /// as a shorter list.
  /// </summary>
  public IReadOnlyList<AudioDevice> Devices() {
    List<AudioDevice> devices = new List<AudioDevice>();

    try {
      using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();

      this.Collect(enumerator, DataFlow.Capture, AudioDeviceKind.Capture, devices);
      this.Collect(enumerator, DataFlow.Render, AudioDeviceKind.Loopback, devices);
    } catch(Exception ex) when(ex is COMException or InvalidOperationException) {
      this.log.LogWarning(ex, "Could not enumerate audio endpoints. Trying again shortly.");
    }

    return devices;
  }

  /// <summary>
  /// Opens the endpoint with this id, or null when it is gone.
  ///
  /// The caller owns what comes back and must dispose it.
  /// </summary>
  public MMDevice? Open(string id) {
    try {
      using MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
      return enumerator.GetDevice(id);
    } catch(Exception ex) when(ex is COMException or ArgumentException or InvalidOperationException) {
      // The ordinary case, not a fault: a device unplugged between a list being
      // sent to a browser and a button being pressed on it.
      this.log.LogDebug(ex, "Audio endpoint {Id} could not be opened.", id);
      return null;
    }
  }

  /// <summary>
  /// Picks the device to capture, in the order a listener would want.
  ///
  /// An exact id wins, because it is the only unambiguous way to name a device and
  /// nobody writes one down by accident. A name preference comes next, matched
  /// against inputs before render endpoints. Nothing at all is the third answer
  /// and a deliberate one: with no match, Flux captures nothing and says so,
  /// rather than adopting a device that will be silent all night and reporting it
  /// as the audio source.
  /// </summary>
  /// <param name="devices">The candidates, as returned by <see cref="Devices"/>.</param>
  /// <param name="deviceId">An exact endpoint id, or null.</param>
  /// <param name="preferredName">Part of a device name, or null or empty for no preference.</param>
  public static AudioDevice? Select(
    IReadOnlyList<AudioDevice> devices,
    string? deviceId,
    string? preferredName) {
    ArgumentNullException.ThrowIfNull(devices);

    if(deviceId is { Length: > 0 }) {
      AudioDevice? exact = devices.FirstOrDefault(device => device.Id == deviceId);
      if(exact is not null) {
        return exact;
      }

      // Falls through rather than giving up. A configured id belongs to one
      // machine, so a settings file copied to a second one names a device that
      // does not exist there - and there is usually a perfectly good FLX-10
      // plugged into that machine too.
    }

    if(preferredName is not { Length: > 0 }) {
      return null;
    }

    return devices.FirstOrDefault(device => device.Kind == AudioDeviceKind.Capture && Matches(device, preferredName))
      ?? devices.FirstOrDefault(device => Matches(device, preferredName));
  }

  private static bool Matches(AudioDevice device, string fragment) =>
    device.Name.Contains(fragment, StringComparison.OrdinalIgnoreCase);

  private void Collect(
    MMDeviceEnumerator enumerator,
    DataFlow flow,
    AudioDeviceKind kind,
    List<AudioDevice> devices) {
    foreach(MMDevice device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)) {
      using(device) {
        try {
          devices.Add(new AudioDevice(device.ID, device.FriendlyName, kind));
        } catch(Exception ex) when(ex is COMException or InvalidOperationException) {
          this.log.LogDebug(ex, "Skipping an audio endpoint that would not describe itself.");
        }
      }
    }
  }
}
