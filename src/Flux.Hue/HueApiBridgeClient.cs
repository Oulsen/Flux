using HueApi;
using HueApi.BridgeLocator;
using HueApi.Models;
using HueApi.Models.Clip;
using HueApi.Models.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Hue;

/// <summary>
/// The one class in Flux that names a HueApi type.
///
/// Everything it does is a translation: bridges, areas and credentials go out as
/// Flux's own shapes, and the library's exceptions come in and turn into either a
/// null (the link button, which is not a failure) or a
/// <see cref="HueBridgeException"/> (everything else). Keeping that boundary at one
/// file is what lets the state machine above it be tested without a bridge.
/// </summary>
internal sealed class HueApiBridgeClient: IHueBridgeClient {
  // What the bridge lists in its whitelist, so a user deleting entries in the Hue
  // app can tell which one is this application on which machine.
  private const string ApplicationName = "flux";

  private readonly IOptions<HueOptions> _options;
  private readonly ILogger<HueApiBridgeClient> _logger;

  public HueApiBridgeClient(IOptions<HueOptions> options, ILogger<HueApiBridgeClient> logger) {
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);

    _options = options;
    _logger = logger;
  }

  public async Task<IReadOnlyList<HueBridge>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken) {
    if(_options.Value.BridgeAddress is { Length: > 0 } configured) {
      // A configured address skips discovery entirely rather than joining its
      // results: somebody who names an address has already answered the question
      // discovery exists to ask, and returning a list for them to choose from again
      // would be a strange way to honour it.
      return [await DescribeAsync(configured, cancellationToken).ConfigureAwait(false)];
    }

    // mDNS and SSDP together, and concurrently, because they are both passive listens
    // that cost their window and nothing else - running them in sequence would double
    // the wait to learn what one pass already knows.
    //
    // SSDP is here because mDNS is not reliable enough to be the only local route. On
    // a machine with several disconnected adapters sitting on APIPA addresses, the
    // library's mDNS locator found nothing while SSDP found the bridge immediately, on
    // a network where mDNS itself was demonstrably working. That is a real desk, not a
    // hypothetical one.
    IReadOnlyList<HueBridge>[] local = await Task.WhenAll(
      LocateAsync(new MdnsBridgeLocator(), timeout, cancellationToken),
      LocateAsync(new SsdpBridgeLocator(), timeout, cancellationToken)).ConfigureAwait(false);

    // Merged by bridge id: a bridge that answers both is one bridge, and it is the
    // ordinary case rather than the exception.
    List<HueBridge> found = [.. local
      .SelectMany(bridges => bridges)
      .DistinctBy(bridge => bridge.Id, StringComparer.OrdinalIgnoreCase)];

    if(found.Count > 0) {
      return found;
    }

    // A subnet sweep: entirely local, and slower than a listen because it asks every
    // address rather than waiting to be told. Worth reaching for before leaving the
    // network, and not before, since it is the only route here that generates traffic
    // to machines that have nothing to do with Hue.
    _logger.LogInformation("Neither mDNS nor SSDP answered. Scanning the local network for a bridge.");
    IReadOnlyList<HueBridge> scanned = await LocateAsync(new LocalNetworkScanBridgeLocator(), timeout, cancellationToken).ConfigureAwait(false);
    if(scanned.Count > 0) {
      return scanned;
    }

    // The cloud lookup is the last resort and never a peer of the others: it is
    // rate-limited to roughly one request per fifteen minutes, it leaves the network,
    // and it only knows bridges that were seen from this same public address. A few
    // restarts inside that window exhaust it, which is exactly when a paired user most
    // needs an answer.
    _logger.LogInformation("Nothing answered on this network. Asking the discovery service, which only knows bridges seen from this address.");
    return await LocateAsync(new HttpBridgeLocator(), timeout, cancellationToken).ConfigureAwait(false);
  }

  public async Task<HueCredentials?> TryPairAsync(HueBridge bridge, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(bridge);

    try {
      RegisterEntertainmentResult? result = await LocalHueApi
        .RegisterAsync(bridge.Address, ApplicationName, Environment.MachineName, generateClientKey: true, cancellationToken)
        .ConfigureAwait(false);

      if(result is null) {
        // Older bridges answer an unpressed button with an empty result rather than
        // the documented error, so this is the same "keep waiting" and not a failure.
        return null;
      }

      if(string.IsNullOrEmpty(result.Username) || string.IsNullOrEmpty(result.StreamingClientKey)) {
        // A registration without a client key would pair successfully and then fail
        // at DTLS, an hour later, with nothing pointing back to here.
        throw new HueBridgeException("The bridge registered Flux but returned no streaming key. Pair again, and check that the bridge supports entertainment.");
      }

      return new HueCredentials(bridge.Id, result.Username, result.StreamingClientKey);
    } catch(LinkButtonNotPressedException) {
      return null;
    } catch(HueException e) {
      throw new HueBridgeException($"The bridge at {bridge.Address} refused the pairing request: {e.Message}", e);
    } catch(HttpRequestException e) {
      throw new HueBridgeException($"The bridge at {bridge.Address} could not be reached.", e);
    }
  }

  public async Task<HueBridge?> TryDescribeAsync(string address, CancellationToken cancellationToken) {
    if(string.IsNullOrWhiteSpace(address)) {
      return null;
    }

    try {
      return await DescribeAsync(address, cancellationToken).ConfigureAwait(false);
    } catch(HueBridgeException) {
      // A hint that did not pay off is not a failure worth reporting: the address is
      // remembered precisely because it might be stale.
      return null;
    }
  }

  public async Task<IReadOnlyList<HueArea>> GetAreasAsync(HueBridge bridge, HueCredentials credentials, CancellationToken cancellationToken) {
    ArgumentNullException.ThrowIfNull(bridge);
    ArgumentNullException.ThrowIfNull(credentials);

    try {
      LocalHueApi api = new LocalHueApi(bridge.Address, credentials.ApplicationKey);
      HueResponse<EntertainmentConfiguration> response = await api.EntertainmentConfiguration.GetAllAsync().ConfigureAwait(false);

      return [.. response.Data.Select(configuration => new HueArea(
        configuration.Id,
        configuration.Metadata?.Name is { Length: > 0 } name ? name : "Unnamed area",
        configuration.Channels?.Count ?? 0,
        // The bridge allows one entertainment session, so this is the whole of the
        // busy pre-flight: read before opening anything, because the failure it
        // prevents arrives as a DTLS timeout that blames the network instead.
        configuration.Status == EntertainmentConfigurationStatus.active))];
    } catch(HueException e) {
      throw new HueBridgeException($"The bridge at {bridge.Address} would not list its entertainment areas: {e.Message}", e);
    } catch(HttpRequestException e) {
      throw new HueBridgeException($"The bridge at {bridge.Address} could not be reached.", e);
    }
  }

  private async Task<IReadOnlyList<HueBridge>> LocateAsync(IBridgeLocator locator, TimeSpan timeout, CancellationToken cancellationToken) {
    try {
      using CancellationTokenSource window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      window.CancelAfter(timeout);

      IEnumerable<LocatedBridge> located = await locator.LocateBridgesAsync(window.Token).ConfigureAwait(false);
      return [.. located.Select(bridge => new HueBridge(bridge.BridgeId, bridge.IpAddress))];
    } catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) {
      // The timeout elapsing is how discovery ends, not how it fails: answers arrive
      // over time and the window is a patience setting.
      return [];
    } catch(HttpRequestException e) {
      _logger.LogWarning(e, "A bridge lookup failed. Discovery continues with whatever else answers.");
      return [];
    }
  }

  private async Task<HueBridge> DescribeAsync(string address, CancellationToken cancellationToken) {
    // Asked without a key on purpose: the identity of a bridge is not privileged, and
    // this runs before there are any credentials to ask with. What it buys is that a
    // configured address still produces the same bridge id discovery would have
    // found, so credentials belong to a bridge rather than to a line in a settings
    // file.
    try {
      BridgeConfig? config = await new LocalHueApi(address, string.Empty).GetConfigAsync().ConfigureAwait(false);
      cancellationToken.ThrowIfCancellationRequested();

      if(config is null || string.IsNullOrEmpty(config.BridgeId)) {
        throw new HueBridgeException($"Whatever answered at {address} did not identify itself as a Hue bridge.");
      }

      return new HueBridge(config.BridgeId, address);
    } catch(Exception e) when(e is HueException or HttpRequestException) {
      throw new HueBridgeException($"Hue:BridgeAddress is set to {address}, and nothing answered there.", e);
    }
  }
}
