using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// Which addresses the window keeps and which it sends to the browser. The window has
/// no address bar, so an address let through by mistake is a page that can only be
/// left by quitting.
/// </summary>
public sealed class PageOriginTests {
  private static readonly Uri Page = new Uri("http://127.0.0.1:5050/");

  [Theory]
  [InlineData("http://127.0.0.1:5050/")]
  [InlineData("http://127.0.0.1:5050/some/path?query=1#part")]
  [InlineData("HTTP://127.0.0.1:5050/")]
  public void TheWindowKeepsItsOwnPage(string address) {
    Assert.True(MainWindow.IsSameOrigin(Page, address));
  }

  [Theory]
  [InlineData("https://example.com/")]
  [InlineData("http://127.0.0.1:5051/")]
  [InlineData("https://127.0.0.1:5050/")]
  [InlineData("http://localhost:5050/")]
  [InlineData("http://127.0.0.1.example.com:5050/")]
  [InlineData("file:///C:/Windows/win.ini")]
  [InlineData("about:blank")]
  [InlineData("not an address")]
  [InlineData("")]
  public void AnythingElseIsSentAway(string address) {
    Assert.False(MainWindow.IsSameOrigin(Page, address));
  }
}
