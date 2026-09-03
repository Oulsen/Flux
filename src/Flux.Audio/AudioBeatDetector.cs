using Flux.Beat;

namespace Flux.Audio;

/// <summary>
/// Everything the detector concluded from one frame, including where it now thinks
/// the beat is.
///
/// One record per frame rather than properties to read afterwards, and that is
/// deliberate: the detector runs on the analyzer thread and is not thread-safe, so
/// the web layer must never read it directly. It reads the last decision instead,
/// which is a value and therefore safe to hand across a thread.
/// </summary>
/// <param name="Strength">What this frame was judged to be.</param>
/// <param name="Drop">Whether this frame is the return out of a breakdown.</param>
/// <param name="Flux">The spectral flux measured, which is the number the threshold is compared against.</param>
/// <param name="Bpm">The current tempo estimate, or zero before there is one.</param>
/// <param name="Locked">Whether that estimate has settled.</param>
/// <param name="BeatsDetected">Onsets accepted since the last reset.</param>
/// <param name="LastBeatMicros">When the last onset was accepted, or null if there has not been one.</param>
/// <param name="Confidence">How much the detector trusts its own tempo, 0 to 1.</param>
/// <param name="Timeline">
/// A timeline to publish, or null when this frame changed nothing worth publishing.
/// Most frames are null: the grid is re-anchored on strong beats and on drops, not
/// sixty times a second.
/// </param>
/// <param name="Rebase">
/// Whether <paramref name="Timeline"/> renumbers the beats rather than re-anchoring
/// them, which is what a drop and a first estimate do. Anything anchored to a beat
/// index has to re-arm when this is set.
/// </param>
internal readonly record struct BeatDecision(
  BeatStrength Strength,
  bool Drop,
  double Flux,
  double Bpm,
  bool Locked,
  long BeatsDetected,
  double? LastBeatMicros,
  double Confidence,
  BeatTimeline? Timeline,
  bool Rebase);

/// <summary>
/// Finds the beat in a spectrum, one frame at a time.
///
/// A reimplementation of the beat detection in the SignalRGB scene this project
/// grew out of, and reimplemented rather than ported for one reason: that code had
/// been watched against real music for a long time, so its <em>numbers</em> are
/// evidence, while its structure is a visualizer's main loop with a beat detector
/// woven through it. The numbers are transcribed exactly, in
/// <see cref="AudioConstants"/>. The structure is not.
///
/// The substantive change is what comes out. The scene kept its own beat grid: a
/// tempo, an interval, and a counter it advanced by wall-clock time. Flux already
/// has one of those, shared by every source, so this class publishes
/// <see cref="BeatTimeline"/>s instead and lets <see cref="BeatGrid"/> do the
/// counting. That is what makes an audio tempo interchangeable with a Link tempo
/// rather than a second opinion running alongside it.
///
/// It is pure in the sense that matters: no audio device, no clock of its own, no
/// threads. Every timing decision comes from the timestamp on the frame it is
/// handed, so a recorded run replays identically and a test does not have to wait
/// for real seconds to pass.
/// </summary>
internal sealed class AudioBeatDetector {
  private readonly int sensitivity;

  /// <summary>Recent flux values, for the adaptive threshold.</summary>
  private readonly List<double> fluxHistory = new List<double>(AudioConstants.HistoryMinFrames * 2);

  /// <summary>When onsets were accepted, in milliseconds, for the tempo estimate.</summary>
  private readonly List<double> beatTimes = new List<double>(AudioConstants.MaxBeatTimes);

  private int warmUpFramesLeft = AudioConstants.WarmUpFrames;
  private double? lastFrameMicros;
  private double previousKickEnergy;

  private double lastBeatMillis = double.NegativeInfinity;
  private double lastStrongBeatMillis = double.NegativeInfinity;
  private double? lastBeatMicros;
  private long beatsDetected;

  private double bpm;
  private double smoothedBpm;
  private bool locked;
  private double lockedAtMillis;

