using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Beat;

/// <summary>One registered source, and what it last had to say.</summary>
/// <param name="Name">The source's protocol name.</param>
/// <param name="Status">Its own view of its health, asked for just now.</param>
/// <param name="IsActive">Whether the grid is currently listening to it.</param>
public readonly record struct SyncSourceReport(string Name, SyncSourceStatus Status, bool IsActive);

/// <summary>
/// Holds the registered sync sources and forwards exactly one of them to the
/// <see cref="BeatGrid"/>.
///
/// This class exists so that "which source is driving the lights?" has one
/// answer in one place. Every source publishes all the time; only the active
/// one's timelines reach the grid. The alternative - sources knowing whether
/// they are active and staying quiet if not - spreads the same decision across
/// every source that will ever be written, and makes switching a race between
/// two of them.
///
/// The last timeline from every source is kept, active or not. That is what
/// makes switching feel instant: a manual tempo set two minutes ago is still
/// there when Manual is selected, where waiting for the source to work its
/// tempo out again would mean a dark room for as long as that takes.
/// </summary>
public sealed class SyncSourceManager {
  private readonly BeatGrid grid;
  /// <summary>
  /// The source to start on: the one last chosen from the page, or
  /// <c>Beat:DefaultSource</c> where nobody has ever chosen.
  ///
  /// Resolved once at construction rather than read on each registration, because
  /// sources register from their own constructors and a file read on that path would be
  /// one per source for an answer that cannot change between them.
  /// </summary>
  private readonly string preferredSource;

  /// <summary><c>Beat:DefaultSource</c>, which is what a preference nothing answers falls back to.</summary>
  private readonly string defaultSource;

  private readonly IBeatSettingsStore? settings;
  private readonly ILogger<SyncSourceManager> log;
  private readonly object gate = new object();

  /// <summary>
  /// Held across a whole selection, so two pages choosing at once finish in the order
  /// they chose.
  ///
  /// Separate from <see cref="gate"/> because a selection writes a file and applies a
  /// timeline, and holding the lock sources publish through across either would put
  /// every beat behind a disk write. Only another selection waits on this one.
  /// </summary>
  private readonly object selectGate = new object();

  private readonly Dictionary<string, Registration> sources =
    new Dictionary<string, Registration>(StringComparer.OrdinalIgnoreCase);

  private string? active;

  /// <param name="grid">The grid every source publishes into.</param>
  /// <param name="options">Carries <c>Beat:DefaultSource</c>, the answer for a machine nobody has told otherwise.</param>
  /// <param name="log">Where source changes are announced.</param>
  /// <param name="settings">
  /// Where the last chosen source is remembered, or null in a host that remembers
  /// nothing.
  ///
  /// Nullable and deliberately not optional, on the argument the light engine's spectrum
  /// already makes: a default here would let a construction site forget to pass one and
  /// produce a build that silently stops remembering, which presents as the configured
  /// default quietly winning every restart - the exact fault this closes.
  /// </param>
  public SyncSourceManager(
    BeatGrid grid,
    IOptions<BeatOptions> options,
    IBeatSettingsStore? settings,
    ILogger<SyncSourceManager> log) {
    ArgumentNullException.ThrowIfNull(options);

    this.grid = grid ?? throw new ArgumentNullException(nameof(grid));
    this.settings = settings;
    this.log = log ?? throw new ArgumentNullException(nameof(log));

    string? remembered = settings?.Load().Source;

    this.defaultSource = options.Value.DefaultSource;
    this.preferredSource = string.IsNullOrWhiteSpace(remembered) ? this.defaultSource : remembered;
  }

  /// <summary>
  /// The source the grid is listening to, or null before any source registers.
  /// </summary>
  public string? ActiveSource {
    get {
      lock(this.gate) {
        return this.active;
      }
    }
  }

