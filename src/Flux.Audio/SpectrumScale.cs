namespace Flux.Audio;

/// <summary>
/// The arithmetic between an FFT magnitude and the numbers the detector and the
/// visualizer work in.
///
/// Separated from <see cref="SpectrumAnalyzer"/> because this is where being wrong
/// is both easy and invisible. Every threshold in <see cref="AudioConstants"/> is
/// a number on the byte scale this class produces, so a mistake here does not look
/// like a bug: detection simply becomes slightly deaf or slightly hysterical, and
/// no amount of watching the light show says which. Pure functions can be pinned
/// by tests; a thread reading a sound card cannot.
/// </summary>
internal static class SpectrumScale {
  /// <summary>
  /// Usable bins in a transform: the positive half of the spectrum. The rest
  /// mirrors it and carries nothing.
  /// </summary>
  public const int Bins = AudioConstants.FftSize / 2;

  /// <summary>
  /// The quietest magnitude taken seriously, so a bin of bit-exact zeros has a
  /// finite decibel value instead of negative infinity.
  /// </summary>
  private const double MagnitudeFloor = 1e-12;

  /// <summary>
  /// The bin a frequency falls in.
  ///
  /// Rounded rather than truncated so a band edge lands in the nearer bin, and
  /// clamped because a low sample rate can put a band edge past the end of the
  /// spectrum: at 8 kHz the high band's top is beyond the Nyquist frequency, and
  /// the honest answer there is a narrower band rather than an exception.
  /// </summary>
  public static int BinOf(double hz, int sampleRate) {
    if(sampleRate <= 0) {
      return 0;
    }

    return Math.Clamp((int)Math.Round(hz * AudioConstants.FftSize / sampleRate), 0, Bins);
  }

  /// <summary>
  /// The half-open bin range covering a frequency band.
  ///
  /// Half-open so that adjacent bands sharing an edge, as the kick and mid bands
  /// do at 200 Hz, cannot both claim the bin on it. Counting one bin twice would
  /// tilt the kick-versus-mid ratio that separates a kick from a snare, which is
  /// the one comparison in the detector with no threshold to absorb an error.
  /// </summary>
  /// <returns>
  /// A range guaranteed to hold at least one bin, unless the band starts past the
  /// end of the spectrum entirely.
  /// </returns>
  public static (int Low, int High) BandRange(double lowHz, double highHz, int sampleRate) {
    int low = BinOf(lowHz, sampleRate);
    int high = BinOf(highHz, sampleRate);

    // A band narrower than a bin still has to be measurable: at any sample rate
    // above about 6 kHz the kick band spans several bins, but the guard costs
    // nothing and an empty range would divide by zero downstream.
    return low >= Bins ? (Bins - 1, Bins) : (low, Math.Max(high, low + 1));
  }

  /// <summary>
  /// Turns a normalized magnitude into the 0 to 255 value every threshold is
  /// expressed against.
  ///
  /// Decibels and not the magnitude itself, which is the whole point: music covers
  /// four orders of magnitude and a linear scale spends nearly all of its range on
  /// the loudest tenth of it. The band and the endpoints are
  /// <see cref="AudioConstants.SpectrumMinDb"/> and
  /// <see cref="AudioConstants.SpectrumMaxDb"/>, and the reason they are those
  /// numbers is argued there.
  /// </summary>
  public static int ToByte(double magnitude) {
    if(!double.IsFinite(magnitude)) {
      // A non-finite magnitude means the window held one, which the capture path
      // already refuses to write. Reading it as silence keeps a single bad sample
      // from spreading across the frame instead of killing the band.
      return 0;
    }

    double decibels = 20.0 * Math.Log10(Math.Max(Math.Abs(magnitude), MagnitudeFloor));
    double position = (decibels - AudioConstants.SpectrumMinDb)
      / (AudioConstants.SpectrumMaxDb - AudioConstants.SpectrumMinDb);

    return Math.Clamp((int)Math.Round(position * 255.0), 0, 255);
  }

  /// <summary>
  /// The mean squared level over a band, on a 0 to 1 scale.
  ///
  /// Squared because the scene squares, and the mean rather than the sum for a
  /// reason worth keeping: a sum would scale with how many bins the band happens
  /// to cover, so the same music would clear the thresholds at 48 kHz and fail to
  /// at 44.1 kHz. A mean is comparable across sample rates and across the scene's
  /// coarser spectrum, which is what makes its thresholds transferable at all.
  /// </summary>
  public static double BandEnergy(ReadOnlySpan<int> spectrum, int low, int high) {
    int from = Math.Clamp(low, 0, spectrum.Length);
    int to = Math.Clamp(high, from, spectrum.Length);
    if(to == from) {
      return 0;
    }

    double total = 0;
    for(int bin = from; bin < to; bin++) {
      double level = spectrum[bin] / 255.0;
      total += level * level;
    }

    return total / (to - from);
  }

  /// <summary>
  /// Reduces the spectrum to the display columns the protocol sends.
  ///
  /// Log-spaced, because an octave is a constant ratio and a linear spectrum
  /// crams every octave below 1 kHz into the leftmost twentieth of the picture:
  /// the bass, which is exactly what the detector is triggering on and what the
  /// picture exists to check. The value taken is the loudest bin in the column and
  /// not the average, so a narrow peak stays visible after 1024 bins have been
  /// folded into 64 - a visualizer read as an instrument must not average away the
  /// thing being looked for.
  /// </summary>
  public static int[] DisplayColumns(ReadOnlySpan<int> spectrum, int sampleRate) {
    int[] columns = new int[AudioConstants.DisplayBins];
    if(sampleRate <= 0) {
      return columns;
    }

    double ratio = AudioConstants.DisplayHighHz / AudioConstants.DisplayLowHz;

    for(int column = 0; column < columns.Length; column++) {
      double lowHz = AudioConstants.DisplayLowHz
        * Math.Pow(ratio, (double)column / columns.Length);
      double highHz = AudioConstants.DisplayLowHz
        * Math.Pow(ratio, (double)(column + 1) / columns.Length);

      (int low, int high) = BandRange(lowHz, highHz, sampleRate);
      high = Math.Min(high, spectrum.Length);

      int loudest = 0;
      for(int bin = low; bin < high; bin++) {
        if(spectrum[bin] > loudest) {
          loudest = spectrum[bin];
        }
      }

      columns[column] = loudest;
    }

    return columns;
  }
}
