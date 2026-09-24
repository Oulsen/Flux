using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Flux.Audio;

/// <summary>
/// The audio choices a user made, as opposed to the ones an operator configured.
/// </summary>
/// <param name="DeviceId">
/// The device last picked from the page, or null while nobody has picked one.
///
/// Stored because the alternative is what Flux did until now: a rig whose capture
/// device is not the one <see cref="AudioOptions.PreferDeviceNamed"/> names has to be
/// re-picked on the page at the start of every session, from a list of a dozen
/// endpoints, in a booth. The configuration keys were never wrong - they are the right
/// answer for the machine they were written for - they simply have no way of learning.
/// </param>
public sealed record AudioSettings(string? DeviceId) {
  /// <summary>Nothing chosen yet.</summary>
  public static AudioSettings Empty { get; } = new AudioSettings(DeviceId: null);
}

/// <summary>
/// Where the audio choices live between runs.
///
/// Its own file beside the Hue one rather than a shared "settings", because the two
/// belong to different projects and a shared record would make <c>Flux.Audio</c> and
/// <c>Flux.Hue</c> depend on each other to store a string and a Guid.
/// </summary>
public interface IAudioSettingsStore {
  /// <summary>The stored settings, or <see cref="AudioSettings.Empty"/> when there are none.</summary>
  AudioSettings Load();

  /// <summary>
  /// Stores the settings, replacing anything already there.
  ///
  /// Never throws. It is called from the path that answers a user picking a device,
  /// and a full disk must cost them the memory of the choice rather than the choice.
  /// </summary>
  void Save(AudioSettings settings);
}

/// <summary>Plain JSON in the same per-user folder as everything else Flux remembers.</summary>
internal sealed class JsonAudioSettingsStore: IAudioSettingsStore {
  private const string FileName = "audio-settings.json";

  private static readonly JsonSerializerOptions Formatted = new JsonSerializerOptions { WriteIndented = true };

  private readonly string directory;
  private readonly ILogger<JsonAudioSettingsStore> logger;
  private readonly object gate = new();

  public JsonAudioSettingsStore(string directory, ILogger<JsonAudioSettingsStore> logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(logger);

    this.directory = directory;
    this.logger = logger;
  }

  private string Path => System.IO.Path.Combine(this.directory, FileName);

  public AudioSettings Load() {
    lock(this.gate) {
      if(!File.Exists(this.Path)) {
        return AudioSettings.Empty;
      }

      try {
        return JsonSerializer.Deserialize<AudioSettings>(File.ReadAllText(this.Path), Formatted) ?? AudioSettings.Empty;
      } catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException) {
        // A preference nobody can read is not worth failing a launch over: the cost is
        // one forgotten choice, and the alternative is an application that will not
        // start because of a file somebody hand-edited.
        this.logger.LogWarning(e, "Audio settings at {Path} could not be read. Starting with none.", this.Path);
        return AudioSettings.Empty;
      }
    }
  }

  public void Save(AudioSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);

    lock(this.gate) {
      try {
        Directory.CreateDirectory(this.directory);
        File.WriteAllText(this.Path, JsonSerializer.Serialize(settings, Formatted));
      } catch(Exception e) when(e is IOException or UnauthorizedAccessException) {
        // Swallowed where the Hue store lets a write throw, and the difference is who
        // is waiting. That one is saved from a deliberate act of setup; this one is
        // saved as a side effect of picking a device mid-set, and failing there would
        // turn a full disk into a device selection that appears to be refused.
        this.logger.LogWarning(e, "The chosen audio device could not be remembered in {Path}.", this.Path);
      }
    }
  }
}
