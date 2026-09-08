using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Flux.Audio;

/// <summary>
/// Writes mono 32-bit float samples as a WAVE file.
///
/// Hand-rolled rather than taken from NAudio, and the reason is a design invariant
/// rather than a preference: <see cref="AudioCaptureSession"/> is the only file in
/// this project that names a NAudio type, which is what makes everything else
/// testable without a sound card. A header is forty-four bytes of arithmetic, so
/// buying it with a second NAudio reference would cost the invariant and save
/// nothing.
///
/// No conversion happens here. The samples arrive as the floats the analyzer reads
/// and land in the file unchanged, because the file exists to answer questions
/// about the signal the detector saw - a recording that had been normalized,
/// dithered or requantized on the way out would answer them about itself.
/// </summary>
internal sealed class WaveFile: IDisposable {
  /// <summary>WAVE format tag 3, <c>WAVE_FORMAT_IEEE_FLOAT</c>.</summary>
  private const ushort IeeeFloat = 3;

  private const ushort Channels = 1;
  private const ushort BitsPerSample = 32;

  /// <summary>Bytes before the sample data: the RIFF, fmt and data headers.</summary>
  private const int HeaderBytes = 44;

  /// <summary>Offset of the RIFF chunk size, which counts everything after it.</summary>
  private const int RiffSizeOffset = 4;

  /// <summary>Offset of the data chunk size, which counts the samples alone.</summary>
  private const int DataSizeOffset = 40;

  private readonly Stream stream;
  private readonly int sampleRate;

  private long samples;
  private bool closed;

