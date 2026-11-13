using Flux.Rekordbox;
using Flux.Web.Server;

namespace Flux.App;

/// <summary>
/// The rekordbox source as the page's deck. A shape change and nothing more: the web
/// layer cannot reference the Windows-only reader, so the desktop app, which has both,
/// joins them, as it does for updates.
/// </summary>
internal sealed class RekordboxDeckView(RekordboxSource rekordbox): IDeckView {
  public DeckPlayhead? Playhead() {
    return rekordbox.Deck() is { } deck
      ? new DeckPlayhead(deck.Deck, deck.TrackId, deck.PositionMs, deck.Rate, deck.Playing)
      : null;
  }

  public DeckTrack? Track(string track) {
    return rekordbox.Track(track) is { } read
      ? new DeckTrack(read.EntriesPerSecond, read.LowMidHigh, read.HeightRgb, read.BeatTimesMs, read.BeatsInBar)
      : null;
  }
}
