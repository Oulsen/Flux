using System.Text.Json;
using Xunit;

namespace Flux.Audio.Tests;

public class FrameRecordingTests {
  [Fact]
  public void ReplaysWhatItRecorded() {
    // The point of the format: a recording of a real track has to reach the detector
    // as the frames that came off the analyzer, or a fixture proves something about
    // the file rather than about the music.
    IReadOnlyList<SpectrumFrame> recorded = new FrameTrack().Warmup().Beats(128, 4).Frames;
    string path = TempPath();

    try {
      File.WriteAllLines(path, recorded.Select(FrameRecording.ToLine));

      List<SpectrumFrame> replayed = [.. FrameRecording.Read(path)];

      Assert.Equal(recorded.Count, replayed.Count);

      for(int index = 0; index < recorded.Count; index++) {
        Assert.Equal(recorded[index].TimestampMicros, replayed[index].TimestampMicros);
        Assert.Equal(recorded[index].SampleRate, replayed[index].SampleRate);
        Assert.Equal(recorded[index].Kick, replayed[index].Kick);
        Assert.Equal(recorded[index].Mids, replayed[index].Mids);
        Assert.Equal(recorded[index].Highs, replayed[index].Highs);
        Assert.Equal(recorded[index].Bins, replayed[index].Bins);
        Assert.Equal(recorded[index].Generation, replayed[index].Generation);
      }
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void ReachesTheSameVerdictAfterARoundTrip() {
    // The assertion that actually matters. Equal numbers are not the claim; the
    // claim is that a replayed recording is the same evidence, and the detector is
    // the only thing that can say so.
    IReadOnlyList<SpectrumFrame> recorded = new FrameTrack().Warmup().Beats(128, 16).Frames;
    string path = TempPath();

    try {
      File.WriteAllLines(path, recorded.Select(FrameRecording.ToLine));

      AudioBeatDetector fromMemory = new AudioBeatDetector();
      BeatDecision live = recorded.Select(fromMemory.Observe).Last();

      AudioBeatDetector fromFile = new AudioBeatDetector();
      BeatDecision replayed = FrameRecording.Read(path).Select(fromFile.Observe).Last();

      Assert.Equal(live, replayed);
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void SkipsBlankLinesAndKeepsTheRest() {
    // A recording is flushed while it is being written, so the last line of an
    // interrupted run can be anything, including nothing.
    string path = TempPath();

    try {
      File.WriteAllLines(path, [
        FrameRecording.ToLine(Frame(1)),
        string.Empty,
        FrameRecording.ToLine(Frame(2)),
      ]);

      List<SpectrumFrame> replayed = [.. FrameRecording.Read(path)];

      Assert.Equal(2, replayed.Count);
      Assert.Equal([1.0, 2.0], replayed.Select(frame => frame.TimestampMicros));
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void RefusesToQuietlyDropATruncatedLine() {
    // The opposite trade to a blank line, and deliberate: a half-written line is
    // data that was meant to be there, and silently discarding it would let a
    // fixture lose its last second with nothing to show for it.
    string path = TempPath();

    try {
      File.WriteAllLines(path, [FrameRecording.ToLine(Frame(1)), "{\"t\":2.0,\"rate\":48"]);

      Assert.Throws<JsonException>(() => FrameRecording.Read(path).ToList());
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void CarriesTheGenerationThroughARoundTrip() {
    // The generation is the one field a replay cannot reconstruct from the rest of
    // the line: a device switch leaves a gap in the timestamps far too short to
    // recognise as one, which is the reason the field exists at all.
    string path = TempPath();

    try {
      File.WriteAllLines(path, [
        FrameRecording.ToLine(Frame(1)),
        FrameRecording.ToLine(Frame(2) with { Generation = 3 }),
      ]);

      List<SpectrumFrame> replayed = [.. FrameRecording.Read(path)];

      Assert.Equal([0, 3], replayed.Select(frame => frame.Generation));
    } finally {
      File.Delete(path);
    }
  }

  [Fact]
  public void ReadsARecordingMadeBeforeGenerationsWereRecorded() {
    // Every recording made before the field existed is one unbroken run of audio
    // as far as anyone can now tell, and generation zero says exactly that.
    string path = TempPath();

    try {
      File.WriteAllLines(path, ["{\"t\":1,\"rate\":48000,\"kick\":0.1,\"mids\":0.2,\"highs\":0.3,\"bins\":[]}"]);

      SpectrumFrame replayed = Assert.Single(FrameRecording.Read(path));

      Assert.Equal(0, replayed.Generation);
    } finally {
      File.Delete(path);
    }
  }

  private static SpectrumFrame Frame(double micros) {
    return new SpectrumFrame(micros, 48_000, new int[AudioConstants.DisplayBins], 0.1, 0.2, 0.3);
  }

  private static string TempPath() {
    return Path.Combine(Path.GetTempPath(), $"flux-frames-{Guid.NewGuid():N}.jsonl");
  }
}
