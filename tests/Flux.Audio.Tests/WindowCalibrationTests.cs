using Xunit;

namespace Flux.Audio.Tests;

/// <summary>
/// The offline half of positioning the decibel window.
///
/// Both facts here are skipped unless a local recording is present, because the
/// material is a multi-megabyte capture of real music that is deliberately not in
/// the repository. They are committed anyway: the arithmetic that decides
/// <see cref="AudioConstants.SpectrumMaxDb"/> is worth keeping next to the
/// constant it decides, and re-running it after any change to the analyzer is how
/// the constant stops being folklore.
/// </summary>
public class WindowCalibrationTests {
  /// <summary>The environment variable that overrides where recordings are looked for.</summary>
  private const string FolderVariable = "FLUX_RECORDINGS";

  /// <summary>
  /// Why these are skipped, phrased so it says what to do about it. The recording
  /// is several megabytes of somebody's music and is deliberately not committed.
  /// </summary>
  private const string Missing = "No calibration recording found. Record one with Audio:RecordAudioPath and "
    + "Audio:RecordFramesPath into .plan/recordings, or point " + FolderVariable + " at a folder holding it.";

  private const string Take = "2026-09-04-master-01";

  /// <summary>
  /// The decibel window in force when <see cref="Take"/> was recorded, which is
  /// not the one in <see cref="AudioConstants"/> any more.
  ///
  /// Written down here because a frame recording carries no record of the window
  /// that produced it: the file holds band energies, and those are meaningless
  /// without knowing what scale they are on. That is the sharp end of why the WAVE
  /// recording is the durable fixture and the JSONL is a derived one - the audio
  /// is the same evidence whatever the constants say, and these numbers have to be
  /// remembered by hand.
  /// </summary>
  private const double TakeMinDb = -100;

  /// <inheritdoc cref="TakeMinDb"/>
  private const double TakeMaxDb = -30;

  [Fact]
  public void TheProbeAgreesWithTheAnalyzerThatRecordedTheSameRun() {
    // The credibility check, and the reason a WAVE recording and a frame recording
    // are made in the same run. SpectrumProbe reproduces the analyzer's transform
    // instead of calling it, so everything the calibration concludes rests on that
    // reproduction being faithful - and the frame recording is the analyzer's own
    // account of the same music, on the window that was in force at the time.
    //
    // Distributions rather than frame-by-frame equality, deliberately. The
    // analyzer's windows fall where the ring buffer and the frame clock put them,
    // which is not where a replay at exactly 60 fps puts them, so matching frames
    // pairwise would be asserting that two different windows of a real signal are
    // identical. What has to agree is the level.
    if(Find(".wav") is not string wave || Find(".jsonl") is not string frames) {
      Assert.Skip(Missing);
      return;
    }

    (int rate, float[] samples) = WaveFile.Read(wave);

    List<double> replayedKick = [];
    List<double> replayedMids = [];
    SpectrumProbe.Replay(samples, rate, (seconds, magnitudes) => {
      SpectrumFrame frame = SpectrumProbe.ToFrame(seconds, magnitudes, rate, TakeMinDb, TakeMaxDb);

      replayedKick.Add(frame.Kick);
      replayedMids.Add(frame.Mids);
    });

    List<double> recordedKick = [];
    List<double> recordedMids = [];
    foreach(SpectrumFrame frame in FrameRecording.Read(frames)) {
      recordedKick.Add(frame.Kick);
      recordedMids.Add(frame.Mids);
    }

    foreach(double quantile in new[] { 0.1, 0.5, 0.9 }) {
      Assert.Equal(Percentile(recordedKick, quantile), Percentile(replayedKick, quantile), 0.06);
      Assert.Equal(Percentile(recordedMids, quantile), Percentile(replayedMids, quantile), 0.06);
    }
  }

