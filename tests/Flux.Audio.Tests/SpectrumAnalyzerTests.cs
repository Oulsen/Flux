using Flux.Beat;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flux.Audio.Tests;

/// <summary>
/// A signal generator standing in for a sound card.
///
/// Every window it hands out is identical, which is exactly what a calibration test
/// wants: the analyzer's smoothing then converges on the true magnitude instead of
/// chasing a moving one, so an assertion about a level is an assertion about the
/// scaling rather than about how long the test waited.
/// </summary>
internal sealed class ToneSamples: IAudioSamples {
  public int SampleRate { get; set; } = 48_000;

  public double Amplitude { get; set; }

  public double Frequency { get; set; }

  public bool Delivering { get; set; } = true;

  public bool TryReadLatest(Span<float> destination) {
    if(!this.Delivering || this.SampleRate <= 0) {
      return false;
    }

    for(int index = 0; index < destination.Length; index++) {
      destination[index] = (float)(this.Amplitude
        * Math.Sin(2.0 * Math.PI * this.Frequency * index / this.SampleRate));
    }

    return true;
  }
}

public class SpectrumAnalyzerTests {
  /// <summary>
  /// Bin 43 at 48 kHz over 2048 samples, which is 1007.8125 Hz exactly. A bin
  /// centre, so a Hann window puts the whole amplitude in that one bin rather than
  /// spreading it over its neighbours: the point of the test is the scaling, not
  /// the leakage.
  /// </summary>
  private const double ToneHz = 43 * 48_000.0 / AudioConstants.FftSize;

  /// <summary>
  /// The column bin 43 lands in, and the byte a -65 dBFS tone has to read there.
  /// The midpoint of the byte range is the only level worth calibrating against:
  /// anything within 30 dB of full scale is clamped to 255, so a magnitude scaling
  /// that was out by a factor of two would look perfect.
  /// </summary>
  private const int ToneColumn = 35;

  private const double MinusSixtyFiveDbfs = 0.000562341325;

  private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

  [Fact]
  public async Task FiresFramesOnItsOwnThread() {
    // The test the whole IAudioSamples seam exists for. Phase 1's beat scheduler
    // shipped a loop that resynced to the next deadline whenever it woke up late,
    // which meant it fired nothing at all, and every unit test of its arithmetic
    // passed. Only running the real thread finds that.
    ToneSamples source = new ToneSamples { Amplitude = 0.5, Frequency = ToneHz };
    SpectrumAnalyzer analyzer = Build(source);

    await analyzer.StartAsync(TestContext.Current.CancellationToken);

    try {
      Assert.True(
        await WaitUntil(() => analyzer.Frames >= 20, Patience),
        $"The analyzer produced {analyzer.Frames} frames, so its frame loop is not firing.");

      // A ceiling as well as a floor, and the ceiling is the interesting half: a
      // loop that catches up after a late frame would produce two frames from one
      // window of audio, and the second would measure a change the music never
      // made. The floor is deliberately far below 60 because nothing here raises the
      // system timer resolution, which the host does and a test host does not.
      Assert.True(
        await WaitUntil(() => analyzer.Fps > 0, Patience),
        "The analyzer never reported a frame rate.");
      Assert.InRange(analyzer.Fps, 15, AudioConstants.FrameRateHz * 1.2);
    } finally {
      await Stop(analyzer);
    }
  }

