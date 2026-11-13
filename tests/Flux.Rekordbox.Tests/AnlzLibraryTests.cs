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

  [Fact]
  public void ReadsEveryWaveformInTheLocalLibraryAtTheLengthOfItsTrack() {
    // Skips without a library, as above.
    if(!Directory.Exists(Library)) {
      Assert.Skip("No local rekordbox library.");
    }

    string[] dats = Directory.GetFiles(Library, "ANLZ0000.DAT", SearchOption.AllDirectories);
    var unread = new List<string>();
    var misfit = new List<string>();
    foreach(string dat in dats) {
      string threeBand = AnlzFile.ThreeBandPath(dat);
      if(!File.Exists(threeBand)) {
        continue;
      }

      byte[] file = File.ReadAllBytes(threeBand);
      if(AnlzWaveformReader.Read(file) is not { } waveform) {
        if(AnlzFile.TryFindSection(file, "PWV7"u8, out _, out _)) {
          unread.Add(threeBand);
        }

        continue;
      }

      // 150 columns a second of track: a grid's last beat inside the waveform, give or
      // take the beats rekordbox piles on the final millisecond, is the check that the
      // rate is what the header says.
      if(AnlzBeatGrid.Read(File.ReadAllBytes(dat)) is { Count: > 0 } grid) {
        double seconds = waveform.Count / (double)waveform.EntriesPerSecond;
        if(grid[^1].TimeMs / 1000.0 > seconds + 1) {
          misfit.Add(threeBand);
        }
      }
    }

    Assert.Empty(unread);
    Assert.Empty(misfit);
  }

  [Fact]
  public void ReadsEveryColourWaveformInTheLocalLibrary() {
    // Skips without a library, as above.
    if(!Directory.Exists(Library)) {
      Assert.Skip("No local rekordbox library.");
    }

    var unread = new List<string>();
    foreach(string ext in Directory.GetFiles(Library, "ANLZ0000.EXT", SearchOption.AllDirectories)) {
      byte[] file = File.ReadAllBytes(ext);
      if(AnlzColorWaveformReader.Read(file) is null && AnlzFile.TryFindSection(file, "PWV5"u8, out _, out _)) {
        unread.Add(ext);
      }
    }

    Assert.Empty(unread);
  }

  /// <summary>A grid section with no beats: a track rekordbox has not analysed, which reads as no grid by design.</summary>
  private static bool HasEmptyGrid(byte[] file) =>
    AnlzFile.TryFindSection(file, "PQTZ"u8, out ReadOnlySpan<byte> section, out _)
    && section.Length >= 24
    && BinaryPrimitives.ReadUInt32BigEndian(section[20..]) == 0;
}
