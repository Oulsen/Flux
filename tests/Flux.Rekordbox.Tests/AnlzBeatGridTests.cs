using Xunit;

namespace Flux.Rekordbox.Tests;

public class AnlzBeatGridTests {
  [Fact]
  public void ReadsEachBeatsBarPositionTempoAndTimePastTheOtherSections() {
    byte[] file = AnlzFixture.Grid((1, 174.0f, 12), (2, 174.0f, 357), (3, 174.03f, 702));

    Assert.Equal(
      [new AnlzBeat(1, 174.0f, 12), new AnlzBeat(2, 174.0f, 357), new AnlzBeat(3, 174.03f, 702)],
      AnlzBeatGrid.Read(file));
  }

  [Fact]
  public void KeepsAGridThatDoesNotStartOnTheDownbeat() {
    // About one track in five: the reason this source can anchor bars at all is that
    // the grid says where beat 1 is instead of assuming it is the first beat.
    IReadOnlyList<AnlzBeat> grid = AnlzBeatGrid.Read(AnlzFixture.SteadyGrid(128.0f, 8, firstBeatInBar: 3))!;

    Assert.Equal([3, 4, 1, 2, 3, 4, 1, 2], grid.Select(beat => beat.BeatInBar));
  }

  [Fact]
  public void ReadsAFirstBeatJustBeforeTheAudioAsANegativeTime() {
    // Seen in the library as FF FF FF FF: -1 ms, not 49.7 days.
    IReadOnlyList<AnlzBeat> grid = AnlzBeatGrid.Read(AnlzFixture.Grid((1, 175.0f, -1), (2, 175.0f, 342)))!;

    Assert.Equal(-1, grid[0].TimeMs);
  }

  [Fact]
  public void KeepsBeatsPiledOnTheTracksLastMillisecond() {
    byte[] file = AnlzFixture.Grid((4, 127.0f, 421591), (1, 127.0f, 421930), (2, 127.0f, 421930));

    Assert.Equal(3, AnlzBeatGrid.Read(file)!.Count);
  }

  [Fact]
  public void RefusesTimeRunningBackwards() {
    Assert.Null(AnlzBeatGrid.Read(AnlzFixture.Grid((1, 120.0f, 500), (2, 120.0f, 499))));
  }

  [Theory]
  [InlineData(0)]
  [InlineData(5)]
  public void RefusesABeatOutsideTheBar(int beatInBar) {
    Assert.Null(AnlzBeatGrid.Read(AnlzFixture.Grid((1, 120.0f, 0), (beatInBar, 120.0f, 500))));
  }

  [Fact]
  public void HasNoGridForATrackNotYetAnalysed() {
    Assert.Null(AnlzBeatGrid.Read(AnlzFixture.Grid()));
    Assert.Null(AnlzBeatGrid.Read(AnlzFixture.File()));
  }

  [Fact]
  public void RefusesAFileThatIsNotAnAnlzFile() {
    Assert.Null(AnlzBeatGrid.Read("RIFF....WAVEfmt "u8));
  }

  [Fact]
  public void RefusesAFileCutShortInsideTheGrid() {
    byte[] file = AnlzFixture.SteadyGrid(174.0f, 16);

    Assert.Null(AnlzBeatGrid.Read(file.AsSpan(0, file.Length - 20)));
  }

  [Fact]
  public void RefusesACountTheSectionCannotHold() {
    byte[] file = AnlzFixture.SteadyGrid(174.0f, 4);
    int countAt = file.Length - (4 * 8) - 4;
    file[countAt + 3] = 5;

    Assert.Null(AnlzBeatGrid.Read(file));
  }

  [Fact]
  public void FindsTheExtendedFileBesideTheOneADeckNames() {
    Assert.Equal(
      "C:/Users/dj/AppData/Roaming/Pioneer/rekordbox/share/PIONEER/USBANLZ/000/abc/ANLZ0000.EXT",
      AnlzFile.ExtendedPath("C:/Users/dj/AppData/Roaming/Pioneer/rekordbox/share/PIONEER/USBANLZ/000/abc/ANLZ0000.DAT"));
  }
}