  [Fact]
  public async Task ReadsAToneAtTheLevelTheScalePromises() {
    // This is what pins SpectrumAnalyzer.MagnitudeScale, and through it the Hann
    // normalization and the transform's own scaling convention. If any of the three
    // changes, every threshold in AudioConstants silently moves with it, because
    // they are all numbers on this byte scale. A test is the only thing standing
    // between that and a detector that is merely slightly deaf.
    ToneSamples source = new ToneSamples { Amplitude = MinusSixtyFiveDbfs, Frequency = ToneHz };
    SpectrumAnalyzer analyzer = Build(source);

    await analyzer.StartAsync(TestContext.Current.CancellationToken);

    try {
      // Enough frames for the smoothing to have converged: 0.8 to the fortieth is
      // about one part in eight thousand, well under a single byte step.
      Assert.True(
        await WaitUntil(() => analyzer.Frames >= 40, Patience),
        "The analyzer did not produce enough frames to have settled.");

      SpectrumFrame frame = Assert.IsType<SpectrumFrame>(analyzer.Latest);

      Assert.Equal(48_000, frame.SampleRate);
      Assert.Equal(AudioConstants.DisplayBins, frame.Bins.Length);
      Assert.InRange(frame.Bins[ToneColumn], 125, 131);

      // The tone is at 1008 Hz, which is inside the high band and nowhere near the
      // kick band. A leak into the kick band is worth failing on: it would be
      // spectral energy the detector triggers on, arriving from a frequency that has
      // nothing to do with a kick drum.
      Assert.True(frame.Highs > frame.Kick, "The tone did not land in the band it belongs to.");
      Assert.InRange(frame.Kick, 0, 0.001);
    } finally {
      await Stop(analyzer);
    }
  }

  [Fact]
  public async Task ForgetsTheSpectrumWhenAudioStops() {
    // A spectrum frozen at the last thing it heard is the most misleading picture a
    // visualizer can draw, because it looks like music. Null is the honest answer
    // and the page can say so.
    ToneSamples source = new ToneSamples { Amplitude = 0.5, Frequency = ToneHz };
    SpectrumAnalyzer analyzer = Build(source);

    await analyzer.StartAsync(TestContext.Current.CancellationToken);

    try {
      Assert.True(await WaitUntil(() => analyzer.Latest is not null, Patience), "Nothing was ever analyzed.");

      source.Delivering = false;

      Assert.True(
        await WaitUntil(() => analyzer.Latest is null, Patience),
        "The analyzer kept publishing a spectrum after the audio stopped.");
    } finally {
      await Stop(analyzer);
    }
  }

  [Fact]
  public async Task KeepsRunningWhenAHandlerThrows() {
    // The visualizer is fed from this thread too, and it is the instrument the
    // detector is judged with. One bad subscriber must not take it down.
    ToneSamples source = new ToneSamples { Amplitude = 0.5, Frequency = ToneHz };
    SpectrumAnalyzer analyzer = Build(source);

    analyzer.Analyzed += _ => throw new InvalidOperationException("A handler misbehaving on purpose.");

    await analyzer.StartAsync(TestContext.Current.CancellationToken);

    try {
      Assert.True(
        await WaitUntil(() => analyzer.Frames >= 10, Patience),
        "A throwing handler stopped the analyzer thread.");
    } finally {
      await Stop(analyzer);
    }
  }

  [Fact]
  public async Task HandsEveryPublishedFrameToItsHandlers() {
    ToneSamples source = new ToneSamples { Amplitude = 0.5, Frequency = ToneHz };
    SpectrumAnalyzer analyzer = Build(source);

    // Counted with an interlocked increment because the writer is the analyzer
    // thread and the reader is this one.
    int seen = 0;
    analyzer.Analyzed += _ => Interlocked.Increment(ref seen);

    await analyzer.StartAsync(TestContext.Current.CancellationToken);

    try {
      Assert.True(await WaitUntil(() => Volatile.Read(ref seen) >= 10, Patience), "The handler was never called.");
    } finally {
      await Stop(analyzer);
    }

    // Compared after the thread has stopped, so the two numbers cannot be read a
    // frame apart. The detector is driven from this event, so a frame that reached
    // the slot and not the handler is a frame the detector never hears.
    Assert.Equal(analyzer.Frames, seen);
  }

  private static SpectrumAnalyzer Build(IAudioSamples source) {
    return new SpectrumAnalyzer(source, new MonotonicClock(), NullLogger<SpectrumAnalyzer>.Instance);
  }

  private static async Task Stop(SpectrumAnalyzer analyzer) {
    await analyzer.StopAsync(CancellationToken.None);
    analyzer.Dispose();
  }

  private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout) {
    DateTime deadline = DateTime.UtcNow + timeout;

    while(DateTime.UtcNow < deadline) {
      if(condition()) {
        return true;
      }

      await Task.Delay(20, CancellationToken.None);
    }

    return condition();
  }
}
