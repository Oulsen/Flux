using Flux.Audio;

namespace Flux.Hue;

/// <summary>
/// Turns three band energies into three numbers that can be compared with each other.
///
/// <b>The problem this exists for is that the bands are not comparable as they arrive.</b>
/// Music carries far more energy in the kick band than in the highs, always, so reading
/// the raw levels against each other answers the same way on every frame of every track:
/// the kick leads. A room mapped straight off them is a fixed gradient - bright in the
/// middle, dim at the edges - that pulses in unison and never rearranges. It looks like
/// a dimmer set wrong rather than like music moving through a room, which is what
/// sixty frames off the live socket showed on 2026-09-11.
///
/// Each band is therefore read against its own recent loudest rather than against the
/// other two. A hi-hat at the top of what hats do in this track counts as fully lit
/// exactly as a kick at the top of what kicks do, so during a break with no kick the
/// outer lights lead, and the moment the drop lands the centre takes it back. That
/// inversion is the whole point: the ordering has to be able to change or there is no
/// information in it.
///
/// This is Flux's own and has no counterpart in the scene being reimplemented, which
/// drove colour sections by index and never compared one band against another. Nothing
/// here is a carried-across tuned value, so unlike the palette and the pulse the two
/// constants below are open to argument.
/// </summary>
internal sealed class BandDynamics {
  /// <summary>
  /// How long a band's remembered peak takes to fall to a tenth, with nothing louder
  /// arriving.
  ///
  /// Four seconds is about two bars at 120 BPM, and both ends of that are deliberate.
  /// Shorter and the peak tracks the pattern itself - a kick every half second keeps
  /// resetting it, every kick reads as "the loudest there has been", and the measure
  /// stops distinguishing a heavy bar from a light one. Longer and a breakdown stays
  /// dark for its whole length, because the section before it set a peak the quiet part
  /// cannot approach.
  ///
  /// Two bars is also the smallest window over which "how loud does this band get" is a
  /// question about the passage rather than about the last note.
  /// </summary>
  private const double PeakFallsToTenthInSeconds = 4.0;

  /// <summary>
  /// The quietest a band's peak can be and still be treated as playing.
  ///
  /// Without it, normalising divides a silent band by its own decayed-to-nothing peak
  /// and hands back a number near one, so a room with no music would light up as though
  /// every band were at full - and the quieter the input, the more confidently wrong the
  /// answer. The floor turns that into the honest reading: a band nothing is happening
  /// in produces nothing.
  ///
  /// Deliberately not a silence *gate* for the room as a whole. Whether a quiet room
  /// should be a dark room is the beat-energy envelope's question, and answering it here
  /// as well would leave two places deciding it.
  /// </summary>
  private const double SilentBelow = 0.02;

  /// <summary>
  /// How long the room's reference loudness takes to fall to a tenth, with nothing
  /// louder arriving.
  ///
  /// A minute, which is fifteen times the per-band window above, and the two are
  /// different measurements rather than one number used twice. A band's reference
  /// answers "is this band busy right now", where two bars is the right memory. The
  /// room's answers "how loud is this passage compared with the track it is part of",
  /// and a breakdown runs sixteen to thirty-two bars - so a reference that forgets
  /// inside one would quietly decide, halfway through the break, that the break is now
  /// normal and bring the lights back up under it. That is precisely the effect this
  /// measurement exists to avoid.
  ///
  /// The cost is at the other end and is stated rather than hidden: a genuinely quiet
  /// track following a loud one is read as quiet for its first minute. That is the right
  /// way round for a set, where the alternative flattens every dynamic a DJ builds.
  ///
  /// <b>Flux's own and never listened to.</b> This number and
  /// <see cref="PeakFallsToTenthInSeconds"/> are the two in this file that want tuning
  /// by ear with music playing, not by argument.
  /// </summary>
  private const double LoudnessFallsToTenthInSeconds = 60.0;

