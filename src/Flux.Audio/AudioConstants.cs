namespace Flux.Audio;

/// <summary>
/// The numbers audio beat detection is made of.
///
/// Almost all of them are transcribed from the SignalRGB scene this detector
/// reimplements, and transcribed <em>as coded</em> rather than as commented. That
/// distinction is doing real work: the scene's own comments disagree with its
/// arithmetic in places, and the version that was tuned against music over many
/// nights is the one that ran. Where the two differ,
/// <c>docs/audio-constants.md</c> records both and says which was taken.
///
/// They are constants and not settings on purpose. Every one of them was arrived
/// at against real music, they interact (the strong-beat thresholds are multiples
/// of the ordinary ones), and a knob per threshold would be twenty ways to make
/// detection worse and one to make it better. The single knob that exists is
/// <see cref="AudioOptions.Sensitivity"/>, which is the scene's own.
/// </summary>
internal static class AudioConstants {
  /// <summary>
  /// Samples per FFT, so 42.7 ms of audio at 48 kHz and a 23.4 Hz bin.
  ///
  /// The trade is time against frequency and both ends matter here. Half this and
  /// the bass bins are too coarse to separate a kick from the bass line under it;
  /// double it and the window is 85 ms long, which smears the attack the detector
  /// triggers on across most of a sixteenth note at 175 BPM.
  /// </summary>
  public const int FftSize = 2048;

  /// <summary>
  /// Analyzer frames a second.
  ///
  /// It is the scene's rate, which ran on <c>requestAnimationFrame</c>, and it is
  /// not arbitrary: the history windows below are sized in frames, and the
  /// 280 ms minimum beat interval is only 17 frames at this rate. It is also
  /// deliberately faster than the 30 Hz the spectrum reaches a browser at, so a
  /// phone's refresh rate is never in charge of beat detection.
  /// </summary>
  public const int FrameRateHz = 60;

  /// <summary>
  /// Columns in the spectrum the protocol sends. Part of the wire contract, so it
  /// lives here rather than in the encoder.
  /// </summary>
  public const int DisplayBins = 64;

  /// <summary>
  /// The span the display columns are spread over, log-spaced.
  ///
  /// Flux's choice rather than the scene's: there the visible width is a user
  /// setting that defaults to the first 2.5 kHz. A visualizer being read as a test
  /// instrument wants the whole picture, and the top end has to stop somewhere
  /// short of the Nyquist frequency because the last octave of a 48 kHz capture is
  /// nothing anyone can hear.
  /// </summary>
  public const double DisplayLowHz = 30;

  /// <inheritdoc cref="DisplayLowHz"/>
  public const double DisplayHighHz = 16_000;

  /// <summary>
  /// The band the beat detector triggers on: the scene's bins 1 to 3, which on its
  /// 50 Hz-per-bin scale is 50 to 200 Hz.
  ///
  /// The scene's comment says "kick drums are ~50-100Hz" while its indices cover
  /// twice that, and the wider range is the one that ran. It is also the more
  /// defensible of the two: a kick's fundamental sits near 50 Hz but its attack
  /// (which is what spectral flux measures) carries well above 100.
  /// </summary>
  public const double KickLowHz = 50;

  /// <inheritdoc cref="KickLowHz"/>
  public const double KickHighHz = 200;

  /// <summary>
  /// The band a kick has to beat to count: the scene's bins 4 to 8, so 200 to
  /// 450 Hz. Snares and claps live here, and the ratio test below is the only
  /// thing separating them from kicks.
  /// </summary>
  public const double MidLowHz = 200;

  /// <inheritdoc cref="MidLowHz"/>
  public const double MidHighHz = 450;

  /// <summary>
  /// The band shown as "highs": the scene's bins 15 to 25, so 750 to 1300 Hz.
  ///
  /// The scene calls these "hi-hats are ~8k+Hz", which its own scale contradicts
  /// by nearly an order of magnitude. As coded wins, and the cost of being wrong
  /// is bounded: no decision is taken on this band, it is drawn.
  /// </summary>
  public const double HighLowHz = 750;

  /// <inheritdoc cref="HighLowHz"/>
  public const double HighHighHz = 1300;

