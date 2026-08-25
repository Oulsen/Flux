using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NAudio.Dsp;

namespace Flux.Audio;

/// <summary>
/// Turns captured audio into a spectrum, sixty times a second, on a thread of its
/// own.
///
/// The frame loop repeats the lesson Phase 1's beat scheduler paid for. It aims at
/// absolute timestamps rather than sleeping "one frame" at a time, because
/// sleeping a duration accumulates every scheduling error the operating system
/// hands out. And it <em>fires a late frame</em> rather than resyncing to the next
/// one: treating any overshoot as a miss makes a loop perpetually chase the
/// following deadline, and the first version of the beat scheduler fired exactly
/// zero beats that way. Only a test that runs this thread can catch that, which is
/// why <see cref="IAudioSamples"/> exists.
///
/// The one thing it does not do is catch up. A loop that fell a whole frame behind
/// does not then produce two frames from one window of audio: the second would
/// have no flux against the first, and the detector would read a discontinuity
/// where the music has none.
/// </summary>
internal sealed class SpectrumAnalyzer: IHostedService, IDisposable {
  /// <summary>
  /// Undoes the two halvings between a full-scale sine and its bin.
  ///
  /// The Hann window sums to half its length, and the energy of a real signal
  /// splits between the positive and the negative frequency, so a full-scale tone
  /// arrives at a quarter of full scale.
  ///
  /// It is a constant rather than a derivation because it is pinned by a test: a
  /// full-scale sine has to read 0 dBFS. If the transform's scaling convention ever
  /// changes, that test fails loudly, where the alternative is every threshold in
  /// <see cref="AudioConstants"/> drifting silently.
  /// </summary>
  internal const double MagnitudeScale = 4.0;

  /// <summary>Exponent of <see cref="AudioConstants.FftSize"/>, which is what the transform wants.</summary>
  private const int FftExponent = 11;

  private const double FramePeriodMillis = 1000.0 / AudioConstants.FrameRateHz;

  private const double FramePeriodMicros = 1_000_000.0 / AudioConstants.FrameRateHz;

  /// <summary>Closer than this to the deadline counts as arrived; sleeping again would overshoot it.</summary>
  private const double ArrivalToleranceMillis = 1.0;

  /// <summary>How long to wait before looking again while no device is delivering audio.</summary>
  private const int IdlePollMillis = 100;

  /// <summary>How long the measured frame rate is averaged over.</summary>
  private const double FpsWindowMicros = 1_000_000.0;

  /// <summary>How long to wait for the analyzer thread on shutdown.</summary>
  private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromMilliseconds(500);

  private readonly IAudioSamples samples;
  private readonly IMonotonicClock clock;
  private readonly ILogger<SpectrumAnalyzer> log;
  private readonly CancellationTokenSource stopping = new CancellationTokenSource();

  private readonly float[] window = new float[AudioConstants.FftSize];
  private readonly Complex[] transform = new Complex[AudioConstants.FftSize];
  private readonly double[] taper = new double[AudioConstants.FftSize];

  /// <summary>
  /// The previous frame's magnitudes, carried forward by
  /// <see cref="AudioConstants.SpectrumSmoothing"/>. Part of reproducing the scale
  /// the thresholds were tuned against, not a cosmetic filter.
  /// </summary>
  private readonly double[] smoothed = new double[SpectrumScale.Bins];

  private readonly int[] spectrum = new int[SpectrumScale.Bins];

  private Thread? thread;

  /// <summary>The rate the cached band ranges were derived for.</summary>
  private int bandsForRate;
  private (int Low, int High) kickBand;
  private (int Low, int High) midBand;
  private (int Low, int High) highBand;

  private SpectrumFrame? latest;
  private double measuredFps;
  private long frames;
  private long dropped;

  public SpectrumAnalyzer(IAudioSamples samples, IMonotonicClock clock, ILogger<SpectrumAnalyzer> log) {
    this.samples = samples ?? throw new ArgumentNullException(nameof(samples));
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    for(int index = 0; index < this.taper.Length; index++) {
      this.taper[index] = FastFourierTransform.HannWindow(index, this.taper.Length);
    }
  }

