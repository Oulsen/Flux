using Flux.Web.Server.Protocol;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// Guards the wire shape against <c>docs/protocol.md</c>.
///
/// These assert on parsed JSON rather than on the C# records deliberately. The
/// records are one side of a contract whose other side is a TypeScript file in a
/// different project, and what those two must agree on is the bytes: a rename
/// that a C#-only test would sail through is exactly the change that breaks the
/// page.
/// </summary>
public sealed class WireFormatTests {
  [Fact]
  public void HelloCarriesTheProtocolVersionAndTheBuild() {
    JsonElement data = Payload(Frames.Hello("0.1.0", quantum: 4, stateHz: 20), "hello");

    Assert.Equal(Frames.Version, data.GetProperty("protocol").GetInt32());
    Assert.Equal("0.1.0", data.GetProperty("app").GetString());
    Assert.Equal(4, data.GetProperty("quantum").GetInt32());
    Assert.Equal(20, data.GetProperty("stateHz").GetInt32());
  }

  [Fact]
  public void StateNamesEveryFieldTheDocumentPromises() {
    StateData state = new StateData(
      "link",
      "live",
      new GridData(128.5, 1024.25, 1024, 1, BarAligned: false),
      new DiagnosticsData(4821, 0.0));

    JsonElement data = Payload(Frames.State(state), "state");

    Assert.Equal("link", data.GetProperty("source").GetString());
    Assert.Equal("live", data.GetProperty("sourceState").GetString());

    JsonElement grid = data.GetProperty("grid");
    Assert.Equal(128.5, grid.GetProperty("bpm").GetDouble());
    Assert.Equal(1024.25, grid.GetProperty("beat").GetDouble());
    Assert.Equal(1024, grid.GetProperty("beatIndex").GetInt64());
    Assert.Equal(1, grid.GetProperty("positionInBar").GetInt32());
    Assert.False(grid.GetProperty("barAligned").GetBoolean());

    JsonElement diagnostics = data.GetProperty("diagnostics");
    Assert.Equal(4821, diagnostics.GetProperty("packets").GetInt64());
    Assert.Equal(0.0, diagnostics.GetProperty("reanchorMaxBeats").GetDouble());
  }

  /// <summary>
  /// The null is load-bearing. An absent tempo sent as zero renders as a
  /// confident 0.00 BPM, which is the one reading a beat display must never
  /// invent.
  /// </summary>
  [Fact]
  public void WaitingStateSendsANullGridRatherThanZeroes() {
    StateData state = new StateData("link", "waiting", Grid: null, new DiagnosticsData(0, 0));

    JsonElement data = Payload(Frames.State(state), "state");

    Assert.Equal(JsonValueKind.Null, data.GetProperty("grid").ValueKind);
  }

  [Fact]
  public void BeatCarriesTheIndexAndTheTempo() {
    JsonElement data = Payload(Frames.Beat(new BeatData(1024, 3, BarAligned: false, 128.0)), "beat");

    Assert.Equal(1024, data.GetProperty("index").GetInt64());
    Assert.Equal(3, data.GetProperty("positionInBar").GetInt32());
    Assert.False(data.GetProperty("barAligned").GetBoolean());
    Assert.Equal(128.0, data.GetProperty("bpm").GetDouble());
  }

  [Fact]
  public void PongEchoesTheClientTimestampUnchanged() {
    JsonElement data = Payload(Frames.Pong(1755950400123), "pong");

    Assert.Equal(1755950400123, data.GetProperty("t").GetInt64());
  }

  [Fact]
  public void ErrorCarriesACodeAndProse() {
    JsonElement data = Payload(Frames.Error("unknown_type", "Unknown message type 'x'."), "error");

    Assert.Equal("unknown_type", data.GetProperty("code").GetString());
    Assert.Equal("Unknown message type 'x'.", data.GetProperty("message").GetString());
  }

  /// <summary>
  /// Reads a frame, checks its type tag, and hands back the payload.
  /// </summary>
  private static JsonElement Payload(ReadOnlyMemory<byte> frame, string expectedType) {
    using JsonDocument document = JsonDocument.Parse(frame);
    JsonElement root = document.RootElement;

    Assert.Equal(expectedType, root.GetProperty("type").GetString());

    // Cloned because the document is disposed on the way out of this method.
    return root.GetProperty("data").Clone();
  }
}
