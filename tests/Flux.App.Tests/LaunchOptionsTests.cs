using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// The one flag the process reads for itself. What matters is that it never reaches
/// the configuration, where a bare <c>--console</c> would take the next argument as
/// its value and quietly lose a setting.
/// </summary>
public sealed class LaunchOptionsTests {
  [Fact]
  public void NoFlagMeansNoConsoleAndEveryArgumentGoesToTheHost() {
    LaunchOptions launch = LaunchOptions.Parse(["--Web:Port", "6060"]);

    Assert.False(launch.Console);
    Assert.Equal(["--Web:Port", "6060"], launch.HostArguments);
  }

  [Theory]
  [InlineData("--console")]
  [InlineData("--CONSOLE")]
  public void TheFlagAsksForAConsoleWhateverItsCase(string flag) {
    Assert.True(LaunchOptions.Parse([flag]).Console);
  }

  /// <summary>
  /// Taken out wherever it sits, so the pair after it stays a pair: left in, the host
  /// would read <c>--console --Web:Port</c> as a key with the other key as its value.
  /// </summary>
  [Fact]
  public void TheFlagIsTakenOutAndTheSettingsAroundItSurvive() {
    LaunchOptions launch = LaunchOptions.Parse(["--console", "--Web:Port", "6060"]);

    Assert.True(launch.Console);
    Assert.Equal(["--Web:Port", "6060"], launch.HostArguments);
  }
}