  /// <summary>
  /// Raised once per frame, on the analyzer thread.
  ///
  /// Where the beat detector is driven from, so a handler must be prompt and must
  /// not throw: anything slow here delays the next frame, and the frame rate is
  /// what the detector's history windows are measured in.
  /// </summary>
  public event Action<SpectrumFrame>? Analyzed;

  /// <summary>
  /// The most recent frame, or null while nothing is being heard.
  ///
  /// Null rather than a stale frame on purpose. The visualizer reads this, and a
  /// spectrum frozen at the last thing it heard before the device closed is the
  /// most misleading picture it could draw: it looks like music.
  /// </summary>
  public SpectrumFrame? Latest => Volatile.Read(ref this.latest);

  /// <summary>Measured frames a second, for the diagnostics row.</summary>
  public double Fps => Volatile.Read(ref this.measuredFps);

  /// <summary>Frames published since start.</summary>
  public long Frames => Interlocked.Read(ref this.frames);

  /// <summary>
  /// Deadlines missed entirely, meaning a whole frame was lost rather than fired
  /// late. Worth counting rather than only fixing: a rising number here is the
  /// signal that beat detection is being starved of frames, and it is otherwise
  /// indistinguishable from a quiet track.
  /// </summary>
  public long DroppedFrames => Interlocked.Read(ref this.dropped);

  public Task StartAsync(CancellationToken cancellationToken) {
    this.thread = new Thread(this.Run) {
      Name = "Flux spectrum analyzer",
      IsBackground = true,

      // Normal, where the beat scheduler runs above it, and the difference is what
      // each thread loses by being late. A beat that fires late is a beat in the
      // wrong place; a frame that arrives late is still a frame, and this one does
      // real arithmetic sixty times a second next to an audio stack it must not
      // compete with.
      Priority = ThreadPriority.Normal,
    };

    this.thread.Start();

    this.log.LogInformation(
      "Spectrum analysis is running: {Rate} frames a second over {Samples}-sample windows.",
      AudioConstants.FrameRateHz,
      AudioConstants.FftSize);

    return Task.CompletedTask;
  }

  public async Task StopAsync(CancellationToken cancellationToken) {
    await this.stopping.CancelAsync().ConfigureAwait(false);

    Thread? running = this.thread;
    if(running is not null && !running.Join(ShutdownTimeout)) {
      this.log.LogWarning(
        "The spectrum analyzer thread was still running {Timeout} ms after being asked to stop.",
        ShutdownTimeout.TotalMilliseconds);
    }
  }

  public void Dispose() {
    this.stopping.Dispose();
  }

  private void Run() {
    CancellationToken stop = this.stopping.Token;
    double next = this.clock.NowMicros;
    double fpsWindowStart = next;
    long fpsWindowFrames = 0;

    while(!stop.IsCancellationRequested) {
      double now = this.clock.NowMicros;
      double remainingMillis = (next - now) / 1000.0;

      if(remainingMillis > ArrivalToleranceMillis) {
        Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(remainingMillis, FramePeriodMillis)));
        continue;
      }

      if(this.TryAnalyze(now)) {
        fpsWindowFrames++;
        next += FramePeriodMicros;

        // Already past the following deadline too, so a whole frame was lost.
        // Snapping forward rather than firing twice: two frames from one window of
        // audio measure a difference the music never made.
        if(next < now) {
          Interlocked.Increment(ref this.dropped);
          next = now + FramePeriodMicros;
        }
      } else {
        // Nothing to analyze, which is not lateness: no device, or one that has not
        // filled a window yet. The deadline is taken fresh from the clock rather
        // than advanced, so a spell with no audio does not book a lost frame for
        // every poll it waited through.
        Thread.Sleep(IdlePollMillis);
        next = this.clock.NowMicros + FramePeriodMicros;
      }

