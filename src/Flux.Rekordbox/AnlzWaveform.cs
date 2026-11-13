using System.Buffers.Binary;

namespace Flux.Rekordbox;

/// <summary>
/// A track's 3-band detail waveform: <see cref="EntriesPerSecond"/> columns a second of
/// track at normal speed, each three heights from 0 to <see cref="MaxHeight"/>, stored
/// low, mid, high, one byte each.
/// </summary>
internal sealed record AnlzWaveform(int EntriesPerSecond, byte[] LowMidHigh) {
  /// <summary>The tallest a band can be: the most any band reached in the library it was checked against, and the 7 bits the byte leaves.</summary>
  internal const int MaxHeight = 127;

  internal int Count => this.LowMidHigh.Length / 3;
}

/// <summary>
/// The 3-band detail waveform in a track's <c>.2EX</c> analysis file (<c>PWV7</c>), for the
/// page to draw the master deck as rekordbox does, with the beatgrid over it.
///
/// Header 24 bytes: tag, lengths, the entry size at 12 (always 3), the entry count at 16,
/// and at 20 a word Deep Symmetry's notes call unknown and "may always" be <c>0x00960000</c>.
/// In the library it is, in all 5,480 files, and its top half is 150: the entries a second,
/// which is also the count divided by the track's length. Each entry is three heights,
/// mid, high and low in that order per the notes; this hands them back as low, mid, high,
/// the order the rest of Flux counts bands in.
///
/// Read as strictly as the other tags. An entry size other than 3, a rate other than 150
/// or a height above 127 is a file this was never checked against, and reads as no
/// waveform rather than as a picture of the wrong thing.
/// </summary>
internal static class AnlzWaveformReader {
  private const int EntryBytes = 3;
  private const int EntriesPerSecond = 150;

  /// <summary>The waveform of the <c>.2EX</c> file at <paramref name="path"/>, or null when there is none to read.</summary>
  internal static AnlzWaveform? Read(string path) =>
    AnlzFile.TryReadAllBytes(path) is { } file ? Read(file) : null;

  /// <summary>The waveform, or null when the file has none, an empty one, or one laid out other than as checked.</summary>
  internal static AnlzWaveform? Read(ReadOnlySpan<byte> file) {
    if(!AnlzFile.TryReadTable(file, "PWV7"u8, EntryBytes, EntriesPerSecond, out ReadOnlySpan<byte> entries, out _)) {
      return null;
    }

    byte[] lowMidHigh = new byte[entries.Length];
    for(int i = 0; i < entries.Length; i += EntryBytes) {
      byte mid = entries[i];
      byte high = entries[i + 1];
      byte low = entries[i + 2];
      if(mid > AnlzWaveform.MaxHeight || high > AnlzWaveform.MaxHeight || low > AnlzWaveform.MaxHeight) {
        return null;
      }

      lowMidHigh[i] = low;
      lowMidHigh[i + 1] = mid;
      lowMidHigh[i + 2] = high;
    }

    return new AnlzWaveform(EntriesPerSecond, lowMidHigh);
  }
}

/// <summary>
/// A track's colour detail waveform, the one rekordbox's RGB view draws:
/// <see cref="EntriesPerSecond"/> columns a second, each four bytes - height 0 to
/// <see cref="MaxHeight"/>, then red, green and blue each 0 to <see cref="MaxColor"/>.
/// </summary>
internal sealed record AnlzColorWaveform(int EntriesPerSecond, byte[] HeightRgb) {
  internal const int MaxHeight = 31;
  internal const int MaxColor = 7;

  internal int Count => this.HeightRgb.Length / 4;
}

/// <summary>
/// The colour detail waveform in a track's <c>.EXT</c> analysis file (<c>PWV5</c>): what
/// rekordbox draws in its RGB waveform view, which is its default and the one the user's
/// screen shows, so the strip can look like the deck beside it.
///
/// Header 24 bytes: entry size at 12 (2), count at 16, and at 20 the word
/// <c>00 96 03 05</c> in all 5,489 tags of the library - 150 entries a second again, and
/// a second half nobody has explained. Each entry is a big-endian 16-bit word: red in
/// bits 15-13, green 12-10, blue 9-7, height 6-2, per Deep Symmetry's notes; bits 1-0
/// were 0 in every one of 12 million entries surveyed, and each field reached its full
/// range, which is the check that the fields sit where the notes put them.
///
/// Refused rather than drawn: an entry size or rate other than that, or a word with
/// either low bit set.
/// </summary>
internal static class AnlzColorWaveformReader {
  private const int EntryBytes = 2;
  private const int EntriesPerSecond = 150;

  /// <summary>The colour waveform of the <c>.EXT</c> file at <paramref name="path"/>, or null when there is none to read.</summary>
  internal static AnlzColorWaveform? Read(string path) =>
    AnlzFile.TryReadAllBytes(path) is { } file ? Read(file) : null;

  internal static AnlzColorWaveform? Read(ReadOnlySpan<byte> file) {
    if(!AnlzFile.TryReadTable(file, "PWV5"u8, EntryBytes, EntriesPerSecond, out ReadOnlySpan<byte> entries, out int count)) {
      return null;
    }

    byte[] heightRgb = new byte[count * 4];
    for(int i = 0; i < count; i++) {
      int word = BinaryPrimitives.ReadUInt16BigEndian(entries[(i * EntryBytes)..]);
      if((word & 0b11) != 0) {
        return null;
      }

      heightRgb[i * 4] = (byte)((word >> 2) & 0x1F);
      heightRgb[(i * 4) + 1] = (byte)((word >> 13) & 0x7);
      heightRgb[(i * 4) + 2] = (byte)((word >> 10) & 0x7);
      heightRgb[(i * 4) + 3] = (byte)((word >> 7) & 0x7);
    }

    return new AnlzColorWaveform(EntriesPerSecond, heightRgb);
  }
}
