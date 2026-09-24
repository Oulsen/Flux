using Flux.Beat;

namespace Flux.Hue;

/// <summary>
/// Who is holding the strobe button right now, and the one place that can say whether
/// anyone is.
///
/// A hold is a lease rather than a switch, and that is the whole design. A switch is
/// turned off by a release message, and a release message is exactly what does not
/// arrive when it matters most: a phone whose screen sleeps mid-press, a tab closed with
/// a finger still down, Wi-Fi dropping on the stairs. A socket that closes cleanly is
/// caught by its connection ending, but one that goes quiet without closing is not, and
/// nothing in the web layer notices a silent socket for a long time. A room left
/// strobing because a phone went to sleep is not a cosmetic fault - a strobe is a
/// photosensitivity hazard, and the person who could stop it may not be in the room.
///
/// So a press has to keep being made. A holding page repeats it every
/// <see cref="RenewEveryMs"/>, and a hold that goes <see cref="LeaseMs"/> without one
/// is over, whatever happened to the release. A clean release and a closed connection
/// still end a hold at once; the lease is what ends it when neither arrives.
///
/// Keyed by holder rather than counted, so two pages holding at once stay held until
/// both have let go, and one page pressing twice is still one hold.
/// </summary>
public sealed class StrobeHolds {
  /// <summary>
  /// How often a holding page repeats its press, in milliseconds.
  ///
  /// Owned here rather than by the page because it only means anything against
  /// <see cref="LeaseMs"/>: the page mirrors it, and the two stay in one file so
  /// that nobody can move one without seeing the other.
  /// </summary>
  public const int RenewEveryMs = 500;

  /// <summary>
  /// How long a hold survives without being renewed, in milliseconds.
  ///
  /// Three renewals, so ordinary Wi-Fi jitter - one late message, even two - never
  /// breaks a hold somebody is still making, and a page that has genuinely gone away
  /// stops the strobe within a second and a half. Longer would leave a sleeping phone
  /// strobing the room for noticeably longer; shorter starts dropping real holds on a
  /// busy network.
  /// </summary>
  public const int LeaseMs = 1500;

  private const double MicrosPerMilli = 1000.0;

  private readonly IMonotonicClock clock;
  private readonly Dictionary<object, double> expiries = [];
  private readonly object gate = new();

  public StrobeHolds(IMonotonicClock clock) {
    this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
  }

  /// <summary>
  /// Whether anybody is holding the strobe at this instant.
  ///
  /// Read on the stream loop, fifty times a second, and cheap enough for that: the
  /// dictionary holds one entry per finger on a button, which is almost always none.
  /// Expired holds are removed as they are found, so a holder that vanished without a
  /// release costs nothing after its lease runs out.
  /// </summary>
  public bool IsHeld {
    get {
      double now = this.clock.NowMicros;

      lock(this.gate) {
        if(this.expiries.Count == 0) {
          return false;
        }

        bool held = false;
        List<object>? expired = null;

        foreach(KeyValuePair<object, double> hold in this.expiries) {
          if(hold.Value > now) {
            held = true;
          } else {
            (expired ??= []).Add(hold.Key);
          }
        }

        foreach(object holder in expired ?? []) {
          this.expiries.Remove(holder);
        }

        return held;
      }
    }
  }

  /// <summary>
  /// Starts a hold, or renews one, for another <see cref="LeaseMs"/>.
  /// </summary>
  /// <param name="holder">Whoever is pressing - one per connection, compared by reference.</param>
  public void Press(object holder) {
    ArgumentNullException.ThrowIfNull(holder);

    double expiry = this.clock.NowMicros + (LeaseMs * MicrosPerMilli);

    lock(this.gate) {
      this.expiries[holder] = expiry;
    }
  }

  /// <summary>
  /// Ends a hold at once. Releasing a holder that holds nothing is not an error: a
  /// connection ending releases whether or not it was ever pressing.
  /// </summary>
  public void Release(object holder) {
    ArgumentNullException.ThrowIfNull(holder);

    lock(this.gate) {
      this.expiries.Remove(holder);
    }
  }
}
