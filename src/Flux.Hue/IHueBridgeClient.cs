namespace Flux.Hue;

/// <summary>
/// Everything Flux asks a bridge over HTTP, and the only seam where the Hue library
/// exists.
///
/// The pattern is <c>AudioCaptureSession</c>'s, deliberately: one class names the
/// third-party types and everything above it works in shapes of Flux's own. That is
/// what made the audio engine testable on a machine with no sound card, and it is
/// what makes the pairing state machine, the countdown and the busy pre-flight
/// testable here without a bridge, a link button, or somebody standing in a hallway.
/// </summary>
public interface IHueBridgeClient {
  /// <summary>
  /// Bridges answering on this network, or an empty list.
  ///
  /// Finding none is an ordinary answer rather than an error: mDNS replies arrive
  /// over time and a bridge that was slow is missed, not broken.
  /// </summary>
  Task<IReadOnlyList<HueBridge>> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken);

  /// <summary>
  /// Asks the bridge to register Flux, once.
  ///
  /// <b>Null means the link button has not been pressed</b>, which is the documented
  /// error type 101 and the expected answer for most of a pairing attempt - so it is
  /// a return value rather than an exception. Anything genuinely wrong throws
  /// <see cref="HueBridgeException"/>: the difference between "keep waiting" and
  /// "stop and tell the user" is the entire design of the pairing loop, and burying
  /// it in exception types would put the decision in the wrong place.
  /// </summary>
  Task<HueCredentials?> TryPairAsync(HueBridge bridge, CancellationToken cancellationToken);

  /// <summary>
  /// Asks whatever is at an address to identify itself, without credentials.
  ///
  /// Null for anything that is not a bridge, or does not answer. Unauthenticated on
  /// purpose: a bridge's identity is not privileged, and this runs in the one case
  /// where discovery has already failed and the only thing left is an address that
  /// worked before.
  /// </summary>
  Task<HueBridge?> TryDescribeAsync(string address, CancellationToken cancellationToken);

  /// <summary>The bridge's entertainment areas, including whether each is already streaming.</summary>
  Task<IReadOnlyList<HueArea>> GetAreasAsync(HueBridge bridge, HueCredentials credentials, CancellationToken cancellationToken);
}

/// <summary>
/// A bridge conversation that failed for a reason worth showing the user.
///
/// Deliberately not thrown for the link button, which is the one failure that is
/// not one.
/// </summary>
public sealed class HueBridgeException: Exception {
  public HueBridgeException(string message): base(message) {
  }

  public HueBridgeException(string message, Exception inner): base(message, inner) {
  }
}
