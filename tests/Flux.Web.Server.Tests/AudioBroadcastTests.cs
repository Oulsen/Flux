using Flux.Audio;
using Flux.Beat;
using Flux.Web.Server.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// What the two audio feeds send, and the far more interesting question of when
/// they send nothing.
///
/// Both broadcasters are timers around a single decision, and the decision is what
/// is worth pinning: a frame composed for nobody is wasted work in a process that
/// has a beat scheduler to run, and a frame composed from nothing at all is a
/// picture of silence that a reader cannot tell apart from a picture of a broken
/// analyzer.
/// </summary>
public sealed class AudioBroadcastTests {
  [Fact]
  public void SendsNoSpectrumWhileNobodyHasSubscribed() {
    FakeSpectrum audio = new FakeSpectrum { Latest = Snapshot(BeatStrength.Strong) };
    SpectrumBroadcaster broadcaster = new SpectrumBroadcaster(NewHub(), audio, Options.Create(new WebOptions()));

    Assert.False(broadcaster.TryBroadcast());
  }

  /// <summary>
  /// A frame of zeroes would be drawn thirty times a second as a flat spectrum,
  /// which looks exactly like an analyzer that has stopped working. Sending
  /// nothing is the honest report of hearing nothing.
  /// </summary>
  [Fact]
  public async Task SendsNoSpectrumWhileNothingIsBeingHeard() {
    FakeSpectrum audio = new FakeSpectrum { Latest = null };
    Rig rig = await NewSubscribedRigAsync(spectrum: true, deviceLevels: false);

    try {
      SpectrumBroadcaster broadcaster = new SpectrumBroadcaster(rig.Hub, audio, Options.Create(new WebOptions()));

      Assert.True(rig.Hub.HasSpectrumSubscribers);
      Assert.False(broadcaster.TryBroadcast());
    } finally {
      await rig.CloseAsync();
    }
  }

  [Fact]
  public async Task SendsTheSpectrumWithTheDetectorsVerdictOnIt() {
    FakeSpectrum audio = new FakeSpectrum { Latest = Snapshot(BeatStrength.Strong) };
    Rig rig = await NewSubscribedRigAsync(spectrum: true, deviceLevels: false);

    try {
      SpectrumBroadcaster broadcaster = new SpectrumBroadcaster(rig.Hub, audio, Options.Create(new WebOptions()));

      Assert.True(broadcaster.TryBroadcast());

      JsonElement frame = await rig.NextAsync("spectrum");

      Assert.Equal(214, frame.GetProperty("bins")[0].GetInt32());
      Assert.Equal(0.62, frame.GetProperty("kick").GetDouble());
      Assert.Equal(0.031, frame.GetProperty("flux").GetDouble());

      // The verdict is the half of the frame the instrument exists for: bands alone
      // say what was heard and nothing about whether the detector agreed.
      Assert.Equal("strong", frame.GetProperty("beat").GetString());
    } finally {
      await rig.CloseAsync();
    }
  }

  [Fact]
  public void SendsNoDeviceListWhileNobodyHasSubscribed() {
    FakeAudioDevices audio = new FakeAudioDevices().Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)");
    DevicesBroadcaster broadcaster = new DevicesBroadcaster(NewHub(), audio, Options.Create(new WebOptions()));