  [Fact]
  public void ReportsWhereTheDecibelWindowShouldSit() {
    if(Find(".wav") is not string wave) {
      Assert.Skip(Missing);
      return;
    }

    (int rate, float[] samples) = WaveFile.Read(wave);
    (int kickLow, int kickHigh) = SpectrumScale.BandRange(
      AudioConstants.KickLowHz,
      AudioConstants.KickHighHz,
      rate);

    double[] candidates = [-30, -20, -16, -14, -12, -10, -8, -6, -4, -2, 0];
    Dictionary<double, AudioBeatDetector> detectors = candidates.ToDictionary(
      candidate => candidate,
      _ => new AudioBeatDetector());
    Dictionary<double, BeatDecision> verdicts = [];

    // Every locked reading, not only the last one. A window that reports the right
    // tempo for four minutes is a different thing from one that happens to be right
    // when the music stops, and only the first is a calibration.
    Dictionary<double, List<double>> locked = candidates.ToDictionary(
      candidate => candidate,
      _ => new List<double>());
    Dictionary<double, long> clamped = [];
    Dictionary<double, List<double>> energies = candidates.ToDictionary(
      candidate => candidate,
      _ => new List<double>());

    List<double> peakDb = [];
    List<double> meanDb = [];
    long total = 0;

    SpectrumProbe.Replay(samples, rate, (seconds, magnitudes) => {
      total++;

      double peak = double.NegativeInfinity;
      double sum = 0;
      for(int bin = kickLow; bin < kickHigh; bin++) {
        double decibels = SpectrumProbe.Decibels(magnitudes[bin]);
        peak = Math.Max(peak, decibels);
        sum += magnitudes[bin] * magnitudes[bin];
      }

      peakDb.Add(peak);
      meanDb.Add(SpectrumProbe.Decibels(Math.Sqrt(sum / (kickHigh - kickLow))));

      foreach(double candidate in candidates) {
        SpectrumFrame frame = SpectrumProbe.ToFrame(
          seconds,
          magnitudes,
          rate,
          candidate - (AudioConstants.SpectrumMaxDb - AudioConstants.SpectrumMinDb),
          candidate);

        energies[candidate].Add(frame.Kick);
        verdicts[candidate] = detectors[candidate].Observe(frame);

        if(verdicts[candidate].Locked) {
          locked[candidate].Add(verdicts[candidate].Bpm);
        }

        for(int bin = kickLow; bin < kickHigh; bin++) {
          if(SpectrumProbe.ToByte(
            magnitudes[bin],
            candidate - (AudioConstants.SpectrumMaxDb - AudioConstants.SpectrumMinDb),
            candidate) >= 255) {
            clamped[candidate] = clamped.GetValueOrDefault(candidate) + 1;
          }
        }
      }
    });

    Console.WriteLine($"Recording: {Take}, {samples.Length / (double)rate:F1} s at {rate} Hz, {total} frames.");
    Console.WriteLine($"Kick band: bins {kickLow} to {kickHigh - 1}.");
    Console.WriteLine();
    Console.WriteLine("Unclamped kick band level, which is what the window has to contain:");
    foreach(double quantile in new[] { 0.5, 0.9, 0.99, 0.999, 1.0 }) {
      Console.WriteLine(
        $"  p{quantile * 100,-6:0.#}  loudest bin {Percentile(peakDb, quantile),7:F1} dB"
          + $"   band mean {Percentile(meanDb, quantile),7:F1} dB");
    }

    Console.WriteLine();
    Console.WriteLine("Candidate window tops, span held at 70 dB:");
    Console.WriteLine("  top     clamped   kick p50   final BPM   median BPM   locked   beats");
    foreach(double candidate in candidates) {
      long bins = total * (kickHigh - kickLow);
      BeatDecision verdict = verdicts[candidate];
      List<double> readings = locked[candidate];

      Console.WriteLine(
        $"  {candidate,4:F0} dB   {100.0 * clamped.GetValueOrDefault(candidate) / bins,6:F1}%"
          + $"   {Percentile(energies[candidate], 0.5),8:F3}"
          + $"   {verdict.Bpm,9:F2}"
          + $"   {(readings.Count == 0 ? double.NaN : Percentile(readings, 0.5)),10:F2}"
          + $"   {100.0 * readings.Count / total,5:F0}%   {verdict.BeatsDetected,5}");
    }

    Console.WriteLine();
    Console.WriteLine("Tempo from the audio, independent of the window:");
    foreach((double bpm, double correlation) in Tempo(meanDb)) {
      Console.WriteLine($"  {bpm,7:F2} BPM   r={correlation:F3}");
    }

    // Per block, because a take that spans two tracks spans two tempi, and one
    // number for the whole recording would average two different claims.
    Console.WriteLine();
    Console.WriteLine("The same, in 30 second blocks, with the level at that moment:");
    int block = AudioConstants.FrameRateHz * 30;
    for(int start = 0; start + block <= meanDb.Count; start += block) {
      List<double> slice = meanDb.GetRange(start, block);
      (double bpm, double correlation) = Tempo(slice)[0];

      Console.WriteLine(
        $"  {start / AudioConstants.FrameRateHz,4} to {(start + block) / AudioConstants.FrameRateHz,4} s"
          + $"   {bpm,7:F2} BPM   r={correlation:F3}   band mean p50 {Percentile(slice, 0.5),6:F1} dB");
    }
  }