  /// <summary>
  /// Takes a seekable stream and writes the header immediately, so a file that is
  /// never written to again is still a valid empty recording.
  /// </summary>
  public WaveFile(Stream stream, int sampleRate) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);

    if(!stream.CanSeek) {
      throw new ArgumentException("A WAVE file's sizes are patched after the samples, so the stream must seek.", nameof(stream));
    }

    this.stream = stream;
    this.sampleRate = sampleRate;

    this.WriteHeader();
  }

  /// <summary>Samples written so far, which is the recording's length in frames.</summary>
  public long Samples => this.samples;

  /// <summary>
  /// Reads back a recording this class wrote.
  ///
  /// Deliberately narrow: mono 32-bit float, which is the only thing
  /// <see cref="AudioRecorder"/> produces. A general WAVE reader would be a
  /// different job with a different risk profile, and the one job here is replaying
  /// Flux's own recordings through Flux's own analysis.
  ///
  /// Chunks are walked rather than assumed at fixed offsets. This class writes a
  /// canonical forty-four byte header, but a file that has been through any other
  /// tool may well have gained a <c>LIST</c> chunk, and reading the samples from a
  /// fixed offset would then return metadata as audio.
  /// </summary>
  public static (int SampleRate, float[] Samples) Read(string path) {
    byte[] bytes = File.ReadAllBytes(path);

    if(bytes.Length < 12 || Tag(bytes, 0) != "RIFF" || Tag(bytes, 8) != "WAVE") {
      throw new InvalidDataException($"{path} is not a WAVE file.");
    }

    int rate = 0;
    int position = 12;

    while(position + 8 <= bytes.Length) {
      string tag = Tag(bytes, position);
      uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4));
      int body = position + 8;

      if(tag == "fmt ") {
        // Sixteen bytes is the shape this class writes and the smallest fmt chunk
        // that can hold the fields read below. Checked so a truncated file fails
        // the way every other malformation does, as a statement about the file,
        // rather than as an index exception about this method.
        if(size < 16 || body + 16 > bytes.Length) {
          throw new InvalidDataException($"{path} has a truncated fmt chunk.");
        }

        ushort format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body));
        ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 2));
        ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(body + 14));

        if(format != IeeeFloat || channels != Channels || bits != BitsPerSample) {
          throw new InvalidDataException(
            $"{path} is {bits}-bit format {format} on {channels} channel(s). "
              + "Only the mono 32-bit float recordings Flux writes can be replayed.");
        }

        rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(body + 4));
      } else if(tag == "data") {
        if(rate <= 0) {
          throw new InvalidDataException($"{path} holds samples before it says what rate they are at.");
        }

        // Trusting the file's length over the header's size field, because a
        // recording killed mid-flush has a size field one second stale - and the
        // samples past it are perfectly good audio.
        int available = bytes.Length - body;
        int dataBytes = Math.Min((int)Math.Min(size, int.MaxValue), available);

        float[] samples = new float[dataBytes / (BitsPerSample / 8)];
        MemoryMarshal.Cast<byte, float>(bytes.AsSpan(body, samples.Length * (BitsPerSample / 8))).CopyTo(samples);

        return (rate, samples);
      }

      // Chunks are padded to an even length, and the pad byte is not in the size.
      position = body + (int)size + ((size & 1) == 1 ? 1 : 0);
    }

    throw new InvalidDataException($"{path} has no data chunk.");
  }

  /// <summary>
  /// Appends samples.
  ///
  /// The floats go out as little-endian IEEE 754, which on every platform this
  /// project targets is their in-memory layout - so the copy is a reinterpret
  /// rather than a conversion loop.
  /// </summary>
  public void Write(ReadOnlySpan<float> mono) {
    if(this.closed || mono.Length == 0) {
      return;
    }

    this.stream.Write(MemoryMarshal.AsBytes(mono));
    this.samples += mono.Length;
  }

  /// <summary>
  /// Patches the two size fields and flushes.
  ///
  /// Called periodically and not only at the end, which is the difference between
  /// an interrupted recording being a file and being rubble: a WAVE reader trusts
  /// the size fields, so a run that was killed rather than stopped would otherwise
  /// leave a header claiming zero samples in front of ninety megabytes of them.
  /// Unlike a truncated JSON line, this is not visible on inspection - the file
  /// simply reads as empty.
  /// </summary>
  public void Flush() {
    if(this.closed) {
      return;
    }

    long dataBytes = this.samples * (BitsPerSample / 8);
    long position = this.stream.Position;

    this.PatchSize(RiffSizeOffset, HeaderBytes - 8 + dataBytes);
    this.PatchSize(DataSizeOffset, dataBytes);

    this.stream.Position = position;
    this.stream.Flush();
  }

  public void Dispose() {
    if(this.closed) {
      return;
    }

    this.Flush();
    this.closed = true;
    this.stream.Dispose();
  }

  private static string Tag(byte[] bytes, int offset) {
    return System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
  }

  private void WriteHeader() {
    Span<byte> header = stackalloc byte[HeaderBytes];
    int byteRate = this.sampleRate * Channels * (BitsPerSample / 8);

    "RIFF"u8.CopyTo(header[..4]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], HeaderBytes - 8);
    "WAVE"u8.CopyTo(header[8..12]);

    "fmt "u8.CopyTo(header[12..16]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[16..], 16);
    BinaryPrimitives.WriteUInt16LittleEndian(header[20..], IeeeFloat);
    BinaryPrimitives.WriteUInt16LittleEndian(header[22..], Channels);
    BinaryPrimitives.WriteUInt32LittleEndian(header[24..], (uint)this.sampleRate);
    BinaryPrimitives.WriteUInt32LittleEndian(header[28..], (uint)byteRate);
    BinaryPrimitives.WriteUInt16LittleEndian(header[32..], Channels * (BitsPerSample / 8));
    BinaryPrimitives.WriteUInt16LittleEndian(header[34..], BitsPerSample);

    "data"u8.CopyTo(header[36..40]);
    BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0);

    this.stream.Write(header);
  }

  /// <summary>
  /// Writes one size field, clamped to what the format can express.
  ///
  /// WAVE sizes are unsigned 32-bit, so the format runs out at four gigabytes -
  /// about six hours of this signal. Clamping rather than throwing: a recording
  /// that has just passed six hours is not a recording anybody is about to lose to
  /// an exception, and every sample before the limit stays readable.
  /// </summary>
  private void PatchSize(int offset, long size) {
    Span<byte> field = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)Math.Min(size, uint.MaxValue));

    this.stream.Position = offset;
    this.stream.Write(field);
  }
}
