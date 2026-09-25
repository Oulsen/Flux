using System.Buffers.Binary;
using Xunit;

namespace Flux.Rekordbox.Tests;

public class AnlzLibraryTests {
  private static readonly string Library = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pioneer", "rekordbox", "share", "PIONEER", "USBANLZ");

  [Fact]
  public void ReadsEveryGridAndPhraseTagInTheLocalLibrary() {
    // The fixtures are built from the published layout, so they can only show the
    // reader agrees with the notes. This holds it against what rekordbox actually
    // wrote: every analysed track's grid reads, and so does every phrase tag. Skips
    // without a rekordbox library, which is every CI run; it is somebody's music.
    if(!Directory.Exists(Library)) {
      Assert.Skip("No local rekordbox library.");
    }

    string[] dats = Directory.GetFiles(Library, "ANLZ0000.DAT", SearchOption.AllDirectories);
    if(dats.Length == 0) {
      Assert.Skip("The local rekordbox library has no analysed tracks.");
    }

    var unreadGrids = new List<string>();
    var unreadPhrases = new List<string>();
    int phrases = 0;
    int onDownbeat = 0;
    foreach(string dat in dats) {
      byte[] file = File.ReadAllBytes(dat);
      IReadOnlyList<AnlzBeat>? grid = AnlzBeatGrid.Read(file);
      if(grid is null && !HasEmptyGrid(file)) {
        unreadGrids.Add(dat);
      }

      string ext = AnlzFile.ExtendedPath(dat);
      if(!File.Exists(ext)) {
        continue;
      }

      byte[] extended = File.ReadAllBytes(ext);
      AnlzPhraseList? list = AnlzPhrases.Read(extended);
      if(list is null) {
        if(AnlzFile.TryFindSection(extended, "PSSI"u8, out _, out _)) {
          unreadPhrases.Add(ext);
        }

        continue;
      }

      foreach(AnlzPhrase phrase in list.Phrases.Where(phrase => grid is not null && phrase.StartBeat <= grid.Count)) {
        phrases++;
        onDownbeat += grid![phrase.StartBeat - 1].BeatInBar == 1 ? 1 : 0;
      }
    }

    Assert.Empty(unreadGrids);
    Assert.Empty(unreadPhrases);

    // Start beats count the grid from 1: counted that way, 92% of phrases across 5,500
    // tracks start on beat 1 of a bar. Counted from 0, each lands on the beat after its
    // real start and 2% do, so a share anywhere near that means the count is off by one.
    Assert.InRange(onDownbeat / (double)Math.Max(phrases, 1), 0.8, 1.0);
  }

  /// <summary>A grid section with no beats: a track rekordbox has not analysed, which reads as no grid by design.</summary>
  private static bool HasEmptyGrid(byte[] file) =>
    AnlzFile.TryFindSection(file, "PQTZ"u8, out ReadOnlySpan<byte> section, out _)
    && section.Length >= 24
    && BinaryPrimitives.ReadUInt32BigEndian(section[20..]) == 0;
}
