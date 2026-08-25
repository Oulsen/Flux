using Flux.Beat;

namespace Flux.Audio;

/// <summary>
/// A level reading over the window just past.
/// </summary>
/// <param name="State">
/// What was heard. Never <see cref="AudioDeviceState.Failed"/>: this type
/// measures samples, and a device that cannot be opened produces none to measure.
/// Lifetime is <see cref="AudioCaptureService"/>'s business.
/// </param>
/// <param name="Levels">The level over the window, both figures 0 to 1.</param>
internal readonly record struct AudioMeterReading(AudioDeviceState State, AudioLevels Levels);

/// <summary>
/// Turns capture callbacks into the level a human can read off a meter, and into
/// the distinction between the three ways of hearing nothing.
///
/// It measures over a fixed window rather than reporting the last callback,
/// because a callback is about ten milliseconds long: a meter drawn from one
/// flickers far too fast to correlate with anything. A quarter of a second is the
/// rate the Phase 0 spike printed at, and pulling the channel fader was legible
/// against it - which is the entire job of this number. The correct endpoint on
/// this rig is called <c>Microfoon</c>, so a name cannot identify it and only
/// watching a level respond to a physical control can.
/// </summary>
internal sealed class AudioMeter {
  /// <summary>
  /// How long each measurement covers. Shorter flickers, longer lags visibly
  /// behind the hand on the fader.
  /// </summary>
  private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(250);

  private readonly IMonotonicClock clock;
  private readonly object gate = new object();

  private double windowEndsMicros;
  private long samples;
  private long nonZeroSamples;
  private double sumSquares;
  private double peak;

  private AudioMeterReading published = new AudioMeterReading(AudioDeviceState.NoData, AudioLevels.None);

  public AudioMeter(IMonotonicClock clock) {
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.windowEndsMicros = clock.NowMicros + (Window.TotalMilliseconds * 1000.0);
  }

  /// <summary>
  /// Folds in one capture callback. Called from the capture thread, so it does no
  /// more than arithmetic under a lock.
  /// </summary>
  public void Add(in PcmBlock block) {
    lock(this.gate) {
      this.Roll();

      this.samples += block.Samples;
      this.nonZeroSamples += block.NonZeroSamples;
      this.sumSquares += block.SumSquares;
      if(block.Peak > this.peak) {
        this.peak = block.Peak;
      }
    }
  }

  /// <summary>
  /// The last completed window.
  ///
  /// Reading also closes an overdue window, which is what makes silence visible: a
  /// device delivering nothing never calls <see cref="Add"/> at all, so without
  /// this the last reading before it went quiet would stay on screen forever.
  /// </summary>
  public AudioMeterReading Read() {
    lock(this.gate) {
      this.Roll();
      return this.published;
    }
  }

  /// <summary>
  /// Starts over for a different device, so its first reading cannot be a blend of
  /// the two.
  /// </summary>
  public void Reset() {
    lock(this.gate) {
      this.published = new AudioMeterReading(AudioDeviceState.NoData, AudioLevels.None);
      this.windowEndsMicros = this.clock.NowMicros + (Window.TotalMilliseconds * 1000.0);
      this.Clear();
    }
  }

  /// <summary>
  /// Publishes the window and starts the next one, if this one is over.
  ///
  /// Late by however much the caller was late, deliberately: the window is a
  /// measurement interval, not a schedule to be caught up with, and a fresh
  /// absolute deadline each time keeps a slow reader from compounding into
  /// windows that grow without bound.
  /// </summary>
  private void Roll() {
    double now = this.clock.NowMicros;
    if(now < this.windowEndsMicros) {
      return;
    }

    if(this.samples == 0) {
      // No callbacks at all. Ordinary for a loopback endpoint with nothing
      // playing to it, which Windows leaves silent rather than sending zeros -
      // hence a state of its own rather than an error.
      this.published = new AudioMeterReading(AudioDeviceState.NoData, AudioLevels.None);
    } else {
      // Clamped because the shared mixer can hand over floats past full scale
      // and the protocol promises 0 to 1. A reading pinned at exactly 1 is also
      // the honest description of a signal that is clipping.
      AudioLevels levels = new AudioLevels(
        Math.Clamp(Math.Sqrt(this.sumSquares / this.samples), 0, 1),
        Math.Clamp(this.peak, 0, 1));

      // Every single sample zero, not an average that rounded to it. Music at
      // -60 dBFS still has non-zero samples; a stopped deck or a closed fader
      // produces bit-exact zeros, which is what makes this distinguishable from
      // quiet at all - and it is how Phase 0 proved the FLX-10 endpoint carries
      // a digital mix rather than room sound.
      this.published = new AudioMeterReading(
        this.nonZeroSamples == 0 ? AudioDeviceState.DigitalSilence : AudioDeviceState.Signal,
        levels);
    }

    this.windowEndsMicros = now + (Window.TotalMilliseconds * 1000.0);
    this.Clear();
  }

  private void Clear() {
    this.samples = 0;
    this.nonZeroSamples = 0;
    this.sumSquares = 0;
    this.peak = 0;
  }
}
