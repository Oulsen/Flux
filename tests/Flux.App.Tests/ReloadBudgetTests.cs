using Xunit;

namespace Flux.App.Tests;

/// <summary>
/// When the window stops reloading a page that keeps dying. Too strict, and a crash
/// that would have recovered sends the room to the browser; too loose, and a page that
/// dies on load is reloaded all night.
/// </summary>
public sealed class ReloadBudgetTests {
  [Fact]
  public void ReloadsUpToTheLimitWithinThePeriod() {
    ReloadBudget budget = new ReloadBudget();

    for(int i = 0; i < ReloadBudget.Limit; i++) {
      Assert.True(budget.TrySpend(i * 1_000));
    }

    Assert.False(budget.TrySpend(ReloadBudget.Limit * 1_000));
  }

  [Fact]
  public void AReloadIsBackOnceThePeriodHasPassed() {
    ReloadBudget budget = new ReloadBudget();

    for(int i = 0; i < ReloadBudget.Limit; i++) {
      Assert.True(budget.TrySpend(0));
    }

    Assert.False(budget.TrySpend(ReloadBudget.Period - 1));
    Assert.True(budget.TrySpend(ReloadBudget.Period));
  }

  [Fact]
  public void CrashesSpreadAcrossTheNightAreAlwaysReloaded() {
    ReloadBudget budget = new ReloadBudget();

    for(int i = 0; i < 100; i++) {
      Assert.True(budget.TrySpend(i * ReloadBudget.Period / 2));
    }
  }
}
