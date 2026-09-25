namespace Flux.Rekordbox;

/// <summary>
/// Where a playhead sits in a track's beatgrid, as a fractional beat, and where the
/// downbeats around it are.
///
/// Beats are numbered by their place in the grid from 0, which makes the grid's own
/// numbering the source's beat numbering: a playhead between beats 11 and 12 reads
/// 11.something whatever the tempo, so a tempo change republished at the same
/// playhead continues the count instead of renumbering it.
///
/// Between two beats the position is interpolated, which is exact for rekordbox's
/// grids since they are piecewise constant. Outside the grid - before the first beat,
/// past the last, and across the last few beats a grid piles onto the track's final
/// millisecond - the nearest beat's own tempo carries the count on.
/// </summary>
internal static class BeatgridPosition {
  /// <summary>The fractional beat at <paramref name="trackMs"/>, milliseconds into the track at its normal speed.</summary>
  internal static double BeatAt(IReadOnlyList<AnlzBeat> grid, double trackMs) {
    ArgumentOutOfRangeException.ThrowIfZero(grid.Count);
    if(trackMs < grid[0].TimeMs) {
      return (trackMs - grid[0].TimeMs) / Interval(grid, 0);
    }

    int at = LastAtOrBefore(grid, trackMs);
    return at + ((trackMs - grid[at].TimeMs) / Interval(grid, at));
  }

  /// <summary>
  /// The downbeat nearest <paramref name="beat"/>, as a beat number. Found within the
  /// grid by each beat's own bar position; past its ends by counting on from the beat
  /// at the end, since a track's bars do not stop where its grid does.
  /// </summary>
  internal static long NearestDownbeat(IReadOnlyList<AnlzBeat> grid, double beat) {
    // A bar in the grid is four beats, so the five beats around the nearest hold one
    // downbeat or two, and two are told apart by the fractional beat rather than by the
    // rounded one: at 4.1, beats 2 and 6 are both two away from 4, and 6 is the nearer.
    long nearest = (long)Math.Round(beat);
    long? best = null;
    for(long candidate = nearest - 2; candidate <= nearest + 2; candidate++) {
      if(BeatInBar(grid, candidate) == 1 && (best is not { } b || Math.Abs(candidate - beat) < Math.Abs(b - beat))) {
        best = candidate;
      }
    }

    // None means the grid's own bar positions skip a downbeat, which is rekordbox's to
    // answer for. The nearest beat is the least wrong thing to call bar 1.
    return best ?? nearest;
  }

  /// <summary>The bar position rekordbox gives beat <paramref name="index"/>, counted on past the grid's ends.</summary>
  private static int BeatInBar(IReadOnlyList<AnlzBeat> grid, long index) {
    int edge = (int)Math.Clamp(index, 0, grid.Count - 1);
    long position = (grid[edge].BeatInBar - 1 + (index - edge)) % 4;
    return (int)(position < 0 ? position + 4 : position) + 1;
  }

  /// <summary>How long beat <paramref name="index"/> lasts in the track: to the next beat, or at its own tempo where there is none after it.</summary>
  private static double Interval(IReadOnlyList<AnlzBeat> grid, int index) {
    if(index + 1 < grid.Count && grid[index + 1].TimeMs > grid[index].TimeMs) {
      return grid[index + 1].TimeMs - grid[index].TimeMs;
    }

    // At the end, or among beats piled on one millisecond. A grid with no tempo at all
    // has never been seen, and 120 is only there so the count cannot divide by zero.
    float bpm = grid[index].Bpm > 0 ? grid[index].Bpm : 120f;
    return 60_000.0 / bpm;
  }

  /// <summary>The last beat at or before <paramref name="trackMs"/>, which the caller has checked is not before the first.</summary>
  private static int LastAtOrBefore(IReadOnlyList<AnlzBeat> grid, double trackMs) {
    int low = 0;
    int high = grid.Count - 1;
    while(low < high) {
      int middle = low + ((high - low + 1) / 2);
      if(grid[middle].TimeMs <= trackMs) {
        low = middle;
      } else {
        high = middle - 1;
      }
    }

    return low;
  }
}