  /// <summary>
  /// Adds a source and hands back the sink it publishes through.
  ///
  /// The preferred source - the one last chosen from the page, or
  /// <c>Beat:DefaultSource</c> where nobody has chosen - becomes active as soon as it
  /// registers, whenever that happens to be. Until then the first source to
  /// register holds the position, so a build without it still starts with something
  /// selected rather than with nothing. Neither case touches the grid: nothing has
  /// published yet, so there is no timeline to swap.
  ///
  /// Sources call this from their own constructors, so <paramref name="source"/>
  /// is not fully constructed yet. That is safe by contract rather than by luck:
  /// <see cref="ISyncSource.Status"/> may be called at any moment, so it must
  /// only read fields, and a source whose fields are still at their defaults has
  /// to report <see cref="SyncSourceState.Waiting"/> - which is the truth about a
  /// source that has not started.
  /// </summary>
  /// <exception cref="InvalidOperationException">Two sources claim the same name.</exception>
  public ITimelineSink Register(ISyncSource source) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentException.ThrowIfNullOrWhiteSpace(source.Name);

    lock(this.gate) {
      if(!this.sources.TryAdd(source.Name, new Registration(source))) {
        throw new InvalidOperationException(
          $"A sync source named '{source.Name}' is already registered. Source names are the protocol's own and must be unique.");
      }

      // Three claims in descending order, settled without knowing what registers next.
      // The preference outranks the default outright; the default is taken only while
      // the preference has not been met, so a remembered source that registers after the
      // configured one still takes the position back; and anything at all beats nothing,
      // which is what keeps a build that has neither from starting on no source.
      bool preferred = string.Equals(source.Name, this.preferredSource, StringComparison.OrdinalIgnoreCase);
      bool configured = string.Equals(source.Name, this.defaultSource, StringComparison.OrdinalIgnoreCase);
      bool preferenceMet = this.active is not null
        && string.Equals(this.active, this.preferredSource, StringComparison.OrdinalIgnoreCase);

      if(this.active is null || preferred || (configured && !preferenceMet)) {
        this.active = source.Name;
      }
    }

