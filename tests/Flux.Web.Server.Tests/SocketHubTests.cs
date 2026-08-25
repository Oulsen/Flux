using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// What the hub does with a connection, observed from the socket's side.
/// </summary>
public sealed class SocketHubTests {
  [Fact]
  public async Task GreetsAClientBeforeItSendsAnythingElse() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);

    using JsonDocument hello = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());

    JsonElement data = hello.RootElement.GetProperty("data");
    Assert.Equal(Frames.Version, data.GetProperty("protocol").GetInt32());
    Assert.Equal(4, data.GetProperty("quantum").GetInt32());
    Assert.Equal(20, data.GetProperty("stateHz").GetInt32());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task AnswersAPingWithTheSameTimestamp() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.Client("""{"type":"ping","data":{"t":1755950400123}}""");

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());
    Assert.Equal(1755950400123, pong.RootElement.GetProperty("data").GetProperty("t").GetInt64());

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A pong echoing a timestamp the client never sent would read as a round trip
  /// of several decades, so a ping without one is refused rather than defaulted.
  /// </summary>
  [Fact]
  public async Task RefusesAPingWithNoTimestamp() {
    await AssertErrorAsync("""{"type":"ping","data":{}}""", "invalid_data");
  }

  [Fact]
  public async Task AnswersAnUnknownTypeRatherThanIgnoringIt() {
    await AssertErrorAsync("""{"type":"pnig","data":{}}""", "unknown_type");
  }

  [Fact]
  public async Task AnswersMalformedJson() {
    await AssertErrorAsync("{ this is not json", "malformed");
  }

  [Fact]
  public async Task AnswersAMessageWithNoType() {
    await AssertErrorAsync("""{"data":{"t":1}}""", "malformed");
  }

  /// <summary>
  /// A client cannot have its own input amplified back at it: the quoted type is
  /// capped well below the message it arrived in.
  /// </summary>
  [Fact]
  public async Task TruncatesALongTypeBeforeQuotingItBack() {
    // Built by concatenation rather than interpolated: a raw string cannot carry
    // the run of closing braces this JSON ends on without ambiguity.
    string enormous = new string('x', 3000);
    string message = "{\"type\":\"" + enormous + "\",\"data\":{}}";

    string error = await AssertErrorAsync(message, "unknown_type");

    Assert.True(error.Length < 200, $"The error quoted {error.Length} characters back at the client.");
  }

  /// <summary>
  /// The receive buffer is the documented maximum, so a message that outgrows it
  /// can never be read whole: the connection is closed with the status the RFC
  /// reserves for exactly this.
  /// </summary>
  [Fact]
  public async Task ClosesAMessageThatOutgrowsTheBuffer() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.Client(new string('x', Frames.MaxInboundBytes + 1));
    await serving;

    Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
  }

  /// <summary>
  /// A message at exactly the limit is within the protocol's promise and is
  /// still answered; one byte past it is the close above.
  /// </summary>
  [Fact]
  public async Task AcceptsAMessageExactlyAtTheLimit() {
    const string skeleton = """{"type":"ping","data":{"t":1},"pad":""}""";
    string message = skeleton.Replace("\"pad\":\"\"", $"\"pad\":\"{new string('x', Frames.MaxInboundBytes - skeleton.Length)}\"", StringComparison.Ordinal);
    Assert.Equal(Frames.MaxInboundBytes, message.Length);

    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.Client(message);

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A real socket delivers a message in as many fragments as it likes; the
  /// receive loop has to reassemble them before acting. Five bytes at a time is
  /// enough to force the path a whole-message fake never touches.
  /// </summary>
  [Fact]
  public async Task ReassemblesAFragmentedMessage() {
    FakeWebSocket socket = new FakeWebSocket { MaxChunk = 5 };
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.Client("""{"type":"ping","data":{"t":42}}""");

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());
    Assert.Equal(42, pong.RootElement.GetProperty("data").GetProperty("t").GetInt64());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task ClosesOnABinaryFrame() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.ClientBinary(1, 2, 3);
    await serving;

    Assert.Equal(WebSocketCloseStatus.InvalidMessageType, socket.CloseStatus);
  }

  [Fact]
  public async Task BroadcastsToEveryConnectedClient() {
    FakeWebSocket first = new FakeWebSocket();
    FakeWebSocket second = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task servingFirst = hub.ServeAsync(first, CancellationToken.None);
    Task servingSecond = hub.ServeAsync(second, CancellationToken.None);
    await first.NextAsync();
    await second.NextAsync();

    hub.Broadcast(Frames.Beat(new BeatData(7, 3, BarAligned: false, 128.0)));

    foreach(FakeWebSocket socket in new[] { first, second }) {
      using JsonDocument beat = JsonDocument.Parse(await socket.NextAsync());
      Assert.Equal("beat", beat.RootElement.GetProperty("type").GetString());
      Assert.Equal(7, beat.RootElement.GetProperty("data").GetProperty("index").GetInt64());
    }

    first.ClientClose();
    second.ClientClose();
    await Task.WhenAll(servingFirst, servingSecond);
  }

  /// <summary>
  /// The state pump asks this before it builds a snapshot, so it has to be false
  /// once the last browser has gone rather than merely eventually.
  /// </summary>
  [Fact]
  public async Task ForgetsAClientThatDisconnects() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Assert.False(hub.HasClients);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();
    Assert.True(hub.HasClients);

    socket.ClientClose();
    await serving;

    Assert.False(hub.HasClients);
  }

  /// <summary>
  /// Sends one client message and returns the prose of the error it produced.
  /// </summary>
  private static async Task<string> AssertErrorAsync(string message, string expectedCode) {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.Client(message);

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("error", error.RootElement.GetProperty("type").GetString());

    JsonElement data = error.RootElement.GetProperty("data");
    Assert.Equal(expectedCode, data.GetProperty("code").GetString());

    socket.ClientClose();
    await serving;

    return data.GetProperty("message").GetString() ?? string.Empty;
  }

  private static SocketHub NewHub() {
    return new SocketHub(
      Options.Create(new WebOptions()),
      Options.Create(new LinkOptions()),
      NullLogger<SocketHub>.Instance);
  }
}