      if(now - fpsWindowStart >= FpsWindowMicros) {
        Volatile.Write(ref this.measuredFps, fpsWindowFrames / ((now - fpsWindowStart) / 1_000_000.0));
        fpsWindowStart = now;
        fpsWindowFrames = 0;
      }
    }
  }

  /// <summary>
  /// Analyzes one window and publishes it.
  /// </summary>
  /// <returns>False when there was nothing to analyze, which is not a fault.</returns>
  private bool TryAnalyze(double nowMicros) {
    int rate = this.samples.SampleRate;

    if(rate <= 0 || !this.samples.TryReadLatest(this.window)) {
      // A device switch empties the capture buffer deliberately, and a controller
      // gets plugged in mid-set. Everything derived from the previous device is
      // dropped here, because carrying smoothing across the gap would blend two
      // signals, and the difference between two signals is what the detector calls
      // a beat.
      this.Forget();

      return false;
    }

    if(rate != this.bandsForRate) {
      this.RecomputeBands(rate);
    }

    this.Transform();

    SpectrumFrame frame = new SpectrumFrame(
      nowMicros,
      rate,
      SpectrumScale.DisplayColumns(this.spectrum, rate),
      SpectrumScale.BandEnergy(this.spectrum, this.kickBand.Low, this.kickBand.High),
      SpectrumScale.BandEnergy(this.spectrum, this.midBand.Low, this.midBand.High),
      SpectrumScale.BandEnergy(this.spectrum, this.highBand.Low, this.highBand.High));

    Volatile.Write(ref this.latest, frame);
    Interlocked.Increment(ref this.frames);

    try {
      this.Analyzed?.Invoke(frame);
    } catch(Exception ex) {
      // One misbehaving handler must not take the analyzer thread down with it: the
      // visualizer is fed from here too, and it is the instrument the detector is
      // being judged with.
      this.log.LogError(ex, "A spectrum handler threw.");
    }

    return true;
  }

  /// <summary>
  /// Windows, transforms and scales the captured samples into
  /// <see cref="spectrum"/>.
  /// </summary>
  private void Transform() {
    for(int index = 0; index < this.window.Length; index++) {
      this.transform[index].X = (float)(this.window[index] * this.taper[index]);
      this.transform[index].Y = 0;
    }

    FastFourierTransform.FFT(true, FftExponent, this.transform);

    for(int bin = 0; bin < this.spectrum.Length; bin++) {
      double real = this.transform[bin].X;
      double imaginary = this.transform[bin].Y;
      double magnitude = Math.Sqrt((real * real) + (imaginary * imaginary)) * MagnitudeScale;

      this.smoothed[bin] = (this.smoothed[bin] * AudioConstants.SpectrumSmoothing)
        + (magnitude * (1.0 - AudioConstants.SpectrumSmoothing));

      this.spectrum[bin] = SpectrumScale.ToByte(this.smoothed[bin]);
    }
  }

  private void RecomputeBands(int rate) {
    this.kickBand = SpectrumScale.BandRange(AudioConstants.KickLowHz, AudioConstants.KickHighHz, rate);
    this.midBand = SpectrumScale.BandRange(AudioConstants.MidLowHz, AudioConstants.MidHighHz, rate);
    this.highBand = SpectrumScale.BandRange(AudioConstants.HighLowHz, AudioConstants.HighHighHz, rate);
    this.bandsForRate = rate;

    this.log.LogInformation(
      "Audio bands at {Rate} Hz: kick bins {KickLow}-{KickHigh}, mids {MidLow}-{MidHigh}, highs {HighLow}-{HighHigh}.",
      rate,
      this.kickBand.Low,
      this.kickBand.High - 1,
      this.midBand.Low,
      this.midBand.High - 1,
      this.highBand.Low,
      this.highBand.High - 1);
  }

  /// <summary>
  /// Drops everything derived from audio that is no longer arriving.
  /// </summary>
  private void Forget() {
    Volatile.Write(ref this.latest, null);
    Array.Clear(this.smoothed);
    Array.Clear(this.spectrum);
  }
}
