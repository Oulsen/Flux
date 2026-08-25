using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;

namespace Flux.Audio;

/// <summary>
/// One open device, feeding the ring buffer and the meter until it is disposed.
///
/// The whole NAudio surface of the audio engine lives here. Everything downstream
/// - the ring buffer, the meter, the analyzer, the detector - sees mono floats and
/// a sample rate, which is why all of it can be tested without a sound card.
///
/// The two capture paths differ only in which class opens the device. That is the
/// entire abstraction and it is enough: ASIO keeps rekordbox's audio out of the
/// Windows mixer, so a render endpoint hears nothing while the DJ plays and the
/// controller's USB input hears everything - but once open, both hand over the
/// same interleaved bytes.
/// </summary>
internal sealed class AudioCaptureSession: IDisposable {
  /// <summary>
  /// <c>KSDATAFORMAT_SUBTYPE_IEEE_FLOAT</c> from <c>ksmedia.h</c>.
  ///
  /// Declared here rather than guessed from the sample width. An extensible format
  /// is 32 bits wide whether it carries floats or integers, and reading one as the
  /// other does not fail - it produces plausible-looking garbage, either a
  /// spectrum of denormal noise or a signal that clips on every sample. The
  /// subformat is the only field that actually answers the question.
  /// </summary>
  private static readonly Guid IeeeFloatSubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71");

  private readonly AudioDevice device;
  private readonly MMDevice endpoint;
  private readonly MonoRingBuffer ring;
  private readonly AudioMeter meter;
  private readonly ILogger log;
  private readonly WasapiCapture capture;
  private readonly PcmFormat format;

  /// <summary>
  /// Scratch space for the downmix, reused across callbacks.
  ///
  /// Not thread-safe and does not need to be: NAudio raises
  /// <see cref="IWaveIn.DataAvailable"/> from a single capture thread, one call at
  /// a time. Allocating per callback instead would hand the garbage collector a
  /// buffer a hundred times a second for the lifetime of the app.
  /// </summary>
  private float[] scratch = [];

  private volatile bool failed;

  private AudioCaptureSession(
    AudioDevice device,
    MMDevice endpoint,
    WasapiCapture capture,
    MonoRingBuffer ring,
    AudioMeter meter,
    ILogger log) {
    this.device = device;
    this.endpoint = endpoint;
    this.capture = capture;
    this.ring = ring;
    this.meter = meter;
    this.log = log;
    this.format = Describe(capture.WaveFormat);
  }

  /// <summary>The rate the device opened at, which is the device's choice.</summary>
  public int SampleRate => this.capture.WaveFormat.SampleRate;

  /// <summary>
  /// Whether this session has stopped delivering because of an error, as opposed
  /// to being merely silent. Only this is worth reopening a device for.
  /// </summary>
  public bool Failed => this.failed;

  /// <summary>
  /// Opens a device and starts capturing, or returns null having said why.
  ///
  /// Returning null rather than throwing, because every reason this fails is
  /// ordinary: the device was unplugged a moment ago, another application holds it
  /// exclusively, or its format is one the downmix cannot read. None of them is a
  /// reason to end the audio source, which will try again.
  /// </summary>
  public static AudioCaptureSession? Open(
    AudioDevice device,
    MMDevice endpoint,
    MonoRingBuffer ring,
    AudioMeter meter,
    ILogger log) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(endpoint);
    ArgumentNullException.ThrowIfNull(ring);
    ArgumentNullException.ThrowIfNull(meter);
    ArgumentNullException.ThrowIfNull(log);

