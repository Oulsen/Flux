namespace Flux.App;

/// <summary>
/// How often the window's page is loaded again after it stops, before the window is
/// given up on. A page that stops now and then is reloaded however long the night
/// runs; one that stops as soon as it loads is caught inside a minute rather than
/// reloaded until morning.
///
/// Three in a minute is reasoned about, not tuned: a single crash, or two close
/// together, is worth another try, and a third within a minute is a page that will not
/// stay up, where the browser is a better answer than a fourth try. It is the first
/// number to move if the window gives up on a page that would have recovered.
/// </summary>
internal sealed class ReloadBudget {
  /// <summary>How many reloads fit inside <see cref="Period"/>.</summary>
  internal const int Limit = 3;

  /// <summary>The period the reloads are counted over, in milliseconds.</summary>
  internal const long Period = 60_000;

  private readonly Queue<long> spent = new Queue<long>();

  /// <summary>
  /// Takes one reload if the budget has one left, and says whether it did.
  /// </summary>
  /// <param name="now">The time in milliseconds on a clock that only moves forward.</param>
  internal bool TrySpend(long now) {
    while(this.spent.Count > 0 && now - this.spent.Peek() >= Period) {
      this.spent.Dequeue();
    }

    if(this.spent.Count >= Limit) {
      return false;
    }

    this.spent.Enqueue(now);

    return true;
  }
}
