using Xunit;

namespace Flux.Audio.Tests;

public class PcmDownmixTests {
  [Fact]
  public void AveragesChannelsAndMeasuresThemSeparately() {
    // Half scale left, half scale right, in opposite phase. The mono sample is
    // zero and the signal is not silent, which is exactly why the measurement is
    // taken before the downmix rather than after it.
    byte[] pcm = [.. Int16(16384), .. Int16(-16384)];
    float[] mono = new float[1];

    PcmBlock block = PcmDownmix.ToMono(pcm, new PcmFormat(Channels: 2, BitsPerSample: 16, IsFloat: false), mono);

    Assert.Equal(1, block.Frames);
    Assert.Equal(2L, block.Samples);
    Assert.Equal(2L, block.NonZeroSamples);
    Assert.Equal(0.5, block.Peak, precision: 6);
    Assert.Equal(0, mono[0], precision: 6);
  }

  [Fact]
  public void ReadsThirtyTwoBitFloatsAndDropsNonFiniteOnes() {
    // A single NaN in a ring buffer poisons every FFT window that later contains
    // it, so it is read as zero and counted as one.
    byte[] pcm = [.. Float(0.25f), .. Float(float.NaN), .. Float(1.5f)];
    float[] mono = new float[3];

    PcmBlock block = PcmDownmix.ToMono(pcm, new PcmFormat(1, 32, IsFloat: true), mono);

    Assert.Equal(3, block.Frames);
    Assert.Equal(2L, block.NonZeroSamples);
    Assert.Equal([0.25f, 0f, 1.5f], mono);

    // Not clamped here: the level meter is what promises 0 to 1, and clamping a
    // sample would put a flat top on a waveform the analyzer has to transform.
    Assert.Equal(1.5, block.Peak, precision: 6);
  }

  [Fact]
  public void SignExtendsTwentyFourBitSamples() {
    // The trap this pins: reading the top byte unsigned turns full-scale negative
    // into full-scale positive, which sounds like distortion and shows up as a
    // spectrum with no shape at all.
    byte[] pcm = [0x00, 0x00, 0x80, 0x00, 0x00, 0x40];
    float[] mono = new float[2];

    PcmDownmix.ToMono(pcm, new PcmFormat(1, 24, IsFloat: false), mono);

    Assert.Equal(-1f, mono[0], precision: 6);
    Assert.Equal(0.5f, mono[1], precision: 6);
  }

  [Fact]
  public void ReadsThirtyTwoBitIntegers() {
    byte[] pcm = [.. Int32(int.MinValue), .. Int32(1 << 30)];
    float[] mono = new float[2];

    PcmDownmix.ToMono(pcm, new PcmFormat(1, 32, IsFloat: false), mono);

    Assert.Equal(-1f, mono[0], precision: 6);
    Assert.Equal(0.5f, mono[1], precision: 6);
  }

  [Theory]
  [InlineData(2, 8, false)]
  [InlineData(2, 16, true)]
  [InlineData(0, 32, true)]
  public void ConvertsNothingForAFormatItCannotRead(int channels, int bits, bool isFloat) {
    // Zero channels is in here for a reason beyond completeness: it is the only
    // input that would divide by a zero frame size, and a device reporting it is
    // less unlikely than it sounds mid-driver-install.
    PcmBlock block = PcmDownmix.ToMono(new byte[16], new PcmFormat(channels, bits, isFloat), new float[4]);

    Assert.Equal(0, block.Frames);
    Assert.Equal(0L, block.Samples);
  }

  [Fact]
  public void IgnoresATrailingPartialFrame() {
    byte[] pcm = new byte[5];

    PcmBlock block = PcmDownmix.ToMono(pcm, new PcmFormat(2, 16, IsFloat: false), new float[4]);

    Assert.Equal(1, block.Frames);
  }

  [Fact]
  public void ConvertsOnlyWhatFitsInTheDestination() {
    byte[] pcm = [.. Float(1f), .. Float(1f), .. Float(1f)];
    float[] mono = new float[2];

    PcmBlock block = PcmDownmix.ToMono(pcm, new PcmFormat(1, 32, IsFloat: true), mono);

    Assert.Equal(2, block.Frames);
    Assert.Equal(2L, block.Samples);
  }

  private static byte[] Int16(short value) => BitConverter.GetBytes(value);

  private static byte[] Int32(int value) => BitConverter.GetBytes(value);

  private static byte[] Float(float value) => BitConverter.GetBytes(value);
}