  /// <summary>
  /// What fraction of its recent loudest the room has to reach to count as fully playing.
  ///
  /// Three fifths, and this exists for the same reason <see cref="TransientAtRatio"/> was
  /// lowered: **a gate, not a dimmer.** Read as a bare ratio, loudness equals one only at
  /// the single loudest instant of the last minute and sits well below it the rest of the
  /// time, so multiplying the pulse by it dimmed every beat of every track rather than
  /// only the quiet ones - which is exactly what sixty frames off the live socket showed
  /// on 2026-09-11, a room that never passed 44 per cent of its ceiling.
  ///
  /// Saturating at three fifths means ordinary playing is simply "playing", and the
  /// measure does its work where it was always meant to: a breakdown well under the
  /// track's own level, and a finished track at nothing.
  /// </summary>
  private const double LoudEnoughFraction = 0.6;

  /// <summary>
  /// How far under the track's own loudest kick, in decibels, the kick band can sit and
  /// still count as playing a kick in full - and, below, how far under it counts as no
  /// kick at all.
  ///
  /// <b>What sizes the beat's pulse, replacing two measures that each failed at one end,
  /// found on 2026-09-30 by replaying frames recorded at the desk.</b> The first asked
  /// how far the kick stood out from its own last second, which reads a fifth in a drop,
  /// where the bassline fills the band and the kicks barely stand out. The patch for that
  /// let the room's running-hot energy vouch for the beat, which then pulsed a vocal and
  /// strings build at full on every beat, because a build is running hot by definition.
  /// What separates the passages is simpler than either: whether the kick band is at the
  /// level this track's kick plays at.
  ///
  /// <b>In decibels since 2026-11-12</b>, when the travelling pulse was seen running
  /// through a break. Until then this was a fraction of the band's energy against a
  /// reference falling to a tenth in a minute, and both halves of that were wrong for a
  /// band that is on a decibel scale already: a bassline twenty decibels under the kick
  /// still reads a sizeable fraction of it, and the reference forgets fast enough for
  /// that fraction to climb within seconds. Replaying the 2026-09-08 session showed it -
  /// a stretch fourteen to twenty decibels under the track's kick read as a kick at
  /// seven tenths to all of one. Measured in decibels, the drops of both recordings sit
  /// two to six under their loudest kick, that stretch fourteen to twenty, the 2026-09-30
  /// build twenty-two to twenty-five.
  ///
  /// A ramp and not a switch, smoothed at both ends, so a kick a little under the
  /// track's own gets most of a pulse. The light kick under the 2026-09-30 build sits
  /// twenty-five under and now gets none, where the fraction gave it a small one: that
  /// passage is carried by the shimmer instead. <b>Flux's own, reasoned from two
  /// recordings</b>, and the first pair to move if a break still pulses or a drop's
  /// kick reads short.
  /// </summary>
  private const double KickFullWithinDb = 6;

  /// <inheritdoc cref="KickFullWithinDb"/>
  private const double KickNoneBelowDb = 12;

  /// <summary>
  /// How far the sub band has to swing within <see cref="PunchWindowSeconds"/>, in
  /// decibels, before the kick band's level is believed - none at the first, whole at
  /// the second.
  ///
  /// <b>Level alone cannot tell a kick from a bassline, found on 2026-11-12</b> by
  /// recording a break the lights pulsed straight through. The break's bass sat as loud
  /// in the kick band as the drop's kicks, so it read as a whole kick on every beat. What
  /// tells them apart is shape: a kick's thump hits and falls away inside the beat, and a
  /// held bass does not. In the raw 40 to 100 Hz band that recording swung twenty to
  /// thirty decibels a beat wherever a kick played and two and a half through the break
  /// and the build. Measured per beat at 135 BPM, ten and sixteen let 2 of the break's 45
  /// beats through and kept all 54 of the drop's, whose quietest tenth still swung
  /// sixteen; six and twelve let 11 of the break's through, because a bassline does move
  /// now and then. <b>Flux's own, reasoned from that one recording.</b>
  /// </summary>
  private const double PunchNoneBelowDb = 10;

