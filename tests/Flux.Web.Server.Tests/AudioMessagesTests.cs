using Flux.Audio;
using Flux.Beat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Flux.Web.Server.Tests;

/// <summary>
/// The audio source's sensitivity on the wire: told to a page as it connects, set from
/// any page, and announced to all of them.
/// </summary>
public sealed class AudioMessagesTests {
  [Fact]
  public async Task APageIsToldTheSensitivityAsItConnects() {
    Rig rig = new Rig(sensitivity: 70);
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);

    JsonElement audio = await rig.AudioFrameAsync(socket);
    Assert.Equal(70, audio.GetProperty("sensitivity").GetInt32());

    socket.ClientClose();
    await serving;
  }

  [Fact]
  public async Task ASensitivityIsSetAndAnnouncedToEveryPage() {
    Rig rig = new Rig(sensitivity: 50);
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.AudioFrameAsync(socket);

    socket.Client("""{"type":"setSensitivity","data":{"sensitivity":80}}""");

    using JsonDocument frame = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("audio", frame.RootElement.GetProperty("type").GetString());
    Assert.Equal(80, frame.RootElement.GetProperty("data").GetProperty("sensitivity").GetInt32());
    Assert.Equal(80, rig.Sensitivity.Sensitivity);

    socket.ClientClose();
    await serving;
  }

  [Theory]
  [InlineData("""{"sensitivity":0}""", "rejected")]
  [InlineData("""{"sensitivity":101}""", "rejected")]
  [InlineData("""{"sensitivity":"high"}""", "invalid_data")]
  [InlineData("""{}""", "invalid_data")]
  public async Task ASensitivityOutsideItsRangeOrShapeIsRefused(string data, string code) {
    Rig rig = new Rig(sensitivity: 50);
    FakeWebSocket socket = new FakeWebSocket();
    Task serving = rig.Hub.ServeAsync(socket, CancellationToken.None);
    await rig.AudioFrameAsync(socket);

    socket.Client($$"""{"type":"setSensitivity","data":{{data}}}""");

    using JsonDocument error = JsonDocument.Parse(await socket.NextAsync());
    Assert.Equal("error", error.RootElement.GetProperty("type").GetString());
    Assert.Equal(code, error.RootElement.GetProperty("data").GetProperty("code").GetString());
    Assert.Equal(50, rig.Sensitivity.Sensitivity);

    socket.ClientClose();
    await serving;
  }

  private sealed class Rig {
    public Rig(int sensitivity) {
      MonotonicClock clock = new MonotonicClock();
      BeatOptions beat = new BeatOptions();
      SyncSourceManager sources = new SyncSourceManager(
        new BeatGrid(clock, beat.Quantum, beat.StaleAfter),
        Options.Create(beat),
        settings: null,
        NullLogger<SyncSourceManager>.Instance);

      this.Sensitivity = new FakeSensitivity(sensitivity);
      this.Hub = new SocketHub(
        Options.Create(new WebOptions()),
        Options.Create(beat),
        sources,
        new ManualSource(clock, sources),
        audio: null,
        hue: null,
        stream: null,
        new StageService(new MemoryStageSettingsStore()),
        updates: null,
        new FakeHostLifetime(),
        NullLogger<SocketHub>.Instance,
        phone: null,
        this.Sensitivity);

      // Started rather than faked, so a broadcaster that forgot to subscribe fails here.
      new AudioBroadcaster(this.Hub, this.Sensitivity).StartAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public SocketHub Hub { get; }

    public FakeSensitivity Sensitivity { get; }

    /// <summary>Reads the greeting up to and including the audio frame, and returns its data.</summary>
    public async Task<JsonElement> AudioFrameAsync(FakeWebSocket socket) {
      while(true) {
        using JsonDocument frame = JsonDocument.Parse(await socket.NextAsync());
        if(frame.RootElement.GetProperty("type").GetString() == "audio") {
          return frame.RootElement.GetProperty("data").Clone();
        }
      }
    }
  }

  private sealed class FakeSensitivity: IAudioSensitivity {
    public FakeSensitivity(int sensitivity) {
      this.Sensitivity = sensitivity;
    }

    public int Sensitivity { get; private set; }

    public event Action<int>? SensitivityChanged;

    public bool TrySetSensitivity(int sensitivity) {
      if(sensitivity is < IAudioSensitivity.Min or > IAudioSensitivity.Max) {
        return false;
      }

      this.Sensitivity = sensitivity;
      this.SensitivityChanged?.Invoke(sensitivity);
      return true;
    }
  }
}
