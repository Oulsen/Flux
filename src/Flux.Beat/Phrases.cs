namespace Flux.Beat;

/// <summary>
/// What a stretch of a track is, as rekordbox's phrase analysis labels it. The labels
/// rekordbox shows depend on the mood it chose for the track, and every one of them is
/// one of these with a number beside it.
/// </summary>
public enum PhraseKind {
  Intro,
  Verse,
  Bridge,
  Up,
  Down,
  Chorus,
  Outro,
}

/// <summary>
/// One phrase of the track playing: what it is, the number rekordbox shows beside it -
/// the 2 in "Verse 2" or "Up 2", 0 for a phrase shown without one - and the local moment
/// its first beat falls on.
/// </summary>
public readonly record struct PhraseMark(PhraseKind Kind, int Variant, double StartMicros);

/// <summary>
/// The phrases of the track playing, laid out on the local clock.
///
/// <b>Moments rather than beat numbers</b>, for the reason a downbeat from rekordbox is
/// one (<see cref="ITimelineSink.AlignBarAt"/>): the phrases are a statement about the
/// track, and they hold whichever source is keeping time. With Link selected the beats
/// are numbered by Link, and a phrase start given as rekordbox's beat number would name
/// the wrong beat; given as a moment, it names the beat of any timeline that lands
/// within half a beat of it.
///
/// A moment on the local clock is only as good as the timeline it was worked out on, so
/// a source republishes the map whenever it republishes a different timeline. Phrase
/// starts already behind the playhead are worked out on the same timeline as the ones
/// ahead of it, which keeps the beats between any two of them a whole number even where
/// the tempo has moved since.
/// </summary>
/// <param name="Phrases">Every phrase of the track in order, each starting after the one before.</param>
/// <param name="EndMicros">Where the last phrase ends. Past it no phrase is playing.</param>
public sealed record PhraseMap(IReadOnlyList<PhraseMark> Phrases, double EndMicros) {
  /// <summary>
  /// The phrase playing at <paramref name="micros"/>, or null before the first phrase,
  /// from the end of the last, and in a map with none.
  /// </summary>
  public PhraseMark? At(double micros) {
    if(micros >= this.EndMicros) {
      return null;
    }

    int low = 0;
    int high = this.Phrases.Count - 1;
    int? found = null;
    while(low <= high) {
      int middle = low + ((high - low) / 2);
      if(this.Phrases[middle].StartMicros <= micros) {
        found = middle;
        low = middle + 1;
      } else {
        high = middle - 1;
      }
    }

    return found is { } at ? this.Phrases[at] : null;
  }
}
