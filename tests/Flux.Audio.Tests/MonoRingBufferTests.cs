using Xunit;

namespace Flux.Audio.Tests;

public class MonoRingBufferTests {
  [Fact]
  public void ReturnsTheNewestSamplesOldestFirst() {
    MonoRingBuffer ring = new MonoRingBuffer(4);

    ring.Write([1, 2, 3, 4, 5, 6]);

    float[] read = new float[4];
    Assert.True(ring.CopyLatest(read));
    Assert.Equal([3f, 4f, 5f, 6f], read);
    Assert.Equal(6L, ring.Written);
  }

  [Fact]
  public void ReadsAcrossTheWrap() {
    // The case an off-by-one hides in: the newest four samples start before the
    // end of the array and finish after the start of it.
    MonoRingBuffer ring = new MonoRingBuffer(4);

    ring.Write([1, 2, 3]);
    ring.Write([4, 5]);

    float[] read = new float[4];
    Assert.True(ring.CopyLatest(read));
    Assert.Equal([2f, 3f, 4f, 5f], read);
  }

  [Fact]
  public void KeepsOnlyTheTailOfAnOversizedWrite() {
    MonoRingBuffer ring = new MonoRingBuffer(4);

    ring.Write([1, 2, 3, 4, 5, 6, 7, 8, 9, 10]);

    float[] read = new float[4];
    Assert.True(ring.CopyLatest(read));
    Assert.Equal([7f, 8f, 9f, 10f], read);

    // Still counts everything that arrived: this is "how much audio has been
    // captured", not "how much is still in the buffer".
    Assert.Equal(10L, ring.Written);
  }

  [Fact]
  public void RefusesToHalfFillAWindow() {
    MonoRingBuffer ring = new MonoRingBuffer(8);

    ring.Write([1, 2, 3]);

    float[] read = [-1, -1, -1, -1];
    Assert.False(ring.CopyLatest(read));

    // Nothing written, so the caller cannot mistake a partly filled window for a
    // full one. A window with a step change in it transforms into a smear across
    // every band, which is exactly what a snare looks like.
    Assert.Equal([-1f, -1f, -1f, -1f], read);
  }

  [Fact]
  public void RefusesToReadMoreThanItHolds() {
    MonoRingBuffer ring = new MonoRingBuffer(4);

    ring.Write([1, 2, 3, 4, 5, 6]);

    Assert.False(ring.CopyLatest(new float[5]));
  }

  [Fact]
  public void ForgetsEverythingOnReset() {
    MonoRingBuffer ring = new MonoRingBuffer(4);

    ring.Write([1, 2, 3, 4]);
    ring.Reset();

    Assert.Equal(0L, ring.Written);
    Assert.False(ring.CopyLatest(new float[1]));

    ring.Write([9]);

    float[] read = new float[1];
    Assert.True(ring.CopyLatest(read));
    Assert.Equal([9f], read);
  }
}