  /// <summary>
  /// Spread of the intervals the last estimate was drawn from, as a fraction of
  /// their mean. Not in the scene, which computes it only when deciding to lock:
  /// confidence has to be reported every frame, and this is the only measurement
  /// the detector has of its own reliability.
  /// </summary>
  private double relativeSpread = 1.0;

  private bool wasQuiet;
  private double quietSinceMillis;
  private double lastDropMillis = double.NegativeInfinity;
  private int recentDrops;
  private double? dropWindowStartMillis;

  private bool published;
  private double anchorBeat;
  private double anchorMicros;

  /// <param name="sensitivity">
  /// The scene's beat sensitivity, 1 to 100. Clamped rather than rejected: it
  /// arrives from configuration that has already been validated, and a detector
  /// that refuses to run is a worse answer than one that runs at the nearest legal
  /// setting.
  /// </param>
  public AudioBeatDetector(int sensitivity = AudioConstants.DefaultSensitivity) {
    this.sensitivity = Math.Clamp(sensitivity, AudioConstants.MinSensitivity, AudioConstants.MaxSensitivity);
  }

  /// <summary>
  /// Judges one frame.
  ///
  /// Called on the analyzer thread, once per frame, in order. Out-of-order or
  /// repeated frames are not handled and do not occur: the analyzer never fires
  /// two frames for one deadline.
  /// </summary>
  public BeatDecision Observe(SpectrumFrame frame) {
    ArgumentNullException.ThrowIfNull(frame);

    double nowMillis = frame.TimestampMicros / 1000.0;
    double elapsedSeconds = this.lastFrameMicros is double previous
      ? (frame.TimestampMicros - previous) / 1_000_000.0
      : 1.0 / AudioConstants.FrameRateHz;
    this.lastFrameMicros = frame.TimestampMicros;

    double energy = frame.Kick;
    bool drop = this.CheckForDrop(energy, nowMillis);

    double flux = Math.Max(0, energy - this.previousKickEnergy);
    this.previousKickEnergy = energy;

    double threshold = this.UpdateThreshold(flux, elapsedSeconds);

    BeatStrength strength = BeatStrength.None;

    if(this.warmUpFramesLeft > 0) {
      // The smoothed spectrum climbs from silence over the first few frames after a
      // start or a device change, and that climb is a rising flux with no music
      // behind it. The scene never saw this: its host handed it an already running
      // spectrum. Ignoring the ramp costs a third of a second and saves a phantom
      // onset from entering the tempo estimate, where one bad interval costs far
      // longer than that.
      this.warmUpFramesLeft--;
    } else {
      strength = this.Judge(frame, flux, threshold, energy, nowMillis);
    }

    return this.Decide(frame, strength, drop, flux);
  }

  /// <summary>
  /// Forgets everything: a different device, or audio that stopped arriving.
  ///
  /// Everything, including the tempo, and that is the point. The alternative is
  /// keeping a tempo across a gap of unknown length, which produces a timeline
  /// whose anchor is somewhere in the silence and a beat display confidently in the
  /// wrong place. A source with nothing to say should say nothing.
  /// </summary>
  public void Reset() {
    this.fluxHistory.Clear();
    this.beatTimes.Clear();

    this.warmUpFramesLeft = AudioConstants.WarmUpFrames;
    this.lastFrameMicros = null;
    this.previousKickEnergy = 0;

    this.lastBeatMillis = double.NegativeInfinity;
    this.lastStrongBeatMillis = double.NegativeInfinity;
    this.lastBeatMicros = null;
    this.beatsDetected = 0;

    this.bpm = 0;
    this.smoothedBpm = 0;
    this.locked = false;
    this.lockedAtMillis = 0;
    this.relativeSpread = 1.0;

    this.wasQuiet = false;
    this.quietSinceMillis = 0;
    this.lastDropMillis = double.NegativeInfinity;
    this.recentDrops = 0;
    this.dropWindowStartMillis = null;

    this.published = false;
    this.anchorBeat = 0;
    this.anchorMicros = 0;
  }

