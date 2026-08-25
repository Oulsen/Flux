using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Flux.Beat;

/// <summary>
/// Listens for Ableton Link ALIVE datagrams and publishes the timeline as the
/// <c>link</c> sync source.
///
/// Passive by design: Flux joins the multicast group, reads, and transmits
/// nothing at all. It never announces itself, so it is invisible to the session
/// and can never become tempo master - the right trade for a lighting app, and
/// the reason it costs the DJ nothing to leave running.
///
/// Two things it does insist on. It follows <em>one</em> Link session rather than
/// whichever datagram arrived last, because a second session on the LAN is a
/// normal thing to meet at a venue. And it keeps trying: a bind that fails, a
/// network that is not up yet, an adapter that flaps mid-set - none of those end
/// the listener, and none of them take the host process down.
/// </summary>
public sealed class AbletonLinkListener: BackgroundService, ISyncSource {
  /// <summary>Big enough for any Link datagram; the observed ALIVE packet is 107 bytes.</summary>
  private const int ReceiveBufferBytes = 2048;

  private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);

  /// <summary>First wait after a cycle that never managed to join.</summary>
  private static readonly TimeSpan MinRetryDelay = TimeSpan.FromSeconds(1);

  /// <summary>
  /// Longest wait between attempts. A capped backoff, not an escalating one: the
  /// usual cause is a network still coming up, and half a minute is as long as a
  /// DJ should ever wait for Flux to notice it arrived.
  /// </summary>
  private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long the packet count may stand still before the membership is assumed
  /// lost and the group re-joined.
  /// </summary>
  private static readonly TimeSpan RejoinAfterSilence = TimeSpan.FromSeconds(30);

  /// <summary>
  /// How long the followed session may go quiet before another one may take over.
  ///
  /// Hysteresis, deliberately longer than the grid's own staleness window: the
  /// followed peer briefly dropping a few packets must not hand the light show to
  /// an unrelated session, and a session that really has gone away costs five
  /// seconds of extrapolation before the replacement is adopted.
  /// </summary>
  private static readonly TimeSpan SessionHandoverAfter = TimeSpan.FromSeconds(5);

  /// <summary>This source's name in the protocol.</summary>
  public const string SourceName = "link";

  private readonly ITimelineSink sink;
  private readonly IMonotonicClock clock;
  private readonly LinkOptions options;
  private readonly double staleAfterMicros;
  private readonly ILogger<AbletonLinkListener> log;

  /// <summary>
  /// Guards the fields the receive loop writes and <see cref="Status"/> reads.
  /// Two different threads at four and twenty hertz respectively, so the lock is
  /// never contended and the alternative - volatile reads of a double - would
  /// only be harder to read.
  /// </summary>
  private readonly object statusGate = new object();

  /// <summary>
  /// Cancels the bind-join-receive cycle currently running, so a network change
  /// or the watchdog can force a fresh join. Null between cycles.
  /// </summary>
  private CancellationTokenSource? membership;

  /// <summary>
  /// The session last published, kept across receive cycles: a timeline from a
  /// different session is a wholesale renumbering, not a correction, and the grid
  /// must be told which one it is getting. Cycle-local state cannot answer that
  /// after a rejoin.
  /// </summary>
  private ulong? lastPublishedSession;

  /// <summary>
  /// Packets this listener has accepted, counted here rather than read back from
  /// the grid.
  ///
  /// That distinction is load-bearing now that the grid only hears the active
  /// source. Reading the grid's count would leave this listener's own watchdog
  /// blind whenever Link is not selected: the count would sit still, the watchdog
  /// would read that as a lost multicast membership, and it would rejoin the group
  /// every thirty seconds to fix a problem that does not exist. A source has to be
  /// able to report its own health while something else drives the lights, which
  /// is also exactly what the source selector needs from it.
  /// </summary>
  private long acceptedPackets;

  private double lastAcceptedMicros;

  private BeatTimeline? lastPublishedTimeline;

  public AbletonLinkListener(
    SyncSourceManager sources,
    IMonotonicClock clock,
    IOptions<LinkOptions> options,
    IOptions<BeatOptions> beat,
    ILogger<AbletonLinkListener> log) {
    ArgumentNullException.ThrowIfNull(sources);
    ArgumentNullException.ThrowIfNull(options);
    ArgumentNullException.ThrowIfNull(beat);

    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    this.options = options.Value;
    this.staleAfterMicros = beat.Value.StaleAfter.TotalMilliseconds * 1000.0;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    // Registered last, so every field this object needs is set before the manager
    // can hand the reference to anyone. Status() reads only fields and answers
    // Waiting from their defaults, so even a call arriving mid-construction gets
    // the truth about a listener that has not started.
    this.sink = sources.Register(this);
  }

  /// <inheritdoc/>
  public string Name => SourceName;

  /// <summary>
  /// What this listener knows, whether or not it is the active source.
  ///
  /// Confidence is 1 the moment a single packet has been accepted, and that is not
  /// laziness. Link <em>transmits</em> the tempo rather than estimating it, so
  /// there is no uncertainty here to describe - the honest signal about a session
  /// that has stopped publishing is the Quiet state, not a tempo reported as
  /// half-believed. Audio detection is where confidence earns its keep.
  /// </summary>
  public SyncSourceStatus Status() {
    lock(this.statusGate) {
      if(this.acceptedPackets == 0) {
        return new SyncSourceStatus(SyncSourceState.Waiting, Confidence: 0);
      }

      bool quiet = this.clock.NowMicros - this.lastAcceptedMicros > this.staleAfterMicros;

      return new SyncSourceStatus(
        quiet ? SyncSourceState.Quiet : SyncSourceState.Live,
        Confidence: 1);
    }
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // Nothing above the first await may throw. BackgroundService hands the task
    // this method returns straight back to StartAsync, so an exception thrown
    // before it suspends faults host startup - and "rekordbox already holds the
    // port" would stop Flux from launching at all, which is strictly worse than
    // launching with no Link source.
    if(!IPAddress.TryParse(this.options.BindAddress, out IPAddress? bindTo)
      || !IPAddress.TryParse(this.options.MulticastGroup, out IPAddress? group)) {
      this.log.LogError(
        "Link:BindAddress ('{Bind}') and Link:MulticastGroup ('{Group}') must both be IP addresses. No Link source.",
        this.options.BindAddress,
        this.options.MulticastGroup);
      return;
    }

    bool watchingNetwork = false;
    try {
      NetworkChange.NetworkAddressChanged += this.OnNetworkAddressChanged;
      watchingNetwork = true;
    } catch(NetworkInformationException ex) {
      this.log.LogWarning(ex, "Could not subscribe to network changes. Link will only re-join on the watchdog's schedule.");
    }

    try {
      await this.ListenUntilStoppedAsync(bindTo, group, stoppingToken).ConfigureAwait(false);
    } finally {
      if(watchingNetwork) {
        NetworkChange.NetworkAddressChanged -= this.OnNetworkAddressChanged;
      }
    }
  }

  /// <summary>
  /// Runs bind-join-receive cycles until the host stops.
  ///
  /// The retry is the whole point. A single attempt at startup means a laptop
  /// that joins the venue Wi-Fi thirty seconds after Flux launches never sees
  /// Link at all, and the watchdog then blames rekordbox for it.
  /// </summary>
  private async Task ListenUntilStoppedAsync(IPAddress bindTo, IPAddress group, CancellationToken stoppingToken) {
    TimeSpan retryDelay = MinRetryDelay;

    while(!stoppingToken.IsCancellationRequested) {
      bool joined = await this.ListenOnceAsync(bindTo, group, stoppingToken).ConfigureAwait(false);
      if(stoppingToken.IsCancellationRequested) {
        return;
      }

      // A cycle that joined and then ended was ended on purpose, by a network
      // change or by the watchdog, so it re-joins promptly. Only a cycle that
      // never got off the ground backs off.
      retryDelay = joined ? MinRetryDelay : Slower(retryDelay);

      try {
        await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
      } catch(OperationCanceledException) {
        return;
      }
    }
  }

  /// <summary>
  /// One bind-join-receive cycle.
  /// </summary>
  /// <returns>
  /// True when the group was joined, which is what separates "try again now"
  /// from "back off and try later".
  /// </returns>
  private async Task<bool> ListenOnceAsync(IPAddress bindTo, IPAddress group, CancellationToken stoppingToken) {
    using CancellationTokenSource cycle = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
    using Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    NetworkCandidate joined;
    try {
      // Every Link peer on the machine shares this port through SO_REUSEADDR -
      // it is how rekordbox and Flux listen at the same time, verified in Phase 0.
      socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
      socket.Bind(new IPEndPoint(bindTo, this.options.Port));

      NetworkCandidate? chosen = this.Join(socket, group);
      if(chosen is null) {
        this.log.LogError("Could not join {Group} on any IPv4 interface. Retrying until the network returns.", group);
        return false;
      }

      joined = chosen;
    } catch(SocketException ex) {
      this.log.LogError(
        ex,
        "Could not open the Ableton Link socket on {Bind}:{Port} ({Error}). Retrying.",
        bindTo,
        this.options.Port,
        ex.SocketErrorCode);
      return false;
    }

    this.log.LogInformation(
      "Listening for Ableton Link on {Group}:{Port} via {Interface} ({Address}).",
      group,
      this.options.Port,
      joined.Name,
      joined.Address);

    this.membership = cycle;
    Task watchdog = this.WatchAsync(joined, cycle);

    try {
      await this.ReceiveAsync(socket, cycle.Token).ConfigureAwait(false);
      return true;
    } finally {
      this.membership = null;
      await cycle.CancelAsync().ConfigureAwait(false);
      await watchdog.ConfigureAwait(false);
    }
  }

  /// <summary>
  /// Reports every tempo this session publishes, including the first.
  ///
  /// Driven by the listener's own decode rather than by
  /// <see cref="BeatGrid.TempoChanged"/>, which it used to subscribe to. The grid
  /// now hears whichever source is active, so a line saying "Link tempo" would
  /// eventually be printed for an audio tempo change - a log that names the wrong
  /// source is worse than no log, because it is believed.
  ///
  /// The raw microseconds-per-beat is logged next to the BPM deliberately. When
  /// the displayed tempo looks wrong or looks stuck, the question is always
  /// whether the wire value moved and Flux mishandled it, or whether the wire
  /// value never moved at all - and only the number off the packet separates
  /// those two.
  /// </summary>
  private void LogTempo(BeatTimeline timeline, double numberingShift) {
    // A fractional shift is just the two timelines disagreeing about the current
    // instant by a rounding error. Whole beats mean the numbering was rebased,
    // which is the part worth putting in the log.
    //
    // Formatted invariantly, because ILogger renders its own parameters that way
    // and a locale-formatted fragment spliced in beside them produces a single
    // line carrying two different decimal separators.
    string renumbering = Math.Abs(numberingShift) >= 0.5
      ? string.Create(
        CultureInfo.InvariantCulture,
        $" Beat numbering shifted by {numberingShift:F1} beats.")
      : string.Empty;

    this.log.LogInformation(
      "Link tempo is now {Bpm:F2} BPM ({MicrosPerBeat} us/beat).{Renumbering}",
      timeline.Bpm,
      timeline.MicrosPerBeat,
      renumbering);
  }

  /// <summary>
  /// Re-joins the group whenever a local address changes.
  ///
  /// Multicast membership is per interface, so an adapter that drops, returns or
  /// changes address leaves the socket waiting on a group it is no longer in.
  /// Nothing surfaces from that: no error, no packets, just a silence that reads
  /// exactly like LINK being switched off at the other end.
  /// </summary>
  private void OnNetworkAddressChanged(object? sender, EventArgs e) {
    CancellationTokenSource? current = this.membership;
    if(current is null) {
      return;
    }

    this.log.LogInformation("A local network address changed. Re-joining the Ableton Link group.");

    try {
      current.Cancel();
    } catch(ObjectDisposedException) {
      // The cycle finished on its own between reading the field and cancelling
      // it. The outer loop is already on its way to a fresh join.
    }
  }

  /// <summary>
  /// Joins the group on exactly one interface: the configured one when given,
  /// otherwise the best-ranked candidate.
  /// </summary>
  private NetworkCandidate? Join(Socket socket, IPAddress group) {
    IReadOnlyList<NetworkCandidate> candidates = MulticastInterfaces.Candidates();
    NetworkCandidate? chosen;

    if(this.options.InterfaceAddress is { Length: > 0 } requested) {
      chosen = candidates.FirstOrDefault(candidate => candidate.Address.ToString() == requested);

      if(chosen is null) {
        this.log.LogWarning(
          "Configured Link interface {Requested} is not present. Falling back to automatic selection.",
          requested);
        chosen = MulticastInterfaces.Preferred(candidates);
      }
    } else {
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

  /// <summary>
  /// Reads datagrams until the cycle ends, feeding the grid from one session.
  ///
  /// Following a single session is not hardening against an attacker so much as
  /// against a normal Friday night. Two Link sessions on one network - a second
  /// DJ, a phone app that never joined, a soundcheck laptop - each publish their
  /// own timeline, and adopting whichever arrived last makes the grid flip-flop
  /// between two unrelated tempi at 4 Hz. Worse, the tempi differ, so every swap
  /// is booked as a tempo change and the re-anchor drift metric stays at zero
  /// while the beat is visibly wrong.
  /// </summary>
  private async Task ReceiveAsync(Socket socket, CancellationToken cycleToken) {
    byte[] buffer = new byte[ReceiveBufferBytes];
    EndPoint sender = new IPEndPoint(IPAddress.Any, 0);

    ulong? following = null;
    double lastFollowedMicros = 0;
    bool warnedAboutRival = false;

    while(!cycleToken.IsCancellationRequested) {
      SocketReceiveFromResult received;
      try {
        received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, sender, cycleToken).ConfigureAwait(false);
      } catch(OperationCanceledException) {
        return;
      } catch(SocketException ex) {
        // A transient ICMP-driven error must not end the session: the next
        // datagram is 250 ms away and the grid keeps extrapolating meanwhile.
        this.log.LogWarning(ex, "Link receive failed ({Error}). Continuing.", ex.SocketErrorCode);
        continue;
      }

      if(!TryDecode(buffer, received.ReceivedBytes, this.options.ClockOffsetMicros,
        out BeatTimeline timeline, out ulong? announced)) {
        continue;
      }

      // A datagram carrying no session chunk cannot be told apart from the
      // followed peer's, so it is credited to the session already being followed
      // rather than treated as a rival.
      ulong session = announced ?? following ?? 0;
      double nowMicros = this.clock.NowMicros;

      if(following is ulong current && session != current) {
        double quietMicros = nowMicros - lastFollowedMicros;
        if(quietMicros < SessionHandoverAfter.TotalMilliseconds * 1000.0) {
          if(!warnedAboutRival) {
            this.log.LogWarning(
              "A second Ableton Link session ({Rival:X16} from {Peer}) is on this network. Ignoring it and staying with {Session:X16}.",
              session,
              received.RemoteEndPoint,
              current);
            warnedAboutRival = true;
          }

          continue;
        }

        this.log.LogInformation(
          "Ableton Link session {Session:X16} went quiet. Following {Rival:X16} from {Peer} instead.",
          current,
          session,
          received.RemoteEndPoint);
        warnedAboutRival = false;
      } else if(following is null) {
        this.log.LogInformation(
          "Following Ableton Link session {Session:X16} from {Peer}.",
          session,
          received.RemoteEndPoint);
      }

      following = session;
      lastFollowedMicros = nowMicros;

      // Only a datagram that names its session can prove the session changed; an
      // anonymous one is already credited to the session being followed above.
      bool sessionChanged = announced is ulong id
        && this.lastPublishedSession is ulong last
        && id != last;

      BeatTimeline? previous;
      lock(this.statusGate) {
        previous = this.lastPublishedTimeline;
        this.acceptedPackets++;
        this.lastAcceptedMicros = nowMicros;
        this.lastPublishedTimeline = timeline;
      }

      if(announced is ulong applied) {
        this.lastPublishedSession = applied;
      }

      if(previous is not BeatTimeline earlier
        || earlier.MicrosPerBeat != timeline.MicrosPerBeat
        || sessionChanged) {
        this.LogTempo(
          timeline,
          previous is BeatTimeline was ? timeline.BeatAt(nowMicros) - was.BeatAt(nowMicros) : 0);
      }

      this.sink.Publish(timeline, sessionChanged);
    }
  }

  /// <summary>
  /// Decodes a received datagram. Separate from the receive loop so the span
  /// never has to share a scope with an await.
  /// </summary>
  private static bool TryDecode(
    byte[] buffer,
    int length,
    double clockOffsetMicros,
    out BeatTimeline timeline,
    out ulong? sessionId) {
    ReadOnlySpan<byte> datagram = buffer.AsSpan(0, length);
    sessionId = null;

    if(!LinkPacket.TryParseTimeline(datagram, clockOffsetMicros, out timeline)) {
      return false;
    }

    if(LinkPacket.TryParseSessionId(datagram, out ulong parsed)) {
      sessionId = parsed;
    }

    return true;
  }

  /// <summary>
  /// Says out loud when nothing is arriving, and re-joins when nothing has
  /// arrived for a long time.
  ///
  /// Silence has causes that look identical from here - LINK switched off in
  /// rekordbox, the group joined on the wrong adapter, or a membership quietly
  /// lost to an adapter reset that changed no address at all. The warning names
  /// the first two; only re-joining can rule out the third, and it is cheap
  /// enough to be worth doing on spec.
  /// </summary>
  private async Task WatchAsync(NetworkCandidate joined, CancellationTokenSource cycle) {
    using PeriodicTimer timer = new PeriodicTimer(WatchdogInterval);

    long lastPackets = -1;
    TimeSpan silence = TimeSpan.Zero;

    try {
      while(await timer.WaitForNextTickAsync(cycle.Token).ConfigureAwait(false)) {
        // This listener's own counters, not the grid's. The grid only hears the
        // active source, so reading it here would make the watchdog fire on a
        // perfectly healthy session that simply is not the selected one.
        SyncSourceStatus status = this.Status();

        long packets;
        double bpm;
        lock(this.statusGate) {
          packets = this.acceptedPackets;
          bpm = this.lastPublishedTimeline?.Bpm ?? 0;
        }

        if(packets == lastPackets) {
          silence += WatchdogInterval;
        } else {
          lastPackets = packets;
          silence = TimeSpan.Zero;
        }

        if(status.State == SyncSourceState.Waiting) {
          this.log.LogWarning(
            "No Ableton Link packets yet on {Address}. Is LINK enabled in rekordbox?{Alternatives}",
            joined.Address,
            DescribeAlternatives(joined));
        } else if(status.State == SyncSourceState.Quiet) {
          // Says "last published" rather than "still extrapolating", because
          // whether anything is extrapolating this tempo depends on whether Link
          // is the selected source, which this listener deliberately cannot see.
          this.log.LogWarning(
            "Ableton Link has gone quiet after {Packets} packets. Its last published tempo was {Bpm:F2} BPM.",
            packets,
            bpm);
        }

        if(silence >= RejoinAfterSilence) {
          this.log.LogInformation(
            "Nothing received on {Address} for {Seconds:F0} s. Re-joining the Ableton Link group.",
            joined.Address,
            silence.TotalSeconds);

          await cycle.CancelAsync().ConfigureAwait(false);
          return;
        }
      }
    } catch(OperationCanceledException) {
      // The cycle ended.
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

  private static TimeSpan Slower(TimeSpan delay) {
    TimeSpan doubled = delay * 2;
    return doubled > MaxRetryDelay ? MaxRetryDelay : doubled;
  }
}
