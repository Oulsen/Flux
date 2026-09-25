using System.Buffers.Binary;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// The tempos in an ANLZ file's beatgrid, which is how the finder tells a deck's ANLZ
/// path from the hundreds of library paths rekordbox also keeps in memory: the file a
/// deck's chain names has to carry that deck's tempo.
///
/// Only the part of the format this check needs, from Deep Symmetry's published
/// analysis-file notes: a <c>PMAI</c> header, then sections of tag, header length and
/// total length, all big-endian; in <c>PQTZ</c> each beat after the section header is
/// eight bytes of beat-in-bar, tempo in hundredths of a BPM, and time in milliseconds.
/// Flux's own ANLZ parser is Phase 6 step 3; this stays a check, not a reader.
/// </summary>
internal static class AnlzTempo {
  private const int BeatBytes = 8;

  /// <summary>The distinct tempos in the file's beatgrid, or null when it has none or is not an ANLZ file.</summary>
  internal static IReadOnlyList<float>? Read(string path) {
    try {
      return Read(File.ReadAllBytes(path));
    } catch(Exception error) when(error is IOException or UnauthorizedAccessException) {
      return null;
    }
  }

  internal static IReadOnlyList<float>? Read(ReadOnlySpan<byte> file) {
    if(file.Length < 12 || !file[..4].SequenceEqual("PMAI"u8)) {
      return null;
    }

    long at = BinaryPrimitives.ReadUInt32BigEndian(file[4..]);
    while(at + 12 <= file.Length) {
      ReadOnlySpan<byte> section = file[(int)at..];
      long header = BinaryPrimitives.ReadUInt32BigEndian(section[4..]);
      long length = BinaryPrimitives.ReadUInt32BigEndian(section[8..]);
      if(length < 12 || header > length || at + length > file.Length) {
        return null;
      }

      if(section[..4].SequenceEqual("PQTZ"u8)) {
        var tempos = new SortedSet<float>();
        for(long beat = header; beat + BeatBytes <= length; beat += BeatBytes) {
          tempos.Add(BinaryPrimitives.ReadUInt16BigEndian(section[((int)beat + 2)..]) / 100f);
        }

        return tempos.Count == 0 ? null : [.. tempos];
      }

      at += length;
    }

    return null;
  }

  /// <summary>Whether the file at <paramref name="path"/> has a beat at <paramref name="bpm"/>.</summary>
  internal static bool Carries(string path, float bpm) =>
    Read(path) is { } tempos && tempos.Any(tempo => MathF.Abs(tempo - bpm) < 0.06f);
}
