using Microsoft.Extensions.Options;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Flux.Beat;

/// <summary>
/// Checks the Link settings before anything starts, so a bad one names itself.
///
/// The difference is not cosmetic. A misspelled multicast group otherwise reaches
/// <c>IPAddress.Parse</c> inside the listener, where the exception surfaces as a
/// stack trace naming neither the setting nor the value that broke it, and where
/// a value that cannot ever work is retried forever. Validated here, the same
/// typo stops the app with a sentence that says which key to fix.
/// </summary>
internal sealed class LinkOptionsValidator: IValidateOptions<LinkOptions> {
  public ValidateOptionsResult Validate(string? name, LinkOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    List<string> failures = new List<string>();

    if(!TryReadIPv4(options.MulticastGroup, out IPAddress? group)) {
      failures.Add($"Link:MulticastGroup must be an IPv4 address, not '{options.MulticastGroup}'.");
    } else if(group.GetAddressBytes()[0] is < 224 or > 239) {
      failures.Add($"Link:MulticastGroup must be in the multicast range 224.0.0.0-239.255.255.255, not '{options.MulticastGroup}'.");
    }

    if(!TryReadIPv4(options.BindAddress, out _)) {
      failures.Add($"Link:BindAddress must be an IPv4 address, not '{options.BindAddress}'.");
    }

    if(options.InterfaceAddress is { Length: > 0 } requested && !TryReadIPv4(requested, out _)) {
      failures.Add($"Link:InterfaceAddress must be an IPv4 address or empty, not '{requested}'.");
    }

    if(options.Port is < 1 or > 65535) {
      failures.Add($"Link:Port must be between 1 and 65535, not {options.Port}.");
    }

    if(options.Quantum < 1) {
      failures.Add($"Link:Quantum must be at least 1 beat per bar, not {options.Quantum}.");
    }

    if(options.StaleAfter <= TimeSpan.Zero) {
      failures.Add($"Link:StaleAfter must be greater than zero, not {options.StaleAfter}.");
    }

    // A non-finite offset would otherwise reach the decoder, where it turns every
    // timeline anchor into a rejected packet and reads as a dead Link session.
    if(!double.IsFinite(options.ClockOffsetMicros)) {
      failures.Add($"Link:ClockOffsetMicros must be a finite number, not {options.ClockOffsetMicros}.");
    }

    return failures.Count == 0
      ? ValidateOptionsResult.Success
      : ValidateOptionsResult.Fail(failures);
  }

  private static bool TryReadIPv4(string? value, [NotNullWhen(true)] out IPAddress? address) {
    return IPAddress.TryParse(value, out address)
      && address.AddressFamily == AddressFamily.InterNetwork;
  }
}