    WasapiCapture? capture = null;
    try {
      capture = device.Kind == AudioDeviceKind.Loopback
        ? new WasapiLoopbackCapture(endpoint)
        : new WasapiCapture(endpoint);

      AudioCaptureSession session = new AudioCaptureSession(device, endpoint, capture, ring, meter, log);
      if(!session.format.IsSupported) {
        log.LogError(
          "{Device} offers {Bits}-bit {Encoding} audio on {Channels} channel(s), which Flux cannot read. Pick another device.",
          device.Name,
          capture.WaveFormat.BitsPerSample,
          capture.WaveFormat.Encoding,
          capture.WaveFormat.Channels);

        session.Dispose();
        return null;
      }

      ring.Reset();
      meter.Reset();

      capture.DataAvailable += session.OnDataAvailable;
      capture.RecordingStopped += session.OnRecordingStopped;
      capture.StartRecording();

      log.LogInformation(
        "Capturing {Kind} audio from {Device} at {SampleRate} Hz, {Channels} channel(s), {Bits}-bit {Encoding}.",
        device.Kind == AudioDeviceKind.Loopback ? "loopback" : "input",
        device.Name,
        capture.WaveFormat.SampleRate,
        capture.WaveFormat.Channels,
        capture.WaveFormat.BitsPerSample,
        capture.WaveFormat.Encoding);

      return session;
    } catch(Exception ex) when(ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException) {
      log.LogWarning(ex, "Could not start capturing {Device}. It may be in use by another application.", device.Name);
      capture?.Dispose();
      return null;
    }
  }

  public void Dispose() {
    this.capture.DataAvailable -= this.OnDataAvailable;
    this.capture.RecordingStopped -= this.OnRecordingStopped;

    try {
      // Asks the capture thread to finish; the buffers it hands over between now
      // and then land in a ring buffer that is about to be reset anyway.
      this.capture.StopRecording();
    } catch(Exception ex) when(ex is COMException or InvalidOperationException) {
      this.log.LogDebug(ex, "Stopping the capture of {Device} failed. Disposing it anyway.", this.device.Name);
    }

    this.capture.Dispose();
    this.endpoint.Dispose();
  }

  /// <summary>
  /// Reads NAudio's format description into the three facts the downmix needs.
  ///
  /// <c>Extensible</c> is the format shared mode actually hands over, and it is the
  /// only case that cannot be read off the encoding alone - hence the subformat
  /// GUID rather than the sample width the Phase 0 spike guessed from. The spike
  /// only had to answer "zero or not zero", where a wrong guess still gives the
  /// right answer; a spectrum does not have that luxury.
  /// </summary>
  private static PcmFormat Describe(WaveFormat format) {
    bool isFloat = format.Encoding switch {
      WaveFormatEncoding.IeeeFloat => true,
      WaveFormatEncoding.Extensible => format is WaveFormatExtensible extensible
        && extensible.SubFormat == IeeeFloatSubFormat,
      _ => false,
    };

    return new PcmFormat(format.Channels, format.BitsPerSample, isFloat);
  }

  /// <summary>
  /// The capture callback. It converts, stores, measures, and returns.
  ///
  /// Nothing else belongs here. This runs on WASAPI's capture thread against a
  /// device buffer of ten to twenty milliseconds, so anything that blocks - a
  /// send, an FFT, a log line under contention - is a glitch in someone's audio
  /// and, on a loopback device, in the audience's.
  /// </summary>
  private void OnDataAvailable(object? sender, WaveInEventArgs arguments) {
    int frames = arguments.BytesRecorded / this.format.BytesPerFrame;
    if(frames == 0) {
      return;
    }

    if(this.scratch.Length < frames) {
      this.scratch = new float[frames];
    }

    Span<float> mono = this.scratch.AsSpan(0, frames);
    PcmBlock block = PcmDownmix.ToMono(
      arguments.Buffer.AsSpan(0, arguments.BytesRecorded),
      this.format,
      mono);

    if(block.Frames == 0) {
      return;
    }

    this.ring.Write(mono[..block.Frames]);
    this.meter.Add(block);
  }

  /// <summary>
  /// Records that the device gave up.
  ///
  /// A clean stop is Flux's own doing and says nothing; an exception here is the
  /// device going away underneath a running capture, which is the one case the
  /// service should reopen. Distinguishing them is why "silent" and "failed" are
  /// different states on the wire.
  /// </summary>
  private void OnRecordingStopped(object? sender, StoppedEventArgs arguments) {
    if(arguments.Exception is null) {
      return;
    }

    this.failed = true;
    this.log.LogWarning(
      arguments.Exception,
      "Capture of {Device} stopped unexpectedly. Looking for it again.",
      this.device.Name);
  }
}
