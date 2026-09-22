using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// The pairing flow, the reconnect and the area choice, none of which need a bridge.
/// The pairing window is milliseconds here rather than the real minute: what is being
/// tested is that the loop keeps asking and stops for the right reasons, and a test
/// that waits out a real countdown is a test nobody runs.
/// </summary>
public class HueBridgeServiceTests {
  private static readonly HueBridge Bridge = new HueBridge("ecb5fafffe0d1234", "192.168.1.51");
  private static readonly HueCredentials Credentials = new HueCredentials("ecb5fafffe0d1234", "app-key", "client-key");
  private static readonly HueArea Living = new HueArea(Guid.Parse("11111111-1111-1111-1111-111111111111"), "Living room", 6, Streaming: false);
  private static readonly HueArea Booth = new HueArea(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Booth", 3, Streaming: true);

  [Fact]
  public async Task StaysUnpairedWhenNothingWasEverPaired() {
    Harness harness = new Harness();

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Unpaired, harness.Service.Connection.State);
  }

  [Fact]
  public async Task ContactsNoBridgeAtAllWhenLightsAreSwitchedOff() {
    Harness harness = new Harness(options => options.Enabled = false);
    harness.Credentials.Stored = Credentials;

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Disabled, harness.Service.Connection.State);
    Assert.Equal(0, harness.Client.DiscoverCalls);
  }

  [Fact]
  public async Task DiscoveryKeepsWhatItFoundWithoutClaimingToBePaired() {
    Harness harness = new Harness();
    harness.Client.Bridges = [Bridge];

    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HueConnectionState.Unpaired, harness.Service.Connection.State);
    Assert.Equal(Bridge, Assert.Single(harness.Service.Connection.Discovered));
  }

  [Fact]
  public async Task PairingRefusesABridgeNobodyHasDiscovered() {
    // The ordinary race: a pairing screen left open while the network changed under
    // it. The client was right to ask and the server is right to refuse.
    Harness harness = new Harness();

    bool accepted = await harness.Service.PairAsync("ecb5fafffe0dbeef", TestContext.Current.CancellationToken);

    Assert.False(accepted);
    Assert.Equal(0, harness.Client.PairAttempts);
  }

  [Fact]
  public async Task PairingKeepsAskingUntilTheButtonIsPressed() {
    Harness harness = new Harness();
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];
    harness.Client.PairAnswers.Enqueue(null);
    harness.Client.PairAnswers.Enqueue(null);
    harness.Client.PairAnswers.Enqueue(Credentials);
    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    await harness.Service.PairAsync(Bridge.Id, TestContext.Current.CancellationToken);

    Assert.Equal(3, harness.Client.PairAttempts);
    Assert.Equal(Credentials, harness.Credentials.Stored);
    Assert.Equal(HueConnectionState.Ready, harness.Service.Connection.State);
    Assert.Equal(Bridge, harness.Service.Connection.Bridge);
  }

  [Fact]
  public async Task PairingShowsACountdownWhileItWaitsAndDropsItAfterwards() {
    // The countdown is the whole point of treating error 101 as "keep waiting": the
    // person pressing the button is holding the phone that shows it.
    Harness harness = new Harness();
    harness.Client.Bridges = [Bridge];
    harness.Client.PairAnswers.Enqueue(null);
    harness.Client.PairAnswers.Enqueue(Credentials);
    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    await harness.Service.PairAsync(Bridge.Id, TestContext.Current.CancellationToken);

    Assert.Contains(harness.Seen, connection => connection.State == HueConnectionState.Pairing);

    // The invariant, asserted in both directions on every frame that was broadcast: a
    // countdown exists exactly while pairing. One frame saying "pairing" without a
    // number blanks it on screen; one saying "connecting" with a number left on it is
    // counting down to nothing.
    Assert.All(harness.Seen, connection => Assert.Equal(
      connection.State == HueConnectionState.Pairing,
      connection.PairingSecondsRemaining.HasValue));
  }

  [Fact]
  public async Task PairingGivesUpWithSomethingAUserCanActOn() {
    Harness harness = new Harness();
    harness.Client.Bridges = [Bridge];
    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    await harness.Service.PairAsync(Bridge.Id, TestContext.Current.CancellationToken);

    HueConnection connection = harness.Service.Connection;
    // Unpaired rather than Failed: nothing broke, the button was not pressed, and the
    // next action is to press it.
    Assert.Equal(HueConnectionState.Unpaired, connection.State);
    Assert.Contains("link button", connection.Message, StringComparison.OrdinalIgnoreCase);
    Assert.Null(connection.PairingSecondsRemaining);
    Assert.True(harness.Client.PairAttempts > 1, "the bridge should have been asked more than once");
  }

  [Fact]
  public async Task FindsThePairedBridgeAgainAtWhateverAddressItNowHas() {
    // DHCP moves a bridge between sessions and the credentials survive the move,
    // which is why nothing stores an address.
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [new HueBridge(Bridge.Id, "192.168.1.99")];
    harness.Client.Areas = [Living];

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Ready, harness.Service.Connection.State);
    Assert.Equal("192.168.1.99", harness.Service.Connection.Bridge?.Address);
  }

  [Fact]
  public async Task SaysSoWhenThePairedBridgeIsNotOnTheNetwork() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [new HueBridge("ecb5fafffe0dbeef", "192.168.1.60")];

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Failed, harness.Service.Connection.State);
    Assert.NotNull(harness.Service.Connection.Message);
  }

  [Fact]
  public async Task ABridgeThatRefusesToTalkBecomesAMessageRatherThanAnException() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.AreasThrows = new HueBridgeException("The bridge said no.");

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Failed, harness.Service.Connection.State);
    Assert.Equal("The bridge said no.", harness.Service.Connection.Message);
  }

  [Fact]
  public async Task ReportsAnAreaSomethingElseIsAlreadyStreamingTo() {
    // The busy pre-flight: read before anything opens a socket, because the failure
    // it prevents arrives as a DTLS timeout that blames the network.
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living, Booth];

    await harness.RunStartupAsync();

    Assert.True(harness.Service.Connection.Areas.Single(area => area.Id == Booth.Id).Streaming);
    Assert.False(harness.Service.Connection.Areas.Single(area => area.Id == Living.Id).Streaming);
  }

  [Fact]
  public async Task RemembersTheChosenAreaAcrossARestart() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Settings.Stored = new HueSettings(Living.Id);
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living, Booth];

    await harness.RunStartupAsync();

    Assert.Equal(Living.Id, harness.Service.Connection.SelectedAreaId);
  }

  [Fact]
  public async Task DropsARememberedAreaTheBridgeNoLongerHas() {
    // Deleted in the Hue app between sessions. Offering to stream to it would fail at
    // the one moment nobody is watching a screen.
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Settings.Stored = new HueSettings(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];

    await harness.RunStartupAsync();

    Assert.Null(harness.Service.Connection.SelectedAreaId);
  }

  [Fact]
  public async Task ChoosingAnAreaRemembersIt() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living, Booth];
    await harness.RunStartupAsync();

    bool accepted = await harness.Service.SelectAreaAsync(Booth.Id, TestContext.Current.CancellationToken);

    Assert.True(accepted);
    Assert.Equal(Booth.Id, harness.Settings.Stored.EntertainmentAreaId);
    Assert.Equal(Booth.Id, harness.Service.Connection.SelectedAreaId);
  }

  [Fact]
  public async Task RefusesAnAreaTheBridgeNeverReported() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];
    await harness.RunStartupAsync();

    bool accepted = await harness.Service.SelectAreaAsync(Booth.Id, TestContext.Current.CancellationToken);

    Assert.False(accepted);
    Assert.Null(harness.Settings.Stored.EntertainmentAreaId);
  }

  [Fact]
  public async Task ForgettingClearsBothTheKeysAndTheChoice() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Settings.Stored = new HueSettings(Living.Id);
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];
    await harness.RunStartupAsync();

    await harness.Service.ForgetAsync(TestContext.Current.CancellationToken);

    Assert.Null(harness.Credentials.Stored);
    Assert.Null(harness.Settings.Stored.EntertainmentAreaId);
    Assert.Equal(HueConnectionState.Unpaired, harness.Service.Connection.State);
    Assert.Null(harness.Service.Connection.Bridge);
    Assert.Empty(harness.Service.Connection.Areas);
  }

  /// <summary>
  /// The case a real network produced within an hour of the feature existing: mDNS
  /// filtered, the cloud lookup's fifteen-minute budget spent on earlier restarts, and
  /// a paired bridge sitting at an address Flux had been talking to minutes before.
  /// </summary>
  [Fact]
  public async Task FallsBackToTheLastWorkingAddressWhenDiscoveryFindsNothing() {
    Harness harness = new Harness(
      stored: settings => settings.Stored = new HueSettings(null, null, LastBridgeAddress: "192.168.1.51"));
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [];
    harness.Client.AtAddress["192.168.1.51"] = Bridge;
    harness.Client.Areas = [Living];

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Ready, harness.Service.Connection.State);
    Assert.Equal(Bridge, harness.Service.Connection.Bridge);
  }

  [Fact]
  public async Task DoesNotTalkToWhateverNowHoldsAnOldAddress() {
    // The hint is verified rather than believed: a bridge that genuinely moved must be
    // reported missing, not confused with the next device to take that lease.
    Harness harness = new Harness(
      stored: settings => settings.Stored = new HueSettings(null, null, LastBridgeAddress: "192.168.1.51"));
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [];
    harness.Client.AtAddress["192.168.1.51"] = new HueBridge("ecb5fafffe0dbeef", "192.168.1.51");

    await harness.RunStartupAsync();

    Assert.Equal(HueConnectionState.Failed, harness.Service.Connection.State);
  }

  [Fact]
  public async Task RemembersWhereTheBridgeAnsweredForNextTime() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];

    await harness.RunStartupAsync();

    Assert.Equal(Bridge.Address, harness.Settings.Stored.LastBridgeAddress);
  }

  /// <summary>
  /// A discovery that comes back to a failed state must bring the reason with it.
  ///
  /// Without this the screen reads "the bridge conversation failed" and nothing else,
  /// which is the least useful sentence a failure can produce - and it happened
  /// exactly when somebody pressed the one button that looked like it might help.
  /// </summary>
  [Fact]
  public async Task LookingForBridgesAgainDoesNotEraseWhyItFailed() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [new HueBridge("ecb5fafffe0dbeef", "192.168.1.60")];
    await harness.RunStartupAsync();

    string? reason = harness.Service.Connection.Message;
    Assert.NotNull(reason);

    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    Assert.Equal(HueConnectionState.Failed, harness.Service.Connection.State);
    Assert.Equal(reason, harness.Service.Connection.Message);
  }

  [Fact]
  public async Task AnnouncesEveryChangeSoAPageNeverHasToAsk() {
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];

    await harness.RunStartupAsync();

    Assert.Contains(harness.Seen, connection => connection.State == HueConnectionState.Connecting);
    Assert.Contains(harness.Seen, connection => connection.State == HueConnectionState.Ready);
  }

  [Fact]
  public void TheLatencyOffsetStartsFromTheConfiguredOneWhenNobodyHasMovedTheSlider() {
    Harness harness = new Harness(options => options.LatencyOffsetMs = 90);

    Assert.Equal(90, harness.Service.Connection.LatencyOffsetMs);
  }

  [Fact]
  public void AStoredLatencyOffsetWinsOverTheConfiguredOne() {
    // The setting is a starting value and the slider is the instrument. Reading them
    // the other way round would undo a tuned number on every restart.
    Harness harness = new Harness(
      options => options.LatencyOffsetMs = 90,
      settings => settings.Stored = new HueSettings(EntertainmentAreaId: null, LatencyOffsetMs: 40));

    Assert.Equal(40, harness.Service.Connection.LatencyOffsetMs);
  }

  [Fact]
  public void AStoredZeroIsADecisionAndNotAnAbsentValue() {
    // The reason the stored offset is nullable at all: without that, a rig that needs
    // no correction could never be told so while a non-zero default was configured.
    Harness harness = new Harness(
      options => options.LatencyOffsetMs = 90,
      settings => settings.Stored = new HueSettings(EntertainmentAreaId: null, LatencyOffsetMs: 0));

    Assert.Equal(0, harness.Service.Connection.LatencyOffsetMs);
  }

  [Fact]
  public void SettingTheLatencyOffsetRemembersItAndTellsEveryPage() {
    Harness harness = new Harness();

    bool accepted = harness.Service.TrySetLatencyOffset(-120);

    Assert.True(accepted);
    Assert.Equal(-120, harness.Service.Connection.LatencyOffsetMs);
    Assert.Equal(-120, harness.Settings.Stored.LatencyOffsetMs);
    Assert.Contains(harness.Seen, connection => connection.LatencyOffsetMs == -120);
  }

  [Theory]
  [InlineData(501)]
  [InlineData(-501)]
  public void RefusesALatencyOffsetAimedAtADifferentBeat(int offsetMs) {
    Harness harness = new Harness(options => options.LatencyOffsetMs = 90);

    bool accepted = harness.Service.TrySetLatencyOffset(offsetMs);

    Assert.False(accepted);
    Assert.Equal(90, harness.Service.Connection.LatencyOffsetMs);
    Assert.Null(harness.Settings.Stored.LatencyOffsetMs);
  }

  [Fact]
  public async Task ForgettingABridgeKeepsTheTunedLatencyOffset() {
    // The offset measures the room and the fixtures, not the bridge. Re-pairing after
    // a router reboot must not quietly throw away a number somebody set by eye.
    Harness harness = new Harness();
    harness.Credentials.Stored = Credentials;
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];
    await harness.RunStartupAsync();
    harness.Service.TrySetLatencyOffset(75);

    await harness.Service.ForgetAsync(TestContext.Current.CancellationToken);

    Assert.Null(harness.Credentials.Stored);
    Assert.Null(harness.Settings.Stored.EntertainmentAreaId);
    Assert.Equal(75, harness.Settings.Stored.LatencyOffsetMs);
    Assert.Equal(75, harness.Service.Connection.LatencyOffsetMs);
  }

  /// <summary>
  /// The sequence that used to undo a forget: a bridge on screen from a failed
  /// connection, somebody re-pairing from a phone, and the forget button pressed on the
  /// laptop while the countdown is still polling. Taking no gate and cancelling nothing,
  /// the forget cleared the key and the poll wrote it straight back a moment later.
  /// </summary>
  [Fact]
  public async Task ForgettingEndsAPairingCountdownRatherThanLettingItWriteTheKeyBack() {
    Harness harness = new Harness();
    harness.Client.Bridges = [Bridge];
    harness.Client.Areas = [Living];
    harness.Client.PairAnswers.Enqueue(Credentials);
    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    // The link press is held until the forget has been asked for, which is the ordering
    // that matters: any other one is a race this test would pass without the fix.
    harness.Client.HoldTheLinkButton();
    Task<bool> pairing = harness.Service.PairAsync(Bridge.Id, TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => harness.Client.PairAttempts > 0);

    Task forgetting = harness.Service.ForgetAsync(TestContext.Current.CancellationToken);
    harness.Client.ReleaseTheLinkButton();

    await forgetting;

    Assert.Null(harness.Credentials.Stored);
    Assert.Equal(HueConnectionState.Unpaired, harness.Service.Connection.State);
    Assert.Null(harness.Service.Connection.Bridge);
    Assert.Null(harness.Service.Connection.PairingSecondsRemaining);

    // Observed rather than abandoned: the pairing loop is cancelled mid-flight, and a
    // cancellation is the expected way for it to end here.
    await IgnoringCancellationAsync(pairing);
  }

  /// <summary>
  /// The other half of the same fix. Waiting for the gate alone would mean a forget
  /// pressed during a countdown sat there for the rest of the pairing window - a minute
  /// of a button that looks broken, on the screen of somebody who is standing in front
  /// of a bridge they have decided not to use.
  /// </summary>
  [Fact]
  public async Task ForgettingDoesNotWaitOutACountdownNobodyIsGoingToFinish() {
    Harness harness = new Harness(options => options.PairingWindow = TimeSpan.FromMinutes(1));
    harness.Client.Bridges = [Bridge];
    await harness.Service.DiscoverAsync(TestContext.Current.CancellationToken);

    Task<bool> pairing = harness.Service.PairAsync(Bridge.Id, TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => harness.Service.Connection.State == HueConnectionState.Pairing);

    Stopwatch waited = Stopwatch.StartNew();
    await harness.Service.ForgetAsync(TestContext.Current.CancellationToken);

    // Generous, because what is being caught is a wait of a whole minute rather than a
    // slow machine.
    Assert.True(waited.Elapsed < TimeSpan.FromSeconds(10), $"the forget waited {waited.Elapsed.TotalSeconds:F1}s for the countdown to run out");
    Assert.Equal(HueConnectionState.Unpaired, harness.Service.Connection.State);

    await IgnoringCancellationAsync(pairing);
  }

  private static async Task IgnoringCancellationAsync(Task task) {
    try {
      await task;
    } catch(OperationCanceledException) {
    }
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    for(int attempt = 0; attempt < 250 && !condition(); attempt++) {
      await Task.Delay(20, TestContext.Current.CancellationToken);
    }

    Assert.True(condition(), "the bridge never reached the state this test was waiting for");
  }

  private sealed class Harness {
    /// <param name="configure">Adjusts the options before the service reads them.</param>
    /// <param name="stored">
    /// Runs before the service is built, for the settings it reads once at
    /// construction. Assigning to <see cref="Settings"/> afterwards is too late for
    /// the latency offset and would pass while proving nothing.
    /// </param>
    public Harness(Action<HueOptions>? configure = null, Action<InMemorySettingsStore>? stored = null) {
      HueOptions options = new HueOptions {
        // A window measured in milliseconds, polled fast. The real values are a
        // minute and a second, and both are settings for exactly this reason.
        PairingWindow = TimeSpan.FromMilliseconds(300),
        PairingPollInterval = TimeSpan.FromMilliseconds(10),
      };
      configure?.Invoke(options);
      stored?.Invoke(Settings);

      Service = new HueBridgeService(Client, Credentials, Settings, Options.Create(options), new FakeClock(), NullLogger<HueBridgeService>.Instance);
      Service.Changed += connection => Seen.Add(connection);
    }

    public FakeHueBridgeClient Client { get; } = new FakeHueBridgeClient();

    public InMemoryCredentialStore Credentials { get; } = new InMemoryCredentialStore();

    public InMemorySettingsStore Settings { get; } = new InMemorySettingsStore();

    public HueBridgeService Service { get; }

    public List<HueConnection> Seen { get; } = [];

    /// <summary>
    /// Starts the service and waits for its startup work to finish.
    ///
    /// <c>StartAsync</c> returns at the first await by design - a host must not be
    /// held up by a bridge - so the test awaits the background task itself rather
    /// than sleeping and hoping.
    /// </summary>
    public async Task RunStartupAsync() {
      await Service.StartAsync(TestContext.Current.CancellationToken);
      if(Service.ExecuteTask is { } running) {
        await running;
      }
    }
  }
}
