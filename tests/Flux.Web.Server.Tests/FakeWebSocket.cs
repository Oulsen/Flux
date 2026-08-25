using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Flux.Web.Server.Tests;

/// <summary>
/// A WebSocket with no network under it: messages are pushed in from a test and
/// everything the server sends is captured.
///
/// Worth the fifty lines because the alternative is not testing the hub at all.
/// Its two interesting behaviours - that a greeting arrives before any broadcast,
/// and that exactly one loop ever writes to the socket - are properties of the
/// orchestration rather than of any function, so they can only be observed from
/// the socket's side.
/// </summary>
internal sealed class FakeWebSocket: WebSocket {
  private readonly Channel<Frame> inbound = Channel.CreateUnbounded<Frame>();
  private readonly Channel<string> outbound = Channel.CreateUnbounded<string>();

  private WebSocketCloseStatus? closeStatus;
  private string? closeDescription;
  private WebSocketState state = WebSocketState.Open;

  private Frame? receiving;
  private int delivered;

  /// <summary>
  /// The most bytes one ReceiveAsync call hands over. A real socket fragments
  /// messages at the network's whim; lowering this makes a test do it on
  /// purpose, so reassembly is exercised rather than assumed.
  /// </summary>
  public int MaxChunk { get; init; } = int.MaxValue;

  public override WebSocketCloseStatus? CloseStatus => this.closeStatus;

  public override string? CloseStatusDescription => this.closeDescription;

  public override WebSocketState State => this.state;

  public override string? SubProtocol => null;

  /// <summary>Queues a text message as if a browser had sent it.</summary>
  public void Client(string json) {
    this.inbound.Writer.TryWrite(new Frame(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text));
  }

  /// <summary>Queues a binary message, which the protocol does not allow.</summary>
  public void ClientBinary(params byte[] payload) {
    this.inbound.Writer.TryWrite(new Frame(payload, WebSocketMessageType.Binary));
  }

  /// <summary>Queues a close, as a browser tab does when it goes away.</summary>
  public void ClientClose() {
    this.inbound.Writer.TryWrite(new Frame([], WebSocketMessageType.Close));
  }

  /// <summary>Waits for the next frame the server sends, or fails the test.</summary>
  public async Task<string> NextAsync() {
    using CancellationTokenSource giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    return await this.outbound.Reader.ReadAsync(giveUp.Token).ConfigureAwait(false);
  }

  /// <summary>
  /// Hands over at most the caller's buffer (and at most <see cref="MaxChunk"/>)
  /// per call, reporting end-of-message only with a frame's final byte - the
  /// contract a real socket honours and a naive fake silently doesn't. The
  /// server's too-big close depends on it: a message that outgrows the buffer is
  /// only ever seen as full-buffer reads that never end.
  /// </summary>
  public override async Task<WebSocketReceiveResult> ReceiveAsync(
    ArraySegment<byte> buffer,
    CancellationToken cancellationToken) {
    if(this.receiving is null) {
      Frame frame = await this.inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

      if(frame.Type == WebSocketMessageType.Close) {
        this.state = WebSocketState.CloseReceived;
        return new WebSocketReceiveResult(
          0,
          WebSocketMessageType.Close,
          endOfMessage: true,
          WebSocketCloseStatus.NormalClosure,
          "Client closed.");
      }

      this.receiving = frame;
      this.delivered = 0;
    }

    Frame current = this.receiving.Value;
    int count = Math.Min(Math.Min(buffer.Count, this.MaxChunk), current.Payload.Length - this.delivered);

    current.Payload.AsSpan(this.delivered, count).CopyTo(buffer.AsSpan());
    this.delivered += count;

    bool endOfMessage = this.delivered == current.Payload.Length;
    if(endOfMessage) {
      this.receiving = null;
    }

    return new WebSocketReceiveResult(count, current.Type, endOfMessage);
  }

  public override Task SendAsync(
    ArraySegment<byte> buffer,
    WebSocketMessageType messageType,
    bool endOfMessage,
    CancellationToken cancellationToken) {
    this.outbound.Writer.TryWrite(Encoding.UTF8.GetString(buffer));
    return Task.CompletedTask;
  }

  public override Task CloseAsync(
    WebSocketCloseStatus closeStatus,
    string? statusDescription,
    CancellationToken cancellationToken) {
    this.closeStatus = closeStatus;
    this.closeDescription = statusDescription;
    this.state = WebSocketState.Closed;

    // Ends the server's receive loop the way a real closed socket would.
    this.inbound.Writer.TryComplete();
    return Task.CompletedTask;
  }

  public override Task CloseOutputAsync(
    WebSocketCloseStatus closeStatus,
    string? statusDescription,
    CancellationToken cancellationToken) {
    return this.CloseAsync(closeStatus, statusDescription, cancellationToken);
  }

  public override void Abort() {
    this.state = WebSocketState.Aborted;
    this.inbound.Writer.TryComplete();
  }

  public override void Dispose() {
    this.inbound.Writer.TryComplete();
    this.outbound.Writer.TryComplete();
  }

  private readonly record struct Frame(byte[] Payload, WebSocketMessageType Type);
}
