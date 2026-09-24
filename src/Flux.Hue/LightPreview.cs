using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flux.Hue;

/// <summary>Whether a session is open and its loop is rendering the engine itself.</summary>
internal interface IStreamActivity {
  bool IsStreaming { get; }
}

/// <summary>One preview frame: the colour the engine chose, and what each light would have been sent.</summary>
/// <param name="Color">The room's colour before any one light's band is applied, as <see cref="ILightSource.Render"/> returns it.</param>
/// <param name="Channels">One entry per light of the selected room, or per stand-in light when there is none.</param>
public sealed record LightPreviewFrame(HueColor Color, IReadOnlyList<LightChannel> Channels);

/// <summary>
/// The light engine, run for the page while nothing is streaming to a bridge.
///
/// The engine is pulled rather than pushed, and until this existed the only thing that
/// pulled it was the stream: with no session open there was no colour anywhere, so the
/// stage drew a neutral placeholder and every look chosen in the drawer was invisible
/// until the bulbs were on. This pulls it instead whenever the stream does not, at the
/// stream's own rate, so the screen shows what the room would be doing and the settings
/// can be judged with the lights off - or with no bridge at all, since the look is stored
/// on this machine and not on the bridge.
///
/// <b>It hands over rather than competes.</b> Every tick first asks whether a session is
/// rendering, and stops publishing the moment one is; the engine itself serialises the
/// two callers, so the tick that overlaps a session opening waits rather than tearing the
/// engine's state. Two callers taking turns are safe because everything the engine keeps
/// integrates over the time since the last render, whoever made it.
///
/// <b>The room is the selected area as the bridge laid it out</b>, read from the same
/// answer that lists the areas, so where each light listens - and which colour group it
/// falls in above one colour - is what the stream would work out. With no area to read,
/// the preview lights <see cref="StandInLights"/> stand-ins spread evenly across the
/// spectrum instead of none: a colour split needs lights to split, and with none the
/// page could only ever show the first colour of two or three.
/// </summary>
public sealed class LightPreview: BackgroundService {
  /// <summary>
  /// How many lights the preview imagines when there is no room to read.
  ///
  /// Six: enough that three colours get two lights each and a band's shimmer has
  /// neighbours to differ from, few enough that a bar on the stage still reads as one
  /// light's colour rather than a gradient. Only ever drawn, never sent.
  /// </summary>
  internal const int StandInLights = 6;

  private static readonly LightPlacement[] StandIns = [.. Enumerable.Range(0, StandInLights)
    .Select(index => new LightPlacement(index, index / (double)(StandInLights - 1)))];

  private readonly ILightSource lights;
  private readonly IStreamActivity stream;
  private readonly HueBridgeService bridge;
  private readonly IOptions<HueOptions> options;
  private readonly ILogger<LightPreview> logger;

  private LightPreviewFrame? latest;

  /// <summary>The channel list the placements below were worked out from, so they are only worked out again when it changes.</summary>
  private IReadOnlyList<HueChannel>? placedFrom;
  private LightPlacement[] placements = StandIns;

  internal LightPreview(
    ILightSource lights,
    IStreamActivity stream,
    HueBridgeService bridge,
    IOptions<HueOptions> options,
    ILogger<LightPreview> logger) {
    this.lights = lights ?? throw new ArgumentNullException(nameof(lights));
    this.stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this.bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
    this.options = options ?? throw new ArgumentNullException(nameof(options));
    this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
  }

  /// <summary>
  /// The newest preview, or null while a session is streaming - when the stream's own
  /// reading is the one to draw - and before the first tick.
  /// </summary>
  public LightPreviewFrame? Latest => Volatile.Read(ref this.latest);

  protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
    // The stream's rate, so the engine is sampled as finely with the lights off as with
    // them on: its pulse holds its peak for two bridge frames, and a coarser preview would
    // show a flatter envelope than the room would get.
    using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / Math.Max(1, this.options.Value.StreamHz)));

    try {
      while(await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
        this.Tick();
      }
    } catch(OperationCanceledException) {
      // Shutdown.
    } catch(Exception e) {
      // A preview is worth nothing next to the application it decorates: it stops, and
      // the stage falls back to the placeholder it drew before this existed.
      this.logger.LogError(e, "The light preview stopped.");
      Volatile.Write(ref this.latest, null);
    }
  }

  /// <summary>One preview frame, or nothing while a session renders. Internal so a test can step it.</summary>
  internal void Tick() {
    if(this.stream.IsStreaming) {
      Volatile.Write(ref this.latest, null);
      return;
    }

    LightPlacement[] room = this.Room();
    HueColor[] colors = new HueColor[room.Length];

    HueColor shared = this.lights.Render(room, colors).Clamped();

    LightChannel[] channels = new LightChannel[room.Length];
    for(int index = 0; index < room.Length; index++) {
      channels[index] = new LightChannel(room[index].At, colors[index].Clamped());
    }

    Volatile.Write(ref this.latest, new LightPreviewFrame(shared, channels));
  }

  /// <summary>The selected area's lights, placed as the stream would place them, or the stand-ins.</summary>
  private LightPlacement[] Room() {
    HueConnection connection = this.bridge.Connection;
    IReadOnlyList<HueChannel>? channels = connection.Areas
      .FirstOrDefault(area => area.Id == connection.SelectedAreaId)?.Channels;

    if(channels is not { Count: > 0 }) {
      this.placedFrom = null;
      return this.placements = StandIns;
    }

    if(!ReferenceEquals(channels, this.placedFrom)) {
      this.placedFrom = channels;
      this.placements = [.. LightPlacements.Spread(channels)];
    }

    return this.placements;
  }
}
