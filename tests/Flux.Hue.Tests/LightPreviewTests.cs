using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// The engine run for the page while nothing streams, stepped one tick at a time.
///
/// The engine itself is faked: what is under test is the handover - who renders when -
/// and which room is drawn, not the colours, which the engine's own tests own.
/// </summary>
public class LightPreviewTests {
  private static readonly HueBridge Bridge = new HueBridge("ecb5fafffe0d1234", "192.168.1.51");
  private static readonly HueCredentials Credentials = new HueCredentials("ecb5fafffe0d1234", "app-key", "client-key");

  /// <summary>
  /// Two lights, far apart: the one at the centre follows the kick and the one out at the
  /// edge follows the top, which is what the stream would work out from the same room.
  /// </summary>
  private static readonly HueArea Booth = new HueArea(
    Guid.Parse("22222222-2222-2222-2222-222222222222"),
    "Booth",
    2,
    Streaming: false,
    [new HueChannel(0, new LightPosition(0, 0, 0)), new HueChannel(1, new LightPosition(1, 1, 1))]);

  /// <summary>
  /// With no room to read the preview still has lights to split: a colour count above one
  /// needs lights to divide between, and with none the page could only show the first.
  /// </summary>
  [Fact]
  public void WithNoRoomTheEngineLightsStandInsSpreadAcrossTheSpectrum() {
    Rig rig = new Rig();

    rig.Preview.Tick();

    LightPreviewFrame frame = Assert.IsType<LightPreviewFrame>(rig.Preview.Latest);
    Assert.Equal(FakeLightSource.Shared, frame.Color);
    Assert.Equal(LightPreview.StandInLights, frame.Channels.Count);
    Assert.Equal(0.0, frame.Channels[0].At);
    Assert.Equal(1.0, frame.Channels[^1].At);
    Assert.All(frame.Channels, light => Assert.Equal(FakeLightSource.PerLight, light.Color));
  }

  /// <summary>
  /// The selected area is drawn as the bridge laid it out, read from the area list rather
  /// than from a stream, so the room is right before the lights have ever been on.
  /// </summary>
  [Fact]
  public async Task TheSelectedRoomIsDrawnAsTheBridgeLaidItOut() {
    Rig rig = await Rig.WithRoomAsync();

    rig.Preview.Tick();

    LightPreviewFrame frame = Assert.IsType<LightPreviewFrame>(rig.Preview.Latest);
    Assert.Equal(2, frame.Channels.Count);
    Assert.Equal([0.0, 1.0], frame.Channels.Select(light => light.At).Order());
  }

  /// <summary>
  /// The handover: once a session renders the engine the preview stops both rendering and
  /// publishing, so the page draws the stream's own reading and the engine has one caller.
  /// </summary>
  [Fact]
  public void WhileAStreamRendersThePreviewStandsDown() {
    Rig rig = new Rig();
    rig.Preview.Tick();

    rig.Activity.IsStreaming = true;
    rig.Preview.Tick();

    Assert.Null(rig.Preview.Latest);
    Assert.Equal(1, rig.Lights.Renders);
  }

  private sealed class Rig {
    internal Rig() {
      BridgeService = new HueBridgeService(
        Client,
        CredentialStore,
        new InMemorySettingsStore(),
        Options.Create(new HueOptions()),
        new FakeClock(),
        NullLogger<HueBridgeService>.Instance);

      Preview = new LightPreview(Lights, Activity, BridgeService, Options.Create(new HueOptions()), NullLogger<LightPreview>.Instance);
    }

    internal FakeHueBridgeClient Client { get; } = new FakeHueBridgeClient();

    internal InMemoryCredentialStore CredentialStore { get; } = new InMemoryCredentialStore();

    internal FakeLightSource Lights { get; } = new FakeLightSource();

    internal FakeActivity Activity { get; } = new FakeActivity();

    internal HueBridgeService BridgeService { get; }

    internal LightPreview Preview { get; }

    /// <summary>A paired, reachable bridge with the booth chosen.</summary>
    internal static async Task<Rig> WithRoomAsync() {
      Rig rig = new Rig();
      rig.CredentialStore.Stored = Credentials;
      rig.Client.Bridges = [Bridge];
      rig.Client.Areas = [Booth];

      await rig.BridgeService.StartAsync(TestContext.Current.CancellationToken);
      if(rig.BridgeService.ExecuteTask is { } running) {
        await running;
      }

      Assert.True(await rig.BridgeService.SelectAreaAsync(Booth.Id, TestContext.Current.CancellationToken));

      return rig;
    }
  }

  private sealed class FakeLightSource: ILightSource {
    internal static readonly HueColor Shared = new HueColor(0.8, 0.2, 0.4);
    internal static readonly HueColor PerLight = new HueColor(0.1, 0.6, 0.3);

    internal int Renders { get; private set; }

    public HueColor Render(ReadOnlySpan<LightPlacement> lights, Span<HueColor> colors) {
      Renders++;
      colors[..lights.Length].Fill(PerLight);

      return Shared;
    }
  }

  private sealed class FakeActivity: IStreamActivity {
    public bool IsStreaming { get; set; }
  }
}
