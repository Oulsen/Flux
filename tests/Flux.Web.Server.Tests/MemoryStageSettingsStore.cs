namespace Flux.Web.Server.Tests;

/// <summary>
/// A stage settings store that remembers only for as long as the test does.
///
/// Shared rather than one per fixture because <see cref="StageService"/> loads at
/// construction: every rig that builds a hub needs one, and a test that wrote to
/// <c>%APPDATA%</c> would leave a real user's chosen look behind it.
/// </summary>
internal sealed class MemoryStageSettingsStore: IStageSettingsStore {
  private StageSettings settings = StageSettings.Default;

  /// <summary>How many times a save reached the store, so a test can assert it did not.</summary>
  internal int Saves { get; private set; }

  public StageSettings Load() {
    return this.settings;
  }

  public void Save(StageSettings settings) {
    this.settings = settings;
    this.Saves++;
  }
}
