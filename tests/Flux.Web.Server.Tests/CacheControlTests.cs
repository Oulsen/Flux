using Microsoft.AspNetCore.Http;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// Pins what the page and its bundle say about caching. With nothing said, the
/// desktop window's WebView2 went on showing the previous build's page after a
/// rebuild, so the page must always be checked and only the hashed bundle kept.
/// </summary>
public sealed class CacheControlTests {
  [Theory]
  [InlineData("/")]
  [InlineData("/index.html")]
  [InlineData("/favicon.svg")]
  public void ThePageIsAlwaysCheckedWithTheServer(string path) {
    Assert.Equal("no-cache", WebHostExtensions.CacheControlFor(new PathString(path)));
  }

  [Theory]
  [InlineData("/assets/index-gEnPcxJS.js")]
  [InlineData("/assets/index-CtpQ1Wm9.css")]
  public void TheHashedBundleIsKeptForGood(string path) {
    Assert.Equal("public, max-age=31536000, immutable", WebHostExtensions.CacheControlFor(new PathString(path)));
  }

  /// <summary>A name that merely starts with the word is not the folder.</summary>
  [Fact]
  public void OnlyTheAssetsFolderCountsAsTheBundle() {
    Assert.Equal("no-cache", WebHostExtensions.CacheControlFor(new PathString("/assets-list.json")));
  }
}
