namespace Flux.Beat;

/// <summary>How much a sync source currently knows.</summary>
public enum SyncSourceState {
  /// <summary>Nothing has ever been received from it.</summary>
  Waiting,

  /// <summary>It is publishing.</summary>
  Live,

  /// <summary>It published once and has since gone silent.</summary>
  Quiet,
}

/// <summary>
/// What a sync source reports about itself, whether or not it is the active one.
///
/// Reported by every registered source rather than only the active one, because
/// that is exactly the question the source selector has to answer: a user
/// choosing between Link and audio needs to see which of them currently has a
/// signal, and finding out by switching and watching the lights die is not an
/// acceptable way to ask.
/// </summary>
/// <param name="State">Whether the source has ever published, and whether it still is.</param>
/// <param name="Confidence">
/// How much the source trusts its own tempo, from 0 to 1. Meaningful only for a
/// source that <em>estimates</em> the tempo: a source that is told the tempo
/// reports 1 whenever it knows one at all, because it has no uncertainty to
/// describe and inventing a number would only make the honest ones look worse.
/// </param>
public readonly record struct SyncSourceStatus(SyncSourceState State, double Confidence);

/// <summary>
/// One way of knowing where the beat is: an Ableton Link session, audio
/// analysis, or a human tapping.
///
/// A source's entire job is to publish timelines through its
/// <see cref="ITimelineSink"/> and to say how it is doing. It does not count
/// beats, number them, decide when they fire, or know whether it is the active
/// source, and none of that is an oversight: there is one
/// <see cref="BeatGrid"/> and one <see cref="BeatScheduler"/> for all sources,
/// so a new source cannot introduce a second opinion about what a beat is.
/// </summary>
public interface ISyncSource {
  /// <summary>
  /// The name the protocol uses for this source: <c>link</c>, <c>audio</c> or
  /// <c>manual</c>. Stable, lowercase, and part of the wire contract.
  /// </summary>
  string Name { get; }

  /// <summary>
  /// The source's own view of its health. Called from the web layer at the state
  /// rate, so it must not block or allocate meaningfully.
  /// </summary>
  SyncSourceStatus Status();
}

/// <summary>
/// Where a source hands its timelines. One sink per registered source, handed
/// out by <see cref="SyncSourceManager"/>.
///
/// A source publishes unconditionally and never asks whether it is active. That
/// keeps the switching decision in exactly one place, and it means an inactive
/// source stays warm: it keeps a current timeline ready, so selecting it takes
/// effect on the next frame rather than after however long that source needs to
/// work out the tempo again.
/// </summary>
public interface ITimelineSink {
  /// <summary>
  /// Offers a freshly derived timeline.
  /// </summary>
  /// <param name="timeline">The timeline now in force for this source.</param>
  /// <param name="newSession">
  /// True when the beat numbering was rebased rather than merely re-anchored: a
  /// Link session change, an audio drop, a tap that sets a new phase. Anything
  /// anchored to a beat index has to re-arm when this is set.
  /// </param>
  void Publish(BeatTimeline timeline, bool newSession = false);

  /// <summary>
  /// Marks the beat nearest to now as the downbeat.
  ///
  /// Deliberately <em>not</em> gated on this source being the active one, which
  /// is the difference between a tap and a timeline. A timeline is one source's
  /// opinion about the tempo and only the active one's counts; a downbeat is a
  /// statement about the music in the room, and it is equally true whoever is
  /// timing it. Tapping the downbeat while Link supplies the tempo is a normal
  /// thing to want, and it is the only way to get one, because no source
  /// transmits a downbeat.
  /// </summary>
  /// <param name="method">What supplied the downbeat, for the UI to show.</param>
  void AlignBar(BarAlignmentMethod method);
}
