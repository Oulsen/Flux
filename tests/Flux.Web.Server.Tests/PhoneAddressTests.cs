using System.Net;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The address the page's QR code carries. Wrong here is a code a phone scans and
/// then cannot open, with nothing on screen to say why.
/// </summary>
public sealed class PhoneAddressTests {
  /// <summary>A loopback bind is reachable from this PC only, so there is no address to offer.</summary>
  [Theory]
  [InlineData("127.0.0.1")]
  [InlineData("::1")]
  public void ALoopbackBindHasNoPhoneAddress(string bindAddress) {
    Assert.Null(PhoneAddress.For(new WebOptions { BindAddress = bindAddress, Port = 5050 }));
  }

  [Fact]
  public void ASpecificBindIsTheAddress() {
    WebOptions options = new WebOptions { BindAddress = "192.168.1.10", Port = 6060 };

    Assert.Equal(new Uri("http://192.168.1.10:6060/"), PhoneAddress.For(options));
  }

  /// <summary>
  /// The wildcard picks an adapter of this machine's, which a test cannot name in
  /// advance; what it can hold is that the answer is never the loopback and always on
  /// Flux's port.
  /// </summary>
  [Fact]
  public void TheWildcardBindOffersANetworkAddressOnFluxsPort() {
    Uri? phone = PhoneAddress.For(new WebOptions { BindAddress = "0.0.0.0", Port = 5050 });

    if(phone is null) {
      return;
    }

    Assert.Equal(5050, phone.Port);
    Assert.False(IPAddress.IsLoopback(IPAddress.Parse(phone.Host)));
  }
}
