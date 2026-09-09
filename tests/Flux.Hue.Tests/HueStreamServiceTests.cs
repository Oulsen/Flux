using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// The 50 Hz loop, run for real.
///
/// A test that only inspected the arithmetic would be worthless here. Phase 1 recorded
/// a scheduler that ran 36 iterations in one second and fired zero beats, with no error
/// anywhere, because it re-aimed at the next deadline every time it missed one - and
/// the only thing that caught it was a test that started the thread and counted what
/// came out. This is that test for the light stream.
/// </summary>
public class HueStreamServiceTests {
  private static readonly HueBridge Bridge = new HueBridge("ecb5fafffe0d1234", "192.168.1.51");
  private static readonly HueCredentials Credentials = new HueCredentials("ecb5fafffe0d1234", "app-key", "client-key");
  private static readonly HueArea Booth = new HueArea(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Booth", 3, Streaming: false);
  private static readonly HueArea Lounge = new HueArea(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Lounge", 5, Streaming: false);

  [Fact]
  public async Task SendsFramesRatherThanChasingADeadlineForever() {
    // The assertion that matters is "more than zero". A loop with the Phase 1 bug in it
    // spins at full speed and sends nothing at all, so any real count disproves it.
    Harness harness = await Harness.ReadyAsync();

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    await Task.Delay(TimeSpan.FromMilliseconds(1200), TestContext.Current.CancellationToken);

    HueStreamStatus status = harness.Service.Status();
    harness.Service.Stop();

    Assert.True(harness.Stream.Sends > 20, $"the loop sent {harness.Stream.Sends} frames in 1.2 seconds");
    Assert.True(status.Running);

    // Generous on both sides on purpose: this runs on a shared CI machine, and what is
    // being tested is that the number is measured at all rather than that a build agent
    // can hold 50 Hz. A rate near zero, or one far above the configured rate, are the
    // two failures worth catching - the second would mean the loop is bursting to catch
    // up rather than giving up the frames it missed.
    Assert.InRange(status.Fps, 10.0, 70.0);
  }

  [Fact]
  public async Task ReportsNoFrameRateAtAllWhileItIsNotStreaming() {
    // Zero rather than a stale last reading, and paired with Running: false so a page
    // never has to guess which of the two a zero means.
    Harness harness = await Harness.ReadyAsync();

    Assert.Equal(new HueStreamStatus(Running: false, Fps: 0), harness.Service.Status());

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
    harness.Service.Stop();

    Assert.Equal(new HueStreamStatus(Running: false, Fps: 0), harness.Service.Status());
  }

  [Fact]
  public async Task RefusesToStartWithNoAreaChosen() {
    Harness harness = await Harness.ReadyAsync(chooseArea: false);

    Assert.False(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    Assert.False(harness.Service.Status().Running);
    Assert.Equal(0, harness.Factory.Opens);
  }

  [Fact]
  public async Task RefusesToStartWithNoBridgePaired() {
    Harness harness = new Harness();

    Assert.False(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    Assert.Equal(0, harness.Factory.Opens);
  }

  /// <summary>
  /// The pre-flight, consulted where it actually prevents something.
  ///
  /// Built in step 2 and then not asked at the one moment it mattered, which cost a
  /// real lock-up at the desk: the handshake into a taken session does not fail, it
  /// never answers.
  /// </summary>
  [Fact]
  public async Task RefusesToStartWhenSomethingElseHoldsTheRoom() {
    Harness harness = await Harness.ReadyAsync();
    harness.Client.Areas = [Booth with { Streaming = true }];

    Assert.False(await harness.Service.StartAsync(TestContext.Current.CancellationToken));

    Assert.Equal(0, harness.Factory.Opens);
    Assert.False(harness.Service.Status().Running);

    // Said where somebody can read it, not only to the console. A refusal nobody sees
    // is barely better than the hang it replaced - the button is pressed, nothing
    // happens, and the reason is in a log nobody has open.
    Assert.Contains("Booth", harness.Bridge.Connection.Message, StringComparison.Ordinal);
    Assert.Equal(HueConnectionState.Ready, harness.Bridge.Connection.State);
  }

  /// <summary>
  /// The note goes with the reading it was about: a refusal naming a busy room must not
  /// still be on screen beside a fresh list showing that room as free.
  /// </summary>
  [Fact]
  public async Task TheRefusalClearsOnceTheRoomIsFreeAgain() {
    Harness harness = await Harness.ReadyAsync();
    harness.Client.Areas = [Booth with { Streaming = true }];
    await harness.Service.StartAsync(TestContext.Current.CancellationToken);
    Assert.NotNull(harness.Bridge.Connection.Message);

    harness.Client.Areas = [Booth];
    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    harness.Service.Stop();

    Assert.Null(harness.Bridge.Connection.Message);
  }

  /// <summary>
  /// The busy flag has a lifetime of seconds, so the reading a page is looking at is
  /// worthless by the time somebody presses start. It is re-read first.
  ///
  /// Counted on the room read and deliberately not on discovery: re-discovering to
  /// check one flag is a multi-second mDNS sweep against an address already known, and
  /// it takes the bridge screen down while it runs. Both halves of that were felt at
  /// the desk before this test existed.
  /// </summary>
  [Fact]
  public async Task ChecksTheRoomIsFreeAgainRatherThanTrustingTheListOnScreen() {
    Harness harness = await Harness.ReadyAsync();
    int rooms = harness.Client.AreaCalls;
    int sweeps = harness.Client.DiscoverCalls;

    await harness.Service.StartAsync(TestContext.Current.CancellationToken);
    harness.Service.Stop();

    Assert.True(harness.Client.AreaCalls > rooms, "the rooms were not re-read before the handshake");
    Assert.Equal(sweeps, harness.Client.DiscoverCalls);
  }

  /// <summary>
  /// A handshake that never answers must not leave the service unusable.
  ///
  /// This is the defect found at the desk, reproduced: the library's connect takes no
  /// cancellation token, so a session the bridge will never grant hangs forever. What
  /// the fix guarantees is that "streaming" stays false throughout and that stopping
  /// puts the service back in a state a second attempt can use.
  /// </summary>
  [Fact]
  public async Task AHandshakeThatNeverAnswersIsNotReportedAsStreaming() {
    Harness harness = await Harness.ReadyAsync();
    harness.Factory.Hang = true;

    Task<bool> starting = harness.Service.StartAsync(TestContext.Current.CancellationToken);

    // While the handshake is outstanding the page must not be told light is coming out.
    await Task.Delay(200, TestContext.Current.CancellationToken);
    Assert.False(harness.Service.Status().Running);

    harness.Service.Stop();
    Assert.False(await starting);
    Assert.False(harness.Service.Status().Running);
  }

  [Fact]
  public async Task ClosesTheSessionWhenItStops() {
    // The bridge allows exactly one session, so a stream that stopped without closing
    // would lock every other application out of the lights until Flux exited.
    Harness harness = await Harness.ReadyAsync();

    await harness.Service.StartAsync(TestContext.Current.CancellationToken);
    await Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);
    harness.Service.Stop();

    Assert.True(harness.Stream.Disposed);
  }

  /// <summary>
  /// Nothing the loop thread does may end the process.
  ///
  /// This is the crash, reproduced. The library's Close and Dispose both tear the UDP
  /// transport down, so calling both threw ObjectDisposedException out of the thread's
  /// own finally - an unhandled exception on a dedicated thread, which took the beat
  /// grid, the web server and every open page down with a light stream. The catch that
  /// prevents it is the same lesson Phase 1 recorded for the Link listener.
  /// </summary>
  [Fact]
  public async Task AStreamThatThrowsOnTeardownDoesNotTakeFluxDown() {
    Harness harness = await Harness.ReadyAsync();
    harness.Stream.ThrowOnDispose = true;

    await harness.Service.StartAsync(TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => harness.Stream.Sends > 0);

    harness.Service.Stop();

    await WaitUntilAsync(() => !harness.Service.Status().Running);
    Assert.False(harness.Service.Status().Running);
  }

  /// <summary>A send that fails mid-set stops the stream and leaves Flux standing.</summary>
  [Fact]
  public async Task ASendThatFailsStopsTheStreamRatherThanTheProcess() {
    Harness harness = await Harness.ReadyAsync();
    harness.Stream.ThrowOnSend = true;

    await harness.Service.StartAsync(TestContext.Current.CancellationToken);

    await WaitUntilAsync(() => !harness.Service.Status().Running);
    Assert.False(harness.Service.Status().Running);
  }

  /// <summary>
  /// The reason a session has an identity at all.
  ///
  /// A pump wedged in a send outlives the stop that gave up joining it, and everything
  /// it clears on its way out - the running flag, the frame rate - belongs by then to
  /// whatever somebody started in the meantime. The page reads that as a stream that
  /// died, offers the start button again, and the press behind it aims a second DTLS
  /// handshake at a bridge that grants exactly one session.
  /// </summary>
  [Fact]
  public async Task APumpAbandonedByAStopCannotReportTheStreamAfterItAsStopped() {
    Harness harness = await Harness.ReadyAsync();
    harness.Stream.HoldSends();

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    await WaitUntilAsync(() => harness.Stream.Sends > 0);

    // The join times out: nothing is going to finish the send that pump is inside.
    harness.Service.Stop();
    Assert.False(harness.Service.Status().Running);

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    Assert.True(harness.Service.Status().Running);

    // The abandoned pump only now notices it was cancelled and runs its own teardown.
    harness.Stream.ReleaseSends();
    await WaitUntilAsync(() => harness.Stream.Disposed);
    await Task.Delay(100, TestContext.Current.CancellationToken);

    Assert.True(harness.Service.Status().Running, "the abandoned pump reported the live stream as stopped");
    Assert.False(harness.Factory.Reopened.Disposed, "the abandoned pump closed the session that replaced it");

    harness.Service.Stop();
  }

  /// <summary>
  /// Choosing another room while a session is open used to leave the pump streaming to
  /// the previous one: the page showed the new room selected and a stop button, and the
  /// lights in the old room kept running with nothing on screen accounting for them.
  /// </summary>
  [Fact]
  public async Task ChoosingAnotherRoomClosesTheSessionOpenOnTheOldOne() {
    Harness harness = await Harness.ReadyAsync();
    harness.Client.Areas = [Booth, Lounge];
    await harness.Bridge.TryRefreshAreasAsync(TestContext.Current.CancellationToken);

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    await WaitUntilAsync(() => harness.Stream.Sends > 0);

    Assert.True(await harness.Bridge.SelectAreaAsync(Lounge.Id, TestContext.Current.CancellationToken));

    await WaitUntilAsync(() => !harness.Service.Status().Running);
    Assert.True(harness.Stream.Disposed);
  }

  /// <summary>
  /// Forgetting the bridge is the worse half of the same defect. The credentials go and
  /// the screen returns to unpaired, while the session stays open holding the bridge's
  /// one entertainment slot - so the next pairing cannot stream and nothing says why.
  /// </summary>
  [Fact]
  public async Task ForgettingTheBridgeReleasesTheSessionItWasHolding() {
    Harness harness = await Harness.ReadyAsync();

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    await WaitUntilAsync(() => harness.Stream.Sends > 0);

    await harness.Bridge.ForgetAsync(TestContext.Current.CancellationToken);

    await WaitUntilAsync(() => !harness.Service.Status().Running);
    Assert.True(harness.Stream.Disposed);
  }

  /// <summary>
  /// Two phones on one page is the normal case, and the second press lands during the
  /// first handshake. Answering it false reaches that phone as the refusal text - choose
  /// a reachable room, stop whatever else is streaming - a moment before the lights come
  /// on, which is a red message about something that is working.
  /// </summary>
  [Fact]
  public async Task ASecondStartDuringTheHandshakeIsNotARefusal() {
    Harness harness = await Harness.ReadyAsync();
    harness.Factory.Hang = true;

    Task<bool> first = harness.Service.StartAsync(TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => harness.Factory.Opens > 0);

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));

    // And it is the same attempt rather than a competing one, which is what the bridge's
    // single session makes worth asserting.
    Assert.Equal(1, harness.Factory.Opens);

    harness.Service.Stop();
    Assert.False(await first);
  }

  [Fact]
  public async Task StoppingSomethingThatIsNotRunningIsNotAFailure() {
    Harness harness = await Harness.ReadyAsync();

    harness.Service.Stop();
    harness.Service.Stop();

    Assert.False(harness.Service.Status().Running);
    await Task.CompletedTask;
  }

  /// <summary>
  /// A refusal is the answer to the request, not something discovered later.
  ///
  /// Awaiting the handshake is what buys this: the caller gets a false it can put on
  /// screen as a sentence, instead of a page that says "streaming" and then quietly
  /// stops saying it a moment afterwards for reasons nobody saw.
  /// </summary>
  [Fact]
  public async Task ABridgeThatRefusesTheSessionSaysSoInTheAnswer() {
    Harness harness = await Harness.ReadyAsync();
    harness.Factory.Throws = new HueBridgeException("Another application is already streaming.");

    Assert.False(await harness.Service.StartAsync(TestContext.Current.CancellationToken));

    Assert.False(harness.Service.Status().Running);
    Assert.Equal(0, harness.Stream.Sends);
  }

  [Fact]
  public async Task ColoursReachTheBridgeInsideTheRangeItUnderstands() {
    Harness harness = await Harness.ReadyAsync();
    harness.Service.Color = new HueColor(1.4, -0.2, double.NaN);

    await harness.Service.StartAsync(TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => harness.Stream.Sends > 0);
    harness.Service.Stop();

    Assert.Equal(new HueColor(1, 0, 0), harness.Stream.Last);
  }

  /// <summary>
  /// A page draws the room from this reading, so it has to be the colour the bridge
  /// received and not a second opinion about what it should have been. Asserting it
  /// against the stream's own last frame is what makes the difference observable: a
  /// reading recomputed on demand would satisfy an equality against the engine and fail
  /// this one.
  /// </summary>
  [Fact]
  public async Task TheColourReportedIsTheOneTheBridgeWasSent() {
    Harness harness = await Harness.ReadyAsync();
    harness.Service.Color = new HueColor(1.4, 0.25, -0.2);

    await harness.Service.StartAsync(TestContext.Current.CancellationToken);
    await WaitUntilAsync(() => harness.Stream.Sends > 0);

    HueStreamStatus status = harness.Service.Status();
    harness.Service.Stop();

    // Clamped, because clamped is what went out. A reading taken before the clamp hands
    // a page a red of 1.4 and a blue of -0.2, neither of which any bulb ever showed.
    Assert.Equal(new HueColor(1, 0.25, 0), status.Color);
    Assert.Equal(harness.Stream.Last, status.Color);
  }

  /// <summary>
  /// Published after the send rather than before it, which is the whole difference
  /// between reporting what happened and reporting what was about to. A pump wedged
  /// inside its first send has told the bridge nothing at all, and a page drawing a
  /// colour from it would be showing a room that never changed.
  /// </summary>
  [Fact]
  public async Task NoColourIsReportedUntilAFrameHasActuallyGoneOut() {
    Harness harness = await Harness.ReadyAsync();
    harness.Stream.HoldSends();

    Assert.True(await harness.Service.StartAsync(TestContext.Current.CancellationToken));
    await WaitUntilAsync(() => harness.Stream.Sends > 0);

    HueStreamStatus status = harness.Service.Status();

    Assert.True(status.Running, "the session is open - it is the frame that has not landed");
    Assert.Null(status.Color);

    harness.Stream.ReleaseSends();
    await WaitUntilAsync(() => harness.Service.Status().Color is not null);

    harness.Service.Stop();
  }

  private static async Task WaitUntilAsync(Func<bool> condition) {
    Stopwatch waited = Stopwatch.StartNew();

    while(!condition() && waited.Elapsed < TimeSpan.FromSeconds(5)) {
      await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
    }

    Assert.True(condition(), "the stream never reached the state this test was waiting for");
  }

  private sealed class Harness {
    public Harness() {
      HueOptions options = new HueOptions { PairingWindow = TimeSpan.FromMilliseconds(200) };

      this.Bridge = new HueBridgeService(
        this.Client,
        this.Credentials,
        this.Settings,
        Options.Create(options),
        NullLogger<HueBridgeService>.Instance);

      this.Service = new HueStreamService(
        this.Factory,
        this.Bridge,
        this.Credentials,
        Options.Create(options),
        NullLogger<HueStreamService>.Instance);
    }

    public FakeHueBridgeClient Client { get; } = new FakeHueBridgeClient();

    public InMemoryCredentialStore Credentials { get; } = new InMemoryCredentialStore();

    public InMemorySettingsStore Settings { get; } = new InMemorySettingsStore();

    public FakeStreamFactory Factory { get; } = new FakeStreamFactory();

    public FakeStream Stream => this.Factory.Opened;

    public HueBridgeService Bridge { get; }

    public HueStreamService Service { get; }

    /// <summary>A paired bridge that is reachable, with an area chosen unless asked otherwise.</summary>
    public static async Task<Harness> ReadyAsync(bool chooseArea = true) {
      Harness harness = new Harness();
      harness.Credentials.Stored = HueStreamServiceTests.Credentials;
      harness.Client.Bridges = [HueStreamServiceTests.Bridge];
      harness.Client.Areas = [Booth];

      await harness.Bridge.StartAsync(TestContext.Current.CancellationToken);
      if(harness.Bridge.ExecuteTask is { } running) {
        await running;
      }

      if(chooseArea) {
        await harness.Bridge.SelectAreaAsync(Booth.Id, TestContext.Current.CancellationToken);
      }

      return harness;
    }
  }

  private sealed class FakeStreamFactory: IHueStreamFactory {
    public FakeStream Opened { get; } = new FakeStream();

    /// <summary>
    /// What a second open gets, so a test can tell the two sessions apart.
    ///
    /// A distinct object rather than the same one, because the case worth testing is
    /// precisely the one where a pump from the first session is still alive while the
    /// second is running: sharing a stream would let either of them account for what
    /// the other did.
    /// </summary>
    public FakeStream Reopened { get; } = new FakeStream();

    public int Opens { get; private set; }

    public Exception? Throws { get; set; }

    /// <summary>A handshake that never answers, which is what a taken session does.</summary>
    public bool Hang { get; set; }

    public async Task<IHueStream> OpenAsync(HueBridge bridge, HueCredentials credentials, Guid areaId, CancellationToken cancellationToken) {
      this.Opens++;

      if(this.Throws is not null) {
        throw this.Throws;
      }

      if(this.Hang) {
        // Deliberately ignores the token, exactly as the real one does.
        await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
      }

      return this.Opens == 1 ? this.Opened : this.Reopened;
    }
  }

  /// <summary>A session that counts what the loop asked it to do.</summary>
  private sealed class FakeStream: IHueStream {
    /// <summary>Open unless a test closes it, which wedges the pump inside a send.</summary>
    private readonly ManualResetEventSlim sending = new ManualResetEventSlim(initialState: true);

    private int sends;

    public int ChannelCount => 3;

    public int Sends => Volatile.Read(ref this.sends);

    public HueColor Last { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>The real one did exactly this, from inside the thread's finally.</summary>
    public bool ThrowOnDispose { get; set; }

    public bool ThrowOnSend { get; set; }

    public void SetAll(HueColor color) => this.Last = color;

    /// <summary>
    /// Wedges the pump in its next send and leaves it there, which is a transport that
    /// has stopped answering - the state a stop cannot join its way out of.
    /// </summary>
    public void HoldSends() => this.sending.Reset();

    public void ReleaseSends() => this.sending.Set();

    public void Send() {
      Interlocked.Increment(ref this.sends);

      if(this.ThrowOnSend) {
        throw new ObjectDisposedException(nameof(FakeStream));
      }

      this.sending.Wait();
    }

    public void Dispose() {
      this.Disposed = true;

      if(this.ThrowOnDispose) {
        throw new ObjectDisposedException(nameof(FakeStream));
      }
    }
  }
}
