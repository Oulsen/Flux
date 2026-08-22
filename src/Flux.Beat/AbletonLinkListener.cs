using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Flux.Beat;

/// <summary>
/// Listens for Ableton Link ALIVE datagrams and feeds the timeline into the
/// <see cref="BeatGrid"/>.
///
/// Passive by design: Flux joins the multicast group, reads, and transmits
/// nothing at all. It never announces itself, so it is invisible to the session
/// and can never become tempo master - the right trade for a lighting app, and
/// the reason it costs the DJ nothing to leave running.
/// </summary>
public sealed class AbletonLinkListener: BackgroundService {
  /// <summary>Big enough for any Link datagram; the observed ALIVE packet is 107 bytes.</summary>
  private const int ReceiveBufferBytes = 2048;

  private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);

  private readonly BeatGrid grid;
  private readonly LinkOptions options;
  private readonly ILogger<AbletonLinkListener> log;

  public AbletonLinkListener(BeatGrid grid, IOptions<LinkOptions> options, ILogger<AbletonLinkListener> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
    this.options = options.Value;
    this.log = log ?? throw new ArgumentNullException(nameof(log));
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    // Every Link peer on the machine shares this port through SO_REUSEADDR -
    // it is how rekordbox and Flux listen at the same time, verified in Phase 0.
    socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    socket.Bind(new IPEndPoint(IPAddress.Parse(this.options.BindAddress), this.options.Port));

    IPAddress group = IPAddress.Parse(this.options.MulticastGroup);
    NetworkCandidate? joined = this.Join(socket, group);
    if(joined is null) {
      this.log.LogError(
        "No usable IPv4 interface to join {Group} on. The Link source is unavailable until the network returns.",
        group);
      return;
    }

    this.log.LogInformation(
      "Listening for Ableton Link on {Group}:{Port} via {Interface} ({Address}).",
      group,
      this.options.Port,
      joined.Name,
      joined.Address);

    using CancellationTokenSource watchdogStop = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    Task watchdog = this.WatchAsync(joined, watchdogStop.Token);

    this.grid.TempoChanged += this.OnTempoChanged;

    try {
      await this.ReceiveAsync(socket, stoppingToken).ConfigureAwait(false);
    } finally {
      this.grid.TempoChanged -= this.OnTempoChanged;
      await watchdogStop.CancelAsync().ConfigureAwait(false);
      await watchdog.ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Reports every tempo the session publishes, including the first.
  ///
  /// The raw microseconds-per-beat is logged next to the BPM deliberately. When
  /// the displayed tempo looks wrong or looks stuck, the question is always
  /// whether the wire value moved and Flux mishandled it, or whether the wire
  /// value never moved at all - and only the number off the packet separates
  /// those two.
  /// </summary>
  private void OnTempoChanged(TempoChange change) {
    // A fractional shift is just the two timelines disagreeing about the current
    // instant by a rounding error. Whole beats mean the numbering was rebased,
    // which is the part worth putting in the log.
    //
    // Formatted invariantly, because ILogger renders its own parameters that way
    // and a locale-formatted fragment spliced in beside them produces a single
    // line carrying two different decimal separators.
    string renumbering = Math.Abs(change.BeatNumberingShift) >= 0.5
      ? string.Create(
        CultureInfo.InvariantCulture,
        $" Beat numbering shifted by {change.BeatNumberingShift:F1} beats.")
      : string.Empty;

    this.log.LogInformation(
      "Link tempo is now {Bpm:F2} BPM ({MicrosPerBeat} us/beat).{Renumbering}",
      change.Timeline.Bpm,
      change.Timeline.MicrosPerBeat,
      renumbering);
  }

  /// <summary>
  /// Joins the group on exactly one interface: the configured one when given,
  /// otherwise the best-ranked candidate.
  /// </summary>
  private NetworkCandidate? Join(Socket socket, IPAddress group) {
    IReadOnlyList<NetworkCandidate> candidates = MulticastInterfaces.Candidates();

    NetworkCandidate? chosen = this.options.InterfaceAddress is { Length: > 0 } configured
      ? candidates.FirstOrDefault(candidate => candidate.Address.ToString() == configured)
      : MulticastInterfaces.Preferred(candidates);

    if(this.options.InterfaceAddress is { Length: > 0 } requested && chosen is null) {
      this.log.LogWarning(
        "Configured Link interface {Requested} is not present. Falling back to automatic selection.",
        requested);
      chosen = MulticastInterfaces.Preferred(candidates);
    }

    if(chosen is null) {
      return null;
    }

    try {
      socket.SetSocketOption(
        SocketOptionLevel.IP,
        SocketOptionName.AddMembership,
        new MulticastOption(group, chosen.Address));
      return chosen;
    } catch(SocketException ex) {
      this.log.LogError(ex, "Could not join {Group} on {Address}.", group, chosen.Address);
      return null;
    }
  }

  private async Task ReceiveAsync(Socket socket, CancellationToken stoppingToken) {
    byte[] buffer = new byte[ReceiveBufferBytes];
    EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

    while(!stoppingToken.IsCancellationRequested) {
      SocketReceiveFromResult received;
      try {
        received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, stoppingToken).ConfigureAwait(false);
      } catch(OperationCanceledException) {
        return;
      } catch(SocketException ex) {
        // A transient ICMP-driven error must not end the session: the next
        // datagram is 250 ms away and the grid keeps extrapolating meanwhile.
        this.log.LogWarning(ex, "Link receive failed ({Error}). Continuing.", ex.SocketErrorCode);
        continue;
      }

      if(LinkPacket.TryParseTimeline(
        buffer.AsSpan(0, received.ReceivedBytes),
        this.options.ClockOffsetMicros,
        out LinkTimeline timeline)) {
        this.grid.Apply(timeline);
      }
    }
  }

  /// <summary>
  /// Says out loud when nothing is arriving.
  ///
  /// Silence has two very different causes that look identical from here - LINK
  /// switched off in rekordbox, or the group joined on the wrong adapter - so
  /// the warning names the alternatives rather than leaving someone to guess
  /// which.
  /// </summary>
  private async Task WatchAsync(NetworkCandidate joined, CancellationToken stoppingToken) {
    using PeriodicTimer timer = new PeriodicTimer(WatchdogInterval);

    try {
      while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
        if(!this.grid.TryGetSnapshot(out BeatGridSnapshot snapshot)) {
          this.log.LogWarning(
            "No Ableton Link packets yet on {Address}. Is LINK enabled in rekordbox?{Alternatives}",
            joined.Address,
            DescribeAlternatives(joined));
          continue;
        }

        if(snapshot.IsStale) {
          this.log.LogWarning("Ableton Link has gone quiet after {Packets} packets. Still extrapolating at {Bpm:F2} BPM.",
            snapshot.Packets,
            snapshot.Bpm);
        }
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    }
  }

  /// <summary>
  /// Names the interfaces that were <em>not</em> joined, so the obvious next
  /// thing to try is in the warning itself. Says nothing at all when there is
  /// nothing else to try, rather than offering the current choice back as an
  /// alternative to itself.
  /// </summary>
  private static string DescribeAlternatives(NetworkCandidate joined) {
    string[] others = [.. MulticastInterfaces.Candidates()
      .Where(candidate => !candidate.Address.Equals(joined.Address))
      .Select(candidate => candidate.Address.ToString())];

    return others.Length == 0
      ? string.Empty
      : $" Other interfaces to try via Link:InterfaceAddress: {string.Join(", ", others)}.";
  }
}
