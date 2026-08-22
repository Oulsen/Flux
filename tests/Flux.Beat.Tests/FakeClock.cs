namespace Flux.Beat.Tests;

/// <summary>
/// A clock the test drives by hand, so grid behaviour that depends on elapsed
/// time can be asserted without any of it actually elapsing.
/// </summary>
internal sealed class FakeClock: IMonotonicClock {
  public double NowMicros { get; private set; }

  public void Advance(TimeSpan elapsed) {
    this.NowMicros += elapsed.TotalMilliseconds * 1000.0;
  }

  public void AdvanceMicros(double micros) {
    this.NowMicros += micros;
  }
}
