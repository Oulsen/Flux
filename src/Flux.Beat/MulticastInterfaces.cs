using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Flux.Beat;

/// <summary>A local IPv4 interface the Link group could be joined on.</summary>
/// <param name="Address">The unicast address to pass to <c>AddMembership</c>.</param>
/// <param name="Name">Adapter description, for the log line that explains the choice.</param>
/// <param name="HasGateway">Whether the adapter has an IPv4 default gateway.</param>
/// <param name="Type">Ethernet and Wi-Fi outrank everything else.</param>
public sealed record NetworkCandidate(IPAddress Address, string Name, bool HasGateway, NetworkInterfaceType Type) {
  /// <summary>
  /// Higher is better. A default gateway is the strongest signal available that
  /// an adapter carries real LAN traffic, which is what separates the Wi-Fi card
  /// from the pile of Hyper-V, VPN and loopback adapters a developer machine
  /// accumulates.
  /// </summary>
  public int Rank => (this.HasGateway ? 2 : 0)
    + (this.Type is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 1 : 0);
}

/// <summary>
/// Picks the single interface to join the Link multicast group on.
///
/// Choosing exactly one is not an optimisation. Phase 0 joined on two and
/// received every ALIVE datagram twice, which read as a ~123 ms packet interval
/// instead of Link's ~250 ms - so any rate derived from arrivals would have been
/// wrong by the number of interfaces present.
/// </summary>
public static class MulticastInterfaces {
  /// <summary>Every interface that could plausibly carry the Link session.</summary>
  public static IReadOnlyList<NetworkCandidate> Candidates() {
    List<NetworkCandidate> candidates = new List<NetworkCandidate>();

    foreach(NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces()) {
      if(adapter.OperationalStatus != OperationalStatus.Up
        || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback
        || !adapter.SupportsMulticast) {
        continue;
      }

      IPInterfaceProperties properties = adapter.GetIPProperties();
      bool hasGateway = properties.GatewayAddresses
        .Any(gateway => gateway.Address.AddressFamily == AddressFamily.InterNetwork
          && !gateway.Address.Equals(IPAddress.Any));

      foreach(UnicastIPAddressInformation unicast in properties.UnicastAddresses) {
        if(unicast.Address.AddressFamily == AddressFamily.InterNetwork) {
          candidates.Add(new NetworkCandidate(unicast.Address, adapter.Description, hasGateway, adapter.NetworkInterfaceType));
        }
      }
    }

    return candidates;
  }

  /// <summary>
  /// The best candidate, or null when the machine has no usable IPv4 interface.
  /// </summary>
  public static NetworkCandidate? Preferred(IReadOnlyList<NetworkCandidate> candidates) {
    ArgumentNullException.ThrowIfNull(candidates);

    return candidates.OrderByDescending(candidate => candidate.Rank).FirstOrDefault();
  }
}