    return new Sink(this, source.Name);
  }

  /// <summary>
  /// Makes <paramref name="name"/> the active source.
  ///
  /// Called from the socket receive loop, so it does its own work under a short
  /// lock and touches the grid outside it.
  /// </summary>
  /// <returns>False when no source by that name is registered.</returns>
  public bool TrySelect(string name) {
    if(string.IsNullOrWhiteSpace(name)) {
      return false;
    }

    // The file and the grid are both written outside the source lock, so without this a
    // second page's choice could land in between and leave the file, or the grid,
    // remembering the choice that lost.
    lock(this.selectGate) {
      BeatTimeline? cached;
      string resolved;

      lock(this.gate) {
        if(!this.sources.TryGetValue(name, out Registration? registration)) {
          return false;
        }

        resolved = registration.Source.Name;
        if(string.Equals(this.active, resolved, StringComparison.OrdinalIgnoreCase)) {
          return true;
        }

        this.active = resolved;
        cached = registration.Last;
      }

      // Outside the source lock, because this writes a file and that lock is on every
      // publish. Saved on a change and not on every call, which the early return
      // above already guarantees: re-selecting the source that is already active is not a
      // choice and should not cost a write.
      this.settings?.Save(new BeatSettings(resolved));

      // A different source's numbering is a different numbering, even when the two
      // tempi match, so the switch is announced as a rebasing exactly like a Link
      // session change. The grid carries the bar anchor across by the shift, so the
      // downbeat stays where it was in the music rather than at the number it had.
      if(cached is BeatTimeline timeline) {
        this.grid.Apply(timeline, newSession: true);
        this.log.LogInformation("Sync source is now {Source}, at {Bpm:F2} BPM.", resolved, timeline.Bpm);
      } else {
        // Keeping the outgoing source's timeline would leave its tempo on screen
        // under the new source's name, which is the one reading a beat display must
        // never invent. Reporting nothing is the honest answer until this source
        // publishes.
        this.grid.Reset();
        this.log.LogInformation("Sync source is now {Source}, which has nothing to report yet.", resolved);
      }

      return true;
    }
  }

  /// <summary>
  /// Marks the beat nearest to now as the downbeat.
  ///
  /// Public because the callers are not all sources: the FLX-10 observer supplies
  /// a cue press without ever publishing a timeline, and the web layer forwards a
  /// tap. Alignment belongs to the grid rather than to whoever is timing it.
  /// </summary>
  /// <param name="method">What supplied the downbeat, for the UI to show.</param>
  /// <param name="snapToBar">
  /// Whether this is a guess that defers to a bar already known, which is the drop
  /// detector and nothing else - see <see cref="BeatGrid.TryAlignBar"/>.
  /// </param>
  /// <returns>False when no timeline is known yet, so there is no beat to anchor to.</returns>
  public bool AlignBar(BarAlignmentMethod method, bool snapToBar = false) {
    return this.grid.TryAlignBar(method, snapToBar);
  }

  /// <summary>
  /// Every registered source and its current health, for the source selector.
  /// </summary>
  public IReadOnlyList<SyncSourceReport> Sources() {
    ISyncSource[] registered;
    string? current;

    lock(this.gate) {
      registered = [.. this.sources.Values.Select(registration => registration.Source)];
      current = this.active;
    }

    // Status() is called outside the lock: it is a source's own method, on the
    // source's own state, and holding this lock across it would let a slow source
    // block a tempo change on a different one.
    return [.. registered.Select(source => new SyncSourceReport(
      source.Name,
      source.Status(),
      string.Equals(source.Name, current, StringComparison.OrdinalIgnoreCase)))];
  }

  /// <summary>
  /// Caches a source's timeline, and forwards it to the grid if that source is
  /// the active one.
  /// </summary>
  private void Publish(string name, BeatTimeline timeline, bool newSession) {
    bool forward;

    lock(this.gate) {
      if(!this.sources.TryGetValue(name, out Registration? registration)) {
        return;
      }

      registration.Last = timeline;
      forward = string.Equals(this.active, name, StringComparison.OrdinalIgnoreCase);
    }

    // Outside the lock, because BeatGrid.Apply raises TempoChanged to its
    // subscribers synchronously and one of those could reasonably want to ask
    // this manager something. Two sources publishing at once can therefore reach
    // the grid out of order, which costs nothing: the grid re-anchors on every
    // timeline rather than integrating them, so the next update corrects it.
    if(forward) {
      this.grid.Apply(timeline, newSession);
    }
  }

  /// <summary>
  /// Drops a source's cached timeline, and clears the grid if that source is the
  /// active one.
  /// </summary>
  private void Withdraw(string name) {
    bool clear;

    lock(this.gate) {
      if(!this.sources.TryGetValue(name, out Registration? registration) || registration.Last is null) {
        return;
      }

      registration.Last = null;
      clear = string.Equals(this.active, name, StringComparison.OrdinalIgnoreCase);
    }

    if(clear) {
      // Reset rather than leaving the last timeline running: the source has
      // retracted its tempo, so continuing to extrapolate it would put a number
      // on stage that nothing behind it stands behind any more.
      this.grid.Reset();
      this.log.LogInformation("Sync source {Source} has withdrawn its tempo.", name);
    }
  }

  private sealed class Registration(ISyncSource source) {
    public ISyncSource Source { get; } = source;

    /// <summary>
    /// The last timeline this source published, active or not. Guarded by the
    /// manager's lock.
    /// </summary>
    public BeatTimeline? Last { get; set; }
  }

  /// <summary>
  /// A source's own handle on the manager. It carries the source's name so the
  /// source never has to state it again, and so a source cannot publish on
  /// another one's behalf.
  /// </summary>
  private sealed class Sink(SyncSourceManager manager, string name): ITimelineSink {
    public void Publish(BeatTimeline timeline, bool newSession = false) {
      manager.Publish(name, timeline, newSession);
    }

    public void Withdraw() {
      manager.Withdraw(name);
    }

    public void AlignBar(BarAlignmentMethod method, bool snapToBar = false) {
      manager.AlignBar(method, snapToBar);
    }
  }
}