  /// <inheritdoc cref="PunchNoneBelowDb"/>
  private const double PunchFullFromDb = 16;

  /// <summary>
  /// How far back the sub band's swing is measured: six tenths of a second, which holds
  /// at least one kick and the trough after it from about 100 BPM up, so the swing is a
  /// whole beat's and never only the top or only the gap.
  /// </summary>
  private const double PunchWindowSeconds = 0.6;

  /// <summary>How many sub readings are kept, which at fifty frames a second covers <see cref="PunchWindowSeconds"/>.</summary>
  private const int PunchHistory = 40;

  /// <summary>
  /// How far under its own recent loudest, in decibels, a band reads as nothing for the
  /// swell - the measure the lights follow through a break.
  ///
  /// <b>In decibels because the energy ratio did not move</b>, found on 2026-11-12 in a
  /// recorded break: its vocals and pads moved five to eleven decibels every couple of
  /// seconds, which on the energy scale is a ratio of about four fifths, so a light
  /// following the ratio sat pinned near the top and the break looked stale. Twelve
  /// decibels spans the movement a phrase makes. <b>Flux's own and reasoned.</b>
  /// </summary>
  private const double SwellRangeDb = 12;

  /// <summary>
  /// How fast a band's loudest falls for the swell, in decibels a second: about a range
  /// in four seconds, so the measure follows the phrase rather than the track.
  /// </summary>
  private const double SwellFallsDbPerSecond = 3;

  /// <summary>
  /// How long a band's running average looks back, which is what a transient is measured
  /// against.
  ///
  /// A second, which is two beats at 120 BPM. Shorter and the average chases the pattern
  /// it is supposed to be the background of - at a third of a second a kick every half
  /// second drags the mean up behind it and the next kick no longer stands out. Longer
  /// and the measure stops being about now: a bar that has just got busier reads as
  /// ordinary for several seconds afterwards.
  /// </summary>
  private const double BandAverageSeconds = 1.0;

  /// <summary>
  /// How far above its own running average a band has to be to count as fully hitting.
  ///
  /// Half again, lowered from twice on 2026-09-11 because the room was measurably dim:
  /// sixty frames off the live socket never reached half the brightness they were
  /// entitled to. **This is a gate and not a dimmer**, and that distinction is what the
  /// number has to express. Whether a band is hitting is a yes-or-no question in
  /// practice, so the answer should reach the top under ordinary playing and bite only
  /// when the band has genuinely gone flat. Demanding a doubling made an ordinary kick
  /// worth a fraction of a pulse and left the room permanently under its ceiling.
  ///
  /// Zero is unaffected by the change, which is what matters for a breakdown: a band
  /// sitting at its own average scores nothing whatever this is set to, so what this
  /// moves is how quickly a real hit counts for everything and never whether silence
  /// counts for nothing.
  /// </summary>
  private const double TransientAtRatio = 1.5;

  /// <summary>
  /// How long the room's energy is averaged over before it is compared with the track,
  /// which is what sizes the beat's pulse by the passage it is in.
  ///
  /// A second and a half, about three beats at 120 BPM. Long enough that one loud kick or
  /// the gap after it does not move the answer, so the pulses within a passage stay one
  /// size; short enough that the drop landing after a quiet bar is answered within its
  /// first bar rather than after it. <b>Flux's own and reasoned, not listened to.</b>
  /// </summary>
  private const double SectionEnergySeconds = 1.5;

  /// <summary>
  /// How far under the track's loudest passage, in decibels, the passage playing now can
  /// sit and still pulse in full - and, below, how far under it pulses not at all.
  ///
  /// <b>The answer to "the lights beat just as hard when the music is quiet",
  /// reported on 2026-11-12.</b> The bands are on a decibel scale already, so a passage
  /// ten decibels under the drop still reads about two thirds of the drop's energy, and
  /// the loudness gate counts anything over three fifths as fully playing. Replaying the
  /// 2026-09-08 session showed the room pulsing at nine tenths of its height on nearly
  /// every beat for minutes, through a stretch eight to twelve decibels under the rest.
  /// Measured in decibels the passages separate: that session's steady playing sits
  /// within three of its loudest, its quieter stretch eight to twelve under, and the
  /// 2026-09-30 build fifteen to twenty-five under its drop.
  ///
  /// Smoothed between the two rather than linear, so neither end is a visible step, and
  /// nine under - the middle - gets half a pulse. <b>Flux's own, reasoned from two
  /// recordings and not yet listened to</b>, and the first pair to move if a quiet
  /// passage still pumps or a drop looks held back.
  /// </summary>
  private const double FullWithinDb = 3;

