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
  /// Twice, which is a hit that doubles what the band has been doing. Below this the
  /// response is proportional, above it the light is simply at the top. The one thing
  /// this number decides in practice is how much of a hat pattern reaches the outer
  /// lights, and it is the first thing to move if the room shimmers too much or too
  /// little.
  /// </summary>
  private const double TransientAtRatio = 2.0;

  private double kickPeak;
  private double midsPeak;
  private double highsPeak;
  private double roomPeak;

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

    SpectrumLevels transient = new SpectrumLevels(
      Above(heard.Kick, this.kickMean),
      Above(heard.Mids, this.midsMean),
      Above(heard.Highs, this.highsMean));

    return new RoomSound(drive, transient, Math.Clamp(loudest / this.roomPeak, 0, 1));
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
/// </param>
internal readonly record struct RoomSound(SpectrumLevels Drive, SpectrumLevels Transient, double Loudness);
