using Flux.Beat.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NAudio.CoreAudioApi;
using Xunit;

namespace Flux.Audio.Tests;

/// <summary>
/// The capture loop's policy: which device it resolves, how it backs off when one
/// refuses to open, and when that backoff is forgotten.
///
/// Driven one poll at a time through <see cref="AudioCaptureService.PollOnce"/>
/// against a fake catalog and a fake clock, which is the seam
/// <see cref="IAudioDeviceCatalog"/> exists to open. Every open here is refused,
/// deliberately: a session that actually delivers audio needs a real endpoint, so
/// that half stays with the machine that has a controller plugged in, while the
/// retry arithmetic - the half whose regressions read as flaky hardware - runs
/// here against a clock the test owns.
/// </summary>
public sealed class AudioCaptureServiceTests {
  private static readonly AudioDevice Flx = Device("flx", "Microfoon (2- DDJ-FLX10)");
  private static readonly AudioDevice Line = Device("line", "Line In (Realtek)");

  [Fact]
  public void StaysIdleWhenNothingMatchesThePreference() {
    // Capturing nothing is the deliberate answer to no match: adopting a device
    // that will be silent all night would report an audio source that is not one.
    Rig rig = new Rig();
    rig.Catalog.Present = [Line];

    rig.Service.PollOnce();

    Assert.Empty(rig.Catalog.Opened);

    AudioCaptureStatus status = rig.Service.Status();
    Assert.Null(status.Device);
    Assert.Equal(AudioDeviceState.NoData, status.State);
  }

  [Fact]
  public void BacksOffAfterEachRefusalAndDoublesTheWait() {
    Rig rig = new Rig();
    rig.Catalog.Present = [Flx];

    rig.Service.PollOnce();

    // Failed rather than NoData, and the wanted device stays on the readout: the
    // moment somebody needs to know why there is no audio is exactly when the
    // device must not vanish from it.
    Assert.Single(rig.Catalog.Opened);
    AudioCaptureStatus status = rig.Service.Status();
    Assert.Equal(Flx, status.Device);
    Assert.Equal(AudioDeviceState.Failed, status.State);

    // Polls inside the wait try nothing.
    rig.Service.PollOnce();
    Assert.Single(rig.Catalog.Opened);

    rig.Clock.Advance(TimeSpan.FromSeconds(1.1));
    rig.Service.PollOnce();
    Assert.Equal(2, rig.Catalog.Opened.Count);

    // The wait doubled: one second past the second refusal is inside it, another
    // second is past it.
    rig.Clock.Advance(TimeSpan.FromSeconds(1.1));
    rig.Service.PollOnce();
    Assert.Equal(2, rig.Catalog.Opened.Count);

    rig.Clock.Advance(TimeSpan.FromSeconds(1.0));
    rig.Service.PollOnce();
    Assert.Equal(3, rig.Catalog.Opened.Count);
  }

  [Fact]
  public void CapsTheBackoffAtItsCeiling() {
    // The cause of a refusal is usually another application holding the device
    // exclusively, which ends without warning, so the wait must stop growing:
    // half a minute is as long as Flux should take to notice.
    Rig rig = new Rig();
    rig.Catalog.Present = [Flx];

    // Enough refusals for an uncapped doubling to be waiting minutes by now.
    for(int attempt = 0; attempt < 8; attempt++) {
      rig.Service.PollOnce();
      rig.Clock.Advance(TimeSpan.FromSeconds(64));
    }

    rig.Service.PollOnce();
    int attempts = rig.Catalog.Opened.Count;

    // Not shorter than the ceiling...
    rig.Clock.Advance(TimeSpan.FromSeconds(29));
    rig.Service.PollOnce();
    Assert.Equal(attempts, rig.Catalog.Opened.Count);

    // ...and not longer either.
    rig.Clock.Advance(TimeSpan.FromSeconds(2));
    rig.Service.PollOnce();
    Assert.Equal(attempts + 1, rig.Catalog.Opened.Count);
  }

  [Fact]
  public void ANewSelectionDoesNotServeAnotherDevicesBackoff() {
    // The backoff belongs to the device that earned it. A selection is a choice
    // somebody just made from a list they were looking at, and making it wait out
    // an old device's half-minute would read as dead hardware.
    Rig rig = new Rig();
    rig.Catalog.Present = [Flx, Line];

    rig.Service.PollOnce();
    Assert.Equal(["flx"], rig.Catalog.Opened);

    Assert.True(rig.Service.TrySelect("line"));

    rig.Service.PollOnce();
    Assert.Equal(["flx", "line"], rig.Catalog.Opened);
  }

  [Fact]
  public void RefusesASelectionItNeverShowed() {
    // A selection is only ever made from a list somebody was looking at, so an id
    // that was never in one is refused rather than remembered.
    Rig rig = new Rig();
    rig.Catalog.Present = [Flx];

    rig.Service.PollOnce();

    Assert.False(rig.Service.TrySelect("ghost"));
    Assert.False(rig.Service.TrySelect(string.Empty));
    Assert.True(rig.Service.TrySelect("flx"));
  }

  [Fact]
  public void RefusesASelectionAfterDisposalInsteadOfThrowing() {
    // TrySelect arrives from a socket receive loop, so after disposal it has to
    // answer false rather than throw into a client's dispatch. Shutdown ordering
    // protects this today; the test protects it against a registration reorder.
    Rig rig = new Rig();
    rig.Catalog.Present = [Flx];

    rig.Service.PollOnce();
    rig.Service.Dispose();

    Assert.False(rig.Service.TrySelect("flx"));
  }

