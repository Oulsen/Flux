namespace Flux.Audio;

/// <summary>
/// Which way audio is taken from an endpoint.
///
/// Two paths rather than one because ASIO takes rekordbox's output out of the
/// Windows mixer entirely, so loopback hears nothing at all while the DJ is
/// playing - measured in Phase 0, on every render endpoint on this machine. The
/// controller hands the master mix back over USB as an ordinary input, which is
/// why the capture path exists and why it is the default.
/// </summary>
public enum AudioDeviceKind {
  /// <summary>An input: a line in, a microphone, or a controller's USB return.</summary>
  Capture,

  /// <summary>What a render endpoint is playing, captured from the mixer.</summary>
  Loopback,
}

/// <summary>
/// What is arriving from a device, as the four cases that call for four different
/// responses.
///
/// A single "no signal" would blur three of them, and they are not
/// interchangeable: one means the deck is stopped, one is Windows behaving as
/// documented, and only the last is a fault worth colouring red.
/// </summary>
public enum AudioDeviceState {
  /// <summary>Audio is arriving and is not silent.</summary>
  Signal,

  /// <summary>Audio is arriving and every sample is zero: the deck is stopped, or the fader is down.</summary>
  DigitalSilence,

  /// <summary>
  /// Nothing has been delivered. Ordinary rather than broken for a loopback
  /// device, which Windows leaves silent while nothing plays to it.
  /// </summary>
  NoData,

  /// <summary>The device could not be opened, or stopped delivering after an error.</summary>
  Failed,
}

/// <summary>
/// An audio endpoint Flux could listen to.
/// </summary>
/// <param name="Id">
/// The endpoint id Windows assigns. Opaque, and the only value the protocol's
/// <c>selectAudioDevice</c> accepts: names are neither unique nor stable, and two
/// identical interfaces produce two identical names.
/// </param>
/// <param name="Name">
/// What Windows calls it. Worth showing and never worth trusting: the endpoint
/// carrying the master mix here is called <c>Microfoon (2- DDJ-FLX10)</c>,
/// because Windows labels every USB audio input that way. Nothing is plugged
/// into the controller.
/// </param>
/// <param name="Kind">Which capture path reaches it.</param>
public sealed record AudioDevice(string Id, string Name, AudioDeviceKind Kind);

/// <summary>
/// A level measurement over a recent window, both figures on a 0 to 1 scale.
///
/// Both are kept because they answer different questions and either one alone
/// hides a fault: a signal that is present but far too quiet has a low peak, and
/// a signal that is clipping has a peak pinned at 1 with an average well below
/// it.
/// </summary>
/// <param name="Rms">Average level over the window.</param>
/// <param name="Peak">Loudest single sample in the window.</param>
public readonly record struct AudioLevels(double Rms, double Peak) {
  /// <summary>Nothing measured.</summary>
  public static AudioLevels None => default;
}

/// <summary>
/// What the capture service is currently hearing, and from where.
/// </summary>
/// <param name="Device">
/// The device being captured, or null when none was chosen or none could be
/// opened. Null is a normal state, not a failure: a machine with no matching
/// input is the standalone case, and Link or a tapped tempo still runs the night.
/// </param>
/// <param name="State">How that device is behaving.</param>
/// <param name="Levels">Its level over the last meter window.</param>
/// <param name="SampleRate">
/// The rate the device actually opened at, which is the device's choice and not a
/// setting. Zero while nothing is open.
/// </param>
/// <param name="Frames">Mono samples captured since this device was opened.</param>
public readonly record struct AudioCaptureStatus(
  AudioDevice? Device,
  AudioDeviceState State,
  AudioLevels Levels,
  int SampleRate,
  long Frames);

/// <summary>
/// The audio endpoints Flux can see, which one it is listening to, and the means
/// to change that.
///
/// This is the whole of what the web layer is allowed to know about audio
/// hardware: a list, a status and a selection. Nothing here mentions a sample
/// format, a buffer or a driver mode, which is what keeps a device picker from
/// slowly acquiring opinions about WASAPI.
/// </summary>
public interface IAudioDevices {
  /// <summary>
  /// The endpoints Flux can see, as of the last check.
  ///
  /// Served from a cache, and the caller is the reason: a subscribed browser asks
  /// ten times a second, and enumerating operating-system endpoints at that rate
  /// to answer a question whose answer changes when somebody plugs in a cable
  /// would be absurd.
  /// </summary>
  IReadOnlyList<AudioDevice> Devices();

  /// <summary>
  /// What is being captured right now, and how it is behaving.
  ///
  /// The device named is the <em>wanted</em> one rather than the open one, so a
  /// device that will not open is reported alongside its
  /// <see cref="AudioDeviceState.Failed"/> instead of vanishing from the readout
  /// at the exact moment somebody needs to know why there is no audio.
  /// </summary>
  AudioCaptureStatus Status();

  /// <summary>
  /// Points capture at a different device.
  ///
  /// Answers false for an id that is not in the list, which is the protocol's
  /// <c>rejected</c> and covers the ordinary race of a device unplugged between
  /// the list arriving in a browser and a button being pressed on it. It does not
  /// wait for the device to open: whether the choice was a good one is what the
  /// level meter is for.
  /// </summary>
  bool TrySelect(string deviceId);
}
