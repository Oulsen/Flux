using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;

namespace Flux.Web.Server;

/// <summary>
/// Every connected browser, and the one place frames are written to them.
///
/// The rule the whole class exists to enforce: <b>nothing that produces a frame
/// ever waits for a client to receive it.</b> Beats are produced on the
/// scheduler thread, and a phone on bad Wi-Fi that could stall that thread would
/// stall the light show along with it. So a broadcast is a non-blocking write
/// into one bounded queue per client, and the socket is only ever touched by
/// that client's own send loop.
///
/// Two consequences worth stating, because they are design and not oversight. A
/// slow client loses its oldest queued messages rather than slowing anyone down.
/// And because exactly one loop per connection writes to the socket, no send can
/// ever overlap another on the same socket, which <see cref="WebSocket"/> does
/// not permit and does not check. The close frame honours the same rule: a close
/// decided on the receive side is not written until the send loop has exited.
/// </summary>
public sealed class SocketHub {
  /// <summary>
  /// How long a closing connection may spend draining its queue before it is
  /// torn down without the parting close frame. A live LAN client drains in
  /// milliseconds; this exists so a stalled one cannot hold its slot open.
  /// </summary>
  private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

  private readonly ConcurrentDictionary<Guid, Client> clients = new ConcurrentDictionary<Guid, Client>();
  private readonly WebOptions options;
  private readonly ILogger<SocketHub> log;
  private readonly ReadOnlyMemory<byte> greeting;

  /// <summary>
  /// The quantum comes from the beat settings rather than from the web settings
  /// because it is a property of the music, not of the transport. It is also the
  /// one part of the beat that no source can supply: bar <em>position</em> gets
  /// anchored by a cue press or a tap, but bar <em>length</em> is only ever
  /// configuration, which is why switching sources cannot change this greeting.
  /// </summary>
  public SocketHub(IOptions<WebOptions> options, IOptions<BeatOptions> beat, ILogger<SocketHub> log) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(beat);

