using Flux.Beat;

namespace Flux.Hue.Tests;

/// <summary>
/// Time that only moves when a test says so.
///
/// The light engine is a function of beats and the clock and nothing else, which is
/// the whole reason it was built as a pull rather than a push: with this in front of
/// it, any moment of any brightness envelope is reachable exactly, with no threads and
/// nothing sleeping. A copy of the same idea in <c>Flux.Beat.Tests</c> rather than a
/// shared one, because test doubles are not a public contract and a project reference
/// between two test assemblies to save nine lines is a worse trade than the nine lines.
/// </summary>
internal sealed class FakeClock: IMonotonicClock {
  public double NowMicros { get; private set; }

  public void Advance(TimeSpan by) {
    NowMicros += by.TotalMilliseconds * 1000.0;
  }
}
