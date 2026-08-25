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

| Step | Value | Flux symbol | Why |
|---|---|---|---|
| Window | Hann, 2048 samples | `FftSize` | 42.7 ms at 48 kHz. Half of it and the bass bins cannot separate a kick from the bass line under it; double it and the window smears the attack across most of a sixteenth note at 175 BPM |
| Magnitude | `4 x` the bin magnitude | `SpectrumAnalyzer.MagnitudeScale` | The Hann window sums to half its length and a real signal's energy splits between the positive and negative frequency, so a full-scale tone arrives at a quarter of full scale |
| Smoothing | 0.8 of the previous frame | `SpectrumSmoothing` | Spectral flux is a difference between consecutive frames, so smoothing decides how spiky that difference is. The thresholds were tuned against a smoothed spectrum; a raw one makes every frame look like an onset |
| Decibels | `20 x log10(magnitude)` | - | Music covers four orders of magnitude, and a linear scale spends nearly all of a byte on the loudest tenth of it |
| Byte | `255 x (dB + 100) / 70`, clamped | `SpectrumMinDb`, `SpectrumMaxDb` | -100 dBFS reads 0 and -30 dBFS reads 255. Everything within 30 dB of full scale saturates, which is deliberate: that is where the detail a detector needs is not |
| Band energy | mean of `(byte / 255)^2` | `SpectrumScale.BandEnergy` | The mean and not the sum, so the same music gives the same answer over a band of 7 bins and one of 23. A sum would make every threshold depend on the sample rate |

**This is the riskiest part of the transcription.** If the reconstruction is out by
a factor of two in magnitude, that is 6 dB, which is 22 byte values, which moves
every threshold below at once - and the symptom is not a crash but a detector that
is uniformly slightly deaf or slightly hysterical. Two things guard against it: a
test that a tone of a known level reads the byte value the scale promises
(`SpectrumAnalyzerTests.ReadsAToneAtTheLevelTheScalePromises`), and
`Audio:Sensitivity`, which compensates without anyone having to touch the scale.

### Measured against a real master bus, and it is mispositioned

Two recordings off the FLX-10's master return, 48 seconds each, say the window is
in the wrong place for this signal:

| Measurement | Value |
|---|---|
| Kick band byte value | median 214 of 255 |
| Kick band bytes above 240 | 14.8% |
| Kick band bytes clamped at 255 | 3.2% |
| Kick band energy, p10 to p90 | 0.49 to 0.79 |
| Span of the 70 dB window actually used | about 13 dB |
| Frames clearing the 0.06 energy floor | 100% |
| Spectral flux, p99 | 0.046 |

The top of the window, -30 dBFS, sits below this signal's peaks. A master bus at
around -22 dBFS RMS has bass bins well above that, so the kick band parks near the
top of the byte range and a kick's onset becomes a small rise on a high plateau
instead of a spike from near silence. Two consequences follow directly: the 0.06
energy floor gates nothing at all, and the flux the detector triggers on is
compressed into the top few percent of its range.

The scene will not have seen this. Its host captured system audio at ordinary
desktop levels, which lands inside the window rather than above it.

**This is not yet fixed, and it should not be fixed by moving a threshold.** The
thresholds are the part with evidence behind them; the window position is the part
that was guessed. Whatever replaces it has to be calibrated against recordings
whose tempo is known independently, and then checked on more than one track.

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

Two numbers here are not the scene's, and both exist because of a difference
between the two programs rather than a disagreement about music.

| Flux | Value | Why |
|---|---|---|
| `WarmUpFrames` | 20 frames | The scene's host handed it a spectrum that was already running. Ours starts at silence and climbs to the signal over several frames as the smoothing fills, and that climb is a rising flux with no music behind it. A third of a second is enough for the carry-over to fall under a twentieth, and far cheaper than one phantom onset inside the tempo estimate |
| `DisplayBins`, `DisplayLowHz`, `DisplayHighHz` | 64 columns, 30 Hz to 16 kHz | The 64 log-spaced columns the protocol sends. The scene's visible width is a user setting defaulting to its first 2.5 kHz; a visualizer being read as a test instrument wants the whole picture |

`FrameRateHz` is 60, which is the scene's rate because it ran on
`requestAnimationFrame`. It is not free to change: the history windows above are
sized in frames, and 280 ms is only 17 frames at this rate.

## What none of this proves

The constants are evidence about music, so the only test that can confirm them is
music. `Audio:RecordFramesPath` writes every analyzed frame to a JSON Lines file
for exactly that reason: a run against a real track through the real controller
becomes a fixture that replays through the detector at any speed, so a defect
found on stage becomes a test rather than a memory.

What the synthetic fixtures in `tests/Flux.Audio.Tests` do prove is the
arithmetic: that an onset every 469 ms comes out as 128 BPM, that a snare louder
than its kick is rejected, that a drop renumbers the beats and a short gap does
not, and that the analyzer's frame loop actually fires. Those are worth having and
they are not the same claim.

One consequence of the recording format is worth knowing before any band edge is
ever moved: a recording holds band energies, not audio. Change where a band is and
every existing fixture describes the old bands, so they need re-recording rather
than reinterpreting.
