using Microsoft.Extensions.Logging;
using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// The log file. The retention rule gets the most attention, because it is the part
/// that deletes files, and a rule that matched too much would delete something that
/// was not Flux's to delete.
/// </summary>
public sealed class FileLoggerProviderTests {
  private static readonly DateOnly Today = new DateOnly(2026, 9, 24);

  [Fact]
  public void AFileIsNamedByItsDay() {
    Assert.Equal("flux-20260924.log", FileLoggerProvider.FileName(Today));
  }

  /// <summary>Fourteen days back is still kept; fifteen is the first to go.</summary>
  [Theory]
  [InlineData("flux-20260924.log", false)]
  [InlineData("flux-20260910.log", false)]
  [InlineData("flux-20260909.log", true)]
  [InlineData("flux-20250101.log", true)]
  public void OnlyLogsPastTheRetentionAreExpired(string name, bool expired) {
    Assert.Equal(expired, FileLoggerProvider.Expired([name], Today).Contains(name));
  }

  /// <summary>
  /// Anything that is not exactly one of Flux's own names is left alone, however old
  /// the date inside it looks.
  /// </summary>
  [Theory]
  [InlineData("flux-20200101.log.bak")]
  [InlineData("flux-2020010.log")]
  [InlineData("flux-notadate.log")]
  [InlineData("other-20200101.log")]
  [InlineData("flux-20201301.log")]
  [InlineData("notes.txt")]
  public void NothingButAFluxLogIsEverExpired(string name) {
    Assert.Empty(FileLoggerProvider.Expired([name], Today));
  }

  /// <summary>
  /// A line logged is a line on disk once the provider is disposed, which is the order
  /// the host shuts down in.
  /// </summary>
  [Fact]
  public void ALoggedLineReachesTheFileAndOldLogsAreCleared() {
    string folder = Path.Combine(Path.GetTempPath(), "flux-log-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);

    string stale = Path.Combine(folder, "flux-20250101.log");
    string foreign = Path.Combine(folder, "notes.txt");
    File.WriteAllText(stale, "old");
    File.WriteAllText(foreign, "keep");

    try {
      string path;

      using(FileLoggerProvider provider = new FileLoggerProvider(folder, Today.ToDateTime(new TimeOnly(21, 0)))) {
        path = provider.Path;
        provider.CreateLogger("Flux.Test").LogWarning("The bridge went quiet after {Seconds} s.", 3);
      }

      string written = File.ReadAllText(path);

      Assert.Equal(Path.Combine(folder, "flux-20260924.log"), path);
      Assert.Contains("warn Flux.Test: The bridge went quiet after 3 s.", written, StringComparison.Ordinal);
      Assert.False(File.Exists(stale), "A log past its retention is deleted at startup.");
      Assert.True(File.Exists(foreign), "A file that is not a Flux log is left alone.");
    } finally {
      Directory.Delete(folder, recursive: true);
    }
  }
}
