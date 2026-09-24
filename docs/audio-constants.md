# Audio constants

Every number audio beat detection runs on, where it came from, and what it means.

Flux's audio beat detector is a reimplementation of the beat detection in a
SignalRGB visualizer scene that had been watched against real music, night after
night, for a long time. That history is the reason its numbers are worth copying
exactly: they are not derived from theory, they are what turned out to work. The
code is not copied - its structure is a visualizer's main loop with a detector
woven through it - but the constants are, and they live in
`src/Flux.Audio/AudioConstants.cs`.

This document is the audit trail for that transcription. It exists because the
constants are meaningless without their scale: every threshold below is a number
about a 0 to 255 spectrum on a particular decibel range, so reproducing the scale
is a precondition for the thresholds meaning anything at all. Three things are
recorded here, in this order, because each depends on the one before it: the
scale, the frequency bands, and then the thresholds.

Where the scene's comments and the scene's arithmetic disagree, **as coded wins**,
and both are written down. The version that ran is the version that was tuned.

## The scale

The scene reads a 200-element array of bytes from its host, one value per
frequency band, and a companion audio level in decibels. Everything else is
derived from those bytes, so the byte scale is load-bearing.

The host does not document how a spectrum becomes bytes. Flux reconstructs it as
the Web Audio `AnalyserNode` convention, which is the only published mapping that
turns a spectrum into a 0 to 255 array, and which fits the one other clue
available: the companion level is in decibels rather than linear.

The convention's *shape* survived contact with real music and its *position* did
not. The window is 70 dB wide as the convention says, and it now sits 24 dB higher
than the convention puts it, because a DJ master bus is 24 dB louder than the
desktop audio the convention was written for. That is measured rather than
reasoned: see below.

| Step | Value | Flux symbol | Why |
|---|---|---|---|
| Window | Hann, 2048 samples | `FftSize` | 42.7 ms at 48 kHz. Half of it and the bass bins cannot separate a kick from the bass line under it; double it and the window smears the attack across most of a sixteenth note at 175 BPM |
| Magnitude | `4 x` the bin magnitude | `SpectrumAnalyzer.MagnitudeScale` | The Hann window sums to half its length and a real signal's energy splits between the positive and negative frequency, so a full-scale tone arrives at a quarter of full scale |
| Smoothing | 0.8 of the previous frame | `SpectrumSmoothing` | Spectral flux is a difference between consecutive frames, so smoothing decides how spiky that difference is. The thresholds were tuned against a smoothed spectrum; a raw one makes every frame look like an onset |
| Decibels | `20 x log10(magnitude)` | - | Music covers four orders of magnitude, and a linear scale spends nearly all of a byte on the loudest tenth of it |
| Byte | `255 x (dB + 76) / 70`, clamped | `SpectrumMinDb`, `SpectrumMaxDb` | -76 dBFS reads 0 and -6 dBFS reads 255. The width is the convention's; the position is measured against this rig, and the measurement is the section below |
| Band energy | mean of `(byte / 255)^2` | `SpectrumScale.BandEnergy` | The mean and not the sum, so the same music gives the same answer over a band of 7 bins and one of 23. A sum would make every threshold depend on the sample rate |

**This is the riskiest part of the transcription.** If the reconstruction is out by
a factor of two in magnitude, that is 6 dB, which is 22 byte values, which moves
every threshold below at once - and the symptom is not a crash but a detector that
is uniformly slightly deaf or slightly hysterical. Two things guard against it: a
test that a tone of a known level reads the byte value the scale promises
(`SpectrumAnalyzerTests.ReadsAToneAtTheLevelTheScalePromises`), and
`Audio:Sensitivity`, which compensates without anyone having to touch the scale.

### Measured against a real master bus, and it was mispositioned

Two recordings off the FLX-10's master return, 48 seconds each, said the window
was in the wrong place for this signal:

| Measurement | Value |
|---|---|
| Kick band byte value | median 214 of 255 |
| Kick band bytes above 240 | 14.8% |
| Kick band bytes clamped at 255 | 3.2% |
| Kick band energy, p10 to p90 | 0.49 to 0.79 |
| Span of the 70 dB window actually used | about 13 dB |
| Frames clearing the 0.06 energy floor | 100% |
| Spectral flux, p99 | 0.046 |