  private static AudioDevice Device(string id, string name) {
    return new AudioDevice(id, name, AudioDeviceKind.Capture);
  }

  /// <summary>
  /// The service under test with every dependency it cannot be allowed to have for
  /// real: a catalog of devices that do not exist, a clock the test advances, and
  /// a tap that keeps nothing.
  /// </summary>
  [Fact]
  public void TheDeviceChosenLastTimeIsCapturedWithoutAnyoneChoosingItAgain() {
    // The whole point of remembering one. Line In does not match the name preference
    // and would never be resolved on its own, so opening it proves the stored choice
    // is what decided rather than the configuration.
    FakeSettings settings = new FakeSettings(new AudioSettings(Line.Id));
    Rig rig = new Rig(settings: settings);
    rig.Catalog.Present = [Flx, Line];

    rig.Service.PollOnce();

    Assert.Equal([Line.Id], rig.Catalog.Opened);
  }

  /// <summary>
  /// The distinction this feature turns on, and the bug it would have been if it were
  /// missed. A live selection deliberately never falls back - it came from a list
  /// somebody was looking at. A remembered one came from a file that may well predate
  /// the interface being unplugged, so falling back is the only honest answer: the
  /// alternative is a rig that captures nothing after a restart, with no explanation
  /// beyond a device id nobody has chosen this year.
  /// </summary>
  [Fact]
  public void ARememberedDeviceThatIsGoneFallsBackToThePreferenceRatherThanCapturingNothing() {
    FakeSettings settings = new FakeSettings(new AudioSettings("unplugged-since-last-week"));
    Rig rig = new Rig(settings: settings);
    rig.Catalog.Present = [Flx];

    rig.Service.PollOnce();

    Assert.Equal([Flx.Id], rig.Catalog.Opened);
  }

  [Fact]
  public void ARememberedDeviceOutranksTheConfiguredOne() {
    // The same footing the persisted latency offset has with Hue:LatencyOffsetMs: the
    // key is where a machine starts, and the page owns it afterwards. Without this a
    // rig with Audio:DeviceId set could never have its device changed from the page in
    // a way that survived a restart, which is the complaint that prompted all of it.
    FakeSettings settings = new FakeSettings(new AudioSettings(Line.Id));
    Rig rig = new Rig(new AudioOptions { DeviceId = Flx.Id }, settings);
    rig.Catalog.Present = [Flx, Line];

    rig.Service.PollOnce();

    Assert.Equal([Line.Id], rig.Catalog.Opened);
  }

  [Fact]
  public void ChoosingADeviceWritesItDownAtOnce() {
    // At once, rather than at shutdown. Flux is closed by killing a console window as
    // often as not, and a preference saved on the way out is one that is never saved.
    FakeSettings settings = new FakeSettings(AudioSettings.Empty);
    Rig rig = new Rig(settings: settings);
    rig.Catalog.Present = [Flx, Line];

    rig.Service.PollOnce();
    Assert.True(rig.Service.TrySelect(Line.Id));

    Assert.Equal(Line.Id, settings.Saved?.DeviceId);
  }

  [Fact]
  public void ASelectionThisSessionStillRefusesToFallBackWhenItsDeviceVanishes() {
    // The rule the remembered device is deliberately not subject to, pinned here so
    // that giving one of them a fallback cannot quietly give it to both.
    Rig rig = new Rig();
    rig.Catalog.Present = [Flx, Line];

    rig.Service.PollOnce();
    Assert.True(rig.Service.TrySelect(Line.Id));

    rig.Catalog.Opened.Clear();
    rig.Catalog.Present = [Flx];
    rig.Service.PollOnce();

    Assert.Empty(rig.Catalog.Opened);
  }

  /// <summary>A store that keeps its one record in memory and says what it was handed.</summary>
  private sealed class FakeSettings: IAudioSettingsStore {
    private readonly AudioSettings initial;

    public FakeSettings(AudioSettings initial) {
      this.initial = initial;
    }

    public AudioSettings? Saved { get; private set; }

    public AudioSettings Load() {
      return this.initial;
    }

    public void Save(AudioSettings settings) {
      this.Saved = settings;
    }
  }

  private sealed class Rig {
    public Rig(AudioOptions? options = null, IAudioSettingsStore? settings = null) {
      this.Service = new AudioCaptureService(
        this.Catalog,
        this.Clock,
        Options.Create(options ?? new AudioOptions()),
        new NoTap(),
        NullLogger<AudioCaptureService>.Instance,
        settings);
    }

    public FakeCatalog Catalog { get; } = new FakeCatalog();

    public FakeClock Clock { get; } = new FakeClock();

    public AudioCaptureService Service { get; }
  }

  /// <summary>
  /// A catalog whose devices all refuse to open, which is the one behaviour a fake
  /// endpoint can honestly have: there is no constructing a real
  /// <see cref="MMDevice"/> without hardware, and the policy under test is what
  /// happens after a refusal, not after a success.
  /// </summary>
  private sealed class FakeCatalog: IAudioDeviceCatalog {
    public IReadOnlyList<AudioDevice> Present { get; set; } = [];

    public List<string> Opened { get; } = [];

    public IReadOnlyList<AudioDevice> Devices() {
      return this.Present;
    }

    public MMDevice? Open(string id) {
      this.Opened.Add(id);
      return null;
    }
  }

  private sealed class NoTap: IMonoTap {
    public void Write(ReadOnlySpan<float> mono, int sampleRate) {
    }
  }
}