  /// <inheritdoc cref="FullWithinDb"/>
  private const double NoneBelowDb = 15;

  /// <summary>
  /// How fast the loudest passage is forgotten, in decibels a second, with nothing as
  /// loud arriving.
  ///
  /// A fixed rate in decibels and not a fall to a fraction, which is the lesson of the
  /// loudness reference beside it: falling to a tenth in a minute sounds slow, but on a
  /// decibel scale a passage at two thirds of the drop catches it up in about ten
  /// seconds, and from then on the quieter passage counts as normal. At a tenth of a
  /// decibel a second a passage ten under is still read as quieter half a minute later -
  /// sixteen bars at 128 BPM - and a genuinely quieter track following a loud one is
  /// caught up within about a minute and a half. <b>Flux's own and reasoned.</b>
  /// </summary>
  private const double LoudestFallsDbPerSecond = 0.1;

  /// <summary>
  /// How far the hat band has to jump above its own last tenth of a second, in decibels,
  /// to count as a hit.
  ///
  /// <b>What turns the hat band into hits, found on 2026-11-12 by replaying the
  /// 2026-09-08 session's audio through the analyzer's transform.</b> Read raw, a hat is
  /// a step: an open hat went from -86 to -47 in one frame, a closed one rises twenty or
  /// more, and what sits between hits falls away fast. Fifteen counted three to six hits a
  /// second through the passages with hats and almost none through the quiet stretch;
  /// ten let the tail of a busy pattern count as hits of its own, twenty dropped most
  /// closed hats. <b>Flux's own, reasoned from two recordings</b>, like the two beside it.
  /// </summary>
  private const double HatRiseDb = 15;

  /// <summary>
  /// How far under the hat band's loudest a hit may land and still count, in decibels.
  ///
  /// The same idea as the kick's: a hit is a hit of this track's hats, and a jump in a
  /// band that is otherwise near silent - the noise between tracks, a reverb tail - is
  /// not. The loudest falls at <see cref="LoudestFallsDbPerSecond"/>.
  /// </summary>
  private const double HatNearLoudestDb = 15;

  /// <summary>
  /// How far back a hit's rise is measured from, and the least time between two hits.
  ///
  /// A tenth of a second back is about a sixteenth at 150 BPM, the closest two hats sit
  /// in most music, so the rise is from the gap before this hat rather than from the hat
  /// before it. Eighty milliseconds between hits is two bridge frames, the shortest
  /// sparkle a bulb will render as one.
  /// </summary>
  private const double HatLookBackSeconds = 0.1;

  /// <inheritdoc cref="HatLookBackSeconds"/>
  private const double HatSpacingSeconds = 0.08;

  /// <summary>How many recent hat readings are kept, which at fifty frames a second covers <see cref="HatLookBackSeconds"/> with room to spare.</summary>
  private const int HatHistory = 8;

  private double kickPeak;
  private double midsPeak;
  private double highsPeak;
  private double kickSwellDb;
  private double midsSwellDb;
  private double highsSwellDb;
  private double roomPeak;

  /// <summary>The loudest the kick band has been, in decibels up the band scale, falling at <see cref="LoudestFallsDbPerSecond"/>.</summary>
  private double kickLoudestDb;

  /// <summary>The room's energy averaged over <see cref="SectionEnergySeconds"/>, or null before the first frame.</summary>
  private double? sectionEnergy;

