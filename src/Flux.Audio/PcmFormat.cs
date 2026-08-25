using System.Buffers.Binary;

namespace Flux.Audio;

/// <summary>
/// The shape of the bytes a capture device hands over, reduced to the three
/// things the conversion needs.
///
/// A type of its own rather than NAudio's <c>WaveFormat</c>, so the conversion is
/// pure arithmetic over a byte span and can be tested against a handful of
/// literal bytes instead of against a sound card.
/// </summary>
/// <param name="Channels">Interleaved channels per frame.</param>
/// <param name="BitsPerSample">Width of one sample.</param>
/// <param name="IsFloat">Whether samples are IEEE floats rather than signed integers.</param>
internal readonly record struct PcmFormat(int Channels, int BitsPerSample, bool IsFloat) {
  public int BytesPerSample => this.BitsPerSample / 8;

  public int BytesPerFrame => this.BytesPerSample * this.Channels;

  /// <summary>
  /// Whether <see cref="PcmDownmix"/> can read this format.
  ///
  /// The list is the widths WASAPI actually produces here: 32-bit float in shared
  /// mode, and the integer widths a pro interface offers in exclusive mode. An
  /// unsupported format is answered by capturing nothing rather than by
  /// misreading bytes into a spectrum that looks like noise and cannot be
  /// explained.
  /// </summary>
  public bool IsSupported =>
    this.Channels > 0 && (this.IsFloat ? this.BitsPerSample == 32 : this.BitsPerSample is 16 or 24 or 32);
}

/// <summary>
/// What one capture callback contained.
///
/// The counts travel together with the sums because a level is only meaningful
/// against the number of samples it came from, and because
/// <see cref="AudioDeviceState.DigitalSilence"/> is a statement about every
/// sample in a window rather than about an average that rounded to zero.
/// </summary>
/// <param name="Frames">Mono samples written.</param>
/// <param name="Samples">Individual channel samples read.</param>
/// <param name="NonZeroSamples">How many of them were not exactly zero.</param>
/// <param name="SumSquares">Sum of squares of every sample read, for an RMS.</param>
/// <param name="Peak">Loudest single sample, as a magnitude.</param>
internal readonly record struct PcmBlock(
  int Frames,
  long Samples,
  long NonZeroSamples,
  double SumSquares,
  double Peak);

/// <summary>
/// Turns interleaved device bytes into mono samples, and measures them on the way
/// past.
///
/// Mono because everything downstream is: an FFT of the left channel would miss a
/// kick panned right, and running two of them would double the cost to answer the
/// same question. Measured here rather than afterwards because the samples are
/// already in registers, and because the measurement wants the channels
/// separately: a level taken after the downmix reports silence for a signal that
/// is merely out of phase between two channels.
/// </summary>
internal static class PcmDownmix {
  private const float Int16Scale = 32768f;
  private const float Int24Scale = 8388608f;
  private const float Int32Scale = 2147483648f;

  /// <summary>
  /// Converts as many whole frames as fit in <paramref name="mono"/>.
  /// </summary>
  /// <returns>
  /// What was converted. <see cref="PcmBlock.Frames"/> is zero for an
  /// unsupported format, which is the same answer a callback with no whole frame
  /// in it gets: nothing usable arrived.
  /// </returns>
  public static PcmBlock ToMono(ReadOnlySpan<byte> pcm, PcmFormat format, Span<float> mono) {
    if(!format.IsSupported) {
      return default;
    }

    int bytesPerFrame = format.BytesPerFrame;
    int frames = Math.Min(pcm.Length / bytesPerFrame, mono.Length);

    long samples = 0;
    long nonZero = 0;
    double sumSquares = 0;
    double peak = 0;

    for(int frame = 0; frame < frames; frame++) {
      int offset = frame * bytesPerFrame;
      float sum = 0;

      for(int channel = 0; channel < format.Channels; channel++) {
        float sample = ReadSample(pcm[(offset + (channel * format.BytesPerSample))..], format);

        sum += sample;
        samples++;
        if(sample != 0f) {
          nonZero++;
        }

        sumSquares += sample * (double)sample;
        double magnitude = Math.Abs(sample);
        if(magnitude > peak) {
          peak = magnitude;
        }
      }

      mono[frame] = sum / format.Channels;
    }

    return new PcmBlock(frames, samples, nonZero, sumSquares, peak);
  }

  /// <summary>
  /// Reads one sample as -1..1.
  ///
  /// A non-finite float is read as zero, which is not defensive tidiness. A single
  /// NaN reaching the ring buffer turns every FFT that later includes it into NaN
  /// bins, so one corrupt sample would silently kill beat detection until the
  /// device is reopened - and nothing would point at the sample that did it.
  /// </summary>
  private static float ReadSample(ReadOnlySpan<byte> sample, PcmFormat format) {
    if(format.IsFloat) {
      float value = BinaryPrimitives.ReadSingleLittleEndian(sample);
      return float.IsFinite(value) ? value : 0f;
    }

    return format.BitsPerSample switch {
      16 => BinaryPrimitives.ReadInt16LittleEndian(sample) / Int16Scale,

      // Little-endian, low byte first, and the top byte read as signed so the
      // sign extends: reading it unsigned turns every negative sample into a
      // large positive one, which sounds like violent distortion and looks like
      // a spectrum with no shape.
      24 => (sample[0] | (sample[1] << 8) | ((sbyte)sample[2] << 16)) / Int24Scale,
      _ => BinaryPrimitives.ReadInt32LittleEndian(sample) / Int32Scale,
    };
  }
}
