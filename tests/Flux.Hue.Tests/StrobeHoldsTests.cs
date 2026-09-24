using Xunit;

namespace Flux.Hue.Tests;

/// <summary>
/// The strobe hold as a lease, which is the property that keeps a sleeping phone from
/// leaving a room strobing: a hold that stops being renewed ends on its own.
/// </summary>
public class StrobeHoldsTests {
  [Fact]
  public void NobodyIsHoldingUntilSomebodyPresses() {
    StrobeHolds holds = new StrobeHolds(new FakeClock());

    Assert.False(holds.IsHeld);
  }

  [Fact]
  public void AHoldThatIsNeverRenewedEndsWhenItsLeaseRunsOut() {
    FakeClock clock = new FakeClock();
    StrobeHolds holds = new StrobeHolds(clock);

    holds.Press(new object());

    clock.Advance(TimeSpan.FromMilliseconds(StrobeHolds.LeaseMs - 1));
    Assert.True(holds.IsHeld);

    clock.Advance(TimeSpan.FromMilliseconds(1));
    Assert.False(holds.IsHeld);
  }

  /// <summary>
  /// Renewing at the documented interval keeps a hold alive indefinitely - ten seconds
  /// here, far past any single lease - which is what a page holding the button does.
  /// </summary>
  [Fact]
  public void RenewingAtTheDocumentedIntervalKeepsAHoldAlive() {
    FakeClock clock = new FakeClock();
    StrobeHolds holds = new StrobeHolds(clock);
    object finger = new object();

    for(int renewal = 0; renewal < 20; renewal++) {
      holds.Press(finger);
      clock.Advance(TimeSpan.FromMilliseconds(StrobeHolds.RenewEveryMs));
      Assert.True(holds.IsHeld, $"The hold lapsed after {renewal + 1} renewal(s).");
    }
  }

  [Fact]
  public void TwoPagesHoldingStayHeldUntilBothLetGo() {
    StrobeHolds holds = new StrobeHolds(new FakeClock());
    object phone = new object();
    object laptop = new object();

    holds.Press(phone);
    holds.Press(laptop);

    holds.Release(phone);
    Assert.True(holds.IsHeld);

    holds.Release(laptop);
    Assert.False(holds.IsHeld);
  }

  [Fact]
  public void ReleasingSomebodyWhoIsNotHoldingChangesNothing() {
    StrobeHolds holds = new StrobeHolds(new FakeClock());
    object finger = new object();

    holds.Press(finger);
    holds.Release(new object());

    Assert.True(holds.IsHeld);
  }
}