  /// <summary>The loudest the averaged energy has been, in decibels up the band scale, falling at <see cref="LoudestFallsDbPerSecond"/>.</summary>
  private double sectionLoudestDb;

  /// <summary>How long this has been observing, which the hat history is timed against.</summary>
  private double observedSeconds;

  private readonly double[] hatHistoryDb = new double[HatHistory];
  private readonly double[] hatHistoryAt = [.. Enumerable.Repeat(double.NegativeInfinity, HatHistory)];
  private int hatHistoryNext;
  private double hatsLoudestDb = SpectrumLevels.SilentDb;
  private double lastHatHitAt = double.NegativeInfinity;

  private readonly double[] subHistoryDb = new double[PunchHistory];
  private readonly double[] subHistoryAt = [.. Enumerable.Repeat(double.NegativeInfinity, PunchHistory)];
  private int subHistoryNext;

  private double kickMean;
  private double midsMean;
  private double highsMean;
  private bool heardAnything;

  /// <summary>
  /// What the room sounds like in the two forms the engine needs, or null when nothing
  /// is playing at all.
  ///
  /// Null means "no opinion" and is the same answer the engine gives when there is no
  /// audio project in the build: every light on the room's own envelope, pulsing at full
  /// height. It is not a failure. It is the only honest answer when Flux is being driven
  /// by a clock with no microphone anywhere near it, which is an ordinary way to run it.
  /// </summary>
  internal RoomSound? Observe(SpectrumLevels heard, double elapsedSeconds) {
    // Clamped because a stalled frame must not wipe the memory in one step, and a
    // negative would raise the peaks instead of lowering them. Both are properties of
    // the clock rather than of the music, and neither should be audible in the room.
    double elapsed = Math.Max(0, elapsedSeconds);
    double bandFall = Math.Pow(0.1, Math.Min(elapsed, PeakFallsToTenthInSeconds) / PeakFallsToTenthInSeconds);
    double roomFall = Math.Pow(0.1, Math.Min(elapsed, LoudnessFallsToTenthInSeconds) / LoudnessFallsToTenthInSeconds);

    this.kickPeak = Math.Max(heard.Kick, this.kickPeak * bandFall);
    this.midsPeak = Math.Max(heard.Mids, this.midsPeak * bandFall);
    this.highsPeak = Math.Max(heard.Highs, this.highsPeak * bandFall);

    double loudest = heard.Peak;
    this.roomPeak = Math.Max(loudest, this.roomPeak * roomFall);

    // At the slow decibel rate and not the band's two bars: the band's peak falls far
    // enough inside a four-second break for the strings' low end to read as a whole
    // kick, which is precisely what this has to tell apart. See KickFullWithinDb.
    double kickDb = DecibelsOf(heard.Kick);
    this.kickLoudestDb = Math.Max(kickDb, this.kickLoudestDb - (LoudestFallsDbPerSecond * elapsed));

    // Kept up whether or not the room is playing, like the references above, so the
    // first hat after a silence is measured against the silence.
    this.observedSeconds += elapsed;
    bool hatHit = this.HatHit(heard.HatsDb, elapsed);
    double punch = this.Punch(heard.SubDb);

    // Started at the first frame for the reason the band means below are, and kept up
    // through silence like the peaks, so a track starting finds it where the room is.
    // Compared in decibels, for the reason LoudestFallsDbPerSecond gives.
    double section = this.sectionEnergy is { } held
      ? held + ((loudest - held) * (1 - Math.Exp(-elapsed / SectionEnergySeconds)))
      : loudest;
    this.sectionEnergy = section;
    double sectionDb = DecibelsOf(section);
    this.sectionLoudestDb = Math.Max(sectionDb, this.sectionLoudestDb - (LoudestFallsDbPerSecond * elapsed));

    // The room's own reference and not the three band ones: a track with no hats leaves
    // the highs reference under the floor all night, and that is a fact about the track
    // rather than a reason to stop measuring the room.
    if(this.roomPeak < SilentBelow) {
      return null;
    }

    // Started at the first frame rather than at zero. Rising from nothing would make
    // every band read as many times its own average for the first second, which is a
    // room that flares on the first note of the night and then settles.
    if(!this.heardAnything) {
      this.heardAnything = true;
      this.kickMean = heard.Kick;
      this.midsMean = heard.Mids;
      this.highsMean = heard.Highs;
    } else {
      double approach = 1 - Math.Exp(-elapsed / BandAverageSeconds);

      this.kickMean += (heard.Kick - this.kickMean) * approach;
      this.midsMean += (heard.Mids - this.midsMean) * approach;
      this.highsMean += (heard.Highs - this.highsMean) * approach;
    }

    SpectrumLevels drive = new SpectrumLevels(
      Against(heard.Kick, this.kickPeak),
      Against(heard.Mids, this.midsPeak),
      Against(heard.Highs, this.highsPeak));

    double swellFall = SwellFallsDbPerSecond * elapsed;
    this.kickSwellDb = Math.Max(DecibelsOf(heard.Kick), this.kickSwellDb - swellFall);
    this.midsSwellDb = Math.Max(DecibelsOf(heard.Mids), this.midsSwellDb - swellFall);
    this.highsSwellDb = Math.Max(DecibelsOf(heard.Highs), this.highsSwellDb - swellFall);

    SpectrumLevels swell = new SpectrumLevels(
      Swell(heard.Kick, this.kickSwellDb),
      Swell(heard.Mids, this.midsSwellDb),
      Swell(heard.Highs, this.highsSwellDb));

    SpectrumLevels transient = new SpectrumLevels(
      Above(heard.Kick, this.kickMean),
      Above(heard.Mids, this.midsMean),
      Above(heard.Highs, this.highsMean));

    // The smaller of the two, because they are two conditions on one answer: the band at
    // this track's kick level, and moving the way a kick moves. Either alone was fooled.
    double kickPresence = this.kickLoudestDb < DecibelsOf(SilentBelow)
      ? 0
      : Math.Min(
        SmoothStep((KickNoneBelowDb - (this.kickLoudestDb - kickDb)) / (KickNoneBelowDb - KickFullWithinDb)),
        punch);

    double intensity = SmoothStep((NoneBelowDb - (this.sectionLoudestDb - sectionDb)) / (NoneBelowDb - FullWithinDb));

    return new RoomSound(
      drive,
      transient,
      Math.Clamp(loudest / (this.roomPeak * LoudEnoughFraction), 0, 1),
      kickPresence,
      intensity,
      hatHit,
      swell);
  }