The top of the window, -30 dBFS, sat below this signal's peaks. A master bus at
around -22 dBFS RMS has bass bins well above that, so the kick band parked near
the top of the byte range and a kick's onset became a small rise on a high plateau
instead of a spike from near silence. Two consequences followed directly: the 0.06
energy floor gated nothing at all, and the flux the detector triggers on was
compressed into the top few percent of its range.

The scene will not have seen this. Its host captured system audio at ordinary
desktop levels, which lands inside the window rather than above it.

**It was not fixed by moving a threshold**, and that was the right call: the
thresholds are the part with evidence behind them and the window position was the
part that had been guessed. What follows is the calibration that replaced it.

### Repositioned, on 2026-09-04

The material is a four minute capture off the same master return: three
beatmatched tracks, fader at playing level, nothing touched during the run, with
Link independently reporting **122 BPM** throughout. Two recordings were made in
the same run, a WAVE capture of the audio and a JSONL capture of the analyzed
frames, and the second exists to check the first: the harness replays the audio
through the analyzer's own transform, so it has to reproduce the frames the
analyzer wrote at the time before anything it says is worth reading.

**The measurement had to start from the samples, and that is the whole reason the
audio is recorded at all.** `SpectrumScale.ToByte` clamps at `SpectrumMaxDb`, so
on a signal that reaches the top of the window every frame recording and every
frame on the wire holds the ceiling instead of the level. Choosing the ceiling
from data already cut off at it is circular. The unclamped kick band, recomputed
from the audio:

| Kick band, unclamped | loudest bin | band mean |
|---|---|---|
| p50 | -27.7 dBFS | -32.1 dBFS |
| p99 | -18.3 dBFS | -24.1 dBFS |
| maximum | -17.0 dBFS | -22.8 dBFS |

So the old ceiling sat about 13 dB below the signal's peaks, and it clamped
**22.6% of every kick-band bin in the run**.

Candidate ceilings, span held at 70 dB, each one replayed through the real
detector against a true 122 BPM:

| Ceiling | Clamped bins | Kick energy p50 | Median locked BPM | Frames locked |
|---|---|---|---|---|
| -30 dBFS (old) | 22.6% | 0.789 | 125.05 | 9% |
| -20 dBFS | 0.9% | 0.581 | 121.85 | 54% |
| -16 to -12 dBFS | 0.0% | 0.50 to 0.42 | 122.50 | 65% |
| **-10 to -2 dBFS** | **0.0%** | **0.39 to 0.26** | **122.0 to 122.7** | **74 to 77%** |
| 0 dBFS | 0.0% | 0.232 | 114.74 | 38% |

**Both edges of that plateau fail, and they fail differently**, which is why the
answer is its middle rather than its best single reading. Too low and the loudest
bins clamp, the flux the detector triggers on is compressed, and the reported
tempo climbs: at the old ceiling it read 125 against a true 122 while holding a
lock for 9% of the run. Too high and the bins are faithful but the detector's
*absolute* energy gates stop being met in quiet passages, because
`MinEnergyFloor` and the strong-beat multiple are levels rather than ratios. At
0 dBFS that collapses to 114 BPM.

`SpectrumMaxDb = -6` therefore sits in the centre of the working range: 11 dB of
headroom above the loudest bin measured, 6 dB of margin above the cliff.
`SpectrumMinDb = -76` follows from holding the span at 70 dB, which is not
negotiable - the span is what converts a decibel rise into a byte rise, so
changing it would retune every threshold in this document at once.

**The two tempo columns of that table are the estimator of the day, and the
rules under "Changed from the scene" have since improved them** - the plateau
now reads 122.0 to 122.5 and both failing edges degrade more gently, so a
re-run will not reproduce those two columns exactly. The clamping percentages
and energy levels will, and they are what chose the ceiling: they are
properties of the signal rather than of the estimator, which is why -6 dBFS
does not move.

The harness is `WindowCalibrationTests` with `SpectrumProbe`, and it is skipped
unless a local recording is present. Recordings are made by running with
`Audio:RecordAudioPath` and `Audio:RecordFramesPath` both set.

**What this does not yet establish.** One session, one rig, one master level: the
three tracks were beatmatched and played at a single gain, so the survey covers
several pieces of music but only one level. A second take at a noticeably
different level would test the headroom claim rather than assume it, and until
that exists the 11 dB above the loudest measured bin is a margin rather than a
proven bound. Note also that a frame recording carries no record of the window it
was made under, so an old JSONL fixture is only interpretable alongside the
constants of the day: that is why the WAVE capture is the durable fixture and the
frame recording is a derived one.

