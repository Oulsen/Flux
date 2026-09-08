namespace Flux.Audio.Tests;

/// <summary>
/// Builds runs of analyzer frames: a click track, a breakdown, a snare pattern.
///
/// What these can and cannot prove is worth being clear about. They prove the
/// arithmetic: that an onset every 469 ms comes out as 128 BPM, that the
/// kick-versus-mid gate rejects a snare, that a drop renumbers the beats and a
/// short gap does not. They cannot prove the <em>tuning</em>, because the
/// thresholds in <see cref="AudioConstants"/> are claims about music and a
/// synthetic pulse is not music. That is what
/// <see cref="AudioOptions.RecordFramesPath"/> and a recording of a real track are
/// for.
///
/// Every frame carries its own timestamp, spaced at the analyzer's frame rate, so
/// the detector under test measures time exactly as it would on stage.
/// </summary>
internal sealed class FrameTrack {
  private const double FramePeriodMicros = 1_000_000.0 / AudioConstants.FrameRateHz;

  /// <summary>
  /// Bass energy between hits. Above
  /// <see cref="AudioConstants.QuietEnergyThreshold"/> on purpose, so an ordinary
  /// click track never looks like a breakdown to the drop detector.
  /// </summary>
  private const double BetweenHits = 0.03;

  /// <summary>
  /// Bass energy during a breakdown. Below the quiet threshold, and above zero,
  /// because a mix rarely goes bit-exact silent and the drop ratio must not depend
  /// on it doing so.
  /// </summary>
  private const double DuringSilence = 0.005;

  /// <summary>
  /// How much of a hit survives into the next frame. A kick decays over about four
  /// frames at this rate, which is what makes the frame after an onset a fall rather
  /// than a second onset.
  /// </summary>
  private const double HitDecay = 0.5;

  private readonly List<SpectrumFrame> frames = new List<SpectrumFrame>();

  /// <summary>
  /// A plausible clock origin rather than zero, so nothing under test can pass by
  /// accidentally treating a timestamp as an elapsed time.
  /// </summary>
  private double micros = 1_000_000;

  public IReadOnlyList<SpectrumFrame> Frames => this.frames;

  /// <summary>
  /// Enough near-silence to get past the detector's warm-up, and no more.
  ///
  /// Deliberately shorter than <see cref="AudioConstants.MinQuietForDropMillis"/>,
  /// so a track that starts here starts with a beat rather than with a drop.
  /// </summary>
  public FrameTrack Warmup() {
    return this.Quiet(AudioConstants.WarmUpFrames * 2 * FramePeriodMicros / 1000.0);
  }

  /// <summary>A run of kick drums at a steady tempo.</summary>
  public FrameTrack Beats(double bpm, int count, double peak = 0.5, double mids = 0.05) {
    return this.Hits(bpm, [.. Enumerable.Range(0, count).Select(beat => (double)beat)], peak, mids);
  }

  /// <summary>
  /// Kick onsets at arbitrary beat positions, ascending, for what a steady run
  /// cannot express: missed kicks, and bass notes landing off the grid. The
  /// section runs to one beat past the last onset, so consecutive sections join
  /// without a gap.
  /// </summary>
  public FrameTrack Hits(double bpm, IReadOnlyList<double> beats, double peak = 0.5, double mids = 0.05) {
    double framesPerBeat = 60.0 / bpm * AudioConstants.FrameRateHz;
    int total = (int)Math.Round((beats[^1] + 1) * framesPerBeat);

    HashSet<int> onsets = [];
    foreach(double beat in beats) {
      onsets.Add((int)Math.Round(beat * framesPerBeat));
    }

    int sinceOnset = -1;
    for(int frame = 0; frame < total; frame++) {
      if(onsets.Contains(frame)) {
        sinceOnset = 0;
      } else if(sinceOnset >= 0) {
        sinceOnset++;
      }

      double energy = sinceOnset < 0
        ? BetweenHits
        : BetweenHits + ((peak - BetweenHits) * Math.Pow(HitDecay, sinceOnset));

      this.Add(energy, mids);
    }

    return this;
  }

  /// <summary>A breakdown: energy under the quiet threshold for a while.</summary>
  public FrameTrack Quiet(double millis) {
    int count = (int)Math.Round(millis * 1000.0 / FramePeriodMicros);
    for(int frame = 0; frame < count; frame++) {
      this.Add(DuringSilence, 0.002);
    }

    return this;
  }

  private void Add(double kick, double mids) {
    // The display columns are not what the detector reads, so they are filled with
    // something recognisable rather than something realistic: a test that started
    // depending on them would be testing the wrong half of the frame.
    int[] bins = new int[AudioConstants.DisplayBins];
    Array.Fill(bins, (int)Math.Clamp(kick * 255, 0, 255));

    this.frames.Add(new SpectrumFrame(this.micros, 48_000, bins, kick, mids, 0.01));
    this.micros += FramePeriodMicros;
  }
}
