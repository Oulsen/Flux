using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Flux.Audio;

/// <summary>
/// One analyzed frame, as it appears on a line of a recording.
///
/// Deliberately not the whole spectrum. A recording exists to replay through
/// <see cref="AudioBeatDetector"/>, which reads three numbers and a timestamp, so
/// storing all 1024 bins would grow the file forty times over to keep data nothing
/// replays. The display columns are kept because a fixture nobody can look at is a
/// fixture nobody trusts, and 64 of them is cheap.
///
/// The consequence is worth knowing before a band edge is ever moved: a recording
/// holds band energies, not audio, so changing where the bands are makes every
/// existing fixture describe the old bands. They would need re-recording, and the
/// detector tests would be pinning history rather than behaviour.
/// </summary>
/// <param name="T">Frame timestamp in microseconds, on the monotonic clock of the run that recorded it.</param>
/// <param name="Rate">The sample rate the frame was analyzed at.</param>
/// <param name="Kick">Bass band energy.</param>
/// <param name="Mids">Mid band energy.</param>
/// <param name="Highs">High band energy.</param>
/// <param name="Bins">The display columns, for looking at.</param>
internal sealed record RecordedFrame(
  double T,
  int Rate,
  double Kick,
  double Mids,
  double Highs,
  int[] Bins);

/// <summary>
/// Source-generated so the format is one declaration rather than a serializer
/// configured identically in two places.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RecordedFrame))]
internal sealed partial class RecordingJson: JsonSerializerContext {
}

/// <summary>
/// Reads and writes recordings: one JSON object per line.
///
/// JSON Lines rather than one JSON document, for the property a test fixture needs
/// most: a recording that was interrupted, by a crash or by closing the app, is
/// still a valid recording up to its last complete line. An array would be a
/// syntax error, and the run worth keeping is exactly the one that ended badly.
/// </summary>
internal static class FrameRecording {
  public static string ToLine(SpectrumFrame frame) {
    ArgumentNullException.ThrowIfNull(frame);

    return JsonSerializer.Serialize(
      new RecordedFrame(frame.TimestampMicros, frame.SampleRate, frame.Kick, frame.Mids, frame.Highs, frame.Bins),
      RecordingJson.Default.RecordedFrame);
  }

  /// <summary>
  /// Replays a recording as frames.
  ///
  /// Blank lines are skipped and a trailing partial line is not: a truncated line
  /// fails to parse and says so, because silently dropping it would let a fixture
  /// lose its last second without anything noticing.
  /// </summary>
  public static IEnumerable<SpectrumFrame> Read(string path) {
    foreach(string line in File.ReadLines(path)) {
      if(line.Length == 0) {
        continue;
      }

      RecordedFrame recorded = JsonSerializer.Deserialize(line, RecordingJson.Default.RecordedFrame)
        ?? throw new InvalidDataException($"A line of {path} held a JSON null rather than a frame.");

      yield return new SpectrumFrame(
        recorded.T,
        recorded.Rate,
        recorded.Bins,
        recorded.Kick,
        recorded.Mids,
        recorded.Highs);
    }
  }
}

/// <summary>
/// Writes every analyzed frame to a file, when asked to.
///
/// This is the only way a defect found on stage becomes a test. Synthetic signals
/// prove the arithmetic in the detector and cannot prove the tuning: the thresholds
/// are numbers about music, so the fixtures that matter are recordings of a real
/// track through the real controller. Recording is off unless
/// <see cref="AudioOptions.RecordFramesPath"/> says otherwise.
///
/// Frames reach the file through a bounded queue and a writer of its own, never
/// straight from the analyzer thread. The reason is specific rather than general:
/// a disk that stalls for 40 ms would delay the frame loop, and the frame loop's
/// cadence is one of the things a recording is made to study. A recorder that
/// distorted what it recorded would be worse than none.
/// </summary>
internal sealed class FrameRecorder: IHostedService {
  /// <summary>
  /// Frames the queue holds, about ten seconds' worth.
  ///
  /// Generous because the writer only ever falls behind in bursts, and dropping the
  /// oldest is the right answer when it does: a gap in the middle of a recording is
  /// visible in the timestamps, where a stalled analyzer would be invisible in the
  /// data and wrong in every frame.
  /// </summary>
  private const int QueueCapacity = AudioConstants.FrameRateHz * 10;

  /// <summary>How often the file is flushed, so an interrupted run keeps what it had.</summary>
  private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

  private readonly SpectrumAnalyzer analyzer;
  private readonly AudioOptions options;
  private readonly ILogger<FrameRecorder> log;

  private readonly Channel<SpectrumFrame> queue = Channel.CreateBounded<SpectrumFrame>(
    new BoundedChannelOptions(QueueCapacity) {
      FullMode = BoundedChannelFullMode.DropOldest,
      SingleReader = true,
      SingleWriter = true,
    });

  private Task? writer;
  private long recorded;
  private long dropped;

  public FrameRecorder(SpectrumAnalyzer analyzer, IOptions<AudioOptions> options, ILogger<FrameRecorder> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
    this.options = options.Value;
    this.log = log ?? throw new ArgumentNullException(nameof(log));
  }

  public Task StartAsync(CancellationToken cancellationToken) {
    if(this.options.RecordFramesPath is not { Length: > 0 } path) {
      return Task.CompletedTask;
    }

    this.writer = Task.Run(() => this.WriteUntilDoneAsync(path), CancellationToken.None);
    this.analyzer.Analyzed += this.Enqueue;

    this.log.LogInformation("Recording analyzed audio frames to {Path}.", path);

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    if(this.writer is not Task running) {
      return;
    }

    this.analyzer.Analyzed -= this.Enqueue;
    this.queue.Writer.TryComplete();

    await running.ConfigureAwait(false);

    this.log.LogInformation(
      "Recorded {Frames} audio frames, dropping {Dropped}.",
      Interlocked.Read(ref this.recorded),
      Interlocked.Read(ref this.dropped));
  }

  /// <summary>
  /// Called on the analyzer thread, and does nothing that can wait.
  /// </summary>
  private void Enqueue(SpectrumFrame frame) {
    if(!this.queue.Writer.TryWrite(frame)) {
      Interlocked.Increment(ref this.dropped);
    }
  }

  private async Task WriteUntilDoneAsync(string path) {
    try {
      // Truncating rather than appending. Two runs in one file would interleave two
      // unrelated timelines from two unrelated clock origins, and the resulting
      // fixture would look like a track that jumped backwards in time.
      await using StreamWriter file = new StreamWriter(path, append: false);
      DateTime nextFlush = DateTime.UtcNow + FlushInterval;

      await foreach(SpectrumFrame frame in this.queue.Reader.ReadAllAsync().ConfigureAwait(false)) {
        await file.WriteLineAsync(FrameRecording.ToLine(frame)).ConfigureAwait(false);
        Interlocked.Increment(ref this.recorded);

        if(DateTime.UtcNow >= nextFlush) {
          await file.FlushAsync().ConfigureAwait(false);
          nextFlush = DateTime.UtcNow + FlushInterval;
        }
      }
    } catch(Exception ex) {
      // A recording is a diagnostic. A full disk or a bad path must cost the
      // recording and nothing else: the set carries on.
      this.log.LogError(ex, "Recording audio frames to {Path} stopped.", path);
    }
  }
}
