using Flux.Beat;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Windows.Devices.Enumeration;
using Windows.Devices.Midi;
using Windows.Storage.Streams;

namespace Flux.Midi;

/// <summary>
/// Watches the controller's MIDI input and turns a cue press on an audible deck
/// into a downbeat.
///
/// Read-only, and that is a design rule rather than an implementation detail:
/// nothing here ever sends to the device, so observation cannot disturb what
/// rekordbox is doing with the same controller. Phase 0 proved the two can share it,
/// which is the only reason this exists.
///
/// It is a hint and never a sync source. It publishes no timeline and has no opinion
/// about tempo - it says where a bar starts, which is a statement about the music in
/// the room rather than one source's estimate, and that is why
/// <see cref="SyncSourceManager.AlignBar"/> is reachable without being the active
/// source. The hint is also inherently incomplete: a deck on the hidden layer emits
/// nothing at all, so Flux may never see the cue press for the track that is
/// actually playing. Audio detection is the authority; this arrives early and often
/// enough to be worth having anyway.
///
/// This is the only file in the project that names a WinRT type, and everything it
/// decides lives in <see cref="FlxMessageParser"/> and <see cref="FlxDeckState"/>
/// instead. What is left here is a port that has to be found, opened, and reopened,
/// which needs a controller on the desk to verify and cannot be usefully tested
/// without one.
/// </summary>
internal sealed class FlxObserver: BackgroundService {
  /// <summary>
  /// How often the device list is re-read, which is also how long a freshly plugged
  /// controller waits to be noticed.
  ///
  /// One loop rather than a DeviceWatcher plus a separate retry path, which is a
  /// deviation from the plan and the simpler arrangement. Finding the device,
  /// opening it, failing to open it, and noticing it has gone are four states of one
  /// question, and <see cref="MidiInPort.FromIdAsync"/> forces a retry loop anyway
  /// by answering null rather than throwing when the port is unavailable. Two
  /// mechanisms would each half-answer it, and the failure that produces - a device
  /// reported as connected with no port open behind it - is worse than a few
  /// seconds' delay on a hotplug.
  /// </summary>
  private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

  private readonly SyncSourceManager sources;
  private readonly MidiOptions options;
  private readonly ILogger<FlxObserver> log;

  /// <summary>
  /// Guards the parser, the deck state and the warning flags. The port raises its
  /// messages on a thread pool thread and is not documented to serialise them, and
  /// the parser's 14-bit latch is exactly the kind of state that gives a wrong
  /// answer rather than an exception when two threads reach it at once.
  /// </summary>
  private readonly object gate = new object();

  private MidiInPort? port;
  private string? openDeviceId;

  /// <summary>
  /// Rebuilt on every open, deliberately. A fader can be moved while the controller
  /// is unplugged, so carrying the old positions across a reconnect would answer
  /// "audible" from a measurement that is no longer about anything. Starting from
  /// nothing known is both true and the safe direction.
  /// </summary>
  private FlxMessageParser parser = new FlxMessageParser();
  private FlxDeckState decks = new FlxDeckState();
  private bool[] announcedUnseenFader = new bool[FlxConstants.DeckCount];

  private bool announcedMissing;
  private bool announcedRefusal;
  private bool announcedFailure;

  public FlxObserver(SyncSourceManager sources, IOptions<MidiOptions> options, ILogger<FlxObserver> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.sources = sources ?? throw new ArgumentNullException(nameof(sources));
    this.options = options.Value;
    this.log = log ?? throw new ArgumentNullException(nameof(log));
  }

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    if(!this.options.Enabled) {
      this.log.LogInformation("MIDI observation is off (Midi:Enabled). A cue press will not anchor the bar.");
      return;
    }

    if(string.IsNullOrEmpty(this.options.PreferDeviceNamed)) {
      this.log.LogInformation(
        "No MIDI device preference is set (Midi:PreferDeviceNamed), so no controller is watched.");
      return;
    }

    this.log.LogInformation(
      "Watching for a MIDI input matching '{Preference}'. A cue press on an audible deck will anchor the bar.",
      this.options.PreferDeviceNamed);

