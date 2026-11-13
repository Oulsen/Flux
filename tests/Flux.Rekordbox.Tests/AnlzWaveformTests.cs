using Xunit;

namespace Flux.Rekordbox.Tests;

public class AnlzWaveformTests {
  [Fact]
  public void ReadsEachColumnAsLowMidHighFromTheMidHighLowOnDisk() {
    byte[] file = AnlzFixture.Waveform([(Mid: 10, High: 20, Low: 30), (Mid: 1, High: 2, Low: 127)]);

    AnlzWaveform waveform = AnlzWaveformReader.Read(file)!;

    Assert.Equal(150, waveform.EntriesPerSecond);
    Assert.Equal(2, waveform.Count);
    Assert.Equal([30, 10, 20, 127, 1, 2], waveform.LowMidHigh);
  }

  [Theory]
  [InlineData(4, 150)]
  [InlineData(3, 75)]
  public void RefusesALayoutOtherThanTheOneChecked(int entryBytes, int rate) {
    byte[] file = AnlzFixture.Waveform([(Mid: 1, High: 1, Low: 1), (Mid: 1, High: 1, Low: 1)], entryBytes, rate);

    Assert.Null(AnlzWaveformReader.Read(file));
  }

  [Fact]
  public void RefusesAHeightNoFileInTheLibraryReached() {
    byte[] file = AnlzFixture.Waveform([(Mid: 1, High: 128, Low: 1)]);

    Assert.Null(AnlzWaveformReader.Read(file));
  }

  [Fact]
  public void HasNoWaveformInAFileWithoutTheTag() {
    Assert.Null(AnlzWaveformReader.Read(AnlzFixture.SteadyGrid(120f, 4)));
  }

  [Fact]
  public void HasNoWaveformWhenTheTagIsEmpty() {
    Assert.Null(AnlzWaveformReader.Read(AnlzFixture.Waveform([])));
  }

  [Fact]
  public void ReadsEachColourColumnAsHeightThenRedGreenBlue() {
    // Red 5, green 2, blue 7, height 19: 101 010 111 10011 00.
    const ushort word = (5 << 13) | (2 << 10) | (7 << 7) | (19 << 2);

    AnlzColorWaveform waveform = AnlzColorWaveformReader.Read(AnlzFixture.ColorWaveform([word, 0]))!;

    Assert.Equal(150, waveform.EntriesPerSecond);
    Assert.Equal([19, 5, 2, 7, 0, 0, 0, 0], waveform.HeightRgb);
  }

  [Theory]
  [InlineData(3, 150)]
  [InlineData(2, 75)]
  public void RefusesAColourLayoutOtherThanTheOneChecked(int entryBytes, int rate) {
    Assert.Null(AnlzColorWaveformReader.Read(AnlzFixture.ColorWaveform([4, 4], entryBytes, rate)));
  }

  [Fact]
  public void RefusesAColourWordWithTheUnusedBitsSet() {
    Assert.Null(AnlzColorWaveformReader.Read(AnlzFixture.ColorWaveform([0b1])));
  }

  [Fact]
  public void NamesTheSecondExtendedFileBesideTheDat() {
    Assert.Equal("C:/rb/a/ANLZ0000.2EX", AnlzFile.ThreeBandPath("C:/rb/a/ANLZ0000.DAT"));
  }
}
