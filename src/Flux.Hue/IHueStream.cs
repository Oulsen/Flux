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

  /// <summary>
  /// The channels this session drives, in the order <see cref="Set"/> writes them.
  ///
  /// Carries each one's position, which the bridge reports and nothing in Flux read
  /// until now. A channel the bridge places nowhere reports
  /// <see cref="LightPosition.Centre"/> rather than being left out: an unplaced light is
  /// still a light, and dropping it from the room would mean streaming to fewer
  /// channels than the area has.
  /// </summary>
  IReadOnlyList<HueChannel> Channels { get; }

  /// <summary>Points every channel at one colour.</summary>
  void SetAll(HueColor color);

  /// <summary>
  /// Points each channel at its own colour, in the order of <see cref="Channels"/>.
  ///
  /// Shorter than <see cref="Channels"/> is a programming error rather than a partial
  /// update: a frame that lit six of nine lights and left three showing the previous
  /// beat would be a flicker nobody could trace back to here.
  /// </summary>
  void Set(ReadOnlySpan<HueColor> colors);

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
