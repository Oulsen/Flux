using System.Buffers.Binary;

namespace Flux.Rekordbox;

/// <summary>
/// Which set of phrase labels rekordbox chose for a track. The same kind number means
/// different phrases under different moods, so a phrase is not readable without it.
/// </summary>
internal enum PhraseMood {
  High = 1,
  Mid = 2,
  Low = 3,
}

internal enum PhraseKind {
  Intro,
  Verse,
  Bridge,
  Up,
  Down,
  Chorus,
  Outro,
}

/// <summary>
/// One phrase: the grid beat it starts on (1-based, as rekordbox counts), what it is,
/// and the numbered variant rekordbox shows - the 2 in "Verse 2" or "Up 2" - with 0 for
/// a phrase shown without a number. <see cref="FillBeat"/> is where a fill at its end
/// begins, when rekordbox found one.
/// </summary>
internal readonly record struct AnlzPhrase(int StartBeat, PhraseKind Kind, int Variant, int? FillBeat);

/// <summary>A track's phrases, and the beat the last one ends on.</summary>
internal sealed record AnlzPhraseList(PhraseMood Mood, int EndBeat, IReadOnlyList<AnlzPhrase> Phrases);

/// <summary>
/// The phrases in a track's <c>.EXT</c> analysis file (<c>PSSI</c>), for the light engine
/// to change look on a phrase rather than on a count of bars.
///
/// The layout is from Deep Symmetry's published notes, which also describe an XOR mask
/// rekordbox 6 and later put over everything after the entry count. rekordbox's own
/// library copies turned out not to be masked - every one of them read plain - but a
/// track loaded from a USB export reads its analysis from the export, which is, so both
/// are accepted. Which one a tag is gets decided by its content, not by where the file
/// sits: the phrases of a real tag are numbered 1, 2, 3 and the mask cannot leave that
/// standing.
///
/// Labels are resolved here because they depend on the mood and, under the high mood,
/// on three flag bytes as well. A kind number or flag combination outside the published
/// tables makes the whole tag unreadable rather than labelled by guess: none turned up
/// in the library this was checked against, so one that does is a rekordbox that has
/// changed what the numbers mean. Start beats index the grid as rekordbox wrote them,
/// and on a few tracks they count twice the beats the grid holds and run past its end;
/// whether a phrase still fits its grid is for the sync source to decide.
/// </summary>
internal static class AnlzPhrases {
  private const int HeaderBytes = 32;
  private const int EntryBytes = 24;

  /// <summary>Everything from here on is under the mask, when there is one.</summary>
  private const int MaskedFrom = 18;

  private static readonly byte[] MaskPattern = [
    0xCB, 0xE1, 0xEE, 0xFA, 0xE5, 0xEE, 0xAD, 0xEE, 0xE9, 0xD2,
    0xE9, 0xEB, 0xE1, 0xE9, 0xF3, 0xE8, 0xE9, 0xF4, 0xE1,
  ];

  /// <summary>The phrases in the <c>.EXT</c> file at <paramref name="path"/>, or null when there are none to read.</summary>
  internal static AnlzPhraseList? Read(string path) =>
    AnlzFile.TryReadAllBytes(path) is { } file ? Read(file) : null;

  /// <summary>
  /// The phrases, or null when the file has no phrase tag, has one without phrases, or
  /// has one that reads as phrases neither plain nor unmasked.
  /// </summary>
  internal static AnlzPhraseList? Read(ReadOnlySpan<byte> file) {
    if(!AnlzFile.TryFindSection(file, "PSSI"u8, out ReadOnlySpan<byte> section, out int header)
       || header < HeaderBytes
       || BinaryPrimitives.ReadUInt32BigEndian(section[12..]) != EntryBytes) {
      return null;
    }

    int count = BinaryPrimitives.ReadUInt16BigEndian(section[16..]);
    if(count == 0 || header + (count * EntryBytes) > section.Length) {
      return null;
    }

    if(NumbersRunFromOne(section, header, count)) {
      return Parse(section, header, count);
    }

    byte[] unmasked = section.ToArray();
    for(int i = MaskedFrom; i < unmasked.Length; i++) {
      unmasked[i] ^= (byte)(MaskPattern[(i - MaskedFrom) % MaskPattern.Length] + count);
    }

    return NumbersRunFromOne(unmasked, header, count) ? Parse(unmasked, header, count) : null;
  }

