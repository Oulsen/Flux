using System.Buffers.Binary;
using System.Text;

namespace Flux.Rekordbox.ChainFinder;

/// <summary>
/// The name of the audio file an ANLZ file analyses, from its <c>PPTH</c> section, so the
/// desk can see which track each deck holds rather than only its tempo. In rekordbox 7
/// the section holds <c>?/</c> and the file name, not a path or a title
/// (<c>docs/rekordbox-chains.md</c>); the file name is what is left once that and the
/// extension are taken off, and it is usually the track's own name.
/// </summary>
internal static class AnlzTrack {
  private const int LengthField = 12;

  /// <summary>The audio file's name without its extension, or null when the file has no readable <c>PPTH</c>.</summary>
  internal static string? Name(string anlzPath) =>
    AnlzFile.TryReadAllBytes(anlzPath) is { } file ? Name(file) : null;

  internal static string? Name(ReadOnlySpan<byte> file) {
    if(!AnlzFile.TryFindSection(file, "PPTH"u8, out ReadOnlySpan<byte> section, out int header) || header < LengthField + 4) {
      return null;
    }

    long length = BinaryPrimitives.ReadUInt32BigEndian(section[LengthField..]);
    if(length < 2 || header + length > section.Length) {
      return null;
    }

    string path = Encoding.BigEndianUnicode.GetString(section.Slice(header, (int)length)).TrimEnd('\0');
    string name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
    return name.Length > 0 ? name : null;
  }
}
