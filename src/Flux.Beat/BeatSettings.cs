using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Flux.Beat;

/// <summary>
/// The beat choices a user made, as opposed to the ones an operator configured.
/// </summary>
/// <param name="Source">
/// The sync source last selected from the page, or null while nobody has selected one.
///
/// Stored for the reason the audio device is, and the failure it closes was found the
/// same way - by using it. <c>Beat:DefaultSource</c> is the right answer for the machine
/// it was written for and has no way of learning, so a restart put the grid back on Link;
/// Link publishes a timeline and nothing else, so with rekordbox closed there were no
/// beats, and a room whose lights sit perfectly still looks like a broken light engine
/// rather than like a source that was never selected. The configuration key stays as the
/// answer for a machine that has never been told otherwise.
/// </param>
public sealed record BeatSettings(string? Source) {
  /// <summary>Nothing chosen yet.</summary>
  public static BeatSettings Empty { get; } = new BeatSettings(Source: null);
}

/// <summary>
/// Where the beat choices live between runs.
///
/// Its own file beside the audio and Hue ones rather than a shared "settings", on the
/// argument that already put those two apart: they belong to different projects, and a
/// shared record would make three projects depend on each other to store three strings.
/// </summary>
public interface IBeatSettingsStore {
  /// <summary>The stored settings, or <see cref="BeatSettings.Empty"/> when there are none.</summary>
  BeatSettings Load();

  /// <summary>
  /// Stores the settings, replacing anything already there.
  ///
  /// Never throws. It is called from the path that answers a user choosing a source, and
  /// a full disk must cost them the memory of the choice rather than the choice.
  /// </summary>
  void Save(BeatSettings settings);
}

/// <summary>Plain JSON in the same per-user folder as everything else Flux remembers.</summary>
internal sealed class JsonBeatSettingsStore: IBeatSettingsStore {
  private const string FileName = "beat-settings.json";

  private static readonly JsonSerializerOptions Formatted = new JsonSerializerOptions { WriteIndented = true };

  private readonly string directory;
  private readonly ILogger<JsonBeatSettingsStore> logger;
  private readonly object gate = new();

  public JsonBeatSettingsStore(string directory, ILogger<JsonBeatSettingsStore> logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(logger);

    this.directory = directory;
    this.logger = logger;
  }

  private string Path => System.IO.Path.Combine(this.directory, FileName);

  public BeatSettings Load() {
    lock(this.gate) {
      if(!File.Exists(this.Path)) {
        return BeatSettings.Empty;
      }

      try {
        return JsonSerializer.Deserialize<BeatSettings>(File.ReadAllText(this.Path), Formatted) ?? BeatSettings.Empty;
      } catch(Exception e) when(e is JsonException or IOException) {
        // A preference nobody can read is not worth failing a launch over: the cost is
        // one forgotten choice and a fall back to the configured default, which is
        // exactly where a machine that had never been told otherwise would start.
        this.logger.LogWarning(e, "Beat settings at {Path} could not be read. Starting with none.", this.Path);
        return BeatSettings.Empty;
      }
    }
  }

  public void Save(BeatSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);

    lock(this.gate) {
      try {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(this.Path, JsonSerializer.Serialize(settings, Formatted));
      } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
        // Swallowed, like the audio device and unlike the Hue keys. This is saved as a
        // side effect of changing source mid-set, and failing there would turn a full
        // disk into a source selection that appears to have been refused.
        this.logger.LogWarning(e, "The chosen sync source could not be remembered in {Path}.", this.Path);
      }
    }
  }
}