  /// <summary>
  /// Looks for the moment a breakdown ends.
  ///
  /// A drop is the one event in a track where the "1" is unambiguous, which is why
  /// it is worth detecting at all: it is the only downbeat audio analysis can find
  /// without being told. The three conditions are all necessary. Silence for long
  /// enough rules out a passing gap, a ratio against the quiet passage rules out a
  /// gradual build, and an absolute energy floor rules out the ratio's own failure
  /// mode, where any rise out of near-silence is five times something near zero.
  /// </summary>
  private bool CheckForDrop(double energy, double nowMillis) {
    if(this.dropWindowStartMillis is not double windowStart
      || nowMillis - windowStart > AudioConstants.DropCountWindowMillis) {
      this.recentDrops = 0;
      this.dropWindowStartMillis = nowMillis;
    }

    if(energy < AudioConstants.QuietEnergyThreshold) {
      if(!this.wasQuiet) {
        this.quietSinceMillis = nowMillis;
        this.wasQuiet = true;
      }

      return false;
    }

    bool wasQuietLongEnough = this.wasQuiet
      && nowMillis - this.quietSinceMillis > AudioConstants.MinQuietForDropMillis;
    bool allowed = nowMillis - this.lastDropMillis > AudioConstants.DropCooldownMillis
      && this.recentDrops < AudioConstants.MaxDropsPerWindow;

    this.wasQuiet = false;

    if(!wasQuietLongEnough || !allowed) {
      return false;
    }

    double ratio = energy / (this.previousKickEnergy + AudioConstants.DropRatioEpsilon);
    if(ratio <= AudioConstants.DropEnergyRatio || energy <= AudioConstants.DropMinEnergy) {
      return false;
    }

    this.lastDropMillis = nowMillis;
    this.recentDrops++;

    // The tempo survives a drop and the phase does not, which is the scene's own
    // choice and the right one: a drop is where the bar restarts, not where the
    // track changes speed.
    this.beatTimes.Clear();

    return true;
  }

  /// <summary>
  /// Folds this frame's flux into the history and returns the bar an onset has to
  /// clear.
  ///
  /// Adaptive because a fixed threshold cannot work across a quiet intro and a
  /// mastered-loud chorus, and offset by a constant because the adaptive part alone
  /// goes to zero in silence, where every flicker then clears it.
  /// </summary>
  private double UpdateThreshold(double flux, double elapsedSeconds) {
    int length = Math.Max(
      AudioConstants.HistoryMinFrames,
      (int)(AudioConstants.HistorySeconds / Math.Max(elapsedSeconds, 1e-6)));

    this.fluxHistory.Add(flux);
    while(this.fluxHistory.Count > length) {
      this.fluxHistory.RemoveAt(0);
    }

    double total = 0;
    foreach(double value in this.fluxHistory) {
      total += value;
    }

    double average = total / this.fluxHistory.Count;
    double multiplier = AudioConstants.SensitivityBase
      - (this.sensitivity / 100.0 * AudioConstants.SensitivitySpan);

    return (average * multiplier) + AudioConstants.FluxThresholdOffset;
  }

  /// <summary>
  /// Applies the four tests an onset has to pass, then the three more that make it
  /// a strong one.
  /// </summary>
  private BeatStrength Judge(
    SpectrumFrame frame,
    double flux,
    double threshold,
    double energy,
    double nowMillis) {
    // The last of the four is the one that earns its keep: bass has to dominate the
    // mids. Without it a loud snare on the backbeat is indistinguishable from a
    // kick, every other onset is spurious, and the tempo comes out doubled.
    bool onset = flux > threshold
      && energy > AudioConstants.MinEnergyFloor
      && nowMillis - this.lastBeatMillis > AudioConstants.MinBeatIntervalMillis
      && frame.Kick > frame.Mids * AudioConstants.KickOverMidRatio;

    if(!onset) {
      return BeatStrength.None;
    }

    this.lastBeatMillis = nowMillis;
    this.lastBeatMicros = frame.TimestampMicros;
    this.beatsDetected++;

    bool strong = flux > threshold * AudioConstants.StrongBeatFluxMultiplier
      && energy > AudioConstants.MinEnergyFloor * AudioConstants.StrongBeatEnergyMultiplier
      && nowMillis - this.lastStrongBeatMillis > AudioConstants.MinStrongBeatIntervalMillis;

    if(strong) {
      this.lastStrongBeatMillis = nowMillis;
    }

    this.RecordBeatTime(nowMillis);
    this.UpdateTempo(nowMillis);

    return strong ? BeatStrength.Strong : BeatStrength.Beat;
  }

