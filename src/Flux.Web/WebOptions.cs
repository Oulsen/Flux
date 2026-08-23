namespace Flux.Web;

/// <summary>
/// Configuration for the web layer: where it listens, how often it talks, and
/// how much it is willing to queue for a client that has stopped keeping up.
/// </summary>
public sealed class WebOptions {
  public const string SectionName = "Web";

  /// <summary>
  /// The address Kestrel binds. The wildcard is the point rather than a
  /// convenience: the phone remote is a requirement, and a loopback-only bind
  /// serves the desktop and nothing else.
  ///
  /// It also means Flux is reachable by anything on the same network, with no
  /// authentication - which is the documented v1 position for a LAN lighting
  /// app, and the reason this port must never be forwarded.
  /// </summary>
  public string BindAddress { get; set; } = "0.0.0.0";

  /// <summary>
  /// The HTTP port. Short enough to type into a phone by hand before the QR code
  /// arrives in Phase 5.
  ///
  /// Not 5040, which was the first choice and is wrong: Windows' Connected
  /// Devices Platform service listens there on a stock Windows 11, so Flux would
  /// have failed to start out of the box on the machine it is built for.
  /// </summary>
  public int Port { get; set; } = 5050;

  /// <summary>
  /// How often the full state snapshot goes out, in hertz.
  ///
  /// This is the readout rate, not the beat rate: beats are their own message,
  /// sent at the instant they land, precisely because 20 Hz cannot place one
  /// closer than 50 ms. Raising this makes the BPM digits smoother and nothing
  /// else.
  /// </summary>
  public int StateHz { get; set; } = 20;

  /// <summary>
  /// How many messages may queue for one client before the oldest are dropped.
  ///
  /// A little over a second of state at the default rate. The queue exists so a
  /// phone on bad Wi-Fi degrades on its own connection instead of anywhere else;
  /// see "Backpressure" in <c>docs/protocol.md</c> for why dropping the oldest
  /// is the right end to drop from.
  /// </summary>
  public int ClientQueueCapacity { get; set; } = 32;
}
