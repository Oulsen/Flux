using Microsoft.AspNetCore.Http;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// Pins the origin check, which is the socket's one security gate: any page open
/// in a browser on the LAN can ask for this socket, and this decides who gets it.
/// Every documented case is spelled out so a refactor cannot quietly widen or
/// narrow the gate.
/// </summary>
public sealed class OwnOriginTests {
  /// <summary>
  /// No Origin header means a native client, not a page - and a native client
  /// could forge the header anyway, so refusing it would protect nothing.
  /// </summary>
  [Fact]
  public void AllowsARequestWithNoOrigin() {
    Assert.True(WebHostExtensions.IsOwnOrigin(Request(origin: null)));
  }

  /// <summary>
  /// Loopback in every spelling is the dev machine itself: the Vite dev server
  /// is a second origin by design, on a port that changes.
  /// </summary>
  [Theory]
  [InlineData("http://localhost:5173")]
  [InlineData("http://LOCALHOST:5173")]
  [InlineData("http://127.0.0.1:5173")]
  [InlineData("http://127.0.0.1:64999")]
  [InlineData("http://[::1]:5173")]
  public void AllowsLoopbackOnAnyPort(string origin) {
    Assert.True(WebHostExtensions.IsOwnOrigin(Request(origin)));
  }

  /// <summary>The page Flux itself served, reached over the LAN.</summary>
  [Fact]
  public void AllowsThePageFluxServed() {
    Assert.True(WebHostExtensions.IsOwnOrigin(Request("http://192.168.1.10:5050", host: "192.168.1.10:5050")));
  }

  [Theory]
  [InlineData("https://evil.example")]
  [InlineData("http://192.168.1.99:5050")]
  [InlineData("http://localhost.evil.example:5050")]
  public void DeniesAForeignPage(string origin) {
    Assert.False(WebHostExtensions.IsOwnOrigin(Request(origin, host: "192.168.1.10:5050")));
  }

  /// <summary>
  /// An Origin that does not parse is a client doing something strange, and
  /// strange fails closed.
  /// </summary>
  [Theory]
  [InlineData("not a url")]
  [InlineData("null")]
  public void DeniesAnUnparseableOrigin(string origin) {
    Assert.False(WebHostExtensions.IsOwnOrigin(Request(origin)));
  }

  private static HttpRequest Request(string? origin, string host = "localhost:5050") {
    DefaultHttpContext context = new DefaultHttpContext();
    context.Request.Host = HostString.FromUriComponent(host);

    if(origin is not null) {
      context.Request.Headers.Origin = origin;
    }

    return context.Request;
  }
}
