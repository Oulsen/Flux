using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Buffers;
using System.Threading.Channels;

namespace Flux.Audio;

/// <summary>
/// Somewhere the captured mono signal can be sent, without the capture path
/// knowing what happens to it.
///
/// An interface rather than a direct reference so the callback stays ignorant: it
/// hands over samples and returns, and whether anything is recording is not its
/// business.
/// </summary>
internal interface IMonoTap {
  /// <summary>
  /// Offers samples, and must not block. Called on the capture thread.
  /// </summary>
  /// <param name="mono">The downmixed samples, valid only for this call.</param>
  /// <param name="sampleRate">The rate the device opened at.</param>
  void Write(ReadOnlySpan<float> mono, int sampleRate);
}

/// <summary>
/// Writes the captured audio to a WAVE file, when asked to.
///
/// This is <see cref="FrameRecorder"/>'s counterpart, and it exists because that
/// one cannot answer the question it was built for. A recorded frame holds band
/// energies, and those are computed from bytes already clamped by
/// <see cref="AudioConstants.SpectrumMaxDb"/> - so on a signal loud enough to
/// reach the top of that window, the recording holds the ceiling rather than the
/// music, and no replay can recover what was above it. Every question about where
/// the window belongs, where the band edges belong, or how large the transform
/// should be is a question about the signal itself, and only the signal answers
/// those.
///
/// The consequence worth knowing: this file is the durable fixture and a frame
/// recording is a derived one. A recording made here stays useful across changes
/// to the analyzer that make every JSONL fixture describe an analyzer that no
/// longer exists.
///
/// Off unless <see cref="AudioOptions.RecordAudioPath"/> says otherwise, because
/// it writes about eleven megabytes a minute for as long as the app runs.
/// </summary>
internal sealed class AudioRecorder: IHostedService, IMonoTap {
  /// <summary>
  /// Blocks the queue holds. A device delivers roughly a hundred a second, so this
  /// is about forty seconds of slack.
  ///
  /// Far more generous than <see cref="FrameRecorder"/>'s, and for the opposite
  /// reason to that one's. A dropped frame is visible in the recording as a gap in
  /// the timestamps; a dropped block of audio is invisible, and silently shortens
  /// the file and shifts every sample after it. So the queue is sized never to
  /// need dropping, and a drop is reported the first time it happens rather than
  /// discovered later in the arithmetic.
  /// </summary>
  private const int QueueCapacity = 4096;

  /// <summary>How often the samples and the header reach the disk.</summary>
  private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

