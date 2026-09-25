using System.Buffers.Binary;
using System.Text;

namespace Flux.Rekordbox.ChainFinder.Tests;

/// <summary>
/// ANLZ files on disk for the tests, in the layout <see cref="AnlzTempo"/> reads: a
/// <c>PMAI</c> file header, a <c>PPTH</c> section, and a <c>PQTZ</c> beatgrid at one
/// tempo, all big-endian. Paths use forward slashes, as rekordbox keeps them in memory.
/// </summary>
internal sealed class AnlzFiles: IDisposable {
  private const int FileHeader = 28;

  private readonly string root = Path.Combine(Path.GetTempPath(), $"flux-anlz-{Guid.NewGuid():N}");

  /// <summary>A file whose beatgrid is <paramref name="beats"/> beats at <paramref name="bpm"/>.</summary>
  internal string Write(string name, float bpm, int beats = 16) {
    string directory = Path.Combine(this.root, name);
    Directory.CreateDirectory(directory);
    string path = Path.Combine(directory, "ANLZ0000.DAT").Replace('\\', '/');
    File.WriteAllBytes(path, Build(bpm, beats));
    return path;
  }

  internal static byte[] Build(float bpm, int beats) {
    byte[] path = Encoding.BigEndianUnicode.GetBytes("?/track.mp3\0");
    byte[] ppth = Section("PPTH", header: 16, body: path, fields: [(uint)path.Length]);

    byte[] grid = new byte[beats * 8];
    for(int beat = 0; beat < beats; beat++) {
      Span<byte> entry = grid.AsSpan(beat * 8, 8);
      BinaryPrimitives.WriteUInt16BigEndian(entry, (ushort)((beat % 4) + 1));
      BinaryPrimitives.WriteUInt16BigEndian(entry[2..], (ushort)MathF.Round(bpm * 100));
      BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)(beat * 60_000 / bpm));
    }

    byte[] pqtz = Section("PQTZ", header: 24, body: grid, fields: [0, 0x80000, (uint)beats]);

    byte[] file = new byte[FileHeader + ppth.Length + pqtz.Length];
    "PMAI"u8.CopyTo(file);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), FileHeader);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)file.Length);
    ppth.CopyTo(file, FileHeader);
    pqtz.CopyTo(file, FileHeader + ppth.Length);
    return file;
  }

  public void Dispose() {
    try {
      Directory.Delete(this.root, recursive: true);
    } catch(IOException) {
    }
  }

  /// <summary>A section: tag, header length, total length, then the header's own fields, then the body.</summary>
  private static byte[] Section(string tag, int header, byte[] body, uint[] fields) {
    byte[] section = new byte[header + body.Length];
    Encoding.ASCII.GetBytes(tag).CopyTo(section, 0);
    BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(4), (uint)header);
    BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(8), (uint)section.Length);
    for(int i = 0; i < fields.Length; i++) {
      BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(12 + (i * 4)), fields[i]);
    }

    body.CopyTo(section, header);
    return section;
  }
}