    try {
      while(!stoppingToken.IsCancellationRequested) {
        // Every pass is guarded, and this is the Phase 1 lesson rather than
        // defensiveness: a hosted service runs synchronously up to its first await,
        // so an exception from the device enumeration - a machine with no MIDI stack
        // at all - would otherwise fault host startup. Flux launching without a cue
        // press is strictly better than Flux not launching.
        try {
          await this.PollAsync(stoppingToken).ConfigureAwait(false);
        } catch(OperationCanceledException) {
          break;
        } catch(Exception ex) {
          this.Close();

          if(!this.announcedFailure) {
            this.announcedFailure = true;
            this.log.LogWarning(
              ex,
              "MIDI observation hit an error and will keep retrying every {Seconds:F0} s. Cue presses will not anchor the bar until it recovers.",
              PollInterval.TotalSeconds);
          }
        }

        await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    } finally {
      this.Close();
    }
  }

  /// <summary>
  /// Reconciles what is plugged in with what is open: finds the preferred device,
  /// opens it if it is not open, and lets go of it if it has gone.
  /// </summary>
  private async Task PollAsync(CancellationToken cancellationToken) {
    DeviceInformationCollection inputs = await DeviceInformation
      .FindAllAsync(MidiInPort.GetDeviceSelector())
      .AsTask(cancellationToken)
      .ConfigureAwait(false);

    DeviceInformation? match = inputs.FirstOrDefault(input =>
      input.Name.Contains(this.options.PreferDeviceNamed, StringComparison.OrdinalIgnoreCase));

    this.announcedFailure = false;

    if(this.openDeviceId is string open) {
      // Still the same device, still open. The port raises no event when its device
      // disappears, so re-reading the list is how an unplugged controller is
      // noticed at all.
      if(string.Equals(match?.Id, open, StringComparison.Ordinal)) {
        return;
      }

      this.log.LogInformation("The MIDI controller is gone. Watching for it to come back.");
      this.Close();
    }

    if(match is null) {
      if(!this.announcedMissing) {
        this.announcedMissing = true;
        this.log.LogInformation(
          "No MIDI input matching '{Preference}' is present. Watching for one.",
          this.options.PreferDeviceNamed);
      }

      return;
    }

    this.announcedMissing = false;

    MidiInPort? opened;
    Exception? refusal = null;

    try {
      opened = await MidiInPort.FromIdAsync(match.Id).AsTask(cancellationToken).ConfigureAwait(false);
    } catch(Exception ex) when(ex is not OperationCanceledException) {
      // A device id read a moment ago can be gone by the time it is opened - the
      // controller was unplugged between the two awaits - and that arrives as an
      // exception rather than as the null below. Both mean the same thing to this
      // loop, so both take the same path: the next pass will find the device list
      // has changed and stop looking for it. Treating it as a failure instead would
      // put a stack trace in the log for unplugging a USB cable.
      opened = null;
      refusal = ex;
    }

    // Null rather than an exception is how WinRT reports a port it cannot hand over,
    // and on this machine that is usually transient rather than a refusal: Windows
    // MIDI Services shares the device between clients, but a port being torn down
    // and rebuilt by another application is a real few hundred milliseconds. Retried
    // rather than given up on, and announced once so a genuine refusal is visible
    // without a log line every three seconds behind it.
    if(opened is null) {
      if(!this.announcedRefusal) {
        this.announcedRefusal = true;
        this.log.LogInformation(
          "'{Device}' is present but its MIDI port could not be opened yet. Retrying every {Seconds:F0} s.",
          match.Name,
          PollInterval.TotalSeconds);
      }

      if(refusal is not null) {
        this.log.LogDebug(refusal, "Opening the MIDI port for '{Device}' threw.", match.Name);
      }

      return;
    }

    this.announcedRefusal = false;

    lock(this.gate) {
      this.parser = new FlxMessageParser();
      this.decks = new FlxDeckState();
      this.announcedUnseenFader = new bool[FlxConstants.DeckCount];
    }

    // Subscribed after the state above is fresh, which is the ordering that matters:
    // the first message of this connection is then judged against this connection
    // rather than against whatever the last one left behind. The handler reads
    // nothing that is assigned below it, so a message arriving between these three
    // lines is handled correctly rather than tolerated.
    opened.MessageReceived += this.OnMessageReceived;
    this.port = opened;
    this.openDeviceId = match.Id;

    this.log.LogInformation("Watching '{Device}' for cue presses.", match.Name);
  }