  private static bool NumbersRunFromOne(ReadOnlySpan<byte> section, int header, int count) {
    for(int i = 0; i < count; i++) {
      if(BinaryPrimitives.ReadUInt16BigEndian(section[(header + (i * EntryBytes))..]) != i + 1) {
        return false;
      }
    }

    return true;
  }

  private static AnlzPhraseList? Parse(ReadOnlySpan<byte> section, int header, int count) {
    int moodValue = BinaryPrimitives.ReadUInt16BigEndian(section[18..]);
    if(moodValue is < (int)PhraseMood.High or > (int)PhraseMood.Low) {
      return null;
    }

    var mood = (PhraseMood)moodValue;
    int endBeat = BinaryPrimitives.ReadUInt16BigEndian(section[26..]);
    var phrases = new AnlzPhrase[count];
    for(int i = 0; i < count; i++) {
      ReadOnlySpan<byte> entry = section.Slice(header + (i * EntryBytes), EntryBytes);
      int start = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
      if(start < 1 || (i > 0 && start <= phrases[i - 1].StartBeat)) {
        return null;
      }

      if(Label(mood, BinaryPrimitives.ReadUInt16BigEndian(entry[4..]), k1: entry[7], k2: entry[9], k3: entry[19]) is not { } label) {
        return null;
      }

      int? fill = entry[21] != 0 ? BinaryPrimitives.ReadUInt16BigEndian(entry[22..]) : null;
      phrases[i] = new AnlzPhrase(start, label.Kind, label.Variant, fill);
    }

    return endBeat < phrases[^1].StartBeat ? null : new AnlzPhraseList(mood, endBeat, phrases);
  }

  /// <summary>
  /// The label rekordbox shows, from the published tables. Under the mid and low moods
  /// the kind number alone decides; the low mood has three numbers each for "Verse 1"
  /// and "Verse 2" that rekordbox does not tell apart. Under the high mood the number
  /// of an intro, chorus or outro is in the first flag and an up's in the other two.
  /// </summary>
  private static (PhraseKind Kind, int Variant)? Label(PhraseMood mood, int kind, byte k1, byte k2, byte k3) {
    return mood switch {
      PhraseMood.High => High(),
      PhraseMood.Mid => kind switch {
        1 => (PhraseKind.Intro, 0),
        >= 2 and <= 7 => (PhraseKind.Verse, kind - 1),
        8 => (PhraseKind.Bridge, 0),
        9 => (PhraseKind.Chorus, 0),
        10 => (PhraseKind.Outro, 0),
        _ => null,
      },
      _ => kind switch {
        1 => (PhraseKind.Intro, 0),
        >= 2 and <= 4 => (PhraseKind.Verse, 1),
        >= 5 and <= 7 => (PhraseKind.Verse, 2),
        8 => (PhraseKind.Bridge, 0),
        9 => (PhraseKind.Chorus, 0),
        10 => (PhraseKind.Outro, 0),
        _ => null,
      },
    };

    (PhraseKind, int)? High() {
      if(k1 > 1 || k2 > 1 || k3 > 1) {
        return null;
      }

      int firstOrSecond = k1 == 1 ? 1 : 2;
      return (kind, k2, k3) switch {
        (1, _, _) => (PhraseKind.Intro, firstOrSecond),
        (2, 0, 0) => (PhraseKind.Up, 1),
        (2, 0, 1) => (PhraseKind.Up, 2),
        (2, 1, 0) => (PhraseKind.Up, 3),
        (3, _, _) => (PhraseKind.Down, 0),
        (5, _, _) => (PhraseKind.Chorus, firstOrSecond),
        (6, _, _) => (PhraseKind.Outro, firstOrSecond),
        _ => null,
      };
    }
  }
}