  /// <summary>
  /// The bottom of the decibel range a bin is scaled over.
  ///
  /// This pair is the only number in the file measured against this rig rather
  /// than transcribed from the scene, and it had to be. The scene reads a byte
  /// array from its host, so every threshold below is a number on that scale and
  /// the scale itself is the one thing the scene could not tell us. The first
  /// answer here was the Web Audio <c>AnalyserNode</c> convention, -100 dB to
  /// -30 dB across 0 to 255, on the grounds that it is the only published mapping
  /// that turns a spectrum into bytes. It is the right shape and the wrong place:
  /// the scene's host captured desktop audio, which sits inside that window,
  /// while a DJ master bus sits above it.
  ///
  /// Measured on a four minute capture of three beatmatched tracks off the
  /// FLX-10's master return, with Link independently reporting 122 BPM, the old
  /// ceiling clamped 22.6% of all kick-band bins - so a kick's onset became a
  /// small rise on a plateau of 255s, which is why the detector called 125 while
  /// locking for 9% of the run. The derivation is in
  /// <c>docs/audio-constants.md</c> and the harness that produced it is
  /// <c>WindowCalibrationTests</c>.
  ///
  /// The span stays 70 dB. It is what converts a decibel rise into a byte rise,
  /// so widening it would retune every threshold below at once, silently.
  ///
  /// If detection turns out to be uniformly too eager or too deaf against real
  /// music, <see cref="AudioOptions.Sensitivity"/> is still the knob that
  /// compensates without touching this.
  /// </summary>
  public const double SpectrumMinDb = -76;

  /// <summary>
  /// The top of that range, and the number the measurement actually moved.
  ///
  /// Chosen as the middle of a plateau rather than as a best single reading,
  /// because both edges of it fail and they fail differently. Below about -12 dB
  /// the loudest bins start clamping again and the reported tempo climbs; above
  /// about -2 dB the bins are faithful but the detector's absolute energy gates
  /// stop being met in quiet passages, and at 0 dB it collapses to 114 BPM. Across
  /// -10 dB to -2 dB it reported 122.0 to 122.7 against a true 122 and held a lock
  /// for three quarters of the run, so this sits in the centre of that: 11 dB of
  /// headroom above the loudest bin measured, 6 dB above the cliff.
  /// </summary>
  public const double SpectrumMaxDb = -6;

  /// <summary>
  /// How much of each bin is carried over from the previous frame.
  ///
  /// Part of the same reconstruction as the decibel range, and load-bearing for a
  /// different reason: spectral flux is a difference between consecutive frames,
  /// so smoothing is what decides how spiky that difference is. The thresholds
  /// were tuned against a smoothed spectrum, and feeding them a raw one would make
  /// every frame look like an onset.
  /// </summary>
  public const double SpectrumSmoothing = 0.8;

  /// <summary>Default sensitivity, on the scene's 1 to 100 scale.</summary>
  public const int DefaultSensitivity = 50;

  /// <inheritdoc cref="DefaultSensitivity"/>
  public const int MinSensitivity = 1;

  /// <inheritdoc cref="DefaultSensitivity"/>
  public const int MaxSensitivity = 100;

  /// <summary>
  /// Sensitivity becomes a multiplier on the average flux:
  /// <c>4.0 - sensitivity / 100 * 2.5</c>, so 1 asks for nearly four times the
  /// average and 100 asks for one and a half.
  /// </summary>
  public const double SensitivityBase = 4.0;

  /// <inheritdoc cref="SensitivityBase"/>
  public const double SensitivitySpan = 2.5;

  /// <summary>
  /// Added to the adaptive threshold so silence cannot trigger.
  ///
  /// Without it the threshold is a multiple of the recent average, and the average
  /// over a quiet passage is near zero, so any flicker at all clears it. This is
  /// the floor under the adaptive part.
  /// </summary>
  public const double FluxThresholdOffset = 0.015;

  /// <summary>How much bass energy a frame needs before a beat is considered at all.</summary>
  public const double MinEnergyFloor = 0.06;

  /// <summary>
  /// The shortest gap between beats, which caps detection at about 214 BPM. Its
  /// real job is not the ceiling but the snare that follows a kick 100 ms later:
  /// without this, half the beats are counted twice and the tempo comes out
  /// double.
  /// </summary>
  public const double MinBeatIntervalMillis = 280;

