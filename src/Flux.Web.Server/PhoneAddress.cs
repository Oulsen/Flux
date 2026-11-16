using Flux.Beat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.NetworkInformation;

namespace Flux.Web.Server;

/// <summary>
/// The address a phone on this network opens the page on, for the page to show as a
/// QR code.
///
/// One address rather than the list the startup log prints, because a QR code is one
/// address and a code that leads nowhere is worse than none: a developer machine
/// carries Hyper-V, WSL and VPN adapters beside the Wi-Fi card, and Windows hands an
/// adapter whose DHCP has not answered a 169.254 address. So only an adapter with a
/// default gateway is offered - the strongest sign available that it is the network a
/// phone is on - and with none of those there is no code. The log answers a different
/// question, every address Flux can be reached on, and keeps its own list for that.
///
/// Where somebody has pinned the Link adapter with <c>Link:InterfaceAddress</c>, that
/// adapter is the answer here too when it qualifies, so a machine whose automatic choice
/// was wrong for Link is not left with the same wrong choice in the code.
///
/// Worked out once and again whenever Windows reports an address change, rather than per
/// connection, so a page connecting costs no adapter walk.
/// </summary>
public sealed class PhoneAddress: IDisposable {
  private readonly WebOptions web;
  private readonly string? linkInterface;
  private readonly ILogger<PhoneAddress> log;
  private Uri? current;

  public PhoneAddress(IOptions<WebOptions> web, IOptions<LinkOptions> link, ILogger<PhoneAddress> log) {
    ArgumentNullException.ThrowIfNull(web);
    ArgumentNullException.ThrowIfNull(link);

    this.web = web.Value;
    this.linkInterface = link.Value.InterfaceAddress;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    this.Refresh();
    NetworkChange.NetworkAddressChanged += this.OnAddressChanged;
  }

  /// <summary>The address as of the last network change, or null when no phone can reach the page.</summary>
  public Uri? Current => Volatile.Read(ref this.current);

  public void Dispose() {
    NetworkChange.NetworkAddressChanged -= this.OnAddressChanged;
  }

  /// <summary>
  /// The choice itself, apart from the adapter walk so a test can hand it adapters.
  ///
  /// Null for a loopback bind or one that does not parse, and for a wildcard bind with
  /// no adapter that qualifies. A specific bind address is the whole answer, as it is
  /// for the log.
  /// </summary>
  public static Uri? Choose(WebOptions web, string? linkInterface, IReadOnlyList<NetworkCandidate> candidates) {
    ArgumentNullException.ThrowIfNull(web);
    ArgumentNullException.ThrowIfNull(candidates);

    if(!IPAddress.TryParse(web.BindAddress, out IPAddress? bound) || IPAddress.IsLoopback(bound)) {
      return null;
    }

    IPAddress? address = bound.Equals(IPAddress.Any) ? Best(linkInterface, candidates) : bound;

    return address is null ? null : new UriBuilder(Uri.UriSchemeHttp, address.ToString(), web.Port, "/").Uri;
  }

  private static IPAddress? Best(string? linkInterface, IReadOnlyList<NetworkCandidate> candidates) {
    NetworkCandidate[] usable = [.. candidates.Where(candidate => candidate.HasGateway && !IsLinkLocal(candidate.Address))];

    if(linkInterface is { Length: > 0 }
      && usable.FirstOrDefault(candidate => candidate.Address.ToString() == linkInterface) is { } pinned) {
      return pinned.Address;
    }

    return MulticastInterfaces.Preferred(usable)?.Address;
  }

  /// <summary>169.254.0.0/16: what Windows gives an adapter no DHCP server answered.</summary>
  private static bool IsLinkLocal(IPAddress address) {
    byte[] bytes = address.GetAddressBytes();
    return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
  }

  private void OnAddressChanged(object? sender, EventArgs e) {
    this.Refresh();
  }

  private void Refresh() {
    Uri? found;
    try {
      found = Choose(this.web, this.linkInterface, MulticastInterfaces.Candidates());
    } catch(NetworkInformationException e) {
      // An adapter that cannot be read mid-change costs the code and nothing else: the
      // greeting carries this, and a greeting that threw would take every page with it.
      this.log.LogWarning(e, "Could not read the network adapters for the phone address.");
      found = null;
    }

    Volatile.Write(ref this.current, found);
  }
}
