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

  /// <summary>
  /// Held across "write the choice down and announce it", which is one change and has
  /// to look like one.
  ///
  /// Not <see cref="_gate"/>, which is held across awaits and so cannot be taken by the
  /// synchronous setters, and which some of those setters deliberately do not wait for.
  /// What this closes is narrower and real: every mutation here is a load-modify-save
  /// against the settings file followed by a read-modify-write of
  /// <see cref="_connection"/>, and <c>setLatencyOffset</c> and <c>setLightSettings</c>
  /// arrive inline on each client's own receive loop. A laptop dragging brightness at
  /// ten a second beside a phone tapping a room is two threads through here, and
  /// interleaving them drops one of the two writes or leaves the file and the screen
  /// disagreeing about which change won.
  /// </summary>
  private readonly object _state = new();

  /// <summary>
  /// Guards <see cref="_pairing"/> alone, so that cancelling a countdown and the
  /// countdown finishing cannot race over a disposed source.
  /// </summary>
  private readonly object _pairingGate = new();

  /// <summary>The countdown in flight, or null when nobody is pairing.</summary>
  private CancellationTokenSource? _pairing;

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

    HueSettings stored = settings.Load();

    // A stored offset wins over the configured one, and null is the difference between
    // "nobody has touched the slider" and "somebody decided this rig needs no
    // correction". Read once here rather than on every broadcast: it is the user's
    // number from this point on, and the settings file is only where it survives a
    // restart.
    //
    // Stored light settings are checked rather than trusted, which the offset does not
    // need to be because the offset arrives through a bound and validated option. This
    // file is plain JSON in a folder a person can open, and a hand-edited palette that
    // names nothing would otherwise reach the engine.
    _connection = HueConnection.Initial(
      options.Value.Enabled ? HueConnectionState.Unpaired : HueConnectionState.Disabled,
      stored.LatencyOffsetMs ?? options.Value.LatencyOffsetMs,
      stored.Light is { } light && light.IsValid() ? light : LightSettings.Default);
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

      // Both remembered, because both are restored. Clearing the message while the
      // search runs is right - a stale error should not sit under a spinner - but
      // putting the state back without it left Failed with nothing to explain it,
      // which renders as a red line saying only that something went wrong.
      string? reason = Connection.Message;
      Update(connection => connection with { State = HueConnectionState.Discovering, Message = null });

      IReadOnlyList<HueBridge> found = await _client.DiscoverAsync(_options.Value.DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
      _logger.LogInformation("Bridge discovery found {Count} bridge(s).", found.Count);

      bool settled = resting == HueConnectionState.Discovering;

      Update(connection => connection with {
        State = settled ? HueConnectionState.Unpaired : resting,
        Message = settled ? null : reason,
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

      // Cancellable independently of the caller's token, because forgetting the bridge
      // has to be able to end a countdown that is otherwise a minute of polling with
      // the gate held - see ForgetAsync for what that minute costs.
      CancellationTokenSource pairing = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
      lock(_pairingGate) {
        _pairing = pairing;
      }

      try {
        await PollForTheLinkButtonAsync(bridge, pairing.Token).ConfigureAwait(false);
        return true;
      } finally {
        // Cleared before it is disposed, and both under the lock a cancel is taken
        // under, so a cancel arriving now finds nothing rather than a dead source.
        lock(_pairingGate) {
          _pairing = null;
          pairing.Dispose();
        }
      }
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

      lock(_state) {
        _settings.Save(_settings.Load() with { EntertainmentAreaId = areaId });
        Update(connection => connection with { SelectedAreaId = areaId });
      }

      return true;
    } finally {
      _gate.Release();
    }
  }

  /// <summary>
  /// Shifts light against the beat, and remembers it.
  ///
  /// Returns false outside <see cref="HueOptions.MaxLatencyOffsetMs"/> either way,
  /// rather than clamping: the values just outside the range are typos, and clamping
  /// turns a typo into a plausible wrong answer that somebody then tunes around.
  ///
  /// Not gated on a bridge being paired. The offset describes the room rather than the
  /// hardware, and refusing to remember it until a bridge answers would lose the
  /// number somebody set while waiting for one.
  /// </summary>
  public bool TrySetLatencyOffset(int offsetMs) {
    if(Math.Abs(offsetMs) > HueOptions.MaxLatencyOffsetMs) {
      return false;
    }

    lock(_state) {
      _settings.Save(_settings.Load() with { LatencyOffsetMs = offsetMs });
      Update(connection => connection with { LatencyOffsetMs = offsetMs });
    }

    return true;
  }

  /// <summary>
  /// Sets how the light engine turns beats into colour, and remembers it.
  ///
  /// One call for all three settings rather than one each, because they are changed
  /// together and read together: the engine consults the whole record on every frame,
  /// and three separate commands would let a page put a palette and an interval on the
  /// wire in an order that briefly means something nobody asked for.
  ///
  /// Returns false for anything outside the documented ranges, refused rather than
  /// clamped on the same argument as the offset. Not gated on a paired bridge either -
  /// these describe a look rather than the hardware, and there is no reason somebody
  /// waiting for a bridge to answer should not be able to choose a palette.
  /// </summary>
  public bool TrySetLightSettings(LightSettings settings) {
    if(settings is null || !settings.IsValid()) {
      return false;
    }

    lock(_state) {
      _settings.Save(_settings.Load() with { Light = settings });
      Update(connection => connection with { Light = settings });
    }

    return true;
  }

  /// <summary>
  /// Says something to every page, without claiming anything broke.
  ///
  /// <see cref="HueConnection.Message"/> reaches every screen at once, which a
  /// per-client error frame does not - and pairing and starting are both watched from
  /// two places by design. The state is deliberately left alone: a room being busy is
  /// a fact about somebody else's application, not a failure of this one, and
  /// <see cref="HueConnectionState.Unpaired"/> carrying the pairing-window message set
  /// the precedent that prose does not imply a fault.
  /// </summary>
  public void Note(string? message) {
    Update(connection => connection with { Message = message });
  }

  /// <summary>
  /// Re-reads only the areas, from the bridge Flux is already talking to.
  ///
  /// The cheap half of <see cref="RefreshAsync"/>, and the difference is worth a
  /// method rather than a parameter. A full refresh re-discovers first, which is a
  /// multi-second mDNS sweep, and it passes through
  /// <see cref="HueConnectionState.Connecting"/> on the way - so using it to check one
  /// flag costs five seconds and takes the whole bridge screen down while it runs.
  /// This asks the address it already has and leaves <c>State</c> alone.
  /// </summary>
  /// <returns>False when there is nothing to ask, or the bridge did not answer.</returns>
  public async Task<bool> TryRefreshAreasAsync(CancellationToken cancellationToken) {
    if(!_options.Value.Enabled) {
      return false;
    }

    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      if(Connection.Bridge is not { } bridge || _credentials.Load() is not { } credentials) {
        return false;
      }

      IReadOnlyList<HueArea> areas = await _client.GetAreasAsync(bridge, credentials, cancellationToken).ConfigureAwait(false);

      Guid? remembered = Connection.SelectedAreaId;
      Guid? selected = areas.Any(area => area.Id == remembered) ? remembered : null;

      // The note goes with the reading it was about: a refusal that named a busy room
      // must not still be on screen beside a fresh list showing that room as free.
      Update(connection => connection with { Areas = areas, SelectedAreaId = selected, Message = null });
      return true;
    } catch(HueBridgeException e) {
      // Not a failure of the connection, so the state is deliberately left where it
      // was: a bridge that missed one request is still the paired bridge, and moving
      // the screen to Failed over it would be louder than the event.
      _logger.LogDebug(e, "The bridge did not answer a room refresh.");
      return false;
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
  ///
  /// A countdown in flight is ended first, and then this waits its turn behind every
  /// other bridge command, both of which it used to do neither of. The failure that
  /// closes is a real sequence rather than a theoretical one: a connection that failed
  /// keeps its bridge on screen, so the forget button is offered while somebody is
  /// re-pairing from a phone. Cancelling nothing left the poll running with the gate
  /// held, and the link press it was waiting for then wrote the key straight back and
  /// drove the state to Ready - a forget silently undone, by a button somebody pressed
  /// on purpose.
  /// </summary>
  public async Task ForgetAsync(CancellationToken cancellationToken) {
    lock(_pairingGate) {
      _pairing?.Cancel();
    }

    await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
    try {
      _credentials.Clear();

      lock(_state) {
        // The latency offset and the light settings survive, and they are the only
        // things here that should. One describes how long light takes to reach the room
        // and the other describes how the room is meant to look, and neither is a fact
        // about the bridge - so re-pairing after a network change must not silently
        // discard a number somebody tuned by eye or a palette they picked for the night.
        HueSettings previous = _settings.Load();
        _settings.Save(HueSettings.Empty with { LatencyOffsetMs = previous.LatencyOffsetMs, Light = previous.Light });
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
    } finally {
      _gate.Release();
    }
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

      // Written on success only, so the hint is always an address that worked.
      lock(_state) {
        HueSettings stored = _settings.Load();
        if(stored.LastBridgeAddress != bridge.Address) {
          _settings.Save(stored with { LastBridgeAddress = bridge.Address });
        }
      }

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

    if(found.FirstOrDefault(bridge => string.Equals(bridge.Id, bridgeId, StringComparison.OrdinalIgnoreCase)) is { } located) {
      return located;
    }

    // Discovery found nothing, which on an mDNS-hostile network is the normal state
    // once the cloud lookup's fifteen-minute budget is spent. The last working address
    // is tried before giving up - and verified rather than assumed, so a bridge that
    // has genuinely moved is still reported as missing instead of Flux talking to
    // whatever now holds that lease.
    if(_settings.Load().LastBridgeAddress is not { Length: > 0 } hint) {
      return null;
    }

    if(await _client.TryDescribeAsync(hint, cancellationToken).ConfigureAwait(false) is not { } remembered
      || !string.Equals(remembered.Id, bridgeId, StringComparison.OrdinalIgnoreCase)) {
      return null;
    }

    _logger.LogInformation("No bridge answered discovery. The paired bridge was still at its last address, {Address}.", hint);
    Update(connection => connection with { Discovered = [remembered] });

    return remembered;
  }

  private void Fail(HueBridgeException e) {
    _logger.LogWarning(e, "The bridge conversation failed.");
    Update(connection => connection with { State = HueConnectionState.Failed, PairingSecondsRemaining = null, Message = e.Message });
  }

  private void Update(Func<HueConnection, HueConnection> change) {
    // Read, compared, written and announced under one lock, and re-entrant on purpose:
    // the setters take it around a settings write as well, so their file and their
    // broadcast are the same change. Without it the read-modify-write below is two
    // threads' worth of "take the picture, edit one field, put it back", and the second
    // one puts back a picture taken before the first one existed.
    lock(_state) {
      HueConnection updated = change(_connection);
      if(updated == _connection) {
        // A poll that changed nothing must not wake every connected page. The record
        // compares field by field, so this catches the scalar no-ops - the same area
        // chosen twice, a countdown redrawn on the same second. It deliberately does
        // not catch a re-discovered list with identical contents: those are compared by
        // reference, and re-announcing them costs one frame on a screen somebody is
        // already watching.
        return;
      }

      Volatile.Write(ref _connection, updated);

      // Announced inside the lock rather than after it, so pages receive the changes in
      // the order they happened. Two threads leaving the lock and then racing to raise
      // the event can deliver the older picture last, and every screen keeps it. The
      // handlers this reaches encode a frame and drop it into a bounded queue - none of
      // them waits on anything, which is what makes holding the lock across them safe.
      Changed?.Invoke(updated);
    }
  }
}