## The bands

The scene's band edges are indices into its 200-element array, so they are only
meaningful alongside how wide one of its bins is. Its own comment answers that:
the array reaches about 10 kHz, so a bin is about **50 Hz**. Its
`usedFreqSector` setting, which defaults to showing the first 50 of the 200
bands as "the highest visible frequencies", is consistent with that reading.

| Band | Scene bins | Hz at 50 Hz per bin | Scene's own comment | FFT bins at 48 kHz | Width |
|---|---|---|---|---|---|
| Kick | 1 to 3 | 50 to 200 | "kick drums are ~50-100Hz" | 2 to 8 | 7 bins |
| Mids | 4 to 8 | 200 to 450 | "snares/claps are ~200-400Hz" | 9 to 18 | 10 bins |
| Highs | 15 to 25 | 750 to 1300 | "hi-hats are ~8k+Hz" | 32 to 54 | 23 bins |

The derivation, with a bin at 48 kHz over 2048 samples being
`48000 / 2048 = 23.4375 Hz`:

```
bin(hz)    = round(hz / 23.4375)
kick       = [bin(50), bin(200))  = [2, 9)   -> bins 2 to 8
mids       = [bin(200), bin(450)) = [9, 19)  -> bins 9 to 18
highs      = [bin(750), bin(1300)) = [32, 55) -> bins 32 to 54
```

Three notes on that table, in order of how much they matter.

**The ranges are half-open, and that is not tidiness.** The kick and mid bands
meet at 200 Hz. A bin counted in both would tilt `kickEnergy > midEnergy x 0.7`
towards the mids on every kick, and that comparison is the only test in the
detector separating a kick drum from a snare - the one with no threshold to absorb
an error.

**The kick and mid comments disagree with the indices, and the indices win.** The
comment says 50 to 100 Hz where bins 1 to 3 cover 50 to 200. The wider range is
what ran, and it is also the more defensible of the two: a kick's fundamental sits
near 50 Hz, but its *attack*, which is what spectral flux measures, carries well
above 100.

**The highs comment is wrong by nearly an order of magnitude, and it costs
nothing.** Bins 15 to 25 are 750 to 1300 Hz on the scene's own scale, not "8k+".
As coded wins here too, and the reason it is safe to take that literally is that
no decision is made on this band: it is drawn, never decided on. It reaches a
browser as the protocol's `highs`.

**Band edges are recomputed from the device's real sample rate**, never assumed.
44.1 kHz happens to put the kick band in exactly the same bins as 48 kHz, which is
a coincidence worth knowing and not worth relying on; a device that opens at 96 kHz
would put a band computed for 48 kHz an octave out.

## The thresholds

Transcribed values. "Scene" is the identifier in the scene's source; "Flux" is the
symbol in `AudioConstants.cs`.