  /// <summary>
  /// Judges one MIDI message.
  ///
  /// Runs on a thread pool thread, once per message, for every jog-wheel tick the
  /// controller produces - about ninety messages a second with a hand on the device.
  /// The parser throws nearly all of them away, and it is worth keeping that the
  /// cheap path here.
  /// </summary>
  private void OnMessageReceived(MidiInPort sender, MidiMessageReceivedEventArgs args) {
    byte[] raw = ReadBytes(args.Message.RawData);

    int deck;
    DeckAudibility audibility;
    bool? displayed;
    bool announce;

    lock(this.gate) {
      if(this.parser.Parse(raw) is not FlxEvent observed) {
        return;
      }

      this.decks.Apply(observed);

      if(observed.Kind == FlxEventKind.FaderMoved) {
        // The fader has now been seen, so the explanation below is worth giving
        // again if this deck ever goes back to having no known position.
        this.announcedUnseenFader[observed.Deck - 1] = false;
        return;
      }

      if(observed.Kind != FlxEventKind.CuePressed) {
        return;
      }

      deck = observed.Deck;
      audibility = this.decks.Audibility(deck);
      displayed = this.decks.IsOnDisplayedLayer(deck);

      announce = audibility == DeckAudibility.Unknown && !this.announcedUnseenFader[deck - 1];
      if(announce) {
        this.announcedUnseenFader[deck - 1] = true;
      }
    }

    // Outside the lock: aligning the bar reaches into the grid, and holding a lock
    // taken by the MIDI callback across that would put this thread's stalls into
    // somebody else's path.
    switch(audibility) {
      case DeckAudibility.Audible:
        if(this.sources.AlignBar(BarAlignmentMethod.Cue)) {
          this.log.LogInformation(
            "Cue on deck {Deck} anchored the bar. (Displayed layer: {Displayed}.)",
            deck,
            Describe(displayed));
        } else {
          // No timeline exists yet, so there is no beat to call the downbeat. Worth
          // a line, because pressing cue and seeing nothing happen is otherwise
          // indistinguishable from the two refusals below.
          this.log.LogInformation(
            "Cue on deck {Deck} arrived before any source had a tempo, so there was no beat to anchor to.",
            deck);
        }

        break;

      case DeckAudibility.Unknown:
        // Announced once per deck, because the state is genuinely confusing: the
        // fader may well be up, Flux simply has not seen it move and refuses to
        // assume. Nudging it resolves this permanently.
        if(announce) {
          this.log.LogInformation(
            "Cue on deck {Deck} was ignored: its channel fader has not been seen yet, so Flux cannot tell whether the deck is audible. Move that fader once.",
            deck);
        }

        break;

      case DeckAudibility.Silent:
        // The normal case, and deliberately quiet. A DJ previewing the incoming
        // track in headphones presses cue repeatedly on a deck with the fader down,
        // and this is precisely the gesture the audibility test exists to discard.
        this.log.LogDebug("Cue on deck {Deck} was ignored: its channel fader is down.", deck);
        break;

      default:
        break;
    }
  }

  /// <summary>
  /// Copies a WinRT buffer into bytes the parser can read.
  ///
  /// A copy per message, which is the price of keeping every decision in a type that
  /// takes bytes and can therefore be tested without a controller. At this message
  /// rate it does not register.
  /// </summary>
  private static byte[] ReadBytes(IBuffer buffer) {
    byte[] bytes = new byte[buffer.Length];

    using(DataReader reader = DataReader.FromBuffer(buffer)) {
      reader.ReadBytes(bytes);
    }

    return bytes;
  }

  private static string Describe(bool? displayed) {
    return displayed switch {
      true => "yes",
      false => "no",
      null => "not said",
    };
  }

  /// <summary>
  /// Lets go of the port. Unsubscribing first, so a message already in flight cannot
  /// reach a handler whose state is being replaced.
  /// </summary>
  private void Close() {
    MidiInPort? closing = this.port;

    this.port = null;
    this.openDeviceId = null;

    if(closing is null) {
      return;
    }

    closing.MessageReceived -= this.OnMessageReceived;
    closing.Dispose();
  }
}
