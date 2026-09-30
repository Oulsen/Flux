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

  /// <summary>
  /// Whether <paramref name="path"/> is one to open: a full path on a drive of this
  /// computer. Every path read here comes out of rekordbox's memory, and a chain that
  /// has moved can land on any string at all; one naming a network share would have
  /// Windows connect to that host, and offer it the user's credentials, before the file
  /// turned out not to be there. A drive letter mapped to a share connects just the same,
  /// so a path on one is refused too.
  ///
  /// Only the plain form, a drive letter, a colon and a separator, is accepted. Every
  /// other fully qualified form is a device path - <c>\\?\</c>, <c>\\.\</c>, <c>\??\</c> -
  /// and each of them can name a share, <c>\??\UNC\host\share</c> among them, without
  /// starting with the two separators a share is otherwise known by.
  /// </summary>
  internal static bool IsLocal(string path) {
    ArgumentNullException.ThrowIfNull(path);
    bool onDrive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsSeparator(path[2]);
    return onDrive && !OnNetworkDrive(path);

    static bool IsSeparator(char c) => c is '\\' or '/';

    // Asking Windows what the drive letter is mapped to does not connect to the host
    // behind it.
    static bool OnNetworkDrive(string path) {
      try {
        return new DriveInfo(path[..1]).DriveType == DriveType.Network;
      } catch(ArgumentException) {
        return false;
      }
    }
  }

  /// <summary>Whether <paramref name="path"/> is <see cref="IsLocal"/> and names a <c>.DAT</c>, the file a deck's chain points at.</summary>
  internal static bool IsLocalDat(string path) {
    return IsLocal(path) && Path.GetExtension(path).Equals(".DAT", StringComparison.OrdinalIgnoreCase);
  }

  /// <summary>
  /// The file's bytes, or null when it cannot be read; a missing file is the common case,
  /// a track never analysed. A path that is not <see cref="IsLocal"/>, or a file larger
  /// than <see cref="MaxFileBytes"/>, is not read at all.
  /// </summary>
  internal static byte[]? TryReadAllBytes(string path) {
    if(!IsLocal(path)) {
      return null;
    }

    try {
      return new FileInfo(path).Length <= MaxFileBytes ? File.ReadAllBytes(path) : null;
    } catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException) {
      return null;
    }
  }

  /// <summary>
  /// The largest analysis file read. Flux's own number, reasoned: the largest in a
  /// rekordbox 7.2.19 library of 5,529 tracks, measured on 2026-09-30, is an <c>.EXT</c>
  /// of 6.7 MB, and about ten times that leaves room for a longer recording while still
  /// refusing a path that has landed on something else before it is read whole.
  /// </summary>
  internal const long MaxFileBytes = 64L * 1024 * 1024;
}