  /// <summary>
  /// How much of the mid band the kick band has to exceed for a hit to count as a
  /// kick. The one test that distinguishes a kick drum from a snare, and the
  /// reason a track with a loud backbeat does not read as twice its tempo.
  /// </summary>
  public const double KickOverMidRatio = 0.7;

  /// <summary>
  /// A strong beat needs this multiple of <see cref="MinEnergyFloor"/>.
  ///
  /// Strong beats are the ones the grid is anchored to, so they are deliberately a
  /// stricter test rather than a louder one: a beat that only just cleared the
  /// ordinary bar is not evidence worth moving the whole timeline for.
  /// </summary>
  public const double StrongBeatEnergyMultiplier = 2.5;

  /// <inheritdoc cref="StrongBeatEnergyMultiplier"/>
  public const double StrongBeatFluxMultiplier = 1.8;

  /// <inheritdoc cref="StrongBeatEnergyMultiplier"/>
  public const double MinStrongBeatIntervalMillis = 350;

  /// <summary>
  /// The shortest history the flux average is taken over, in frames.
  ///
  /// The scene takes the longer of this and half a second's worth of frames, which
  /// matters because its frame rate was whatever the browser gave it. At Flux's
  /// fixed 60 fps the half second wins, and the floor only ever applies while the
  /// analyzer is starting up.
  /// </summary>
  public const int HistoryMinFrames = 20;

  /// <inheritdoc cref="HistoryMinFrames"/>
  public const double HistorySeconds = 0.5;

  /// <summary>
  /// Frames ignored after a start or a device change.
  ///
  /// Not the scene's, and needed because of one difference between it and Flux: its
  /// host handed it a spectrum that was already running, while ours starts at
  /// silence and climbs to the signal over several frames as the smoothing fills.
  /// That climb is a rising flux with no music behind it. A third of a second is
  /// enough for the carry-over to fall under a twentieth, and far cheaper than one
  /// phantom onset inside the tempo estimate.
  /// </summary>
  public const int WarmUpFrames = 20;

  /// <summary>Beat timestamps kept for the tempo estimate.</summary>
  public const int MaxBeatTimes = 20;

  /// <summary>
  /// A beat closer than this to the previous one is not recorded for tempo
  /// estimation, though it still counts as a beat.
  ///
  /// Tighter than <see cref="MinBeatIntervalMillis"/> and therefore dead code as
  /// coded, since nothing closer than 280 ms gets this far. Transcribed anyway:
  /// it says what the estimator is protecting itself from, and a later change to
  /// the minimum interval would wake it up.
  /// </summary>
  public const double MinBeatSpacingForBpmMillis = 250;

  /// <summary>Beats needed before a tempo is estimated at all.</summary>
  public const int MinBeatsForBpm = 6;

  /// <summary>Usable intervals needed among them.</summary>
  public const int MinIntervalsForBpm = 4;

  /// <summary>
  /// The interval band an estimate is drawn from: 60 to 200 BPM. Intervals outside
  /// it are dropped rather than averaged in, which is what stops one missed kick
  /// from halving the answer.
  /// </summary>
  public const double BpmIntervalMinMillis = 300;

  /// <inheritdoc cref="BpmIntervalMinMillis"/>
  public const double BpmIntervalMaxMillis = 1000;

  /// <summary>
  /// How close an interval must sit to an exact multiple of the current beat
  /// interval to be read as missed kicks and divided down, as a fraction of the
  /// beat interval.
  ///
  /// Not the scene's; the derivation is in <c>docs/audio-constants.md</c>. The
  /// floor is frame quantization: a doubled interval lands within two frames of
  /// exact, which is under 6% of a beat across the whole tempo band. The ceiling
  /// is the off-grid contamination the unlock guards against: an interval of one
  /// and a half beats folded by two misses the beat by 25%, and folding it would
  /// launder inconsistent evidence into consistent-looking evidence. A tenth sits
  /// between the two with at least a factor of two to spare each way.
  /// </summary>
  public const double FoldTolerance = 0.10;

  /// <summary>
  /// How much of the previous estimate survives a new one while the tempo is
  /// unlocked. Heavily weighted towards history: a tempo that moves as fast as one
  /// bad measurement is a tempo nothing can be anchored to.
  /// </summary>
  public const double BpmSmoothingHeld = 0.8;