  /// <summary>A band's level as how close it is to its own recent loudest, 0 to 1, in decibels - see <see cref="SwellRangeDb"/>.</summary>
  private static double Swell(double energy, double loudestDb) {
    return energy < SilentBelow ? 0 : SmoothStep((SwellRangeDb - (loudestDb - DecibelsOf(energy))) / SwellRangeDb);
  }

  /// <summary>
  /// How much the sub band moves like a kick, 0 to 1, from its swing over the last
  /// <see cref="PunchWindowSeconds"/>; see <see cref="PunchNoneBelowDb"/>.
  ///
  /// One when the band was never measured - a source with no sub band, or a recording
  /// made before it existed - so that the level check alone decides, exactly as it did
  /// before this measure, rather than every beat reading as no kick.
  /// </summary>
  private double Punch(double subDb) {
    this.subHistoryDb[this.subHistoryNext] = subDb;
    this.subHistoryAt[this.subHistoryNext] = this.observedSeconds;
    this.subHistoryNext = (this.subHistoryNext + 1) % PunchHistory;

    if(subDb <= SpectrumLevels.SilentDb) {
      return 1;
    }

    double highest = double.NegativeInfinity;
    double lowest = double.PositiveInfinity;
    for(int index = 0; index < PunchHistory; index++) {
      if(this.observedSeconds - this.subHistoryAt[index] <= PunchWindowSeconds) {
        highest = Math.Max(highest, this.subHistoryDb[index]);
        lowest = Math.Min(lowest, this.subHistoryDb[index]);
      }
    }

    return SmoothStep((highest - lowest - PunchNoneBelowDb) / (PunchFullFromDb - PunchNoneBelowDb));
  }

