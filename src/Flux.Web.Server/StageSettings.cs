using Flux.Audio;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flux.Web.Server;

/// <summary>Which way the stage builds its picture.</summary>
public enum StageLayout {
  /// <summary>
  /// Bars grow up from the floor across the full width. Flux's own, and the default.
  ///
  /// It shows every band once and gives a bar the whole height to travel, both of which
  /// <see cref="Mirrored"/> halves.
  /// </summary>
  Upward,

  /// <summary>
  /// Bars grow outward from the centre, each centred on the horizontal axis.
  ///
  /// The reference scene's arrangement. There it is not only a look: that canvas *is*
  /// the device, sampled per LED by position, so mirroring is how a symmetric strip ends
  /// up looking symmetric. Flux drives bulbs from an entertainment configuration with
  /// real coordinates, so none of that carries over and this is taste.
  /// </summary>
  Mirrored,
}

/// <summary>The shape of the bars. The reference scene's six.</summary>
public enum StageShape {
  /// <summary>A bar filling its slot bar the gap.</summary>
  Bars,

  /// <summary>Half a slot, for an airier picture.</summary>
  ThinBars,

  /// <summary>Two slots, so the bars overlap and read as one moving mass.</summary>
  ChonkerBars,

  /// <summary>A short cap at each tip rather than the bar under it.</summary>
  Pixel,

  /// <summary>One outline traced through every tip.</summary>
  Smooth,

  /// <summary>The same outline through every third tip.</summary>
  SmoothSimple,
}

/// <summary>
/// Where the stage's colour comes from.
///
/// Only <see cref="RandomBeat"/> is a claim about the room. The other four are screen
/// embellishments the lights have no equivalent of, which is exactly why they are
/// allowed to be computed in a browser: the standing rule for this phase is that
/// anything a bulb also does is decided on the server and read off the wire.
/// </summary>
public enum StageColor {
  /// <summary>The colour the bridge was sent, read back from <c>state.light</c>.</summary>
  RandomBeat,

  /// <summary>One hue for the whole picture, travelling on a clock.</summary>
  HueCycle,

  /// <summary>A rainbow across the frequency axis, sweeping.</summary>
  HueWave,

  /// <summary>The same rainbow down the picture instead of across it.</summary>
  HueWaveVert,

  /// <summary>Two of the page's own colours, fixed.</summary>
  Static,
}

/// <summary>
/// How the stage draws, as one complete statement of it.
///
/// On the server rather than in each browser's storage, which reverses where this
/// started. The argument for keeping it local was that whether a phone draws a canvas
/// is a fact about the phone - and that still holds for the one switch that turns the
/// stage off entirely, which is why that one stayed in the browser. It does not hold
/// for the rest: the look of the stage is part of the show, it is set by whoever is
/// standing at the page, and it should be the same look on the laptop and on the phone
/// in somebody's hand. <see cref="Bands"/> settles it on its own, since it decides what
/// the analyzer produces and cannot be a per-screen preference at all.
/// </summary>
/// <param name="Layout">Upward or mirrored.</param>
/// <param name="Shape">Which of the six bar shapes.</param>
/// <param name="Color">Where the colour comes from.</param>
/// <param name="Bands">
/// How many display columns the analyzer produces, inside
/// <see cref="SpectrumBands"/>' bounds.
/// </param>
/// <param name="TrailMs">
/// How long the picture trails behind itself, in milliseconds, from 0 to
/// <see cref="MaxTrailMs"/>, where 0 is off and every frame starts clean. Off by default,
/// as the scene shipped its fade.
///
/// The trail is an afterimage: the last few frames of the spectrum drawn again under the
/// live one, each fainter, falling to a tenth over this long - the threshold
/// <see cref="Flux.Hue.LightSettings.PulseMs"/> already names, so the number on the slider
/// is how long the trail visibly lasts.
///
/// A departure from the scene on two counts, both settled by looking at the stage. The
/// scene smeared pixels - it repainted the background at partial opacity - and an 8-bit
/// canvas cannot quite fade a faint smear to the background, so its long end left a haze
/// of old bars; an afterimage is drawn from the spectrum and is simply not drawn once it
/// has run out. And the scene counted its fade per frame, so one setting trailed twice as
/// long on a 60 Hz display as on a 120 Hz one; this is counted in time. Screen only: in
/// the scene the canvas was the device and the fade smeared the LEDs too, but Flux's
/// lights have an envelope of their own, and <see cref="Flux.Hue.LightSettings.PulseMs"/>
/// is what decides how long a light trails a beat.
/// </param>
public sealed record StageSettings(
  StageLayout Layout,
  StageShape Shape,
  StageColor Color,
  int Bands,
  int TrailMs = 0) {
  /// <summary>
  /// The longest trail, in milliseconds.
  ///
  /// A second, chosen by looking rather than carried across. The scene's own top end
  /// works out to nearly ten seconds at 60 Hz, which on this stage turned every bar into
  /// one smear of paint; past a second a trail stops reading as the motion of a bar and
  /// starts reading as a fog over the picture.
  /// </summary>
  public const int MaxTrailMs = 1000;

  /// <summary>
  /// The look a fresh install opens on.
  ///
  /// Upward and 64 bands are argued where they are declared. `RandomBeat` is the default
  /// colour because the whole reason the engine's colour is on the wire is that the
  /// screen should show what the bulbs were sent: a page that opened on a hue cycle would
  /// open showing something the room is demonstrably not doing.
  /// </summary>
  public static StageSettings Default { get; } = new StageSettings(
    StageLayout.Upward,
    StageShape.Bars,
    StageColor.RandomBeat,
    SpectrumBands.Default);

  /// <summary>Whether every field is one this build can act on.</summary>
  public bool IsValid =>
    Enum.IsDefined(this.Layout)
    && Enum.IsDefined(this.Shape)
    && Enum.IsDefined(this.Color)
    && SpectrumBands.IsValid(this.Bands)
    && this.TrailMs >= 0
    && this.TrailMs <= MaxTrailMs;
}

