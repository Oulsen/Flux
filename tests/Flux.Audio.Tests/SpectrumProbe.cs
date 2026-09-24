using NAudio.Dsp;

namespace Flux.Audio.Tests;

/// <summary>
/// Replays recorded audio through the same transform <see cref="SpectrumAnalyzer"/>
/// runs, and hands out the magnitudes before anything clamps them.
///
/// This exists because the one number the decibel window has to be chosen against
/// is the one number nothing publishes. <see cref="SpectrumScale.ToByte"/> clamps
/// at <see cref="AudioConstants.SpectrumMaxDb"/>, so on a signal that reaches the
/// top of the window every recording and every frame on the wire holds the ceiling
/// rather than the level. Choosing the ceiling from data that was already cut off
/// at it is circular, so the level has to be recomputed from the samples.
///
/// It reproduces the analyzer's arithmetic rather than calling it, which is a real
/// risk and is why <c>WindowCalibrationTests</c> checks the reproduction against a
/// frame recording made in the same run before it believes anything this says. The
/// pieces that must not drift - the Hann taper, the transform, and
/// <see cref="SpectrumAnalyzer.MagnitudeScale"/> - are taken from the same places
/// the analyzer takes them from rather than copied as numbers.
/// </summary>
internal static class SpectrumProbe {
  private const int FftExponent = 11;

  /// <summary>
  /// Walks the recording one analyzer frame at a time.
  /// </summary>
  /// <param name="samples">The whole recording, mono.</param>
  /// <param name="rate">Its sample rate.</param>
  /// <param name="onFrame">
  /// Called per frame with the frame's time in seconds and the smoothed magnitudes,
  /// one per bin. The array is reused between calls, so a caller that wants to keep
  /// it has to copy it.
  /// </param>
  public static void Replay(float[] samples, int rate, Action<double, double[]> onFrame) {
    ArgumentNullException.ThrowIfNull(samples);
    ArgumentNullException.ThrowIfNull(onFrame);
    ArgumentOutOfRangeException.ThrowIfLessThan(rate, 1);

    double[] taper = new double[AudioConstants.FftSize];
    for(int index = 0; index < taper.Length; index++) {
      taper[index] = FastFourierTransform.HannWindow(index, taper.Length);
    }

    Complex[] transform = new Complex[AudioConstants.FftSize];
    double[] smoothed = new double[SpectrumScale.Bins];

    // Frame k reads the newest window as of k/60 s, which is what
    // IAudioSamples.TryReadLatest hands the analyzer on a device that is keeping up.
    for(long frame = 0; ; frame++) {
      long end = (long)(frame * (double)rate / AudioConstants.FrameRateHz);
      if(end < AudioConstants.FftSize) {
        continue;
      }

      if(end > samples.Length) {
        return;
      }

      long start = end - AudioConstants.FftSize;
      for(int index = 0; index < AudioConstants.FftSize; index++) {
        transform[index].X = (float)(samples[start + index] * taper[index]);
        transform[index].Y = 0;
      }

      FastFourierTransform.FFT(true, FftExponent, transform);

      for(int bin = 0; bin < smoothed.Length; bin++) {
        double real = transform[bin].X;
        double imaginary = transform[bin].Y;
        double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary)) * SpectrumAnalyzer.MagnitudeScale;

        smoothed[bin] = (smoothed[bin] * AudioConstants.SpectrumSmoothing)
          + (magnitude * (1.0 - AudioConstants.SpectrumSmoothing));
      }

      onFrame(end / (double)rate, smoothed);
    }
  }

  /// <summary>
  /// A magnitude as decibels, with the same floor <see cref="SpectrumScale"/> uses
  /// and none of its clamping.
  /// </summary>
  public static double Decibels(double magnitude) {
    return 20.0 * Math.Log10(Math.Max(Math.Abs(magnitude), 1e-12));
  }

  /// <summary>
  /// <see cref="SpectrumScale.ToByte"/> with the window supplied rather than taken
  /// from <see cref="AudioConstants"/>, so candidate windows can be compared
  /// without recompiling.
  /// </summary>
  public static int ToByte(double magnitude, double minDb, double maxDb) {
    double position = (Decibels(magnitude) - minDb) / (maxDb - minDb);

    return Math.Clamp((int)Math.Round(position * 255.0), 0, 255);
  }

  /// <summary>
  /// Turns one frame's magnitudes into the frame the detector consumes, on a
  /// candidate window.
  /// </summary>
  public static SpectrumFrame ToFrame(double seconds, double[] magnitudes, int rate, double minDb, double maxDb) {
    ArgumentNullException.ThrowIfNull(magnitudes);

    int[] spectrum = new int[magnitudes.Length];
    for(int bin = 0; bin < magnitudes.Length; bin++) {
      spectrum[bin] = ToByte(magnitudes[bin], minDb, maxDb);
    }

    (int kickLow, int kickHigh) = SpectrumScale.BandRange(AudioConstants.KickLowHz, AudioConstants.KickHighHz, rate);
    (int midLow, int midHigh) = SpectrumScale.BandRange(AudioConstants.MidLowHz, AudioConstants.MidHighHz, rate);
    (int highLow, int highHigh) = SpectrumScale.BandRange(AudioConstants.HighLowHz, AudioConstants.HighHighHz, rate);

    return new SpectrumFrame(
      seconds * 1_000_000.0,
      rate,
      SpectrumScale.DisplayColumns(spectrum, rate, AudioConstants.DisplayBins),
      SpectrumScale.BandEnergy(spectrum, kickLow, kickHigh),
      SpectrumScale.BandEnergy(spectrum, midLow, midHigh),
      SpectrumScale.BandEnergy(spectrum, highLow, highHigh));
  }
}
