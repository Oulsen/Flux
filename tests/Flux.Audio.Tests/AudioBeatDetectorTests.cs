using Flux.Beat;
using Xunit;

namespace Flux.Audio.Tests;

public class AudioBeatDetectorTests {
  private const double Bpm = 128;

  [Fact]
  public void FindsTheBeatInASteadyClickTrack() {
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16));

    BeatDecision last = decisions[^1];

    // Every onset, and nothing between them. A detector that found extra beats
    // would still report a plausible tempo, which is why the count is asserted and
    // not only the tempo.
    Assert.Equal(16, last.BeatsDetected);
    Assert.Equal(16, decisions.Count(decision => decision.Strength != BeatStrength.None));

    Assert.True(last.Locked, "The tempo never locked over sixteen consistent beats.");
    Assert.InRange(last.Bpm, 125, 131);
    Assert.True(last.Confidence > 0.6, $"A locked tempo reported a confidence of {last.Confidence}.");
    Assert.NotNull(last.LastBeatMicros);
  }

  [Fact]
  public void CallsAClickTrackKickStrongEveryTime() {
    // Strong beats are the ones the grid is anchored to, so a detector that found
    // beats but never a strong one would track the tempo and never place it.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16));

    Assert.Equal(16, decisions.Count(decision => decision.Strength == BeatStrength.Strong));
  }

  [Fact]
  public void TakesTwelveBeatsToLock() {
    // The lock is what confidence is built on, and locking early would mean locking
    // on a coincidence. Asserted through the frame it happens on rather than the
    // constant, because the beats have to arrive before it can count them.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16));

    int beatsWhenLocked = 0;
    foreach(BeatDecision decision in decisions) {
      if(decision.Strength != BeatStrength.None) {
        beatsWhenLocked++;
      }

      if(decision.Locked) {
        break;
      }
    }

    // Counted inclusively: the twelfth beat is the one that locks, on the frame it
    // arrives, rather than the one after it.
    Assert.Equal(AudioConstants.LockAfterBeats, beatsWhenLocked);
  }

  [Fact]
  public void RenumbersOnceAndThenOnlyReAnchors() {
    // The mitigation for the risk the phase plan called its biggest: a smoothed
    // tempo extrapolated from one anchor is right about the interval and slowly
    // wrong about the phase, so the anchor moves onto every strong beat that is
    // actually heard. Carrying the beat number forward is what keeps it a
    // re-anchoring rather than a renumbering, and a renumbering would throw away the
    // bar alignment on every kick.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16));

    List<BeatDecision> published = decisions.Where(decision => decision.Timeline is not null).ToList();

    Assert.Equal(1, published.Count(decision => decision.Rebase));
    Assert.True(published[0].Rebase, "The first timeline published has to be the one that renumbers.");
    Assert.Equal(0, published[0].Timeline!.Value.BeatOrigin);

    // Strictly increasing, and by about one beat each time: the anchor lands on the
    // beat that was just heard, so a stall or a jump would both show up here.
    for(int index = 1; index < published.Count; index++) {
      double step = published[index].Timeline!.Value.BeatOrigin - published[index - 1].Timeline!.Value.BeatOrigin;
      Assert.Equal(1, step);
    }
  }

  [Fact]
  public void PublishesATempoTheGridWillAccept() {
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16));

    BeatTimeline timeline = decisions.Last(decision => decision.Timeline is not null).Timeline!.Value;

    Assert.True(timeline.IsPlausible);
    Assert.InRange(timeline.Bpm, 125, 131);

    // The anchor is the moment a beat was heard, not the moment the frame was
    // processed, and the two are the same here only because the detector times
    // everything from the frame it is handed.
    Assert.InRange(
      timeline.TimeOriginMicros,
      (long)decisions[0].LastBeatMicros.GetValueOrDefault(),
      long.MaxValue);
  }

  [Fact]
  public void IgnoresASnareLouderThanItsKick() {
    // Without the kick-versus-mid gate a loud backbeat is indistinguishable from a
    // kick, every other onset is spurious, and the tempo comes out doubled. This is
    // the test that would fail first if the band edges moved.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16, peak: 0.2, mids: 0.5));

    Assert.Equal(0, decisions[^1].BeatsDetected);
    Assert.Equal(0, decisions[^1].Bpm);
    Assert.All(decisions, decision => Assert.Null(decision.Timeline));
  }

  [Fact]
  public void HearsNothingInSilence() {
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Quiet(4_000));

    Assert.Equal(0, decisions[^1].BeatsDetected);
    Assert.Equal(0, decisions[^1].Confidence);
    Assert.Null(decisions[^1].LastBeatMicros);
    Assert.DoesNotContain(decisions, decision => decision.Drop);
  }

  [Fact]
  public void IgnoresTheFramesWhileTheSpectrumIsFillingUp() {
    // Flux's own addition, and it is needed because our spectrum starts at silence
    // and climbs to the signal over several frames as the smoothing fills. That
    // climb is a rising flux with no music behind it. The scene never saw it: its
    // host handed it a spectrum that was already running.
    List<BeatDecision> decisions = Replay(new FrameTrack().Beats(Bpm, 4));

    for(int frame = 0; frame < AudioConstants.WarmUpFrames; frame++) {
      Assert.Equal(BeatStrength.None, decisions[frame].Strength);
    }
  }

  [Fact]
  public void TreatsTheReturnFromABreakdownAsADrop() {
    // A drop is the only downbeat audio analysis can find without being told, which
    // is the whole reason it is worth detecting.
    List<BeatDecision> decisions = Replay(new FrameTrack()
      .Warmup()
      .Beats(Bpm, 16)
      .Quiet(1_500)
      .Beats(Bpm, 4));

    BeatDecision drop = Assert.Single(decisions, decision => decision.Drop);

    Assert.True(drop.Rebase, "A drop has to be announced as a renumbering, or the grid counts the jump as drift.");
    Assert.Equal(0, drop.Timeline!.Value.BeatOrigin);

    // The tempo survives, and only the phase is thrown away. A drop is where the bar
    // restarts, not where the track changes speed.
    Assert.InRange(drop.Timeline!.Value.Bpm, 125, 131);
  }

  [Fact]
  public void RefusesADropAfterAShortGap() {
    // A passing gap between phrases is not a breakdown, and snapping the bar to one
    // would restart the bar count several times a track.
    List<BeatDecision> decisions = Replay(new FrameTrack()
      .Warmup()
      .Beats(Bpm, 16)
      .Quiet(400)
      .Beats(Bpm, 4));

    Assert.DoesNotContain(decisions, decision => decision.Drop);
  }

  [Fact]
  public void RefusesASecondDropInsideTheCooldown() {
    // A breakdown with stabs in it would otherwise re-anchor the grid every couple
    // of bars, which is the most disruptive thing this detector can do.
    List<BeatDecision> decisions = Replay(new FrameTrack()
      .Warmup()
      .Beats(Bpm, 16)
      .Quiet(1_500)
      .Beats(Bpm, 4)
      .Quiet(1_500)
      .Beats(Bpm, 4));

    Assert.Single(decisions, decision => decision.Drop);
  }

  [Fact]
  public void HoldsTheLockThroughAContaminatedStretch() {
    // A passage where offbeat bass notes slip past the kick gate feeds the
    // estimator a mixture of true intervals and half-shifted ones: each accepted
    // offbeat lands inside the refractory window of the kick behind it, so the
    // mixture runs at one and a half beats as often as at one. A single median
    // over it disagrees with the lock by more than the unlock threshold, and a
    // detector that unlocked on that one outlier would adopt it. The mixture is
    // inconsistent in a way a real track change never is, which is the test the
    // unlock applies.
    FrameTrack track = new FrameTrack().Warmup().Beats(125, 16);
    for(int repeat = 0; repeat < 5; repeat++) {
      track.Hits(125, [0, 1.5, 3, 4.5, 5.23, 6.23]);
    }

    List<BeatDecision> decisions = Replay(track);

    Assert.True(decisions[^1].Locked, "The contaminated stretch stole the lock.");
    Assert.InRange(decisions[^1].Bpm, 120, 131);
    Assert.All(
      decisions.SkipWhile(decision => !decision.Locked),
      decision => Assert.True(decision.Locked, $"The lock was given up at {decision.Bpm} BPM."));
  }

  [Fact]
  public void LocksDespiteMissedKicks() {
    // Above 120 BPM a single missed kick produces an interval that still fits the
    // 300 to 1000 ms band, so a track with sparse kicks feeds the estimate a
    // permanent mixture of true and doubled intervals whose spread never settles
    // under the lock bar - the tempo then hovers at the mixture's compromise
    // forever. Reading an exact multiple of the current beat as missed kicks and
    // dividing it down is what makes this diet lockable; the tolerance is
    // derived in docs/audio-constants.md.
    List<double> beats = [];
    for(int bar = 0; bar < 10; bar++) {
      beats.AddRange([bar * 4.0, (bar * 4.0) + 1, (bar * 4.0) + 2]);
    }

    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Hits(125, beats));

    Assert.True(decisions[^1].Locked, "The tempo never locked through missed kicks.");
    Assert.InRange(decisions[^1].Bpm, 120, 131);
  }

  [Fact]
  public void RefinesALockMintedFromAJitteredIntro() {
    // The lock is minted from a mean over at most nineteen intervals, and onset
    // times carry frame quantization and detection jitter, so that snapshot can
    // sit most of a BPM off the true tempo in either direction - and the scene
    // then displayed it unchanged for the rest of the track. Here one early
    // onset in the intro biases the minted mean high, and the clean beats that
    // follow have to pull the locked tempo back to the truth.
    List<double> beats = [];
    for(int beat = 0; beat <= 10; beat++) {
      beats.Add(beat);
    }

    beats.Add(10.94);
    for(int beat = 12; beat < 48; beat++) {
      beats.Add(beat);
    }

    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Hits(125, beats));

    Assert.True(decisions[^1].Locked, "The tempo never locked.");
    Assert.InRange(decisions[^1].Bpm, 124.5, 125.4);
  }

  [Fact]
  public void UnlocksForAGenuineTempoChange() {
    // The counterpart of holding the lock through contamination: a new track at a
    // genuinely different tempo produces intervals that agree with each other, and
    // that consistency is what distinguishes it from noise. A detector that could
    // never unlock would play the previous track's tempo forever.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(128, 16).Beats(90, 45));

    BeatDecision last = decisions[^1];
    Assert.True(last.Locked, "The tempo never re-locked on the new track.");
    Assert.InRange(last.Bpm, 87, 93);

    // Through an unlock rather than a drift: the old lock has to be given up
    // before the new tempo can be adopted.
    int firstLock = decisions.FindIndex(decision => decision.Locked);
    Assert.Contains(decisions.Skip(firstLock), decision => !decision.Locked);
  }

  [Fact]
  public void ForgetsTheTempoOnReset() {
    // What a device change does. Keeping a tempo across a gap of unknown length
    // produces a timeline anchored somewhere in the silence, which is a beat display
    // confidently in the wrong place.
    AudioBeatDetector detector = new AudioBeatDetector();
    FrameTrack track = new FrameTrack().Warmup().Beats(Bpm, 16);

    BeatDecision before = default;
    foreach(SpectrumFrame frame in track.Frames) {
      before = detector.Observe(frame);
    }

    Assert.True(before.Locked);

    detector.Reset();

    BeatDecision after = detector.Observe(new FrameTrack().Beats(Bpm, 1).Frames[0]);

    Assert.Equal(0, after.Bpm);
    Assert.False(after.Locked);
    Assert.Equal(0, after.BeatsDetected);
    Assert.Null(after.Timeline);
  }

  [Fact]
  public void FindsAFasterTempoAsReadilyAsASlowOne() {
    // 175 BPM is what this rig actually plays, and it is close enough to the 280 ms
    // minimum beat interval to be worth pinning: at 214 BPM the two would collide.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(175, 20));

    Assert.True(decisions[^1].Locked);
    Assert.InRange(decisions[^1].Bpm, 171, 179);
  }

  [Fact]
  public void ReportsMoreConfidenceOnceLocked() {
    // The number exists so the source selector can show that audio is a worse bet
    // than Link right now, without the user having to switch to it and watch.
    List<BeatDecision> decisions = Replay(new FrameTrack().Warmup().Beats(Bpm, 16));

    double beforeLock = decisions.Last(decision => !decision.Locked && decision.Bpm > 0).Confidence;
    double afterLock = decisions[^1].Confidence;

    Assert.InRange(beforeLock, 0, 0.5);
    Assert.True(afterLock > beforeLock, "Locking has to be worth more confidence than not locking.");
  }

  private static List<BeatDecision> Replay(FrameTrack track) {
    AudioBeatDetector detector = new AudioBeatDetector();

    return [.. track.Frames.Select(detector.Observe)];
  }
}
