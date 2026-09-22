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
  /// The colours to send right now: one per light, and the one the room is on.
  ///
  /// Called on the stream's own thread, fifty times a second, by a loop whose entire
  /// job is punctuality - and, while no stream is open, by the preview at the same rate.
  /// It must not block, allocate heavily, or wait on anything beyond the other caller's
  /// render at a handover - a slow answer here does not delay a colour, it drops a frame.
  ///
  /// Reads the clock itself rather than being handed a timestamp, so there is exactly
  /// one opinion about what time it is on the path between a beat and a light.
  ///
  /// <b>Once per frame, by one caller at a time.</b> The call advances the hue transition
  /// and the band references by the time since the last call, whoever made it, so two
  /// callers taking turns split an interval rather than counting it twice - but two
  /// calls interleaving inside one render would tear that state, so an implementation
  /// holding any has to serialise them.
  ///
  /// <b>The geometry is passed in rather than remembered</b>, and that is deliberate.
  /// A session owns its room: the placements are computed when the stream opens and
  /// thrown away when it closes, so a pump that a stop gave up waiting for cannot leave
  /// the next session lighting the previous room's shape. It also keeps the engine free
  /// of any state that two overlapping pumps could disagree about.
  /// </summary>
  /// <param name="lights">
  /// Where each light listens on the frequency axis, in the order the caller will write
  /// colours back in. Empty is allowed and means the caller has no geometry, which is
  /// the beat lab's case: only the return value is then meaningful.
  /// </param>
  /// <param name="colors">
  /// Filled with one colour per entry in <paramref name="lights"/>. Must be at least as
  /// long, and anything past that length is left alone.
  /// </param>
  /// <returns>
  /// The colour the room is on, before any one light's own band has been taken into
  /// account. This is what a page is shown as <c>state.light.color</c>, and it is a
  /// value the engine genuinely computes rather than an average of the lights: the
  /// average of nine bulbs is a colour nothing in the room is displaying.
  /// </returns>
  HueColor Render(ReadOnlySpan<LightPlacement> lights, Span<HueColor> colors);
}
