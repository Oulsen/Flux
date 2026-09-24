using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The address the desktop window loads. Wrong here is a window that opens on a blank
/// page while the phone works fine, which is the hardest way round to diagnose.
/// </summary>
public sealed class LocalPageUriTests {
  /// <summary>
  /// The wildcard bind is reached over the IPv4 loopback, not <c>localhost</c>, which
  /// Windows resolves to an IPv6 address nothing is listening on.
  /// </summary>
  [Fact]
  public void TheWildcardBindIsOpenedOnTheIPv4Loopback() {
    WebOptions options = new WebOptions { BindAddress = "0.0.0.0", Port = 5050 };

    Assert.Equal(new Uri("http://127.0.0.1:5050/"), options.LocalPageUri());
  }

  /// <summary>
  /// A bind to one interface listens there and nowhere else, so the loopback would
  /// find nothing: the bound address is the only one that works.
  /// </summary>
  [Theory]
  [InlineData("127.0.0.1", 6060, "http://127.0.0.1:6060/")]
  [InlineData("192.168.1.10", 5050, "http://192.168.1.10:5050/")]
  public void ASpecificBindIsOpenedOnTheAddressItBound(string bindAddress, int port, string expected) {
    WebOptions options = new WebOptions { BindAddress = bindAddress, Port = port };

    Assert.Equal(new Uri(expected), options.LocalPageUri());
  }
}
