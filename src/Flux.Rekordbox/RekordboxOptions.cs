namespace Flux.Rekordbox;

/// <summary>
/// Whether Flux reads rekordbox at all.
///
/// On by default, on the argument the FLX-10 observer makes: reading is read-only and
/// costs the DJ nothing, and a rekordbox that is not running, or is a version Flux has
/// no chains for, is simply a source with nothing to say. The setting exists to rule
/// it out in one line when the bar keeps moving and somebody needs to know why.
/// </summary>
public sealed class RekordboxOptions {
  public const string SectionName = "Rekordbox";

  public bool Enabled { get; set; } = true;
}