    Assert.False(broadcaster.TryBroadcast());
  }

  [Fact]
  public async Task SendsTheDeviceListToASubscriber() {
    FakeAudioDevices audio = new FakeAudioDevices()
      .Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)")
      .Capturing("{0.0.1}", AudioDeviceState.Signal, rms: 0.12, peak: 0.44);

    Rig rig = await NewSubscribedRigAsync(spectrum: false, deviceLevels: true, audio: audio);

    try {
      DevicesBroadcaster broadcaster = new DevicesBroadcaster(rig.Hub, audio, Options.Create(new WebOptions()));

      Assert.True(broadcaster.TryBroadcast());

      JsonElement frame = await rig.NextAsync("devices");
      Assert.Equal(0.12, frame.GetProperty("devices")[0].GetProperty("rms").GetDouble());
    } finally {
      await rig.CloseAsync();
    }
  }

  /// <summary>
  /// Nothing here is registered in a build with no audio, but a host is free to
  /// construct one anyway, and it must not have to know whether it has a sound card
  /// to do so.
  /// </summary>
  [Fact]
  public void BothFeedsDoNothingInABuildWithNoAudio() {
    SocketHub hub = NewHub();

    Assert.False(new SpectrumBroadcaster(hub, audio: null, Options.Create(new WebOptions())).TryBroadcast());
    Assert.False(new DevicesBroadcaster(hub, audio: null, Options.Create(new WebOptions())).TryBroadcast());
  }

  [Fact]
  public void NamesTheSelectedDeviceAndMeasuresOnlyThatOne() {
    FakeAudioDevices audio = new FakeAudioDevices()
      .Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)")
      .Seeing("{0.0.2}", "Speakers", AudioDeviceKind.Loopback)
      .Capturing("{0.0.1}", AudioDeviceState.Signal, rms: 0.12, peak: 0.44);

    DevicesData devices = DeviceFrames.Compose(audio);

    DeviceData selected = devices.Devices[0];
    Assert.True(selected.Selected);
    Assert.Equal("capture", selected.Kind);
    Assert.Equal("signal", selected.State);
    Assert.Equal(0.12, selected.Rms);
    Assert.Equal(0.44, selected.Peak);

    // An omitted field says "not measured". A zero would say "measured, and
    // silent", which is a claim about somebody's hardware that Flux has not earned:
    // metering every endpoint means holding twenty of them open for a whole set.
    DeviceData other = devices.Devices[1];
    Assert.False(other.Selected);
    Assert.Equal("loopback", other.Kind);
    Assert.Null(other.State);
    Assert.Null(other.Rms);
    Assert.Null(other.Peak);
  }

  /// <summary>
  /// The row that earns the whole "wanted rather than open" rule: the endpoint
  /// somebody picked, sitting there labelled failed, is the answer to why there is
  /// no audio. Its meters stay absent, because a device that never opened delivered
  /// nothing to measure.
  /// </summary>
  [Fact]
  public void KeepsAFailedDeviceOnTheListWithNoMeters() {
    FakeAudioDevices audio = new FakeAudioDevices()
      .Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)")
      .Capturing("{0.0.1}", AudioDeviceState.Failed);

    DeviceData device = DeviceFrames.Compose(audio).Devices.Single();

    Assert.True(device.Selected);
    Assert.Equal("failed", device.State);
    Assert.Null(device.Rms);
    Assert.Null(device.Peak);
  }

  /// <summary>
  /// Digital silence is a measurement rather than an absence of one: bit-exact
  /// zeros are a real reading of a stopped deck or a closed fader, and a meter
  /// pinned at the bottom is what tells that apart from Flux not listening.
  /// </summary>
  [Fact]
  public void MetersADeviceThatIsDeliveringSilence() {
    FakeAudioDevices audio = new FakeAudioDevices()
      .Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)")
      .Capturing("{0.0.1}", AudioDeviceState.DigitalSilence);

    DeviceData device = DeviceFrames.Compose(audio).Devices.Single();

    Assert.Equal("digitalSilence", device.State);
    Assert.Equal(0.0, device.Rms);
    Assert.Equal(0.0, device.Peak);
  }

  [Fact]
  public void SaysNothingAboutAnyDeviceWhileNoneIsSelected() {
    FakeAudioDevices audio = new FakeAudioDevices().Seeing("{0.0.1}", "Microfoon (2- DDJ-FLX10)");

    DeviceData device = DeviceFrames.Compose(audio).Devices.Single();

    Assert.False(device.Selected);
    Assert.Null(device.State);
  }

  private static SpectrumSnapshot Snapshot(BeatStrength strength) {
    return new SpectrumSnapshot([214, 198, 120], 0.62, 0.24, 0.11, 0.031, strength);
  }

  private static SocketHub NewHub(IAudioDevices? audio = null) {
    MonotonicClock clock = new MonotonicClock();
    BeatOptions beat = new BeatOptions();

    SyncSourceManager sources = new SyncSourceManager(
      new BeatGrid(clock, beat.Quantum, beat.StaleAfter),
      Options.Create(beat),
      NullLogger<SyncSourceManager>.Instance);

    return new SocketHub(
      Options.Create(new WebOptions()),
      Options.Create(beat),
      sources,
      new ManualSource(clock, sources),
      audio,
      hue: null,
      new FakeHostLifetime(),
      NullLogger<SocketHub>.Instance);
  }

  /// <summary>
  /// A hub with one connected client that has already said what it wants.
  ///
  /// The subscription goes through a real client message rather than a test hook,
  /// because the subscription flags are the gate under test and a hook would test
  /// the gate by bypassing it.
  /// </summary>
  private static async Task<Rig> NewSubscribedRigAsync(
    bool spectrum,
    bool deviceLevels,
    IAudioDevices? audio = null) {
    FakeWebSocket socket = new FakeWebSocket();
    SocketHub hub = NewHub(audio);

    Task serving = hub.ServeAsync(socket, CancellationToken.None);
    await socket.NextAsync();

    socket.Client(
      $"{{\"type\":\"subscribe\",\"data\":{{\"spectrum\":{Json(spectrum)},\"deviceLevels\":{Json(deviceLevels)}}}}}");

    // A ping behind the subscribe, so the wait below is proof the subscription was
    // acted on rather than a sleep hoping it was. Frames ahead of the pong are
    // discarded rather than asserted on: a subscription that turns the device
    // levels on is answered with a snapshot immediately, which is a feature this
    // helper must not be surprised by.
    socket.Client("""{"type":"ping","data":{"t":7}}""");

    Rig rig = new Rig(hub, socket, serving);
    await rig.SettleAsync();

    return rig;
  }

  private static string Json(bool value) {
    return value ? "true" : "false";
  }

  private sealed record Rig(SocketHub Hub, FakeWebSocket Socket, Task Serving) {
    /// <summary>Reads the next frame, insists it is the type expected, and hands back its payload.</summary>
    public async Task<JsonElement> NextAsync(string expectedType) {
      using JsonDocument frame = JsonDocument.Parse(await this.Socket.NextAsync());

      Assert.Equal(expectedType, frame.RootElement.GetProperty("type").GetString());

      // Cloned because the document is disposed on the way out of this method.
      return frame.RootElement.GetProperty("data").Clone();
    }

    /// <summary>
    /// Reads frames until the pong arrives, which is proof that everything sent
    /// before it has already been acted on. The receive loop handles one message at
    /// a time in order, so this is a synchronisation point rather than a delay, and
    /// a test that slept instead would be a test that fails on a busy machine.
    /// </summary>
    public async Task SettleAsync() {
      while(true) {
        using JsonDocument frame = JsonDocument.Parse(await this.Socket.NextAsync());

        if(frame.RootElement.GetProperty("type").GetString() == "pong") {
          return;
        }
      }
    }

    public async Task CloseAsync() {
      this.Socket.ClientClose();
      await this.Serving;
    }
  }

  /// <summary>A spectrum feed a test sets by hand.</summary>
  private sealed class FakeSpectrum: IAudioSpectrum {
    public SpectrumSnapshot? Latest { get; init; }

    public SpectrumSnapshot? Spectrum() {
      return this.Latest;
    }
  }
}
