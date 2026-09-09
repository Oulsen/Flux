using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Hue.Tests;

public class HueOptionsValidatorTests {
  [Fact]
  public void AcceptsTheDefaults() {
    Assert.True(Validate(new HueOptions()).Succeeded);
  }

  [Fact]
  public void AcceptsANullBridgeAddressAsDiscoverOne() {
    // The intended path, and the reason the key exists at all is the case it cannot
    // cover. Null must never read as a mistake.
    Assert.True(Validate(new HueOptions { BridgeAddress = null }).Succeeded);
  }

  [Theory]
  [InlineData("192.168.1.42")]
  [InlineData("philips-hue.local")]
  public void AcceptsAnAddressABridgeCouldActuallyHave(string address) {
    Assert.True(Validate(new HueOptions { BridgeAddress = address }).Succeeded);
  }

  [Fact]
  public void RefusesABridgeAddressMadeOnlyOfSpaces() {
    // Not absent and not usable: discovery is skipped because a preference was
    // expressed, and then nothing can be resolved from it.
    ValidateOptionsResult result = Validate(new HueOptions { BridgeAddress = "   " });

    Assert.True(result.Failed);
    Assert.Contains("Hue:BridgeAddress", result.FailureMessage, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusesAnAddressThatIsNotAHost() {
    ValidateOptionsResult result = Validate(new HueOptions { BridgeAddress = "http://192.168.1.42/api" });

    Assert.True(result.Failed);
    Assert.Contains("Hue:BridgeAddress", result.FailureMessage, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(0)]
  [InlineData(9)]
  [InlineData(61)]
  public void RefusesAStreamRateTheBridgeCannotBeDrivenAt(int hz) {
    ValidateOptionsResult result = Validate(new HueOptions { StreamHz = hz });

    Assert.True(result.Failed);
    Assert.Contains("Hue:StreamHz", result.FailureMessage, StringComparison.Ordinal);
  }

  [Fact]
  public void AcceptsTheOfficialFiftyHertz() {
    Assert.True(Validate(new HueOptions { StreamHz = 50 }).Succeeded);
  }

  [Theory]
  [InlineData(-501)]
  [InlineData(501)]
  public void RefusesAnOffsetLargerThanABeat(int offsetMs) {
    // Half a second is a whole beat at 120 BPM, so beyond this the setting is no
    // longer compensating for the light path - it is aiming at a different beat.
    ValidateOptionsResult result = Validate(new HueOptions { LatencyOffsetMs = offsetMs });

    Assert.True(result.Failed);
    Assert.Contains("Hue:LatencyOffsetMs", result.FailureMessage, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(-120)]
  [InlineData(0)]
  [InlineData(140)]
  public void AcceptsAnOffsetTheLightPathCouldExplain(int offsetMs) {
    // The measured Hue path is 60 to 140 ms, in both directions of correction.
    Assert.True(Validate(new HueOptions { LatencyOffsetMs = offsetMs }).Succeeded);
  }

  [Fact]
  public void RefusesADiscoveryTimeoutOfZero() {
    ValidateOptionsResult result = Validate(new HueOptions { DiscoveryTimeout = TimeSpan.Zero });

    Assert.True(result.Failed);
    Assert.Contains("Hue:DiscoveryTimeout", result.FailureMessage, StringComparison.Ordinal);
  }

  [Fact]
  public void RefusesADiscoveryTimeoutNobodyWouldWaitOut() {
    ValidateOptionsResult result = Validate(new HueOptions { DiscoveryTimeout = TimeSpan.FromMinutes(2) });

    Assert.True(result.Failed);
    Assert.Contains("Hue:DiscoveryTimeout", result.FailureMessage, StringComparison.Ordinal);
  }

  private static ValidateOptionsResult Validate(HueOptions options) {
    return new HueOptionsValidator().Validate(Options.DefaultName, options);
  }
}
