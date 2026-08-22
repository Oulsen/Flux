using System.Diagnostics;

namespace Flux.Beat;

/// <summary>
/// The single time source everything downstream of Link shares.
///
/// It is an interface for one reason: the grid and the scheduler are pure
/// arithmetic over timestamps, and tests that have to wait for real seconds to
/// pass are tests nobody runs.
/// </summary>
public interface IMonotonicClock {
  /// <summary>Microseconds since an arbitrary fixed point. Never goes backwards.</summary>
  double NowMicros { get; }
}

/// <summary>
/// <see cref="Stopwatch"/> read as microseconds.
///
/// This is the same counter Link's Windows implementation derives its host time
/// from, which is the whole reason a passive listener on this machine can map
/// session time onto local time without the measurement exchange.
/// </summary>
public sealed class MonotonicClock: IMonotonicClock {
  private const double MicrosPerSecond = 1_000_000.0;

  private static readonly double MicrosPerTick = MicrosPerSecond / Stopwatch.Frequency;

  public double NowMicros => Stopwatch.GetTimestamp() * MicrosPerTick;
}
