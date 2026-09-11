using Flux.Beat;
using Flux.Hue;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The Hue half of the socket: the frame a client gets before it asks, and what the
/// bridge and light messages do with a payload.
///
/// No bridge and no link button anywhere in here, which is the whole reason
/// <c>Flux.Hue</c> put the library behind one class. What is being tested is the wire
/// and the dispatch; the pairing loop itself has its own tests next door.
/// </summary>
public sealed class HueMessagesTests {
  private static readonly HueBridge Bridge = new HueBridge("ecb5fafffe0d1234", "192.168.1.51");
  private static readonly HueArea Booth = new HueArea(Guid.Parse("44444444-4444-4444-4444-444444444444"), "Booth", 3, Streaming: true);

  /// <summary>
  /// The frame nobody asked for, and the reason it exists: <c>hue</c> is pushed on
  /// change, so a page that waited for the next change could show an empty pairing
  /// screen for as long as nothing happened.
  /// </summary>
  [Fact]
  public async Task AClientIsToldAboutTheBridgeBeforeItAsksForAnything() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();

    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);

    using JsonDocument hello = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());

    using JsonDocument hue = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("hue", hue.RootElement.GetProperty("type").GetString());
    Assert.Equal("unpaired", hue.RootElement.GetProperty("data").GetProperty("state").GetString());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task ALatencyOffsetIsAcceptedAndAnnouncedToEveryPage() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"setLatencyOffset","data":{"offsetMs":120}}""");

    using JsonDocument hue = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("hue", hue.RootElement.GetProperty("type").GetString());
    Assert.Equal(120, hue.RootElement.GetProperty("data").GetProperty("latencyOffsetMs").GetInt32());
    Assert.Equal(120, rig.Bridge.Connection.LatencyOffsetMs);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// Refused rather than clamped, on the same argument as a manual tempo: half a
  /// second is a whole beat at 120 BPM, so the values past it are typos and clamping
  /// one produces a plausible wrong answer somebody then tunes around.
  /// </summary>
  [Fact]
  public async Task ALatencyOffsetAimedAtADifferentBeatIsRefused() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"setLatencyOffset","data":{"offsetMs":900}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
    Assert.Equal("rejected", error.RootElement.GetProperty("data").GetProperty("code").GetString());
    Assert.Equal(0, rig.Bridge.Connection.LatencyOffsetMs);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A fractional offset is a client that has stopped agreeing with the document,
  /// which is <c>invalid_data</c> rather than <c>rejected</c>: the first will happen
  /// every time, the second is a race the client was right to have lost.
  /// </summary>
  [Fact]
  public async Task AFractionalLatencyOffsetIsABugRatherThanARefusal() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"setLatencyOffset","data":{"offsetMs":12.5}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("invalid_data", error.RootElement.GetProperty("data").GetProperty("code").GetString());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task LightSettingsAreAcceptedAndAnnouncedToEveryPage() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"setLightSettings","data":{"palette":"neon","colorChangeBars":8,"pulseMs":300,"brightness":60,"pulseDepth":40,"colorCount":2,"colorLayout":"alternating"}}""");

    using JsonDocument hue = JsonDocument.Parse(await socket.NextAsync());
    JsonElement engine = hue.RootElement.GetProperty("data").GetProperty("engine");

    Assert.Equal("neon", engine.GetProperty("palette").GetString());
    Assert.Equal(8, engine.GetProperty("colorChangeBars").GetInt32());
    Assert.Equal(300, engine.GetProperty("pulseMs").GetInt32());
    Assert.Equal(60, engine.GetProperty("brightness").GetInt32());
    Assert.Equal(40, engine.GetProperty("pulseDepth").GetInt32());
    Assert.Equal(2, engine.GetProperty("colorCount").GetInt32());
    Assert.Equal("alternating", engine.GetProperty("colorLayout").GetString());
    Assert.Equal(new LightSettings(LightPalette.Neon, 8, 300, 60, 40, 2, LightColorLayout.Alternating), rig.Bridge.Connection.Light);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A palette name that is not on the list is <c>rejected</c> rather than
  /// <c>invalid_data</c>, on the precedent <c>selectSource</c> set: the field is a
  /// string and it is a string, so the shape was right and the value was not.
  /// </summary>
  [Fact]
  public async Task APaletteThatNamesNothingIsRefusedWithTheNameQuotedBack() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"setLightSettings","data":{"palette":"beige","colorChangeBars":4,"pulseMs":240,"brightness":100,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    JsonElement data = error.RootElement.GetProperty("data");

    Assert.Equal("rejected", data.GetProperty("code").GetString());
    Assert.Contains("beige", data.GetProperty("message").GetString());
    Assert.Equal(LightSettings.Default, rig.Bridge.Connection.Light);

    socket.ClientClose();
    await serving;
  }

  [Theory]
  [InlineData(-1, 240, 100, 85, 1)]
  [InlineData(17, 240, 100, 85, 1)]
  [InlineData(4, -1, 100, 85, 1)]
  [InlineData(4, 2001, 100, 85, 1)]
  [InlineData(4, 240, -1, 85, 1)]
  [InlineData(4, 240, 101, 85, 1)]
  [InlineData(4, 240, 100, -1, 1)]
  [InlineData(4, 240, 100, 101, 1)]
  [InlineData(4, 240, 100, 85, 0)]
  [InlineData(4, 240, 100, 85, 4)]
  public async Task ASettingOutsideItsRangeIsRefusedRatherThanClamped(int bars, int pulseMs, int brightness, int depth, int colors) {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    // The palette last so that no interpolation is followed by a run of closing braces,
    // which a raw literal cannot tell from the end of an interpolation.
    string engine = $$"""{"colorChangeBars":{{bars}},"pulseMs":{{pulseMs}},"brightness":{{brightness}},"pulseDepth":{{depth}},"colorCount":{{colors}},"colorLayout":"radial","palette":"warm"}""";

    socket.Client($$"""{"type":"setLightSettings","data":{{engine}}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("rejected", error.RootElement.GetProperty("data").GetProperty("code").GetString());
    Assert.Equal(LightSettings.Default, rig.Bridge.Connection.Light);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// The message is one complete statement of how the lights should look rather than a
  /// patch, for the reason <c>subscribe</c> requires both of its flags: a page that
  /// could send a palette alone would rely on the server to supply the rest.
  /// </summary>
  [Theory]
  [InlineData("""{"colorChangeBars":4,"pulseMs":240,"brightness":100,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}""")]
  [InlineData("""{"palette":"warm","pulseMs":240,"brightness":100,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}""")]
  [InlineData("""{"palette":"warm","colorChangeBars":4,"brightness":100,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}""")]
  [InlineData("""{"palette":"warm","colorChangeBars":4,"pulseMs":240,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}""")]
  [InlineData("""{"palette":"warm","colorChangeBars":4,"pulseMs":240,"brightness":100,"colorCount":1,"colorLayout":"radial"}""")]
  [InlineData("""{"palette":"warm","colorChangeBars":4,"pulseMs":240,"brightness":100,"pulseDepth":85}""")]
  [InlineData("""{"palette":"warm","colorChangeBars":4,"pulseMs":240,"brightness":100,"pulseDepth":85,"colorCount":1}""")]
  [InlineData("""{"palette":"warm","colorChangeBars":4,"pulseMs":12.5,"brightness":100,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}""")]
  [InlineData("""{"palette":4,"colorChangeBars":4,"pulseMs":240,"brightness":100,"pulseDepth":85,"colorCount":1,"colorLayout":"radial"}""")]
  public async Task AnIncompleteStatementOfTheLookIsABugRatherThanARefusal(string data) {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client($$"""{"type":"setLightSettings","data":{{data}}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("invalid_data", error.RootElement.GetProperty("data").GetProperty("code").GetString());

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// Every palette the document lists survives the trip out and back, which is the
  /// invariant the paired name and parse exist to hold: the page sends back exactly the
  /// names it was given.
  /// </summary>
  [Theory]
  [InlineData(LightPalette.Random)]
  [InlineData(LightPalette.Warm)]
  [InlineData(LightPalette.Cool)]
  [InlineData(LightPalette.Neon)]
  [InlineData(LightPalette.Sunset)]
  [InlineData(LightPalette.Ocean)]
  [InlineData(LightPalette.Forest)]
  [InlineData(LightPalette.Party)]
  public void EveryPaletteHasAProtocolNameThatParsesBackToIt(LightPalette palette) {
    HueConnection connection = HueConnection.Initial(
      HueConnectionState.Ready,
      latencyOffsetMs: 0,
      LightSettings.Default with { Palette = palette });

    string name = Payload(HueFrames.Snapshot(connection)).GetProperty("engine").GetProperty("palette").GetString()!;

    Assert.True(HueFrames.TryParsePalette(name, out LightPalette parsed));
    Assert.Equal(palette, parsed);
  }

  [Fact]
  public async Task AnAreaIdThatIsNotAUuidIsABugRatherThanARace() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"selectArea","data":{"areaId":"the-booth"}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("invalid_data", error.RootElement.GetProperty("data").GetProperty("code").GetString());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task PairingWithoutABridgeIdIsRefusedBeforeAnythingIsAttempted() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"pairBridge","data":{}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("invalid_data", error.RootElement.GetProperty("data").GetProperty("code").GetString());
    Assert.Equal(0, rig.Client.PairAttempts);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// Never refused, and the reason is on screen rather than in the code: forgetting a
  /// bridge that was never paired leaves the world exactly as the caller wanted it,
  /// and an error there is a red message for somebody who got what they asked for.
  /// </summary>
  [Fact]
  public async Task ForgettingABridgeIsNeverRefusedEvenWithNothingToForget() {
    Rig rig = new Rig();
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.SkipGreetingAsync(socket);

    socket.Client("""{"type":"forgetBridge","data":{}}""");
    socket.Client("""{"type":"ping","data":{"t":7}}""");

    // The pong is the proof: the receive loop takes one message at a time in order, so
    // anything the forget was going to send would already be sitting ahead of it.
    using JsonDocument next = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", next.RootElement.GetProperty("type").GetString());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public void ComposeNamesEveryFieldTheDocumentPromises() {
    HueConnection connection = new HueConnection(
      HueConnectionState.Ready,
      Bridge,
      [Bridge],
      [Booth],
      Booth.Id,
      PairingSecondsRemaining: null,
      LatencyOffsetMs: 90,
      new LightSettings(LightPalette.Neon, ColorChangeBars: 8, PulseMs: 120, Brightness: 70, PulseDepth: 30),
      Message: null);

    JsonElement data = Payload(HueFrames.Snapshot(connection));

    Assert.Equal("ready", data.GetProperty("state").GetString());
    Assert.Equal(Bridge.Id, data.GetProperty("bridge").GetProperty("id").GetString());
    Assert.Equal(Bridge.Address, data.GetProperty("bridge").GetProperty("address").GetString());
    Assert.Equal(1, data.GetProperty("discovered").GetArrayLength());
    Assert.Equal(90, data.GetProperty("latencyOffsetMs").GetInt32());

    JsonElement light = data.GetProperty("engine");
    Assert.Equal("neon", light.GetProperty("palette").GetString());
    Assert.Equal(8, light.GetProperty("colorChangeBars").GetInt32());
    Assert.Equal(120, light.GetProperty("pulseMs").GetInt32());
    Assert.Equal(70, light.GetProperty("brightness").GetInt32());
    Assert.Equal(30, light.GetProperty("pulseDepth").GetInt32());
    Assert.Equal(1, light.GetProperty("colorCount").GetInt32());
    Assert.Equal("radial", light.GetProperty("colorLayout").GetString());

    JsonElement area = data.GetProperty("areas")[0];
    Assert.Equal(Booth.Id, area.GetProperty("id").GetGuid());
    Assert.Equal("Booth", area.GetProperty("name").GetString());
    Assert.Equal(3, area.GetProperty("channelCount").GetInt32());
    Assert.True(area.GetProperty("streaming").GetBoolean());
    Assert.Equal(Booth.Id, data.GetProperty("selectedAreaId").GetGuid());
  }

  /// <summary>
  /// Unlike <c>diagnostics</c>, nothing here is omitted when null. Every nullable
  /// field is a documented value rather than an absence, and a client that had to
  /// tell absent from null would be doing the server's job.
  /// </summary>
  [Fact]
  public void ComposeSendsItsNullsRatherThanLeavingThemOut() {
    JsonElement data = Payload(HueFrames.Snapshot(HueConnection.Initial(HueConnectionState.Unpaired, 0, LightSettings.Default)));

    Assert.Equal(JsonValueKind.Null, data.GetProperty("bridge").ValueKind);
    Assert.Equal(JsonValueKind.Null, data.GetProperty("selectedAreaId").ValueKind);
    Assert.Equal(JsonValueKind.Null, data.GetProperty("pairingSecondsRemaining").ValueKind);
    Assert.Equal(JsonValueKind.Null, data.GetProperty("message").ValueKind);
    Assert.Empty(data.GetProperty("discovered").EnumerateArray());
    Assert.Empty(data.GetProperty("areas").EnumerateArray());
  }

  /// <summary>
  /// The invariant asserted in both directions rather than in the one the code
  /// happens to break first: a countdown outside pairing counts down to nothing, and
  /// pairing without one blanks the number somebody in a hallway is reading.
  /// </summary>
  [Theory]
  [InlineData(HueConnectionState.Disabled)]
  [InlineData(HueConnectionState.Unpaired)]
  [InlineData(HueConnectionState.Discovering)]
  [InlineData(HueConnectionState.Pairing)]
  [InlineData(HueConnectionState.Connecting)]
  [InlineData(HueConnectionState.Ready)]
  [InlineData(HueConnectionState.Failed)]
  public void EveryStateHasAProtocolNameAndOnlyPairingHasACountdown(HueConnectionState state) {
    int? countdown = state == HueConnectionState.Pairing ? 42 : null;
    HueConnection connection = HueConnection.Initial(state, 0, LightSettings.Default) with { PairingSecondsRemaining = countdown };

    JsonElement data = Payload(HueFrames.Snapshot(connection));

    Assert.NotEqual(JsonValueKind.Undefined, data.GetProperty("state").ValueKind);
    Assert.Equal(
      state == HueConnectionState.Pairing,
      data.GetProperty("pairingSecondsRemaining").ValueKind == JsonValueKind.Number);
  }

  private static JsonElement Payload(ReadOnlyMemory<byte> frame) {
    using JsonDocument document = JsonDocument.Parse(frame);

    Assert.Equal("hue", document.RootElement.GetProperty("type").GetString());

    return document.RootElement.GetProperty("data").Clone();
  }

  /// <summary>A hub wired to a bridge service that has never met a bridge.</summary>
  private sealed class Rig {
    public Rig() {
      MonotonicClock clock = new MonotonicClock();
      BeatOptions beat = new BeatOptions();

      SyncSourceManager sources = new SyncSourceManager(
        new BeatGrid(clock, beat.Quantum, beat.StaleAfter),
        Options.Create(beat),
        NullLogger<SyncSourceManager>.Instance);

      this.Bridge = new HueBridgeService(
        this.Client,
        new StubCredentialStore(),
        this.Settings,
        Options.Create(new HueOptions()),
        NullLogger<HueBridgeService>.Instance);

      this.Hub = new SocketHub(
        Options.Create(new WebOptions()),
        Options.Create(beat),
        sources,
        new ManualSource(clock, sources),
        audio: null,
        this.Bridge,
        stream: null,
        new StageService(new MemoryStageSettingsStore()),
        new FakeHostLifetime(),
        NullLogger<SocketHub>.Instance);

      // Started rather than faked, so the event wiring is under test too: a
      // broadcaster that forgot to subscribe would pass every assertion made against
      // the composer alone.
      this.Broadcaster = new HueBroadcaster(this.Hub, this.Bridge);
      this.Broadcaster.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public SocketHub Hub { get; }

    public HueBridgeService Bridge { get; }

    public HueBroadcaster Broadcaster { get; }

    public StubBridgeClient Client { get; } = new StubBridgeClient();

    public StubSettingsStore Settings { get; } = new StubSettingsStore();

    /// <summary>
    /// Reads past the three frames a connection to a rig with a bridge opens with:
    /// hello, the stage's look, and the bridge picture.
    /// </summary>
    public async Task SkipGreetingAsync(FakeWebSocket socket) {
      await socket.NextAsync();
      await socket.NextAsync();
      await socket.NextAsync();
    }
  }

  /// <summary>A bridge that is never there. Nothing here needs one to answer.</summary>
  private sealed class StubBridgeClient: IHueBridgeClient {
    public int PairAttempts { get; private set; }

    public Task<IReadOnlyList<HueBridge>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken) {
      return Task.FromResult<IReadOnlyList<HueBridge>>([]);
    }

    public Task<HueCredentials?> TryPairAsync(HueBridge bridge, CancellationToken cancellationToken) {
      this.PairAttempts++;
      return Task.FromResult<HueCredentials?>(null);
    }

    public Task<HueBridge?> TryDescribeAsync(string address, CancellationToken cancellationToken) {
      return Task.FromResult<HueBridge?>(null);
    }

    public Task<IReadOnlyList<HueArea>> GetAreasAsync(HueBridge bridge, HueCredentials credentials, CancellationToken cancellationToken) {
      return Task.FromResult<IReadOnlyList<HueArea>>([]);
    }
  }

  private sealed class StubCredentialStore: ICredentialStore {
    public HueCredentials? Load() => null;

    public void Save(HueCredentials credentials) {
    }

    public void Clear() {
    }
  }

  private sealed class StubSettingsStore: IHueSettingsStore {
    public HueSettings Stored { get; private set; } = HueSettings.Empty;

    public HueSettings Load() => this.Stored;

    public void Save(HueSettings settings) => this.Stored = settings;
  }
}