  /// <summary>
  /// Whether the hat band was hit on this frame, and remembers the reading for the next.
  ///
  /// A rise of <see cref="HatRiseDb"/> over the quietest of the last
  /// <see cref="HatLookBackSeconds"/>, near the band's own loudest, and not inside
  /// <see cref="HatSpacingSeconds"/> of the last hit. The quietest rather than the last
  /// reading, because the light engine samples a sixty-frame analyzer fifty times a
  /// second and can land on the second frame of a hat: measured from the frame before,
  /// that hat would rise nothing.
  /// </summary>
  private bool HatHit(double hatsDb, double elapsed) {
    this.hatsLoudestDb = Math.Max(hatsDb, this.hatsLoudestDb - (LoudestFallsDbPerSecond * elapsed));

    double quietest = double.PositiveInfinity;
    for(int index = 0; index < HatHistory; index++) {
      if(this.observedSeconds - this.hatHistoryAt[index] <= HatLookBackSeconds) {
        quietest = Math.Min(quietest, this.hatHistoryDb[index]);
      }
    }

    bool hit = double.IsFinite(quietest)
      && hatsDb - quietest >= HatRiseDb
      && hatsDb >= this.hatsLoudestDb - HatNearLoudestDb
      && this.observedSeconds - this.lastHatHitAt >= HatSpacingSeconds;

    if(hit) {
      this.lastHatHitAt = this.observedSeconds;
    }

    this.hatHistoryDb[this.hatHistoryNext] = hatsDb;
    this.hatHistoryAt[this.hatHistoryNext] = this.observedSeconds;
    this.hatHistoryNext = (this.hatHistoryNext + 1) % HatHistory;

    return hit;
  }

  /// <summary>
  /// A band's energy as decibels up its scale, which is the form two passages are
  /// compared in - see <see cref="SpectrumLevels.DecibelSpan"/> for why the square root.
  /// </summary>
  private static double DecibelsOf(double energy) {
    return Math.Sqrt(Math.Max(energy, 0)) * SpectrumLevels.DecibelSpan;
  }

  /// <summary>A ramp from 0 to 1 that leaves and arrives flat, for a value clamped to 0 to 1 first.</summary>
  private static double SmoothStep(double value) {
    double x = Math.Clamp(value, 0, 1);

    return x * x * (3 - (2 * x));
  }

  /// <summary>
  /// How hard a band is hitting right now compared with what it has been doing, 0 to 1.
  ///
  /// <b>This is what makes one light differ from another in time rather than only in
  /// height.</b> The level measure beside it answers "is this band loud", which is a slow
  /// question - and a room driven only by that plus a beat pulse is a set of simultaneous
  /// flashes at different heights, which is what the room still looked like after the
  /// first attempt at this on 2026-09-11. A transient answers "is this band doing
  /// something right now", so a hat pattern blinks the outer lights in the gaps where
  /// nothing is happening to the kick.
  ///
  /// Zero for a band sitting at its own average, which is the point and is worth being
  /// explicit about: a sustained pad contributes nothing here however loud it is. What
  /// keeps those lights alive is the beat share and the floor, not this.
  /// </summary>
  private static double Above(double level, double mean) {
    if(mean < SilentBelow) {
      return 0;
    }

    return Math.Clamp(((level / mean) - 1) / (TransientAtRatio - 1), 0, 1);
  }

  /// <summary>
  /// One band's level as a fraction of its own peak, and zero for a band that is not
  /// playing.
  ///
  /// The zero is per band rather than per room: a track with no hats at all should leave
  /// the lights that listen to the top sitting at the room's resting level for the whole
  /// of it, which is what the engine's floor is for and is a great deal better than
  /// having them shimmer at whatever the noise floor divides into.
  /// </summary>
  private static double Against(double level, double peak) {
    return peak < SilentBelow ? 0 : Math.Clamp(level / peak, 0, 1);
  }
}

