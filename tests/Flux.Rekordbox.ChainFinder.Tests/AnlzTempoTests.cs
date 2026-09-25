using Xunit;

namespace Flux.Rekordbox.ChainFinder.Tests;

public class AnlzTempoTests {
  [Fact]
  public void ReadsTheBeatgridTempoPastTheOtherSections() {
    // The layout checked against rekordbox 7.2.19's own files: PQTZ after PPTH, a
    // 24-byte section header, tempo in hundredths.
    Assert.Equal(new[] { 174.0f }, AnlzTempo.Read(AnlzFiles.Build(174.0f, beats: 16)));
  }

  [Fact]
  public void KeepsFractionalTempos() {
    Assert.Equal(new[] { 176.03f }, AnlzTempo.Read(AnlzFiles.Build(176.03f, beats: 4)));
  }

  [Fact]
  public void RefusesAFileThatIsNotAnAnlzFile() {
    Assert.Null(AnlzTempo.Read("RIFF....WAVEfmt "u8));
  }

  [Fact]
  public void RefusesAFileCutShortInsideASection() {
    // A section claiming more bytes than the file holds is a broken or foreign file,
    // and reading on would take tempos from whatever follows.
    byte[] file = AnlzFiles.Build(174.0f, beats: 16);

    Assert.Null(AnlzTempo.Read(file.AsSpan(0, file.Length - 20)));
  }

  [Fact]
  public void ChecksAFileOnDiskForADecksTempo() {
    using var files = new AnlzFiles();
    string path = files.Write("deck", 172.0f);

    Assert.True(AnlzTempo.Carries(path, 172.0f));
    Assert.False(AnlzTempo.Carries(path, 174.0f));
    Assert.False(AnlzTempo.Carries(path + ".missing", 172.0f));
  }
}
