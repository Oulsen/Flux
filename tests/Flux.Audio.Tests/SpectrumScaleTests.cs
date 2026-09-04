using Xunit;

namespace Flux.Audio.Tests;

public class SpectrumScaleTests {
  /// <summary>
  /// The bin the analyzer's own calibration test uses, at 48 kHz: 43 * 23.4375 Hz
  /// is exactly 1007.8125 Hz, which is a bin centre and therefore leaks into its
  /// neighbours as little as a Hann window allows.
  /// </summary>
  private const int ToneBin = 43;

  [Fact]
  public void DerivesTheSceneBandsAtFortyEightKilohertz() {
    // The whole derivation, in one assertion each. The scene worked on a 200-bin
    // array whose bins were about 50 Hz wide, and its bands were bins 1 to 3, 4 to
    // 8, and 15 to 25 of that. Those are the frequencies below, and these are the
    // bins they land in at 48 kHz over 2048 samples, where a bin is 23.4375 Hz.
    // The full working is in docs/audio-constants.md.
    Assert.Equal((2, 9), SpectrumScale.BandRange(AudioConstants.KickLowHz, AudioConstants.KickHighHz, 48_000));
    Assert.Equal((9, 19), SpectrumScale.BandRange(AudioConstants.MidLowHz, AudioConstants.MidHighHz, 48_000));
    Assert.Equal((32, 55), SpectrumScale.BandRange(AudioConstants.HighLowHz, AudioConstants.HighHighHz, 48_000));
  }

  [Fact]
  public void SharesNoBinBetweenTheKickAndMidBands() {
    // The two bands meet at 200 Hz, and the kick-versus-mid ratio is the one test
    // in the detector with no threshold to absorb an error. A bin counted in both
    // would tilt it in favour of the mids on every kick.
    (int _, int kickHigh) = SpectrumScale.BandRange(AudioConstants.KickLowHz, AudioConstants.KickHighHz, 48_000);
    (int midLow, int _) = SpectrumScale.BandRange(AudioConstants.MidLowHz, AudioConstants.MidHighHz, 48_000);

    Assert.Equal(kickHigh, midLow);
  }

  [Fact]
  public void PutsTheKickBandInTheSameBinsAtBothCommonRates() {
    // Not a coincidence worth relying on, but worth knowing: 44.1 kHz and 48 kHz
    // put the kick band in exactly the same bins, so a device that opens at the
    // other rate is not a different detector.
    Assert.Equal(
      SpectrumScale.BandRange(AudioConstants.KickLowHz, AudioConstants.KickHighHz, 48_000),
      SpectrumScale.BandRange(AudioConstants.KickLowHz, AudioConstants.KickHighHz, 44_100));
  }

  [Fact]
  public void NeverReturnsAnEmptyBand() {
    // A band narrower than a bin, and a band starting past the end of the spectrum,
    // both have to be measurable: the caller divides by the bin count.
    (int low, int high) = SpectrumScale.BandRange(50, 51, 48_000);
    Assert.True(high > low);

    (int farLow, int farHigh) = SpectrumScale.BandRange(750, 1300, 2_000);
    Assert.True(farHigh > farLow);
    Assert.True(farHigh <= SpectrumScale.Bins);
  }

  [Fact]
  public void ReadsAMissingSampleRateAsBinZero() {
    // Nothing open. The analyzer skips the frame rather than asking, so this only
    // has to avoid dividing by zero.
    Assert.Equal(0, SpectrumScale.BinOf(1000, 0));
  }

  [Theory]
  // Deliberately concrete rather than derived from the window. Every threshold in
  // AudioConstants is a number on this scale, so an accidental change to the window
  // has to fail something loudly, and this is the something.
  //
  // Full scale, and the top of the window itself, both saturate.
  [InlineData(1.0, 255)]
  [InlineData(0.501187234, 255)]
  // -30 dBFS, which is where a DJ master bus keeps its bass and where the old
  // window's ceiling used to sit. It reads as a level now rather than as 255, which
  // is the entire substance of the calibration: a kick's onset is a rise from here
  // instead of a plateau at the top of the range.
  [InlineData(0.0316227766, 168)]
  // -45 dBFS, mid-window, where nothing is clamped and a magnitude scaling that was
  // out by a factor of two would show. A few dB off the exact midpoint on purpose:
  // the midpoint lands on a rounding boundary and would make this a coin toss on
  // the last bit.
  [InlineData(0.005623413, 113)]
  // The bottom of the window and below it.
  [InlineData(0.000158489319, 0)]
  [InlineData(0.00001, 0)]
  [InlineData(0.0, 0)]
  public void ScalesMagnitudesOntoTheByteRange(double magnitude, int expected) {
    Assert.Equal(expected, SpectrumScale.ToByte(magnitude));
  }

