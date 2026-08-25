namespace Flux.Audio;

/// <summary>
/// One analyzed window of audio: what the analyzer heard, before anything decides
/// what it means.
///
/// Immutable and handed on by reference rather than filled in place, which is what
/// lets the analyzer thread publish it into a slot the web layer reads without a
/// lock: a reader either has the old frame or the new one, never a half-written
/// mixture of the two.
/// </summary>
/// <param name="TimestampMicros">
/// When the newest sample in the window was captured, on the shared monotonic
/// clock. The detector times everything from this rather than from a clock of its
/// own, which is what makes a recorded run replayable at any speed.
/// </param>
/// <param name="SampleRate">The rate the window was captured at, which decides where the band edges fall.</param>
/// <param name="Bins">
/// <see cref="AudioConstants.DisplayBins"/> log-spaced columns, low frequency
/// first, each 0 to 255. For drawing only: no decision is taken on these.
/// </param>
/// <param name="Kick">Bass band energy, 0 to 1. The one the detector triggers on.</param>
/// <param name="Mids">Mid band energy, 0 to 1. Used only to tell a kick from a snare.</param>
/// <param name="Highs">High band energy, 0 to 1. Drawn, never decided on.</param>
internal sealed record SpectrumFrame(
  double TimestampMicros,
  int SampleRate,
  int[] Bins,
  double Kick,
  double Mids,
  double Highs);

/// <summary>
/// Where the analyzer gets its audio.
///
/// An interface for two reasons, one of them immediate. A cadence test has to run
/// the real analyzer thread against a signal it controls, on a machine with no
/// sound card, and that is the only kind of test that catches a frame loop which
/// runs but never fires (Phase 1's beat scheduler shipped exactly that bug and
/// only a real-thread test found it). The second is that
/// <see cref="AudioCaptureService"/> is the whole WASAPI stack, and the analyzer
/// needs three lines of it.
/// </summary>
internal interface IAudioSamples {
  /// <summary>
  /// The rate the open device is running at, or zero while nothing is open.
  ///
  /// Read every frame rather than cached, because it changes when the device does:
  /// band edges derived from a stale rate would put the kick band in the wrong
  /// place and the detector would go deaf without anything looking broken.
  /// </summary>
  int SampleRate { get; }

  /// <summary>
  /// Copies the newest captured samples, or answers false when that many have not
  /// been captured yet.
  /// </summary>
  bool TryReadLatest(Span<float> destination);
}
