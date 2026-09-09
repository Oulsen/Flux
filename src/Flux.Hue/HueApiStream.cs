using HueApi.ColorConverters;
using HueApi.Entertainment;
using HueApi.Entertainment.Models;
using HueApi.Models;
using HueApi.Models.Exceptions;
using System.Net.Sockets;

namespace Flux.Hue;

/// <summary>
/// Opens the DTLS session, and the only place the entertainment half of HueApi is
/// named.
///
/// The same boundary argument as <see cref="HueApiBridgeClient"/>: above this file the
/// loop, the frame rate and the colour engine work in Flux's own shapes and are
/// testable with no bridge, no key exchange and nothing listening on UDP 2100.
/// </summary>
internal sealed class HueApiStreamFactory: IHueStreamFactory {
  public async Task<IHueStream> OpenAsync(
    HueBridge bridge,
    HueCredentials credentials,
    Guid areaId,
    CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(bridge);
    ArgumentNullException.ThrowIfNull(credentials);

    StreamingHueClient client = new StreamingHueClient(
      bridge.Address,
      credentials.ApplicationKey,
      credentials.ClientKey);

    try {
      // Listed and filtered rather than fetched by id. It is one extra request at the
      // moment a session opens, and it reuses the one call this codebase has already
      // seen answer correctly against a real bridge - which is worth more here than
      // saving a round trip, because the alternative failure appears as a DTLS
      // timeout that blames the network.
      HueResponse<EntertainmentConfiguration> response = await client.LocalHueApi.EntertainmentConfiguration
        .GetAllAsync()
        .ConfigureAwait(false);

      EntertainmentConfiguration configuration = response.Data.FirstOrDefault(area => area.Id == areaId)
        ?? throw new HueBridgeException("The bridge no longer has the entertainment area Flux was told to stream to.");

      if(configuration.Channels is not { Count: > 0 } channels) {
        throw new HueBridgeException("That entertainment area has no channels, so there is nothing to light.");
      }

      StreamingGroup group = new StreamingGroup(channels);
      EntertainmentLayer layer = group.GetNewLayer(isBaseLayer: true);

      cancellationToken.ThrowIfCancellationRequested();
      await client.ConnectAsync(areaId).ConfigureAwait(false);

      return new HueApiStream(client, group, layer);
    } catch(Exception e) when(e is HueException or HueEntertainmentException or HttpRequestException or SocketException) {
      client.Dispose();
      throw new HueBridgeException(
        $"The bridge at {bridge.Address} would not open a light stream. The usual cause is another application holding its one entertainment session.",
        e);
    } catch {
      // Anything else - a cancellation, most likely - still must not leak the socket.
      client.Dispose();
      throw;
    }
  }
}

/// <summary>One open session, and the frame that goes down it.</summary>
internal sealed class HueApiStream: IHueStream {
  private readonly StreamingHueClient client;
  private readonly StreamingGroup group;
  private readonly EntertainmentLayer layer;

  private bool disposed;

  internal HueApiStream(StreamingHueClient client, StreamingGroup group, EntertainmentLayer layer) {
    this.client = client;
    this.group = group;
    this.layer = layer;
  }

  public int ChannelCount => this.layer.Count;

  public void SetAll(HueColor color) {
    HueColor safe = color.Clamped();
    RGBColor rgb = new RGBColor(safe.R, safe.G, safe.B);

    foreach(EntertainmentLight light in this.layer) {
      light.State.SetRGBColor(rgb);

      // Brightness is pinned at full and the colour carries the intensity, which is
      // one decision rather than two multiplying each other. A dim red is RGB(0.3,0,0)
      // here, not full red at 30% - the second form gives the same result two ways and
      // makes the light engine's output impossible to reason about from a log line.
      light.State.SetBrightness(1.0);
    }
  }

  public void Send() {
    // onlySendDirtyStates is false deliberately. With it on, a frame identical to the
    // last one sends nothing at all: the bridge would then time the session out during
    // any passage where the lights hold still, and the measured frame rate would be
    // counting frames that never left the machine.
    this.client.ManualUpdate(this.group, onlySendDirtyStates: false);
  }

  public void Dispose() {
    if(this.disposed) {
      return;
    }

    this.disposed = true;

    // Dispose only, never Close first. The library's Close already disposes the UDP
    // transport, so calling both shuts down a socket that has already gone and throws
    // ObjectDisposedException - which, raised from the stream thread's own finally,
    // took the whole process down once.
    try {
      this.client.Dispose();
    } catch(ObjectDisposedException) {
      // Its own teardown is not idempotent, and a session being closed twice is not
      // worth a word to anybody: the socket is shut either way.
    }
  }
}
