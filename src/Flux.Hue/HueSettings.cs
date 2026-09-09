using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Flux.Hue;

/// <summary>
/// The choices a user made that are not secrets.
/// </summary>
/// <param name="EntertainmentAreaId">
/// The area lights are streamed to. Null until one is chosen, which is also the
/// state after pairing a bridge that has more than one.
/// </param>
/// <param name="LatencyOffsetMs">
/// The offset the user tuned by eye, or null when they never touched the slider.
///
/// Null rather than zero, because the two mean different things: null defers to
/// <see cref="HueOptions.LatencyOffsetMs"/>, while zero is somebody deciding this rig
/// needs no correction. Collapsing them would make a configured default impossible to
/// change back to.
/// </param>
/// <param name="LastBridgeAddress">
/// Where the paired bridge last answered, kept as a hint and never as the truth.
///
/// Credentials still belong to a bridge id rather than to an address - discovery is
/// asked first, every time, and this is only consulted when it finds nothing. That
/// case is real rather than theoretical: on a network where mDNS is filtered, the only
/// other route is a cloud lookup rate-limited to about one request every fifteen
/// minutes, and a few restarts inside that window leave Flux unable to find a bridge
/// it was talking to moments earlier. The hint is verified before it is used, by
/// asking whatever answers there for its id, so a stale one is discarded rather than
/// believed.
/// </param>
public sealed record HueSettings(Guid? EntertainmentAreaId, int? LatencyOffsetMs = null, string? LastBridgeAddress = null) {
  /// <summary>Nothing chosen yet.</summary>
  public static HueSettings Empty { get; } = new HueSettings(EntertainmentAreaId: null);
}

/// <summary>
/// Where the non-secret Hue choices live between runs.
///
/// Separate from <see cref="ICredentialStore"/> rather than one store holding
/// everything, because the two have opposite requirements: a key must be unreadable
/// to anyone but this Windows account, and a preference should be readable by the
/// person who set it. Merging them would mean either encrypting a chosen area for no
/// reason, or leaving a DTLS key in plain text for a bad one.
/// </summary>
public interface IHueSettingsStore {
  /// <summary>The stored settings, or <see cref="HueSettings.Empty"/> when there are none.</summary>
  HueSettings Load();

  /// <summary>Stores the settings, replacing anything already there.</summary>
  void Save(HueSettings settings);
}

/// <summary>Plain JSON beside the credentials, in the same per-user folder.</summary>
internal sealed class JsonHueSettingsStore: IHueSettingsStore {
  private const string FileName = "hue-settings.json";

  private static readonly JsonSerializerOptions Formatted = new JsonSerializerOptions { WriteIndented = true };

  private readonly string _directory;
  private readonly ILogger<JsonHueSettingsStore> _logger;
  private readonly object _gate = new();

  public JsonHueSettingsStore(string directory, ILogger<JsonHueSettingsStore> logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(logger);

    _directory = directory;
    _logger = logger;
  }

  private string Path => System.IO.Path.Combine(_directory, FileName);

  public HueSettings Load() {
    lock(_gate) {
      if(!File.Exists(Path)) {
        return HueSettings.Empty;
      }

      try {
        return JsonSerializer.Deserialize<HueSettings>(File.ReadAllText(Path)) ?? HueSettings.Empty;
      } catch(Exception e) when(e is JsonException or IOException) {
        // A preference nobody can read is not worth failing a launch over: the cost
        // is one forgotten choice, and the alternative is an application that will
        // not start because of a file somebody hand-edited.
        _logger.LogWarning(e, "Hue settings at {Path} could not be read. Starting with none.", Path);
        return HueSettings.Empty;
      }
    }
  }

  public void Save(HueSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);

    lock(_gate) {
      Directory.CreateDirectory(_directory);
      File.WriteAllText(Path, JsonSerializer.Serialize(settings, Formatted));
    }
  }
}
