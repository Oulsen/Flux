namespace Flux.Hue;

/// <summary>
/// An open entertainment session, as the 50 Hz loop needs to see one.
///
/// Deliberately not async. <see cref="Send"/> hands a datagram to a socket and returns;
/// making it awaitable would put a state machine and a possible thread hop inside a
/// loop whose entire job is to be where it said it would be, twenty milliseconds from
/// now.
/// </summary>
public interface IHueStream: IDisposable {
  /// <summary>
  /// How many channels the area drives, read from the bridge rather than from the
  /// area list, because this is what the frame actually covers.
  /// </summary>
  int ChannelCount { get; }

  /// <summary>Points every channel at one colour. Solid colours only in this phase.</summary>
  void SetAll(HueColor color);

  /// <summary>
  /// Sends the current colours to the bridge, once.
  ///
  /// Called whether or not anything changed, and that is not waste: the bridge ends a
  /// session that goes quiet, so an unchanged frame is what keeps the session alive
  /// through a passage where the lights are meant to hold still.
  /// </summary>
  void Send();
}

/// <summary>
/// Opens entertainment sessions, and the second of the two places HueApi exists.
///
/// A factory rather than a method on <see cref="IHueBridgeClient"/> because the two
/// have nothing in common but the bridge: that one is stateless HTTP, and this hands
/// back a live DTLS socket somebody has to close.
/// </summary>
public interface IHueStreamFactory {
  /// <summary>
  /// Opens a session to one entertainment area.
  ///
  /// Throws <see cref="HueBridgeException"/> when the bridge refuses, which most often
  /// means its one session is already taken - the failure the busy pre-flight exists to
  /// catch earlier and in words.
  /// </summary>
  Task<IHueStream> OpenAsync(HueBridge bridge, HueCredentials credentials, Guid areaId, CancellationToken cancellationToken);
}