  /// <summary>
  /// How often the length so far is logged.
  ///
  /// Short enough to pace a take by, because that is what it is for: a recording
  /// meant to hold a given stretch of one track is made by a human listening to
  /// the music rather than watching the console, and fifteen seconds is a coarse
  /// enough clock to glance at and fine enough to aim with. It also answers the
  /// only question a silent console leaves open, which is whether audio is still
  /// arriving at all.
  /// </summary>
  private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);

  private readonly AudioOptions options;
  private readonly ILogger<AudioRecorder> log;
  private readonly Channel<Block> queue;

  private Task? writer;
  private volatile bool recording;
  private long dropped;

  public AudioRecorder(IOptions<AudioOptions> options, ILogger<AudioRecorder> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.options = options.Value;
    this.log = log ?? throw new ArgumentNullException(nameof(log));
    this.queue = Channel.CreateBounded<Block>(
      new BoundedChannelOptions(QueueCapacity) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
      },
      this.OnDropped);
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    if(this.options.RecordAudioPath is not { Length: > 0 } path) {
      return Task.CompletedTask;
    }

    this.writer = Task.Run(() => this.WriteUntilDoneAsync(path), CancellationToken.None);
    this.recording = true;

    this.log.LogInformation("Recording captured audio to {Path}.", path);

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    if(this.writer is not Task running) {
      return;
    }

    this.recording = false;
    this.queue.Writer.TryComplete();

    await running.ConfigureAwait(false);
  }

  /// <inheritdoc/>
  ///
  /// <remarks>
  /// Copies and returns. The samples belong to the capture callback's buffer and
  /// are overwritten as soon as it returns, so the queue cannot hold the span
  /// itself - and a rented array keeps a hundred copies a second off the garbage
  /// collector.
  /// </remarks>
  public void Write(ReadOnlySpan<float> mono, int sampleRate) {
    if(!this.recording || mono.Length == 0 || sampleRate <= 0) {
      return;
    }

    float[] buffer = ArrayPool<float>.Shared.Rent(mono.Length);
    mono.CopyTo(buffer);

    if(!this.queue.Writer.TryWrite(new Block(buffer, mono.Length, sampleRate))) {
      // The queue is complete: the host is stopping and this block arrived after.
      ArrayPool<float>.Shared.Return(buffer);
    }
  }

  /// <summary>
  /// Returns a dropped block's buffer and says so once.
  ///
  /// Once and not per block, because the condition that causes one drop causes
  /// thousands, and a warning per block would itself hold up the writer that is
  /// already behind.
  /// </summary>
  private void OnDropped(Block block) {
    ArrayPool<float>.Shared.Return(block.Buffer);

    if(Interlocked.Increment(ref this.dropped) == 1) {
      this.log.LogWarning(
        "The audio recording fell behind and is dropping samples. The file will be shorter than the run, "
          + "so timings taken from it will not line up with anything else.");
    }
  }

  private async Task WriteUntilDoneAsync(string path) {
    WaveFile? file = null;
    int openedAt = 0;

    try {
      DateTime nextFlush = DateTime.UtcNow + FlushInterval;
      DateTime nextProgress = DateTime.UtcNow + ProgressInterval;

      await foreach(Block block in this.queue.Reader.ReadAllAsync().ConfigureAwait(false)) {
        try {
          if(file is null) {
            // Opened on the first block rather than at startup, because the rate
            // is the device's choice and nothing knows it until one is open.
            openedAt = block.SampleRate;
            file = new WaveFile(File.Create(path), openedAt);
            this.log.LogInformation("Recording audio at {SampleRate} Hz, mono, 32-bit float.", openedAt);
          }

          if(block.SampleRate != openedAt) {
            // A WAVE file carries one rate. Splicing a second one in would leave a
            // file whose header is a lie about most of its contents, which is worse
            // than a recording that stops and says why.
            this.log.LogError(
              "The capture rate changed from {Opened} Hz to {Now} Hz, so the recording stops here. "
                + "Restart Flux with the device already connected to record the rest.",
              openedAt,
              block.SampleRate);

            this.recording = false;
            return;
          }

          file.Write(block.Buffer.AsSpan(0, block.Count));

          if(DateTime.UtcNow >= nextFlush) {
            file.Flush();
            nextFlush = DateTime.UtcNow + FlushInterval;
          }

          if(DateTime.UtcNow >= nextProgress) {
            // The recording's own length, not the wall clock: it counts captured
            // audio, so it stalls if the device stops delivering rather than
            // reporting time that produced no samples.
            this.log.LogInformation("Recording: {Seconds:F0} s of audio.", file.Samples / (double)openedAt);
            nextProgress = DateTime.UtcNow + ProgressInterval;
          }
        } finally {
          ArrayPool<float>.Shared.Return(block.Buffer);
        }
      }
    } catch(Exception ex) {
      // A recording is a diagnostic. A full disk or a bad path costs the recording
      // and nothing else: the set carries on.
      this.log.LogError(ex, "Recording audio to {Path} stopped.", path);
      this.recording = false;
    } finally {
      long samples = file?.Samples ?? 0;
      file?.Dispose();

      this.log.LogInformation(
        "Recorded {Seconds:F1} seconds of audio to {Path}, dropping {Dropped} blocks.",
        openedAt > 0 ? samples / (double)openedAt : 0,
        path,
        Interlocked.Read(ref this.dropped));
    }
  }

  /// <summary>
  /// One queued block. The rate travels with it because the file is opened from
  /// the first block that arrives, on the writer's thread rather than the
  /// callback's.
  /// </summary>
  private readonly record struct Block(float[] Buffer, int Count, int SampleRate);
}