| Scene | Value | Flux | Used for | Why this number |
|---|---|---|---|---|
| `beatSensitivity` | 50 (1 to 100) | `DefaultSensitivity` | Scales the onset threshold | The scene's own default, and the only setting Flux exposes |
| `thresholdMult` | `4.0 - sens/100 x 2.5` | `SensitivityBase`, `SensitivitySpan` | Multiplier on average flux | At 1 an onset needs nearly four times the recent average; at 100, one and a half |
| `fluxThreshold` offset | 0.015 | `FluxThresholdOffset` | Floor under the adaptive threshold | The adaptive part goes to zero in silence, where any flicker then clears it |
| `minEnergyFloor` | 0.06 | `MinEnergyFloor` | Minimum bass energy for a beat | Stops the threshold finding onsets in noise between tracks |
| `minBeatInterval` | 280 ms | `MinBeatIntervalMillis` | Minimum gap between beats | Caps detection at ~214 BPM, but its real job is the snare 100 ms after a kick: without it half the beats count twice and the tempo comes out doubled |
| `isLikelyKick` | `kick > mid x 0.7` | `KickOverMidRatio` | Kick versus snare | The only test distinguishing the two, and the reason a loud backbeat does not read as twice the tempo |
| `strongBeatEnergyThreshold` | `floor x 2.5` | `StrongBeatEnergyMultiplier` | Strong beat energy | Strong beats are what the grid is anchored to, so they are a stricter test, not merely a louder one |
| `strongBeatFluxThreshold` | `threshold x 1.8` | `StrongBeatFluxMultiplier` | Strong beat flux | As above |
| `minStrongBeatInterval` | 350 ms | `MinStrongBeatIntervalMillis` | Minimum gap between strong beats | Above the ordinary interval, so a fill cannot re-anchor the grid twice in a beat |
| `historyLength` | `max(20, 0.5 s of frames)` | `HistoryMinFrames`, `HistorySeconds` | Flux and energy history | The scene's frame rate was whatever the browser gave it. At Flux's fixed 60 fps the half second always wins and the floor only applies at startup |
| `beatTimes` limit | 20 | `MaxBeatTimes` | Beats kept for tempo estimation | About five bars: enough for a stable median, short enough to follow a tempo change |
| `lastBeatDelta` guard | 250 ms | `MinBeatSpacingForBpmMillis` | Beats admitted to the estimate | Unreachable as coded, since nothing closer than 280 ms gets that far. Transcribed anyway: it says what the estimator is protecting itself from |
| `beatTimes.length >= 6` | 6 | `MinBeatsForBpm` | Beats before a tempo exists | Five intervals to take a median of |
| `intervals.length >= 4` | 4 | `MinIntervalsForBpm` | Usable intervals needed | After the 300 to 1000 ms filter has thrown some away |
| interval filter | 300 to 1000 ms | `BpmIntervalMinMillis`, `BpmIntervalMaxMillis` | Intervals admitted | 60 to 200 BPM. Dropping outliers rather than averaging them is what stops one missed kick from halving the answer |
| BPM smoothing | `0.8 old + 0.2 new` | `BpmSmoothingHeld`, `BpmSmoothingFresh` | Unlocked tempo | A tempo that moves as fast as one bad measurement is a tempo nothing can be anchored to |
| lock threshold | 12 beats | `LockAfterBeats` | Beats before the tempo can lock | |
| lock consistency | stdev/mean < 0.2 | `LockMaxRelativeStdDev` | Interval consistency to lock | Loose enough for a human-played track, tight enough that a passage of half-time drums does not qualify |
| unlock difference | 25 BPM | `UnlockBpmDifference` | Disagreement needed to unlock | |
| unlock delay | 15 s | `UnlockAfterLockMillis` | How long a lock is protected | Deliberately asymmetric with locking. A mix runs two tracks at once for far longer than a bar, and a detector that unlocked on the incoming track's first kick would throw away the tempo the floor is still dancing to |
| `quietThreshold` | 0.02 | `QuietEnergyThreshold` | Energy counting as quiet | |
| `minQuietForDrop` | 800 ms | `MinQuietForDropMillis` | Silence before a drop can happen | A passing gap between phrases is not a breakdown |
| drop energy ratio | 5 | `DropEnergyRatio` | Loudness of the return | |
| drop energy floor | 0.25 | `DropMinEnergy` | Absolute loudness of the return | The ratio alone is met by any rise out of near-silence, which is why there is a floor beside it |
| drop ratio guard | 0.001 | `DropRatioEpsilon` | Denominator floor for the drop ratio | A numerical guard, not a tuned value: it keeps the ratio finite when the previous frame was bit-exact silence. A twentieth of `QuietEnergyThreshold`, so against any passage quiet enough to arm a drop it barely moves the ratio it protects |
| `dropCooldown` | 5 s | `DropCooldownMillis` | Minimum gap between drops | A drop rebases the beat numbering, which is the most disruptive thing the detector can do |
| max drops | 3 per 30 s | `MaxDropsPerWindow`, `DropCountWindowMillis` | Drop rate limit | A breakdown with stabs in it would otherwise re-anchor the grid every couple of bars |

### Not transcribed

- **`pastEnergies` and `avgEnergy`.** The scene maintains an energy history and
  averages it, and then never uses the average in any detection test. Left out
  rather than carried as dead state.
- **`manualBPM`.** The scene's manual tempo override is Flux's `ManualSource`,
  which is a sync source of its own rather than a mode inside the detector.
- **The time-based beat grid** (`beatGridInterval`, `lastGridBeatTime`,
  `totalBeatsDetected`). The scene advanced its own beat counter by wall-clock
  time. Flux already has exactly one of those, shared by every source, so the
  detector publishes a `BeatTimeline` and `BeatGrid` does the counting. That is
  what makes an audio tempo interchangeable with a Link tempo instead of a second
  opinion running beside it.