/// <summary>
/// Where the stage's look lives between runs.
///
/// Its own file rather than a section of the Hue settings, because the two are about
/// different things and one of them exists without a bridge: <c>Hue:Enabled</c> false is
/// a supported configuration, and a beat lab still has a stage.
/// </summary>
public interface IStageSettingsStore {
  /// <summary>The stored look, or <see cref="StageSettings.Default"/> when there is none.</summary>
  StageSettings Load();

  /// <summary>Stores the look, replacing anything already there.</summary>
  void Save(StageSettings settings);
}

/// <summary>Plain JSON in the same per-user folder as the other settings.</summary>
internal sealed class JsonStageSettingsStore: IStageSettingsStore {
  private const string FileName = "stage-settings.json";

  /// <summary>
  /// Indented, and enums by name rather than by number.
  ///
  /// The naming is not cosmetic, for the reason the Hue store gives: a shape stored as
  /// <c>2</c> means "whichever member happens to sit at index two", so inserting one into
  /// the middle of the enum would silently restyle a choice somebody had made, with no
  /// compile error and no runtime error. By name it survives reordering, and the file
  /// says <c>"chonkerBars"</c> to whoever opens it.
  /// </summary>
  private static readonly JsonSerializerOptions Formatted = new JsonSerializerOptions {
    WriteIndented = true,
    Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
  };

  private readonly string directory;
  private readonly ILogger<JsonStageSettingsStore> logger;
  private readonly object gate = new();

  public JsonStageSettingsStore(string directory, ILogger<JsonStageSettingsStore> logger) {
    ArgumentException.ThrowIfNullOrWhiteSpace(directory);
    ArgumentNullException.ThrowIfNull(logger);

    this.directory = directory;
    this.logger = logger;
  }

  private string Path => System.IO.Path.Combine(this.directory, FileName);

  public StageSettings Load() {
    lock(this.gate) {
      if(!File.Exists(this.Path)) {
        return StageSettings.Default;
      }

      try {
        StageSettings? stored = JsonSerializer.Deserialize<StageSettings>(File.ReadAllText(this.Path), Formatted);

        // Validated on the way in as well as on the way over the socket. A file is the
        // one route into this record that no client message passed through, and a hand
        // edited count outside the bounds would reach the analyzer.
        return stored is { IsValid: true } ? stored : StageSettings.Default;
      } catch(Exception e) when(e is JsonException or IOException) {
        // A preference nobody can read is not worth failing a launch over: the cost is
        // one forgotten choice, against an application that will not start because of a
        // file somebody hand-edited.
        this.logger.LogWarning(e, "Stage settings at {Path} could not be read. Starting with the defaults.", this.Path);
        return StageSettings.Default;
      }
    }
  }

  public void Save(StageSettings settings) {
    ArgumentNullException.ThrowIfNull(settings);

    lock(this.gate) {
      Directory.CreateDirectory(this.directory);
      File.WriteAllText(this.Path, JsonSerializer.Serialize(settings, Formatted));
    }
  }
}
