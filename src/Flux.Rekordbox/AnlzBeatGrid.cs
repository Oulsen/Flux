using System.Buffers.Binary;

namespace Flux.Rekordbox;

/// <summary>
/// One beat of rekordbox's beatgrid: where it falls in its bar (1 to 4), the tempo at
/// it, and when it plays at the track's normal speed, in milliseconds from the start.
/// </summary>
internal readonly record struct AnlzBeat(int BeatInBar, float Bpm, int TimeMs);

/// <summary>
/// The beatgrid in a track's <c>.DAT</c> analysis file (<c>PQTZ</c>), which is the one
/// thing no other Flux source has: each beat's position in its bar, so bar 1 comes from
/// the track rather than from a tap. About one track in five in the library it was
/// checked against does not start on beat 1, which is the case a count from the first
/// beat would get wrong.
///
/// A beat is eight bytes after a 24-byte section header: beat in bar, tempo in
/// hundredths of a BPM, time in milliseconds. The time is signed, which the published
/// notes do not say: rekordbox writes -1 when the first beat falls a millisecond before
/// the audio starts, and read unsigned that is a beat seven weeks into the track.
///
/// What is refused is what cannot be a grid - a beat outside 1 to 4, time running
/// backwards, a count the section cannot hold. Times that stand still are kept: a grid
/// that runs past the end of the audio has its last few beats piled on the track's
/// final millisecond, and those are rekordbox's, not damage. The grid is returned as
/// written and deciding what it means is for the sync source.
/// </summary>
internal static class AnlzBeatGrid {
  private const int HeaderBytes = 24;
  private const int BeatBytes = 8;

  /// <summary>The beatgrid of the <c>.DAT</c> file at <paramref name="path"/>, or null when there is none to read.</summary>
  internal static IReadOnlyList<AnlzBeat>? Read(string path) =>
    AnlzFile.TryReadAllBytes(path) is { } file ? Read(file) : null;

  /// <summary>
  /// The beatgrid, or null when the file has none, has an empty one - a track rekordbox
  /// has not analysed - or has one that cannot be a grid.
  /// </summary>
  internal static IReadOnlyList<AnlzBeat>? Read(ReadOnlySpan<byte> file) {
    if(!AnlzFile.TryFindSection(file, "PQTZ"u8, out ReadOnlySpan<byte> section, out int header) || header < HeaderBytes) {
      return null;
    }

    long count = BinaryPrimitives.ReadUInt32BigEndian(section[20..]);
    if(count == 0 || header + (count * BeatBytes) > section.Length) {
      return null;
    }

    var beats = new AnlzBeat[count];
    for(int i = 0; i < count; i++) {
      ReadOnlySpan<byte> entry = section.Slice(header + (i * BeatBytes), BeatBytes);
      int beatInBar = BinaryPrimitives.ReadUInt16BigEndian(entry);
      int time = BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
      if(beatInBar is < 1 or > 4 || (i > 0 && time < beats[i - 1].TimeMs)) {
        return null;
      }

      beats[i] = new AnlzBeat(beatInBar, BinaryPrimitives.ReadUInt16BigEndian(entry[2..]) / 100f, time);
    }

    return beats;
  }
}