- **Everything visual**: palettes, flash decay, strobe, gradients.

### Added by Flux

The numbers here are not the scene's, and each exists because of a difference
between the two programs rather than a disagreement about music.

| Flux | Value | Why |
|---|---|---|
| `WarmUpFrames` | 20 frames | The scene's host handed it a spectrum that was already running. Ours starts at silence and climbs to the signal over several frames as the smoothing fills, and that climb is a rising flux with no music behind it. A third of a second is enough for the carry-over to fall under a twentieth, and far cheaper than one phantom onset inside the tempo estimate |
| `DisplayBins`, `DisplayLowHz`, `DisplayHighHz` | 128 columns, 30 Hz to 16 kHz | The log-spaced columns the protocol sends. The scene's visible width is a user setting defaulting to its first 2.5 kHz; a visualizer being read as a test instrument wants the whole picture. 64 until the visualizer became the whole page - see the derivation below |
| `ConfidenceLockedFloor`, `ConfidenceLockedSpan` | 0.6 + up to 0.4 | The scene never reported confidence; Flux has a source selector that needs one. These define the protocol's `confidence` field for a locked tempo. The floor sits above the unlocked ceiling so the bands cannot overlap: a lock is a qualitative claim about twelve consistent beats, and no amount of unlocked consistency should outrank it. The span is the remainder of the scale, filled in by interval consistency |
| `ConfidenceUnlockedCeiling` | 0.5 | The most an unlocked tempo can claim, a tenth below the locked floor so the two bands stay visibly apart rather than merely disjoint |
| `ConfidenceSpreadRange` | 2 | Interval spread is mapped to a 0 to 1 consistency by dividing by this multiple of `LockMaxRelativeStdDev`. Two puts the lock bar itself at the middle of the scale: intervals exactly at the bar read 0.5, and only a spread twice as loose as anything lockable reads zero |

`FrameRateHz` is 60, which is the scene's rate because it ran on
`requestAnimationFrame`. It is not free to change: the history windows above are
sized in frames, and 280 ms is only 17 frames at this rate.

`DisplayBins` was 64 and is 128, raised when the visualizer stopped being a panel
and became the whole page: across a monitor, 64 columns read as blocks rather than
as a spectrum. What the doubling actually buys is bounded by the transform and not
by this number, and the bound is the part worth writing down.

At 48 kHz, `FftSize` of 2048 resolves 48000 / 2048 = 23.4 Hz per bin. The columns
are log-spaced, so column *c* of *N* spans from
`DisplayLowHz * (DisplayHighHz / DisplayLowHz)^(c/N)` to the same at *c+1*, and its
width in hertz grows with *c*. Setting that width equal to one bin gives the column
below which the picture is asking for detail the transform does not have:

| `DisplayBins` | Column where a column first exceeds one bin | Frequency there | Columns that resolve |
|---|---|---|---|
| 64 | 21 | 235 Hz | 43 of 64 |
| 128 | 56 | 468 Hz | 72 of 128 |

So the bottom 56 columns of 128 share about 20 bins and repeat each other, where
64 columns had 21 doing the same. The count that resolves real detail rises from
43 to 72, which is what makes the picture finer; the bass end is no worse than it
was, it is only drawn in more columns.

Fixing the bass end properly means a longer transform, and that is deliberately not
done here. `FftSize` is a tuned number with a beat detector downstream: doubling it
doubles the window a kick's onset is smeared over, and the effect of that on the
onset timing is not something to change in a commit about how the picture looks.

None of this touches detection. The detector reads `BandEnergy` over the full
transform, never the display columns, which is why this is a change to one number
and a doc table rather than a re-calibration.

### Changed from the scene

Three rules deviate from the scene rather than transcribing it. Each replaces a
behaviour that is defensible on a desktop visualizer and wrong on a DJ master
bus, and none of them changes a transcribed number.

**Missed kicks fold into the estimate rather than fighting it.** The interval
band's own rationale ("what stops one missed kick from halving the answer")
only holds below 120 BPM: above it, a doubled interval still fits inside
1000 ms. A track with sparse kicks at 125 BPM therefore feeds the estimator a
permanent mixture of 480 ms true intervals and 958 ms doubles, its spread never
drops under the lock bar, and the reported tempo sits at the mixture's
compromise indefinitely - around 116 BPM for a 125 BPM track. The rule added:
once a tempo hypothesis exists, an interval sitting within `FoldTolerance` of an
exact integer multiple of the current beat interval is read as that many beats
and divided down before it enters the estimate. A missed kick is not a tempo
claim.

