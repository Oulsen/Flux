using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace Flux.Hue;

/// <summary>
/// Everything Flux knows about the bridge, and the only thing allowed to change it.
///
/// It holds one <see cref="HueConnection"/> and announces every change, because the
/// pairing screen is driven from a phone in somebody's hand while they walk to the
/// bridge: the countdown, the discovered list and the failure text all have to arrive
/// without being asked for. Commands are serialised - two phones on the same page is
/// the normal case, not an exotic one, and a second "pair" arriving mid-countdown
/// must not start a competing loop.
/// </summary>
public sealed class HueBridgeService: BackgroundService {
  private readonly IHueBridgeClient _client;
  private readonly ICredentialStore _credentials;
  private readonly IHueSettingsStore _settings;
  private readonly IOptions<HueOptions> _options;
  private readonly ILogger<HueBridgeService> _logger;
  private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

  private HueConnection _connection;

  public HueBridgeService(
    IHueBridgeClient client,
    ICredentialStore credentials,
    IHueSettingsStore settings,
    IOptions<HueOptions> options,
    ILogger<HueBridgeService> logger) {
    ArgumentNullException.ThrowIfNull(client);
    ArgumentNullException.ThrowIfNull(credentials);
    ArgumentNullException.ThrowIfNull(settings);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(logger);

    _client = client;
    _credentials = credentials;
    _settings = settings;
    _options = options;
    _logger = logger;
    _connection = HueConnection.Initial(options.Value.Enabled ? HueConnectionState.Unpaired : HueConnectionState.Disabled);
  }

  /// <summary>The current picture. Complete on every read, never a delta.</summary>
  public HueConnection Connection => Volatile.Read(ref _connection);

  /// <summary>Raised whenever <see cref="Connection"/> changes, with the new value.</summary>
  public event Action<HueConnection>? Changed;

  /// <summary>
  /// Looks for bridges and keeps the result.
  ///
  /// Does not change the paired state: discovery is how an unpaired user finds a
  /// bridge, and it is also how a paired one checks that theirs is still on the
  /// network, so it must not throw away what is already working.
  /// </summary>
  public async Task DiscoverAsync(CancellationToken cancellationToken) {
    if(!_options.Value.Enabled) {
      return;
    }

    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      HueConnectionState resting = Connection.State;
      Update(connection => connection with { State = HueConnectionState.Discovering, Message = null });

      IReadOnlyList<HueBridge> found = await _client.DiscoverAsync(_options.Value.DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
      _logger.LogInformation("Bridge discovery found {Count} bridge(s).", found.Count);

      Update(connection => connection with {
        State = resting == HueConnectionState.Discovering ? HueConnectionState.Unpaired : resting,
        Discovered = found,
      });
    } catch(HueBridgeException e) {
      Fail(e);
    } finally {
      _gate.Release();
    }
  }