  /// <inheritdoc cref="BpmSmoothingHeld"/>
  public const double BpmSmoothingFresh = 0.2;

  /// <summary>Beats needed before the tempo can lock.</summary>
  public const int LockAfterBeats = 12;

  /// <summary>
  /// How consistent the intervals have to be to lock, as standard deviation over
  /// mean. Under a fifth is loose enough for a human-played track and tight enough
  /// that a passage of half-time drums does not qualify.
  /// </summary>
  public const double LockMaxRelativeStdDev = 0.2;

  /// <summary>
  /// How far a fresh estimate has to be from the locked tempo before the lock is
  /// questioned, in BPM.
  /// </summary>
  public const double UnlockBpmDifference = 25;

  /// <summary>
  /// How long a lock is protected for.
  ///
  /// The pair is asymmetric on purpose: locking takes twelve beats, unlocking
  /// takes a large disagreement <em>and</em> fifteen seconds. A mix runs two
  /// tracks at once for far longer than a bar, and a detector that unlocked on the
  /// incoming track's first kick would lose the outgoing track's tempo while it is
  /// still what the floor is dancing to.
  /// </summary>
  public const double UnlockAfterLockMillis = 15_000;

  /// <summary>Bass energy below this counts as quiet, for drop detection.</summary>
  public const double QuietEnergyThreshold = 0.02;

  /// <summary>How long it has to stay quiet before the return can be a drop.</summary>
  public const double MinQuietForDropMillis = 800;

  /// <summary>
  /// How much louder than the quiet passage the return has to be. Five times, and
  /// an absolute floor beside it, because a ratio alone is met by any rise out of
  /// near-silence.
  /// </summary>
  public const double DropEnergyRatio = 5;

  /// <inheritdoc cref="DropEnergyRatio"/>
  public const double DropMinEnergy = 0.25;

  /// <summary>
  /// The smallest energy a ratio is taken against, so the ratio stays finite when
  /// the previous frame was bit-exact silence.
  /// </summary>
  public const double DropRatioEpsilon = 0.001;

  /// <summary>
  /// The shortest gap between drops.
  ///
  /// A drop rebases the beat numbering and re-arms the bar, which is the most
  /// disruptive thing this detector can do, so it is rate limited twice: a
  /// cooldown, and a cap per half minute. A breakdown with stabs in it would
  /// otherwise re-anchor the grid every couple of bars.
  /// </summary>
  public const double DropCooldownMillis = 5_000;

  /// <inheritdoc cref="DropCooldownMillis"/>
  public const int MaxDropsPerWindow = 3;

  /// <inheritdoc cref="DropCooldownMillis"/>
  public const double DropCountWindowMillis = 30_000;

  /// <summary>
  /// The confidence a locked tempo starts from, before interval consistency adds
  /// the rest.
  ///
  /// Not the scene's, which never reported confidence at all. These four define
  /// the shape of the protocol's <c>confidence</c> field, and the shape carries
  /// the meaning: the locked band starts above where the unlocked band ends, so a
  /// locked estimate always outranks an unlocked one, because a lock is a
  /// qualitative claim about twelve consistent beats that no amount of unlocked
  /// consistency should be able to shout over.
  /// </summary>
  public const double ConfidenceLockedFloor = 0.6;

  /// <summary>
  /// What interval consistency adds on top of <see cref="ConfidenceLockedFloor"/>,
  /// reaching 1 at a spread of zero. The remainder of the scale, so the two
  /// numbers move together by construction.
  /// </summary>
  public const double ConfidenceLockedSpan = 0.4;

  /// <summary>
  /// The most an unlocked tempo can report. Below the locked floor on purpose, and
  /// the tenth of headroom between them keeps the bands visibly apart rather than
  /// merely non-overlapping.
  /// </summary>
  public const double ConfidenceUnlockedCeiling = 0.5;

  /// <summary>
  /// The interval spread at which consistency reads zero, as a multiple of
  /// <see cref="LockMaxRelativeStdDev"/>. Two, so the lock bar itself sits at the
  /// middle of the consistency scale: intervals exactly at the bar read 0.5, and
  /// only a spread twice as loose as anything lockable reads zero.
  /// </summary>
  public const double ConfidenceSpreadRange = 2.0;
}
