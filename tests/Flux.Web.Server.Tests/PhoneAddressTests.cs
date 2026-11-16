using Flux.Beat;
using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The address the page's QR code carries. Wrong here is a code a phone scans and
/// then cannot open, with nothing on screen to say why.
/// </summary>
public sealed class PhoneAddressTests {
  private static readonly WebOptions Wildcard = new WebOptions { BindAddress = "0.0.0.0", Port = 5050 };

  private static readonly NetworkCandidate WiFi = Adapter("192.168.1.163", hasGateway: true, NetworkInterfaceType.Wireless80211);
  private static readonly NetworkCandidate Cable = Adapter("192.168.1.20", hasGateway: true, NetworkInterfaceType.Ethernet);
  private static readonly NetworkCandidate Wsl = Adapter("172.24.208.1", hasGateway: false, NetworkInterfaceType.Ethernet);
  private static readonly NetworkCandidate Vpn = Adapter("10.8.0.2", hasGateway: true, NetworkInterfaceType.Unknown);

  [Fact]
  public void ALoopbackBindHasNoPhoneAddress() {
    WebOptions loopback = new WebOptions { BindAddress = "127.0.0.1", Port = 5050 };

    Assert.Null(PhoneAddress.Choose(loopback, linkInterface: null, [WiFi]));
  }

  [Fact]
  public void ASpecificBindIsTheAddress() {
    WebOptions bound = new WebOptions { BindAddress = "192.168.1.10", Port = 6060 };

    Assert.Equal(new Uri("http://192.168.1.10:6060/"), PhoneAddress.Choose(bound, linkInterface: null, [WiFi]));
  }

  [Fact]
  public void TheWildcardOffersTheAdapterWithAGatewayOverTheVirtualOnes() {
    Assert.Equal(new Uri("http://192.168.1.163:5050/"), PhoneAddress.Choose(Wildcard, linkInterface: null, [Wsl, Vpn, WiFi]));
  }

  /// <summary>
  /// A laptop off its router with WSL installed: the WSL adapter reports itself as
  /// Ethernet and would outrank anything left, and no phone can reach it.
  /// </summary>
  [Fact]
  public void WithNoAdapterThatHasAGatewayThereIsNoCode() {
    Assert.Null(PhoneAddress.Choose(Wildcard, linkInterface: null, [Wsl]));
  }

  /// <summary>An adapter DHCP never answered gets a 169.254 address, gateway or not.</summary>
  [Fact]
  public void AnAutoconfiguredAddressIsNeverOffered() {
    NetworkCandidate leftover = Adapter("169.254.12.7", hasGateway: true, NetworkInterfaceType.Wireless80211);

    Assert.Equal(new Uri("http://192.168.1.163:5050/"), PhoneAddress.Choose(Wildcard, linkInterface: null, [leftover, WiFi]));
    Assert.Null(PhoneAddress.Choose(Wildcard, linkInterface: null, [leftover]));
  }

  [Fact]
  public void APinnedLinkAdapterIsTheAnswerWhenItQualifies() {
    Assert.Equal(new Uri("http://192.168.1.20:5050/"), PhoneAddress.Choose(Wildcard, linkInterface: "192.168.1.20", [WiFi, Cable]));
  }

  /// <summary>Pinned for Link to a cable with no gateway, which is a Link choice and not a phone's network.</summary>
  [Fact]
  public void APinnedLinkAdapterNoPhoneCanReachIsPassedOver() {
    Assert.Equal(new Uri("http://192.168.1.163:5050/"), PhoneAddress.Choose(Wildcard, linkInterface: "172.24.208.1", [Wsl, WiFi]));
  }

  private static NetworkCandidate Adapter(string address, bool hasGateway, NetworkInterfaceType type) {
    return new NetworkCandidate(IPAddress.Parse(address), type.ToString(), hasGateway, type);
  }
}
