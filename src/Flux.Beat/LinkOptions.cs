namespace Flux.Beat;

/// <summary>
/// Configuration for the Ableton Link listener. Every default here is the value
/// measured or reasoned about in Phase 0; the settings exist as escape hatches
/// for a machine that behaves differently, not as knobs to tune.
/// </summary>
public sealed class LinkOptions {
  public const string SectionName = "Link";

  /// <summary>Link's multicast group. Fixed by the protocol.</summary>
  public string MulticastGroup { get; set; } = "224.76.78.75";

  /// <summary>Link's port. Fixed by the protocol.</summary>
  public int Port { get; set; } = 20808;

  /// <summary>
  /// The local interface to join the multicast group on, or null to pick one
  /// automatically.
  ///
  /// Exactly one interface, never all of them: joining on several delivers every
  /// ALIVE datagram once per interface, which halved the measured packet
  /// interval during Phase 0 and would corrupt any rate derived from arrivals.
  /// </summary>
  public string? InterfaceAddress { get; set; }

  /// <summary>
  /// The local address to bind the socket to. The wildcard is deliberate and
  /// was verified alongside a running rekordbox: Link peers all share this port
  /// through <c>SO_REUSEADDR</c>, and a wildcard bind is how they coexist.
  ///
  /// This is the one UDP socket that does <em>not</em> follow the
  /// bind-the-specific-adapter rule from the findings. That rule solves a
  /// different problem - the administered Windows port exclusion on 50000-50059,
  /// which does not apply here.
  /// </summary>
  public string BindAddress { get; set; } = "0.0.0.0";

  /// <summary>
  /// How long without an ALIVE packet before the grid is reported stale.
  ///
  /// rekordbox republishes roughly every 250 ms, so this is ten missed packets -
  /// long enough not to flicker on a scheduling hiccup, short enough that
  /// switching LINK off is visible on the phone within a breath.
  /// </summary>
  public TimeSpan StaleAfter { get; set; } = TimeSpan.FromSeconds(2.5);

  /// <summary>
  /// Beats per bar. Only ever used to turn a beat number into a 1-2-3-4
  /// readout; Link transmits no meter, so this is Flux's own interpretation and
  /// nothing on the wire contradicts or confirms it.
  /// </summary>
  public int Quantum { get; set; } = 4;

  /// <summary>
  /// Microseconds added to Link's time origin to reach the local clock.
  ///
  /// Zero on this machine, because rekordbox and Flux share a clock. Non-zero
  /// only matters if Flux ever listens from a different PC, where the honest fix
  /// is the <c>mep4</c> measurement exchange rather than a hand-dialled number.
  /// </summary>
  public double ClockOffsetMicros { get; set; }
}
