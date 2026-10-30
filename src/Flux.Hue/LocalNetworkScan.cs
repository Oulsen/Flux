using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace Flux.Hue;

/// <summary>
/// Finds a bridge by asking every address on the local networks who it is.
///
/// Flux's own rather than the library's scan, because the library's asks over plain
/// HTTP, and a Hue Bridge Pro answers plain HTTP with a redirect to HTTPS and nothing
/// else. On a network where mDNS and SSDP also stay quiet, that left a Bridge Pro
/// impossible to find at all. This asks <c>https://&lt;address&gt;/api/0/config</c>,
/// the same unauthenticated question a configured address is checked with, which
/// every bridge that streams entertainment answers.
///
/// The bridge's certificate is not checked here. A bridge signs its own, and the only
/// thing read is its public id, to put a name to an address; nothing secret goes out,
/// and pairing and streaming run through the library as before.
///
/// Only the /24 around each of this machine's own addresses is asked, whatever the
/// adapter's mask says: a /16 would be sixty-five thousand questions to find one box
/// that is almost always on the same /24 as the PC.
/// </summary>
internal sealed class LocalNetworkScan {
  // Enough at once that four networks of silent addresses fit the discovery window:
  // most addresses have nothing on them and cost the whole probe timeout.
  private const int Concurrency = 128;

  private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1500);

  /// <summary>
  /// Asks every candidate address until the window closes, and returns whatever answered
  /// as a bridge by then.
  /// </summary>
  public async Task<IReadOnlyList<HueBridge>> ScanAsync(TimeSpan window, CancellationToken cancellationToken) {
    IReadOnlyList<IPAddress> candidates = Candidates(LocalAddresses());
    ConcurrentDictionary<string, HueBridge> found = new ConcurrentDictionary<string, HueBridge>(StringComparer.OrdinalIgnoreCase);

    using SocketsHttpHandler handler = new SocketsHttpHandler {
      ConnectTimeout = ProbeTimeout,
      SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    };
    using HttpClient http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(window);

    try {
      await Parallel.ForEachAsync(
        candidates,
        new ParallelOptions { MaxDegreeOfParallelism = Concurrency, CancellationToken = deadline.Token },
        async (address, token) => {
          if(await ProbeAsync(http, address, token).ConfigureAwait(false) is string id) {
            found.TryAdd(id, new HueBridge(id, address.ToString()));
          }
        }).ConfigureAwait(false);
    } catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) {
      // The window closing ends the scan; what answered before it still counts.
    }

    return [.. found.Values];
  }

  /// <summary>
  /// Every address on the /24 around each local address, without the local addresses
  /// themselves, the network and broadcast addresses, or duplicates.
  /// </summary>
  internal static IReadOnlyList<IPAddress> Candidates(IEnumerable<IPAddress> local) {
    List<IPAddress> own = [.. local.Where(IsScannable)];
    HashSet<IPAddress> candidates = [];

    foreach(IPAddress address in own) {
      byte[] bytes = address.GetAddressBytes();

      for(int host = 1; host < 255; host++) {
        candidates.Add(new IPAddress([bytes[0], bytes[1], bytes[2], (byte)host]));
      }
    }

    candidates.ExceptWith(own);

    return [.. candidates];
  }

  /// <summary>
  /// The bridge id in a <c>/api/0/config</c> answer, or null for anything that is not
  /// a bridge's: a router's admin page answers on 443 too.
  /// </summary>
  internal static string? ParseBridgeId(string json) {
    try {
      using JsonDocument document = JsonDocument.Parse(json);

      return document.RootElement.ValueKind == JsonValueKind.Object
        && document.RootElement.TryGetProperty("bridgeid", out JsonElement id)
        && id.ValueKind == JsonValueKind.String
        && id.GetString() is { Length: > 0 } value
          ? value
          : null;
    } catch(JsonException) {
      return null;
    }
  }

  /// <summary>
  /// IPv4 only, and not loopback or link-local: an APIPA address is an adapter that
  /// found no network, and its /24 has nothing on it worth a question.
  /// </summary>
  private static bool IsScannable(IPAddress address) {
    if(address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) {
      return false;
    }

    byte[] bytes = address.GetAddressBytes();

    return !(bytes[0] == 169 && bytes[1] == 254);
  }

  private static IEnumerable<IPAddress> LocalAddresses() {
    return NetworkInterface.GetAllNetworkInterfaces()
      .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up
        && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
      .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses)
      .Select(unicast => unicast.Address);
  }

  private static async Task<string?> ProbeAsync(HttpClient http, IPAddress address, CancellationToken cancellationToken) {
    using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    probe.CancelAfter(ProbeTimeout);

    try {
      using HttpResponseMessage response = await http
        .GetAsync(new Uri($"https://{address}/api/0/config"), probe.Token)
        .ConfigureAwait(false);

      if(!response.IsSuccessStatusCode) {
        return null;
      }

      return ParseBridgeId(await response.Content.ReadAsStringAsync(probe.Token).ConfigureAwait(false));
    } catch(Exception e) when(e is HttpRequestException or OperationCanceledException && !cancellationToken.IsCancellationRequested) {
      // Nothing there, nothing on 443, or something that is not a bridge: the ordinary
      // answer for almost every address asked.
      return null;
    }
  }
}
