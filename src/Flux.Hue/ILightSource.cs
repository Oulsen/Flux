namespace Flux.Hue;

/// <summary>
/// Whatever decides what colour the lights are, asked once per frame.
///
/// A pull rather than a push, and the change is deliberate: the earlier design had
/// the engine writing <see cref="HueStreamService.Color"/> whenever a beat arrived,
/// which works only for a colour that is constant between beats. It is not. A beat
/// starts a brightness pulse that fades and a colour that slides toward its target,
/// so something has to evaluate the envelope on every frame - and if that were the
/// engine, the engine would need a second 50 Hz timing loop running beside the
/// stream's own, with its own jitter and its own drift, to answer a question the
/// stream loop is already awake to ask.
///
/// Asking instead costs one virtual call per frame and buys a colour that is a
/// function of the time it is sent at. That is also what makes it testable without
/// waiting: a fake clock and a handful of beats reproduce any moment of any envelope
/// exactly, with no threads and no sleeping.
///
/// <see cref="HueStreamService.Color"/> survives as the holding colour for a stream
/// with no engine behind it, which is the state a beat lab runs in.
/// </summary>
public interface ILightSource {
  /// <summary>
  /// The colour to send right now.
  ///
  /// Called on the stream's own thread, fifty times a second, by a loop whose entire
  /// job is punctuality. It must not block, allocate heavily, or wait on anything -
  /// a slow answer here does not delay a colour, it drops a frame.
  ///
  /// Reads the clock itself rather than being handed a timestamp, so there is exactly
  /// one opinion about what time it is on the path between a beat and a light.
  /// </summary>
  HueColor Render();
}
