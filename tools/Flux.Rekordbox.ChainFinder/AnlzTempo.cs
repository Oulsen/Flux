namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// The tempos in an ANLZ file's beatgrid, which is how the finder tells a deck's ANLZ
/// path from the hundreds of library paths rekordbox also keeps in memory: the file a
/// deck's chain names has to carry that deck's tempo. The grid comes from Flux's own
/// reader, <see cref="AnlzBeatGrid"/>, so the finder accepts a file exactly when Flux
/// could use it.
/// </summary>
internal static class AnlzTempo {
  /// <summary>The distinct tempos in the file's beatgrid, or null when it has none or is not an ANLZ file.</summary>
  internal static IReadOnlyList<float>? Read(string path) =>
    AnlzFile.TryReadAllBytes(path) is { } file ? Read(file) : null;

  internal static IReadOnlyList<float>? Read(ReadOnlySpan<byte> file) =>
    AnlzBeatGrid.Read(file) is { } grid ? [.. grid.Select(beat => beat.Bpm).Distinct().Order()] : null;

  /// <summary>Whether the file at <paramref name="path"/> has a beat at <paramref name="bpm"/>.</summary>
  internal static bool Carries(string path, float bpm) =>
    Read(path) is { } tempos && tempos.Any(tempo => MathF.Abs(tempo - bpm) < 0.06f);
}
