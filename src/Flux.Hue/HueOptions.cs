namespace Flux.Hue;

/// <summary>
/// Which bridge Flux talks to, and how hard it drives it.
///
/// Every setting here has a working default, because the bridge is discovered
/// rather than configured: a DJ who has paired once should never have to open a
/// settings file. What the keys exist for is the case discovery cannot cover - a
/// bridge on another subnet, an mDNS-hostile network, or a rig where the answer
/// is already known and waiting five seconds for it is five seconds wasted.
/// </summary>
public sealed class HueOptions {
  public const string SectionName = "Hue";

  /// <summary>
  /// Whether Flux talks to a bridge at all.
  ///
  /// Worth a setting for the same reason the MIDI observer has one: a build with
  /// lights switched off and a build that cannot find a bridge look identical from
  /// the outside, and ruling one out in a single line is worth more than the line
  /// costs. It also gives the beat lab back, unchanged, for the sessions where the
  /// question is the grid rather than the show.
  /// </summary>
  public bool Enabled { get; set; } = true;

  /// <summary>
  /// The bridge's address, when discovery should not be asked.
  ///
  /// Null means discover, which is the intended path - <b>never hardcode a bridge
  /// address</b>, because DHCP moves it and a pinned address fails on the one night
  /// the router was rebooted. This exists as the last resort behind mDNS and the
  /// cloud lookup, not as the normal way in.
  /// </summary>
  public string? BridgeAddress { get; set; }

  /// <summary>
  /// How long to listen for bridges before answering with what was found.
  ///
  /// mDNS answers arrive over time rather than at once, so this is a
  /// completeness-versus-patience trade and not a timeout in the failure sense: a
  /// bridge that answers late is missed, not broken. Five seconds is long enough
  /// for a quiet LAN and short enough that the pairing screen does not look hung.
  /// </summary>
  public TimeSpan DiscoveryTimeout { get; set; } = TimeSpan.FromSeconds(5);

  /// <summary>
  /// Frames a second sent to the bridge while streaming.
  ///
  /// 50 is the official guidance and the default for that reason. It is not the
  /// rate a bulb renders: the bridge forwards over Zigbee at roughly 25 updates a
  /// second per light and drops the excess, so the two numbers are different layers
  /// and both are correct. Lowering this does not make the lights smoother.
  /// </summary>
  public int StreamHz { get; set; } = 50;

  /// <summary>
  /// Milliseconds to shift light changes against the beat, as the session starts.
  ///
  /// The light path runs 60 to 140 ms behind the music, so a change sent the instant
  /// a beat lands is seen late; a positive offset sends it early to compensate. This
  /// is the starting value only - the page owns it from there, because the honest
  /// unit of adjustment for it is somebody watching the room rather than a rebuild.
  /// </summary>
  public int LatencyOffsetMs { get; set; }
}