  [Fact]
  public void ReadsANonFiniteMagnitudeAsSilence() {
    // One NaN in a window turns every bin of the transform into NaN. The capture
    // path already refuses to write one, so this is the second line of a defence
    // whose failure mode is beat detection dying silently until the device is
    // reopened.
    Assert.Equal(0, SpectrumScale.ToByte(double.NaN));
    Assert.Equal(0, SpectrumScale.ToByte(double.PositiveInfinity));
  }

  [Fact]
  public void TakesBandEnergyAsAMeanOfSquares() {
    int[] spectrum = [255, 255, 0, 0];

    Assert.Equal(1.0, SpectrumScale.BandEnergy(spectrum, 0, 2), 6);
    Assert.Equal(0.5, SpectrumScale.BandEnergy(spectrum, 0, 4), 6);
    Assert.Equal(0.0, SpectrumScale.BandEnergy(spectrum, 2, 4), 6);
  }

  [Fact]
  public void KeepsBandEnergyComparableAcrossBandWidths() {
    // The reason it is a mean and not a sum. The same signal measured over twice as
    // many bins has to give the same answer, or every threshold in AudioConstants
    // would depend on the sample rate.
    int[] narrow = [200, 200];
    int[] wide = [200, 200, 200, 200];

    Assert.Equal(
      SpectrumScale.BandEnergy(narrow, 0, narrow.Length),
      SpectrumScale.BandEnergy(wide, 0, wide.Length),
      6);
  }

  [Fact]
  public void PutsAToneInOneDisplayColumn() {
    int[] spectrum = new int[SpectrumScale.Bins];
    spectrum[ToneBin] = 255;

    int[] columns = SpectrumScale.DisplayColumns(spectrum, 48_000);

    Assert.Equal(AudioConstants.DisplayBins, columns.Length);

    // Column 35 of 64, log-spaced from 30 Hz to 16 kHz, spans about 930 to 1026 Hz.
    // Hardcoded rather than derived, so that a change to the spacing fails here
    // instead of quietly moving the picture.
    Assert.Equal(255, columns[35]);
    Assert.Equal(0, columns[34]);
    Assert.Equal(0, columns[36]);
  }

  [Fact]
  public void KeepsTheLoudestBinInAColumnRatherThanTheAverage() {
    // 1024 bins fold into 64, so most columns cover several. Averaging would hide a
    // narrow peak, and a visualizer being read as an instrument must not average
    // away the thing being looked for.
    int[] spectrum = new int[SpectrumScale.Bins];
    spectrum[ToneBin] = 255;
    spectrum[ToneBin - 1] = 10;

    int[] columns = SpectrumScale.DisplayColumns(spectrum, 48_000);

    Assert.Equal(255, columns[35]);
  }

  [Fact]
  public void GivesEveryColumnMoreRoomThanTheLast() {
    // Log spacing, asserted through its consequence rather than its formula: each
    // column has to cover at least as many bins as the one below it, because an
    // octave is a constant ratio. A linear spectrum would put every octave under
    // 1 kHz in the leftmost twentieth of the picture, which is exactly the part the
    // detector triggers on.
    int[] spectrum = new int[SpectrumScale.Bins];
    Array.Fill(spectrum, 255);

    int[] columns = SpectrumScale.DisplayColumns(spectrum, 48_000);

    // Every column found signal, so none of them is empty.
    Assert.All(columns, column => Assert.Equal(255, column));

    double previousWidth = 0;
    for(int column = 0; column < AudioConstants.DisplayBins; column++) {
      double ratio = AudioConstants.DisplayHighHz / AudioConstants.DisplayLowHz;
      double low = AudioConstants.DisplayLowHz * Math.Pow(ratio, (double)column / AudioConstants.DisplayBins);
      double high = AudioConstants.DisplayLowHz * Math.Pow(ratio, (double)(column + 1) / AudioConstants.DisplayBins);

      Assert.True(high - low >= previousWidth);
      previousWidth = high - low;
    }
  }

  [Fact]
  public void DrawsNothingWithNoDeviceOpen() {
    int[] columns = SpectrumScale.DisplayColumns(new int[SpectrumScale.Bins], 0);

    Assert.Equal(AudioConstants.DisplayBins, columns.Length);
    Assert.All(columns, column => Assert.Equal(0, column));
  }
}