/// <summary>
/// What the room sounds like, in the two forms the light engine asks two different
/// questions of.
/// </summary>
/// <param name="Drive">
/// Each band as a fraction of its own recent loudest, which is the only form in which
/// the three can be compared with each other.
///
/// Carried as a <see cref="SpectrumLevels"/> rather than as a type of its own, and the
/// reason is the frequency axis rather than laziness: the mapping from a light's
/// position to a band is <see cref="SpectrumLevels.At"/>, it is subtle enough to be
/// worth having once, and a second record would either duplicate it or force every
/// caller to convert. What this holds is the same shape - three band values on one axis -
/// measured differently.
/// </param>
/// <param name="Transient">
/// How hard each band is hitting compared with what it has been doing, 0 to 1.
///
/// The fast question where <paramref name="Drive"/> is the slow one, and the pair is the
/// whole of the per-light behaviour: <paramref name="Drive"/> decides which lights lead
/// on a beat, and this decides what lifts a light in the gaps between beats. Without it
/// every light rises and falls at the same instant whatever its share of the beat is,
/// which reads from across a room as every light doing the same thing - and did, on the
/// first attempt at this.
/// </param>
/// <param name="Loudness">
/// How loud the room is now against the loudest it has been lately, 0 to 1.
///
/// <b>This is the answer to "the lights react to the beat but not to the music".</b> A
/// beat arriving during a breakdown is still a beat and the grid still publishes it, so
/// without this the room answers a quiet passage exactly as hard as it answers the drop
/// - and answers a finished track exactly as hard as a playing one, which is the form
/// the problem was first noticed in.
///
/// Measured against the room's own recent loudest rather than against full scale,
/// because absolute level says more about where the gain is set than about the music.
/// The comparison is the same one a person makes without thinking: quiet compared with
/// what this has been.
///
/// It reaches one well before the room reaches its loudest, and that is the correction
/// made after watching it: as a bare ratio this is one only at the single loudest instant
/// of the last minute, so it dimmed every beat of every track instead of only the quiet
/// ones. See <see cref="BandDynamics.LoudEnoughFraction"/>.
/// </param>
/// <param name="KickPresence">
/// Whether the kick band is playing a kick at this track's own level, 0 to 1.
///
/// What a beat's pulse is sized by. In decibels under the loudest the kick has been
/// lately, ramped between <see cref="BandDynamics"/>'s two kick thresholds, so a drop
/// whose kicks are buried in its bassline still reads as fully present, a build's low
/// strings read as little or nothing, and a break as nothing at all.
/// </param>
/// <param name="Intensity">
/// How hard the passage playing now is, against the track's recent hardest, 0 to 1.
///
/// A dimmer where <paramref name="Loudness"/> and <paramref name="KickPresence"/> are
/// gates, and the difference is the point: those two decide whether a beat counts at
/// all, and this decides how big it is allowed to be. A drop is whole, a quieter
/// passage with its kick still in gets a smaller pulse, and a breakdown none, which is
/// what a gate saturating at three fifths of a decibel scale could not tell apart.
/// Averaged over seconds rather than read per frame, so the beats of one passage are
/// one size. See <see cref="BandDynamics"/>'s section constants.
/// </param>
/// <param name="HatHit">
/// Whether a hi-hat or cymbal was hit on this frame. True on one frame per hit and
/// never on two in a row, so the engine can start one sparkle per hit without
/// remembering anything about the band itself.
/// </param>
/// <param name="Swell">
/// Each band as how close it is to its own loudest of the last few seconds, in
/// decibels, 0 to 1: what a light follows through a break, where the energy ratio of
/// <paramref name="Drive"/> barely moves. See <see cref="BandDynamics"/>'s swell constants.
/// </param>
internal readonly record struct RoomSound(SpectrumLevels Drive, SpectrumLevels Transient, double Loudness, double KickPresence, double Intensity, bool HatHit, SpectrumLevels Swell);
