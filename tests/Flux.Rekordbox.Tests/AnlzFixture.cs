using System.Buffers.Binary;
using System.Text;

namespace Flux.Rekordbox.Tests;

/// <summary>
/// ANLZ files built byte by byte in the layout rekordbox 7.2.19 writes: a 28-byte
/// <c>PMAI</c> header, a <c>PPTH</c> section first as in every real file, then the tag
/// under test. Built rather than copied from a library, because a real file names
/// somebody's track and the tests need only its shape.
/// </summary>
internal static class AnlzFixture {
  private const int FileHeader = 28;

  /// <summary>One phrase entry as written: kind number and the three flag bytes, before any label is resolved.</summary>
  internal readonly record struct RawPhrase(int Beat, int Kind, byte K1 = 0, byte K2 = 0, byte K3 = 0, int? Fill = null);

  internal static byte[] Grid(params (int BeatInBar, float Bpm, int TimeMs)[] beats) {
    byte[] body = new byte[beats.Length * 8];
    for(int i = 0; i < beats.Length; i++) {
      Span<byte> entry = body.AsSpan(i * 8, 8);
      BinaryPrimitives.WriteUInt16BigEndian(entry, (ushort)beats[i].BeatInBar);
      BinaryPrimitives.WriteUInt16BigEndian(entry[2..], (ushort)MathF.Round(beats[i].Bpm * 100));
      BinaryPrimitives.WriteInt32BigEndian(entry[4..], beats[i].TimeMs);
    }

    return File(Section("PQTZ", header: 24, body, header => {
      BinaryPrimitives.WriteUInt32BigEndian(header[16..], 0x80000);
      BinaryPrimitives.WriteUInt32BigEndian(header[20..], (uint)beats.Length);
    }));
  }

  /// <summary>A steady grid of <paramref name="count"/> beats whose first beat is <paramref name="firstBeatInBar"/>.</summary>
  internal static byte[] SteadyGrid(float bpm, int count, int firstBeatInBar = 1) =>
    Grid([.. Enumerable.Range(0, count).Select(i => (((firstBeatInBar - 1 + i) % 4) + 1, bpm, (int)(i * 60_000 / bpm)))]);

  /// <summary>A phrase tag as rekordbox keeps it in its own library: not masked.</summary>
  internal static byte[] Phrases(int mood, int endBeat, params RawPhrase[] phrases) =>
    File(PhraseSection(mood, endBeat, phrases));

  /// <summary>
  /// The same tag as a USB export carries it: everything after the entry count XORed
  /// with the published pattern, each byte of it raised by the entry count. Written from
  /// Deep Symmetry's description independently of the reader, so the two agreeing is
  /// a check on the reader rather than on itself.
  /// </summary>
  internal static byte[] MaskedPhrases(int mood, int endBeat, params RawPhrase[] phrases) {
    byte[] section = PhraseSection(mood, endBeat, phrases);
    byte[] pattern = Convert.FromHexString("CBE1EEFAE5EEADEEE9D2E9EBE1E9F3E8E9F4E1");
    for(int i = 18; i < section.Length; i++) {
      section[i] ^= (byte)(pattern[(i - 18) % pattern.Length] + phrases.Length);
    }

    return File(section);
  }

  internal static byte[] PhraseSection(int mood, int endBeat, RawPhrase[] phrases) {
    byte[] body = new byte[phrases.Length * 24];
    for(int i = 0; i < phrases.Length; i++) {
      Span<byte> entry = body.AsSpan(i * 24, 24);
      RawPhrase phrase = phrases[i];
      BinaryPrimitives.WriteUInt16BigEndian(entry, (ushort)(i + 1));
      BinaryPrimitives.WriteUInt16BigEndian(entry[2..], (ushort)phrase.Beat);
      BinaryPrimitives.WriteUInt16BigEndian(entry[4..], (ushort)phrase.Kind);
      entry[7] = phrase.K1;
      entry[9] = phrase.K2;
      entry[19] = phrase.K3;
      if(phrase.Fill is { } fill) {
        entry[21] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(entry[22..], (ushort)fill);
      }
    }

    return Section("PSSI", header: 32, body, header => {
      BinaryPrimitives.WriteUInt32BigEndian(header[12..], 24);
      BinaryPrimitives.WriteUInt16BigEndian(header[16..], (ushort)phrases.Length);
      BinaryPrimitives.WriteUInt16BigEndian(header[18..], (ushort)mood);
      BinaryPrimitives.WriteUInt16BigEndian(header[26..], (ushort)endBeat);
    });
  }

  /// <summary>A file around the given sections, after the <c>PPTH</c> every real one opens with.</summary>
  internal static byte[] File(params byte[][] sections) {
    byte[] path = Encoding.BigEndianUnicode.GetBytes("?/track.mp3\0");
    byte[] ppth = Section("PPTH", header: 16, path, header => BinaryPrimitives.WriteUInt32BigEndian(header[12..], (uint)path.Length));
    byte[][] all = [ppth, .. sections];

    byte[] file = new byte[FileHeader + all.Sum(section => section.Length)];
    "PMAI"u8.CopyTo(file);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), FileHeader);
    BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)file.Length);
    int at = FileHeader;
    foreach(byte[] section in all) {
      section.CopyTo(file, at);
      at += section.Length;
    }

    return file;
  }

  private delegate void HeaderWriter(Span<byte> header);

  private static byte[] Section(string tag, int header, byte[] body, HeaderWriter fields) {
    byte[] section = new byte[header + body.Length];
    Encoding.ASCII.GetBytes(tag).CopyTo(section, 0);
    BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(4), (uint)header);
    BinaryPrimitives.WriteUInt32BigEndian(section.AsSpan(8), (uint)section.Length);
    fields(section.AsSpan(0, header));
    body.CopyTo(section, header);
    return section;
  }
}
