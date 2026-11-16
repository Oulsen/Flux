using Flux.Beat;
using System.Net;

namespace Flux.Web.Server;

/// <summary>
/// The address a phone on this network opens the page on, for the page to show as a
/// QR code.
///
/// One address rather than the list the startup log prints, because a QR code is one
/// address: a developer machine carries Hyper-V, WSL and VPN adapters beside the Wi-Fi
/// card, and a code that led to one of those would be a code that does nothing. The
/// adapter is picked on the rule the Link listener already uses for the same question -
/// a default gateway, then Ethernet or Wi-Fi - so the two never disagree about which
/// network this PC is on.
/// </summary>
public static class PhoneAddress {
  /// <summary>
  /// Null when no other device can reach the page: a loopback bind, or no usable IPv4
  /// interface. A specific bind address is the whole answer, as it is for the log.
  /// </summary>
  public static Uri? For(WebOptions options) {
    ArgumentNullException.ThrowIfNull(options);

    if(!IPAddress.TryParse(options.BindAddress, out IPAddress? bound) || IPAddress.IsLoopback(bound)) {
      return null;
    }

    IPAddress? address = bound.Equals(IPAddress.Any)
      ? MulticastInterfaces.Preferred(MulticastInterfaces.Candidates())?.Address
      : bound;

    return address is null ? null : new UriBuilder(Uri.UriSchemeHttp, address.ToString(), options.Port, "/").Uri;
  }
}
