using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Buffers.Binary;
using Xunit;

namespace Flux.Audio.Tests;

public class WaveFileTests {
  [Fact]
  public void WritesAHeaderAReaderCanTrust() {
    // Every field here is one another program reads to decide what the bytes mean,
    // and getting one wrong produces a file that opens and sounds like noise rather
    // than a file that fails to open.
    using MemoryStream stream = new MemoryStream();

    using(WaveFile file = new WaveFile(stream, 48_000)) {
      file.Write([0.5f, -0.5f]);
    }

    byte[] bytes = stream.ToArray();

    Assert.Equal("RIFF", Tag(bytes, 0));
    Assert.Equal("WAVE", Tag(bytes, 8));
    Assert.Equal("fmt ", Tag(bytes, 12));
    Assert.Equal("data", Tag(bytes, 36));

    Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(16)));
    Assert.Equal(3, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20)));
    Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(22)));
    Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
    Assert.Equal(192_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
    Assert.Equal(4, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(32)));
    Assert.Equal(32, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(34)));
  }

  [Fact]
  public void CarriesSamplesThroughUnchanged() {
    // The reason this file exists at all. A recording is evidence about a signal, so
    // a sample that arrived as one number and left as another would make it evidence
    // about the recorder.
    float[] written = [0.0f, 1.0f, -1.0f, 0.123456789f, -0.000031f, float.Epsilon];
    using MemoryStream stream = new MemoryStream();

    using(WaveFile file = new WaveFile(stream, 44_100)) {
      file.Write(written);
      Assert.Equal(written.Length, file.Samples);
    }

    Assert.Equal(written, Samples(stream.ToArray()));
  }

  [Fact]
  public void StaysReadableWhenTheRunIsKilledRatherThanStopped() {
    // The case that earns the periodic flush. A WAVE reader believes the size
    // fields, so a header still claiming zero would present a full recording as an
    // empty one - and unlike a half-written JSON line, nothing about the file looks
    // wrong.
    using MemoryStream stream = new MemoryStream();
    WaveFile file = new WaveFile(stream, 48_000);

    file.Write([0.25f, 0.5f, 0.75f]);
    file.Flush();

    // No Dispose: the process died here.
    byte[] bytes = stream.ToArray();

    Assert.Equal(36u + 12u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
    Assert.Equal(12u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40)));
    Assert.Equal([0.25f, 0.5f, 0.75f], Samples(bytes));
  }

  [Fact]
  public void KeepsWritingAfterAFlush() {
    // The flush repositions the stream to patch two sizes, so the bug it invites is
    // an append that lands back over the header.
    using MemoryStream stream = new MemoryStream();

    using(WaveFile file = new WaveFile(stream, 48_000)) {
      file.Write([1.0f]);
      file.Flush();
      file.Write([2.0f]);
      file.Flush();
      file.Write([3.0f]);
    }

    Assert.Equal([1.0f, 2.0f, 3.0f], Samples(stream.ToArray()));
  }

  [Fact]
  public void RefusesAStreamThatCannotSeek() {
    // Refused at construction rather than at the first flush, because the flush
    // happens on the writer thread a minute into a recording nobody is watching.
    using MemoryStream inner = new MemoryStream();
    using ForwardOnlyStream forward = new ForwardOnlyStream(inner);

    Assert.Throws<ArgumentException>(() => new WaveFile(forward, 48_000));
  }

  [Fact]
  public async Task RecordsWhatTheCaptureCallbackHandedOver() {
    // Two blocks rather than one, because the queue between the callback and the
    // writer is the part that can reorder or lose them.
    string path = TempPath();
    AudioRecorder recorder = new AudioRecorder(
      Options.Create(new AudioOptions { RecordAudioPath = path }),
      NullLogger<AudioRecorder>.Instance);

    try {
      await recorder.StartAsync(TestContext.Current.CancellationToken);

      recorder.Write([0.1f, -0.2f, 0.3f], 48_000);
      recorder.Write([0.25f, -0.5f], 48_000);

      await recorder.StopAsync(TestContext.Current.CancellationToken);

      byte[] bytes = File.ReadAllBytes(path);

      Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
      Assert.Equal([0.1f, -0.2f, 0.3f, 0.25f, -0.5f], Samples(bytes));
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public async Task RecordsNothingWhenNoPathIsSet() {
    // The normal case: the tap is wired into the capture callback unconditionally,
    // so being off has to cost nothing and throw nothing.
    AudioRecorder recorder = new AudioRecorder(
      Options.Create(new AudioOptions()),
      NullLogger<AudioRecorder>.Instance);

    await recorder.StartAsync(TestContext.Current.CancellationToken);
    recorder.Write([0.1f, 0.2f], 48_000);
    await recorder.StopAsync(TestContext.Current.CancellationToken);
  }

  [Fact]
  public async Task StopsRatherThanSplicingASecondSampleRate() {
    // A WAVE file carries one rate in its header, so audio captured at a second one
    // would be described by a header that is wrong about most of the file. Stopping
    // keeps the part that is true.
    string path = TempPath();
    AudioRecorder recorder = new AudioRecorder(
      Options.Create(new AudioOptions { RecordAudioPath = path }),
      NullLogger<AudioRecorder>.Instance);

    try {
      await recorder.StartAsync(TestContext.Current.CancellationToken);

      recorder.Write([0.5f], 48_000);
      recorder.Write([0.25f], 44_100);

      await recorder.StopAsync(TestContext.Current.CancellationToken);

      byte[] bytes = File.ReadAllBytes(path);

      Assert.Equal(48_000u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(24)));
      Assert.Equal([0.5f], Samples(bytes));
    } finally {
      File.Delete(path);
    }
  }

  private static string Tag(byte[] bytes, int offset) {
    return System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
  }

  private static float[] Samples(byte[] bytes) {
    uint dataBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(40));
    float[] samples = new float[dataBytes / 4];

    for(int index = 0; index < samples.Length; index++) {
      samples[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(44 + (index * 4)));
    }

    return samples;
  }

  private static string TempPath() {
    return Path.Combine(Path.GetTempPath(), $"flux-audio-{Guid.NewGuid():N}.wav");
  }

  /// <summary>A stream that reports it cannot seek, which is what a pipe looks like.</summary>
  private sealed class ForwardOnlyStream: Stream {
    private readonly Stream inner;

    public ForwardOnlyStream(Stream inner) {
      this.inner = inner;
    }

    public override bool CanRead => this.inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => this.inner.CanWrite;

    public override long Length => this.inner.Length;

    public override long Position {
      get => this.inner.Position;
      set => throw new NotSupportedException();
    }

    public override void Flush() => this.inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => this.inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => this.inner.Write(buffer, offset, count);
  }
}
