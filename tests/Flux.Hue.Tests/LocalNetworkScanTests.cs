using System.Net;
using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// Which addresses the scan asks, and what counts as a bridge's answer. The asking
/// itself needs a network with a bridge on it and is checked against one by hand.
/// </summary>
public sealed class LocalNetworkScanTests {
  [Fact]
  public void AsksTheWholeSlash24ButItself() {
    IReadOnlyList<IPAddress> candidates = LocalNetworkScan.Candidates([IPAddress.Parse("192.168.1.163")]);

    Assert.Equal(253, candidates.Count);
    Assert.Contains(IPAddress.Parse("192.168.1.1"), candidates);
    Assert.Contains(IPAddress.Parse("192.168.1.254"), candidates);
    Assert.DoesNotContain(IPAddress.Parse("192.168.1.163"), candidates);
    Assert.DoesNotContain(IPAddress.Parse("192.168.1.0"), candidates);
    Assert.DoesNotContain(IPAddress.Parse("192.168.1.255"), candidates);
  }

  /// <summary>Two adapters on one network are one network to ask, and neither is asked.</summary>
  [Fact]
  public void AsksASharedNetworkOnce() {
    IReadOnlyList<IPAddress> candidates = LocalNetworkScan.Candidates(
      [IPAddress.Parse("192.168.1.163"), IPAddress.Parse("192.168.1.20")]);

    Assert.Equal(252, candidates.Count);
    Assert.DoesNotContain(IPAddress.Parse("192.168.1.20"), candidates);
  }

  /// <summary>
  /// An adapter with no network, the loopback and IPv6 are nothing to sweep: an APIPA
  /// address is exactly the disconnected adapter a desk machine tends to have.
  /// </summary>
  [Fact]
  public void SkipsLinkLocalLoopbackAndIpv6() {
    IReadOnlyList<IPAddress> candidates = LocalNetworkScan.Candidates([
      IPAddress.Parse("169.254.12.7"),
      IPAddress.Loopback,
      IPAddress.Parse("fe80::1"),
    ]);

    Assert.Empty(candidates);
  }

  [Fact]
  public void ReadsTheIdOfABridgePro() {
    // What a Hue Bridge Pro answered on a real network, trimmed.
    const string json = """{"name":"Hue Bridge Pro","apiversion":"1.78.0","bridgeid":"FC268CFFFE10B729","modelid":"BSB003"}""";

    Assert.Equal("FC268CFFFE10B729", LocalNetworkScan.ParseBridgeId(json));
  }

  [Theory]
  [InlineData("<html>router login</html>")]
  [InlineData("""{"name":"some other device"}""")]
  [InlineData("""{"bridgeid":""}""")]
  [InlineData("""{"bridgeid":42}""")]
  [InlineData("""["bridgeid"]""")]
  public void RefusesAnythingThatIsNotABridge(string answer) {
    Assert.Null(LocalNetworkScan.ParseBridgeId(answer));
  }
}
