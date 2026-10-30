using Flux.Web.Server.Protocol;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The <c>update</c> frame's wire names, which a page branches on one by one.
/// </summary>
public sealed class UpdateFramesTests {
  [Theory]
  [InlineData(UpdateStatus.Unavailable, "unavailable")]
  [InlineData(UpdateStatus.Checking, "checking")]
  [InlineData(UpdateStatus.Current, "current")]
  [InlineData(UpdateStatus.Ready, "ready")]
  [InlineData(UpdateStatus.Failed, "failed")]
  public void SpellsEachStatusAsTheProtocolDocumentDoes(UpdateStatus status, string wire) {
    Assert.Equal(wire, UpdateFrames.Compose(new UpdateState(status, "0.7.0")).Status);
  }

  /// <summary>
  /// A version beside any status but ready would read as "this is waiting", which only
  /// ready means.
  /// </summary>
  [Fact]
  public void CarriesAVersionOnlyWhenReady() {
    Assert.Equal("0.7.0", UpdateFrames.Compose(new UpdateState(UpdateStatus.Ready, "0.7.0")).Version);
    Assert.Null(UpdateFrames.Compose(new UpdateState(UpdateStatus.Current, "0.7.0")).Version);
  }
}
