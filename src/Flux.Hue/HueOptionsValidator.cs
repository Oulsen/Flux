using Microsoft.Extensions.Options;

namespace Flux.Hue;

/// <summary>
/// Checks the Hue settings before anything goes looking for a bridge.
///
/// These run at startup rather than at first use, because first use is a background
/// loop where a bad value is indistinguishable from a bridge that is switched off.
/// Every message names the offending key: the reader is somebody editing JSON, and
/// "one or more validation errors occurred" tells them nothing they can act on.
/// </summary>
internal sealed class HueOptionsValidator: IValidateOptions<HueOptions> {
  // The bridge's own guidance is 50 to 60 frames a second, and below about 10 the
  // stream stops being a stream: Flux would be sending steps that the bulb's own
  // fade smooths into something arriving visibly after the beat.
  private const int MinStreamHz = 10;
  private const int MaxStreamHz = 60;

  private static readonly TimeSpan MaxDiscoveryTimeout = TimeSpan.FromSeconds(30);

  public ValidateOptionsResult Validate(string? name, HueOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if(options.BridgeAddress is { } address && string.IsNullOrWhiteSpace(address)) {
      return ValidateOptionsResult.Fail(
        "Hue:BridgeAddress must be a bridge's address, or null to discover one. "
        + "An empty or whitespace value is not the same as absent.");
    }

    if(options.BridgeAddress is { Length: > 0 } host && Uri.CheckHostName(host) == UriHostNameType.Unknown) {
      return ValidateOptionsResult.Fail(
        $"Hue:BridgeAddress '{host}' is not a usable host name or IP address.");
    }

    if(options.DiscoveryTimeout <= TimeSpan.Zero || options.DiscoveryTimeout > MaxDiscoveryTimeout) {
      return ValidateOptionsResult.Fail(
        $"Hue:DiscoveryTimeout must be above zero and at most {MaxDiscoveryTimeout.TotalSeconds} seconds.");
    }

    if(options.PairingWindow <= TimeSpan.Zero) {
      return ValidateOptionsResult.Fail("Hue:PairingWindow must be above zero. It is how long the link button is waited for.");
    }

    if(options.PairingPollInterval <= TimeSpan.Zero || options.PairingPollInterval > options.PairingWindow) {
      return ValidateOptionsResult.Fail(
        "Hue:PairingPollInterval must be above zero and no longer than Hue:PairingWindow. "
        + "A poll slower than the window means the bridge is asked once and the countdown is decoration.");
    }

    if(options.StreamHz is < MinStreamHz or > MaxStreamHz) {
      return ValidateOptionsResult.Fail(
        $"Hue:StreamHz must be between {MinStreamHz} and {MaxStreamHz}. The bridge's own guidance is 50 to 60; "
        + "a bulb renders about 25 updates a second regardless, and raising this cannot change that.");
    }

    if(Math.Abs(options.LatencyOffsetMs) > HueOptions.MaxLatencyOffsetMs) {
      return ValidateOptionsResult.Fail(
        $"Hue:LatencyOffsetMs must be between -{HueOptions.MaxLatencyOffsetMs} and {HueOptions.MaxLatencyOffsetMs}. "
        + "Half a second is a whole beat at 120 BPM, so a larger value aims at a different beat.");
    }

    return ValidateOptionsResult.Success;
  }
}