  /// <summary>
  /// Asks a discovered bridge to register Flux, until it does or the window closes.
  ///
  /// Returns false when no such bridge has been discovered, which is a request the
  /// client was right to make and the server right to refuse - the usual cause is a
  /// pairing screen left open while the network changed underneath it.
  /// </summary>
  public async Task<bool> PairAsync(string bridgeId, CancellationToken cancellationToken) {
    ArgumentException.ThrowIfNullOrWhiteSpace(bridgeId);

    if(!_options.Value.Enabled) {
      return false;
    }

    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      HueBridge? bridge = Connection.Discovered.FirstOrDefault(b => string.Equals(b.Id, bridgeId, StringComparison.OrdinalIgnoreCase));
      if(bridge is null) {
        return false;
      }

      await PollForTheLinkButtonAsync(bridge, cancellationToken).ConfigureAwait(false);
      return true;
    } catch(HueBridgeException e) {
      Fail(e);
      return true;
    } finally {
      _gate.Release();
    }
  }

  /// <summary>
  /// Chooses the entertainment area to stream to, and remembers it.
  ///
  /// Returns false for an area the bridge did not report, which covers the ordinary
  /// race of an area deleted in the Hue app while its button was on screen.
  /// </summary>
  public async Task<bool> SelectAreaAsync(Guid areaId, CancellationToken cancellationToken) {
    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if(!Connection.Areas.Any(area => area.Id == areaId)) {
        return false;
      }

      _settings.Save(_settings.Load() with { EntertainmentAreaId = areaId });
      Update(connection => connection with { SelectedAreaId = areaId });
      return true;
    } finally {
      _gate.Release();
    }
  }

  /// <summary>
  /// Re-reads the bridge: where it is now, and what areas it has.
  ///
  /// The area list is not static - areas are created and deleted in the Hue app while
  /// Flux is running, and one of them being busy is a fact with a lifetime of
  /// seconds.
  /// </summary>
  public async Task RefreshAsync(CancellationToken cancellationToken) {
    if(!_options.Value.Enabled) {
      return;
    }

    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      await ConnectAsync(cancellationToken).ConfigureAwait(false);
    } finally {
      _gate.Release();
    }
  }

  /// <summary>
  /// Forgets the bridge and everything chosen about it.
  ///
  /// The bridge keeps its whitelist entry until somebody deletes it in the Hue app,
  /// so this is Flux forgetting the bridge and not the other way round. Pairing again
  /// creates a second entry rather than reusing the first.
  /// </summary>
  public void Forget() {
    _credentials.Clear();
    _settings.Save(HueSettings.Empty);
    _logger.LogInformation("The paired bridge has been forgotten.");

    Update(connection => connection with {
      State = _options.Value.Enabled ? HueConnectionState.Unpaired : HueConnectionState.Disabled,
      Bridge = null,
      Areas = [],
      SelectedAreaId = null,
      PairingSecondsRemaining = null,
      Message = null,
    });
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Yielded before anything can throw, because ExecuteAsync runs synchronously up
    // to its first await: a bridge that is switched off must never be able to stop
    // Flux from starting. That lesson was paid for once already, by the Link listener
    // taking the whole host down when rekordbox held its port.
    await Task.Yield();

    if(!_options.Value.Enabled) {
      _logger.LogInformation("Hue is switched off in the settings. No bridge will be contacted.");
      return;
    }

    if(_credentials.Load() is null) {
      _logger.LogInformation("No bridge is paired. Pair one from the web page.");
      return;
    }

    await _gate.WaitAsync(stoppingToken).ConfigureAwait(false);
    try {
      await ConnectAsync(stoppingToken).ConfigureAwait(false);
    } finally {
      _gate.Release();
    }
  }

  public override void Dispose() {
    _gate.Dispose();
    base.Dispose();
  }

  private async Task PollForTheLinkButtonAsync(HueBridge bridge, CancellationToken cancellationToken) {
    HueOptions options = _options.Value;
    Stopwatch elapsed = Stopwatch.StartNew();

    _logger.LogInformation("Pairing with bridge {BridgeId} at {Address}. Press the link button.", bridge.Id, bridge.Address);

    while(elapsed.Elapsed < options.PairingWindow) {
      Update(connection => connection with {
        State = HueConnectionState.Pairing,
        PairingSecondsRemaining = (int)Math.Ceiling((options.PairingWindow - elapsed.Elapsed).TotalSeconds),
        Message = null,
      });

      HueCredentials? credentials = await _client.TryPairAsync(bridge, cancellationToken).ConfigureAwait(false);
      if(credentials is not null) {
        _credentials.Save(credentials);
        _logger.LogInformation("Paired with bridge {BridgeId}.", credentials.BridgeId);

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return;
      }

      await Task.Delay(options.PairingPollInterval, cancellationToken).ConfigureAwait(false);
    }

    // Not an error, and worded as what happened rather than as a failure: the button
    // is on a physical object in another room, and the only thing that went wrong is
    // that nobody pressed it in time.
    Update(connection => connection with {
      State = HueConnectionState.Unpaired,
      PairingSecondsRemaining = null,
      Message = "Didn't see the button press. Press the link button on the bridge and try again.",
    });
  }

  private async Task ConnectAsync(CancellationToken cancellationToken) {
    HueCredentials? credentials = _credentials.Load();
    if(credentials is null) {
      Update(connection => connection with { State = HueConnectionState.Unpaired, Bridge = null, Areas = [] });
      return;
    }

    try {
      // The countdown is dropped in the same frame the state leaves Pairing, in both
      // directions: a frame saying "pairing" with no countdown blanks the number
      // somebody is reading, and one saying "connecting" with a countdown left on it
      // is counting down to nothing.
      Update(connection => connection with {
        State = HueConnectionState.Connecting,
        PairingSecondsRemaining = null,
        Message = null,
      });

      HueBridge? bridge = await FindPairedBridgeAsync(credentials.BridgeId, cancellationToken).ConfigureAwait(false);
      if(bridge is null) {
        Update(connection => connection with {
          State = HueConnectionState.Failed,
          Message = "The paired bridge is not answering on this network. Check that it is powered on and on the same network as this PC.",
        });
        return;
      }

      IReadOnlyList<HueArea> areas = await _client.GetAreasAsync(bridge, credentials, cancellationToken).ConfigureAwait(false);

      // A remembered area that the bridge no longer has is dropped rather than
      // carried: it was deleted in the Hue app, and offering to stream to it would
      // fail at the one moment nobody is watching a screen.
      Guid? remembered = _settings.Load().EntertainmentAreaId;
      Guid? selected = areas.Any(area => area.Id == remembered) ? remembered : null;

      _logger.LogInformation("Bridge {BridgeId} at {Address} has {Count} entertainment area(s).", bridge.Id, bridge.Address, areas.Count);

      Update(connection => connection with {
        State = HueConnectionState.Ready,
        Bridge = bridge,
        Areas = areas,
        SelectedAreaId = selected,
        PairingSecondsRemaining = null,
        Message = null,
      });
    } catch(HueBridgeException e) {
      Fail(e);
    }
  }

  private async Task<HueBridge?> FindPairedBridgeAsync(string bridgeId, CancellationToken cancellationToken) {
    // Discovery every time rather than a remembered address, because DHCP moves a
    // bridge between sessions and the credentials stay valid across the move. The
    // stored identity is the bridge, and this is the step that turns it back into a
    // place.
    IReadOnlyList<HueBridge> found = await _client.DiscoverAsync(_options.Value.DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
    Update(connection => connection with { Discovered = found });

    return found.FirstOrDefault(bridge => string.Equals(bridge.Id, bridgeId, StringComparison.OrdinalIgnoreCase));
  }

  private void Fail(HueBridgeException e) {
    _logger.LogWarning(e, "The bridge conversation failed.");
    Update(connection => connection with { State = HueConnectionState.Failed, PairingSecondsRemaining = null, Message = e.Message });
  }

  private void Update(Func<HueConnection, HueConnection> change) {
    HueConnection updated = change(Connection);
    if(updated == Connection) {
      // A poll that changed nothing must not wake every connected page. The record
      // compares field by field, so this catches the scalar no-ops - the same area
      // chosen twice, a countdown redrawn on the same second. It deliberately does
      // not catch a re-discovered list with identical contents: those are compared by
      // reference, and re-announcing them costs one frame on a screen somebody is
      // already watching.
      return;
    }

    Volatile.Write(ref _connection, updated);
    Changed?.Invoke(updated);
  }
}