  /// <summary>
  /// Estimates tempo by autocorrelating the rise in kick level.
  ///
  /// On decibels rather than on the byte scale, which is what makes it an
  /// independent reference: a decibel difference is a ratio, so every gain the
  /// signal passed through on its way here cancels, and the answer cannot depend on
  /// the window position being calibrated. It is also why this is worth reporting
  /// beside the detector's own number rather than instead of it.
  /// </summary>
  private static List<(double Bpm, double Correlation)> Tempo(List<double> levels) {
    List<double> rises = [];
    for(int index = 1; index < levels.Count; index++) {
      rises.Add(Math.Max(0.0, levels[index] - levels[index - 1]));
    }

    double mean = rises.Sum() / rises.Count;
    double variance = rises.Sum(rise => (rise - mean) * (rise - mean)) / rises.Count;
    List<(double Bpm, double Correlation)> found = [];

    // 300 to 1000 ms covers 60 to 200 BPM, which is the interval range the detector
    // itself is willing to believe.
    for(int lag = AudioConstants.FrameRateHz * 300 / 1000; lag <= AudioConstants.FrameRateHz; lag++) {
      double covariance = 0;
      for(int index = 0; index + lag < rises.Count; index++) {
        covariance += (rises[index] - mean) * (rises[index + lag] - mean);
      }

      double milliseconds = lag * 1000.0 / AudioConstants.FrameRateHz;
      found.Add((60_000.0 / milliseconds, covariance / (rises.Count - lag) / variance));
    }

    return [.. found.OrderByDescending(candidate => candidate.Correlation).Take(6)];
  }

  /// <summary>
  /// Locates one of the take's files, or null when it is not on this machine.
  ///
  /// Discovered rather than written down, because a path into one developer's
  /// drive is not a fact about Flux and everything committed here reads as if the
  /// repository were already public. It walks up from the test assembly looking for
  /// the working-notes folder instead of counting directories, since how deep the
  /// assembly sits is a property of the build configuration rather than of the
  /// repository.
  /// </summary>
  private static string? Find(string extension) {
    string? configured = Environment.GetEnvironmentVariable(FolderVariable);
    if(configured is { Length: > 0 }) {
      string named = Path.Combine(configured, Take + extension);

      return File.Exists(named) ? named : null;
    }

    for(DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory);
      folder is not null;
      folder = folder.Parent) {
      string candidate = Path.Combine(folder.FullName, ".plan", "recordings", Take + extension);

      if(File.Exists(candidate)) {
        return candidate;
      }
    }

    return null;
  }

  private static double Percentile(List<double> values, double quantile) {
    List<double> sorted = [.. values.Order()];

    return sorted[Math.Clamp((int)(quantile * (sorted.Count - 1)), 0, sorted.Count - 1)];
  }
}