Both bounds on the tolerance are derived rather than chosen. The floor is frame
quantization: onsets land on the 16.7 ms frame grid, so a doubled interval is
within two frames of exact, under 6% of a beat across the whole 60 to 200 BPM
band. The ceiling is the off-grid contamination the unlock rule below guards
against: an interval of one and a half beats folded by two misses the beat by
25%, and folding it would launder inconsistent evidence into consistent-looking
evidence. A tenth of a beat sits between the two with at least a factor of two
to spare each way, which is why the fold cannot defeat the unlock. Pinned by
`LocksDespiteMissedKicks`.

**A locked tempo keeps refining instead of staying as minted.** The scene froze
`estimatedBPM` at the moment of locking, which makes the reported tempo a mean
over at most nineteen intervals from the track's intro - and since onset times
carry both frame quantization and detection jitter, that snapshot has a
sampling error worth most of a BPM in either direction, which then stands for
the rest of the track. The rule added: while locked, the longest
grid-consistent run of intervals ending at the newest onset (every interval
within `FoldTolerance` of a whole number of beats, at most four so the rounding
stays unambiguous) re-derives the tempo from its endpoints - total time over
total beats - and feeds it through the existing
`BpmSmoothingHeld`/`BpmSmoothingFresh` pair.

Three details carry the design. The endpoint form divides the onset jitter by
the whole span instead of by one interval, which is the entire point: the error
falls as the run grows. It is a suffix rather than the whole window because a
mix contains an off-grid onset more often than once per window, so a gate
demanding a fully clean window would never open. And the run must be worth at
least `LockAfterBeats` beats, holding a refinement to the same evidence bar as
the lock it adjusts, which is what stops contamination steering a lock the
unlock rule would have protected. A side effect worth naming: a locked tempo
now follows a small pitch ride, which a frozen one never could. Pinned by
`RefinesALockMintedFromAJitteredIntro`.

**The unlock demands a lockable challenger.** The scene gives up a locked tempo
when a single median disagrees by more than 25 BPM after the 15 second
protection, and adopts that median wholesale. That single median is not enough
evidence: a passage where offbeat bass notes slip past the kick gate feeds the
estimator a mixture of true and half-shifted intervals - each accepted offbeat
lands inside the 280 ms refractory window of the kick behind it, so the mixture
runs at one and a half beats as often as at one - and one median over that
mixture clears the threshold. Surrendering to it turns one bad window into
minutes of wrong tempo, since the adopted value is then defended by the same
protection the correct one had. Flux therefore adds a third unlock condition:
the disagreeing intervals must themselves be consistent enough to lock
(`LockMaxRelativeStdDev`, the same bar locking applies). A real track change
produces intervals that agree with each other; a contaminated stretch does not.
The rule reuses the lock's own consistency bar, so no constant is added, and
both halves are pinned in `AudioBeatDetectorTests`: contamination holds the
lock, a genuine tempo change still releases it.

## What none of this proves

The constants are evidence about music, so the only test that can confirm them is
music. There are two recorders for that reason. `Audio:RecordFramesPath` writes
every analyzed frame to a JSON Lines file, so a run against a real track through
the real controller becomes a fixture that replays through the detector at any
speed and a defect found on stage becomes a test rather than a memory.
`Audio:RecordAudioPath` writes the captured audio itself, which is what any
question about the analyzer rather than the detector has to be answered from.

What the synthetic fixtures in `tests/Flux.Audio.Tests` do prove is the
arithmetic: that an onset every 469 ms comes out as 128 BPM, that a snare louder
than its kick is rejected, that a drop renumbers the beats and a short gap does
not, and that the analyzer's frame loop actually fires. Those are worth having and
they are not the same claim.

One consequence of the frame format is worth knowing, and it is broader than it
first looks: a frame recording holds band energies, not audio. Change where a band
is and every existing fixture describes the old bands. The same argument applies
to everything upstream of a band energy, and that is the sharper half of it: the
energies are computed from bytes that `SpectrumMaxDb` has already clamped, so a
frame recording cannot be used to reposition the window that produced it, and it
carries no record of where that window was. This was found the hard way, by trying
it. An audio recording has neither problem, which is why the calibration above is
measured from one and why the WAVE capture is the durable fixture.
