using Xunit;

namespace Flux.Rekordbox.Tests;

/// <summary>
/// Which paths out of rekordbox's memory are opened at all. The path is whatever string a
/// chain lands on, so a share or an outsized file has to be refused before it is touched.
/// </summary>
public class AnlzFileTests {
  [Theory]
  [InlineData("C:/rb/USBANLZ/a/ANLZ0000.DAT", true)]
  [InlineData(@"D:\PIONEER\USBANLZ\a\ANLZ0000.DAT", true)]
  [InlineData(@"\\host\share\ANLZ0000.DAT", false)]
  [InlineData("//host/share/ANLZ0000.DAT", false)]
  [InlineData(@"\/host\share\ANLZ0000.DAT", false)]
  [InlineData(@"\\?\UNC\host\share\ANLZ0000.DAT", false)]
  [InlineData(@"\\?\C:\rb\ANLZ0000.DAT", false)]
  [InlineData(@"\??\UNC\host\share\ANLZ0000.DAT", false)]
  [InlineData(@"\??\C:\rb\ANLZ0000.DAT", false)]
  [InlineData(@"\\.\UNC\host\share\ANLZ0000.DAT", false)]
  [InlineData(@"1:\rb\ANLZ0000.DAT", false)]
  [InlineData("USBANLZ/a/ANLZ0000.DAT", false)]
  [InlineData("C:ANLZ0000.DAT", false)]
  [InlineData(@"\USBANLZ\ANLZ0000.DAT", false)]
  [InlineData("", false)]
  public void OpensOnlyAFullPathOnThisComputer(string path, bool local) {
    Assert.Equal(local, AnlzFile.IsLocal(path));
  }

  /// <summary>Skipped on a computer with no drive letter mapped to a share, which is most of them.</summary>
  [Fact]
  public void OpensNothingOnADriveLetterMappedToAShare() {
    DriveInfo? mapped = DriveInfo.GetDrives().FirstOrDefault(drive => drive.DriveType == DriveType.Network);
    if(mapped is null) {
      Assert.Skip("no drive letter here is mapped to a share");
    }

    Assert.False(AnlzFile.IsLocal(Path.Combine(mapped.RootDirectory.FullName, "PIONEER", "ANLZ0000.DAT")));
  }

  [Theory]
  [InlineData("C:/rb/USBANLZ/a/ANLZ0000.DAT", true)]
  [InlineData("C:/rb/USBANLZ/a/anlz0000.dat", true)]
  [InlineData("C:/rb/USBANLZ/a/ANLZ0000.EXT", false)]
  [InlineData("C:/rb/USBANLZ/a/ANLZ0000", false)]
  [InlineData(@"\\host\share\ANLZ0000.DAT", false)]
  public void TakesADecksPathOnlyWhenItIsALocalDat(string path, bool dat) {
    Assert.Equal(dat, AnlzFile.IsLocalDat(path));
  }

  [Fact]
  public void ReadsNothingFromAShare() {
    Assert.Null(AnlzFile.TryReadAllBytes(@"\\host.invalid\share\ANLZ0000.DAT"));
  }

  [Fact]
  public void ReadsAFileUpToTheLimitAndNothingPastIt() {
    string path = Path.Combine(Path.GetTempPath(), $"flux-anlz-{Guid.NewGuid():N}.DAT");
    try {
      File.WriteAllBytes(path, "PMAI"u8.ToArray());
      Assert.Equal("PMAI"u8.ToArray(), AnlzFile.TryReadAllBytes(path));

      using(FileStream file = File.OpenWrite(path)) {
        file.SetLength(AnlzFile.MaxFileBytes + 1);
      }

      Assert.Null(AnlzFile.TryReadAllBytes(path));
    } finally {
      File.Delete(path);
    }
  }
}
