using Flux.Audio;
using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net.WebSockets;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// What the hub does with a connection, observed from the socket's side.
/// </summary>
public sealed class SocketHubTests {
  [Fact]
  public async Task GreetsAClientBeforeItSendsAnythingElse() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);

    using JsonDocument hello = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());

    JsonElement data = hello.RootElement.GetProperty("data");
    Assert.Equal(Frames.Version, data.GetProperty("protocol").GetInt32());
    Assert.Equal(4, data.GetProperty("quantum").GetInt32());
    Assert.Equal(20, data.GetProperty("stateHz").GetInt32());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task AnswersAPingWithTheSameTimestamp() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client("""{"type":"ping","data":{"t":1755950400123}}""");

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());
    Assert.Equal(1755950400123, pong.RootElement.GetProperty("data").GetProperty("t").GetInt64());

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A pong echoing a timestamp the client never sent would read as a round trip
  /// of several decades, so a ping without one is refused rather than defaulted.
  /// </summary>
  [Fact]
  public async Task RefusesAPingWithNoTimestamp() {
    await AssertErrorAsync("""{"type":"ping","data":{}}""", "invalid_data");
  }

  [Fact]
  public async Task AnswersAnUnknownTypeRatherThanIgnoringIt() {
    await AssertErrorAsync("""{"type":"pnig","data":{}}""", "unknown_type");
  }

  [Fact]
  public async Task AnswersMalformedJson() {
    await AssertErrorAsync("{ this is not json", "malformed");
  }

  [Fact]
  public async Task AnswersAMessageWithNoType() {
    await AssertErrorAsync("""{"data":{"t":1}}""", "malformed");
  }

  /// <summary>
  /// A client cannot have its own input amplified back at it: the quoted type is
  /// capped well below the message it arrived in.
  /// </summary>
  [Fact]
  public async Task TruncatesALongTypeBeforeQuotingItBack() {
    // Built by concatenation rather than interpolated: a raw string cannot carry
    // the run of closing braces this JSON ends on without ambiguity.
    string enormous = new string('x', 3000);
    string message = "{\"type\":\"" + enormous + "\",\"data\":{}}";

    string error = await AssertErrorAsync(message, "unknown_type");

    Assert.True(error.Length < 200, $"The error quoted {error.Length} characters back at the client.");
  }

  /// <summary>
  /// The receive buffer is the documented maximum, so a message that outgrows it
  /// can never be read whole: the connection is closed with the status the RFC
  /// reserves for exactly this.
  /// </summary>
  [Fact]
  public async Task ClosesAMessageThatOutgrowsTheBuffer() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client(new string('x', Frames.MaxInboundBytes + 1));
    await serving;

    Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
  }

  /// <summary>
  /// A message at exactly the limit is within the protocol's promise and is
  /// still answered; one byte past it is the close above.
  /// </summary>
  [Fact]
  public async Task AcceptsAMessageExactlyAtTheLimit() {
    const string skeleton = """{"type":"ping","data":{"t":1},"pad":""}""";
    string message = skeleton.Replace("\"pad\":\"\"", $"\"pad\":\"{new string('x', Frames.MaxInboundBytes - skeleton.Length)}\"", StringComparison.Ordinal);
    Assert.Equal(Frames.MaxInboundBytes, message.Length);

    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client(message);

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A real socket delivers a message in as many fragments as it likes; the
  /// receive loop has to reassemble them before acting. Five bytes at a time is
  /// enough to force the path a whole-message fake never touches.
  /// </summary>
  [Fact]
  public async Task ReassemblesAFragmentedMessage() {
    FakeWebSocket socket = new FakeWebSocket { MaxChunk = 5 };
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client("""{"type":"ping","data":{"t":42}}""");

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());
    Assert.Equal(42, pong.RootElement.GetProperty("data").GetProperty("t").GetInt64());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task ClosesOnABinaryFrame() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.ClientBinary(1, 2, 3);
    await serving;

    Assert.Equal(WebSocketCloseStatus.InvalidMessageType, socket.CloseStatus);
  }

  [Fact]
  public async Task BroadcastsToEveryConnectedClient() {
    FakeWebSocket first = new FakeWebSocket();
    FakeWebSocket second = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task servingFirst = hub.ServeAsync(first, CancellationToken.None);
    Task servingSecond = hub.ServeAsync(second, CancellationToken.None);
    await SkipOpeningAsync(first);
    await SkipOpeningAsync(second);

    hub.Broadcast(Frames.Beat(new BeatData(7, 3, BarAligned: false, 128.0)));

    foreach(FakeWebSocket socket in new[] { first, second }) {
      using JsonDocument beat = JsonDocument.Parse(await socket.NextAsync());
      Assert.Equal("beat", beat.RootElement.GetProperty("type").GetString());
      Assert.Equal(7, beat.RootElement.GetProperty("data").GetProperty("index").GetInt64());
    }

    first.ClientClose();
    second.ClientClose();
    await Task.WhenAll(servingFirst, servingSecond);
  }

  /// <summary>
  /// The state pump asks this before it builds a snapshot, so it has to be false
  /// once the last browser has gone rather than merely eventually.
  /// </summary>
  [Fact]
  public async Task ForgetsAClientThatDisconnects() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Assert.False(hub.HasClients);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);
    Assert.True(hub.HasClients);

    socket.ClientClose();
    await serving;

    Assert.False(hub.HasClients);
  }

  [Fact]
  public async Task SubscribingSwitchesTheSpectrumFeedOnForThatClientOnly() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    Assert.False(hub.HasSpectrumSubscribers);

    await SettleAsync(socket, """{"type":"subscribe","data":{"spectrum":true,"deviceLevels":false}}""");

    Assert.True(hub.HasSpectrumSubscribers);
    Assert.False(hub.HasDeviceSubscribers);

    // The message is a complete statement of what the client wants, so sending
    // it again with false is the only unsubscribe there is.
    await SettleAsync(socket, """{"type":"subscribe","data":{"spectrum":false,"deviceLevels":true}}""");

    Assert.False(hub.HasSpectrumSubscribers);
    Assert.True(hub.HasDeviceSubscribers);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// Both flags are required. A missing one read as false would turn a
  /// misspelled field into a silent unsubscribe, whose only symptom is a
  /// visualiser that never draws.
  /// </summary>
  [Fact]
  public async Task RefusesASubscribeThatOmitsAFlag() {
    await AssertErrorAsync("""{"type":"subscribe","data":{"spectrum":true}}""", "invalid_data");
  }

  [Fact]
  public async Task RefusesASubscribeWithANumberForAFlag() {
    await AssertErrorAsync("""{"type":"subscribe","data":{"spectrum":1,"deviceLevels":0}}""", "invalid_data");
  }

  [Fact]
  public async Task SpectrumReachesOnlyTheClientsThatAskedForIt() {
    FakeWebSocket watching = new FakeWebSocket();
    FakeWebSocket listening = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task servingWatcher = hub.ServeAsync(watching, CancellationToken.None);
    Task servingListener = hub.ServeAsync(listening, CancellationToken.None);
    await SkipOpeningAsync(watching);
    await SkipOpeningAsync(listening);

    await SettleAsync(watching, """{"type":"subscribe","data":{"spectrum":true,"deviceLevels":false}}""");

    hub.BroadcastSpectrum(Frames.Spectrum(new SpectrumData([1], 0.1, 0.1, 0.1, 0.1, "none")));
    hub.Broadcast(Frames.Beat(new BeatData(9, 1, BarAligned: false, 128.0)));

    // The phone in the booth never subscribed, so the next thing it sees is the
    // beat: the spectrum was not queued for it at all.
    using JsonDocument beat = JsonDocument.Parse(await listening.NextAsync());
    Assert.Equal("beat", beat.RootElement.GetProperty("type").GetString());

    List<string> seen = [
      JsonDocument.Parse(await watching.NextAsync()).RootElement.GetProperty("type").GetString() ?? string.Empty,
      JsonDocument.Parse(await watching.NextAsync()).RootElement.GetProperty("type").GetString() ?? string.Empty,
    ];

    Assert.Contains("spectrum", seen);
    Assert.Contains("beat", seen);

    watching.ClientClose();
    listening.ClientClose();
    await Task.WhenAll(servingWatcher, servingListener);
  }

  /// <summary>
  /// The spectrum slot holds one frame, so a client that cannot keep up sees
  /// fewer frames and never an older one. An out-of-date picture of a spectrum
  /// is not a smaller version of the picture, it is the wrong one.
  /// </summary>
  [Fact]
  public async Task ANewSpectrumFrameReplacesTheOneWaiting() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);
    await SettleAsync(socket, """{"type":"subscribe","data":{"spectrum":true,"deviceLevels":false}}""");

    Task stalled = socket.StallAsync();
    hub.BroadcastSpectrum(Spectrum(1));
    await stalled;

    hub.BroadcastSpectrum(Spectrum(2));
    hub.BroadcastSpectrum(Spectrum(3));
    socket.Release();

    Assert.Equal(1, await FirstBinAsync(socket));
    Assert.Equal(3, await FirstBinAsync(socket));

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// The invariant the whole two-queue design exists for: a beat is never
  /// delayed, let alone dropped, to make room for a picture of one.
  /// </summary>
  [Fact]
  public async Task EverythingElseGoesOutBeforeAWaitingSpectrumFrame() {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);
    await SettleAsync(socket, """{"type":"subscribe","data":{"spectrum":true,"deviceLevels":false}}""");

    Task stalled = socket.StallAsync();
    hub.BroadcastSpectrum(Spectrum(1));
    await stalled;

    hub.BroadcastSpectrum(Spectrum(2));
    hub.Broadcast(Frames.Beat(new BeatData(11, 3, BarAligned: false, 128.0)));
    socket.Release();

    // The frame already in flight, then the beat, then the spectrum that was
    // queued before it.
    Assert.Equal(1, await FirstBinAsync(socket));

    using JsonDocument beat = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("beat", beat.RootElement.GetProperty("type").GetString());
    Assert.Equal(11, beat.RootElement.GetProperty("data").GetProperty("index").GetInt64());

    Assert.Equal(2, await FirstBinAsync(socket));

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task SelectingASourceMakesItActive() {
    FakeWebSocket socket = new FakeWebSocket();
    Rig rig = NewRig();
    _ = new SilentSource("audio", rig.Sources);

    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    await SettleAsync(socket, """{"type":"selectSource","data":{"source":"audio"}}""");

    Assert.Equal("audio", rig.Sources.ActiveSource);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A source this build does not have is a refusal rather than a silent no-op:
  /// the page shows its button as pending until a state frame agrees, so a
  /// server that said nothing would leave it pending forever.
  /// </summary>
  [Fact]
  public async Task RejectsASourceThisBuildDoesNotHave() {
    await AssertErrorAsync("""{"type":"selectSource","data":{"source":"turntable"}}""", "rejected");
  }

  [Fact]
  public async Task RefusesASelectSourceWithNoName() {
    await AssertErrorAsync("""{"type":"selectSource","data":{}}""", "invalid_data");
  }

  /// <summary>
  /// A build with no audio support answers exactly as an unplugged device does.
  /// From the page's side both are "that device is not there", and both leave the
  /// selection where it was.
  /// </summary>
  [Fact]
  public async Task RejectsAnAudioDeviceThisBuildCannotSee() {
    await AssertErrorAsync("""{"type":"selectAudioDevice","data":{"deviceId":"{0.0.1}"}}""", "rejected");
  }

  [Fact]
  public async Task PassesAnAudioDeviceSelectionToTheAudioLayer() {
    FakeWebSocket socket = new FakeWebSocket();
    FakeAudioDevices audio = new FakeAudioDevices().Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)");
    SocketHub hub = NewHub(audio);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    // No acknowledgement on success, by design: the next devices frame carries the
    // selection, and that confirmation comes from the thing that made the change.
    // The ping riding behind it is what proves the selection was already handled.
    await SettleAsync(socket, """{"type":"selectAudioDevice","data":{"deviceId":"{0.0.1}"}}""");

    Assert.Equal(["{0.0.1}"], audio.Selections);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// The ordinary race, not an error condition: the list a browser is looking at
  /// is up to a second old, so a device unplugged while its button was on screen
  /// will happen.
  /// </summary>
  [Fact]
  public async Task RejectsAnAudioDeviceThatHasGoneAway() {
    FakeWebSocket socket = new FakeWebSocket();
    FakeAudioDevices audio = new FakeAudioDevices().Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)");
    SocketHub hub = NewHub(audio);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client("""{"type":"selectAudioDevice","data":{"deviceId":"{0.0.9}"}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
    Assert.Equal("rejected", error.RootElement.GetProperty("data").GetProperty("code").GetString());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task RefusesASelectAudioDeviceWithNoId() {
    await AssertErrorAsync("""{"type":"selectAudioDevice","data":{}}""", "invalid_data");
  }

  /// <summary>
  /// The list arrives on the subscription rather than on the broadcaster's next
  /// tick, because a picker that opens blank looks broken for as long as it stays
  /// blank.
  /// </summary>
  [Fact]
  public async Task SendsTheDeviceListAtOnceWhenAClientSubscribes() {
    FakeWebSocket socket = new FakeWebSocket();
    FakeAudioDevices audio = new FakeAudioDevices()
      .Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)")
      .Capturing("{0.0.1}", AudioDeviceState.Signal, rms: 0.12, peak: 0.44);

    SocketHub hub = NewHub(audio);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client("""{"type":"subscribe","data":{"spectrum":false,"deviceLevels":true}}""");

    using JsonDocument frame = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("devices", frame.RootElement.GetProperty("type").GetString());

    JsonElement first = frame.RootElement.GetProperty("data").GetProperty("devices")[0];
    Assert.Equal("{0.0.1}", first.GetProperty("id").GetString());
    Assert.True(first.GetProperty("selected").GetBoolean());

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// A client that only wants a spectrum must not be sent a device list it never
  /// asked for: everything expensive on this socket is opt-in, one flag at a time.
  /// </summary>
  [Fact]
  public async Task SendsNoDeviceListToAClientThatOnlyWantedTheSpectrum() {
    FakeWebSocket socket = new FakeWebSocket();
    FakeAudioDevices audio = new FakeAudioDevices().Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)");
    SocketHub hub = NewHub(audio);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    // The pong is the proof: it was queued after the subscribe was handled, so
    // arriving first means no devices frame was queued ahead of it.
    await SettleAsync(socket, """{"type":"subscribe","data":{"spectrum":true,"deviceLevels":false}}""");

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task TapsAreCountedByTheManualSource() {
    FakeWebSocket socket = new FakeWebSocket();
    Rig rig = NewRig();

    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    await SettleAsync(socket, """{"type":"tap","data":{}}""");
    await SettleAsync(socket, """{"type":"tap","data":{}}""");

    Assert.Equal(2, rig.Manual.Status().Diagnostics.Taps);

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task SetsAndClearsTheManualTempo() {
    FakeWebSocket socket = new FakeWebSocket();
    Rig rig = NewRig();

    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    await SettleAsync(socket, """{"type":"setBpm","data":{"bpm":128.5}}""");
    Assert.Equal(128.5, rig.Manual.Bpm);

    // Null is a value here, not an absence: it retracts the tempo rather than
    // setting it to zero.
    await SettleAsync(socket, """{"type":"setBpm","data":{"bpm":null}}""");
    Assert.Null(rig.Manual.Bpm);

    socket.ClientClose();
    await serving;
  }

  /// <summary>
  /// Refused rather than clamped. The values just outside the range are typos,
  /// and clamping a typo produces a plausible wrong answer.
  /// </summary>
  [Fact]
  public async Task RejectsATempoOutsideTheAcceptedRange() {
    await AssertErrorAsync("""{"type":"setBpm","data":{"bpm":900}}""", "rejected");
  }

  [Fact]
  public async Task RefusesASetBpmWithNoTempoField() {
    await AssertErrorAsync("""{"type":"setBpm","data":{}}""", "invalid_data");
  }

  /// <summary>An encoded spectrum frame whose first bin identifies it.</summary>
  private static ReadOnlyMemory<byte> Spectrum(int mark) {
    return Frames.Spectrum(new SpectrumData([mark], 0.1, 0.2, 0.3, 0.4, "none"));
  }

  /// <summary>Reads the next frame, insists it is a spectrum, and returns its mark.</summary>
  private static async Task<int> FirstBinAsync(FakeWebSocket socket) {
    using JsonDocument frame = JsonDocument.Parse(await socket.NextAsync());

    Assert.Equal("spectrum", frame.RootElement.GetProperty("type").GetString());

    return frame.RootElement.GetProperty("data").GetProperty("bins")[0].GetInt32();
  }

  /// <summary>
  /// Sends one client message and returns the prose of the error it produced.
  /// </summary>
  private static async Task<string> AssertErrorAsync(string message, string expectedCode) {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub();

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await SkipOpeningAsync(socket);

    socket.Client(message);

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("error", error.RootElement.GetProperty("type").GetString());

    JsonElement data = error.RootElement.GetProperty("data");
    Assert.Equal(expectedCode, data.GetProperty("code").GetString());

    socket.ClientClose();
    await serving;

    return data.GetProperty("message").GetString() ?? string.Empty;
  }

  private static SocketHub NewHub(IAudioDevices? audio = null) {
    return NewRig(audio).Hub;
  }

  /// <summary>
  /// A hub and the beat pipeline behind it, so a test can check what a client
  /// message actually did rather than only what came back.
  ///
  /// The audio layer defaults to absent, which is a build with no audio support
  /// rather than a machine with no devices, and is the case most of these tests
  /// are about.
  /// </summary>
  private static Rig NewRig(IAudioDevices? audio = null) {
    MonotonicClock clock = new MonotonicClock();
    BeatOptions beat = new BeatOptions();
    BeatGrid grid = new BeatGrid(clock, beat.Quantum, beat.StaleAfter);

    SyncSourceManager sources = new SyncSourceManager(
      grid,
      Options.Create(beat),
      NullLogger<SyncSourceManager>.Instance);

    ManualSource manual = new ManualSource(clock, sources);

    SocketHub hub = new SocketHub(
      Options.Create(new WebOptions()),
      Options.Create(beat),
      sources,
      manual,
      audio,
      hue: null,
      stream: null,
      new StageService(new MemoryStageSettingsStore()),
      new FakeHostLifetime(),
      NullLogger<SocketHub>.Instance);

    return new Rig(hub, sources, manual);
  }

  /// <summary>
  /// Sends one client message and waits for it to have been acted on.
  ///
  /// The wait is a ping behind it rather than a delay: the receive loop handles
  /// one message at a time in order, so a pong is proof that everything sent
  /// before it has already happened. A test that slept instead would be a test
  /// that fails on a busy machine.
  /// </summary>
  private static async Task SettleAsync(FakeWebSocket socket, string message) {
    socket.Client(message);
    socket.Client("""{"type":"ping","data":{"t":7}}""");

    using JsonDocument pong = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());
  }

  private sealed record Rig(SocketHub Hub, SyncSourceManager Sources, ManualSource Manual);

  /// <summary>
  /// A source that exists only to be selected. It never publishes, which is
  /// exactly the case the selector has to survive.
  /// </summary>
  private sealed class SilentSource: ISyncSource {
    public SilentSource(string name, SyncSourceManager sources) {
      this.Name = name;
      sources.Register(this);
    }

    public string Name { get; }

    public SyncSourceStatus Status() {
      return new SyncSourceStatus(SyncSourceState.Waiting, Confidence: 0);
    }
  }

  /// <summary>
  /// Reads past the frames every connection opens with, so a test can assert on the
  /// first one it actually asked for.
  ///
  /// Named rather than counted inline, because the count changes whenever a
  /// push-on-change message is added - the stage's look was the second - and a test
  /// that skipped the wrong number of frames fails somewhere unrelated to whatever it
  /// was testing.
  /// </summary>
  private static async Task SkipOpeningAsync(FakeWebSocket socket) {
    await socket.NextAsync();
    await socket.NextAsync();
  }

}
