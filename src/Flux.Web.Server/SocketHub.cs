using Flux.Audio;
using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
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
  private readonly SyncSourceManager sources;
  private readonly ManualSource manual;

  /// <summary>
  /// Null in a build with no audio support. Both messages that need it answer
  /// <c>rejected</c> in that case, which is the same answer an unplugged device
  /// gets: from the page's side, "that device is not there" covers both, and both
  /// leave the selection exactly where it was.
  /// </summary>
  private readonly IAudioDevices? audio;

  private readonly ILogger<SocketHub> log;
  private readonly ReadOnlyMemory<byte> greeting;

  /// <summary>
  /// The quantum comes from the beat settings rather than from the web settings
  /// because it is a property of the music, not of the transport. It is also the
  /// one part of the beat that no source can supply: bar <em>position</em> gets
  /// anchored by a cue press or a tap, but bar <em>length</em> is only ever
  /// configuration, which is why switching sources cannot change this greeting.
  ///
  /// <see cref="ManualSource"/> arrives as itself rather than through the
  /// manager, because tapping and typing a tempo are that one source's own
  /// operations: routing them through a registry that deliberately knows nothing
  /// about any particular source would mean teaching it about this one.
  /// </summary>
  public SocketHub(
    IOptions<WebOptions> options,
    IOptions<BeatOptions> beat,
    SyncSourceManager sources,
    ManualSource manual,
    IAudioDevices? audio,
    ILogger<SocketHub> log) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(beat);

    this.options = options.Value;
    this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
    this.manual = manual ?? throw new ArgumentNullException(nameof(manual));
    this.audio = audio;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    // Every field in the greeting is fixed for the lifetime of the process, so
    // it is encoded once here rather than per connection.
    this.greeting = Frames.Hello(
      FluxVersion.Current,
      beat.Value.Quantum,
      this.options.StateHz,
      this.options.SpectrumHz,
      this.options.DevicesHz);
  }

  /// <summary>
  /// Whether anyone is listening.
  ///
  /// The state pump checks this before it builds a snapshot: with no browser
  /// open, serialising twenty frames a second into nowhere is the only wasted
  /// work in an otherwise idle process.
  /// </summary>
  public bool HasClients => !this.clients.IsEmpty;

  /// <summary>Whether anyone has asked for spectrum frames.</summary>
  public bool HasSpectrumSubscribers => this.clients.Values.Any(client => client.WantsSpectrum);

  /// <summary>Whether anyone has asked for the device list.</summary>
  public bool HasDeviceSubscribers => this.clients.Values.Any(client => client.WantsDeviceLevels);

  /// <summary>Sends one already-encoded frame to every connected client.</summary>
  public void Broadcast(ReadOnlyMemory<byte> frame) {
    foreach(Client client in this.clients.Values) {
      client.Enqueue(frame);
    }
  }

  /// <summary>
  /// Sends a spectrum frame to the clients that asked for one.
  /// </summary>
  public void BroadcastSpectrum(ReadOnlyMemory<byte> frame) {
    foreach(Client client in this.clients.Values) {
      if(client.WantsSpectrum) {
        client.EnqueueSpectrum(frame);
      }
    }
  }

  /// <summary>
  /// Sends the device list to the clients that asked for it.
  ///
  /// This goes through the ordinary queue rather than the spectrum slot: it is
  /// small, it is rare, and a device list that arrived out of date would leave a
  /// picker showing a device that is no longer there.
  /// </summary>
  public void BroadcastDevices(ReadOnlyMemory<byte> frame) {
    foreach(Client client in this.clients.Values) {
      if(client.WantsDeviceLevels) {
        client.Enqueue(frame);
      }
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
  /// Drains one client's two queues into its socket. The only writer to that
  /// socket, still: two queues, one loop, so no send can overlap another.
  ///
  /// The ordinary queue is always emptied before a spectrum frame is looked at.
  /// That is the whole reason the spectrum has a queue of its own - a 30 Hz
  /// picture sharing the main queue would fill it in about a second and push the
  /// beats out of it, and a beat lost to make room for a picture of a beat is
  /// the wrong trade in a lighting app.
  ///
  /// The two pending waits are held across iterations rather than created fresh
  /// each time. Creating a wait on both channels every pass and abandoning the
  /// loser would leave one dangling waiter per frame on whichever channel is
  /// quiet, which over a long set is a leak with no symptom until it is a large
  /// one.
  /// </summary>
  private static async Task SendAsync(WebSocket socket, Client client, CancellationToken token) {
    Task<bool>? ordinary = null;
    Task<bool>? spectrum = null;

    while(true) {
      if(client.Reader.TryRead(out ReadOnlyMemory<byte> frame)
        || client.SpectrumReader.TryRead(out frame)) {
        await socket.SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, token).ConfigureAwait(false);
        continue;
      }

      ordinary ??= client.Reader.WaitToReadAsync(token).AsTask();
      spectrum ??= client.SpectrumReader.WaitToReadAsync(token).AsTask();

      Task<bool> ready = await Task.WhenAny(ordinary, spectrum).ConfigureAwait(false);

      // Awaited rather than inspected, so a cancelled wait throws here and ends
      // the connection the same way every other cancellation does.
      bool more = await ready.ConfigureAwait(false);

      if(ReferenceEquals(ready, ordinary)) {
        ordinary = null;

        // Only the ordinary queue is ever completed, so this is the connection
        // ending with its queue drained. The spectrum wait left pending here is
        // abandoned with the client it belongs to.
        if(!more) {
          return;
        }
      } else {
        spectrum = null;
      }
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

      case "subscribe":
        if(Frames.TryReadSubscribe(envelope.Data, out bool spectrum, out bool deviceLevels)) {
          client.Subscribe(spectrum, deviceLevels);

          // The list goes out at once rather than on the broadcaster's next tick,
          // because a picker that opens blank looks broken for as long as it stays
          // blank. Sent on any subscribe that wants the levels rather than only on
          // the transition into wanting them: a client re-stating its wants gets a
          // fresh list, which is the answer it would have wanted either way.
          if(deviceLevels && this.audio is not null) {
            client.Enqueue(DeviceFrames.Snapshot(this.audio));
          }
        } else {
          client.Enqueue(Frames.Error(
            "invalid_data",
            "A subscribe needs boolean 'data.spectrum' and 'data.deviceLevels'."));
        }

        break;

      case "selectSource":
        this.SelectSource(client, envelope.Data);
        break;

      case "selectAudioDevice":
        this.SelectAudioDevice(client, envelope.Data);
        break;

      case "tap":
        // Timed inside ManualSource, on arrival. Nothing in the payload is read:
        // the two clocks are unrelated, so a timestamp from the browser would
        // place the beat somewhere arbitrary.
        this.manual.Tap();
        break;

      case "setBpm":
        this.SetBpm(client, envelope.Data);
        break;

      default:
        // Answered rather than ignored: the client half of this protocol is one
        // codebase, so an unrecognised type is a bug in it and should be loud.
        client.Enqueue(Frames.Error("unknown_type", $"Unknown message type '{Frames.Quote(type)}'."));
        break;
    }
  }

  /// <summary>
  /// Switches the sync source, or says why it did not.
  ///
  /// There is no acknowledgement on success: the next state frame carries the
  /// active source, and that is the only confirmation worth having, because it
  /// comes from the thing that actually made the change.
  /// </summary>
  private void SelectSource(Client client, JsonElement data) {
    if(!Frames.TryReadSource(data, out string source)) {
      client.Enqueue(Frames.Error("invalid_data", "Selecting a source needs a string 'data.source'."));
      return;
    }

    if(!this.sources.TrySelect(source)) {
      client.Enqueue(Frames.Error(
        "rejected",
        $"No sync source named '{Frames.Quote(source)}' in this build."));
      return;
    }

    this.log.LogInformation("A web client selected the {Source} sync source.", source);
  }

  /// <summary>
  /// Points audio analysis at a different device, or says why it did not.
  ///
  /// No acknowledgement on success, for the same reason as a source switch: the
  /// next <c>devices</c> frame carries the selection, and that confirmation comes
  /// from the thing that actually made the change. A page that lit its own button
  /// on click would keep lying about it after a rejection.
  ///
  /// A rejection is the ordinary outcome rather than an error condition. The device
  /// list a browser is looking at is up to a second old, so an endpoint unplugged
  /// in the meantime is a race that will happen, and the honest answer is that the
  /// selection stands where it was.
  /// </summary>
  private void SelectAudioDevice(Client client, JsonElement data) {
    if(!Frames.TryReadDeviceId(data, out string deviceId)) {
      client.Enqueue(Frames.Error("invalid_data", "Selecting a device needs a string 'data.deviceId'."));
      return;
    }

    if(this.audio is null || !this.audio.TrySelect(deviceId)) {
      client.Enqueue(Frames.Error(
        "rejected",
        $"No audio device with id '{Frames.Quote(deviceId)}'."));
      return;
    }

    this.log.LogInformation("A web client selected the audio device {DeviceId}.", deviceId);
  }

  /// <summary>
  /// Sets or clears the manual tempo.
  ///
  /// An out-of-range tempo is refused rather than clamped. The values just
  /// outside 20..200 are typos, and clamping a typo produces a plausible wrong
  /// answer, which is worse than a rejection the user can see.
  /// </summary>
  private void SetBpm(Client client, JsonElement data) {
    if(!Frames.TryReadSetBpm(data, out double? bpm)) {
      client.Enqueue(Frames.Error("invalid_data", "Setting a tempo needs 'data.bpm' as a number or null."));
      return;
    }

    if(bpm is not double value) {
      this.manual.ClearBpm();
      return;
    }

    if(!this.manual.TrySetBpm(value)) {
      client.Enqueue(Frames.Error(
        "rejected",
        $"A manual tempo must be between {ManualSource.MinBpm:F0} and {ManualSource.MaxBpm:F0} BPM."));
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
  /// One connected browser: two bounded queues and what it has asked to be sent.
  /// </summary>
  private sealed class Client {
    private readonly Channel<ReadOnlyMemory<byte>> outbound;

    /// <summary>
    /// The one spectrum frame waiting to go out.
    ///
    /// Capacity one, dropping the oldest, which together mean the queued frame
    /// is always the newest one produced. A visualiser is worth exactly one
    /// frame of latency: an older frame is not a smaller version of the picture,
    /// it is the wrong picture.
    /// </summary>
    private readonly Channel<ReadOnlyMemory<byte>> spectrum = Channel.CreateBounded<ReadOnlyMemory<byte>>(
      new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false,
      });

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

    /// <summary>
    /// Whether this client asked for spectrum frames. Written by its own receive
    /// loop and read by the broadcasters, so it is volatile rather than locked:
    /// a broadcaster reading the previous answer sends or skips one frame at
    /// 30 Hz, which is not worth a lock on the path a beat also takes.
    /// </summary>
    public volatile bool WantsSpectrum;

    /// <summary>Whether this client asked for the device list.</summary>
    public volatile bool WantsDeviceLevels;

    public ChannelReader<ReadOnlyMemory<byte>> Reader => this.outbound.Reader;

    public ChannelReader<ReadOnlyMemory<byte>> SpectrumReader => this.spectrum.Reader;

    public void Enqueue(ReadOnlyMemory<byte> frame) {
      this.outbound.Writer.TryWrite(frame);
    }

    public void EnqueueSpectrum(ReadOnlyMemory<byte> frame) {
      this.spectrum.Writer.TryWrite(frame);
    }

    /// <summary>
    /// Records a complete statement of what this client wants. Never a delta:
    /// the protocol has no unsubscribe, so each message replaces the last answer.
    /// </summary>
    public void Subscribe(bool wantsSpectrum, bool wantsDeviceLevels) {
      this.WantsSpectrum = wantsSpectrum;
      this.WantsDeviceLevels = wantsDeviceLevels;
    }

    /// <summary>
    /// Ends the connection's send loop once its ordinary queue has drained.
    ///
    /// The spectrum channel is deliberately never completed. It is the loop's
    /// second wait, and completing it would make that wait return "no more"
    /// immediately and forever - a wait that never blocks, in a loop that has
    /// nothing else to do, is a spinning core. The loop ends on the ordinary
    /// queue, which is the one every message that matters travels through.
    /// </summary>
    public void Complete() {
      this.outbound.Writer.TryComplete();
    }
  }
}