    this.options = options.Value;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    // Every field in the greeting is fixed for the lifetime of the process, so
    // it is encoded once here rather than per connection.
    this.greeting = Frames.Hello(FluxVersion.Current, beat.Value.Quantum, this.options.StateHz);
  }

  /// <summary>
  /// Whether anyone is listening.
  ///
  /// The state pump checks this before it builds a snapshot: with no browser
  /// open, serialising twenty frames a second into nowhere is the only wasted
  /// work in an otherwise idle process.
  /// </summary>
  public bool HasClients => !this.clients.IsEmpty;

  /// <summary>Sends one already-encoded frame to every connected client.</summary>
  public void Broadcast(ReadOnlyMemory<byte> frame) {
    foreach(Client client in this.clients.Values) {
      client.Enqueue(frame);
    }
  }

  /// <summary>
  /// Runs one connection until it closes, the client goes away, or the host
  /// stops.
  /// </summary>
  public async Task ServeAsync(WebSocket socket, CancellationToken stoppingToken) {
    ArgumentNullException.ThrowIfNull(socket);

    Guid id = Guid.NewGuid();
    Client client = new Client(this.options.ClientQueueCapacity);

    // Greeted before registered, so the greeting cannot end up queued behind a
    // state frame that a broadcast slipped in first. The protocol promises hello
    // arrives before anything else.
    client.Enqueue(this.greeting);
    this.clients[id] = client;
    this.log.LogInformation("Web client connected. {Count} now watching.", this.clients.Count);

    using CancellationTokenSource connection = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

    try {
      Task sending = SendAsync(socket, client, connection.Token);
      Task<CloseIntent?> receiving = this.ReceiveAsync(socket, client, connection.Token);

      // Either half finishing means the connection is over: a closed socket ends
      // the receive, and a dead one ends the send.
      await Task.WhenAny(sending, receiving).ConfigureAwait(false);
      client.Complete();

      // A close decided by the receive loop is written only once the send loop
      // has drained and exited: WebSocket permits one outstanding send, and the
      // close frame is a send. A client too stalled to drain within DrainTimeout
      // forfeits the parting frame it could not have received anyway.
      if(receiving.IsCompletedSuccessfully && receiving.Result is CloseIntent close) {
        await Task.WhenAny(sending, Task.Delay(DrainTimeout, stoppingToken)).ConfigureAwait(false);

        if(sending.IsCompletedSuccessfully) {
          await CloseAsync(socket, close.Status, close.Reason, stoppingToken).ConfigureAwait(false);
        }
      }

      await connection.CancelAsync().ConfigureAwait(false);
      await Task.WhenAll(sending, receiving).ConfigureAwait(false);
    } catch(OperationCanceledException) {
      // Shutdown, or the other half of the connection ending first.
    } catch(WebSocketException ex) {
      // A browser tab closed mid-frame is the ordinary way for this to end.
      this.log.LogDebug(ex, "A web client's connection ended abruptly ({Error}).", ex.WebSocketErrorCode);
    } finally {
      this.clients.TryRemove(id, out _);
      this.log.LogInformation("Web client disconnected. {Count} still watching.", this.clients.Count);
    }
  }

  /// <summary>
  /// Drains one client's queue into its socket. The only writer to that socket.
  /// </summary>
  private static async Task SendAsync(WebSocket socket, Client client, CancellationToken token) {
    await foreach(ReadOnlyMemory<byte> frame in client.Reader.ReadAllAsync(token).ConfigureAwait(false)) {
      await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Reads client messages until the connection ends, and returns the close the
  /// connection has earned rather than sending it: the send loop owns the
  /// socket, so <see cref="ServeAsync"/> writes the close once that loop is done.
  ///
  /// The buffer is exactly the documented maximum and is reused for the life of
  /// the connection, so a client cannot make Flux allocate by talking - the
  /// oldest trick against a socket that accepts unauthenticated input, and this
  /// one accepts it from anything on the LAN by design.
  /// </summary>
  private async Task<CloseIntent?> ReceiveAsync(WebSocket socket, Client client, CancellationToken token) {
    byte[] message = new byte[Frames.MaxInboundBytes];
    int length = 0;

    while(!token.IsCancellationRequested) {
      ValueWebSocketReceiveResult received = await socket
        .ReceiveAsync(message.AsMemory(length), token)
        .ConfigureAwait(false);

      if(received.MessageType == WebSocketMessageType.Close) {
        return new CloseIntent(WebSocketCloseStatus.NormalClosure, "Goodbye.");
      }

      if(received.MessageType == WebSocketMessageType.Binary) {
        // The protocol is text-only. Saying so with the status code the RFC
        // reserves for it beats letting a binary client watch its frames vanish.
        return new CloseIntent(WebSocketCloseStatus.InvalidMessageType, "Flux speaks JSON text frames.");
      }

      length += received.Count;

      if(!received.EndOfMessage) {
        if(length == message.Length) {
          return new CloseIntent(WebSocketCloseStatus.MessageTooBig, "Message exceeds 4096 bytes.");
        }

        continue;
      }

      this.Dispatch(client, message.AsSpan(0, length));
      length = 0;
    }

    return null;
  }

  /// <summary>
  /// Acts on one complete client message.
  ///
  /// Replies are queued to the client rather than written here, because this
  /// runs on the receive loop and the send loop owns the socket.
  /// </summary>
  private void Dispatch(Client client, ReadOnlySpan<byte> message) {
    if(!Frames.TryDecode(message, out ClientEnvelope envelope) || envelope.Type is not string type) {
      client.Enqueue(Frames.Error("malformed", "Expected a JSON object with a string 'type'."));
      return;
    }

    switch(type) {
      case "ping":
        if(Frames.TryReadEcho(envelope.Data, out long t)) {
          client.Enqueue(Frames.Pong(t));
        } else {
          client.Enqueue(Frames.Error("invalid_data", "A ping needs a numeric 'data.t'."));
        }

        break;

      default:
        // Answered rather than ignored: the client half of this protocol is one
        // codebase, so an unrecognised type is a bug in it and should be loud.
        client.Enqueue(Frames.Error("unknown_type", $"Unknown message type '{Frames.Quote(type)}'."));
        break;
    }
  }

  private static async Task CloseAsync(
    WebSocket socket,
    WebSocketCloseStatus status,
    string reason,
    CancellationToken token) {
    if(socket.State is WebSocketState.Open or WebSocketState.CloseReceived) {
      await socket.CloseAsync(status, reason, token).ConfigureAwait(false);
    }
  }

  /// <summary>
  /// A close the receive loop has decided on but must not send itself.
  /// </summary>
  private readonly record struct CloseIntent(WebSocketCloseStatus Status, string Reason);

  /// <summary>
  /// One connected browser: a bounded queue and nothing else.
  /// </summary>
  private sealed class Client {
    private readonly Channel<ReadOnlyMemory<byte>> outbound;

    public Client(int capacity) {
      this.outbound = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(capacity) {
          // Dropping the oldest is right for this feed rather than merely
          // convenient: a state frame is a complete snapshot, so a stale one is
          // worthless the moment a newer one exists, and a beat that arrives
          // late is worse than one that never arrives.
          FullMode = BoundedChannelFullMode.DropOldest,
          SingleReader = true,

          // The state pump, the beat pump and this connection's own receive loop
          // all write here, on three different threads.
          SingleWriter = false,
        });
    }

    public ChannelReader<ReadOnlyMemory<byte>> Reader => this.outbound.Reader;

    public void Enqueue(ReadOnlyMemory<byte> frame) {
      this.outbound.Writer.TryWrite(frame);
    }

    public void Complete() {
      this.outbound.Writer.TryComplete();
    }
  }
}
