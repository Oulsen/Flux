namespace Flux.Web.Server;

/// <summary>Where the playhead of the deck being followed is.</summary>
/// <param name="Deck">The deck, counted from 1.</param>
/// <param name="Track">Opaque; names the track for <see cref="IDeckView.Track"/>, and changes when the track or its analysis does.</param>
/// <param name="PositionMs">Milliseconds into the track at its normal speed.</param>
/// <param name="Rate">Track milliseconds per real millisecond: 1 at normal speed.</param>
/// <param name="Playing">Whether the playhead is moving.</param>
public sealed record DeckPlayhead(int Deck, string Track, double PositionMs, double Rate, bool Playing);

/// <summary>A track's waveform and beatgrid, as the page draws them.</summary>
/// <param name="EntriesPerSecond">Waveform columns per second of track, for both waveforms; 0 when there is neither.</param>
/// <param name="LowMidHigh">The 3-band waveform: three heights per column, low, mid, high, each 0 to 127; null without one.</param>
/// <param name="HeightRgb">The colour waveform: four bytes per column, height 0 to 31 then red, green, blue each 0 to 7; null without one.</param>
/// <param name="BeatTimesMs">Each beat, in milliseconds into the track.</param>
/// <param name="BeatsInBar">Each beat's place in its bar, 1 to 4.</param>
public sealed record DeckTrack(int EntriesPerSecond, byte[]? LowMidHigh, byte[]? HeightRgb, IReadOnlyList<int> BeatTimesMs, IReadOnlyList<int> BeatsInBar);

/// <summary>
/// The deck a DJ program is playing, for the page to draw beside the beat.
///
/// Here rather than beside its implementation for the reason <see cref="IAppUpdates"/>
/// is: the rekordbox reader is Windows-only and this project is not, so the desktop app
/// adapts one to the other. A host that reads no DJ program registers none, and the page
/// shows no deck.
/// </summary>
public interface IDeckView {
  /// <summary>The deck followed now, or null while none with an analysed track is.</summary>
  DeckPlayhead? Playhead();

  /// <summary>
  /// The track <paramref name="track"/> names, or null unless it is the one on the deck
  /// followed now. Reads the disk on the first ask for a track, so not for a hot path.
  /// </summary>
  DeckTrack? Track(string track);
}