  private void RecordBeatTime(double nowMillis) {
    double sinceLast = this.beatTimes.Count > 0 ? nowMillis - this.beatTimes[^1] : double.PositiveInfinity;

    // Unreachable as coded, because nothing closer than the minimum beat interval
    // gets this far. Kept because it says what the estimator is protecting itself
    // from, and a later change to that interval would wake it up.
    if(sinceLast <= AudioConstants.MinBeatSpacingForBpmMillis) {
      return;
    }

    this.beatTimes.Add(nowMillis);
    while(this.beatTimes.Count > AudioConstants.MaxBeatTimes) {
      this.beatTimes.RemoveAt(0);
    }
  }

  /// <summary>
  /// Re-estimates the tempo from the intervals between recent onsets.
  ///
  /// The median rather than the mean, for the same reason tap tempo uses one: the
  /// error is a missed kick or an extra one, not a general wobble, and a mean
  /// spreads a single mistake across the whole answer.
  /// </summary>
  private void UpdateTempo(double nowMillis) {
    if(this.beatTimes.Count < AudioConstants.MinBeatsForBpm) {
      return;
    }

    List<double> intervals = new List<double>(this.beatTimes.Count);
    for(int index = 1; index < this.beatTimes.Count; index++) {
      double interval = this.beatTimes[index] - this.beatTimes[index - 1];
      if(interval > AudioConstants.BpmIntervalMinMillis && interval < AudioConstants.BpmIntervalMaxMillis) {
        intervals.Add(interval);
      }
    }

    if(intervals.Count < AudioConstants.MinIntervalsForBpm) {
      return;
    }

    intervals.Sort();

    // The lower of the two middle values on an even count, which is what the scene
    // does. Transcribed rather than corrected to a true median: on a run of
    // intervals this short the difference is under a millisecond, and the tuning
    // that follows was done against this.
    double median = intervals[intervals.Count / 2];
    double rawBpm = 60_000.0 / median;

    double mean = 0;
    foreach(double interval in intervals) {
      mean += interval;
    }

    mean /= intervals.Count;

    double variance = 0;
    foreach(double interval in intervals) {
      variance += (interval - mean) * (interval - mean);
    }

    variance /= intervals.Count;
    this.relativeSpread = mean > 0 ? Math.Sqrt(variance) / mean : 1.0;

    if(this.locked) {
      this.MaybeUnlock(rawBpm, nowMillis);

      return;
    }

    this.smoothedBpm = this.smoothedBpm == 0
      ? rawBpm
      : (this.smoothedBpm * AudioConstants.BpmSmoothingHeld) + (rawBpm * AudioConstants.BpmSmoothingFresh);
    this.bpm = this.smoothedBpm;

    if(this.beatTimes.Count >= AudioConstants.LockAfterBeats
      && this.relativeSpread < AudioConstants.LockMaxRelativeStdDev) {
      // Locking takes the mean rather than the median, deliberately: the median was
      // the robust choice while the estimate was still being argued about, and once
      // the intervals are known to be consistent the mean is the more precise of
      // the two.
      this.locked = true;
      this.lockedAtMillis = nowMillis;
      this.bpm = 60_000.0 / mean;
      this.smoothedBpm = this.bpm;
    }
  }

  /// <summary>
  /// Gives up a locked tempo, but only under protest.
  ///
  /// Twelve beats to lock and a large disagreement sustained for fifteen seconds to
  /// unlock, and the asymmetry is the point. A mix runs two tracks at once for far
  /// longer than a bar, and a detector that unlocked on the incoming track's first
  /// kick would throw away the tempo the floor is still dancing to.
  /// </summary>
  private void MaybeUnlock(double rawBpm, double nowMillis) {
    bool disagrees = Math.Abs(rawBpm - this.smoothedBpm) > AudioConstants.UnlockBpmDifference;
    bool oldEnough = nowMillis - this.lockedAtMillis > AudioConstants.UnlockAfterLockMillis;

    if(!disagrees || !oldEnough) {
      return;
    }

    this.locked = false;
    this.smoothedBpm = rawBpm;
    this.bpm = rawBpm;
    this.beatTimes.Clear();
  }

