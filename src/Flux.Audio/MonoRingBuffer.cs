namespace Flux.Audio;

/// <summary>
/// The handover between the capture callback and the analyzer: the most recent
/// mono samples, overwritten in place.
///
/// A ring and not a queue, because the two ends are allowed to disagree about
/// speed and only one answer is acceptable when they do. The callback must never
/// wait - a blocked WASAPI callback is a glitch in someone's audio - and an
/// analyzer that fell behind wants the newest audio rather than a backlog of
/// stale audio it would then be even further behind on. Dropping the oldest is
/// the same trade the socket queue makes, for the same reason.
///
/// The lock is deliberate rather than a lock-free scheme. It is held for a couple
/// of memory copies between exactly two threads at 100 Hz and 60 Hz, so it is
/// effectively never contended, and a torn read of an audio buffer is not a bug
/// anyone would enjoy finding.
/// </summary>
internal sealed class MonoRingBuffer {
  private readonly object gate = new object();
  private readonly float[] samples;

  /// <summary>Where the next sample goes; the oldest kept sample is here too.</summary>
  private int next;

  private long written;

  public MonoRingBuffer(int capacity) {
    ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);

    this.samples = new float[capacity];
  }

  /// <summary>How many samples the buffer holds at most.</summary>
  public int Capacity => this.samples.Length;

  /// <summary>
  /// Every sample ever offered, including those since overwritten. A counter for
  /// the diagnostics row, and the way <see cref="CopyLatest"/> knows whether the
  /// buffer has filled far enough to answer at all.
  /// </summary>
  public long Written {
    get {
      lock(this.gate) {
        return this.written;
      }
    }
  }

  /// <summary>
  /// Appends samples, keeping only the newest <see cref="Capacity"/> of them.
  /// </summary>
  public void Write(ReadOnlySpan<float> block) {
    lock(this.gate) {
      // A block larger than the whole buffer can only contribute its tail. This
      // does not happen at any sane buffer size, and handling it here is cheaper
      // than a caller having to know the capacity.
      ReadOnlySpan<float> tail = block.Length <= this.samples.Length
        ? block
        : block[^this.samples.Length..];

      int untilWrap = Math.Min(tail.Length, this.samples.Length - this.next);
      tail[..untilWrap].CopyTo(this.samples.AsSpan(this.next));
      if(untilWrap < tail.Length) {
        tail[untilWrap..].CopyTo(this.samples.AsSpan(0));
      }

      this.next = (this.next + tail.Length) % this.samples.Length;

      // Counts what was offered, not what was kept: this is "how much audio has
      // been captured", which is the question both callers actually ask.
      this.written += block.Length;
    }
  }

  /// <summary>
  /// Copies the newest samples, oldest first, filling
  /// <paramref name="destination"/> exactly.
  /// </summary>
  /// <returns>
  /// False when fewer samples than that have ever been written, or when more were
  /// asked for than the buffer can hold. Nothing is copied in that case: a
  /// partially filled FFT window is a window with a step change in it, and the
  /// spectrum of a step change is a smear across every band that looks exactly
  /// like a snare.
  /// </returns>
  public bool CopyLatest(Span<float> destination) {
    lock(this.gate) {
      if(destination.Length > this.samples.Length || this.written < destination.Length) {
        return false;
      }

      int start = ((this.next - destination.Length) % this.samples.Length + this.samples.Length)
        % this.samples.Length;
      int untilWrap = Math.Min(destination.Length, this.samples.Length - start);

      this.samples.AsSpan(start, untilWrap).CopyTo(destination);
      if(untilWrap < destination.Length) {
        this.samples.AsSpan(0, destination.Length - untilWrap).CopyTo(destination[untilWrap..]);
      }

      return true;
    }
  }

  /// <summary>
  /// Forgets everything, for a switch to a different device.
  ///
  /// The counter goes with the samples, so the analyzer waits for a full window of
  /// the new device rather than reading one built from two: audio spliced from two
  /// devices has a discontinuity in it, and the detector would call that a beat.
  /// </summary>
  public void Reset() {
    lock(this.gate) {
      Array.Clear(this.samples);
      this.next = 0;
      this.written = 0;
    }
  }
}
