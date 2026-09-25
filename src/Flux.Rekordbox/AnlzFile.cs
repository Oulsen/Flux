using System.Buffers.Binary;

namespace Flux.Rekordbox;

/// <summary>
/// The envelope every rekordbox analysis (ANLZ) file shares: a <c>PMAI</c> header, then
/// tagged sections of four-character tag, header length and total length, all
/// big-endian. The layout is from Deep Symmetry's published analysis-file notes
/// (djl-analysis.deepsymmetry.org) and was checked against every file in a rekordbox
/// 7.2.19 library; <c>docs/rekordbox-anlz.md</c> has what that check found.
///
/// Finding a section is the only thing this does, and it refuses rather than guesses:
/// a section that claims more bytes than the file holds ends the walk with nothing
/// found, because reading on would take values from whatever follows. The tag readers
/// built on it check their own contents the same way.
/// </summary>
internal static class AnlzFile {
  private const int EnvelopeBytes = 12;

  /// <summary>
  /// The analysis file rekordbox writes beside <paramref name="datPath"/> for data the
  /// <c>.DAT</c> has no room for - phrases among it. A deck's chain names the
  /// <c>.DAT</c>, and the three files differ only by extension.
  /// </summary>
  internal static string ExtendedPath(string datPath) {
    ArgumentException.ThrowIfNullOrEmpty(datPath);
    return Path.ChangeExtension(datPath, ".EXT");
  }

  /// <summary>
  /// The first section tagged <paramref name="tag"/>, from its tag onwards, and how long
  /// its own header is. False when the file is not an ANLZ file, is cut short, or has
  /// no such section.
  /// </summary>
  internal static bool TryFindSection(ReadOnlySpan<byte> file, ReadOnlySpan<byte> tag, out ReadOnlySpan<byte> section, out int headerBytes) {
    section = default;
    headerBytes = 0;
    if(file.Length < EnvelopeBytes || !file[..4].SequenceEqual("PMAI"u8)) {
      return false;
    }

    long at = BinaryPrimitives.ReadUInt32BigEndian(file[4..]);
    while(at + EnvelopeBytes <= file.Length) {
      ReadOnlySpan<byte> rest = file[(int)at..];
      long header = BinaryPrimitives.ReadUInt32BigEndian(rest[4..]);
      long length = BinaryPrimitives.ReadUInt32BigEndian(rest[8..]);
      if(length < EnvelopeBytes || header < EnvelopeBytes || header > length || length > rest.Length) {
        return false;
      }

      if(rest[..4].SequenceEqual(tag)) {
        section = rest[..(int)length];
        headerBytes = (int)header;
        return true;
      }

      at += length;
    }

    return false;
  }

  /// <summary>The file's bytes, or null when it cannot be read; a missing file is the common case, a track never analysed.</summary>
  internal static byte[]? TryReadAllBytes(string path) {
    try {
      return File.ReadAllBytes(path);
    } catch(Exception error) when(error is IOException or UnauthorizedAccessException) {
      return null;
    }
  }
}