  /// <summary>
  /// Turns this frame's conclusions into a decision, and decides whether the grid
  /// is told about it.
  ///
  /// Only two things move the timeline: a drop, which says where the bar starts,
  /// and a strong beat, which says where a beat is. Everything else is left alone,
  /// because a timeline republished sixty times a second is a timeline nothing
  /// downstream can tell has changed.
  /// </summary>
  private BeatDecision Decide(SpectrumFrame frame, BeatStrength strength, bool drop, double flux) {
    BeatTimeline? timeline = null;
    bool rebase = false;

    if(this.bpm > 0) {
      if(drop || !this.published) {
        // A drop renumbers from zero, and so does the first estimate there has ever
        // been. Both are statements that the old numbering means nothing, which is
        // exactly what the grid needs to hear before it re-arms the bar.
        timeline = BeatTimeline.FromBpm(this.bpm, beat: 0, frame.TimestampMicros);
        rebase = true;

        this.anchorBeat = 0;
        this.anchorMicros = frame.TimestampMicros;
        this.published = true;
      } else if(strength == BeatStrength.Strong) {
        timeline = this.ReAnchor(frame.TimestampMicros);
      }
    }

    if(timeline is BeatTimeline candidate && !candidate.IsPlausible) {
      // Cannot happen from a bounded estimate, so this guards a future caller
      // rather than these two. It fails by publishing nothing instead of by putting
      // a nonsense tempo on stage.
      timeline = null;
      rebase = false;
    }

    return new BeatDecision(
      strength,
      drop,
      flux,
      this.bpm,
      this.locked,
      this.beatsDetected,
      this.lastBeatMicros,
      this.Confidence(),
      timeline,
      rebase);
  }

  /// <summary>
  /// Moves the anchor onto this beat without renumbering.
  ///
  /// This is the mitigation for the phase drift the plan named as the phase's
  /// biggest risk. A smoothed tempo extrapolated from one anchor is right about the
  /// interval and slowly wrong about the phase, so every strong beat moves the
  /// anchor onto a beat that was actually heard. The beat <em>number</em> is
  /// carried forward rather than reset, which is what keeps the bar anchor alive:
  /// the grid keeps an anchor across a numbering shift under half a beat and
  /// re-arms it for anything larger, so restarting at zero here would throw the
  /// downbeat away on every kick.
  /// </summary>
  private BeatTimeline ReAnchor(double atMicros) {
    double microsPerBeat = 60_000_000.0 / this.bpm;

    // At least one: a strong beat is 350 ms apart at the closest, so rounding to
    // zero means the tempo estimate is badly wrong, and standing still would then
    // stack several heard beats onto one number.
    double elapsed = Math.Max(1, Math.Round((atMicros - this.anchorMicros) / microsPerBeat));

    this.anchorBeat += elapsed;
    this.anchorMicros = atMicros;

    return BeatTimeline.FromBpm(this.bpm, this.anchorBeat, atMicros);
  }

  /// <summary>
  /// How much the detector trusts its own tempo.
  ///
  /// Two ingredients, ordered so that the bands cannot overlap: a locked estimate
  /// always reports more than an unlocked one, because the lock is a qualitative
  /// statement about twelve consistent beats, and within each band the spread of
  /// the intervals is what varies. It exists so the source selector can show that
  /// audio is a worse bet than Link right now without the user having to switch to
  /// it and watch.
  /// </summary>
  private double Confidence() {
    if(this.bpm <= 0) {
      return 0;
    }

    double consistency = Math.Clamp(
      1.0 - (this.relativeSpread / (2.0 * AudioConstants.LockMaxRelativeStdDev)),
      0,
      1);

    return this.locked ? 0.6 + (0.4 * consistency) : 0.5 * consistency;
  }
}
