using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Audio.Tests;

public class AudioOptionsValidatorTests {
  [Theory]
  [InlineData("DDJ-FLX10")]
  [InlineData("")]
  public void AcceptsAPreferenceAndTheAbsenceOfOne(string preference) {
    Assert.True(Validate(new AudioOptions { PreferDeviceNamed = preference }).Succeeded);
  }

  [Fact]
  public void RejectsAPreferenceMadeOnlyOfSpaces() {
    // Not pedantry. It is not empty, so it counts as a real preference, and nearly
    // every endpoint name contains a space - so it silently selects whichever
    // device Windows enumerated first, which is very likely a render endpoint that
    // will be silent all night.
    ValidateOptionsResult result = Validate(new AudioOptions { PreferDeviceNamed = "  " });

    Assert.True(result.Failed);
    Assert.NotNull(result.FailureMessage);
    Assert.Contains("Audio:PreferDeviceNamed", result.FailureMessage, StringComparison.Ordinal);
  }

  [Fact]
  public void RejectsABlankDeviceIdButAcceptsNone() {
    Assert.True(Validate(new AudioOptions { DeviceId = null }).Succeeded);
    Assert.True(Validate(new AudioOptions { DeviceId = " " }).Failed);
  }

  private static ValidateOptionsResult Validate(AudioOptions options) =>
    new AudioOptionsValidator().Validate(Options.DefaultName, options);
}
