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

    int[] columns = SpectrumScale.DisplayColumns(spectrum, 48_000, AudioConstants.DisplayBins);

    Assert.Equal(AudioConstants.DisplayBins, columns.Length);

    // Column 71 of 128, log-spaced from 30 Hz to 16 kHz, spans about 977 to 1026 Hz.
    // Hardcoded rather than derived, so that a change to the spacing fails here
    // instead of quietly moving the picture. It was 35 when there were 64 columns,
    // and this test is what caught the doubling.
    Assert.Equal(255, columns[71]);
    Assert.Equal(0, columns[70]);
    Assert.Equal(0, columns[72]);
  }

  [Fact]
  public void KeepsTheLoudestBinInAColumnRatherThanTheAverage() {
    // 1024 bins fold into 128, so a column above about 470 Hz covers several. Averaging
    // would hide a narrow peak, and a visualizer being read as an instrument must not
    // average away the thing being looked for.
    int[] spectrum = new int[SpectrumScale.Bins];
    spectrum[ToneBin] = 255;
    spectrum[ToneBin - 1] = 10;

    int[] columns = SpectrumScale.DisplayColumns(spectrum, 48_000, AudioConstants.DisplayBins);

    Assert.Equal(255, columns[71]);
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

    int[] columns = SpectrumScale.DisplayColumns(spectrum, 48_000, AudioConstants.DisplayBins);

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
    int[] columns = SpectrumScale.DisplayColumns(new int[SpectrumScale.Bins], 0, AudioConstants.DisplayBins);

    Assert.Equal(AudioConstants.DisplayBins, columns.Length);
    Assert.All(columns, column => Assert.Equal(0, column));
  }

  [Fact]
  public void NestsACoarserColumnCountInsideTheFinerOneWhereverABinIsResolved() {
    // The property that makes the band count a free choice rather than a short list:
    // the endpoints are fixed and only the count varies, so the frequency span of
    // column c of 64 is exactly that of columns 2c and 2c+1 of 128, and the loudest of
    // the pair is what the coarser count reports.
    //
    // **It holds only where a column resolves at least one whole bin, and that is not
    // a caveat the prose can drop.** Below about 470 Hz at 128 columns the log spacing
    // asks for columns narrower than the transform's 23.4 Hz, so BandRange's floor
    // gives several neighbours the same single bin and the two counts round to
    // different ones. Neither is more right - both are showing a bin next to their own
    // frequency - but they are not equal, and asserting otherwise is what caught this.
    int[] spectrum = new int[SpectrumScale.Bins];
    for(int bin = 0; bin < spectrum.Length; bin++) {
      spectrum[bin] = bin % 255;
    }

    int[] fine = SpectrumScale.DisplayColumns(spectrum, 48_000, 128);
    int[] coarse = SpectrumScale.DisplayColumns(spectrum, 48_000, 64);

    Assert.Equal(64, coarse.Length);
    Assert.Equal(128, fine.Length);

    double ratio = AudioConstants.DisplayHighHz / AudioConstants.DisplayLowHz;
    int compared = 0;

    for(int column = 0; column < coarse.Length; column++) {
      double lowHz = AudioConstants.DisplayLowHz * Math.Pow(ratio, (double)column / coarse.Length);
      double highHz = AudioConstants.DisplayLowHz * Math.Pow(ratio, (double)(column + 1) / coarse.Length);

      // Only where the column genuinely covers more than one bin, which is to say only
      // where no floor was needed to give it a range at all.
      if(SpectrumScale.BinOf(highHz, 48_000) - SpectrumScale.BinOf(lowHz, 48_000) < 2) {
        continue;
      }

      Assert.Equal(Math.Max(fine[2 * column], fine[(2 * column) + 1]), coarse[column]);
      compared++;
    }

    // Most of the picture, and worth asserting: a guard that skipped everything would
    // make the test above vacuous and it would still pass.
    Assert.True(compared > coarse.Length / 2, $"Only {compared} of {coarse.Length} columns were comparable.");
  }

  [Fact]
  public void RefusesABandCountOutsideTheBounds() {
    // It arrives from a browser, so it is clamped rather than trusted. The web layer
    // rejects an out-of-range request before it gets here; this is the second line of a
    // defence whose failure mode is an array nobody sized.
    int[] spectrum = new int[SpectrumScale.Bins];

    Assert.Equal(SpectrumBands.Min, SpectrumScale.DisplayColumns(spectrum, 48_000, 2).Length);
    Assert.Equal(SpectrumBands.Max, SpectrumScale.DisplayColumns(spectrum, 48_000, 4096).Length);
  }
}
