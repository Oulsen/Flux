using Flux.Audio;

namespace Flux.Web.Server;

/// <summary>
/// The one authority on how the stage looks, and the only writer of the file it lives
/// in.
///
/// One instance, like the light engine and the bridge service, and for the same reason:
/// two would hold two disagreeing pictures of one setting and a page would show
/// whichever answered. It is deliberately not a hosted service - it opens nothing and
/// starts nothing, it only remembers.
///
/// It is also <see cref="ISpectrumBands"/>, which is what lets a slider in a browser
/// reach the analyzer. That is a second interface on one object rather than a second
/// object, because the alternative is a copy of the band count kept in step with this
/// one, and a setting that exists in two places is a setting that will eventually
/// disagree with itself.
/// </summary>
public sealed class StageService: ISpectrumBands {
  private readonly IStageSettingsStore store;
  private readonly object gate = new();

  private StageSettings settings;

  public StageService(IStageSettingsStore store) {
    this.store = store ?? throw new ArgumentNullException(nameof(store));

    // Loaded once at construction rather than on each read. The analyzer asks for the
    // band count sixty times a second, and a synchronous file read on that thread would
    // be the slowest thing in the pipeline by a wide margin.
    this.settings = store.Load();
  }

  /// <summary>
  /// Raised after a change has been applied, so the socket layer can tell every page.
  ///
  /// Every page rather than the one that asked: this is shared state now, and a phone
  /// that changed the shape has to see the laptop's copy follow it. That is the same
  /// argument the light settings already run on.
  /// </summary>
  public event Action<StageSettings>? Changed;

  /// <summary>The look in force.</summary>
  public StageSettings Settings {
    get {
      lock(this.gate) {
        return this.settings;
      }
    }
  }

  /// <inheritdoc/>
  public int Count => this.Settings.Bands;

  /// <summary>
  /// Applies a complete look, or refuses it.
  /// </summary>
  /// <returns>
  /// False when any field is one this build cannot act on, in which case nothing
  /// changed. A partial application is not on offer: the message is one statement of
  /// how the stage should look, so half of it is not a look.
  /// </returns>
  public bool TrySet(StageSettings requested) {
    ArgumentNullException.ThrowIfNull(requested);

    if(!requested.IsValid) {
      return false;
    }

    lock(this.gate) {
      if(this.settings == requested) {
        // Nothing to write and nothing to announce. Worth checking rather than being
        // idempotent by luck: a slider drag arrives ten times a second and each one
        // would otherwise be a file write and a broadcast to every open page.
        return true;
      }

      this.settings = requested;

      // Written the instant it is accepted rather than at shutdown, because Flux is
      // closed by killing a console window as often as not. The same argument the audio
      // device store already runs on.
      this.store.Save(requested);
    }

    // Outside the lock: a handler broadcasts to every client, and holding a lock across
    // that would put the analyzer's next band-count read behind a socket write.
    this.Changed?.Invoke(requested);

    return true;
  }
}
