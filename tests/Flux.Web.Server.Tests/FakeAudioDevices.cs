using Flux.Audio;

namespace Flux.Web.Server.Tests;

/// <summary>
/// An audio layer with a fixed device list and no hardware behind it.
///
/// The whole reason <c>Flux.Audio.Contract</c> exists in the form it does: the web
/// layer sees a list, a status and a selection, so every rule about what a
/// <c>devices</c> frame may claim can be tested on a machine with no sound card -
/// including the ones that matter most, which are about a device that will not
/// open.
/// </summary>
internal sealed class FakeAudioDevices: IAudioDevices {
  private readonly List<AudioDevice> devices = new List<AudioDevice>();

  private AudioCaptureStatus status;

  /// <summary>Device ids that <see cref="TrySelect"/> was called with, in order.</summary>
  public List<string> Selections { get; } = new List<string>();

  /// <summary>Adds a device nobody is listening to.</summary>
  public FakeAudioDevices Seeing(string id, string name, AudioDeviceKind kind = AudioDeviceKind.Capture) {
    this.devices.Add(new AudioDevice(id, name, kind));

    return this;
  }

  /// <summary>
  /// Points the status at one of the devices already added, whether or not it
  /// opened. Levels are only ever read for a device that produced samples, so they
  /// are supplied together with the state that says it did.
  /// </summary>
  public FakeAudioDevices Capturing(string id, AudioDeviceState state, double rms = 0, double peak = 0) {
    AudioDevice device = this.devices.Single(candidate => candidate.Id == id);

    this.status = new AudioCaptureStatus(device, state, new AudioLevels(rms, peak), 48_000, Frames: 4096);

    return this;
  }

  public IReadOnlyList<AudioDevice> Devices() {
    return this.devices;
  }

  public AudioCaptureStatus Status() {
    return this.status;
  }

  public bool TrySelect(string deviceId) {
    this.Selections.Add(deviceId);

    return this.devices.Any(device => device.Id == deviceId);
  }
}
